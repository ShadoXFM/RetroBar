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
    // the colour of the cover; the three before it and the three after it are above and below, blurred and dimmer the further they are.
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
        // lyrics lead; and one line gives way to the next by the block fading out and in, sliding a little upward.
        private static readonly TimeSpan LyricsLead = TimeSpan.FromMilliseconds(350);
        private static readonly Duration LyricFadeOut = new(TimeSpan.FromMilliseconds(120));
        private static readonly Duration LyricFadeIn = new(TimeSpan.FromMilliseconds(260));
        private const double LyricSlide = 8;
        private int _lyricFadeToken;

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

        // Room for what is shown whatever the lines are, so the popup keeps its height: a line each, two for the current one (which wraps).
        private void ApplyLyricsHeight()
        {
            double line = SeekArtistText.ActualHeight;
            if (_lyricRows == null || line <= 0)
            {
                return;
            }

            for (int i = 0; i < _lyricRows.Length; i++)
            {
                double height = i == LyricRowsAround ? line * 2 : line;
                _lyricRows[i].MinHeight = height;
                _lyricRows[i].MaxHeight = height;
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
                    SeekLyricsPanel.Visibility = Visibility.Collapsed;
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
            SeekLyricsPanel.Visibility = Visibility.Visible;
            ApplyLyricsHeight();
            _ = LoadLyricsAsync(key, _sessionManager.Artist, _sessionManager.Title, duration);
        }

        private void ResetLyrics()
        {
            _lyricsKey = null;
            _lyrics = null;
            _shownLyricIndex = int.MinValue;
            _lyricFadeToken++;
            SeekLyricsPanel.BeginAnimation(OpacityProperty, null);
            SeekLyricsPanel.RenderTransform = null;
            SeekLyricsPanel.Opacity = 1;
            if (_lyricRows != null)
            {
                foreach (TextBlock row in _lyricRows)
                {
                    row.Text = "";
                }
            }
        }

        private async Task LoadLyricsAsync(string key, string artist, string title, TimeSpan duration)
        {
            LyricLine[] lines = await LyricsClient.GetAsync(artist, title, duration);

            // Back on the UI thread; the track may have changed while it was asked for.
            if (key != _lyricsKey)
            {
                return;
            }

            _lyrics = lines;
            if (lines == null)
            {
                SeekLyricsPanel.Visibility = Visibility.Collapsed;
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

            _shownLyricIndex = index;
            ShowLyrics(index);
        }

        private void FillLyricRows(int index)
        {
            for (int i = 0; i < _lyricRows.Length; i++)
            {
                int line = index + i - LyricRowsAround;
                _lyricRows[i].Text = line >= 0 && line < _lyrics.Length ? _lyrics[line].Text : "";
            }
        }

        // Changes the lines with a fade: what is shown fades out (sliding up a little), then the new lines fade in (sliding up into place).
        private void ShowLyrics(int index)
        {
            int token = ++_lyricFadeToken;
            ApplyLyricsHeight();

            double start = SeekLyricsPanel.Opacity;
            SeekLyricsPanel.BeginAnimation(OpacityProperty, null);

            if (_lyricRows.All(row => string.IsNullOrEmpty(row.Text)))
            {
                FillLyricRows(index);
                FadeInLyrics(token);
                return;
            }

            var slide = new TranslateTransform();
            SeekLyricsPanel.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -LyricSlide, LyricFadeOut));

            var fadeOut = new DoubleAnimation(start, 0, LyricFadeOut) { FillBehavior = FillBehavior.Stop };
            fadeOut.Completed += (s, e) =>
            {
                if (token != _lyricFadeToken)
                {
                    return;
                }

                SeekLyricsPanel.Opacity = 0;
                FillLyricRows(index);
                FadeInLyrics(token);
            };
            SeekLyricsPanel.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void FadeInLyrics(int token)
        {
            var slide = new TranslateTransform(0, LyricSlide);
            SeekLyricsPanel.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(LyricSlide, 0, LyricFadeIn)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });

            SeekLyricsPanel.Opacity = 1;
            var fadeIn = new DoubleAnimation(0, 1, LyricFadeIn)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };
            fadeIn.Completed += (s, e) =>
            {
                // A render transform, even one that does nothing, stops the text being snapped to the screen's pixels.
                if (token == _lyricFadeToken)
                {
                    SeekLyricsPanel.RenderTransform = null;
                }
            };
            SeekLyricsPanel.BeginAnimation(OpacityProperty, fadeIn);
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
                SeekLyricsPanel.Visibility = Visibility.Collapsed;
            }
        }
    }
}
