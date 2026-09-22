using ManagedShell;
using ManagedShell.Common.Logging;
using ManagedShell.WindowsTasks;
using RetroBar.Utilities;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

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

        private UIElementAdorner _albumArtAdorner;
        private Border _albumArtContainer;
        private Rectangle _albumArtVisual;
        private ImageBrush _albumArtBrush;

        private UIElementAdorner _previousButtonAdorner;
        private UIElementAdorner _playPauseButtonAdorner;
        private UIElementAdorner _nextButtonAdorner;
        private Button _previousButton;
        private Button _playPauseButton;
        private Button _nextButton;
        private Path _playPauseGlyph;

        private DispatcherTimer _seekPopupTimer;
        private bool _seekSliderDragging;

        // Tracks album-art-click toggle state ourselves rather than reading the window's live
        // Active/foreground state at click time - clicking anywhere in RetroBar's own window
        // (including the album art itself) can make RetroBar the foreground app before our own
        // click handler runs, so the target app's window would never actually read back as
        // "Active" and the toggle would always just re-activate, never minimize. Reset whenever
        // the source app itself changes, so a new track/app always starts at "click to show".
        private bool _sourceAppShown;
        private string _lastSourceAumid;

        // Bound from Taskbar.xaml as "{Binding}" - Taskbar's own DataContext is already the
        // ShellManager instance (see Taskbar.xaml.cs), so this just captures it explicitly
        // rather than relying on every consumer knowing to reach into an inherited DataContext.
        public static readonly DependencyProperty ShellManagerProperty = DependencyProperty.Register(
            nameof(ShellManager), typeof(ShellManager), typeof(MediaPlayer));

        public ShellManager ShellManager
        {
            get => (ShellManager)GetValue(ShellManagerProperty);
            set => SetValue(ShellManagerProperty, value);
        }

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
            MonitorAdjustments.Changed += MonitorAdjustments_Changed;

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

            SeekPopup.IsOpen = false;
            StopMarquee();
            AlbumArtImage.Source = null;
            AlbumArtImage.Visibility = Visibility.Collapsed;
            RemoveAlbumArtAdorner();
            RemoveTransportButtonAdorners();
            SetIsMediaPlaying(false);

            Visibility = Visibility.Collapsed;
        }

        private void SetIsMediaPlaying(bool isPlaying)
        {
            if (Window.GetWindow(this) is Taskbar taskbar)
            {
                taskbar.IsMediaPlaying = isPlaying;
            }
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
                SetIsMediaPlaying(false);
                return;
            }

            if (!_sessionManager.HasSession || string.IsNullOrEmpty(_sessionManager.Title))
            {
                Visibility = Visibility.Collapsed;
                StopMarquee();
                AlbumArtImage.Source = null;
                AlbumArtImage.Visibility = Visibility.Collapsed;
                RemoveAlbumArtAdorner();
                RemoveTransportButtonAdorners();
                SetIsMediaPlaying(false);
                return;
            }

            Visibility = Visibility.Visible;
            SetIsMediaPlaying(true);

            if (_previousButton == null)
            {
                Dispatcher.BeginInvoke(new Action(InitializeTransportButtonAdorners), DispatcherPriority.Loaded);
            }

            string currentAumid = _sessionManager.SourceAppUserModelId;
            if (!string.Equals(currentAumid, _lastSourceAumid, StringComparison.OrdinalIgnoreCase))
            {
                _lastSourceAumid = currentAumid;
                _sourceAppShown = false;
            }

            string trackText = string.IsNullOrEmpty(_sessionManager.Artist)
                ? _sessionManager.Title
                : $"{_sessionManager.Title} \u2014 {_sessionManager.Artist}";

            if (TrackText.Text != trackText)
            {
                TrackText.Text = trackText;
            }

            SeekTitleText.Text = _sessionManager.Title;
            SeekArtistText.Text = _sessionManager.Artist;
            SeekArtistText.Visibility = string.IsNullOrEmpty(_sessionManager.Artist)
                ? Visibility.Collapsed
                : Visibility.Visible;

            // Measurement has to happen after the text has been laid out. This also
            // covers becoming visible again with a track we were already showing.
            Dispatcher.BeginInvoke(new Action(UpdateMarquee), System.Windows.Threading.DispatcherPriority.Loaded);

            ToolTip = trackText;

            Geometry playPauseGlyph = GetGlyph(_sessionManager.PlaybackState == MediaPlaybackState.Playing
                ? "MediaPlayerPauseGeometry"
                : "MediaPlayerPlayGeometry");
            if (_playPauseGlyph != null)
            {
                _playPauseGlyph.Data = playPauseGlyph;
            }
            SeekPlayPauseGlyph.Data = playPauseGlyph;

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
            // Hidden, not Visible/Collapsed: it still reserves its normal layout slot (used
            // to anchor the Adorner's base position/size) but is never actually painted -
            // the Adorner-hosted copy is what's shown. See UpdateAlbumArtAdorner.
            AlbumArtImage.Visibility = art == null ? Visibility.Collapsed : Visibility.Hidden;

            // The seek popup's own copy - a plain Image, so (unlike AlbumArtImage above) it
            // paints directly and just needs an ordinary Visible/Collapsed toggle. Uses
            // LargeThumbnail, not the same (deliberately icon-sized) Thumbnail the taskbar row
            // uses, since the popup shows it much bigger.
            ImageSource largeArt = Settings.Instance.ShowMediaPlayerAlbumArt ? _sessionManager?.LargeThumbnail : null;
            SeekAlbumArtImage.Source = largeArt;
            SeekAlbumArtImage.Visibility = largeArt == null ? Visibility.Collapsed : Visibility.Visible;

            if (art == null)
            {
                RemoveAlbumArtAdorner();
            }
            else
            {
                // AlbumArtImage.ActualWidth/Height (used below) isn't accurate until layout
                // has run at least once after the Visibility change above, so defer.
                Dispatcher.BeginInvoke(new Action(UpdateAlbumArtAdorner), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// Creates (on first use) or refreshes the Adorner-hosted copy of AlbumArtImage that's
        /// actually shown on screen. Rendering through an Adorner - rather than AlbumArtImage
        /// itself - means a per-monitor MonitorAdjustments offset can push the art outside
        /// AlbumArtImage's own layout bounds without the Tray GroupBox's Padding/
        /// BorderThickness (or any other ancestor's) clipping it. See
        /// Utilities/UIElementAdorner.cs for why an Adorner specifically achieves that.
        /// </summary>
        private void UpdateAlbumArtAdorner()
        {
            if (AlbumArtImage.Source == null)
            {
                RemoveAlbumArtAdorner();
                return;
            }

            AdornerLayer layer = AdornerLayer.GetAdornerLayer(AlbumArtImage);
            if (layer == null)
            {
                return;
            }

            if (_albumArtAdorner == null)
            {
                // A Rectangle filled with an ImageBrush, not an Image element - an Image with
                // Stretch="UniformToFill" can both report a DesiredSize larger than its own
                // explicit Width/Height when the source's aspect ratio doesn't match the box
                // (e.g. a browser's widescreen video-frame thumbnail vs. square album art), and
                // anchor its crop to that inflated size instead of the actually-arranged one -
                // showing an off-center sliver of the source instead of a centered crop. A Brush
                // isn't a FrameworkElement with its own Measure/Arrange pass, so it has neither
                // problem: it simply paints, UniformToFill-cropped and centered (ImageBrush's
                // default AlignmentX/Y), within whatever area it's given. Same technique already
                // used for the weather icon (see WeatherDisplay.xaml's Rectangle/OpacityMask).
                _albumArtBrush = new ImageBrush { Stretch = Stretch.UniformToFill };
                _albumArtVisual = new Rectangle
                {
                    Fill = _albumArtBrush,
                    SnapsToDevicePixels = true,
                    UseLayoutRounding = true,
                    Cursor = Cursors.Hand,
                };
                RenderOptions.SetBitmapScalingMode(_albumArtVisual, BitmapScalingMode.HighQuality);
                _albumArtVisual.MouseLeftButtonUp += ActivateSourceApp;

                // The explicit size lives on this wrapping Border - a plain Rectangle sizes
                // itself from whatever space it's arranged into, so the Border is what actually
                // establishes (and, via ClipToBounds, enforces) the fixed square box.
                _albumArtContainer = new Border
                {
                    ClipToBounds = true,
                    Child = _albumArtVisual,
                };

                _albumArtAdorner = new UIElementAdorner(AlbumArtImage, _albumArtContainer, isHitTestVisible: true);
                layer.Add(_albumArtAdorner);
            }

            _albumArtBrush.ImageSource = AlbumArtImage.Source;

            string deviceName = (Window.GetWindow(this) as Taskbar)?.Screen.DeviceName;
            MonitorOffsetValue offset = MonitorAdjustments.Get(deviceName, "MediaAlbumArt");

            // AlbumArtImage.Width/Height (the style-set explicit value), not ActualWidth/
            // ActualHeight - ActualWidth inherits the exact same Image+UniformToFill inflation
            // this whole fix is working around, so it can't be trusted as a fallback either.
            _albumArtContainer.Width = offset.Width ?? AlbumArtImage.Width;
            _albumArtContainer.Height = offset.Height ?? AlbumArtImage.Height;

            // On the container, not _albumArtVisual: the Image now sits inside a fixed-size
            // clipped Border (see above), so a transform on the Image itself shifts its content
            // within that fixed clip window - cropping it off-center - instead of moving the
            // whole box the way this offset is meant to. Transforming the container moves/scales
            // the box (clip window and all) as a single unit, leaving the image centered and
            // cleanly UniformToFill-cropped inside it.
            bool hasScale = offset.Scale.HasValue && offset.Scale.Value != 1;
            if (offset.X != 0 || offset.Y != 0 || hasScale)
            {
                var group = new TransformGroup();

                if (hasScale)
                {
                    _albumArtContainer.RenderTransformOrigin = new Point(0.5, 0.5);
                    group.Children.Add(new ScaleTransform(offset.Scale.Value, offset.Scale.Value));
                }

                group.Children.Add(new TranslateTransform(offset.X, offset.Y));
                _albumArtContainer.RenderTransform = group;
            }
            else
            {
                _albumArtContainer.RenderTransform = null;
            }

            if (offset.BitmapScaling != null && Enum.TryParse(offset.BitmapScaling, out BitmapScalingMode scalingMode))
            {
                RenderOptions.SetBitmapScalingMode(_albumArtVisual, scalingMode);
            }

            _albumArtAdorner.InvalidateMeasure();
        }

        private void RemoveAlbumArtAdorner()
        {
            if (_albumArtAdorner == null)
            {
                return;
            }

            AdornerLayer.GetAdornerLayer(AlbumArtImage)?.Remove(_albumArtAdorner);
            _albumArtAdorner = null;
            _albumArtContainer = null;
            _albumArtVisual = null;
            _albumArtBrush = null;
        }

        /// <summary>
        /// Builds the three transport buttons in code (rather than XAML) and hosts each in its
        /// own Adorner anchored to a same-styled, always-Hidden spacer left behind in the
        /// StackPanel (see MediaPlayer.xaml) to reserve their layout slot. Needed for the same
        /// reason MediaAlbumArt uses an Adorner: a per-monitor MonitorAdjustments Margin/X large
        /// enough to close the gap to the tray box's edge pushes the button past the Tray
        /// GroupBox's own Padding/BorderThickness-driven clip (see Taskbar.xaml's
        /// AdornerDecorator comment) - rendering through the Adorner escapes that clip
        /// entirely, the same way it already does for the album art. The existing
        /// utilities:MonitorOffset.Tag system still applies to these buttons exactly as before,
        /// since it just tracks the live element - it doesn't care which visual parent hosts it.
        /// </summary>
        private void InitializeTransportButtonAdorners()
        {
            if (_previousButton != null)
            {
                return;
            }

            _previousButton = CreateTransportButton("MediaButtonPrevious", "MediaGlyphPrevious", "MediaPlayerPreviousGeometry", "media_previous", PreviousButton_OnClick, out _);
            AttachTransportButtonAdorner(PreviousButtonSpacer, _previousButton, ref _previousButtonAdorner);

            _playPauseButton = CreateTransportButton("MediaButtonPlayPause", "MediaGlyphPlayPause", "MediaPlayerPlayGeometry", "media_play_pause", PlayPauseButton_OnClick, out _playPauseGlyph);
            AttachTransportButtonAdorner(PlayPauseButtonSpacer, _playPauseButton, ref _playPauseButtonAdorner);

            _nextButton = CreateTransportButton("MediaButtonNext", "MediaGlyphNext", "MediaPlayerNextGeometry", "media_next", NextButton_OnClick, out _);
            AttachTransportButtonAdorner(NextButtonSpacer, _nextButton, ref _nextButtonAdorner);

            if (_sessionManager != null)
            {
                _playPauseGlyph.Data = GetGlyph(_sessionManager.PlaybackState == MediaPlaybackState.Playing
                    ? "MediaPlayerPauseGeometry"
                    : "MediaPlayerPlayGeometry");
            }
        }

        private static Button CreateTransportButton(string buttonTag, string glyphTag, string geometryResourceKey, string tooltipResourceKey, RoutedEventHandler onClick, out Path glyph)
        {
            glyph = new Path();
            glyph.SetResourceReference(StyleProperty, "MediaPlayerGlyphStyle");
            glyph.SetResourceReference(Path.DataProperty, geometryResourceKey);
            MonitorOffset.SetTag(glyph, glyphTag);

            var button = new Button { Content = glyph };
            button.SetResourceReference(StyleProperty, "MediaPlayerButtonStyle");
            button.SetResourceReference(ToolTipProperty, tooltipResourceKey);
            button.Click += onClick;
            MonitorOffset.SetTag(button, buttonTag);

            return button;
        }

        private static void AttachTransportButtonAdorner(FrameworkElement spacer, UIElement button, ref UIElementAdorner adorner)
        {
            AdornerLayer layer = AdornerLayer.GetAdornerLayer(spacer);
            if (layer == null)
            {
                return;
            }

            adorner = new UIElementAdorner(spacer, button, isHitTestVisible: true);
            layer.Add(adorner);
        }

        private void RemoveTransportButtonAdorners()
        {
            RemoveTransportButtonAdorner(PreviousButtonSpacer, ref _previousButtonAdorner);
            RemoveTransportButtonAdorner(PlayPauseButtonSpacer, ref _playPauseButtonAdorner);
            RemoveTransportButtonAdorner(NextButtonSpacer, ref _nextButtonAdorner);

            _previousButton = null;
            _playPauseButton = null;
            _nextButton = null;
            _playPauseGlyph = null;
        }

        private static void RemoveTransportButtonAdorner(FrameworkElement spacer, ref UIElementAdorner adorner)
        {
            if (adorner == null)
            {
                return;
            }

            AdornerLayer.GetAdornerLayer(spacer)?.Remove(adorner);
            adorner = null;
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

        /// <summary>
        /// Toggles the current media session's app window - the same thing clicking its taskbar
        /// button would do: brings it to the foreground if it isn't already active, or minimizes
        /// it if it is (so clicking the album art a second time puts the app back out of the way
        /// instead of doing nothing).
        ///
        /// SMTC's SourceAppUserModelId is a real AppUserModelID for UWP apps, matching
        /// ApplicationWindow.AppUserModelID directly - but for a classic desktop app that never
        /// explicitly registered one (foobar2000, Spotify, Discord, ...; empirically most of
        /// them), it falls back to "{exe filename}.exe", which ManagedShell's AppUserModelID
        /// never populates (it stays "") - so that case is matched against the executable
        /// filename portion of WinFileName instead, which is populated for every window.
        /// </summary>
        private void ActivateSourceApp(object sender, MouseButtonEventArgs e)
        {
            string aumid = _sessionManager?.SourceAppUserModelId;
            if (string.IsNullOrEmpty(aumid) || ShellManager?.Tasks?.GroupedWindows == null)
            {
                return;
            }

            foreach (object item in ShellManager.Tasks.GroupedWindows)
            {
                if (item is not ApplicationWindow window)
                {
                    continue;
                }

                bool isMatch = (!string.IsNullOrEmpty(window.AppUserModelID)
                        && string.Equals(window.AppUserModelID, aumid, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrEmpty(window.WinFileName)
                        && string.Equals(System.IO.Path.GetFileName(window.WinFileName), aumid, StringComparison.OrdinalIgnoreCase));

                if (!isMatch)
                {
                    continue;
                }

                if (_sourceAppShown && !window.IsMinimized)
                {
                    window.Minimize();
                    _sourceAppShown = false;
                }
                else
                {
                    window.BringToFront();
                    _sourceAppShown = true;
                }

                return;
            }
        }

        #region Seek popup

        // Set right before SeekPopup.IsOpen flips to false because the SAME click that's about
        // to reach TrackTextCanvas_OnMouseLeftButtonUp already dismissed it (see
        // SeekPopup_OnClosed) - without this, clicking the track text a second time to close the
        // popup would instead close-then-immediately-reopen it: Popup's own StaysOpen="False"
        // dismisses it on that click before our handler even runs, so by the time our handler
        // checks IsOpen it already reads false and toggles it back open.
        private bool _ignoreNextTrackTextClick;

        private void TrackTextCanvas_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_ignoreNextTrackTextClick)
            {
                _ignoreNextTrackTextClick = false;
                return;
            }

            if (_sessionManager != null && _sessionManager.HasSession)
            {
                SeekPopup.IsOpen = !SeekPopup.IsOpen;
            }
        }

        private void SeekPopup_OnOpened(object sender, EventArgs e)
        {
            bool canSeek = _sessionManager?.CanSeek ?? false;
            SeekSlider.IsEnabled = canSeek;
            SeekBackButton.IsEnabled = canSeek;
            SeekForwardButton.IsEnabled = canSeek;

            RefreshSeekDisplay();

            _seekPopupTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _seekPopupTimer.Tick -= SeekPopupTimer_OnTick;
            _seekPopupTimer.Tick += SeekPopupTimer_OnTick;
            _seekPopupTimer.Start();
        }

        private void SeekPopup_OnClosed(object sender, EventArgs e)
        {
            _seekPopupTimer?.Stop();

            // Only suppress the next click if it's plausibly the one that just caused this close
            // (the mouse is still over the track text) - a dismissal from clicking elsewhere
            // shouldn't eat a later, unrelated click on the text.
            Point mousePos = Mouse.GetPosition(TrackTextCanvas);
            _ignoreNextTrackTextClick = mousePos.X >= 0 && mousePos.Y >= 0
                && mousePos.X <= TrackTextCanvas.ActualWidth && mousePos.Y <= TrackTextCanvas.ActualHeight;
        }

        private void SeekPopupTimer_OnTick(object sender, EventArgs e)
        {
            RefreshSeekDisplay();
        }

        // Skipped while the user has the thumb held down, so the timer's poll doesn't fight
        // their drag - RefreshSeekDisplay resumes updating the moment they let go.
        private void RefreshSeekDisplay()
        {
            if (_sessionManager == null || _seekSliderDragging)
            {
                return;
            }

            (TimeSpan position, TimeSpan duration) = _sessionManager.GetTimeline();
            SetSeekDisplay(position, duration);
        }

        // Separated from RefreshSeekDisplay so a skip/seek can paint the target position
        // immediately (optimistically, before the source app's own position catches up and the
        // next real poll confirms it) - GetTimeline() right after sending a seek command often
        // still reflects the OLD position for a moment, which would otherwise make the bar look
        // like it ignored the click until the next 500ms poll happens to land after the source
        // updates.
        private void SetSeekDisplay(TimeSpan position, TimeSpan duration)
        {
            SeekSlider.Maximum = duration.TotalSeconds > 0 ? duration.TotalSeconds : 1;
            SeekSlider.Value = position.TotalSeconds;
            SeekTimeText.Text = $"{FormatTime(position)} / {FormatTime(duration)}";
        }

        private static string FormatTime(TimeSpan time)
        {
            return time.Hours > 0
                ? $"{time.Hours}:{time.Minutes:D2}:{time.Seconds:D2}"
                : $"{time.Minutes}:{time.Seconds:D2}";
        }

        private void SeekSlider_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _seekSliderDragging = true;
        }

        private async void SeekSlider_OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _seekSliderDragging = false;

            if (_sessionManager == null)
            {
                return;
            }

            TimeSpan target = TimeSpan.FromSeconds(SeekSlider.Value);
            SetSeekDisplay(target, TimeSpan.FromSeconds(SeekSlider.Maximum));

            await _sessionManager.SeekAsync(target);
        }

        private async void SeekBackButton_OnClick(object sender, RoutedEventArgs e)
        {
            await SeekRelativeAsync(TimeSpan.FromSeconds(-10));
        }

        private async void SeekForwardButton_OnClick(object sender, RoutedEventArgs e)
        {
            await SeekRelativeAsync(TimeSpan.FromSeconds(10));
        }

        private async System.Threading.Tasks.Task SeekRelativeAsync(TimeSpan delta)
        {
            if (_sessionManager == null)
            {
                return;
            }

            (TimeSpan position, TimeSpan duration) = _sessionManager.GetTimeline();
            TimeSpan target = position + delta;
            if (target < TimeSpan.Zero)
            {
                target = TimeSpan.Zero;
            }
            else if (duration > TimeSpan.Zero && target > duration)
            {
                target = duration;
            }

            // Paint the target position immediately rather than waiting for the seek command to
            // round-trip and the source app's own position to catch up - see SetSeekDisplay.
            SetSeekDisplay(target, duration);

            await _sessionManager.SeekAsync(target);
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
            MonitorAdjustments.Changed -= MonitorAdjustments_Changed;
            Stop();
            _isLoaded = false;
        }

        private void MonitorAdjustments_Changed(object sender, EventArgs e)
        {
            // Fires on a background (file-watcher) thread.
            Dispatcher.BeginInvoke(new Action(UpdateAlbumArtAdorner));
        }
    }
}
