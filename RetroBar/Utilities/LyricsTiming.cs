using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The timing of the lyrics that was set for one song (from the media popup's menu), kept in a file for the next time it plays.
    /// A song without one has the global timing (<see cref="Settings.LyricsLeadMs"/>, set in the properties).
    /// </summary>
    public static class LyricsTiming
    {
        private static readonly string FilePath = "lyrics-timing.json".InLocalAppData();
        private static Dictionary<string, int> _known;

        public static string KeyOf(string artist, string title) => $"{artist}|{title}";

        private static Dictionary<string, int> Known()
        {
            if (_known == null)
            {
                try
                {
                    _known = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(FilePath));
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                }

                _known ??= new Dictionary<string, int>();
            }

            return _known;
        }

        /// <summary>The timing set for this song, or null when it follows the global one.</summary>
        public static int? Get(string key)
        {
            return key != null && Known().TryGetValue(key, out int ms) ? ms : null;
        }

        /// <summary>Sets the timing of a song; null makes it follow the global one again.</summary>
        public static void Set(string key, int? ms)
        {
            if (key == null)
            {
                return;
            }

            Dictionary<string, int> known = Known();
            if (ms is int value)
            {
                known[key] = Math.Clamp(value, -3000, 3000);
            }
            else if (!known.Remove(key))
            {
                return;
            }

            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(known));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
