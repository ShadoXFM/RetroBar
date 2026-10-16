using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The culture of the language chosen in the Properties window, for what is written with the app's own formats (the days, the times of
    /// the day of the weather): the language files are named by what the language is called in it ("français", "English (United
    /// Kingdom)"), which is what a culture calls itself too. With "System" chosen, or a language that no culture is called, it is the
    /// system's culture, as the clock has.
    /// </summary>
    public static class LanguageCulture
    {
        private static readonly Dictionary<string, CultureInfo> Found = new(StringComparer.OrdinalIgnoreCase);

        public static CultureInfo Current => For(Settings.Instance.Language);

        public static CultureInfo For(string language)
        {
            if (string.IsNullOrWhiteSpace(language) || string.Equals(language, "System", StringComparison.OrdinalIgnoreCase))
            {
                return CultureInfo.CurrentCulture;
            }

            lock (Found)
            {
                if (!Found.TryGetValue(language, out CultureInfo culture))
                {
                    culture = Find(language) ?? CultureInfo.CurrentCulture;
                    Found[language] = culture;
                }

                return culture;
            }
        }

        // The culture called that; a language without a country (a neutral culture) gets the one its speakers mostly are in.
        private static CultureInfo Find(string language)
        {
            string wanted = Normalize(language);
            CultureInfo[] all = CultureInfo.GetCultures(CultureTypes.AllCultures);

            // A specific culture ("English (United Kingdom)") first, then a neutral one ("français").
            CultureInfo match = all.FirstOrDefault(c => c.Name.Length > 0 && !c.IsNeutralCulture && Normalize(c.NativeName) == wanted)
                                ?? all.FirstOrDefault(c => c.Name.Length > 0 && c.IsNeutralCulture && Normalize(c.NativeName) == wanted);
            if (match == null)
            {
                return null;
            }

            try
            {
                return match.IsNeutralCulture ? CultureInfo.CreateSpecificCulture(match.Name) : match;
            }
            catch (CultureNotFoundException)
            {
                return match;
            }
        }

        // Without case, punctuation and spaces ("Norsk (bokmål)" and "norsk bokmål" are the same).
        private static string Normalize(string name) => new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
