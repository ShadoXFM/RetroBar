using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using RetroBar.Utilities;

namespace RetroBar.Controls
{
    // The popup's lyrics line: the synced lyrics of the playing track (LyricsClient) with the line being sung shown under the artist.
    public partial class MediaPlayer
    {
        private LyricLine[] _lyrics;
        private string _lyricsKey;
        private LyricLine _shownLyric;

        // A line is shown a little before its time (the position that is read is a poll behind what is heard), the way the apps' own
        // lyrics lead; and one line gives way to the next by fading out and in.
        private static readonly TimeSpan LyricsLead = TimeSpan.FromMilliseconds(350);
        private static readonly Duration LyricFadeOut = new(TimeSpan.FromMilliseconds(120));
        private static readonly Duration LyricFadeIn = new(TimeSpan.FromMilliseconds(260));
        private const double LyricsOpacity = 0.8;
        private int _lyricFadeToken;

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
                    _lyricsKey = null;
                    _lyrics = null;
                    _shownLyric = null;
                    SeekLyricsText.Text = "";
                    SeekLyricsText.Visibility = Visibility.Collapsed;
                }

                return;
            }

            string key = $"{_sessionManager.Artist}|{_sessionManager.Title}";
            if (key == _lyricsKey || duration <= TimeSpan.Zero || string.IsNullOrEmpty(_sessionManager.Title))
            {
                return;
            }

            _lyricsKey = key;
            _lyrics = null;
            _shownLyric = null;
            SeekLyricsText.Text = "";
            SeekLyricsText.Visibility = Visibility.Visible;
            ApplyLyricsHeight();
            _ = LoadLyricsAsync(key, _sessionManager.Artist, _sessionManager.Title, duration);
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
                SeekLyricsText.Visibility = Visibility.Collapsed;
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

            LyricLine line = LyricsClient.LineAt(_lyrics, position + LyricsLead);
            if (ReferenceEquals(line, _shownLyric))
            {
                return;
            }

            _shownLyric = line;
            ShowLyric(line?.Text ?? "");
        }

        // Room for two lines, whatever the line, so the popup keeps its height.
        private void ApplyLyricsHeight()
        {
            double height = SeekArtistText.ActualHeight * 2;
            if (height > 0)
            {
                SeekLyricsText.MinHeight = height;
                SeekLyricsText.MaxHeight = height;
            }
        }

        // Changes the line with a fade: the one shown fades out, then the new one fades in (an empty line, between verses, only fades out).
        private void ShowLyric(string text)
        {
            int token = ++_lyricFadeToken;
            ApplyLyricsHeight();

            double start = SeekLyricsText.Opacity;
            SeekLyricsText.BeginAnimation(OpacityProperty, null);

            if (string.IsNullOrEmpty(SeekLyricsText.Text))
            {
                SeekLyricsText.Text = text;
                FadeInLyric(token);
                return;
            }

            var fadeOut = new DoubleAnimation(start, 0, LyricFadeOut) { FillBehavior = FillBehavior.Stop };
            fadeOut.Completed += (s, e) =>
            {
                if (token != _lyricFadeToken)
                {
                    return;
                }

                SeekLyricsText.Opacity = 0;
                SeekLyricsText.Text = text;
                FadeInLyric(token);
            };
            SeekLyricsText.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void FadeInLyric(int token)
        {
            if (string.IsNullOrEmpty(SeekLyricsText.Text))
            {
                SeekLyricsText.Opacity = 0;
                return;
            }

            SeekLyricsText.Opacity = LyricsOpacity;
            var fadeIn = new DoubleAnimation(0, LyricsOpacity, LyricFadeIn)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };
            SeekLyricsText.BeginAnimation(OpacityProperty, fadeIn);
        }

        // The setting (Properties window): off takes the line away; on asks for the lyrics of what is playing.
        private void ApplyLyricsSetting()
        {
            _lyricsKey = null;
            _lyrics = null;
            _shownLyric = null;
            SeekLyricsText.Text = "";

            if (Settings.Instance.ShowMediaPopupLyrics)
            {
                EnsureLyrics(_lastSeekDuration);
            }
            else
            {
                SeekLyricsText.Visibility = Visibility.Collapsed;
            }
        }
    }
}
