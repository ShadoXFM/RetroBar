using RetroBar.Utilities;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RetroBar.Controls
{
    // In the media popup, the title of a Spotify track opens its album in Spotify, and the artist opens the artist's page: underlined in the
    // colour of the text while the pointer is on them. The album and the artist come from the track's Spotify search result (the credentials
    // that the lyrics use); without them it opens a Spotify search for the title or the artist.
    public partial class MediaPlayer
    {
        private string _linksKey;
        private string _albumUri;
        private string _artistUri;
        private int _linksAttempts;
        private POINT _linkPress;

        private bool IsSpotifySource => _sessionManager != null && IsSpotify(_sessionManager.SourceAppUserModelId);

        private void InitializeSpotifyLinks()
        {
            foreach (TextBlock text in new[] { SeekTitleText, SeekArtistText })
            {
                text.MouseEnter += SeekLink_OnMouseEnter;
                text.MouseLeave += SeekLink_OnMouseLeave;
                text.MouseLeftButtonDown += (s, e) => GetCursorPos(out _linkPress);
                text.MouseLeftButtonUp += SeekLink_OnMouseUp;
            }
        }

        private void SeekLink_OnMouseEnter(object sender, MouseEventArgs e)
        {
            if (!IsSpotifySource || string.IsNullOrEmpty(((TextBlock)sender).Text))
            {
                return;
            }

            var text = (TextBlock)sender;
            // In the colour of the text itself.
            var pen = new Pen(text.Foreground, 1);
            if (pen.CanFreeze)
            {
                pen.Freeze();
            }

            var underline = new TextDecoration(TextDecorationLocation.Underline, pen, 1, TextDecorationUnit.Pixel, TextDecorationUnit.Pixel);
            text.TextDecorations = new TextDecorationCollection { underline };
            text.Cursor = Cursors.Hand;
        }

        private void SeekLink_OnMouseLeave(object sender, MouseEventArgs e)
        {
            var text = (TextBlock)sender;
            text.ClearValue(TextBlock.TextDecorationsProperty);
            text.ClearValue(FrameworkElement.CursorProperty);
        }

        private void SeekLink_OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!IsSpotifySource || string.IsNullOrEmpty(((TextBlock)sender).Text))
            {
                return;
            }

            // A press and release that moved less than a drag is a click (not the end of a drag of the popup).
            GetCursorPos(out POINT now);
            if (Math.Abs(now.X - _linkPress.X) + Math.Abs(now.Y - _linkPress.Y) >= DragThreshold)
            {
                return;
            }

            bool title = sender == SeekTitleText;
            string artist = _sessionManager.Artist ?? "";
            int cut = artist.IndexOfAny(new[] { ',', ';', '/', '&' });
            string firstArtist = (cut > 0 ? artist[..cut] : artist).Trim();
            string uri = title
                ? _albumUri ?? "spotify:search:" + Uri.EscapeDataString($"{_sessionManager.Title} {firstArtist}".Trim())
                : _artistUri ?? "spotify:search:" + Uri.EscapeDataString(firstArtist);

            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                e.Handled = true;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // Spotify is not there to open it.
            }
        }

        // Called when what plays changes: the album and the artist of a Spotify track are looked up (once its length is known).
        private void PrefetchSpotifyLinks()
        {
            if (!IsSpotifySource || string.IsNullOrEmpty(_sessionManager.Title))
            {
                return;
            }

            string artist = _sessionManager.Artist;
            string title = _sessionManager.Title;
            string key = $"{artist}|{title}";
            if (key == _linksKey)
            {
                _linksAttempts = 0;
                return;
            }

            if (SpotifyTrackLookup.Credentials() == null)
            {
                _linksKey = key;
                _albumUri = null;
                _artistUri = null;
                return;
            }

            TimeSpan duration = _sessionManager.GetTimeline().Duration;
            if (duration <= TimeSpan.Zero)
            {
                if (_linksAttempts++ < 20)
                {
                    _ = Task.Delay(500).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(PrefetchSpotifyLinks)));
                }

                return;
            }

            _linksAttempts = 0;
            _linksKey = key;
            _albumUri = null;
            _artistUri = null;
            _ = LoadSpotifyLinksAsync(key, artist, title, duration);
        }

        private async Task LoadSpotifyLinksAsync(string key, string artist, string title, TimeSpan duration)
        {
            try
            {
                SpotifyTrackLookup.SpotifyTrack track = await SpotifyTrackLookup.FindTrackAsync(artist, title, duration);
                if (key != _linksKey)
                {
                    return;
                }

                _albumUri = track?.AlbumId is string album ? "spotify:album:" + album : null;
                _artistUri = track?.ArtistId is string artistId ? "spotify:artist:" + artistId : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                // No links for now: a click opens a search.
                ManagedShell.Common.Logging.ShellLogger.Debug($"MediaPlayer: no Spotify links now: {ex.Message}");
            }
        }
    }
}
