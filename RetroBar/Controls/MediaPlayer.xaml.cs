using ManagedShell.Common.Logging;
using RetroBar.Utilities;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RetroBar.Controls
{
    /// <summary>
    /// Interaction logic for MediaPlayer.xaml
    /// A compact "now playing" widget with play/pause/skip controls, driven by
    /// whatever app currently owns the System Media Transport Controls session
    /// (Spotify, browsers, Groove/Media Player, etc).
    /// </summary>
    public partial class MediaPlayer : UserControl
    {
        // Marquee tuning.
        private const double MarqueePixelsPerSecond = 30d;
        private const double MarqueeEndPauseSeconds = 2d;
        // Ignore sub-pixel overflow so we don't animate text that already fits.
        private const double MarqueeOverflowThreshold = 1d;

        private MediaSessionManager _sessionManager;
        private bool _isLoaded;
        private bool _marqueeRunning;
        private double _marqueeOverflow;

        public MediaPlayer()
        {
            InitializeComponent();
        }

        private async void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isLoaded)
            {
                return;
            }

            _isLoaded = true;

            Settings.Instance.PropertyChanged += Settings_PropertyChanged;

            if (!Settings.Instance.ShowMediaPlayer)
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            await StartAsync();
        }

        private async System.Threading.Tasks.Task StartAsync()
        {
            // GlobalSystemMediaTransportControlsSessionManager requires Windows 10 1809 (build 17763) or later.
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            {
                // Media session APIs aren't available on this version of Windows; stay hidden.
                Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                _sessionManager = new MediaSessionManager();
                _sessionManager.MediaChanged += SessionManager_MediaChanged;
                await _sessionManager.InitializeAsync();
            }
            catch (Exception ex)
            {
                ShellLogger.Debug($"MediaPlayer: Media session APIs unavailable, hiding widget: {ex.Message}");
                _sessionManager = null;
                Visibility = Visibility.Collapsed;
            }
        }

        private void Stop()
        {
            if (_sessionManager != null)
            {
                _sessionManager.MediaChanged -= SessionManager_MediaChanged;
                _sessionManager.Dispose();
                _sessionManager = null;
            }

            StopMarquee();
            AlbumArtImage.Source = null;
            AlbumArtImage.Visibility = Visibility.Collapsed;

            Visibility = Visibility.Collapsed;
        }

        private void Settings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.ShowMediaPlayerAlbumArt))
            {
                UpdateAlbumArt();
                return;
            }

            if (e.PropertyName != nameof(Settings.ShowMediaPlayer))
            {
                return;
            }

            if (Settings.Instance.ShowMediaPlayer)
            {
                _ = StartAsync();
            }
            else
            {
                Stop();
            }
        }

        private void SessionManager_MediaChanged(object sender, EventArgs e)
        {
            // The SMTC events can fire on a non-UI thread.
            Dispatcher.BeginInvoke(new Action(UpdateFromSession));
        }

        private void UpdateFromSession()
        {
            if (_sessionManager == null || !Settings.Instance.ShowMediaPlayer)
            {
                Visibility = Visibility.Collapsed;
                StopMarquee();
                return;
            }

            if (!_sessionManager.HasSession || string.IsNullOrEmpty(_sessionManager.Title))
            {
                Visibility = Visibility.Collapsed;
                StopMarquee();
                AlbumArtImage.Source = null;
                AlbumArtImage.Visibility = Visibility.Collapsed;
                return;
            }

            Visibility = Visibility.Visible;

            string trackText = string.IsNullOrEmpty(_sessionManager.Artist)
                ? _sessionManager.Title
                : $"{_sessionManager.Title} \u2014 {_sessionManager.Artist}";

            if (TrackText.Text != trackText)
            {
                TrackText.Text = trackText;
            }

            // Measurement has to happen after the text has been laid out. This also
            // covers becoming visible again with a track we were already showing.
            Dispatcher.BeginInvoke(new Action(UpdateMarquee), System.Windows.Threading.DispatcherPriority.Loaded);

            ToolTip = trackText;

            PlayPauseGlyph.Data = GetGlyph(_sessionManager.PlaybackState == MediaPlaybackState.Playing
                ? "MediaPlayerPauseGeometry"
                : "MediaPlayerPlayGeometry");

            UpdateAlbumArt();
        }

        private Geometry GetGlyph(string resourceKey)
        {
            if (TryFindResource(resourceKey) is Geometry geometry)
            {
                return geometry;
            }

            return Geometry.Empty;
        }

        private void UpdateAlbumArt()
        {
            ImageSource art = Settings.Instance.ShowMediaPlayerAlbumArt ? _sessionManager?.Thumbnail : null;

            AlbumArtImage.Source = art;
            AlbumArtImage.Visibility = art == null ? Visibility.Collapsed : Visibility.Visible;
        }

        #region Marquee

        private void TrackText_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
            {
                Dispatcher.BeginInvoke(new Action(UpdateMarquee), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// The text sits on a Canvas, so it is always laid out (and rendered) at
        /// its full natural width. This sizes the clipping host to the width we
        /// actually want to show, then slides the text if it doesn't all fit.
        /// </summary>
        private void UpdateMarquee()
        {
            if (Visibility != Visibility.Visible || string.IsNullOrEmpty(TrackText.Text))
            {
                StopMarquee();
                return;
            }

            // The Canvas child is measured with infinite width, so ActualWidth is
            // the full untruncated text width.
            double fullWidth = Math.Ceiling(TrackText.ActualWidth);

            if (fullWidth <= 0)
            {
                StopMarquee();
                return;
            }

            double max = TrackTextHost.MaxWidth;
            double visibleWidth = double.IsInfinity(max) || double.IsNaN(max) || max <= 0
                ? fullWidth
                : Math.Min(fullWidth, max);

            if (double.IsNaN(TrackTextHost.Width) || Math.Abs(TrackTextHost.Width - visibleWidth) > 0.5)
            {
                TrackTextHost.Width = visibleWidth;
            }

            double overflow = Math.Ceiling(fullWidth - visibleWidth);

            if (overflow <= MarqueeOverflowThreshold)
            {
                StopMarquee();
                return;
            }

            // Don't restart an identical animation on every tick of the session.
            if (_marqueeRunning && Math.Abs(_marqueeOverflow - overflow) < 0.5)
            {
                return;
            }

            StartMarquee(overflow);
        }

        private void StartMarquee(double overflow)
        {
            double travelSeconds = Math.Max(overflow / MarqueePixelsPerSecond, 0.5);

            TimeSpan pause = TimeSpan.FromSeconds(MarqueeEndPauseSeconds);
            TimeSpan travel = TimeSpan.FromSeconds(travelSeconds);

            TimeSpan atStartEnd = pause;
            TimeSpan atEnd = atStartEnd + travel;
            TimeSpan atEndPause = atEnd + pause;
            TimeSpan atFinish = atEndPause + travel;

            DoubleAnimationUsingKeyFrames animation = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(atFinish),
                RepeatBehavior = RepeatBehavior.Forever
            };

            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(atStartEnd)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(atEnd)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(atEndPause)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(atFinish)));

            TrackTextTransform.BeginAnimation(TranslateTransform.XProperty, animation);

            _marqueeRunning = true;
            _marqueeOverflow = overflow;
        }

        private void StopMarquee()
        {
            if (_marqueeRunning)
            {
                TrackTextTransform.BeginAnimation(TranslateTransform.XProperty, null);
                _marqueeRunning = false;
                _marqueeOverflow = 0;
            }

            TrackTextTransform.X = 0;
        }

        #endregion

        private async void PreviousButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (_sessionManager != null)
            {
                await _sessionManager.SkipPreviousAsync();
            }
        }

        private async void PlayPauseButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (_sessionManager != null)
            {
                await _sessionManager.TogglePlayPauseAsync();
            }
        }

        private async void NextButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (_sessionManager != null)
            {
                await _sessionManager.SkipNextAsync();
            }
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
            Stop();
            _isLoaded = false;
        }
    }
}
