using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The Spotify id of a track, found by its title, artist and length through Spotify's own search (the official Web API, with the
    /// client credentials of a free developer app: no user is signed in). The Spicy Lyrics API is asked for lyrics by that id, and Windows
    /// only tells the player the title and the artist. Ids found are kept on disk.
    /// </summary>
    public static class SpotifyTrackLookup
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

        private static readonly TimeSpan LengthTolerance = TimeSpan.FromSeconds(4);

        private static readonly string DiskPath = "spotify-track-ids.json".InLocalAppData();
        private static readonly object Gate = new();
        private static Dictionary<string, string> _ids;

        private static string _token;
        private static DateTime _tokenExpires;

        /// <summary>The Spotify credentials of the settings (or the environment), or null if they are not both given.</summary>
        public static (string Id, string Secret)? Credentials()
        {
            string id = Settings.Instance.SpotifyClientId;
            string secret = Settings.Instance.SpotifyClientSecret;
            if (string.IsNullOrWhiteSpace(id))
            {
                id = Environment.GetEnvironmentVariable("RETROBAR_SPOTIFY_CLIENT_ID");
            }

            if (string.IsNullOrWhiteSpace(secret))
            {
                secret = Environment.GetEnvironmentVariable("RETROBAR_SPOTIFY_CLIENT_SECRET");
            }

            return string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret) ? null : (id.Trim(), secret.Trim());
        }

        /// <summary>The id of the track, or null if Spotify has none like it; throws if the search could not be made.</summary>
        public static async Task<string> FindIdAsync(string artist, string title, TimeSpan duration)
        {
            string key = $"{artist}|{title}|{(int)duration.TotalSeconds / 3}";
            lock (Gate)
            {
                LoadDisk();
                if (_ids.TryGetValue(key, out string known))
                {
                    return known;
                }
            }

            string id = (await FindTrackAsync(artist, title, duration).ConfigureAwait(false))?.Id;

            if (id != null)
            {
                lock (Gate)
                {
                    LoadDisk();
                    if (_ids.Count > 2000)
                    {
                        _ids.Clear();
                    }

                    _ids[key] = id;
                    try
                    {
                        File.WriteAllText(DiskPath, JsonSerializer.Serialize(_ids));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Only a convenience.
                    }
                }
            }

            return id;
        }

        /// <summary>A track found, with the album it is on and the artist it is under.</summary>
        public sealed record SpotifyTrack(string Id, string AlbumId, string ArtistId);

        private static readonly Dictionary<string, SpotifyTrack> Found = new();

        /// <summary>The track with its album and artist (for links to them), or null if Spotify has none like it; throws if the search failed.</summary>
        public static async Task<SpotifyTrack> FindTrackAsync(string artist, string title, TimeSpan duration)
        {
            string key = $"{artist}|{title}|{(int)duration.TotalSeconds / 3}";
            lock (Gate)
            {
                if (Found.TryGetValue(key, out SpotifyTrack known))
                {
                    return known;
                }
            }

            SpotifyTrack track = await SearchAsync($"track:\"{Clean(title)}\" artist:\"{Clean(FirstArtist(artist))}\"", artist, title, duration).ConfigureAwait(false)
                                 ?? await SearchAsync($"{Clean(title)} {Clean(FirstArtist(artist))}", artist, title, duration).ConfigureAwait(false);
            if (track != null)
            {
                lock (Gate)
                {
                    if (Found.Count > 500)
                    {
                        Found.Clear();
                    }

                    Found[key] = track;
                }
            }

            return track;
        }

        private static void LoadDisk()
        {
            if (_ids != null)
            {
                return;
            }

            try
            {
                _ids = File.Exists(DiskPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(DiskPath)) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A damaged file is the same as none.
            }

            _ids ??= new Dictionary<string, string>();
        }

        private static string AlbumIdOf(JsonElement item)
        {
            return item.TryGetProperty("album", out JsonElement album) && album.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }

        // The id of the artist the track is under: the one named, else the first.
        private static string ArtistIdOf(JsonElement item, string name)
        {
            if (!item.TryGetProperty("artists", out JsonElement artists) || artists.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string first = null;
            foreach (JsonElement artist in artists.EnumerateArray())
            {
                if (!artist.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                first ??= id.GetString();
                if (artist.TryGetProperty("name", out JsonElement artistName) && artistName.ValueKind == JsonValueKind.String
                    && string.Equals(artistName.GetString()?.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return id.GetString();
                }
            }

            return first;
        }

        private static string Clean(string text) => (text ?? "").Replace("\"", " ").Trim();

        // "Artist, Other Artist" (or with ; / &): the first one is the one the track is under.
        private static string FirstArtist(string artist)
        {
            if (string.IsNullOrWhiteSpace(artist))
            {
                return "";
            }

            int cut = artist.IndexOfAny(new[] { ',', ';', '/', '&' });
            return cut > 0 ? artist[..cut] : artist;
        }

        private static async Task<SpotifyTrack> SearchAsync(string query, string artist, string title, TimeSpan duration)
        {
            string token = await TokenAsync().ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/search?type=track&limit=10&q=" + Uri.EscapeDataString(query));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _token = null;
            }

            response.EnsureSuccessStatusCode();
            return PickTrack(await response.Content.ReadAsStringAsync().ConfigureAwait(false), artist, title, duration);
        }

        private static async Task<string> TokenAsync()
        {
            if (_token != null && DateTime.UtcNow < _tokenExpires)
            {
                return _token;
            }

            (string Id, string Secret)? credentials = Credentials();
            if (credentials == null)
            {
                throw new InvalidOperationException("No Spotify credentials.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.Value.Id}:{credentials.Value.Secret}")));
            using HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            _token = document.RootElement.GetProperty("access_token").GetString();
            _tokenExpires = DateTime.UtcNow.AddSeconds(Math.Max(60, document.RootElement.GetProperty("expires_in").GetInt32() - 60));
            return _token;
        }

        // The id of the result that is the track: of those as long as it, the one with the same name (if one has), else the closest in length.
        internal static string Pick(string json, string title, TimeSpan duration) => PickTrack(json, null, title, duration)?.Id;

        internal static SpotifyTrack PickTrack(string json, string artist, string title, TimeSpan duration)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("tracks", out JsonElement tracks) || !tracks.TryGetProperty("items", out JsonElement items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            SpotifyTrack best = null;
            double bestScore = double.MaxValue;
            foreach (JsonElement item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                double difference = 0;
                if (duration > TimeSpan.Zero && item.TryGetProperty("duration_ms", out JsonElement length) && length.ValueKind == JsonValueKind.Number)
                {
                    difference = Math.Abs(length.GetDouble() / 1000 - duration.TotalSeconds);
                    if (difference > LengthTolerance.TotalSeconds)
                    {
                        continue;
                    }
                }

                bool sameName = item.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String
                                && string.Equals(name.GetString()?.Trim(), title?.Trim(), StringComparison.OrdinalIgnoreCase);
                double score = difference + (sameName ? 0 : 100);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = new SpotifyTrack(id.GetString(), AlbumIdOf(item), ArtistIdOf(item, FirstArtist(artist)));
                }
            }

            return best;
        }
    }
}
