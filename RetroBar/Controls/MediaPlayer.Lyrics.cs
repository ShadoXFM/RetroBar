using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using RetroBar.Utilities;

namespace RetroBar.Controls
{
    // The popup's lyrics: the synced lyrics of the playing track (LyricsClient), under the album art, every line in full (wrapped), in a
    // window of a fixed height that scrolls to keep the line being sung in the middle (or, at the first and last lines, to the top and the
    // bottom, so there are no gaps). The line being sung is in the colour of the cover; the others are blurred and dimmer the further they
    // are. When the next line starts, the lines scroll and the new line fades into the colour as it gets to its place.
    public partial class MediaPlayer
    {
        // How many lines the window shows, and the room between them.
        private const double LyricRowGap = 2;

        // The room at each side of the lines, so they do not run to the edges of the row of controls.
        private const double LyricSidePadding = 8;

        // How far a line is from the current one gives its look; the last look is for every line further than that. The window shows eight
        // lines, and at the first and last lines all of them are on one side of the current one, so the blur goes that far; the lines
        // beyond are not shown (and do not get an effect, which would cost for nothing).
        private static int LyricStyledDistance => Settings.Instance.LyricLines + 1;
        private static readonly double[] LyricOpacity = { 1, 0.45, 0.3, 0.2 };
        private static readonly double[] LyricBlur = { 0, 2.2, 3, 3.8 };

        // The lines and the words come this much before their time (the lyrics are ahead of what is heard): a setting for every song,
        // which the media popup's right-click menu moves.
        private static TimeSpan LyricsLead => TimeSpan.FromMilliseconds(Settings.Instance.LyricsLeadMs);

        // The scroll and the colour take the same time: the line is in its colour when it is in its place.
        private static readonly Duration LyricScroll = new(TimeSpan.FromMilliseconds(320));
        // A line that loses focus (or gains it) goes between its sharp look and its blurred one by a cross-fade of the two, in a straight line:
        // a blur effect whose radius is animated has no look between "none" (below a radius of 1), a brighter one (up to 2) and the blur, which
        // showed as a flash and an abrupt cut. The cover's colour leaves the line in the same time.
        private static readonly Duration LyricColorFadeOut = new(TimeSpan.FromMilliseconds(600));
        private static readonly TimeSpan LyricsRetry = TimeSpan.FromSeconds(30);

        // The window fades out at its top and bottom edges, so a line cut off there reads as scrolled out of view, not as a stray mark.
        // There is no fade at the top while the first lines are shown, or at the bottom while the last ones are.
        private const double LyricMaskFeather = 14;
        private LinearGradientBrush _lyricMask;
        private GradientStop[] _lyricMaskStops;
        private double _lyricRowUnit;

        // Lyrics synced by word: the current line's words fill with the colour of the cover as they are sung, left to right over the time of
        // each (the timer follows the position between the popup's polls, which are too far apart for words). Background vocals are smaller
        // and dimmer.
        private const double LyricWordSoftEdge = 0.12;
        private const double LyricBackgroundScale = 0.85;

        // The word being sung is brighter, up to this much of the way to white, which comes quickly as it starts and goes slowly after it.
        private const double LyricGlowAmount = 0.5;
        private const double LyricGlowRiseSeconds = 0.15;
        private const double LyricGlowFallSeconds = 0.5;

        // And it has a halo: a blurred copy of it behind the line, in the colour of the cover, as strong as the word is bright.
        private const double LyricHaloBlur = 7;
        private const double LyricHaloOpacity = 0.9;
        private const double LyricHaloTrailShare = 0.35;

        // Both follow the letters: the brightest at the one being sung, and less so behind it over this much of the word.
        private const double LyricGlowTrail = 0.45;
        private Run[][] _lyricRuns;

        // A line being sung by word: its words, and how far each has got (not sung yet 0, being sung 1, sung 2).
        private sealed class ActiveWordLine
        {
            public Run[] Runs;
            public LyricWord[] Words;
            public TextBlock Block;
            public byte[] States;
            public LinearGradientBrush[] Sweeps;
            public double[] Glows;
            public bool[] Bright;
            public LinearGradientBrush[] HaloSweeps;
            public long LastTicks;
            public TextBlock Halo;
            public Run[] HaloRuns;
        }

        private readonly List<ActiveWordLine> _activeLines = new();

        private LyricLine[] _lyrics;
        private string _lyricsKey;
        private int _shownLyricIndex = int.MinValue;

        // The lines in focus: the one that started last, and the lines before it that are still being sung (they overlap it).
        private int[] _focusLines = Array.Empty<int>();
        private TextBlock[] _lyricRows;
        private Grid[] _lyricHosts;
        private TextBlock[] _lyricGhosts;
        private int _lyricScrollToken;

        private static double LyricOpacityAt(int distance) => LyricOpacity[Math.Min(distance, LyricOpacity.Length - 1)];

        private static double LyricBlurAt(int distance) => distance > LyricStyledDistance ? 0 : LyricBlur[Math.Min(distance, LyricBlur.Length - 1)];

