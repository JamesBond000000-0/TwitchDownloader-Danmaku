using System;
using System.IO;
using TwitchDownloaderWPF.Properties;

namespace TwitchDownloaderWPF.Utils
{
    /// <summary>
    /// Resolves where the dedup (already-processed videos) list lives.
    /// The user can point it anywhere in the settings; the same file format is
    /// used by the CLI's massrender state file, so both tools share one list.
    /// </summary>
    internal static class DedupSettings
    {
        public static string GetEffectivePath()
        {
            if (!string.IsNullOrWhiteSpace(Settings.Default.DedupListPath))
                return Settings.Default.DedupListPath;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "TwitchDownloader-Danmaku",
                "processed-ids.txt");
        }

        public static bool IsEnabled => Settings.Default.DedupEnabled;
    }
}
