namespace TwitchDownloaderCore.Tools
{
    /// <summary>
    /// A plain-text record of already-processed video/clip IDs, shared by the
    /// CLI (massrender state file) and the GUI (dedup list) so both tools skip
    /// work the other one already finished.
    ///
    /// Format: one entry per line, tab-separated:
    ///   &lt;id&gt;\t&lt;UTC timestamp&gt;\t&lt;title&gt;
    /// Only the first token of each line is read back as the ID, so files with
    /// bare IDs (one per line) or '#' comment lines are also valid.
    /// IDs are appended only after processing fully succeeded; anything else
    /// is retried on the next run.
    /// </summary>
    public static class DedupList
    {
        private static readonly object FileLock = new();

        public static HashSet<string> LoadIds(string path)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return ids;

            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;
                var id = trimmed.Split([' ', '\t'], 2)[0];
                if (id.Length > 0)
                    ids.Add(id);
            }
            return ids;
        }

        public static bool IsRecorded(string path, string id)
        {
            return !string.IsNullOrWhiteSpace(id) && LoadIds(path).Contains(id);
        }

        public static void Append(string path, string id, string title)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(id))
                return;

            lock (FileLock)
            {
                if (LoadIds(path).Contains(id))
                    return;

                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var safeTitle = (title ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
                using var writer = new StreamWriter(path, append: true);
                writer.WriteLine($"{id}\t{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\t{safeTitle}");
                writer.Flush();
            }
        }
    }
}
