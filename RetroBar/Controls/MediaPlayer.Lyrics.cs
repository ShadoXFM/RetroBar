using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using RetroBar.Utilities;

namespace RetroBar.Controls
{
    // The popup's lyrics: the synced lyrics of the playing track (LyricsClient), under the album art. The line being sung is in the middle, in
    // the colour of the cover; the three before it and the three after it are above and below, blurred and dimmer the further they are. When
    // the next line starts, the lines scroll up by one: each takes the look of the place it moves to, and the line that is now the current
    // one fades into the colour.
    public partial class MediaPlayer
    {
        private const int LyricRowsAround = 3;

        private LyricLine[] _lyrics;
        private string _lyricsKey;
        private int _shownLyricIndex = int.MinValue;
        private TextBlock[] _lyricRows;

        // Row 0 is the third line before the current one, row 3 the current one, row 6 the third line after it.
        private static readonly double[] LyricRowOpacity = { 0.25, 0.38, 0.55, 1, 0.55, 0.38, 0.25 };
        private static readonly double[] LyricRowBlur = { 3.5, 2.5, 1.5, 0, 1.5, 2.5, 3.5 };

        // A line is shown a little before its time (the position that is read is a poll behind what is heard), the way the apps' own
        // lyrics lead.
        private static readonly TimeSpan LyricsLead = TimeSpan.FromMilliseconds(350);
        private static readonly Duration LyricScroll = new(TimeSpan.FromMilliseconds(320));
        private static readonly Duration LyricColorFade = new(TimeSpan.FromMilliseconds(420));
        private static readonly TimeSpan LyricsRetry = TimeSpan.FromSeconds(30);
        private int _lyricScrollToken;

        private void EnsureLyricRows()
        {
            if (_lyricRows != null)
            {
                return;
            }

            _lyricRows = new TextBlock[LyricRowsAround * 2 + 1];
            for (int i = 0; i < _lyricRows.Length; i++)
            {
                bool current = i == LyricRowsAround;
                var row = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = current ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    Opacity = LyricRowOpacity[i],
                };
                row.SetResourceReference(StyleProperty, "MediaPlayerTrackTextStyle");
                if (LyricRowBlur[i] > 0)
                {
                    row.Effect = new BlurEffect { Radius = LyricRowBlur[i], RenderingBias = RenderingBias.Performance };
                }

                _lyricRows[i] = row;
                SeekLyricsPanel.Children.Add(row);
            }

            ApplyLyricsColor();
        }

        // The line being sung is in the colour taken from the cover (see UpdateSeekAccent); the text's own colour when there is none.
        private void ApplyLyricsColor()
        {
            if (_lyricRows == null)
            {
                return;
            }

            TextBlock current = _lyricRows[LyricRowsAround];
            if (_seekAccentColor is Color accent)
            {
                var brush = new SolidColorBrush(accent);
                brush.Freeze();
                current.Foreground = brush;
            }
            else
            {
                current.ClearValue(TextBlock.ForegroundProperty);
            }
        }

        // A line each, two at the most for the current one (which wraps).
        private void ApplyLyricsHeight()
        {
            double line = SeekArtistText.ActualHeight;
            if (_lyricRows == null || line <= 0)
            {
                return;
            }

            for (int i = 0; i < _lyricRows.Length; i++)
            {
                bool current = i == LyricRowsAround;
                _lyricRows[i].MinHeight = line;
                _lyricRows[i].MaxHeight = current ? line * 2 : line;
            }
        }

        // The popup keeps its height: when the current line wraps to a second line, the last row (the faintest) gives that room.
        private void ApplyLyricsWrap()
        {
            double line = SeekArtistText.ActualHeight;
            double width = SeekLyricsHost.ActualWidth;
            if (line <= 0 || width <= 0)
            {
                return;
            }

            TextBlock current = _lyricRows[LyricRowsAround];
            current.Measure(new Size(width, double.PositiveInfinity));
            _lyricRows[^1].Visibility = current.DesiredSize.Height > line * 1.5 ? Visibility.Collapsed : Visibility.Visible;
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
            EnsureLyricRows();
            SeekLyricsHost.Visibility = Visibility.Visible;
            ApplyLyricsHeight();
            _ = LoadLyricsAsync(key, _sessionManager.Artist, _sessionManager.Title, duration);
        }

        private void ResetLyrics()
        {
            _lyricsKey = null;
            _lyrics = null;
            _shownLyricIndex = int.MinValue;
            StopLyricAnimations();
            if (_lyricRows != null)
            {
                foreach (TextBlock row in _lyricRows)
                {
                    row.Text = "";
                }
            }
        }