        // The window's height: a number of lines of the popup's text.
        private void ApplyLyricsHeight()
        {
            double line = SeekArtistText.ActualHeight;
            if (line > 0)
            {
                SeekLyricsHost.Height = Math.Round((line + LyricRowGap) * Settings.Instance.LyricLines);
                _lyricRowUnit = line + LyricRowGap;
            }

            if (_lyricMask == null)
            {
                // In the window's own units: a mask relative to the element's bounds would be spread over all the lines the window is
                // scrolled over, which are all inside it.
                _lyricMaskStops = new[]
                {
                    new GradientStop(Colors.Transparent, 0),
                    new GradientStop(Colors.Transparent, 0),
                    new GradientStop(Colors.Black, 0.0001),
                    new GradientStop(Colors.Black, 0.9999),
                    new GradientStop(Colors.Transparent, 1),
                    new GradientStop(Colors.Transparent, 1),
                };
                _lyricMask = new LinearGradientBrush(new GradientStopCollection(_lyricMaskStops), new Point(0, 0), new Point(0, 1))
                {
                    MappingMode = BrushMappingMode.Absolute,
                };
                SeekLyricsHost.OpacityMask = _lyricMask;
            }

            if (SeekLyricsHost.Height > 0)
            {
                _lyricMask.EndPoint = new Point(0, SeekLyricsHost.Height);
            }
        }

        // The mask shows what is between the top of the first whole line and the bottom of the last.
        private void UpdateLyricMask(double visibleTop, double visibleBottom, bool animate)
        {
            double window = SeekLyricsHost.Height;
            if (window <= 0)
            {
                return;
            }

            double feather = Math.Min(LyricMaskFeather, window / 4) / window;
            double top = visibleTop / window;
            double bottom = visibleBottom / window;
            double[] offsets = { top, top, top + feather, bottom - feather, bottom, bottom };
            for (int i = 0; i < offsets.Length; i++)
            {
                offsets[i] = Math.Clamp(offsets[i], 0, 1);
            }

            for (int i = 0; i < _lyricMaskStops.Length; i++)
            {
                GradientStop stop = _lyricMaskStops[i];
                double from = stop.Offset;
                stop.BeginAnimation(GradientStop.OffsetProperty, null);
                stop.Offset = offsets[i];
                if (animate && Math.Abs(from - offsets[i]) > 1e-6)
                {
                    stop.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(from, offsets[i], LyricScroll) { FillBehavior = FillBehavior.Stop });
                }
            }
        }

        // One row per line of the lyrics, all in the dimmest look until the first line is shown.
        private void BuildLyricRows(LyricLine[] lines)
        {
            SeekLyricsPanel.Children.Clear();
            _lyricRows = new TextBlock[lines.Length];
            _lyricHosts = new Grid[lines.Length];
            _lyricGhosts = new TextBlock[lines.Length];
            _lyricRuns = new Run[lines.Length][];

            // A duet has its second singer's lines on the right and the first one's on the left; the other lyrics are centered.
            bool duet = lines.Any(line => line.OppositeAligned);
            for (int i = 0; i < lines.Length; i++)
            {
                int line = i;
                bool pause = string.IsNullOrWhiteSpace(lines[i].Text);
                var row = new TextBlock
                {
                    // A click on a line goes to it; the whole of its row takes the click, not only its letters.
                    Background = Brushes.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    TextAlignment = !duet || pause ? TextAlignment.Center : lines[i].OppositeAligned ? TextAlignment.Right : TextAlignment.Left,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(LyricSidePadding, LyricRowGap / 2, LyricSidePadding, LyricRowGap / 2),
                    Opacity = LyricOpacityAt(LyricStyledDistance + 1),

                };
                row.SetResourceReference(StyleProperty, "MediaPlayerTrackTextStyle");

                if (lines[i].Words is { Length: > 0 } words && !string.IsNullOrWhiteSpace(lines[i].Text))
                {
                    var runs = new Run[words.Length];
                    for (int j = 0; j < words.Length; j++)
                    {
                        runs[j] = new Run(j == words.Length - 1 ? words[j].Text.TrimEnd() : words[j].Text);
                        if (words[j].IsBackground)
                        {
                            runs[j].FontSize = SeekArtistText.FontSize * LyricBackgroundScale;
                        }

                        row.Inlines.Add(runs[j]);
                    }

                    _lyricRuns[i] = runs;
                }
                else
                {
                    // A pause in the singing is a note.
                    row.Text = string.IsNullOrWhiteSpace(lines[i].Text) ? "♪" : lines[i].Text;
                }

                row.MouseLeftButtonDown += (s, e) => GetCursorPos(out _lyricClickStart);
                row.MouseLeftButtonUp += (s, e) => LyricRow_OnClick(line, e.GetPosition(row));
                _lyricRows[i] = row;
                var host = new Grid();
                host.Children.Add(row);
                _lyricHosts[i] = host;
                SeekLyricsPanel.Children.Add(host);
            }
        }

        // The cover's colour, a little lighter than the one the meter and the spectrum have, to read on the popup's dark ground at this size.
        private const double LyricAccentLighten = 0.3;

        private SolidColorBrush AccentBrush()
        {
            if (_seekAccentColor is not Color accent)
            {
                return null;
            }

            static byte Lighter(byte channel) => (byte)Math.Round(channel + (255 - channel) * LyricAccentLighten);
            return new SolidColorBrush(Color.FromRgb(Lighter(accent.R), Lighter(accent.G), Lighter(accent.B)));
        }

        // The colour the other lines have (the text's own): read once, from the first row before any line has been styled - the first row's
        // colour changes as it is in focus or fading out, and the words of a line took that (the cover's colour, dimmed) for the colour of
        // what is not sung yet.
        private Color? _lyricNeutral;

