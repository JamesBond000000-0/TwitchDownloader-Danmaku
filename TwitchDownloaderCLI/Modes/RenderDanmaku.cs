using SkiaSharp;
using TwitchDownloaderCLI.Modes.Arguments;
using TwitchDownloaderCLI.Tools;
using TwitchDownloaderCore;
using TwitchDownloaderCore.Chat;
using TwitchDownloaderCore.Options;

namespace TwitchDownloaderCLI.Modes
{
    internal static class RenderDanmaku
    {
        // Width used internally for message rendering. Deliberately huge so
        // that DrawText never wraps: every message becomes a single-line sprite.
        private const int SpriteMeasureWidth = 8192;

        internal static void Render(DanmakuRenderArgs inputOptions)
        {
            using var progress = new CliTaskProgress(inputOptions.LogLevel);

            var collisionHandler = new FileCollisionHandler(inputOptions, progress);

            ChatRenderOptions renderOptions = new()
            {
                InputFile = inputOptions.InputFile,
                OutputFile = inputOptions.OutputFile,
                BackgroundColor = SKColor.Parse(inputOptions.BackgroundColor),
                AlternateBackgroundColor = SKColor.Parse("#191919"),
                HighlightUserColor = SKColor.Parse(inputOptions.HighlightUserColor),
                MessageColor = SKColor.Parse("#ffffff"),
                ChatWidth = SpriteMeasureWidth,
                ChatHeight = inputOptions.Height,
                BttvEmotes = (bool)inputOptions.BttvEmotes!,
                FfzEmotes = (bool)inputOptions.FfzEmotes!,
                StvEmotes = (bool)inputOptions.StvEmotes!,
                AllowUnlistedEmotes = true,
                Font = inputOptions.Font,
                FontSize = inputOptions.FontSize,
                MessageFontStyle = SKFontStyle.Normal,
                UsernameFontStyle = SKFontStyle.Bold,
                ChatBadges = (bool)inputOptions.ChatBadges!,
                SubMessages = true,
                Timestamp = false,
                Framerate = inputOptions.Fps,
                UpdateRate = 0,
                FfmpegPath = string.IsNullOrWhiteSpace(inputOptions.FfmpegPath) ? "ffmpeg" : Path.GetFullPath(inputOptions.FfmpegPath),
                TempFolder = inputOptions.TempFolder,
                Offline = inputOptions.Offline,
                EmojiVendor = inputOptions.EmojiVendor.ToLower() switch
                {
                    "twitter" or "twemoji" => EmojiVendor.TwitterTwemoji,
                    "google" or "notocolor" => EmojiVendor.GoogleNotoColor,
                    "system" or "none" => EmojiVendor.None,
                    _ => throw new NotSupportedException("Invalid emoji vendor. Valid values are: 'twitter' / 'twemoji', 'google' / 'notocolor', and 'system' / 'none'.")
                },
                AdjustUsernameVisibility = true,
                HideUsername = !(bool)inputOptions.ShowName!,
                HighlightUsersArray = string.IsNullOrWhiteSpace(inputOptions.HighlightUsers) ? [] : inputOptions.HighlightUsers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                HighlightBadgesArray = string.IsNullOrWhiteSpace(inputOptions.HighlightBadges) ? [] : inputOptions.HighlightBadges.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                HighlightBadgeColor = string.IsNullOrWhiteSpace(inputOptions.HighlightBadgeColor) ? SKColor.Empty : SKColor.Parse(inputOptions.HighlightBadgeColor),
                HighlightBadgeColors = ParseBadgeColors(inputOptions.HighlightBadgeColors),
                DanmakuHighlightShowName = inputOptions.HighlightShowName,
                DanmakuHighlightScale = inputOptions.HighlightScale,
                DanmakuUseGpu = inputOptions.Gpu,
                DanmakuShadow = inputOptions.Shadow,
                DanmakuBandOpacity = inputOptions.BandOpacity,
                Outline = inputOptions.Outline,
                OutlineSize = inputOptions.OutlineSize,
                DisperseCommentOffsets = false,
                FileCollisionCallback = collisionHandler.HandleCollisionCallback,
            };

            var speedMode = inputOptions.SpeedMode.ToLower() switch
            {
                "uniform" => DanmakuSpeedMode.Uniform,
                "fixed" => DanmakuSpeedMode.FixedDuration,
                _ => throw new NotSupportedException("Invalid speed mode. Valid values are: 'uniform' and 'fixed'.")
            };

            using var chatRenderer = new ChatRenderer(renderOptions, progress);
            chatRenderer.ParseJsonAsync().Wait();
            if (!string.IsNullOrWhiteSpace(inputOptions.BaseVideo))
            {
                double baseStart = inputOptions.BaseStart >= 0 ? inputOptions.BaseStart : inputOptions.Start;
                chatRenderer.RenderDanmakuCompositeAsync(inputOptions.BaseVideo, baseStart, inputOptions.Start, inputOptions.Length, inputOptions.Fps, inputOptions.Duration, inputOptions.Width, inputOptions.Height, inputOptions.Offset, speedMode, inputOptions.MaxLanes).Wait();
            }
            else if (inputOptions.Video)
            {
                chatRenderer.RenderDanmakuVideoAsync(inputOptions.Start, inputOptions.Length, inputOptions.Fps, inputOptions.Duration, inputOptions.Width, inputOptions.Height, inputOptions.Offset, speedMode, inputOptions.MaxLanes).Wait();
            }
            else
            {
                chatRenderer.RenderDanmakuFrameAsync(inputOptions.Time, inputOptions.Duration, inputOptions.Width, inputOptions.Height, inputOptions.Offset, speedMode, inputOptions.MaxLanes).Wait();
            }
        }

        private static Dictionary<string, SKColor> ParseBadgeColors(string spec)
        {
            var result = new Dictionary<string, SKColor>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(spec))
                return result;

            foreach (var pair in spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf(':');
                if (separator <= 0)
                    continue;

                result[pair[..separator].Trim()] = SKColor.Parse(pair[(separator + 1)..].Trim());
            }

            return result;
        }
    }
}
