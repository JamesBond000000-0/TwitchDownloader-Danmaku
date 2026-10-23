namespace TwitchDownloaderCore.Tools
{
    /// <summary>
    /// A plain-text record of already-processed videos/clips, shared by the
    /// CLI (massrender state file) and the GUI (dedup list) so both tools skip
    /// work the other one already finished.
    ///
    /// Records are per processing kind (video download / chat download /
    /// render): having the chat of a video must not block downloading the
    /// video itself later.
    ///
    /// Format: one entry per line, tab-separated:
    ///   &lt;id&gt;\t&lt;UTC timestamp&gt;\t&lt;title&gt;\t&lt;kinds&gt;
    /// where &lt;kinds&gt; is a comma-separated subset of "video,chat,render".
    /// Multiple lines with the same ID are unioned. Lines without a kinds
    /// field (written before per-kind records, e.g. by massrender, which
    /// only ever does chat+render) count as {chat, render}. Only the first
    /// token of each line is the ID, so bare IDs and '#' comment lines are
    /// also valid (they count as {chat, render} like other legacy lines).
    /// Entries are appended only after the corresponding work succeeded;
    /// anything else is retried on the next run.
    /// </summary>
    public static class DedupList
    {
        public const string KindVideo = "video";
        public const string KindChat = "chat";
        public const string KindRender = "render";

        private static readonly string[] KnownKinds = [KindVideo, KindChat, KindRender];
        private static readonly object FileLock = new();

        public static Dictionary<string, HashSet<string>> LoadRecords(string path)
        {
            var records = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return records;

            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;

                var fields = trimmed.Split('\t');
                var id = fields[0].Split(' ', 2)[0];
                if (id.Length == 0)
                    continue;

                var kinds = ParseKinds(fields);
                if (!records.TryGetValue(id, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    records[id] = set;
                }
                set.UnionWith(kinds);
            }
            return records;
        }

        private static IEnumerable<string> ParseKinds(string[] fields)
        {
            if (fields.Length >= 4)
            {
                var kinds = fields[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(k => k.ToLowerInvariant())
                    .Where(k => KnownKinds.Contains(k))
                    .ToArray();
                if (kinds.Length > 0)
                    return kinds;
            }

            // Legacy line without (valid) kinds: the writers of such lines
            // (massrender, early GUI dedup) processed chat and rendered it.
            return [KindChat, KindRender];
        }

        public static HashSet<string> LoadIds(string path)
        {
            return new HashSet<string>(LoadRecords(path).Keys, StringComparer.Ordinal);
        }

        public static bool IsRecorded(string path, string id, params string[] kinds)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;
            var records = LoadRecords(path);
            if (!records.TryGetValue(id, out var done))
                return false;
            return kinds.Length == 0 || kinds.All(done.Contains);
        }

        /// <summary>
        /// Appends an entry for <paramref name="id"/> unless every given kind
        /// is already recorded for it. With no kinds, appends a legacy entry
        /// (counted as chat+render) unless the ID is present at all.
        /// </summary>
        public static void Append(string path, string id, string title, params string[] kinds)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(id))
                return;

            kinds = kinds.Select(k => k?.ToLowerInvariant()).Where(k => k is not null && KnownKinds.Contains(k)).Distinct().ToArray();

            lock (FileLock)
            {
                var records = LoadRecords(path);
                if (records.TryGetValue(id, out var done) && (kinds.Length == 0 || kinds.All(done.Contains)))
                    return;

                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var safeTitle = (title ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
                using var writer = new StreamWriter(path, append: true);
                var line = $"{id}\t{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\t{safeTitle}";
                if (kinds.Length > 0)
                    line += $"\t{string.Join(",", kinds)}";
                writer.WriteLine(line);
                writer.Flush();
            }
        }
    }
}