        private Color? NeutralColor()
        {
            if (_lyricNeutral == null && _lyricRows is { Length: > 0 } && _lyricRows[0].Foreground is SolidColorBrush brush)
            {
                _lyricNeutral = brush.Color;
            }

            return _lyricNeutral;
        }

        // A press and release that moved less than a drag is a click (not the end of a drag of the popup).
        private POINT _lyricClickStart;

        private void LyricRow_OnClick(int line, Point point)
        {
            if (!CanSeekSource || _lyrics == null || line >= _lyrics.Length)
            {
                return;
            }

            GetCursorPos(out POINT now);
            if (Math.Abs(now.X - _lyricClickStart.X) + Math.Abs(now.Y - _lyricClickStart.Y) >= DragThreshold)
            {
                return;
            }

            // On a word of a line synced by word: to that word.
            TimeSpan target = _lyrics[line].Time;
            if (_lyricRuns?[line] is Run[] runs && _lyrics[line].Words is LyricWord[] words && runs.Length == words.Length)
            {
                TextPointer pointer = _lyricRows[line].GetPositionFromPoint(point, true);
                int word = pointer?.Parent is Run run ? Array.IndexOf(runs, run) : -1;
                if (word >= 0)
                {
                    target = words[word].Start;
                }
            }

            _ = SeekToAsync(target);
        }

        // The lines being sung are in the colour taken from the cover (see UpdateSeekAccent); the text's own colour when there is none.
        private void ApplyLyricsColor()
        {
            if (_lyricRows == null)
            {
                return;
            }

            bool byWord = false;
            foreach (int line in _focusLines)
            {
                if (line < 0 || line >= _lyricRows.Length)
                {
                    continue;
                }

                if (_lyricRuns?[line] != null)
                {
                    // Its words take the colour as they are sung (see BeginLyricWords).
                    byWord = true;
                }
                else if (AccentBrush() is SolidColorBrush brush)
                {
                    brush.Freeze();
                    _lyricRows[line].Foreground = brush;
                }
                else
                {
                    _lyricRows[line].ClearValue(TextBlock.ForegroundProperty);
                }
            }

            if (byWord)
            {
                BeginLyricWords();
            }
        }

        // Asks for the lyrics of the current track once (when its length is known, which the search needs) while the popup is open. Only
        // for Spotify's tracks.
        private void EnsureLyrics(TimeSpan duration)
        {
            if (!Settings.Instance.ShowMediaPopupLyrics || _sessionManager == null || !SeekPopup.IsOpen)
            {
                return;
            }

            if (!IsSpotify(_sessionManager.SourceAppUserModelId))
            {
                if (_lyricsKey != null)
                {
                    ResetLyrics();
                    SeekLyricsHost.Visibility = Visibility.Collapsed;
                }

                return;
            }

            string key = $"{_sessionManager.Artist}|{_sessionManager.Title}";
            if (key == _lyricsKey || duration <= TimeSpan.Zero || string.IsNullOrEmpty(_sessionManager.Title))
            {
                return;
            }

            ResetLyrics();
            _lyricsKey = key;
            SeekLyricsHost.Visibility = Visibility.Visible;
            ApplyLyricsHeight();
            _ = LoadLyricsAsync(key, _sessionManager.Artist, _sessionManager.Title, duration);
        }

        private void ResetLyrics()
        {
            _lyricsKey = null;
            _lyrics = null;
            _shownLyricIndex = int.MinValue;
            _focusLines = Array.Empty<int>();
            _lyricScrollToken++;
            SeekLyricsPanel.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetTop(SeekLyricsPanel, 0);
            SeekLyricsPanel.Children.Clear();
            _lyricRows = null;
            _lyricHosts = null;
            _lyricGhosts = null;
            _lyricRuns = null;
            _lyricNeutral = null;
            StopLyricWords();
        }

        // What asking for the lyrics of a track gave: the lines (none if the track has none), or that the asking failed.
        private sealed record LyricsFetch(LyricLine[] Lines, bool Failed);

        private string _lyricsFetchKey;
        private Task<LyricsFetch> _lyricsFetch;

        // The lyrics are asked for as soon as a track plays (see UpdateFromSession), not when the popup is first opened for it, which left a
        // moment with nothing or the last track's lyrics. What is asked for is kept for the track, whether the popup is open or not.
        private Task<LyricsFetch> GetLyricsFetch(string key, string artist, string title, TimeSpan duration)
        {
            bool failed = _lyricsFetch is { IsCompleted: true } done && done.Result.Failed;
            if (key != _lyricsFetchKey || _lyricsFetch == null || failed)
            {
                _lyricsFetchKey = key;
                _lyricsFetch = FetchLyricsAsync(artist, title, duration);
            }

            return _lyricsFetch;
        }

        private int _lyricsPrefetchAttempts;

        // Called when what plays changes. The search needs the length of the track, which the source may give a moment after its title.
        private void PrefetchLyrics()
        {
            if (!Settings.Instance.ShowMediaPopupLyrics || _sessionManager == null || !_sessionManager.HasSession
                || string.IsNullOrEmpty(_sessionManager.Title) || !IsSpotify(_sessionManager.SourceAppUserModelId))
            {
                return;
            }

            string artist = _sessionManager.Artist;
            string title = _sessionManager.Title;
            string key = $"{artist}|{title}";
            if (key == _lyricsFetchKey)
            {
                _lyricsPrefetchAttempts = 0;
                return;
            }

            TimeSpan duration = _sessionManager.GetTimeline().Duration;
            if (duration <= TimeSpan.Zero)
            {
                if (_lyricsPrefetchAttempts++ < 20)
                {
                    _ = Task.Delay(500).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(PrefetchLyrics)));
                }

