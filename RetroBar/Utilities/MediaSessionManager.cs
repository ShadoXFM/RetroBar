using ManagedShell.Common.Logging;
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
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

        // Fallback for SMTC bridges (foobar2000's, at least) that don't reliably or promptly
        // raise PlaybackInfoChanged - polls the actual status directly so play/pause state
        // never gets stuck showing stale data.
        private const int PlaybackPollIntervalMs = 500;
        private Timer _playbackPollTimer;

        public bool HasSession => _session != null;
        public string Title { get; private set; } = "";
        public string Artist { get; private set; } = "";
        public MediaPlaybackState PlaybackState { get; private set; } = MediaPlaybackState.None;

        /// <summary>Whether the source app has told SMTC it supports seeking to an arbitrary
        /// position (TryChangePlaybackPositionAsync) - not every app implements this even when
        /// it reports a Position/Duration, so callers should hide/disable seek UI when false
        /// rather than assume it'll work.</summary>
        public bool CanSeek { get; private set; }

        /// <summary>The AppUserModelID of whatever app owns the current session, or "" if there
        /// isn't one - used to find and activate that app's own window.</summary>
        public string SourceAppUserModelId { get; private set; } = "";

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
                SourceAppUserModelId = _session.SourceAppUserModelId ?? "";

                _session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                _session.PlaybackInfoChanged += Session_PlaybackInfoChanged;

                RefreshPlaybackState();
                _ = RefreshPropertiesAsync();

                _playbackPollTimer ??= new Timer(_ => RefreshPlaybackState(), null, PlaybackPollIntervalMs, PlaybackPollIntervalMs);
            }
            else
            {
                _playbackPollTimer?.Dispose();
                _playbackPollTimer = null;

                SourceAppUserModelId = "";
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

        // Applies to whichever axis (width or height) is larger, so a widescreen source -
        // a YouTube video frame via a browser's SMTC session, say, 16:9 instead of a square
        // album cover - can't decode any bigger than a normal cover art thumbnail on its long
        // side. Capping only DecodePixelHeight (as this used to) left DecodePixelWidth
        // unconstrained, so a 16:9 source decoded proportionally wider than a 1:1 one ever
        // would, making video thumbnails look inconsistently larger than regular album art.
        private const int ThumbnailMaxDimension = 64;

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

                // Peek the source's natural pixel dimensions (a separate, disposable stream -
                // cheap, since BitmapCacheOption.None only reads the header) to know which axis
                // to constrain before the real decode below.
                bool constrainWidth;
                using (MemoryStream peekStream = new MemoryStream(bytes))
                {
                    BitmapFrame frame = BitmapFrame.Create(peekStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    constrainWidth = frame.PixelWidth > frame.PixelHeight;
                }

                BitmapImage bitmap = new BitmapImage();

                using (MemoryStream memoryStream = new MemoryStream(bytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    if (constrainWidth)
                    {
                        bitmap.DecodePixelWidth = ThumbnailMaxDimension;
                    }
                    else
                    {
                        bitmap.DecodePixelHeight = ThumbnailMaxDimension;
                    }
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
            MediaPlaybackState previous = PlaybackState;

            try
            {
                GlobalSystemMediaTransportControlsSessionPlaybackInfo info = _session?.GetPlaybackInfo();
                GlobalSystemMediaTransportControlsSessionPlaybackStatus status =
                    info?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;

                PlaybackState = status switch
                {
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlaybackState.Playing,
                    _ => MediaPlaybackState.None
                };
                CanSeek = info?.Controls?.IsPlaybackPositionEnabled ?? false;
            }
            catch (Exception e)
            {
                ShellLogger.Debug($"MediaSessionManager: Unable to read playback info: {e.Message}");
                PlaybackState = MediaPlaybackState.None;
                CanSeek = false;
            }

            // Polling calls this every 500ms regardless of whether anything changed - only
            // notify the UI (which would otherwise reset the marquee/tooltip) on a real change.
            if (PlaybackState != previous)
            {
                MediaChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Reads the current playback position and total duration fresh from the session (not
        /// cached) - callers polling this for a live seek bar should call it on their own timer
        /// rather than relying on an event, since SMTC has no "position changed" notification.
        /// </summary>
        public (TimeSpan Position, TimeSpan Duration) GetTimeline()
        {
            try
            {
                GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = _session?.GetTimelineProperties();
                if (timeline == null)
                {
                    return (TimeSpan.Zero, TimeSpan.Zero);
                }

                TimeSpan duration = timeline.EndTime - timeline.StartTime;
                TimeSpan position = timeline.Position;

                // Most SMTC sources only update Position at track/seek/pause boundaries, not
                // continuously - while playing, estimate how far it's advanced since the last
                // update so the bar moves smoothly instead of jumping every poll.
                if (PlaybackState == MediaPlaybackState.Playing)
                {
                    position += DateTimeOffset.Now - timeline.LastUpdatedTime;
                }

                if (position < TimeSpan.Zero)
                {
                    position = TimeSpan.Zero;
                }
                else if (duration > TimeSpan.Zero && position > duration)
                {
                    position = duration;
                }

                return (position, duration);
            }
            catch (Exception e)
            {
                ShellLogger.Debug($"MediaSessionManager: Unable to read timeline: {e.Message}");
                return (TimeSpan.Zero, TimeSpan.Zero);
            }
        }

        public async Task SeekAsync(TimeSpan position)
        {
            if (_session == null || !CanSeek)
            {
                return;
            }

            try
            {
                if (position < TimeSpan.Zero)
                {
                    position = TimeSpan.Zero;
                }

                await _session.TryChangePlaybackPositionAsync(position.Ticks);
            }
            catch (Exception e)
            {
                ShellLogger.Debug($"MediaSessionManager: Unable to seek: {e.Message}");
            }
        }

        public async Task TogglePlayPauseAsync()
        {
            if (_session == null)
            {
                return;
            }

            try
            {
                // Read status fresh rather than trusting the cached PlaybackState field, which
                // can be stale by up to one poll interval (or racing the poll timer's background
                // thread) and would otherwise sometimes pick the wrong direction (e.g. sending
                // Play when it's already playing, which is a no-op).
                GlobalSystemMediaTransportControlsSessionPlaybackStatus currentStatus =
                    _session.GetPlaybackInfo()?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
                bool isPlaying = currentStatus is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing;

                // Send the explicit Play/Pause command rather than TryTogglePlayPauseAsync -
                // some SMTC bridges (e.g. foobar2000's) wire up the discrete Play and Pause
                // buttons but don't reliably honor a generic toggle command.
                if (isPlaying)
                {
                    await _session.TryPauseAsync();
                }
                else
                {
                    await _session.TryPlayAsync();
                }

                // Some SMTC bridges (e.g. foobar2000's) don't reliably (or promptly) raise
                // PlaybackInfoChanged after a command they themselves handled. This refresh
                // catches it immediately when the app already updated its status by the time
                // the command's await returned; the poll timer catches it otherwise.
                RefreshPlaybackState();
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

            _playbackPollTimer?.Dispose();
            _playbackPollTimer = null;

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
