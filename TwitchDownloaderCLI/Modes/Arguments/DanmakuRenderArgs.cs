using CommandLine;
using TwitchDownloaderCLI.Models;

namespace TwitchDownloaderCLI.Modes.Arguments
{
    [Verb("danmakurender", HelpText = "Renders niconico-style flowing comments (danmaku) from a chat JSON: a still PNG frame by default, or a video segment with --video")]
    internal sealed class DanmakuRenderArgs : IFileCollisionArgs, ITwitchDownloaderArgs
    {
        [Option('i', "input", Required = true, HelpText = "Path to JSON chat file input.")]
        public string InputFile { get; set; }

        [Option('o', "output", Required = true, HelpText = "PNG file the program will output to.")]
        public string OutputFile { get; set; }

        [Option("time", Default = 8.0, HelpText = "Time (seconds into the chat) to render the still frame at.")]
        public double Time { get; set; }

        [Option("video", Default = false, HelpText = "Render a video segment instead of a still frame. Output format follows the output file extension (.mov = ProRes 4444 with alpha, otherwise H.264 MP4).")]
        public bool Video { get; set; }

        [Option("start", Default = 0.0, HelpText = "Start time (seconds into the chat) for --video mode.")]
        public double Start { get; set; }

        [Option("length", Default = 60.0, HelpText = "Length in seconds for --video mode.")]
        public double Length { get; set; }

        [Option("fps", Default = 30, HelpText = "Framerate for --video mode.")]
        public int Fps { get; set; }

        [Option("ffmpeg-path", HelpText = "Path to FFmpeg executable. Defaults to 'ffmpeg' on PATH.")]
        public string FfmpegPath { get; set; }

        [Option("offset", Default = 0.0, HelpText = "Shift all comment times by this many seconds (positive = comments appear later). For negative values use --offset=-5.")]
        public double Offset { get; set; }

        [Option("base-video", HelpText = "Path to a base video file. When set, the danmaku is composited onto the video in a single FFmpeg pass (implies --video, output is MP4).")]
        public string BaseVideo { get; set; }

        [Option("base-start", Default = -1.0, HelpText = "Position in seconds in the base video that corresponds to --start in chat time. Defaults to --start (base video is the full VOD). Set 0 when the base video is a pre-cut segment.")]
        public double BaseStart { get; set; }

        [Option('w', "width", Default = 1920, HelpText = "Width of the output frame.")]
        public int Width { get; set; }

        [Option('h', "height", Default = 1080, HelpText = "Height of the output frame.")]
        public int Height { get; set; }

        [Option("duration", Default = 6.0, HelpText = "Seconds a comment needs to cross the frame width (uniform speed in v1). Smaller = faster.")]
        public double Duration { get; set; }

        [Option("show-name", Default = true, HelpText = "Show the commenter's name (in their user color) before the message text.")]
        public bool? ShowName { get; set; }

        [Option("speed-mode", Default = "uniform", HelpText = "uniform = all comments share one speed (the scroll duration is the time a point needs to cross the screen width). fixed = every comment stays on screen for exactly the scroll duration, so longer comments move faster.")]
        public string SpeedMode { get; set; }

        [Option("max-lanes", Default = 0, HelpText = "Maximum number of lanes (rows) to use, counted from the top. 0 = use the full frame height.")]
        public int MaxLanes { get; set; }

        [Option("shadow", Default = false, HelpText = "Draw a dark drop shadow behind comments to keep them readable on bright backgrounds.")]
        public bool Shadow { get; set; }

        [Option("band", Default = 0.0, HelpText = "Opacity (0 to 1) of a translucent black band drawn behind each comment, e.g. 0.45. 0 disables the band.")]
        public double BandOpacity { get; set; }

        [Option("outline", Default = false, HelpText = "Enable the renderer's text outline around comments.")]
        public bool Outline { get; set; }

        [Option("outline-size", Default = 4.0, HelpText = "Size of the text outline when --outline is enabled.")]
        public double OutlineSize { get; set; }

        [Option("background-color", Default = "#111111", HelpText = "Background color in '#RRGGBB' or '#AARRGGBB' format. Use '#00000000' for transparent.")]
        public string BackgroundColor { get; set; }

        [Option('f', "font", Default = "Inter Embedded", HelpText = "Font to use.")]
        public string Font { get; set; }

        [Option("font-size", Default = 36.0, HelpText = "Font size.")]
        public double FontSize { get; set; }

        [Option("bttv", Default = true, HelpText = "Enable BTTV emotes.")]
        public bool? BttvEmotes { get; set; }

        [Option("ffz", Default = true, HelpText = "Enable FFZ emotes.")]
        public bool? FfzEmotes { get; set; }

        [Option("stv", Default = true, HelpText = "Enable 7TV emotes.")]
        public bool? StvEmotes { get; set; }

        [Option("badges", Default = false, HelpText = "Enable chat badges.")]
        public bool? ChatBadges { get; set; }

        [Option("highlight-users", Default = "", HelpText = "Comma-separated list of users whose comments get a highlight band. Not case-sensitive.")]
        public string HighlightUsers { get; set; }

        [Option("highlight-badges", Default = "", HelpText = "Comma-separated list of badge ids (e.g. vip,moderator,broadcaster) whose comments get a highlight band. Not case-sensitive.")]
        public string HighlightBadges { get; set; }

        [Option("highlight-user-color", Default = "#C8FF0059", HelpText = "Highlight band color in the format '#RRGGBB' or '#AARRGGBB'. Applies to both --highlight-users and --highlight-badges.")]
        public string HighlightUserColor { get; set; }

        [Option("highlight-badge-color", Default = "", HelpText = "Separate highlight band color for --highlight-badges matches, in the format '#RRGGBB' or '#AARRGGBB'. Empty = use --highlight-user-color.")]
        public string HighlightBadgeColor { get; set; }

        [Option("highlight-badge-colors", Default = "", HelpText = "Per-badge highlight colors as comma-separated 'badge:#AARRGGBB' pairs, e.g. 'moderator:#CC00C853,vip:#CCE005B9'. Earlier entries win when a comment carries several of the badges. Badges listed here are highlighted even without --highlight-badges.")]
        public string HighlightBadgeColors { get; set; }

        [Option("highlight-show-name", Default = false, HelpText = "Keep the username on highlighted comments even when names are hidden (--show-name false). Danmaku only.")]
        public bool HighlightShowName { get; set; }

        [Option("highlight-scale", Default = 1.0, HelpText = "Scale factor for highlighted comments in danmaku mode (1 = normal size, clamped to 0.25-4).")]
        public double HighlightScale { get; set; }

        [Option("gpu", Default = false, HelpText = "Render danmaku frames on the GPU (Skia GPU backend, experimental). Falls back to CPU rendering when no usable GPU/OpenGL stack is available.")]
        public bool Gpu { get; set; }

        [Option("offline", Default = false, HelpText = "Render completely offline using only embedded emotes, badges, and bits from the input json.")]
        public bool Offline { get; set; }

        [Option("emoji-vendor", Default = "notocolor", HelpText = "The emoji vendor used for rendering emojis. Valid values are: 'twitter' / 'twemoji', 'google' / 'notocolor', and 'system' / 'none'.")]
        public string EmojiVendor { get; set; }

        [Option("temp-path", Default = "", HelpText = "Path to temporary folder to use for cache.")]
        public string TempFolder { get; set; }

        public OverwriteBehavior OverwriteBehavior { get; set; }
        public bool? ShowBanner { get; set; }
        public LogLevel LogLevel { get; set; }
    }
}
