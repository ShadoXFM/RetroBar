using System;
using System.Linq;
using System.Collections.Generic;
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
        private const double LyricWindowLines = 8;
        private const double LyricRowGap = 2;

        // The room at each side of the lines, so they do not run to the edges of the row of controls.
        private const double LyricSidePadding = 8;

        // How far a line is from the current one gives its look; the last look is for every line further than that. The window shows eight
        // lines, and at the first and last lines all of them are on one side of the current one, so the blur goes that far; the lines
        // beyond are not shown (and do not get an effect, which would cost for nothing).
        private const int LyricStyledDistance = 9;
        private static readonly double[] LyricOpacity = { 1, 0.45, 0.3, 0.2 };
        private static readonly double[] LyricBlur = { 0, 2.2, 3, 3.8 };

        // The lines and the words come this much before their time (the lyrics are ahead of what is heard): a setting for every song,
        // which the media popup's right-click menu moves.
        private static TimeSpan LyricsLead => TimeSpan.FromMilliseconds(Settings.Instance.LyricsLeadMs);

        // The scroll and the colour take the same time: the line is in its colour when it is in its place.
        private static readonly Duration LyricScroll = new(TimeSpan.FromMilliseconds(320));
        private static readonly TimeSpan LyricsRetry = TimeSpan.FromSeconds(30);

        // The window fades out at its top and bottom edges, so a line cut off there reads as scrolled out of view, not as a stray mark.
        // There is no fade at the top while the first lines are shown, or at the bottom while the last ones are.
        private const double LyricMaskFeather = 14;
        private LinearGradientBrush _lyricMask;
        private GradientStop[] _lyricMaskStops;
        private double _lyricRowUnit;

        // Lyrics synced by word: the current line's words fill with the colour of the cover as they are sung, left to right over the time of
        // each, and the one being sung is a pixel higher (the timer follows the position between the popup's polls, which are too far apart for
        // words). Background vocals are smaller and dimmer.
        private const double LyricWordSoftEdge = 0.12;
        private const double LyricBackgroundScale = 0.85;
        private static readonly Duration LyricLiftTime = new(TimeSpan.FromMilliseconds(110));
        private Run[][] _lyricRuns;
        private int[][] _lyricRunOffsets;
        private Run[] _activeRuns;
        private LyricWord[] _activeWords;
        private int[] _activeOffsets;
        private TextBlock _activeBlock;
        private byte[] _activeStates;
        private LinearGradientBrush[] _activeSweeps;
        private TextEffect _wordLift;
        private TranslateTransform _wordLiftMove;
        private int _liftedWord = -1;
        private DispatcherTimer _lyricWordTimer;

        private LyricLine[] _lyrics;
        private string _lyricsKey;
        private int _shownLyricIndex = int.MinValue;
        private TextBlock[] _lyricRows;
        private int _lyricScrollToken;

        private static double LyricOpacityAt(int distance) => LyricOpacity[Math.Min(distance, LyricOpacity.Length - 1)];

        private static double LyricBlurAt(int distance) => distance > LyricStyledDistance ? 0 : LyricBlur[Math.Min(distance, LyricBlur.Length - 1)];

        // The window's height: a number of lines of the popup's text.
        private void ApplyLyricsHeight()
        {
            double line = SeekArtistText.ActualHeight;
            if (line > 0)
            {
                SeekLyricsHost.Height = Math.Round((line + LyricRowGap) * LyricWindowLines);
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
            _lyricRuns = new Run[lines.Length][];
            _lyricRunOffsets = new int[lines.Length][];

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
                    var offsets = new int[words.Length];
                    int offset = 0;
                    for (int j = 0; j < words.Length; j++)
                    {
                        runs[j] = new Run(j == words.Length - 1 ? words[j].Text.TrimEnd() : words[j].Text);
                        if (words[j].IsBackground)
                        {
                            runs[j].FontSize = SeekArtistText.FontSize * LyricBackgroundScale;
                        }

                        offsets[j] = offset;
                        offset += runs[j].Text.Length;
                        row.Inlines.Add(runs[j]);
                    }

                    _lyricRuns[i] = runs;
                    _lyricRunOffsets[i] = offsets;
                }
                else
                {
                    // A pause in the singing is a note.
                    row.Text = string.IsNullOrWhiteSpace(lines[i].Text) ? "♪" : lines[i].Text;
                }

                row.MouseLeftButtonDown += (s, e) => GetCursorPos(out _lyricClickStart);
                row.MouseLeftButtonUp += (s, e) => LyricRow_OnClick(line, e.GetPosition(row));
                _lyricRows[i] = row;
                SeekLyricsPanel.Children.Add(row);
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

        // The colour the other lines have (the text's own).
        private Color? NeutralColor()
        {
            return _lyricRows is { Length: > 0 } && _lyricRows[0].Foreground is SolidColorBrush brush ? brush.Color : null;
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

        // The line being sung is in the colour taken from the cover (see UpdateSeekAccent); the text's own colour when there is none.
        private void ApplyLyricsColor()
        {
            if (_lyricRows == null || _shownLyricIndex < 0 || _shownLyricIndex >= _lyricRows.Length)
            {
                return;
            }

            TextBlock current = _lyricRows[_shownLyricIndex];
            if (_lyricRuns?[_shownLyricIndex] != null)
            {
                // Its words take the colour as they are sung (see BeginLyricWords).
                BeginLyricWords(_shownLyricIndex);
                return;
            }

            if (AccentBrush() is SolidColorBrush brush)
            {
                brush.Freeze();
                current.Foreground = brush;
            }
            else
            {
                current.ClearValue(TextBlock.ForegroundProperty);
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
            _lyricScrollToken++;
            SeekLyricsPanel.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetTop(SeekLyricsPanel, 0);
            SeekLyricsPanel.Children.Clear();
            _lyricRows = null;
            _lyricRuns = null;
            StopLyricWords();
        }

        private async Task LoadLyricsAsync(string key, string artist, string title, TimeSpan duration)
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

            LyricLine[] lines;
            try
            {
                lines = spicy?.Lines ?? await LyricsClient.GetAsync(artist, title, duration);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                // The service is down, or there is no connection: nothing is shown, and it is asked again in a while.
                ManagedShell.Common.Logging.ShellLogger.Debug($"MediaPlayer: lyrics not available now: {ex.Message}");
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

            int index = LyricsClient.IndexAt(_lyrics, position + LyricsLead);
            if (index == _shownLyricIndex)
            {
                // The popup was closed and opened again, which stopped the words.
                if (_activeRuns == null && index >= 0 && _lyricRuns?[index] != null)
                {
                    BeginLyricWords(index);
                }

                return;
            }

            int previous = _shownLyricIndex;
            _shownLyricIndex = index;
            ShowLyrics(previous, index);
        }

        // How far the lines are scrolled (up) to show the one: its middle in the middle of the window, as far as there are lines to fill
        // the window - the first lines are at the top and the last at the bottom. A whole number of the screen's pixels, so the text is sharp.
        private double LyricScrollFor(int index, out double visibleTop, out double visibleBottom)
        {
            SeekLyricsPanel.UpdateLayout();
            double window = SeekLyricsHost.Height;
            double maximum = Math.Max(0, SeekLyricsPanel.ActualHeight - window);
            visibleTop = 0;
            visibleBottom = window;
            if (index < 0 || double.IsNaN(window))
            {
                return 0;
            }

            // The current line in the middle of the window (or as near as the first and last lines allow). The line that doesn't fit whole
            // at an edge is shown cut off and faded out (see UpdateLyricMask).
            TextBlock row = _lyricRows[index];
            double middle = row.TranslatePoint(new Point(0, row.ActualHeight / 2), SeekLyricsPanel).Y;
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

        // Puts the look of its distance from the current line on a row, from the look of its distance from the previous one.
        private void StyleLyricRow(int i, int index, int previous, bool animate, int token)
        {
            TextBlock row = _lyricRows[i];
            int distance = Math.Abs(i - index);
            double opacity = LyricOpacityAt(distance);
            double blur = LyricBlurAt(distance);
            int oldDistance = previous == int.MinValue ? distance : Math.Abs(i - previous);
            double oldOpacity = LyricOpacityAt(oldDistance);
            double oldBlur = LyricBlurAt(oldDistance);

            row.BeginAnimation(OpacityProperty, null);
            row.Opacity = opacity;

            if (blur > 0 || oldBlur > 0)

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
                        // A blur effect, even of nothing, softens the text: the current line has none at rest.
                        radius.Completed += (s, e) =>
                        {
                            if (token == _lyricScrollToken && Math.Abs(i - _shownLyricIndex) == 0)
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
            else
            {
                row.Effect = null;
            }

            // The colour: the current line fades into the cover's colour, the one that was current out of it. (A line synced by word has its
            // words lit one by one instead, see BeginLyricWords; the one that was current, all lit, fades out whole.)
            Color? neutral = NeutralColor();
            Run[] runs = _lyricRuns?[i];
            if (runs != null)
            {
                foreach (Run run in runs)
                {
                    run.ClearValue(TextElement.ForegroundProperty);
                }
            }

            if (i == index && runs != null)
            {
                row.ClearValue(TextBlock.ForegroundProperty);
            }
            else if (i == index && AccentBrush() is SolidColorBrush accent && neutral is Color rest)
            {
                if (animate)
                {
                    var fade = new SolidColorBrush(rest);
                    row.Foreground = fade;
                    fade.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(rest, accent.Color, LyricScroll));
                }
                else
                {
                    accent.Freeze();
                    row.Foreground = accent;
                }
            }
            else if (i == previous && animate && AccentBrush() is SolidColorBrush before && neutral is Color back)
            {
                var fade = new SolidColorBrush(before.Color);
                row.Foreground = fade;
                var animation = new ColorAnimation(before.Color, back, LyricScroll);
                animation.Completed += (s, e) =>
                {
                    if (token == _lyricScrollToken && i != _shownLyricIndex)
                    {
                        row.ClearValue(TextBlock.ForegroundProperty);
                    }
                };
                fade.BeginAnimation(SolidColorBrush.ColorProperty, animation);
            }
            else
            {
                row.ClearValue(TextBlock.ForegroundProperty);
            }
        }

        // Shows the lines around one: the lines that move scroll to their places, and every row takes the look of its new distance from the
        // current line.
        private void ShowLyrics(int previous, int index)
        {
            ApplyLyricsHeight();
            int token = ++_lyricScrollToken;
            bool animate = previous != int.MinValue;

            // The rows that are, or were, near enough to the current line to look different; the others keep the dimmest look.
            int first = Math.Max(0, Math.Min(index, animate ? previous : index) - LyricStyledDistance - 1);
            int last = Math.Min(_lyricRows.Length - 1, Math.Max(index, animate ? previous : index) + LyricStyledDistance + 1);
            for (int i = first; i <= last; i++)
            {
                StyleLyricRow(i, index, previous, animate, token);
            }

            StopLyricWords();
            if (index >= 0 && _lyricRuns?[index] != null)
            {
                BeginLyricWords(index);
            }

            double scroll = LyricScrollFor(index, out double visibleTop, out double visibleBottom);
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

        // The words of the current line start dim, and light up (fade into the colour of the cover) when they are sung.
        private void BeginLyricWords(int line)
        {
            StopLyricWords();
            Run[] runs = _lyricRuns[line];
            LyricWord[] words = _lyrics[line].Words;
            if (runs.Length != words.Length)
            {
                return;
            }

            _activeRuns = runs;
            _activeWords = words;
            _activeOffsets = _lyricRunOffsets[line];
            _activeBlock = _lyricRows[line];
            _activeStates = new byte[runs.Length];
            _activeSweeps = new LinearGradientBrush[runs.Length];
            _liftedWord = -1;
            for (int i = 0; i < runs.Length; i++)
            {
                runs[i].Foreground = new SolidColorBrush(UnsungColor(words[i].IsBackground));
            }

            // The word being sung is a pixel higher: a text effect over its letters, which moves as the singing does.
            _wordLiftMove = new TranslateTransform();
            _wordLift = new TextEffect { Transform = _wordLiftMove, PositionStart = 0, PositionCount = 0 };
            _activeBlock.TextEffects.Add(_wordLift);

            _lyricWordTimer ??= new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _lyricWordTimer.Tick -= LyricWordTimer_OnTick;
            _lyricWordTimer.Tick += LyricWordTimer_OnTick;
            _lyricWordTimer.Start();
            LyricWordTimer_OnTick(null, EventArgs.Empty);
        }

        private void StopLyricWords()
        {
            _lyricWordTimer?.Stop();
            _activeBlock?.TextEffects.Clear();
            _activeBlock = null;
            _wordLift = null;
            _wordLiftMove = null;
            _activeRuns = null;
            _activeWords = null;
            _activeOffsets = null;
            _activeStates = null;
            _activeSweeps = null;
        }

        // What is not sung yet is the text's own colour a little faded, what has been the cover's colour: the background vocals both dimmer.
        private Color UnsungColor(bool background)
        {
            Color rest = NeutralColor() ?? Colors.White;
            return Color.FromArgb((byte)(background ? 90 : 150), rest.R, rest.G, rest.B);
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
            if (_activeRuns == null || _activeWords == null || !SeekPopup.IsOpen)
            {
                _lyricWordTimer?.Stop();
                return;
            }

            TimeSpan position = EstimatedSeekPosition() + LyricsLead;
            int lifted = -1;
            for (int i = 0; i < _activeRuns.Length; i++)
            {
                LyricWord word = _activeWords[i];
                bool background = word.IsBackground;

                // Not sung yet (0), being sung (1), or sung (2).
                byte state = position >= word.End ? (byte)2 : position >= word.Start ? (byte)1 : (byte)0;
                if (state == 1)
                {
                    double progress = word.End > word.Start ? (position - word.Start).TotalSeconds / (word.End - word.Start).TotalSeconds : 1;
                    progress = Math.Clamp(progress, 0, 1);
                    Color sung = SungColor(background);
                    Color unsung = UnsungColor(background);
                    LinearGradientBrush sweep = _activeSweeps[i] ??= new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0.5),
                        EndPoint = new Point(1, 0.5),
                        GradientStops = new GradientStopCollection { new GradientStop(), new GradientStop(), new GradientStop(), new GradientStop() },
                    };

                    // The part sung is filled up to the progress, with a soft edge before the part that is not.
                    sweep.GradientStops[0].Color = sung;
                    sweep.GradientStops[0].Offset = 0;
                    sweep.GradientStops[1].Color = sung;
                    sweep.GradientStops[1].Offset = progress;
                    sweep.GradientStops[2].Color = unsung;
                    sweep.GradientStops[2].Offset = Math.Min(1, progress + LyricWordSoftEdge);
                    sweep.GradientStops[3].Color = unsung;
                    sweep.GradientStops[3].Offset = 1;
                    if (_activeStates[i] != 1)
                    {
                        _activeRuns[i].Foreground = sweep;
                    }

                    if (!background)
                    {
                        lifted = i;
                    }
                }
                else if (_activeStates[i] != state)
                {
                    _activeRuns[i].Foreground = new SolidColorBrush(state == 2 ? SungColor(background) : UnsungColor(background));
                }

                _activeStates[i] = state;
            }

            if (lifted != _liftedWord)
            {
                _liftedWord = lifted;
                if (lifted >= 0)
                {
                    _wordLift.PositionStart = _activeOffsets[lifted];
                    _wordLift.PositionCount = _activeRuns[lifted].Text.Length;
                    _wordLiftMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -1 / GetSeekDpi().DpiScaleY, LyricLiftTime));
                }
                else
                {
                    _wordLiftMove.BeginAnimation(TranslateTransform.YProperty, null);
                    _wordLiftMove.Y = 0;
                    _wordLift.PositionCount = 0;
                }
            }
        }

        // The setting (Properties window, right-click menu): off takes the lyrics away; on asks for those of what is playing.
        private void ApplyLyricsSetting()
        {
            ResetLyrics();

            if (Settings.Instance.ShowMediaPopupLyrics)
            {
                EnsureLyrics(_lastSeekDuration);
            }
            else
            {
                SeekLyricsHost.Visibility = Visibility.Collapsed;
            }
        }
    }
}
