using SkiaSharp;
using System.Diagnostics;
using System.Globalization;
using TwitchDownloaderCore.Models.Render;

namespace TwitchDownloaderCore
{
    // Danmaku (niconico-style flowing comments) renderer.
    //
    // Reuses the existing message rendering pipeline (GenerateCommentSection)
    // with a very wide ChatWidth so every message becomes a single-line sprite,
    // crops the sprite to its content bounds, assigns each sprite to a lane,
    // and composites frames with the sprites scrolling right-to-left.
    //
    // Two speed modes (see DanmakuSpeedMode): Uniform shares one speed
    // between all comments; FixedDuration gives every comment the same
    // on-screen lifetime so longer comments move faster, which needs a
    // catch-up check during lane assignment (CanEnterAfter).
    //
    // Known limitations (see docs/danmaku-design.md):
    // - Sprite content width is measured by scanning the rendered image for
    //   non-transparent pixels, because GenerateCommentSection does not
    //   expose the final draw position.
    // - Lane simulation starts a little before the render window; sprites
    //   wider than MaxAssumedSpriteWidth that started earlier are ignored.
    // - The whole segment's sprites are built up front. Fine for segments
    //   (even a full VOD's chat is only a few thousand comments), but truly
    //   long renders will eventually want chunked timeline building.
    public enum DanmakuSpeedMode
    {
        /// <summary>All comments share one speed: a point crosses the canvas width in the scroll duration.</summary>
        Uniform,
        /// <summary>Each comment's total on-screen lifetime equals the scroll duration, so longer comments move faster (niconico style). Requires catch-up checks during lane assignment.</summary>
        FixedDuration,
    }

    public sealed partial class ChatRenderer
    {
        private const int MaxAssumedSpriteWidth = 4000;

        private sealed class DanmakuTemplate
        {
            public required SKImage Image { get; init; }
            public required List<EmotePosition> Emotes { get; init; }
            public required int Width { get; init; }
            public required int Height { get; init; }
        }

        private sealed class DanmakuSprite
        {
            public required DanmakuTemplate Template { get; init; }
            public required double StartSeconds { get; init; }
            public required double ExitSeconds { get; init; }
            public required double Speed { get; init; } // px per second
            public required int LaneTop { get; init; } // in lane units
            public int LanesNeeded { get; init; } = 1;
        }

        private readonly Dictionary<string, DanmakuTemplate> _danmakuTemplateCache = new();

        // Turns any drawn image into a black silhouette that keeps its alpha
        // shape; used for the danmaku drop shadow.
        private static readonly SKPaint DanmakuShadowPaint = new()
        {
            ColorFilter = SKColorFilter.CreateColorMatrix(new float[]
            {
                0, 0, 0, 0, 0,
                0, 0, 0, 0, 0,
                0, 0, 0, 0, 0,
                0, 0, 0, 0.65f, 0,
            })
        };

        private SKPaint _danmakuBandPaint;

        // Shared setup with RenderAsyncImpl: fetch images, set fonts and the
        // cached metrics that GenerateCommentSection relies on.
        // Returns the Y position used for text inside a comment section.
        private async Task<int> PrepareDanmakuRenderingAsync(CancellationToken cancellationToken)
        {
            await FetchScaledImages(cancellationToken);

            if (renderOptions.Font == "Inter Embedded")
            {
                nameFont.Typeface = GetInterTypeface(renderOptions.UsernameFontStyle);
                messageFont.Typeface = GetInterTypeface(renderOptions.MessageFontStyle);
            }
            else
            {
                nameFont.Typeface = SKTypeface.FromFamilyName(renderOptions.Font, renderOptions.UsernameFontStyle);
                messageFont.Typeface = SKTypeface.FromFamilyName(renderOptions.Font, renderOptions.MessageFontStyle);
            }

            renderOptions.TimestampWidths = !renderOptions.Timestamp ? Array.Empty<int>() : new[]
            {
                (int)messageFont.MeasureText("0:00"),
                (int)messageFont.MeasureText("00:00"),
                (int)messageFont.MeasureText("0:00:00"),
                (int)messageFont.MeasureText("00:00:00")
            };

            SKRect usernameBounds = new SKRect();
            nameFont.MeasureText("ABC123", ref usernameBounds);
            _usernameCenteredY = (int)(((renderOptions.SectionHeight - usernameBounds.Height) / 2.0) + usernameBounds.Height);

            renderOptions.BlockArtCharWidth = GetFallbackFont('█').MeasureText("█");

            RemoveRestrictedComments(chatRoot.comments);

            SKRect sampleTextBounds = new SKRect();
            messageFont.MeasureText("ABC123", ref sampleTextBounds);
            return (int)(((renderOptions.SectionHeight - sampleTextBounds.Height) / 2.0) + sampleTextBounds.Height);
        }

