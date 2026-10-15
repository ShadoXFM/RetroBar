using System;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RetroBar.Utilities;
using Xunit;

namespace RetroBar.Tests
{
    public class CoverColorTests
    {
        private static BitmapSource Solid(byte red, byte green, byte blue, int size = 16)
        {
            var pixels = new byte[size * size * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = blue;
                pixels[i + 1] = green;
                pixels[i + 2] = red;
                pixels[i + 3] = 255;
            }

            return BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        }

        [Fact]
        public void A_vivid_cover_gives_a_colour_of_its_hue()
        {
            Color? color = CoverColor.Vibrant(Solid(200, 30, 30));

            Assert.NotNull(color);
            Assert.True(color.Value.R > color.Value.G + 40 && color.Value.R > color.Value.B + 40, $"not red: {color}");
        }

        [Fact]
        public void A_grey_cover_gives_a_light_grey()
        {
            Color? color = CoverColor.Vibrant(Solid(40, 40, 40));

            Assert.NotNull(color);
            Assert.InRange(Math.Abs(color.Value.R - color.Value.G), 0, 2);
            Assert.InRange(Math.Abs(color.Value.G - color.Value.B), 0, 2);
            Assert.True(color.Value.R > 140, $"not light: {color}");
        }

        [Fact]
        public void The_vivid_part_wins_over_a_big_dull_one()
        {
            // 3/4 dark grey, 1/4 blue.
            var pixels = new byte[16 * 16 * 4];
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    int i = (y * 16 + x) * 4;
                    bool blue = x >= 12;
                    pixels[i] = blue ? (byte)220 : (byte)50;
                    pixels[i + 1] = blue ? (byte)60 : (byte)50;
                    pixels[i + 2] = blue ? (byte)20 : (byte)50;
                    pixels[i + 3] = 255;
                }
            }

