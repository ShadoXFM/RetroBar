using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ManagedShell.Common.Logging;

namespace RetroBar.Utilities
{
    /// <summary>One line of synced lyrics: what is sung from <see cref="Time"/> on (empty between the verses).</summary>
    public record LyricLine(TimeSpan Time, string Text);

    /// <summary>
    /// Synced lyrics from lrclib.net (a free, open lyrics database): found by the track's title and artist (and the one whose length is
    /// closest to the track's), as the lines with the time each starts. A track that has none, or a failed request, gives null, and is
    /// not asked for again until the app restarts.
    /// </summary>
    public static class LyricsClient
    {
        private static readonly HttpClient Http = CreateClient();

        private static readonly Dictionary<string, LyricLine[]> Cache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex Timestamp = new(@"\[(\d+):(\d+(?:\.\d+)?)\]", RegexOptions.Compiled);

        // A track whose length differs from the track's by more than this is a different recording.
        private static readonly TimeSpan LengthTolerance = TimeSpan.FromSeconds(4);

        private const int CacheLimit = 60;

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RetroBar (https://github.com/ShadoXFM/RetroBar)");
            return client;
        }

        /// <summary>The synced lyrics of a track, or null if there are none. Safe to call from any thread.</summary>
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

            LyricLine[] lines = null;
            try
            {
                string url = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title)
                    + (string.IsNullOrWhiteSpace(artist) ? "" : "&artist_name=" + Uri.EscapeDataString(artist));
                string json = await Http.GetStringAsync(url).ConfigureAwait(false);
                lines = Pick(json, duration);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                ShellLogger.Debug($"LyricsClient: no lyrics for '{key}': {ex.Message}");
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

        // The result with synced lyrics whose length is the closest to the track's (the first one when the length is not known).
        private static LyricLine[] Pick(string json, TimeSpan duration)
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

            return best == null ? null : Parse(best);
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

        /// <summary>The line being sung at a position: the last one that has started (null before the first).</summary>
        public static LyricLine LineAt(LyricLine[] lines, TimeSpan position)
        {
            LyricLine current = null;
            foreach (LyricLine line in lines)
            {
                if (line.Time > position)
                {
                    break;
                }

                current = line;
            }

            return current;
        }
    }
}
