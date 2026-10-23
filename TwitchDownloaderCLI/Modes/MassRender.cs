using SkiaSharp;
using TwitchDownloaderCLI.Modes.Arguments;
using TwitchDownloaderCLI.Tools;
using TwitchDownloaderCore;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Models;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.Tools;

namespace TwitchDownloaderCLI.Modes
{
    internal static class MassRender
    {
        // Same sprite-measure width as RenderDanmaku: messages never wrap.
        private const int SpriteMeasureWidth = 8192;
        private const int MaxScanPages = 20; // 50 videos per page

        internal static void Run(MassRenderArgs inputOptions)
        {
            using var progress = new CliTaskProgress(inputOptions.LogLevel);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            RunAsync(inputOptions, progress, cts.Token).Wait(cts.Token);
        }

        private static async Task RunAsync(MassRenderArgs inputOptions, CliTaskProgress progress, CancellationToken cancellationToken)
        {
            var login = ExtractLogin(inputOptions.User);
            if (string.IsNullOrWhiteSpace(login))
            {
                progress.LogError("Could not parse a login name from --user.");
                Environment.Exit(1);
            }

            // Resolve the user first so a typo fails fast, like the right API behavior.
            var idRes = await TwitchHelper.GetUserIds([login]);
            var userId = idRes?.data?.users?.FirstOrDefault()?.id;
            if (userId is null)
            {
                progress.LogError($"User '{login}' was not found on Twitch. Check the login name (not the display name or a URL).");
                Environment.Exit(1);
            }

            var outputRoot = Path.GetFullPath(inputOptions.OutputDirectory);
            Directory.CreateDirectory(outputRoot);
            var statePath = string.IsNullOrWhiteSpace(inputOptions.StateFile)
                ? Path.Combine(outputRoot, "mass-state.txt")
                : Path.GetFullPath(inputOptions.StateFile);
            var doneIds = DedupList.LoadIds(statePath);
            progress.LogInfo($"Mass render for '{login}' (id {userId}): {doneIds.Count} video(s) already recorded in {statePath}");

            var speedMode = inputOptions.SpeedMode.ToLower() switch
            {
                "uniform" => DanmakuSpeedMode.Uniform,
                "fixed" => DanmakuSpeedMode.FixedDuration,
                _ => throw new NotSupportedException("Invalid speed mode. Valid values are: 'uniform' and 'fixed'.")
            };
            var videoType = inputOptions.VideoType.ToUpperInvariant() switch
            {
                "" => "",
                "ARCHIVE" => "ARCHIVE",
                "HIGHLIGHT" => "HIGHLIGHT",
                "UPLOAD" => "UPLOAD",
                _ => throw new NotSupportedException("Invalid --type. Valid values are: ARCHIVE, HIGHLIGHT, UPLOAD (or empty for all).")
            };

            int processed = 0, skipped = 0, failed = 0, scanned = 0;
            string cursor = "";
            for (int page = 0; page < MaxScanPages && processed < inputOptions.Limit; page++)
            {
                var res = await TwitchHelper.GetGqlVideos(login, cursor, 50, videoType);
                var user = res?.data?.user;
                if (user?.videos?.edges is null)
                {
                    progress.LogError("Twitch returned no video list for this user.");
                    break;
                }

                foreach (var edge in user.videos.edges)
                {
                    if (processed >= inputOptions.Limit || cancellationToken.IsCancellationRequested)
                        break;
                    scanned++;
                    var video = edge.node;
                    if (doneIds.Contains(video.id))
                    {
                        skipped++;
                        progress.LogInfo($"Skip {video.id} (already in state file): {video.title}");
                        continue;
                    }
                    if (inputOptions.DryRun)
                    {
                        processed++;
                        progress.LogInfo($"[dry-run] Would process {video.id}: {video.title} ({video.lengthSeconds}s, {video.createdAt:yyyy-MM-dd})");
                        continue;
                    }

                    try
                    {
                        await ProcessVideoAsync(inputOptions, progress, video.id, video.title, video.lengthSeconds, outputRoot, speedMode, cancellationToken);
                        DedupList.Append(statePath, video.id, video.title);
                        doneIds.Add(video.id);
                        processed++;
                        progress.LogInfo($"Recorded {video.id} in the state file ({processed}/{inputOptions.Limit} done).");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        progress.LogError($"Failed {video.id} ({video.title}): {ex.Message} — not recorded, will be retried on the next run.");
                    }
                }

                if (!user.videos.pageInfo.hasNextPage)
                    break;
                var newCursor = user.videos.edges.FirstOrDefault()?.cursor;
                if (string.IsNullOrEmpty(newCursor) || newCursor == cursor)
                    break;
                cursor = newCursor;
            }

            progress.LogInfo($"Mass render finished: {processed} processed, {skipped} skipped (already done), {failed} failed, {scanned} scanned.");
        }

