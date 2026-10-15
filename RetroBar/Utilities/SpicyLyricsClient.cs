using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Lyrics from the Spicy Lyrics API (developers.spicylyrics.org): by the track's Spotify id (see SpotifyTrackLookup), as the best sync
    /// there is - community-made lyrics with the timing of each word, or else line by line. Their terms ask for the provider (and the
    /// uploader, for community lyrics) to be shown wherever the lyrics are, which is what the credit of the result is for. The responses
    /// are kept in files, so that a track is asked for once. A track with no (or only untimed) lyrics gives null; a request that fails
    /// throws, and is not remembered.
    /// </summary>
    public static class SpicyLyricsClient
    {
        private static readonly HttpClient Http = CreateClient();

        private static readonly string Folder = "spicy-lyrics".InLocalAppData();
        private const int FileLimit = 200;

        private static readonly Dictionary<string, LyricsResult> Cache = new(StringComparer.OrdinalIgnoreCase);

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RetroBar (https://github.com/ShadoXFM/RetroBar)");
            return client;
        }

        /// <summary>The key of the settings (or the environment), or null if there is none.</summary>
        public static string Key()
        {
            string key = Settings.Instance.SpicyLyricsKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                key = Environment.GetEnvironmentVariable("RETROBAR_SPICY_KEY");
            }

            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }

        /// <summary>Whether it is set up: the Spicy Lyrics key and the Spotify credentials are given.</summary>
        public static bool IsConfigured => Key() != null && SpotifyTrackLookup.Credentials() != null;

        /// <summary>The lyrics of a track, or null if there are none (or they have no timing); throws if they could not be asked for.</summary>
        public static async Task<LyricsResult> GetAsync(string artist, string title, TimeSpan duration)
        {
            string key = Key();
            if (key == null || string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            string id = await SpotifyTrackLookup.FindIdAsync(artist, title, duration).ConfigureAwait(false);
            if (id == null)
            {
                return null;
            }

            lock (Cache)
            {
                if (Cache.TryGetValue(id, out LyricsResult cached))
                {
                    return cached;
                }
            }

            string json = ReadFile(id);
            if (json == null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spicylyrics.org/v1/lyrics/" + Uri.EscapeDataString(id));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    Remember(id, null);
                    return null;
                }

                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                LyricsResult fresh = Parse(json, duration);
                if (fresh != null)
                {
                    WriteFile(id, json);
                }

                Remember(id, fresh);
                return fresh;
            }

            LyricsResult result = Parse(json, duration);
            Remember(id, result);
            return result;
        }

        private static void Remember(string id, LyricsResult result)
        {
            lock (Cache)
            {
                if (Cache.Count >= 60)
                {
                    Cache.Clear();
                }

                Cache[id] = result;
            }
        }

        private static string ReadFile(string id)
        {
            try
            {
                string path = Path.Combine(Folder, id + ".json");
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void WriteFile(string id, string json)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(Path.Combine(Folder, id + ".json"), json);
                foreach (FileInfo old in new DirectoryInfo(Folder).GetFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc).Skip(FileLimit))
                {
                    old.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Only a convenience.
            }
        }

        private static bool TryGet(JsonElement element, string name, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static string Text(JsonElement element, string name) =>
            TryGet(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        // The times are in seconds; one that is in milliseconds (a "length" of hours) is told by the track's own length.
        private static double TimeUnit(JsonElement content, TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero || content.ValueKind != JsonValueKind.Array)
            {
                return 1;
            }

            double latest = 0;
            foreach (JsonElement line in content.EnumerateArray())
            {
                if (TryGet(line, "EndTime", out JsonElement end) && end.ValueKind == JsonValueKind.Number)
                {
                    latest = Math.Max(latest, end.GetDouble());
                }
                else if (TryGet(line, "Lead", out JsonElement lead) && TryGet(lead, "EndTime", out JsonElement leadEnd) && leadEnd.ValueKind == JsonValueKind.Number)
                {
                    latest = Math.Max(latest, leadEnd.GetDouble());
                }
            }

            return latest > duration.TotalSeconds * 20 ? 0.001 : 1;
        }

        private static TimeSpan Time(JsonElement element, string name, double unit) =>
            TryGet(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                ? TimeSpan.FromSeconds(value.GetDouble() * unit)
                : TimeSpan.Zero;

        /// <summary>The lyrics of a response (see the Spicy Lyrics API's Syllable and Line types); null for untimed ones or ones that cannot be read.</summary>
        public static LyricsResult Parse(string json, TimeSpan duration = default)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }

            using (document)
            {
                JsonElement body = TryGet(document.RootElement, "Body", out JsonElement inner) ? inner : document.RootElement;
                string type = Text(body, "Type");
                if (!TryGet(body, "Content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                double unit = TimeUnit(content, duration);
                var lines = new List<LyricLine>();
                if (string.Equals(type, "Syllable", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (JsonElement line in content.EnumerateArray())
                    {
                        LyricLine parsed = SyllableLine(line, unit);
                        if (parsed != null)
                        {
                            lines.Add(parsed);
                        }
                    }
                }
                else if (string.Equals(type, "Line", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (JsonElement line in content.EnumerateArray())
                    {
                        lines.Add(new LyricLine(Time(line, "StartTime", unit), (Text(line, "Text") ?? "").Trim(), null, Time(line, "EndTime", unit)));
                    }
                }
                else
                {
                    return null;
                }

                if (lines.Count == 0)
                {
                    return null;
                }

                LyricLine[] ordered = WithPauses(lines.OrderBy(line => line.Time).ToList());
                (string credit, string url) = Credit(body);
                return new LyricsResult(ordered, credit, url);
            }
        }

        // A pause longer than this (before the first line, or between two) is a line of its own with no words: shown as a note.
        private static readonly TimeSpan PauseLength = TimeSpan.FromSeconds(4);

        internal static LyricLine[] WithPauses(List<LyricLine> lines)
        {
            var result = new List<LyricLine>();
            if (lines[0].Time >= PauseLength)
            {
                result.Add(new LyricLine(TimeSpan.Zero, ""));
            }

            for (int i = 0; i < lines.Count; i++)
            {
                result.Add(lines[i]);
                if (i + 1 < lines.Count && lines[i].End is TimeSpan end && lines[i + 1].Time - end >= PauseLength)
                {
                    result.Add(new LyricLine(end, ""));
                }
            }

            return result.ToArray();
        }

        // A line of syllables: the lead vocals, and the background ones (in brackets) after them. The timing of every syllable is kept.
        private static LyricLine SyllableLine(JsonElement line, double unit)
        {
            if (!TryGet(line, "Lead", out JsonElement lead))
            {
                return null;
            }

            var words = new List<LyricWord>();
            AddSyllables(lead, unit, words, brackets: false);
            if (TryGet(line, "Background", out JsonElement background) && background.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement group in background.EnumerateArray())
                {
                    AddSyllables(group, unit, words, brackets: true);
                }
            }

            if (words.Count == 0)
            {
                return null;
            }

            TimeSpan start = TryGet(lead, "StartTime", out _) ? Time(lead, "StartTime", unit) : words[0].Start;
            return new LyricLine(start, string.Concat(words.Select(word => word.Text)).Trim(), words.ToArray(), words.Max(word => word.End));
        }

        private static void AddSyllables(JsonElement group, double unit, List<LyricWord> words, bool brackets)
        {
            if (!TryGet(group, "Syllables", out JsonElement syllables) || syllables.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            int first = words.Count;
            foreach (JsonElement syllable in syllables.EnumerateArray())
            {
                string text = Text(syllable, "Text");
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                bool partOfWord = TryGet(syllable, "IsPartOfWord", out JsonElement part) && part.ValueKind == JsonValueKind.True;
                words.Add(new LyricWord(Time(syllable, "StartTime", unit), Time(syllable, "EndTime", unit), partOfWord ? text : text + " "));
            }

            if (brackets && words.Count > first)
            {
                LyricWord opening = words[first];
                words[first] = opening with { Text = "(" + opening.Text };
                LyricWord closing = words[^1];
                words[^1] = closing with { Text = closing.Text.TrimEnd() + ") " };
            }
        }

        // The provider and, for community lyrics, who uploaded (and made) them: what the terms ask to be shown with the lyrics.
        private static (string Credit, string Url) Credit(JsonElement body)
        {
            string source = Text(body, "source");
            string provider = source switch
            {
                "apple_music" => "Spicy Lyrics (Apple Music)",
                "spotify" => "Spicy Lyrics (Spotify)",
                _ => "Spicy Lyrics",
            };

            string url = null;
            if (string.Equals(source, "spicy_lyrics", StringComparison.OrdinalIgnoreCase) && TryGet(body, "UploadAttribution", out JsonElement attribution))
            {
                var credit = new StringBuilder(provider);
                if (TryGet(attribution, "Uploader", out JsonElement uploader) && Text(uploader, "username") is string uploaderName)
                {
                    credit.Append(" · uploaded by ").Append(uploaderName);
                    url = SafeUrl(Text(uploader, "url"));
                }

                if (TryGet(attribution, "Maker", out JsonElement maker) && Text(maker, "username") is string makerName)
                {
                    credit.Append(" · made by ").Append(makerName);
                    url ??= SafeUrl(Text(maker, "url"));
                }

                return (credit.ToString(), url);
            }

            return (provider, null);
        }

        // Only a web address is opened from the credit.
        private static string SafeUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out Uri uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null;
    }
}
