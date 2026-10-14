using System;
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
        private static readonly double[] LyricOpacity = { 1, 0.55, 0.38, 0.25 };
        private static readonly double[] LyricBlur = { 0, 1.5, 2.5, 3.5 };

        // A line is shown a little before its time (the position that is read is a poll behind what is heard), the way the apps' own
        // lyrics lead.
        private static readonly TimeSpan LyricsLead = TimeSpan.FromMilliseconds(350);

        // The scroll and the colour take the same time: the line is in its colour when it is in its place.
        private static readonly Duration LyricScroll = new(TimeSpan.FromMilliseconds(320));
        private static readonly TimeSpan LyricsRetry = TimeSpan.FromSeconds(30);

        // The window fades out at its top and bottom edges, where there are more lines beyond them, so a line is not cut off sharply there:
        // nothing at all in the outermost row (what is cut off there is part of a line, which would be a stray mark), then growing to the
        // full look by the time it is this many rows from the edge. An edge fades by how much there is beyond it (all of it when there
        // are as many rows as that), so as the lyrics get to their end the fade goes away by degrees, with the lines drifting down.
        private const double LyricEdgeClearRows = 1;
        private const double LyricEdgeFadeRows = 2.2;
        private LinearGradientBrush _lyricFadeMask;
        private GradientStop[] _lyricFadeTop;
        private GradientStop[] _lyricFadeBottom;
        private GradientStop _lyricFadeTopInner;
        private GradientStop _lyricFadeBottomInner;
        private double _lyricRowUnit;
        private double _lyricScrollMax;

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

            if (_lyricFadeTop == null)
            {
                _lyricFadeTop = new[] { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, 0.1) };
                _lyricFadeBottom = new[] { new GradientStop(Colors.Black, 0.9), new GradientStop(Colors.Black, 1) };
                _lyricFadeTopInner = new GradientStop(Colors.Black, 0.3);
                _lyricFadeBottomInner = new GradientStop(Colors.Black, 0.7);

                // In the window's own units: a mask relative to the element's bounds would be spread over all the lines the window is
                // scrolled over, which are all inside it.
                _lyricFadeMask = new LinearGradientBrush(new GradientStopCollection
                {
                    _lyricFadeTop[0],
                    _lyricFadeTop[1],
                    _lyricFadeTopInner,
                    _lyricFadeBottomInner,
                    _lyricFadeBottom[0],
                    _lyricFadeBottom[1],
                }, new Point(0, 0), new Point(0, 1))
                {
                    MappingMode = BrushMappingMode.Absolute,
                };
                SeekLyricsHost.OpacityMask = _lyricFadeMask;
            }

            // The rows' share of the window.
            double window = SeekLyricsHost.Height;
            if (window > 0 && _lyricRowUnit > 0)
            {
                _lyricFadeMask.EndPoint = new Point(0, window);
                double clear = Math.Min(0.2, LyricEdgeClearRows * _lyricRowUnit / window);
                double fade = Math.Min(0.45, LyricEdgeFadeRows * _lyricRowUnit / window);
                _lyricFadeTop[1].Offset = clear;
                _lyricFadeTopInner.Offset = fade;
                _lyricFadeBottomInner.Offset = 1 - fade;
                _lyricFadeBottom[0].Offset = 1 - clear;
            }
        }

        // An edge fades by how much there is beyond it.
        private void UpdateLyricEdges(double scroll, bool animate)
        {
            double fade = LyricEdgeFadeRows * (_lyricRowUnit > 0 ? _lyricRowUnit : 15);
            SetLyricEdge(_lyricFadeTop, Math.Clamp(scroll / fade, 0, 1), animate);
            SetLyricEdge(_lyricFadeBottom, Math.Clamp((_lyricScrollMax - scroll) / fade, 0, 1), animate);
        }

        private static void SetLyricEdge(GradientStop[] edge, double strength, bool animate)
        {
            Color target = Color.FromArgb((byte)Math.Round(255 * (1 - strength)), 0, 0, 0);
            foreach (GradientStop stop in edge)
            {
                Color from = stop.Color;
                stop.BeginAnimation(GradientStop.ColorProperty, null);
                stop.Color = target;
                if (animate && from != target)
                {
                    stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(from, target, LyricScroll) { FillBehavior = FillBehavior.Stop });
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
                var row = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, LyricRowGap / 2, 0, LyricRowGap / 2),
                    Opacity = LyricOpacityAt(LyricStyledDistance + 1),

                    // A pause in the singing is a note.
                    Text = string.IsNullOrWhiteSpace(lines[i].Text) ? "♪" : lines[i].Text,
                };
                row.SetResourceReference(StyleProperty, "MediaPlayerTrackTextStyle");
                _lyricRows[i] = row;
                SeekLyricsPanel.Children.Add(row);
            }
        }

        private SolidColorBrush AccentBrush()
        {
            return _seekAccentColor is Color accent ? new SolidColorBrush(accent) : null;
        }

        // The colour the other lines have (the text's own).
        private Color? NeutralColor()
        {
            return _lyricRows is { Length: > 0 } && _lyricRows[0].Foreground is SolidColorBrush brush ? brush.Color : null;
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
        private double LyricScrollFor(int index)
        {
            SeekLyricsPanel.UpdateLayout();
            double window = SeekLyricsHost.Height;
            double maximum = Math.Max(0, SeekLyricsPanel.ActualHeight - window);
            _lyricScrollMax = maximum;
            if (index < 0 || double.IsNaN(window))
            {
                return 0;
            }

            TextBlock row = _lyricRows[index];
            double middle = row.TranslatePoint(new Point(0, row.ActualHeight / 2), SeekLyricsPanel).Y;
            double scroll = Math.Clamp(middle - window / 2, 0, maximum);

            double scale = GetSeekDpi().DpiScaleY;
            return Math.Round(scroll * scale) / scale;
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

            double scroll = LyricScrollFor(index);
            UpdateLyricEdges(scroll, animate);
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
