using ManagedShell.Common.Logging;
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace RetroBar.Utilities
{
    public enum MediaPlaybackState
    {
        None,
        Paused,
        Playing
    }

    /// <summary>
    /// Wraps the Windows System Media Transport Controls (SMTC) session APIs.
    /// All Windows.Media.Control references live in this file only, so callers
    /// can new() this up inside a try/catch and safely no-op on systems where
    /// the WinRT media session APIs aren't available (pre-1809).
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    internal class MediaSessionManager : IDisposable
    {
        public event EventHandler MediaChanged;

        private GlobalSystemMediaTransportControlsSessionManager _manager;
        private GlobalSystemMediaTransportControlsSession _session;
        private bool _disposed;

        public bool HasSession => _session != null;
        public string Title { get; private set; } = "";
        public string Artist { get; private set; } = "";
        public MediaPlaybackState PlaybackState { get; private set; } = MediaPlaybackState.None;

        /// <summary>
        /// Album/track artwork for the current session, or null if the app doesn't
        /// publish one. Always frozen, so it's safe to hand straight to the UI thread.
        /// </summary>
        public ImageSource Thumbnail { get; private set; }

        public async Task InitializeAsync()
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;

            AttachSession(_manager.GetCurrentSession());
        }

        private void Manager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            AttachSession(sender.GetCurrentSession());
        }

        private void AttachSession(GlobalSystemMediaTransportControlsSession session)
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                _session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            }

            _session = session;

            if (_session != null)
            {
                _session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                _session.PlaybackInfoChanged += Session_PlaybackInfoChanged;

                RefreshPlaybackState();
                _ = RefreshPropertiesAsync();
            }
            else
            {
                Title = "";
                Artist = "";
                Thumbnail = null;
                PlaybackState = MediaPlaybackState.None;
                MediaChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private async void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            await RefreshPropertiesAsync();
        }

        private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            RefreshPlaybackState();
        }

        private async Task RefreshPropertiesAsync()
        {
            if (_session == null)
            {
                return;
            }

            try
            {
                GlobalSystemMediaTransportControlsSessionMediaProperties props = await _session.TryGetMediaPropertiesAsync();
                Title = props?.Title ?? "";
                Artist = props?.Artist ?? "";
                Thumbnail = await LoadThumbnailAsync(props?.Thumbnail);
            }
            catch (Exception e)
            {
                ShellLogger.Debug($"MediaSessionManager: Unable to read media properties: {e.Message}");
                Title = "";
                Artist = "";
                Thumbnail = null;
            }

            MediaChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Reads the SMTC thumbnail stream into a frozen BitmapImage. Decoding is
        /// capped to a small size since this only ever renders as a taskbar icon.
        /// </summary>
        private static async Task<ImageSource> LoadThumbnailAsync(IRandomAccessStreamReference reference)
        {
            if (reference == null)
            {
                return null;
            }

            try
            {
                using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();

                if (stream == null || stream.Size == 0 || stream.Size > (ulong)int.MaxValue)
                {
                    return null;
                }

                uint size = (uint)stream.Size;
                byte[] bytes = new byte[size];

                using (DataReader reader = new DataReader(stream.GetInputStreamAt(0)))
                {
                    await reader.LoadAsync(size);
                    reader.ReadBytes(bytes);
                }

                BitmapImage bitmap = new BitmapImage();

                using (MemoryStream memoryStream = new MemoryStream(bytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelHeight = 64;
                    bitmap.StreamSource = memoryStream;
                    bitmap.EndInit();
                }

                bitmap.Freeze();

                return bitmap;
            }
            catch (Exception e)
            {
                ShellLogger.Debug($"MediaSessionManager: Unable to read thumbnail: {e.Message}");

                return null;
            }
        }

        private void RefreshPlaybackState()
        {
            try
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus status =
                    _session?.GetPlaybackInfo()?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;

                PlaybackState = status switch
                {
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlaybackState.Playing,
                    _ => MediaPlaybackState.None
                };
            }
            catch (Exception e)
            {
                ShellLogger.Debug($"MediaSessionManager: Unable to read playback info: {e.Message}");
                PlaybackState = MediaPlaybackState.None;
            }

            MediaChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task TogglePlayPauseAsync()
        {
            if (_session == null)
            {
                return;
            }

            try
            {
                await _session.TryTogglePlayPauseAsync();
            }
            catch (Exception e)
            {
                ShellLogger.Debug($"MediaSessionManager: Unable to toggle play/pause: {e.Message}");
            }
        }

        public async Task SkipNextAsync()
        {
            if (_session == null)
            {
                return;
            }

            try
            {
                await _session.TrySkipNextAsync();
            }
            catch (Exception e)
            {
                ShellLogger.Debug($"MediaSessionManager: Unable to skip next: {e.Message}");
            }
        }

        public async Task SkipPreviousAsync()
        {
            if (_session == null)
            {
                return;
            }

            try
            {
                await _session.TrySkipPreviousAsync();
            }
            catch (Exception e)
            {
                ShellLogger.Debug($"MediaSessionManager: Unable to skip previous: {e.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_manager != null)
            {
                _manager.CurrentSessionChanged -= Manager_CurrentSessionChanged;
            }

            if (_session != null)
            {
                _session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                _session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            }

            _manager = null;
            _session = null;
        }
    }
}