            Color? color = CoverColor.Vibrant(BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4));

            Assert.NotNull(color);
            Assert.True(color.Value.B > color.Value.R + 40, $"not blue: {color}");
        }

        [Fact]
        public void No_image_gives_no_colour()
        {
            Assert.Null(CoverColor.Vibrant(null));
        }
    }

    public class LevelTests
    {
        [Fact]
        public void The_vu_level_is_empty_at_the_floor_and_full_at_full_scale()
        {
            Assert.Equal(1f, AudioSpectrumCapture.ToVuLevel(1.0), 3);
            Assert.Equal(0f, AudioSpectrumCapture.ToVuLevel(0.001), 3);
            Assert.Equal(0f, AudioSpectrumCapture.ToVuLevel(0));
        }

        [Fact]
        public void The_vu_level_is_in_decibels()
        {
            // -30 dB is the middle of the 60 dB.
            Assert.Equal(0.5f, AudioSpectrumCapture.ToVuLevel(Math.Pow(10, -30 / 20.0)), 3);
        }

        [Fact]
        public void The_spectrum_level_is_empty_at_the_floor_and_full_at_the_ceiling()
        {
            Assert.Equal(0f, AudioSpectrumCapture.ToLevel(Math.Pow(10, -60 / 20.0)), 3);
            Assert.Equal(1f, AudioSpectrumCapture.ToLevel(Math.Pow(10, -20 / 20.0)), 3);
            Assert.Equal(1f, AudioSpectrumCapture.ToLevel(1.0));
        }
    }

    public class MonitorAdjustmentsTests
    {
        [Theory]
        [InlineData("MediaTrackTxt", "MediaTrackText")]
        [InlineData("mediatracktext", "MediaTrackText")]
        public void A_tag_a_letter_off_is_found(string typed, string expected)
        {
            Assert.Equal(expected, MonitorAdjustments.Closest(typed, KnownMonitorTags.Names));
        }

        [Fact]
        public void A_tag_nothing_like_any_is_left_alone()
        {
            Assert.Null(MonitorAdjustments.Closest("SomethingEntirelyElse", KnownMonitorTags.Names));
        }

        [Theory]
        [InlineData("", "", 0)]
        [InlineData("abc", "abc", 0)]
        [InlineData("abc", "abd", 1)]
        [InlineData("kitten", "sitting", 3)]
        public void The_edit_distance_counts_changes(string a, string b, int distance)
        {
            Assert.Equal(distance, MonitorAdjustments.EditDistance(a, b));
        }
    }

    public class LanguageCultureTests
    {
        [Theory]
        [InlineData("français", "fr")]
        [InlineData("English (United Kingdom)", "en-GB")]
        [InlineData("Deutsch", "de")]
        [InlineData("Norsk (bokmål)", "nb")]
        public void A_language_file_name_is_the_culture_called_that(string language, string culturePrefix)
        {
            Assert.StartsWith(culturePrefix, LanguageCulture.For(language).Name);
        }

        [Fact]
        public void English_has_the_12_hour_clock_and_english_in_the_united_kingdom_the_24_hour_one()
        {
            Assert.Contains("tt", LanguageCulture.For("English").DateTimeFormat.ShortTimePattern);
            Assert.DoesNotContain("tt", LanguageCulture.For("English (United Kingdom)").DateTimeFormat.ShortTimePattern);
        }

        [Theory]
        [InlineData("System")]
        [InlineData("")]
        [InlineData("No Such Language")]
        public void System_or_an_unknown_language_is_the_system_culture(string language)
        {
            Assert.Equal(System.Globalization.CultureInfo.CurrentCulture, LanguageCulture.For(language));
        }
    }

    public class Foobar2000SeekerTests
    {
        [Fact]
        public void A_seek_is_the_longest_steps_first_to_the_nearest_five_seconds()
        {
            Assert.Equal(new[] { "Ahead by 1 minute", "Ahead by 1 minute", "Ahead by 30 seconds", "Ahead by 10 seconds", "Ahead by 5 seconds" },
                Foobar2000Seeker.CommandsFor(TimeSpan.FromSeconds(167)));
            Assert.Equal(new[] { "Back by 10 seconds", "Back by 5 seconds" }, Foobar2000Seeker.CommandsFor(TimeSpan.FromSeconds(-14)));
            Assert.Equal(new[] { "Ahead by 5 seconds" }, Foobar2000Seeker.CommandsFor(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public void Less_than_two_and_a_half_seconds_is_no_seek_and_a_very_long_one_is_limited()
        {
            Assert.Empty(Foobar2000Seeker.CommandsFor(TimeSpan.FromSeconds(2)));
            Assert.Empty(Foobar2000Seeker.CommandsFor(TimeSpan.Zero));
            Assert.Equal(30, Foobar2000Seeker.CommandsFor(TimeSpan.FromHours(3)).Count);
        }

        [Theory]
        [InlineData("foobar2000.exe", true)]
        [InlineData("Foobar2000", true)]
        [InlineData("Spotify.exe", false)]
        [InlineData(null, false)]
        public void The_player_is_found_by_its_name(string aumid, bool expected)
        {
            Assert.Equal(expected, Foobar2000Seeker.IsFoobar2000(aumid));
        }
    }

    public class TitleTimelineTests
    {
        [Theory]
        [InlineData("[1:23 / 3:45] The Beatles - Run for Your Life [foobar2000]", 83, 225)]
        [InlineData("The Beatles - Run for Your Life  [0:07/3:45]", 7, 225)]
        [InlineData("[1:02:03 / 1:30:00] Long mix", 3723, 5400)]
        public void The_position_and_the_length_are_read_from_a_title(string title, int position, int length)
        {
            Assert.True(TitleTimeline.TryParse(title, out TimeSpan p, out TimeSpan l));
            Assert.Equal(TimeSpan.FromSeconds(position), p);
            Assert.Equal(TimeSpan.FromSeconds(length), l);
        }

        [Theory]
        [InlineData("The Beatles - Run for Your Life [foobar2000]")]
        [InlineData("Song 12:34")]
        [InlineData("")]
        [InlineData(null)]
        public void A_title_without_the_times_gives_none(string title)
        {
            Assert.False(TitleTimeline.TryParse(title, out _, out _));
        }
    }

    public class LyricsTests
    {
        private const string Lrc = "[ti:Song]\n[00:03.50] first\n[00:10.00]\n[00:12.25] second\n[00:20.00][01:00.00] chorus\n";

        [Fact]
        public void Lines_are_read_with_their_times_in_order()
        {
            LyricLine[] lines = LyricsClient.Parse(Lrc);

            Assert.NotNull(lines);
            Assert.Equal(5, lines.Length);
            Assert.Equal(TimeSpan.FromSeconds(3.5), lines[0].Time);
            Assert.Equal("first", lines[0].Text);
            Assert.Equal("", lines[1].Text);
            Assert.Equal("chorus", lines[3].Text);
            Assert.Equal(TimeSpan.FromSeconds(60), lines[4].Time);
            Assert.Equal("chorus", lines[4].Text);
        }

        [Fact]
        public void The_result_with_synced_lyrics_and_the_closest_length_is_picked()
        {
            string json = """
                [
                  { "duration": 200, "syncedLyrics": null },
                  { "duration": 250, "syncedLyrics": "[00:01.00] far" },
                  { "duration": 301, "syncedLyrics": "[00:01.00] near" },
                  { "duration": 300, "syncedLyrics": "[00:01.00] exact" }
                ]
                """;

            Assert.Contains("exact", LyricsClient.PickSynced(json, TimeSpan.FromSeconds(300)));
            Assert.Contains("far", LyricsClient.PickSynced(json, TimeSpan.FromSeconds(251)));
            Assert.Null(LyricsClient.PickSynced(json, TimeSpan.FromSeconds(100)));
            Assert.Null(LyricsClient.PickSynced("[]", TimeSpan.FromSeconds(100)));
        }

        [Fact]
        public void Text_with_no_time_stamps_gives_no_lines()
        {
            Assert.Null(LyricsClient.Parse("just words\nand more words"));
        }

        [Fact]
        public void The_index_of_the_line_being_sung_is_minus_one_before_the_first()
        {
            LyricLine[] lines = LyricsClient.Parse(Lrc);

            Assert.Equal(-1, LyricsClient.IndexAt(lines, TimeSpan.FromSeconds(1)));
            Assert.Equal(0, LyricsClient.IndexAt(lines, TimeSpan.FromSeconds(3.5)));
            Assert.Equal(2, LyricsClient.IndexAt(lines, TimeSpan.FromSeconds(13)));
            Assert.Equal(4, LyricsClient.IndexAt(lines, TimeSpan.FromSeconds(300)));
        }

        [Fact]
        public void The_line_being_sung_is_the_last_that_has_started()
        {
            LyricLine[] lines = LyricsClient.Parse(Lrc);

            Assert.Null(LyricsClient.LineAt(lines, TimeSpan.FromSeconds(1)));
            Assert.Equal("first", LyricsClient.LineAt(lines, TimeSpan.FromSeconds(3.5)).Text);
            Assert.Equal("first", LyricsClient.LineAt(lines, TimeSpan.FromSeconds(9.9)).Text);
            Assert.Equal("", LyricsClient.LineAt(lines, TimeSpan.FromSeconds(11)).Text);
            Assert.Equal("chorus", LyricsClient.LineAt(lines, TimeSpan.FromSeconds(300)).Text);
        }
    }

    public class SpicyLyricsTests
    {
        private const string Syllables = """
            {"Body":{"Type":"Syllable","id":"abc","source":"spicy_lyrics","StartTime":0,"EndTime":10,
             "UploadAttribution":{"Uploader":{"id":"1","username":"alice","url":"https://example.com/alice"},"Maker":{"id":"2","username":"bob","url":"http://insecure.example"}},
             "Content":[
              {"Type":"Vocal","Lead":{"StartTime":1.0,"EndTime":2.5,"Syllables":[
                {"Text":"Hel","StartTime":1.0,"EndTime":1.4,"IsPartOfWord":true},
                {"Text":"lo","StartTime":1.4,"EndTime":1.8,"IsPartOfWord":false},
                {"Text":"you","StartTime":1.8,"EndTime":2.5,"IsPartOfWord":false}]},
               "Background":[{"StartTime":2.0,"EndTime":2.5,"Syllables":[{"Text":"oh","StartTime":2.0,"EndTime":2.5,"IsPartOfWord":false}]}]},
              {"Type":"Vocal","Lead":{"StartTime":4.0,"EndTime":5.0,"Syllables":[{"Text":"again","StartTime":4.0,"EndTime":5.0,"IsPartOfWord":false}]}}
             ]},"Status":200,"Type":"object"}
            """;

        [Fact]
        public void Syllables_become_lines_with_the_timing_of_each_word()
        {
            LyricsResult result = SpicyLyricsClient.Parse(Syllables, TimeSpan.FromSeconds(10));

            Assert.NotNull(result);
            Assert.Equal(2, result.Lines.Length);
            LyricLine first = result.Lines[0];
            Assert.Equal(TimeSpan.FromSeconds(1), first.Time);
            Assert.Equal("Hello you (oh)", first.Text);
            Assert.Equal(new[] { "Hel", "lo ", "you ", "(oh) " }, first.Words.Select(word => word.Text).ToArray());
            Assert.Equal(TimeSpan.FromSeconds(1.4), first.Words[1].Start);
            Assert.Equal(TimeSpan.FromSeconds(1.8), first.Words[1].End);
        }

        [Fact]
        public void The_credit_names_the_provider_and_the_uploader_and_keeps_only_a_secure_link()
        {
            LyricsResult result = SpicyLyricsClient.Parse(Syllables, TimeSpan.FromSeconds(10));

            Assert.Equal("Spicy Lyrics · uploaded by alice · made by bob", result.Credit);
            Assert.Equal("https://example.com/alice", result.CreditUrl);
        }

        [Fact]
        public void Times_in_milliseconds_are_told_by_the_length_of_the_track()
        {
            const string json = """{"Body":{"Type":"Line","Content":[{"Type":"Vocal","Text":"one","StartTime":2000,"EndTime":3000},{"Type":"Vocal","Text":"two","StartTime":4000,"EndTime":5000}]}}""";

            Assert.Equal(TimeSpan.FromSeconds(2), SpicyLyricsClient.Parse(json, TimeSpan.FromSeconds(10)).Lines[0].Time);
            Assert.Equal(TimeSpan.FromSeconds(2000), SpicyLyricsClient.Parse(json, TimeSpan.FromSeconds(3000)).Lines[0].Time);
        }

        [Fact]
        public void Line_synced_lyrics_have_no_words()
        {
            const string json = """{"Body":{"Type":"Line","source":"apple_music","Content":[{"Type":"Vocal","Text":"one","StartTime":2,"EndTime":3},{"Type":"Vocal","Text":"two","StartTime":4,"EndTime":5}]}}""";

            LyricsResult result = SpicyLyricsClient.Parse(json);

            Assert.Equal(2, result.Lines.Length);
            Assert.Null(result.Lines[0].Words);
            Assert.Equal("Spicy Lyrics (Apple Music)", result.Credit);
            Assert.Null(result.CreditUrl);
        }

        [Fact]
        public void Lyrics_without_timing_or_unreadable_ones_give_none()
        {
            Assert.Null(SpicyLyricsClient.Parse("""{"Body":{"Type":"Static","Lines":[{"Text":"a"}]}}"""));
            Assert.Null(SpicyLyricsClient.Parse("not json"));
            Assert.Null(SpicyLyricsClient.Parse("""{"Body":{"Type":"Line","Content":[]}}"""));
        }

        [Fact]
        public void The_track_is_the_result_of_the_same_length_and_name()
        {
            const string json = """
                {"tracks":{"items":[
                  {"id":"live","name":"Song","duration_ms":300000},
                  {"id":"remaster","name":"Song","duration_ms":241000},
                  {"id":"other","name":"Other","duration_ms":240000}]}}
                """;

            Assert.Equal("remaster", SpotifyTrackLookup.Pick(json, "song", TimeSpan.FromSeconds(240)));
            Assert.Null(SpotifyTrackLookup.Pick(json, "song", TimeSpan.FromSeconds(100)));
        }
    }
}