                return;
            }

            _lyricsPrefetchAttempts = 0;

            // The last track's lyrics are not shown for this one, if the popup is opened before these arrive.
            if (_lyricsKey != null && _lyricsKey != key)
            {
                ResetLyrics();
            }

            _ = GetLyricsFetch(key, artist, title, duration);
        }

        private static async Task<LyricsFetch> FetchLyricsAsync(string artist, string title, TimeSpan duration)
        {
            LyricsResult spicy = null;
            if (SpicyLyricsClient.IsConfigured)
            {
                try
                {
                    spicy = await SpicyLyricsClient.GetAsync(artist, title, duration);
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
                {
                    // Not there for now: the other source gives the lyrics (line by line).
                    ManagedShell.Common.Logging.ShellLogger.Debug($"MediaPlayer: Spicy Lyrics not available now: {ex.Message}");
                }
            }

            try
            {
                return new LyricsFetch(spicy?.Lines ?? await LyricsClient.GetAsync(artist, title, duration), false);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                // The service is down, or there is no connection: nothing is shown, and it is asked again in a while.
                ManagedShell.Common.Logging.ShellLogger.Debug($"MediaPlayer: lyrics not available now: {ex.Message}");
                return new LyricsFetch(null, true);
            }
        }

        private async Task LoadLyricsAsync(string key, string artist, string title, TimeSpan duration)
        {
            LyricsFetch fetch = await GetLyricsFetch(key, artist, title, duration);
            if (fetch.Failed)
            {
                if (key == _lyricsKey)
                {
                    SeekLyricsHost.Visibility = Visibility.Collapsed;
                }

                await Task.Delay(LyricsRetry);
                if (key == _lyricsKey)
                {
                    _lyricsKey = null;
                }

                return;
            }

            LyricLine[] lines = fetch.Lines;

            // Back on the UI thread; the track may have changed while it was asked for.
            if (key != _lyricsKey)
            {
                return;
            }

            _lyrics = lines;
            if (lines == null)
            {
                SeekLyricsHost.Visibility = Visibility.Collapsed;
            }
            else
            {
                BuildLyricRows(lines);
                UpdateLyricLine(_lastSeekPosition);
            }
        }

        private void UpdateLyricLine(TimeSpan position)
        {
            if (_lyrics == null || _lyricRows == null)
            {
                return;
            }

            TimeSpan at = position + LyricsLead;
            int index = LyricsClient.IndexAt(_lyrics, at);
            int[] focus = LyricsClient.FocusSet(_lyrics, at, index);
            if (index == _shownLyricIndex && focus.SequenceEqual(_focusLines))
            {
                // The popup was closed and opened again, which stopped the words.
                if (_activeLines.Count == 0 && focus.Any(line => _lyricRuns?[line] != null))
                {
                    BeginLyricWords();
                }

                return;
            }

            int previous = _shownLyricIndex;
            int[] before = _focusLines;
            _shownLyricIndex = index;
            _focusLines = focus;
            ShowLyrics(previous, before, index);
        }

        // How far the lines are scrolled (up) to show the lines from one to another: their middle in the middle of the window, as far as there
        // are lines to fill the window - the first lines are at the top and the last at the bottom. A whole number of the screen's pixels, so the text
        // is sharp.
        private double LyricScrollFor(int firstFocus, int lastFocus, out double visibleTop, out double visibleBottom)
        {
            SeekLyricsPanel.UpdateLayout();
            double window = SeekLyricsHost.Height;
            double maximum = Math.Max(0, SeekLyricsPanel.ActualHeight - window);
            visibleTop = 0;
            visibleBottom = window;
            if (firstFocus < 0 || double.IsNaN(window))
            {
                return 0;
            }

            // The lines in focus in the middle of the window (or as near as the first and last lines allow). The line that doesn't fit whole
            // at an edge is shown cut off and faded out (see UpdateLyricMask).
            TextBlock first = _lyricRows[firstFocus];
            TextBlock last = _lyricRows[lastFocus];
            double top = first.TranslatePoint(new Point(0, 0), SeekLyricsPanel).Y;
            double bottom = last.TranslatePoint(new Point(0, last.ActualHeight), SeekLyricsPanel).Y;
            double middle = (top + bottom) / 2;
            double scale = GetSeekDpi().DpiScaleY;
            double scroll = Math.Clamp(middle - window / 2, 0, maximum);

            // The first and the last line are never left cut off or faded at the edge: near either end of the lyrics the window goes all the
            // way to it (the current line is a little off the middle for that).
            if (maximum - scroll < _lyricRows[^1].ActualHeight + LyricRowGap)
            {
                scroll = maximum;
            }
            else if (scroll < _lyricRows[0].ActualHeight + LyricRowGap)
            {
                scroll = 0;
            }

            scroll = Math.Round(scroll * scale) / scale;
            visibleTop = scroll <= 0.5 ? -LyricMaskFeather : 0;
            visibleBottom = scroll >= maximum - 0.5 ? window + LyricMaskFeather : window;
            return scroll;
        }

        // How far a row is from the lines in focus (from the nearest of them): its look, the same above and below, getting dimmer and more
        // blurred with the distance.
        private static int DistanceToFocus(int[] focus, int row, int fallback)
        {
            if (focus == null || focus.Length == 0)
            {
                return Math.Abs(row - fallback);
            }

            int nearest = int.MaxValue;
            foreach (int line in focus)
            {
                nearest = Math.Min(nearest, Math.Abs(row - line));
            }

            return nearest;
        }

        private bool IsInFocus(int row) => Array.IndexOf(_focusLines, row) >= 0;

        // A copy of a row laid over it (the same text, in one colour, blurred), for the cross-fade between its sharp look and its blurred one.
        private TextBlock AddLyricGhost(int i, Color color, double blur, double opacity)
        {
            TextBlock row = _lyricRows[i];
            var ghost = new TextBlock
            {
                TextAlignment = row.TextAlignment,
                TextWrapping = row.TextWrapping,
                HorizontalAlignment = row.HorizontalAlignment,
                Margin = row.Margin,
                IsHitTestVisible = false,
                Foreground = new SolidColorBrush(color),
                Opacity = opacity,
            };
            ghost.SetResourceReference(StyleProperty, "MediaPlayerTrackTextStyle");
            foreach (Run run in row.Inlines.OfType<Run>())
            {
                ghost.Inlines.Add(new Run(run.Text) { FontSize = run.FontSize });
            }

            if (blur > 0)
            {
                ghost.Effect = new BlurEffect { Radius = blur, RenderingBias = RenderingBias.Performance };
            }

            _lyricHosts[i].Children.Add(ghost);
            _lyricGhosts[i] = ghost;
            return ghost;
        }

        private void RemoveLyricGhost(int i)
        {
            if (_lyricGhosts?[i] is TextBlock ghost)
            {
                _lyricHosts[i].Children.Remove(ghost);
                _lyricGhosts[i] = null;
            }
        }

        // Puts the look of its distance from the lines in focus on a row, from the look of its distance from the ones that were.
        private void StyleLyricRow(int i, int index, int previous, int[] before, bool animate, int token)
        {
            TextBlock row = _lyricRows[i];
            RemoveLyricGhost(i);
            bool nowFocus = IsInFocus(i);
            bool wasFocus = before != null && Array.IndexOf(before, i) >= 0;
            int distance = DistanceToFocus(_focusLines, i, index);
            double opacity = LyricOpacityAt(distance);
            double blur = LyricBlurAt(distance);
            int oldDistance = previous == int.MinValue ? distance : DistanceToFocus(before, i, previous);
            double oldOpacity = LyricOpacityAt(oldDistance);
            double oldBlur = LyricBlurAt(oldDistance);

            Color? neutral = NeutralColor();
            SolidColorBrush accentNow = AccentBrush();
            Run[] runs = _lyricRuns?[i];

            // A line that loses focus goes to its blurred look, and one that gains it to its sharp look, by a cross-fade (see above).
            bool fadingOut = animate && wasFocus && !nowFocus && blur > 0 && accentNow != null && neutral != null;
            bool fadingIn = animate && nowFocus && !wasFocus && oldBlur > 0 && blur == 0 && neutral != null;

            // What the text will look like when it has its new brightness: its color over the popup's ground.
            Color ground = (TryFindResource("ButtonFace") as SolidColorBrush)?.Color ?? Color.FromRgb(18, 16, 20);
            Color rest = neutral ?? Colors.White;
            Color dimmed = Color.FromRgb(
                (byte)Math.Round(ground.R + (rest.R - ground.R) * opacity),
                (byte)Math.Round(ground.G + (rest.G - ground.G) * opacity),
                (byte)Math.Round(ground.B + (rest.B - ground.B) * opacity));

            row.BeginAnimation(OpacityProperty, null);
            row.Opacity = opacity;
            row.Effect = null;

            if (fadingOut)
            {
                // The sharp text fades out as it changes to the dimmed gray, and a blurred copy of the gray fades in over it. At the end the row
                // takes the look the copy has (its blur, its brightness), and the copy goes.
                TextBlock ghost = AddLyricGhost(i, rest, blur, 0);
                var ghostIn = new DoubleAnimation(0, opacity, LyricColorFadeOut);
                ghostIn.Completed += (s, e) =>
                {
                    if (token == _lyricScrollToken && !IsInFocus(i) && _lyricGhosts?[i] == ghost)
                    {
                        RemoveLyricGhost(i);
                        row.BeginAnimation(OpacityProperty, null);
                        row.Opacity = opacity;
                        row.Effect = new BlurEffect { Radius = blur, RenderingBias = RenderingBias.Performance };
                        row.ClearValue(TextBlock.ForegroundProperty);
                        if (runs != null)
                        {
                            foreach (Run run in runs)
                            {
                                run.ClearValue(TextElement.ForegroundProperty);
                            }
                        }
                    }
                };
                ghost.BeginAnimation(OpacityProperty, ghostIn);
                row.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, LyricColorFadeOut));
            }
            else if (fadingIn)
            {
                // The blurred copy of what the row was fades out, and the sharp text fades in.
                TextBlock ghost = AddLyricGhost(i, rest, oldBlur, oldOpacity);
                var ghostOut = new DoubleAnimation(oldOpacity, 0, LyricScroll);
                ghostOut.Completed += (s, e) =>
                {
                    if (_lyricGhosts?[i] == ghost)
                    {
                        RemoveLyricGhost(i);
                    }
                };
                ghost.BeginAnimation(OpacityProperty, ghostOut);
                row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, opacity, LyricScroll));
            }
            else if (blur > 0 || oldBlur > 0)
            {
                bool moves = animate && (oldBlur != blur || oldOpacity != opacity);
                var effect = new BlurEffect { Radius = moves ? oldBlur : blur, RenderingBias = RenderingBias.Performance };
                row.Effect = effect;
                if (moves)
                {
                    var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                    row.BeginAnimation(OpacityProperty, new DoubleAnimation(oldOpacity, opacity, LyricScroll) { EasingFunction = ease });
                    var radius = new DoubleAnimation(oldBlur, blur, LyricScroll) { EasingFunction = ease };
                    if (blur == 0)
                    {
                        // A blur effect, even of nothing, softens the text: the lines in focus have none at rest.
                        radius.Completed += (s, e) =>
                        {
                            if (token == _lyricScrollToken && IsInFocus(i))
                            {
                                row.Effect = null;
                            }
                        };
                    }

                    effect.BeginAnimation(BlurEffect.RadiusProperty, radius);
                }
                else if (blur == 0)
                {
                    row.Effect = null;
                }
            }

            // The colour: a line in focus fades into the cover's colour, one that was out of it. (A line synced by word has its words lit
            // one by one instead, see BeginLyricWords; one that loses focus fades from the look each of its words has at that moment.)
            ActiveWordLine fadingWords = fadingOut && runs != null ? _activeLines.FirstOrDefault(active => ReferenceEquals(active.Block, row)) : null;
            if (runs != null && fadingWords == null)
            {
                foreach (Run run in runs)
                {
                    run.ClearValue(TextElement.ForegroundProperty);
                }
            }

            if (nowFocus && runs != null)
            {
                row.ClearValue(TextBlock.ForegroundProperty);
            }
            else if (nowFocus && accentNow != null && neutral is Color rowRest)
            {
                if (animate && !wasFocus)
                {
                    var fade = new SolidColorBrush(rowRest);
                    row.Foreground = fade;
                    fade.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(rowRest, accentNow.Color, LyricScroll));
                }
                else
                {
                    accentNow.Freeze();
                    row.Foreground = accentNow;
                }
            }
            else if (fadingOut)
            {
                if (fadingWords != null)
                {
                    row.ClearValue(TextBlock.ForegroundProperty);
                    TimeSpan now = EstimatedSeekPosition() + LyricsLead;
                    for (int w = 0; w < runs.Length; w++)
                    {
                        Color from = WordColor(fadingWords, w, now);
                        var wordBrush = new SolidColorBrush(from);
                        runs[w].Foreground = wordBrush;
                        wordBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, dimmed, LyricColorFadeOut));
                    }
                }
                else
                {
                    var fade = new SolidColorBrush(accentNow.Color);
                    row.Foreground = fade;
                    fade.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(accentNow.Color, dimmed, LyricColorFadeOut));
                }
            }
            else
            {
                row.ClearValue(TextBlock.ForegroundProperty);
            }
        }

        // Shows the lines around the ones in focus: the lines that move scroll to their places, and every row takes the look of its new
        // distance from the lines in focus.
        private void ShowLyrics(int previous, int[] before, int index)
        {
            ApplyLyricsHeight();
            int token = ++_lyricScrollToken;
            bool animate = previous != int.MinValue;

            // The rows that are, or were, near enough to the lines in focus to look different; the others keep the dimmest look.
            int low = index;
            int high = index;
            foreach (int line in _focusLines.Concat(animate ? before : Array.Empty<int>()).Concat(animate ? new[] { previous } : Array.Empty<int>()))
            {
                low = Math.Min(low, line);
                high = Math.Max(high, line);
            }

            int first = Math.Max(0, low - LyricStyledDistance - 1);
            int last = Math.Min(_lyricRows.Length - 1, high + LyricStyledDistance + 1);
            for (int i = first; i <= last; i++)
            {
                StyleLyricRow(i, index, previous, before, animate, token);
            }

            BeginLyricWords();

            // Centered on the line that started last (the next one), not on the middle of the lines that overlap.
            double scroll = _focusLines.Length > 0
                ? LyricScrollFor(_focusLines[^1], _focusLines[^1], out double visibleTop, out double visibleBottom)
                : LyricScrollFor(-1, -1, out visibleTop, out visibleBottom);
            UpdateLyricMask(visibleTop, visibleBottom, animate);
            double top = -scroll;
            SeekLyricsPanel.BeginAnimation(Canvas.TopProperty, null);
            double from = Canvas.GetTop(SeekLyricsPanel);
            Canvas.SetTop(SeekLyricsPanel, top);
            if (animate && from != top)
            {
                SeekLyricsPanel.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(from, top, LyricScroll)
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop,
                });
            }
        }

        // The words of the lines in focus start dim, and fill with the colour of the cover as they are sung.
        private void BeginLyricWords()
        {
            StopLyricWords();
            foreach (int line in _focusLines)
            {
                Run[] runs = _lyricRuns?[line];
                LyricWord[] words = _lyrics?[line].Words;
                if (runs == null || words == null || runs.Length != words.Length)
                {
                    continue;
                }

                var active = new ActiveWordLine
                {
                    Runs = runs,
                    Words = words,
                    Block = _lyricRows[line],
                    States = new byte[runs.Length],
                    Sweeps = new LinearGradientBrush[runs.Length],
                    Glows = new double[runs.Length],
                    Bright = new bool[runs.Length],
                    HaloSweeps = new LinearGradientBrush[runs.Length],
                    LastTicks = Stopwatch.GetTimestamp(),
                };
                for (int i = 0; i < runs.Length; i++)
                {
                    runs[i].Foreground = new SolidColorBrush(UnsungColor(words[i].IsBackground));
                }

                (active.Halo, active.HaloRuns) = AddLyricHalo(line);
                _activeLines.Add(active);
            }

            if (_activeLines.Count == 0)
            {
                return;
            }

            // Once for every frame drawn (a timer of 33 ms made the words move at 30 a second).
            CompositionTarget.Rendering -= LyricWordTimer_OnTick;
            CompositionTarget.Rendering += LyricWordTimer_OnTick;
            LyricWordTimer_OnTick(null, EventArgs.Empty);
        }

        private void StopLyricWords()
        {
            CompositionTarget.Rendering -= LyricWordTimer_OnTick;
            foreach (ActiveWordLine active in _activeLines)
            {
                // The halo of the word that was just sung goes out as the word's brightness does, not at once.
                TextBlock halo = active.Halo;
                if (halo?.Parent is Panel panel)
                {
                    if (SeekPopup.IsOpen)
                    {
                        var fade = new DoubleAnimation(halo.Opacity, 0, new Duration(TimeSpan.FromSeconds(LyricGlowFallSeconds)));
                        fade.Completed += (s, e) => panel.Children.Remove(halo);
                        halo.BeginAnimation(OpacityProperty, fade);
                    }
                    else
                    {
                        panel.Children.Remove(halo);
                    }
                }
            }

            _activeLines.Clear();
        }

        // A copy of a row behind it, blurred, whose words are clear until they are sung (the tick gives them the colour of the cover).
        private (TextBlock, Run[]) AddLyricHalo(int i)
        {
            TextBlock row = _lyricRows[i];
            var halo = new TextBlock
            {
                TextAlignment = row.TextAlignment,
                TextWrapping = row.TextWrapping,
                HorizontalAlignment = row.HorizontalAlignment,
                Margin = row.Margin,
                IsHitTestVisible = false,
                Effect = new BlurEffect { Radius = LyricHaloBlur, RenderingBias = RenderingBias.Performance },
            };
            halo.SetResourceReference(StyleProperty, "MediaPlayerTrackTextStyle");
            var runs = new List<Run>();
            foreach (Run run in row.Inlines.OfType<Run>())
            {
                var copy = new Run(run.Text) { FontSize = run.FontSize, Foreground = new SolidColorBrush(Colors.Transparent) };
                halo.Inlines.Add(copy);
                runs.Add(copy);
            }

            _lyricHosts[i].Children.Insert(0, halo);
            return (halo, runs.ToArray());
        }

        // The colour a word of a line being sung has at a moment: not sung yet, sung, or part of the way (the colour the sweep has there).
        private Color WordColor(ActiveWordLine active, int word, TimeSpan position)
        {
            LyricWord w = active.Words[word];
            Color unsung = UnsungColor(w.IsBackground);
            Color sung = SungColor(w.IsBackground);
            if (position >= w.End)
            {
                return sung;
            }

            if (position < w.Start || w.End <= w.Start)
            {
                return position < w.Start ? unsung : sung;
            }

            double progress = Math.Clamp((position - w.Start).TotalSeconds / (w.End - w.Start).TotalSeconds, 0, 1);
            return Color.FromArgb(
                (byte)Math.Round(unsung.A + (sung.A - unsung.A) * progress),
                (byte)Math.Round(unsung.R + (sung.R - unsung.R) * progress),
                (byte)Math.Round(unsung.G + (sung.G - unsung.G) * progress),
                (byte)Math.Round(unsung.B + (sung.B - unsung.B) * progress));
        }

        // What is not sung yet is the text's own colour a little faded, what has been the cover's colour: the background vocals both dimmer.
        private Color UnsungColor(bool background)
        {
            Color rest = NeutralColor() ?? Colors.White;
            return Color.FromArgb((byte)(background ? 90 : 150), rest.R, rest.G, rest.B);
        }

        private static LinearGradientBrush NewWordGradient()
        {
            return new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                GradientStops = new GradientStopCollection { new GradientStop(), new GradientStop(), new GradientStop(), new GradientStop(), new GradientStop() },
            };
        }

        // The five stops of a gradient over a word: from its start to where the trail begins, the peak at the letter being sung, and beyond it
        // (a soft edge, then to the end) what is not sung yet.
        private static void SetWordGradient(LinearGradientBrush brush, Color start, Color trailStart, Color peak, Color ahead, Color end, double trail, double progress, double edge)
        {
            GradientStopCollection stops = brush.GradientStops;
            stops[0].Color = start;
            stops[0].Offset = 0;
            stops[1].Color = trailStart;
            stops[1].Offset = trail;
            stops[2].Color = peak;
            stops[2].Offset = progress;
            stops[3].Color = ahead;
            stops[3].Offset = edge;
            stops[4].Color = end;
            stops[4].Offset = 1;
        }

        // A colour part of the way to white (the transparency it has is kept).
        private static Color Brightened(Color color, double glow)
        {
            double amount = glow * LyricGlowAmount;
            return Color.FromArgb(
                color.A,
                (byte)Math.Round(color.R + (255 - color.R) * amount),
                (byte)Math.Round(color.G + (255 - color.G) * amount),
                (byte)Math.Round(color.B + (255 - color.B) * amount));
        }

        private Color SungColor(bool background)
        {
            Color lit = AccentBrush()?.Color ?? NeutralColor() ?? Colors.White;
            return Color.FromArgb((byte)(background ? 200 : 255), lit.R, lit.G, lit.B);
        }

        // Where the song is now: the last position read, and the time since if it is playing (the polls are a quarter of a second apart).
        private TimeSpan EstimatedSeekPosition()
        {
            TimeSpan position = _lastSeekPosition;
            if (_sessionManager?.PlaybackState == MediaPlaybackState.Playing)
            {
                position += DateTime.UtcNow - _lastSeekPositionAt;
            }

            return _lastSeekDuration > TimeSpan.Zero && position > _lastSeekDuration ? _lastSeekDuration : position;
        }

        private void LyricWordTimer_OnTick(object sender, EventArgs e)
        {
            if (_activeLines.Count == 0 || !SeekPopup.IsOpen)
            {
                // Stopped for good, not only unhooked: with its lines kept, the popup opened again found words that were never moved on
                // (UpdateLyricLine starts them again only when there are none).
                StopLyricWords();
                return;
            }

            TimeSpan position = EstimatedSeekPosition() + LyricsLead;
            long now = Stopwatch.GetTimestamp();
            foreach (ActiveWordLine active in _activeLines)
            {
                double elapsed = (now - active.LastTicks) / (double)Stopwatch.Frequency;
                active.LastTicks = now;
                for (int i = 0; i < active.Runs.Length; i++)
                {
                    LyricWord word = active.Words[i];
                    bool background = word.IsBackground;

                    // Not sung yet (0), being sung (1), or sung (2).
                    byte state = position >= word.End ? (byte)2 : position >= word.Start ? (byte)1 : (byte)0;

                    // How bright the word is: up while it is sung, back down after.
                    double glow = active.Glows[i];
                    glow = state == 1 ? Math.Min(1, glow + elapsed / LyricGlowRiseSeconds) : Math.Max(0, glow - elapsed / LyricGlowFallSeconds);
                    active.Glows[i] = glow;

                    // A word that is being sung, or whose brightness has not gone yet, is drawn by a gradient across its letters: the part
                    // sung in the colour of the cover, the brightest at the letter that is being sung and less so behind it, as is the halo.
                    bool bright = state == 1 || (state == 2 && glow > 0);
                    if (bright)
                    {
                        double progress = 1;
                        if (state == 1)
                        {
                            progress = word.End > word.Start ? (position - word.Start).TotalSeconds / (word.End - word.Start).TotalSeconds : 1;
                            progress = Math.Clamp(progress, 0, 1);
                        }

                        Color baseColor = SungColor(background);
                        Color peak = Brightened(baseColor, glow);
                        Color unsung = UnsungColor(background);
                        double trail = Math.Max(0, progress - LyricGlowTrail);
                        double edge = Math.Min(1, progress + LyricWordSoftEdge);

                        LinearGradientBrush sweep = active.Sweeps[i] ??= NewWordGradient();
                        SetWordGradient(sweep, baseColor, baseColor, peak, unsung, unsung, trail, progress, edge);
                        if (!ReferenceEquals(active.Runs[i].Foreground, sweep))
                        {
                            active.Runs[i].Foreground = sweep;
                        }

                        Color haloColor = baseColor;
                        byte haloAlpha = (byte)Math.Round(255 * glow * LyricHaloOpacity * (background ? 0.6 : 1));
                        Color halo = Color.FromArgb(haloAlpha, haloColor.R, haloColor.G, haloColor.B);
                        Color haloFaint = Color.FromArgb((byte)(haloAlpha * LyricHaloTrailShare), haloColor.R, haloColor.G, haloColor.B);
                        Color haloNone = Color.FromArgb(0, haloColor.R, haloColor.G, haloColor.B);
                        LinearGradientBrush haloSweep = active.HaloSweeps[i] ??= NewWordGradient();
                        SetWordGradient(haloSweep, haloFaint, haloFaint, halo, haloNone, haloNone, trail, progress, edge);
                        if (!ReferenceEquals(active.HaloRuns[i].Foreground, haloSweep))
                        {
                            active.HaloRuns[i].Foreground = haloSweep;
                        }

                        active.Bright[i] = true;
                    }
                    else if (active.Bright[i] || active.States[i] != state)
                    {
                        active.Runs[i].Foreground = new SolidColorBrush(state == 2 ? SungColor(background) : UnsungColor(background));
                        active.HaloRuns[i].Foreground = Brushes.Transparent;
                        active.Bright[i] = false;
                    }

                    active.States[i] = state;
                }
            }
        }

        // The setting (Properties window, right-click menu): off takes the lyrics away; on asks for those of what is playing.
        private void ApplyLyricsSetting()
        {
            ResetLyrics();

            if (Settings.Instance.ShowMediaPopupLyrics)
            {
                PrefetchLyrics();
                EnsureLyrics(_lastSeekDuration);
            }
            else
            {
                SeekLyricsHost.Visibility = Visibility.Collapsed;
            }
        }
    }
}
