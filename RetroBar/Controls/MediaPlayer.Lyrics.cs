using System;
using System.Threading.Tasks;
using System.Windows;
using RetroBar.Utilities;

namespace RetroBar.Controls
{
    // The popup's lyrics line: the synced lyrics of the playing track (LyricsClient) with the line being sung shown under the artist.
    public partial class MediaPlayer
    {
        private LyricLine[] _lyrics;
        private string _lyricsKey;
        private LyricLine _shownLyric;

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

            LyricLine line = LyricsClient.LineAt(_lyrics, position);
            if (ReferenceEquals(line, _shownLyric))
            {
                return;
            }

            _shownLyric = line;
            SeekLyricsText.Text = line?.Text ?? "";
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