        private static async Task ProcessVideoAsync(MassRenderArgs inputOptions, CliTaskProgress progress, string videoId, string title, int lengthSeconds, string outputRoot, DanmakuSpeedMode speedMode, CancellationToken cancellationToken)
        {
            var videoDir = Path.Combine(outputRoot, videoId);
            Directory.CreateDirectory(videoDir);
            var chatPath = Path.Combine(videoDir, $"{videoId}_chat.json");
            var outputPath = Path.Combine(videoDir, $"{videoId}_danmaku.mp4");

            progress.LogInfo($"Downloading chat for {videoId}: {title}");
            var downloadOptions = new ChatDownloadOptions
            {
                Id = videoId,
                Filename = chatPath,
                DownloadFormat = ChatFormat.Json,
                EmbedData = true,
                BttvEmotes = true,
                FfzEmotes = true,
                StvEmotes = true,
                TimeFormat = TimestampFormat.Relative,
                TempFolder = inputOptions.TempFolder,
                FileCollisionCallback = info => info,
            };
            var chatDownloader = new ChatDownloader(downloadOptions, progress);
            await chatDownloader.DownloadAsync(cancellationToken);

            double start = Math.Max(0, inputOptions.RenderStart);
            double length = inputOptions.RenderLength > 0
                ? inputOptions.RenderLength
                : Math.Max(1, lengthSeconds - start) + inputOptions.Duration + 1; // tail so the last comments can flow off-screen
            progress.LogInfo($"Rendering danmaku for {videoId}: {start}s +{length:F0}s -> {outputPath}");

            var renderOptions = new ChatRenderOptions
            {
                InputFile = chatPath,
                OutputFile = outputPath,
                BackgroundColor = SKColor.Parse("#111111"),
                AlternateBackgroundColor = SKColor.Parse("#191919"),
                HighlightUserColor = SKColor.Parse("#C8FF0059"),
                MessageColor = SKColor.Parse("#ffffff"),
                ChatWidth = SpriteMeasureWidth,
                ChatHeight = inputOptions.Height,
                BttvEmotes = true,
                FfzEmotes = true,
                StvEmotes = true,
                AllowUnlistedEmotes = true,
                FontSize = inputOptions.FontSize,
                MessageFontStyle = SKFontStyle.Normal,
                UsernameFontStyle = SKFontStyle.Bold,
                ChatBadges = false,
                SubMessages = true,
                Timestamp = false,
                Framerate = inputOptions.Fps,
                UpdateRate = 0,
                FfmpegPath = string.IsNullOrWhiteSpace(inputOptions.FfmpegPath) ? "ffmpeg" : Path.GetFullPath(inputOptions.FfmpegPath),
                TempFolder = inputOptions.TempFolder,
                EmojiVendor = EmojiVendor.GoogleNotoColor,
                AdjustUsernameVisibility = true,
                HideUsername = !(bool)(inputOptions.ShowName ?? true),
                DanmakuShadow = inputOptions.Shadow,
                DanmakuBandOpacity = inputOptions.BandOpacity,
                DisperseCommentOffsets = false,
                FileCollisionCallback = info => info,
            };

            using var chatRenderer = new ChatRenderer(renderOptions, progress);
            await chatRenderer.ParseJsonAsync(cancellationToken);
            await chatRenderer.RenderDanmakuVideoAsync(start, length, inputOptions.Fps, inputOptions.Duration, inputOptions.Width, inputOptions.Height, 0, speedMode, inputOptions.MaxLanes, cancellationToken);

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new InvalidOperationException("Render finished without producing an output file.");
        }

        private static string ExtractLogin(string user)
        {
            if (string.IsNullOrWhiteSpace(user))
                return null;
            var s = user.Trim();
            int twitchIdx = s.IndexOf("twitch.tv/", StringComparison.OrdinalIgnoreCase);
            if (twitchIdx >= 0)
                s = s[(twitchIdx + "twitch.tv/".Length)..];
            s = s.Trim('/').TrimStart('@');
            int slash = s.IndexOf('/');
            if (slash >= 0)
                s = s[..slash];
            return s.Any(char.IsWhiteSpace) ? null : s.ToLowerInvariant();
        }
    }
}
