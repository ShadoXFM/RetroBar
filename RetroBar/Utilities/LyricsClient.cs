using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RetroBar.Utilities
{
    /// <summary>One line of synced lyrics: what is sung from <see cref="Time"/> on (empty between the verses).</summary>
    public record LyricLine(TimeSpan Time, string Text);

    /// <summary>
    /// Synced lyrics from lrclib.net (a free, open lyrics database): found by the track's title and artist (and the one whose length is
    /// closest to the track's), as the lines with the time each starts. The lyrics found are kept on disk (lyrics-cache.json), so a track
    /// is asked for once, and its lyrics are there without a connection too. A track that has none gives null, and is not asked for again
    /// until the app restarts; a request that fails (the service down, no connection) throws, and is not remembered.
    /// </summary>
    public static class LyricsClient
    {
        private static readonly HttpClient Http = CreateClient();

        private static readonly Dictionary<string, LyricLine[]> Cache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex Timestamp = new(@"\[(\d+):(\d+(?:\.\d+)?)\]", RegexOptions.Compiled);

        // A track whose length differs from the track's by more than this is a different recording.
        private static readonly TimeSpan LengthTolerance = TimeSpan.FromSeconds(4);

        private const int CacheLimit = 60;

        // What is kept on disk: the LRC text of each track found, the oldest forgotten beyond the limit.
        private const int DiskLimit = 300;
        private static readonly string DiskPath = "lyrics-cache.json".InLocalAppData();
        private static Dictionary<string, DiskEntry> _disk;

        private sealed class DiskEntry
        {
            public string Lrc { get; set; }
            public DateTime Saved { get; set; }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RetroBar (https://github.com/ShadoXFM/RetroBar)");
            return client;
        }

        /// <summary>The synced lyrics of a track, or null if there are none; throws if they could not be asked for. Safe to call from any thread.</summary>
        public static async Task<LyricLine[]> GetAsync(string artist, string title, TimeSpan duration)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            string key = $"{artist}|{title}";
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out LyricLine[] cached))
                {
                    return cached;
                }
            }

            LyricLine[] lines = ReadDisk(key) is string kept ? Parse(kept) : null;
            if (lines == null)
            {
                string url = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title)
                    + (string.IsNullOrWhiteSpace(artist) ? "" : "&artist_name=" + Uri.EscapeDataString(artist));
                string json = await Http.GetStringAsync(url).ConfigureAwait(false);
                string lrc = PickSynced(json, duration);
                lines = lrc == null ? null : Parse(lrc);
                if (lrc != null && lines != null)
                {
                    WriteDisk(key, lrc);
                }
            }

            lock (Cache)
            {
                if (Cache.Count >= CacheLimit)
                {
                    Cache.Clear();
                }

                Cache[key] = lines;
            }

            return lines;
        }

        private static void LoadDisk()
        {
            if (_disk != null)
            {
                return;
            }

            try
            {
                if (File.Exists(DiskPath))
                {
                    _disk = JsonSerializer.Deserialize<Dictionary<string, DiskEntry>>(File.ReadAllText(DiskPath));
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A damaged file is the same as none: it is written again.
            }

            _disk = _disk == null ? new Dictionary<string, DiskEntry>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, DiskEntry>(_disk, StringComparer.OrdinalIgnoreCase);
        }

        private static string ReadDisk(string key)
        {
            lock (Cache)
            {
                LoadDisk();
                return _disk.TryGetValue(key, out DiskEntry entry) ? entry?.Lrc : null;
            }
        }

        private static void WriteDisk(string key, string lrc)
        {
            lock (Cache)
            {
                LoadDisk();
                _disk[key] = new DiskEntry { Lrc = lrc, Saved = DateTime.UtcNow };
                foreach (string oldest in _disk.OrderByDescending(pair => pair.Value?.Saved ?? DateTime.MinValue).Skip(DiskLimit).Select(pair => pair.Key).ToList())
                {
                    _disk.Remove(oldest);
                }

                try
                {
                    File.WriteAllText(DiskPath, JsonSerializer.Serialize(_disk));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Only a convenience: failing to save it changes nothing else.
                }
            }
        }

        // The LRC text of the result with synced lyrics whose length is the closest to the track's (the first one when the length is not
        // known); null if none has.
        internal static string PickSynced(string json, TimeSpan duration)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string best = null;
            double bestDifference = double.MaxValue;
            foreach (JsonElement result in document.RootElement.EnumerateArray())
            {
                if (!result.TryGetProperty("syncedLyrics", out JsonElement synced) || synced.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(synced.GetString()))
                {
                    continue;
                }

                double difference = 0;
                if (duration > TimeSpan.Zero && result.TryGetProperty("duration", out JsonElement length) && length.ValueKind == JsonValueKind.Number)
                {
                    difference = Math.Abs(length.GetDouble() - duration.TotalSeconds);
                    if (difference > LengthTolerance.TotalSeconds)
                    {
                        continue;
                    }
                }

                if (difference < bestDifference)
                {
                    bestDifference = difference;
                    best = synced.GetString();
                }
            }

            return best;
        }

        /// <summary>The lines of an LRC text ("[01:23.45] words", several stamps allowed on a line), in time order; null if it has none.</summary>
        public static LyricLine[] Parse(string lrc)
        {
            var lines = new List<LyricLine>();
            foreach (string raw in lrc.Split('\n'))
            {
                MatchCollection stamps = Timestamp.Matches(raw);
                if (stamps.Count == 0)
                {
                    continue;
                }

                string text = Timestamp.Replace(raw, "").Trim();
                foreach (Match stamp in stamps)
                {
                    double seconds = int.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                                     + double.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture);
                    lines.Add(new LyricLine(TimeSpan.FromSeconds(seconds), text));
                }
            }

            return lines.Count == 0 ? null : lines.OrderBy(line => line.Time).ToArray();
        }

        /// <summary>The index of the line being sung at a position: the last one that has started (-1 before the first).</summary>
        public static int IndexAt(LyricLine[] lines, TimeSpan position)
        {
            int current = -1;
            for (int i = 0; i < lines.Length && lines[i].Time <= position; i++)
            {
                current = i;
            }

            return current;
        }

        /// <summary>The line being sung at a position: the last one that has started (null before the first).</summary>
        public static LyricLine LineAt(LyricLine[] lines, TimeSpan position)
        {
            int index = IndexAt(lines, position);
            return index < 0 ? null : lines[index];
        }
    }
}
