using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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

        // How far a line is from the current one gives its look; the last look is for every line further than that. The window shows eight
        // lines, and at the first and last lines all of them are on one side of the current one, so the blur goes that far; the lines
        // beyond are not shown (and do not get an effect, which would cost for nothing).
        private const int LyricStyledDistance = 9;
        private static readonly double[] LyricOpacity = { 1, 0.45, 0.3, 0.2 };
        private static readonly double[] LyricBlur = { 0, 2.2, 3, 3.8 };

        // A line is shown a little before its time (the position that is read is a poll behind what is heard), the way the apps' own
        // lyrics lead.
        private static readonly TimeSpan LyricsLead = TimeSpan.FromMilliseconds(350);

        // The scroll and the colour take the same time: the line is in its colour when it is in its place.
        private static readonly Duration LyricScroll = new(TimeSpan.FromMilliseconds(320));
        private static readonly TimeSpan LyricsRetry = TimeSpan.FromSeconds(30);

        // Only whole lines are shown: the lines scroll by a line at a time (the first line shown is at the top of the window), and what is
        // left below the last line that fits (less than a line) is not drawn. A line cut across by the edge was always a stray mark, however
        // it was faded. The mask below is opaque from the top of the first whole line to the bottom of the last, and nothing outside.
        private const double LyricMaskFeather = 1.5;
        private LinearGradientBrush _lyricMask;
        private GradientStop[] _lyricMaskStops;
        private double _lyricRowUnit;

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

            double feather = LyricMaskFeather / window;
            double top = Math.Clamp(visibleTop / window, 0, 1);
            double bottom = Math.Clamp(visibleBottom / window, 0, 1);
            double[] offsets = { 0, Math.Max(0, top - feather), top, bottom, Math.Min(1, bottom + feather), 1 };

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
            for (int i = 0; i < lines.Length; i++)
            {
                int line = i;
                var row = new TextBlock
                {
                    // A click on a line goes to it; the whole of its row takes the click, not only its letters.
                    Background = Brushes.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, LyricRowGap / 2, 0, LyricRowGap / 2),
                    Opacity = LyricOpacityAt(LyricStyledDistance + 1),

                    // A pause in the singing is a note.
                    Text = string.IsNullOrWhiteSpace(lines[i].Text) ? "♪" : lines[i].Text,
                };
                row.SetResourceReference(StyleProperty, "MediaPlayerTrackTextStyle");
                row.MouseLeftButtonDown += (s, e) => GetCursorPos(out _lyricClickStart);
                row.MouseLeftButtonUp += (s, e) => LyricRow_OnClick(line);
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

        private void LyricRow_OnClick(int line)
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

            _ = SeekToAsync(_lyrics[line].Time);
        }

        // The line being sung is in the colour taken from the cover (see UpdateSeekAccent); the text's own colour when there is none.
        private void ApplyLyricsColor()
        {
            if (_lyricRows == null || _shownLyricIndex < 0 || _shownLyricIndex >= _lyricRows.Length)
            {
                return;
            }

            TextBlock current = _lyricRows[_shownLyricIndex];
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
        }

        private async Task LoadLyricsAsync(string key, string artist, string title, TimeSpan duration)
        {
            LyricLine[] lines;
            try
            {
                lines = await LyricsClient.GetAsync(artist, title, duration);
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

            double[] tops = new double[_lyricRows.Length];
            double[] heights = new double[_lyricRows.Length];
            int from = Math.Max(0, index - 14);
            int to = Math.Min(_lyricRows.Length - 1, index + 14);
            for (int i = from; i <= to; i++)
            {
                tops[i] = _lyricRows[i].TranslatePoint(new Point(0, 0), SeekLyricsPanel).Y;
                heights[i] = _lyricRows[i].ActualHeight;
            }

            double middle = tops[index] + heights[index] / 2;
            double scale = GetSeekDpi().DpiScaleY;

            // The whole lines in the window when it is scrolled to `scroll`.
            bool Whole(double scroll, out double first, out double last)
            {
                first = double.MaxValue;
                last = 0;
                for (int i = from; i <= to; i++)
                {
                    double top = tops[i] - scroll;
                    double bottom = top + heights[i];
                    if (top >= -0.5 && bottom <= window + 0.5)
                    {
                        first = Math.Min(first, top);
                        last = Math.Max(last, bottom);
                    }
                }

                return first < double.MaxValue;
            }

            // The scroll puts a line at the top of the window (so that no line is cut there), or the last lines at the bottom: of those, the
            // one with the current line in the middle of the whole lines that are shown.
            var candidates = new List<double> { 0, maximum };
            for (int i = from; i <= index; i++)
            {
                candidates.Add(Math.Min(tops[i], maximum));
            }

            double best = 0;
            double bestError = double.MaxValue;
            foreach (double candidate in candidates.Select(c => Math.Round(c * scale) / scale))
            {
                if (!Whole(candidate, out double first, out double last))
                {
                    continue;
                }

                double top = tops[index] - candidate;
                if (top < first - 0.5 || top + heights[index] > last + 0.5)
                {
                    continue;
                }

                double error = Math.Abs(middle - candidate - (first + last) / 2);
                if (error < bestError - 0.01)
                {
                    bestError = error;
                    best = candidate;
                }
            }

            if (bestError == double.MaxValue)
            {
                best = Math.Round(Math.Clamp(middle - window / 2, 0, maximum) * scale) / scale;
            }

            if (Whole(best, out double visibleFirst, out double visibleLast))
            {
                visibleTop = Math.Max(0, visibleFirst - 1);
                visibleBottom = Math.Min(window, visibleLast + 1);
            }

            return best;
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

            // The colour: the current line fades into the cover's colour, the one that was current out of it.
            Color? neutral = NeutralColor();
            if (i == index && AccentBrush() is SolidColorBrush accent && neutral is Color rest)
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
