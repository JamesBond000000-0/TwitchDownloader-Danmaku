using CommandLine;
using TwitchDownloaderCLI.Models;

namespace TwitchDownloaderCLI.Modes.Arguments
{
    [Verb("massrender", HelpText = "Fetches a user's videos, downloads each chat and renders it as danmaku. Successfully processed video IDs are recorded in a state file and skipped on later runs.")]
    internal sealed class MassRenderArgs : ITwitchDownloaderArgs
    {
        [Option('u', "user", Required = true, HelpText = "Twitch login name of the user (a channel URL also works).")]
        public string User { get; set; }

        [Option('o', "output", Required = true, HelpText = "Base output directory. Each video gets its own <id> subfolder with the chat JSON and the rendered MP4.")]
        public string OutputDirectory { get; set; }

        [Option("limit", Default = 5, HelpText = "Maximum number of NEW videos to process in this run (videos already in the state file are skipped and do not count).")]
        public int Limit { get; set; }

        [Option("type", Default = "", HelpText = "Video type filter: '' (all), ARCHIVE (past broadcasts), HIGHLIGHT, or UPLOAD.")]
        public string VideoType { get; set; }

        [Option("state", HelpText = "Path to the state file listing already-processed video IDs (one per line). Defaults to <output>/mass-state.txt. IDs are appended only after a video's download AND render both succeed.")]
        public string StateFile { get; set; }

        [Option("dry-run", Default = false, HelpText = "Only list which videos would be processed; download and render nothing.")]
        public bool DryRun { get; set; }

        [Option("render-start", Default = 0.0, HelpText = "Start time (seconds into the chat) to render from. Default renders from the beginning.")]
        public double RenderStart { get; set; }

        [Option("render-length", Default = 0.0, HelpText = "Length in seconds to render per video. 0 = the whole video.")]
        public double RenderLength { get; set; }

        [Option("duration", Default = 6.0, HelpText = "Seconds a comment needs to cross the frame width.")]
        public double Duration { get; set; }

        [Option("speed-mode", Default = "uniform", HelpText = "'uniform' = every comment moves at the same speed; 'fixed' = fixed display duration (longer comments move faster).")]
        public string SpeedMode { get; set; }

        [Option("max-lanes", Default = 0, HelpText = "Maximum number of lanes to use (0 = no extra limit). Excess comments are dropped.")]
        public int MaxLanes { get; set; }

        [Option('w', "width", Default = 1920, HelpText = "Width of the rendered frame.")]
        public int Width { get; set; }

        [Option('h', "height", Default = 1080, HelpText = "Height of the rendered frame.")]
        public int Height { get; set; }

        [Option("font-size", Default = 36.0f, HelpText = "Font size of the comments.")]
        public float FontSize { get; set; }

        [Option("fps", Default = 30, HelpText = "Framerate of the rendered videos.")]
        public int Fps { get; set; }

        [Option("shadow", Default = false, HelpText = "Draw a dark shadow silhouette behind each comment for readability.")]
        public bool Shadow { get; set; }

        [Option("band", Default = 0.0, HelpText = "Opacity (0..1) of a translucent backing band behind each comment. 0 = off.")]
        public double BandOpacity { get; set; }

        [Option("show-name", Default = true, HelpText = "Show the commenter's name before the message.")]
        public bool? ShowName { get; set; }

        [Option("ffmpeg-path", HelpText = "Path to FFmpeg executable. Defaults to 'ffmpeg' on PATH.")]
        public string FfmpegPath { get; set; }

        [Option("temp-path", HelpText = "Path to the temp folder used while downloading/rendering.")]
        public string TempFolder { get; set; }

        public bool? ShowBanner { get; set; }

        public LogLevel LogLevel { get; set; }
    }
}