        private DanmakuTemplate GetDanmakuTemplate(int commentIndex, int sectionDefaultYPos)
        {
            var comment = chatRoot.comments[commentIndex];
            // Identical messages from the same user (spam, repeated emotes)
            // are very common in Twitch chat; reuse their rendered sprite.
            string cacheKey = string.Concat(
                comment.message.user_notice_params?.msg_id, "|",
                comment.commenter?.display_name, "|",
                comment.message.user_color, "|",
                comment.message.body);

            if (_danmakuTemplateCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var section = GenerateCommentSection(commentIndex, sectionDefaultYPos);
            if (section?.Image is null)
            {
                return null;
            }

            var (contentLeft, contentWidth) = MeasureContentBounds(section.Image);
            if (contentWidth <= 0)
            {
                section.Image.Dispose();
                return null;
            }

            SKImage cropped = section.Image.Subset(new SKRectI(contentLeft, 0, contentLeft + contentWidth, section.Image.Height));
            var emotes = section.Emotes
                .Select(e => new EmotePosition(new Point { X = e.DrawPoint.X - contentLeft, Y = e.DrawPoint.Y }, e.Emote))
                .ToList();
            section.Image.Dispose();

            var template = new DanmakuTemplate
            {
                Image = cropped,
                Emotes = emotes,
                Width = contentWidth,
                Height = cropped.Height,
            };
            _danmakuTemplateCache[cacheKey] = template;
            return template;
        }

        // Simulates lane assignment for all comments that can be on screen at
        // any point of [startSeconds, endSeconds] and returns the visible
        // ones, sorted by start time. Comment times are shifted by
        // offsetSeconds (positive = comments appear later).
        private List<DanmakuSprite> BuildDanmakuTimeline(double startSeconds, double endSeconds, double scrollDurationSeconds, DanmakuSpeedMode speedMode, int maxLanes, int canvasWidth, int canvasHeight, int sectionDefaultYPos, double offsetSeconds, out int dropped)
        {
            int lanePitch = renderOptions.SectionHeight;
            int canvasLanes = Math.Max(1, canvasHeight / lanePitch);
            int laneCount = maxLanes > 0 ? Math.Min(maxLanes, canvasLanes) : canvasLanes;
            var lastInLane = new DanmakuSprite[laneCount];
            var sprites = new List<DanmakuSprite>();
            dropped = 0;

            double uniformSpeed = canvasWidth / scrollDurationSeconds;
            double maxTravel = speedMode == DanmakuSpeedMode.FixedDuration
                ? scrollDurationSeconds
                : (canvasWidth + MaxAssumedSpriteWidth) / uniformSpeed;
            double simStart = Math.Max(0, startSeconds - maxTravel);

            for (int i = 0; i < chatRoot.comments.Count; i++)
            {
                var comment = chatRoot.comments[i];
                double start = comment.content_offset_seconds + offsetSeconds;
                if (start < simStart)
                {
                    continue;
                }
                if (start >= endSeconds)
                {
                    break; // comments are in chronological order
                }

                var template = GetDanmakuTemplate(i, sectionDefaultYPos);
                if (template is null)
                {
                    continue;
                }

                double speed = speedMode == DanmakuSpeedMode.FixedDuration
                    ? (canvasWidth + template.Width) / scrollDurationSeconds
                    : uniformSpeed;
                double exit = start + (canvasWidth + template.Width) / speed;
                int lanesNeeded = Math.Max(1, (int)Math.Ceiling(template.Height / (double)lanePitch));

                int laneTop = -1;
                for (int lane = 0; lane + lanesNeeded <= laneCount; lane++)
                {
                    bool free = true;
                    for (int k = 0; k < lanesNeeded; k++)
                    {
                        var prev = lastInLane[lane + k];
                        if (prev is not null && !CanEnterAfter(prev, start, speed, canvasWidth))
                        {
                            free = false;
                            break;
                        }
                    }
                    if (free)
                    {
                        laneTop = lane;
                        break;
                    }
                }

                if (laneTop < 0)
                {
                    if (exit > startSeconds)
                    {
                        dropped++;
                    }
                    continue;
                }

                var sprite = new DanmakuSprite
                {
                    Template = template,
                    StartSeconds = start,
                    ExitSeconds = exit,
                    Speed = speed,
                    LaneTop = laneTop,
                    LanesNeeded = lanesNeeded,
                };
                for (int k = 0; k < lanesNeeded; k++)
                {
                    lastInLane[laneTop + k] = sprite;
                }

                if (exit > startSeconds)
                {
                    sprites.Add(sprite);
                }
            }

            return sprites;
        }

        // Whether a sprite starting at `start` with `speed` may enter a lane
        // directly behind `prev` without ever overlapping it.
        private static bool CanEnterAfter(DanmakuSprite prev, double start, double speed, int canvasWidth)
        {
            // The previous sprite's tail must be fully on screen.
            if (start < prev.StartSeconds + prev.Template.Width / prev.Speed)
            {
                return false;
            }

            // A faster sprite must not catch up before the previous sprite
            // has fully left the screen. The gap between the sprites closes
            // linearly, so it is enough to check the moment the previous
            // sprite exits: its tail is then at x=0, so this sprite's head
            // must not have reached x=0 yet.
            if (speed > prev.Speed && start + canvasWidth / speed < prev.ExitSeconds)
            {
                return false;
            }

            return true;
        }

        // Draws the sprites visible at timeSeconds. The list may be the full
        // timeline (still frames) or the active window maintained by the
        // video loop; the per-sprite time check keeps both correct.
        private void DrawDanmakuFrame(SKCanvas canvas, List<DanmakuSprite> sprites, double timeSeconds, int canvasWidth, int lanePitch)
        {
            canvas.Clear(renderOptions.BackgroundColor);
            long nowMs = (long)(timeSeconds * 1000);

            bool shadow = renderOptions.DanmakuShadow;
            float shadowOffset = shadow ? Math.Max(1f, (float)(2 * renderOptions.ReferenceScale)) : 0;
            double bandOpacity = Math.Clamp(renderOptions.DanmakuBandOpacity, 0, 1);

            foreach (var sprite in sprites)
            {
                if (timeSeconds < sprite.StartSeconds || timeSeconds >= sprite.ExitSeconds)
                {
                    continue;
                }

                float x = (float)(canvasWidth - sprite.Speed * (timeSeconds - sprite.StartSeconds));
                float y = sprite.LaneTop * lanePitch;

                // Translucent backing band behind the comment text.
                if (bandOpacity > 0)
                {
                    _danmakuBandPaint ??= new SKPaint { IsAntialias = true };
                    _danmakuBandPaint.Color = new SKColor(0, 0, 0, (byte)(bandOpacity * 255));
                    float pad = (float)(renderOptions.FontSize * 0.35);
                    canvas.DrawRoundRect(new SKRect(x - pad, y, x + sprite.Template.Width + pad, y + sprite.Template.Height), 8, 8, _danmakuBandPaint);
                }

                // Resolve the animated emote frames once; both the shadow
                // pass and the normal pass use the same frames.
                var emoteFrames = new List<(SKImage Frame, Point DrawPoint)>(sprite.Template.Emotes.Count);
                foreach (var (drawPoint, emote) in sprite.Template.Emotes)
                {
                    int frameIndex = emote.FrameCount > 1
                        ? ComputeAnimFrameIndex(emote, nowMs)
                        : 0;
                    emoteFrames.Add((emote.EmoteFrames[frameIndex], drawPoint));
                }

                if (shadow)
                {
                    canvas.DrawImage(sprite.Template.Image, x + shadowOffset, y + shadowOffset, DanmakuShadowPaint);
                    foreach (var (frame, drawPoint) in emoteFrames)
                    {
                        canvas.DrawImage(frame, x + drawPoint.X + shadowOffset, y + drawPoint.Y + shadowOffset, DanmakuShadowPaint);
                    }
                }

                canvas.DrawImage(sprite.Template.Image, x, y);

                foreach (var (frame, drawPoint) in emoteFrames)
                {
                    canvas.DrawImage(frame, x + drawPoint.X, y + drawPoint.Y);
                }
            }
        }

        private void DisposeDanmakuTemplates()
        {
            foreach (var template in _danmakuTemplateCache.Values)
            {
                template.Image.Dispose();
            }
            _danmakuTemplateCache.Clear();
            _danmakuBandPaint?.Dispose();
            _danmakuBandPaint = null;
        }

        /// <summary>
        /// Renders a single danmaku frame at <paramref name="timeSeconds"/> to
        /// the PNG file given by the render options' OutputFile.
        /// Assumes <see cref="ParseJsonAsync"/> has already been called.
        /// </summary>
        public async Task RenderDanmakuFrameAsync(double timeSeconds, double scrollDurationSeconds, int canvasWidth, int canvasHeight, double offsetSeconds = 0, DanmakuSpeedMode speedMode = DanmakuSpeedMode.Uniform, int maxLanes = 0, CancellationToken cancellationToken = default)
        {
            if (chatRoot.comments is null)
            {
                await ParseJsonAsync(cancellationToken);
            }

            int sectionDefaultYPos = await PrepareDanmakuRenderingAsync(cancellationToken);
            try
            {
                var sprites = BuildDanmakuTimeline(timeSeconds, timeSeconds + 0.001, scrollDurationSeconds, speedMode, maxLanes, canvasWidth, canvasHeight, sectionDefaultYPos, offsetSeconds, out int dropped);

                using var frameBitmap = new SKBitmap(new SKImageInfo(canvasWidth, canvasHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
                using (var frameCanvas = new SKCanvas(frameBitmap))
                {
                    DrawDanmakuFrame(frameCanvas, sprites, timeSeconds, canvasWidth, renderOptions.SectionHeight);
                }

                var outputInfo = renderOptions.FileCollisionCallback(new FileInfo(renderOptions.OutputFile));
                renderOptions.OutputFile = outputInfo.FullName;

                using var frameImage = SKImage.FromBitmap(frameBitmap);
                using var pngData = frameImage.Encode(SKEncodedImageFormat.Png, 100);
                await using (var fs = outputInfo.Open(FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    pngData.SaveTo(fs);
                }

                _progress.LogInfo($"Danmaku frame at {timeSeconds:F1}s: {sprites.Count} comments on screen, {dropped} dropped (no free lane). Wrote {outputInfo.FullName}");
            }
            finally
            {
                DisposeDanmakuTemplates();
            }
        }

        /// <summary>
        /// Renders a danmaku-only video segment starting at
        /// <paramref name="startSeconds"/> (chat time) of
        /// <paramref name="lengthSeconds"/> length, piping raw frames to
        /// FFmpeg. The output format is chosen from the OutputFile extension:
        /// .mov keeps the alpha channel (ProRes 4444), anything else is
        /// encoded as H.264 MP4 over the configured background color.
        /// Assumes <see cref="ParseJsonAsync"/> has already been called.
        /// </summary>
        public Task RenderDanmakuVideoAsync(double startSeconds, double lengthSeconds, int framerate, double scrollDurationSeconds, int canvasWidth, int canvasHeight, double offsetSeconds = 0, DanmakuSpeedMode speedMode = DanmakuSpeedMode.Uniform, int maxLanes = 0, CancellationToken cancellationToken = default)
        {
            var outputInfo = renderOptions.FileCollisionCallback(new FileInfo(renderOptions.OutputFile));
            renderOptions.OutputFile = outputInfo.FullName;

            if (!string.IsNullOrWhiteSpace(renderOptions.InputArgs) && !string.IsNullOrWhiteSpace(renderOptions.OutputArgs))
            {
                // Honor caller-provided ffmpeg arguments (e.g. the WPF GUI's
                // format/codec selection), like the standard renderer does.
                return RenderDanmakuFramesToFfmpegAsync(startSeconds, lengthSeconds, framerate, scrollDurationSeconds, canvasWidth, canvasHeight, offsetSeconds, speedMode, maxLanes, null, outputInfo, cancellationToken, ffmpegProcess: GetFfmpegProcess(outputInfo));
            }

            // Alpha is carried by ProRes 4444 (.mov). VP9/WebM was tried
            // first but this FFmpeg build silently drops the alpha plane
            // (verified 2026-10-23: output comes out as yuv420p).
            bool keepAlpha = string.Equals(outputInfo.Extension, ".mov", StringComparison.OrdinalIgnoreCase);
            string ffmpegArgs = keepAlpha
                ? FormattableString.Invariant($"-y -f rawvideo -pix_fmt bgra -s {canvasWidth}x{canvasHeight} -r {framerate} -i - -c:v prores_ks -profile:v 4 -pix_fmt yuva444p10le \"{outputInfo.FullName}\"")
                : FormattableString.Invariant($"-y -f rawvideo -pix_fmt bgra -s {canvasWidth}x{canvasHeight} -r {framerate} -i - -c:v libx264 -pix_fmt yuv420p -crf 18 -preset veryfast -movflags +faststart \"{outputInfo.FullName}\"");

            return RenderDanmakuFramesToFfmpegAsync(startSeconds, lengthSeconds, framerate, scrollDurationSeconds, canvasWidth, canvasHeight, offsetSeconds, speedMode, maxLanes, ffmpegArgs, outputInfo, cancellationToken);
        }

        /// <summary>
        /// Renders a danmaku segment composited directly onto a base video in
        /// a single FFmpeg pass: the base video is one input, the danmaku
        /// frames are piped in as the second input and overlaid. No
        /// intermediate file is created. The danmaku background is forced
        /// transparent regardless of the configured background color.
        /// Assumes <see cref="ParseJsonAsync"/> has already been called.
        /// </summary>
        /// <param name="baseVideoPath">Base video file (full VOD or a pre-cut segment).</param>
        /// <param name="baseStartSeconds">Position in the base video that corresponds to <paramref name="startSeconds"/> in chat time.</param>
        public Task RenderDanmakuCompositeAsync(string baseVideoPath, double baseStartSeconds, double startSeconds, double lengthSeconds, int framerate, double scrollDurationSeconds, int canvasWidth, int canvasHeight, double offsetSeconds = 0, DanmakuSpeedMode speedMode = DanmakuSpeedMode.Uniform, int maxLanes = 0, CancellationToken cancellationToken = default)
        {
            var outputInfo = renderOptions.FileCollisionCallback(new FileInfo(renderOptions.OutputFile));
            renderOptions.OutputFile = outputInfo.FullName;

            string ffmpegArgs = FormattableString.Invariant(
                $"-y -ss {baseStartSeconds:F3} -i \"{baseVideoPath}\" -f rawvideo -pix_fmt bgra -s {canvasWidth}x{canvasHeight} -r {framerate} -i - -filter_complex \"[0:v][1:v]overlay=0:0:format=auto,format=yuv420p[v]\" -map \"[v]\" -map 0:a? -c:v libx264 -pix_fmt yuv420p -crf 18 -preset veryfast -movflags +faststart -c:a aac -b:a 160k -t {lengthSeconds:F3} \"{outputInfo.FullName}\"");

            return RenderDanmakuFramesToFfmpegAsync(startSeconds, lengthSeconds, framerate, scrollDurationSeconds, canvasWidth, canvasHeight, offsetSeconds, speedMode, maxLanes, ffmpegArgs, outputInfo, cancellationToken, forceTransparentBackground: true);
        }

        /// <summary>
        /// Entry point for callers that configure everything through
        /// <see cref="ChatRenderOptions"/> (the WPF GUI): the render range is
        /// the start/end override when set, otherwise the whole chat,
        /// extended a little so the last comments can scroll off screen.
        /// </summary>
        public async Task RenderDanmakuFromOptionsAsync(CancellationToken cancellationToken = default)
        {
            if (chatRoot.comments is null)
            {
                await ParseJsonAsync(cancellationToken);
            }

            double start = renderOptions.StartOverride >= 0 ? renderOptions.StartOverride : 0;
            double lastOffset = chatRoot.comments.Count > 0 ? chatRoot.comments[^1].content_offset_seconds : 0;
            double end = renderOptions.EndOverride >= 0 ? renderOptions.EndOverride : lastOffset + renderOptions.DanmakuScrollDuration + 1;
            double length = Math.Max(1, end - start);

            await RenderDanmakuVideoAsync(start, length, renderOptions.Framerate, renderOptions.DanmakuScrollDuration, renderOptions.ChatWidth, renderOptions.ChatHeight, 0, renderOptions.DanmakuSpeedMode, renderOptions.DanmakuMaxLanes, cancellationToken);
        }

        private async Task RenderDanmakuFramesToFfmpegAsync(double startSeconds, double lengthSeconds, int framerate, double scrollDurationSeconds, int canvasWidth, int canvasHeight, double offsetSeconds, DanmakuSpeedMode speedMode, int maxLanes, string ffmpegArgs, FileInfo outputInfo, CancellationToken cancellationToken, bool forceTransparentBackground = false, Process ffmpegProcess = null)
        {
            if (chatRoot.comments is null)
            {
                await ParseJsonAsync(cancellationToken);
            }

            var savedBackground = renderOptions.BackgroundColor;
            if (forceTransparentBackground)
            {
                renderOptions.BackgroundColor = SKColors.Transparent;
            }

            try
            {
                int sectionDefaultYPos = await PrepareDanmakuRenderingAsync(cancellationToken);

                double endSeconds = startSeconds + lengthSeconds;
                var sprites = BuildDanmakuTimeline(startSeconds, endSeconds, scrollDurationSeconds, speedMode, maxLanes, canvasWidth, canvasHeight, sectionDefaultYPos, offsetSeconds, out int dropped);
                int lanePitch = renderOptions.SectionHeight;

                Process startedFfmpeg;
                Task<string> stderrTask = null;
                if (ffmpegProcess is not null)
                {
                    // Caller-provided process (already started, stderr is
                    // drained through the progress logger by GetFfmpegProcess).
                    startedFfmpeg = ffmpegProcess;
                }
                else
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = renderOptions.FfmpegPath,
                        Arguments = ffmpegArgs,
                        RedirectStandardInput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };

                    startedFfmpeg = Process.Start(startInfo)
                        ?? throw new InvalidOperationException("Failed to start FFmpeg.");
                    stderrTask = startedFfmpeg.StandardError.ReadToEndAsync(cancellationToken);
                }
                using var ffmpeg = startedFfmpeg;

                int totalFrames = (int)Math.Round(lengthSeconds * framerate);
                var inputStream = ffmpeg.StandardInput.BaseStream;

                using var frameBitmap = new SKBitmap(new SKImageInfo(canvasWidth, canvasHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
                using var frameCanvas = new SKCanvas(frameBitmap);

                // Sliding active window: sprites are sorted by start time, so
                // each frame only touches the sprites currently on screen
                // instead of scanning the whole segment timeline.
                var active = new List<DanmakuSprite>();
                int nextSprite = 0;

                var stopwatch = Stopwatch.StartNew();
                for (int frame = 0; frame < totalFrames; frame++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double t = startSeconds + frame / (double)framerate;

                    while (nextSprite < sprites.Count && sprites[nextSprite].StartSeconds <= t)
                    {
                        active.Add(sprites[nextSprite]);
                        nextSprite++;
                    }
                    active.RemoveAll(s => t >= s.ExitSeconds);

                    DrawDanmakuFrame(frameCanvas, active, t, canvasWidth, lanePitch);
                    frameCanvas.Flush();

                    using var pixmap = frameBitmap.PeekPixels();
                    inputStream.Write(pixmap.GetPixelSpan());

                    if (frame > 0 && frame % (framerate * 10) == 0)
                    {
                        _progress.LogInfo($"Danmaku video: {frame}/{totalFrames} frames ({stopwatch.Elapsed:h\\:mm\\:ss} elapsed)");
                    }

                    if (frame % framerate == 0)
                    {
                        _progress.ReportProgress((int)(frame * 100L / Math.Max(1, totalFrames)));
                    }
                }

                inputStream.Close();
                await ffmpeg.WaitForExitAsync(cancellationToken);
                string stderr = stderrTask is not null ? await stderrTask : "";
                if (ffmpeg.ExitCode != 0)
                {
                    throw new InvalidOperationException($"FFmpeg exited with code {ffmpeg.ExitCode}: {stderr}");
                }

                _progress.LogInfo($"Danmaku video {startSeconds:F0}s +{lengthSeconds:F0}s: {sprites.Count} comments placed, {dropped} dropped (no free lane), {totalFrames} frames in {stopwatch.Elapsed:h\\:mm\\:ss}. Wrote {outputInfo.FullName}");
            }
            finally
            {
                renderOptions.BackgroundColor = savedBackground;
                DisposeDanmakuTemplates();
            }
        }

        // Finds the leftmost/rightmost columns containing any non-transparent
        // pixel. M1 simplification; see file header.
        private static (int Left, int Width) MeasureContentBounds(SKImage image)
        {
            using var bitmap = SKBitmap.FromImage(image);
            int width = bitmap.Width;
            int height = bitmap.Height;

            int left = width;
            int right = -1;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (bitmap.GetPixel(x, y).Alpha == 0)
                    {
                        continue;
                    }
                    if (x < left) left = x;
                    if (x > right) right = x;
                }
            }

            return right < 0 ? (0, 0) : (left, right - left + 1);
        }
    }
}
