using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The position and the length of a track as a player writes them in its window's title ("[1:23 / 3:45]"), for a player that does not
    /// report them to Windows (foobar2000 reports nothing: its title only has them if its format says so, with %playback_time% and
    /// %length%, which it keeps up to date).
    /// </summary>
    public static class TitleTimeline
    {
        private static readonly Regex Times = new(
            @"(?<![\d:])(?<position>\d+:\d{2}(?::\d{2})?)\s*/\s*(?<length>\d+:\d{2}(?::\d{2})?)(?![\d:])",
            RegexOptions.Compiled);

        /// <summary>The first "position / length" in a title, as two times ("m:ss" or "h:mm:ss"); false if it has none.</summary>
        public static bool TryParse(string title, out TimeSpan position, out TimeSpan length)
        {
            position = length = TimeSpan.Zero;
            if (string.IsNullOrEmpty(title))
            {
                return false;
            }

            Match match = Times.Match(title);
            return match.Success && TryParseClock(match.Groups["position"].Value, out position) && TryParseClock(match.Groups["length"].Value, out length);
        }

        private static bool TryParseClock(string text, out TimeSpan time)
        {
            string[] parts = text.Split(':');
            time = TimeSpan.Zero;
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
            {
                time = new TimeSpan(0, minutes, seconds);
                return true;
            }

            if (parts.Length == 3 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes)
                && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
            {
                time = new TimeSpan(hours, minutes, seconds);
                return true;
            }

            return false;
        }
    }
}