        // Every row back at rest: its own opacity, blur and colour, and the lines in place.
        private void StopLyricAnimations()
        {
            _lyricScrollToken++;
            SeekLyricsPanel.RenderTransform = null;
            if (_lyricRows == null)
            {
                return;
            }

            for (int i = 0; i < _lyricRows.Length; i++)
            {
                TextBlock row = _lyricRows[i];
                row.BeginAnimation(OpacityProperty, null);
                row.Opacity = LyricRowOpacity[i];
                if (LyricRowBlur[i] > 0)
                {
                    var blur = new BlurEffect { Radius = LyricRowBlur[i], RenderingBias = RenderingBias.Performance };
                    row.Effect = blur;
                }
                else
                {
                    row.Effect = null;
                }

                if (i != LyricRowsAround)
                {
                    row.ClearValue(TextBlock.ForegroundProperty);
                }
            }

            ApplyLyricsColor();
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
                UpdateLyricLine(_lastSeekPosition);
            }
        }

        private void UpdateLyricLine(TimeSpan position)
        {
            if (_lyrics == null)
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

        private void FillLyricRows(int index)
        {
            for (int i = 0; i < _lyricRows.Length; i++)
            {
                int line = index + i - LyricRowsAround;
                _lyricRows[i].Text = line >= 0 && line < _lyrics.Length ? _lyrics[line].Text : "";
            }
        }

        // Shows the lines around one: the next line after the one shown scrolls them up by a row, any other change (the first lines, a seek)
        // puts them in place.
        private void ShowLyrics(int previous, int index)
        {
            ApplyLyricsHeight();
            bool scroll = previous != int.MinValue && index == previous + 1 && _lyricRows.Any(row => !string.IsNullOrEmpty(row.Text));
            double distance = scroll ? _lyricRows[LyricRowsAround + 1].ActualHeight : 0;

            StopLyricAnimations();
            FillLyricRows(index);
            ApplyLyricsWrap();

            if (scroll && distance > 0)
            {
                ScrollLyrics(distance);
            }

            FadeLyricAccent(scroll);
        }

        // The rows slide up into place from where they were a row lower, each changing from the look of the row it came from to its own.
        private void ScrollLyrics(double distance)
        {
            int token = _lyricScrollToken;

            var slide = new TranslateTransform(0, distance);
            SeekLyricsPanel.RenderTransform = slide;
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var slideAnimation = new DoubleAnimation(distance, 0, LyricScroll) { EasingFunction = ease };
            slideAnimation.Completed += (s, e) =>
            {
                // A render transform, even one that does nothing, stops the text being snapped to the screen's pixels.
                if (token == _lyricScrollToken)
                {
                    SeekLyricsPanel.RenderTransform = null;
                    foreach (TextBlock row in _lyricRows)
                    {
                        row.BeginAnimation(OpacityProperty, null);
                    }

                    _lyricRows[LyricRowsAround].Effect = null;
                }
            };
            slide.BeginAnimation(TranslateTransform.YProperty, slideAnimation);

            for (int i = 0; i < _lyricRows.Length; i++)
            {
                TextBlock row = _lyricRows[i];
                double fromOpacity = i + 1 < LyricRowOpacity.Length ? LyricRowOpacity[i + 1] : 0;
                row.BeginAnimation(OpacityProperty, new DoubleAnimation(fromOpacity, LyricRowOpacity[i], LyricScroll) { EasingFunction = ease });

                double fromBlur = i + 1 < LyricRowBlur.Length ? LyricRowBlur[i + 1] : LyricRowBlur[i];
                if (i == LyricRowsAround)
                {
                    // The current line has no blur at rest (a blur effect would soften it): only while it comes from the row below.
                    var blur = new BlurEffect { Radius = fromBlur, RenderingBias = RenderingBias.Performance };
                    row.Effect = blur;
                    blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(fromBlur, 0, LyricScroll) { EasingFunction = ease });
                }
                else if (row.Effect is BlurEffect rowBlur)
                {
                    rowBlur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(i == LyricRowsAround - 1 ? 0 : fromBlur, LyricRowBlur[i], LyricScroll) { EasingFunction = ease });
                }
            }
        }

        // The current line takes the colour of the cover by fading into it from the text's own colour; the line that was the current one
        // fades out of it as it moves up.
        private void FadeLyricAccent(bool scrolled)
        {
            if (_seekAccentColor is not Color accent)
            {
                return;
            }

            TextBlock current = _lyricRows[LyricRowsAround];
            if (_lyricRows[LyricRowsAround + 1].Foreground is not SolidColorBrush neutralBrush)
            {
                return;
            }

            Color neutral = neutralBrush.Color;
            var fadeIn = new SolidColorBrush(neutral);
            current.Foreground = fadeIn;
            fadeIn.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(neutral, accent, LyricColorFade));

            if (scrolled)
            {
                TextBlock above = _lyricRows[LyricRowsAround - 1];
                var fadeOut = new SolidColorBrush(accent);
                above.Foreground = fadeOut;
                var animation = new ColorAnimation(accent, neutral, LyricColorFade);
                int token = _lyricScrollToken;
                animation.Completed += (s, e) =>
                {
                    if (token == _lyricScrollToken)
                    {
                        above.ClearValue(TextBlock.ForegroundProperty);
                    }
                };
                fadeOut.BeginAnimation(SolidColorBrush.ColorProperty, animation);
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
