using ManagedShell;
using ManagedShell.Common.Logging;
using ManagedShell.WindowsTasks;
using RetroBar.Converters;
using RetroBar.Utilities;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

        private double _vuMeterMaxWidth;

        private UIElementAdorner _previousButtonAdorner;
        private UIElementAdorner _playPauseButtonAdorner;
        private UIElementAdorner _nextButtonAdorner;
        private Button _previousButton;
        private Button _playPauseButton;
        private Button _nextButton;
        private Path _playPauseGlyph;

        // Lives on the button's Adorner, NOT the button itself, since MonitorOffset already owns
        // the button's own RenderTransform for per-monitor X/Y nudges (see MonitorOffset.Apply,
        // which unconditionally overwrites RenderTransform) - a second transform on the same
        // property would fight that the next time monitor-adjustments.json is reloaded. The
        // Adorner is a separate FrameworkElement with its own independent RenderTransform, so
        // this never collides. No longer animated (the old hover-triggered slide/swap with an
        // in-place visualizer is gone - see AttachTransportButtonAdorner's own remarks), but the
        // wrapper Grid built there still needs a RenderTransform target to exist.
        private TranslateTransform _previousButtonSlide;
        private TranslateTransform _playPauseButtonSlide;
        private TranslateTransform _nextButtonSlide;

        // Capture only runs while there's an active session to visualize, started/stopped
        // alongside the transport buttons themselves in UpdateFromSession/Stop. Feeds the VU
        // meter behind the track text (VisualizerCapture_OnLevelUpdated) - the only visualizer
        // RetroBar has left, now that the old in-place and relocated-spectrum-bars displays are
        // both gone.
        private AudioSpectrumCapture _visualizerCapture;

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

            InitializeVuMeterAccentBrush();

            Settings.Instance.PropertyChanged += Settings_PropertyChanged;
            MonitorAdjustments.Changed += MonitorAdjustments_Changed;

            if (!Settings.Instance.ShowMediaPlayer)
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            await StartAsync();
        }

        // The Windows accent color at full saturation read as too strong/colorful for a subtle
        // background fill - blending it toward its own perceptual gray (rather than just adding
        // transparency, which is Opacity's own separate job - see VuMeterAccentBrush's own
        // remarks in the XAML) mutes the color itself while keeping it recognizably tinted.
        // Mutates the brush's Color in place, so every {DynamicResource VuMeterAccentBrush}
        // usage picks it up without needing the resource entry itself replaced. Computed once
        // (SystemColors.HighlightColor itself is a static read, not something that would ever
        // change without the whole app restarting anyway).
        private const double VuMeterAccentDesaturation = 0.95;

        private void InitializeVuMeterAccentBrush()
        {
            if (Resources["VuMeterAccentBrush"] is not SolidColorBrush brush)
            {
                return;
            }

            brush.Color = DesaturateColor(SystemColors.HighlightColor, VuMeterAccentDesaturation);
        }

        // factor 0 = original color, 1 = fully gray. Blends each channel toward the color's own
        // perceptual luminance (standard luma weights) rather than a fixed neutral gray, so
        // brightness stays roughly the same and only how colorful it looks changes.
        private static Color DesaturateColor(Color color, double factor)
        {
            double luminance = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;

            byte Blend(byte channel) => (byte)Math.Clamp(channel + (luminance - channel) * factor, 0, 255);

            return Color.FromRgb(Blend(color.R), Blend(color.G), Blend(color.B));
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

            // Guards against orphaning a previous instance (and its own background poll timer,
            // which would otherwise keep running forever with nothing left reading from it) if
            // this is ever called again while one's already active - confirmed happening via two
            // independent, interleaved RefreshPlaybackState poll sequences in the log.
            if (_sessionManager != null)
            {
                _sessionManager.MediaChanged -= SessionManager_MediaChanged;
                MediaSessionManager.Release(_sessionManager);
                _sessionManager = null;
            }

            try
            {
                // The manager is shared with every other monitor's media player (see
                // MediaSessionManager.AcquireAsync), so they all read the same state.
                MediaSessionManager manager = await MediaSessionManager.AcquireAsync();

                if (!_isLoaded || !Settings.Instance.ShowMediaPlayer)
                {
                    // Unloaded / turned off while the manager was starting.
                    MediaSessionManager.Release(manager);
                    return;
                }

                _sessionManager = manager;
                _sessionManager.MediaChanged += SessionManager_MediaChanged;

                // The shared manager may already know the current track and play state (another
                // monitor's player started it first) and won't raise a change for it - show it now
                // rather than waiting for the next one.
                UpdateFromSession();
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
                MediaSessionManager.Release(_sessionManager);
                _sessionManager = null;
            }

            SeekPopup.IsOpen = false;
            StopMarquee();
            AlbumArtImage.Source = null;
            AlbumArtImage.Visibility = Visibility.Collapsed;
            RemoveAlbumArtAdorner();
            RemoveTransportButtonAdorners();
            StopVisualizer();
            SetIsMediaPlaying(false);
            UpdateVisualizerActiveState(false);

            Visibility = Visibility.Collapsed;
        }

        private void SetIsMediaPlaying(bool isPlaying)
        {
            if (Window.GetWindow(this) is Taskbar taskbar)
            {
                taskbar.IsMediaPlaying = isPlaying;
            }
        }

        // Unlike SetIsMediaPlaying above (which really means "has a session", not literally
        // playing), isPlaying here tracks actual PlaybackState - the VU meter behind the track
        // text shouldn't show for a paused/silent track.
        private void UpdateVisualizerActiveState(bool isPlaying)
        {
            VuMeterContainer.Visibility = isPlaying ? Visibility.Visible : Visibility.Collapsed;
            if (!isPlaying)
            {
                // So the next time it reappears, it doesn't flash whatever width either bar last
                // had before VisualizerCapture_OnLevelUpdated's own next update arrives.
                VuMeterFillLeft.Width = 0;
                VuMeterFillRight.Width = 0;
            }
        }

        private void Settings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.ShowMediaPlayerAlbumArt))
            {
                UpdateAlbumArt();
                return;
            }

            if (e.PropertyName == nameof(Settings.VuMeterCaptureDevice))
            {
                // Settings can change on a non-UI thread; only restart capture if it was running
                // (i.e. there's an active session) - otherwise the next session start picks the
                // new device up on its own.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_visualizerCapture != null)
                    {
                        StopVisualizer();
                        StartVisualizer();
                    }
                }));
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
                StopVisualizer();
                SetIsMediaPlaying(false);
                UpdateVisualizerActiveState(false);
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
                StopVisualizer();
                SetIsMediaPlaying(false);
                UpdateVisualizerActiveState(false);
                return;
            }

            Visibility = Visibility.Visible;
            SetIsMediaPlaying(true);
            UpdateVisualizerActiveState(_sessionManager.PlaybackState == MediaPlaybackState.Playing);

            if (_previousButton == null)
            {
                Dispatcher.BeginInvoke(new Action(InitializeTransportButtonAdorners), DispatcherPriority.Loaded);
            }

            StartVisualizer();

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

            // Same reasoning - needs post-layout ActualWidth/TransformToVisual reads.
            Dispatcher.BeginInvoke(new Action(UpdateVuMeterOffset), System.Windows.Threading.DispatcherPriority.Loaded);

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
            // Falls back to the source app's own taskbar icon (e.g. foobar2000's) when the track
            // itself has no embedded/reported cover art, instead of showing nothing - any player,
            // not just one specific app. FindSourceAppWindow does a small linear search, so it's
            // looked up once and reused for both images below rather than twice.
            ApplicationWindow sourceWindow = Settings.Instance.ShowMediaPlayerAlbumArt ? FindSourceAppWindow() : null;

            ImageSource art = Settings.Instance.ShowMediaPlayerAlbumArt ? (_sessionManager?.Thumbnail ?? sourceWindow?.Icon) : null;

            AlbumArtImage.Source = art;
            // Hidden, not Visible/Collapsed: it still reserves its normal layout slot (used
            // to anchor the Adorner's base position/size) but is never actually painted -
            // the Adorner-hosted copy is what's shown. See UpdateAlbumArtAdorner.
            AlbumArtImage.Visibility = art == null ? Visibility.Collapsed : Visibility.Hidden;

            // The seek popup's own copy - a plain Image (Stretch="Uniform", see the XAML's own
            // remarks), so it paints directly and just needs an ordinary Visible/Collapsed
            // toggle. Uses LargeThumbnail, not the same (deliberately icon-sized) Thumbnail the
            // taskbar row uses, since the popup shows it much bigger - the app icon fallback has
            // no separate large version, so it's reused as-is here too (a small icon, upscaled by
            // the popup's own Image, beats nothing).
            ImageSource largeArt = Settings.Instance.ShowMediaPlayerAlbumArt ? (_sessionManager?.LargeThumbnail ?? sourceWindow?.Icon) : null;
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

            // Album art appearing/disappearing changes how much space TrackTextHost is pushed
            // over by, which is exactly what the VU meter's own left edge is measured from - see
            // UpdateVuMeterOffset. Deferred for the same reason as UpdateAlbumArtAdorner above.
            Dispatcher.BeginInvoke(new Action(UpdateVuMeterOffset), System.Windows.Threading.DispatcherPriority.Loaded);
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
        /// Refreshes VuMeterContainer's own Width and left-shift RenderTransform. It's plain
        /// (non-Adorner) content, declared before the Border/TrackTextCanvas in MediaPlayer.xaml
        /// so it paints behind the track text, and behind the album art's own Adorner too
        /// (Adorners always paint above ordinary content, regardless of tree position - see
        /// UpdateAlbumArtAdorner above for that technique). It only needs a RenderTransform to
        /// reach past TrackTextHost's own left edge, now that TrackTextHost itself is no longer
        /// clipped (see MediaPlayerTrackTextHostStyle) - unlike the album art, it never needs to
        /// escape the Tray GroupBox's own clip, since it never grows further left than
        /// MediaPlayerRoot's own edge.
        /// </summary>
        private void UpdateVuMeterOffset()
        {
            // Reads the same per-monitor "VuMeterContainer" tag this element is itself tagged
            // with in XAML - not applied automatically via MonitorOffset.Apply because X/Y here
            // mean an ADDITIONAL nudge on top of the left-shift computed below, and Width/Height
            // need the "or fall back to the natural layout-derived value" logic MonitorOffset.
            // Apply doesn't have, the same way UpdateAlbumArtAdorner reads "MediaAlbumArt" for
            // _albumArtContainer instead of relying on AlbumArtImage's own automatic tag.
            string deviceName = (Window.GetWindow(this) as Taskbar)?.Screen.DeviceName;
            MonitorOffsetValue offset = MonitorAdjustments.Get(deviceName, "VuMeterContainer");

            // TrackTextHost's own top-left relative to MediaPlayerRoot - already accounts for
            // the album art's reserved width (zero when collapsed, its full box+margin when
            // shown) and any per-monitor MonitorAdjustments margin/offset on either element,
            // without duplicating that math here. VuMeterContainer's own untransformed position
            // already starts here too (it's TrackTextHost's own first child) - shifting it left
            // by exactly this amount moves its start back to the tray box's own left edge.
            Point trackTextHostOrigin = TrackTextHost.TransformToVisual(MediaPlayerRoot).Transform(new Point(0, 0));
            double leftOffset = Math.Max(0, trackTextHostOrigin.X);

            // TrackText's own ActualWidth capped by TrackTextHost's own MaxWidth (its Style's
            // fixed 150, not its dynamic ActualWidth) - the actual glyph width for short text,
            // or the marquee's own visible-width ceiling for a long/scrolling title. Deliberately
            // NOT TrackTextHost.ActualWidth: VuMeterContainer is now a plain sibling INSIDE
            // TrackTextHost's own Grid (see MediaPlayer.xaml), so before UpdateMarquee has
            // explicitly pinned TrackTextHost's own Width for the current track, Grid falls back
            // to auto-sizing itself from the MAX of its children's own DesiredSize - which would
            // include VuMeterContainer's own (possibly still-stale, from the previous track)
            // large Width, inflating TrackTextHost.ActualWidth right back into whatever the bar
            // last reached, in a feedback loop that never actually shrinks down to the real text.
            // MaxWidth is a fixed Style constant, immune to that. Measured from the tray box's
            // own left edge instead of from TrackTextHost's, so a bar can grow past
            // TrackTextHost's own left edge and behind the album art before it starts covering
            // any of the actual text. offset.Width, when set, overrides this outright, same as
            // every other MonitorOffset-tagged Width.
            double textWidth = Math.Min(TrackText.ActualWidth, TrackTextHost.MaxWidth);
            double naturalWidth = leftOffset + Math.Max(0, textWidth);
            _vuMeterMaxWidth = offset.Width ?? naturalWidth;

            VuMeterContainer.Width = _vuMeterMaxWidth;
            VuMeterContainer.Height = offset.Height ?? double.NaN;
            VuMeterContainer.RenderTransform = new TranslateTransform(offset.X - leftOffset, offset.Y);
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
            _previousButtonSlide = AttachTransportButtonAdorner(PreviousButtonSpacer, _previousButton, ref _previousButtonAdorner);

            _playPauseButton = CreateTransportButton("MediaButtonPlayPause", "MediaGlyphPlayPause", "MediaPlayerPlayGeometry", "media_play_pause", PlayPauseButton_OnClick, out _playPauseGlyph);
            _playPauseButtonSlide = AttachTransportButtonAdorner(PlayPauseButtonSpacer, _playPauseButton, ref _playPauseButtonAdorner);

            _nextButton = CreateTransportButton("MediaButtonNext", "MediaGlyphNext", "MediaPlayerNextGeometry", "media_next", NextButton_OnClick, out _);
            _nextButtonSlide = AttachTransportButtonAdorner(NextButtonSpacer, _nextButton, ref _nextButtonAdorner);

            if (_sessionManager != null)
            {
                _playPauseGlyph.Data = GetGlyph(_sessionManager.PlaybackState == MediaPlaybackState.Playing
                    ? "MediaPlayerPauseGeometry"
                    : "MediaPlayerPlayGeometry");
            }

            // Freshly (re)created buttons default to visible - the transport buttons are always
            // shown now (the old hover-triggered swap with an in-place visualizer is gone), so
            // nothing further to fold in here.
            if (_previousButton != null)
            {
                _previousButton.Visibility = Visibility.Visible;
            }
            if (_playPauseButton != null)
            {
                _playPauseButton.Visibility = Visibility.Visible;
            }
            if (_nextButton != null)
            {
                _nextButton.Visibility = Visibility.Visible;
            }
        }

        private void StartVisualizer()
        {
            try
            {
                if (_visualizerCapture != null)
                {
                    return;
                }

                _visualizerCapture = new AudioSpectrumCapture();
                _visualizerCapture.LevelUpdated += VisualizerCapture_OnLevelUpdated;
                _visualizerCapture.Start();
            }
            catch (Exception ex)
            {
                ShellLogger.Error($"MediaPlayer: StartVisualizer threw: {ex}");
            }
        }

        private void StopVisualizer()
        {
            if (_visualizerCapture == null)
            {
                return;
            }

            _visualizerCapture.LevelUpdated -= VisualizerCapture_OnLevelUpdated;
            _visualizerCapture.Dispose();
            _visualizerCapture = null;
        }

        // Fires on AudioSpectrumCapture's own capture thread, not the UI thread. Only actually
        // moves the bars when the VU-meter style is selected and VuMeterContainer is currently
        // Visible (see UpdateVisualizerActiveState) - no need to keep computing a Width nobody can
        // see, and this way a track that's paused (Visibility already Collapsed) doesn't leave
        // either bar holding whatever width it last had once playback resumes and they reappear.
        private void VisualizerCapture_OnLevelUpdated(float left, float right)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_visualizerCapture == null || VuMeterContainer.Visibility != Visibility.Visible)
                {
                    return;
                }

                // _vuMeterMaxWidth (kept up to date by UpdateVuMeterOffset) already reproduces
                // the old TrackText/TrackTextHost cap, just measured from the tray box's own left
                // edge instead of from TrackTextHost's - see that method's own remarks.
                VuMeterFillLeft.Width = Math.Clamp(left, 0, 1) * _vuMeterMaxWidth;
                VuMeterFillRight.Width = Math.Clamp(right, 0, 1) * _vuMeterMaxWidth;
            }));
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

        private static TranslateTransform AttachTransportButtonAdorner(FrameworkElement spacer, FrameworkElement button, ref UIElementAdorner adorner)
        {
            AdornerLayer layer = AdornerLayer.GetAdornerLayer(spacer);
            if (layer == null)
            {
                return null;
            }

            // The slide transform goes on a plain Grid wrapping the button, not on the Adorner
            // itself - see UIElementAdorner's own remarks on why a RenderTransform set directly on
            // an Adorner doesn't work. Stretch (overriding MediaPlayerButtonStyle's own
            // VerticalAlignment="Center") instead of leaving the button Center-aligned inside this
            // wrapper: the wrapper is always arranged at exactly the button's own DesiredSize (see
            // UIElementAdorner.ArrangeOverride), so there's never really any leftover space to
            // center within, but Center still runs its (available-desired)/2 offset arithmetic
            // even when that's expected to land on exactly 0 - and at a fractional DPI scale that
            // computation landed a device pixel off often enough to visibly rest the buttons
            // 1-2px too high. Stretch skips that arithmetic entirely instead of relying on it to
            // round to zero.
            button.VerticalAlignment = VerticalAlignment.Stretch;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;

            var wrapper = new Grid();
            // Top, not the Grid default of Stretch: clipContainer below is deliberately made a
            // few px taller than the button (see ClipBottomSlack), and if wrapper were left to
            // Stretch into that extra height, the button's own explicit Height would win the
            // sizing fight but the leftover space would then split into equal top/bottom slack
            // (WPF centers a fixed-size element within a Stretch slot it can't actually fill) -
            // quietly shifting the button ~half the slack lower than its real resting position.
            // Top anchors wrapper (and so the button) flush with the top of clipContainer, exactly
            // as before slack existed, leaving 100% of the added room at the bottom where it's
            // actually needed.
            wrapper.VerticalAlignment = VerticalAlignment.Top;
            wrapper.Children.Add(button);
            var slide = new TranslateTransform();
            wrapper.RenderTransform = slide;

            // Wraps the slide wrapper in an outer container that clips to its own bounds - which
            // UIElementAdorner.ArrangeOverride would otherwise size to exactly the button's own
            // natural DesiredSize (Stretch above means that's never inflated by any centering
            // slop). Since Clip is evaluated in this container's own LOCAL space, BEFORE the
            // wrapper's RenderTransform is applied to what's inside it, the clip rect itself stays
            // fixed at that resting position while the button slides within/past it - so sliding
            // down visibly sinks the button below a hard edge and gets it swallowed there, instead
            // of just floating freely past the tray box with nothing to cut it off.
            //
            // ClipBottomSlack extends that clip window a few px past the button's own bare height
            // - a per-monitor MonitorOffset Y nudge on the button (e.g. the current +2ish DIPs
            // used to align it against the tray box) is a RenderTransform on the button itself,
            // applied entirely independently of this clip container's own size, so without this
            // slack the clip window (sized to the button's UN-nudged height) would permanently
            // slice off the bottom of the nudged-down button's own outline even at rest. Bound to
            // the real BUTTON's own ActualHeight, not the spacer's - the spacer's own tag
            // (MediaButtonPreviousSpacer/etc., separate from the button's own MediaButtonPrevious/
            // etc.) is only ever given Width/Margin overrides, never Height, so a per-monitor
            // custom Height (e.g. "MediaButtonPrevious": {"Height": 19}) only ever lands on the
            // button, never the spacer; binding to the spacer here previously ignored that
            // override entirely and re-derived a height from the spacer's own default, unrelated
            // size instead - which is why a requested 19 could never actually render as 19. Safe
            // from feedback: Height is explicit on the button (not Stretch-derived), so
            // button.ActualHeight never depends on how much room this container itself ends up
            // giving it.
            const double clipBottomSlack = 4;
            var clipContainer = new Grid { ClipToBounds = true };
            var clipHeightBinding = new MultiBinding { Converter = new SumWidthsConverter(), ConverterParameter = clipBottomSlack.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            clipHeightBinding.Bindings.Add(new Binding(nameof(ActualHeight)) { Source = button });
            clipContainer.SetBinding(FrameworkElement.HeightProperty, clipHeightBinding);
            clipContainer.Children.Add(wrapper);

            adorner = new UIElementAdorner(spacer, clipContainer, isHitTestVisible: true);
            layer.Add(adorner);
            return slide;
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
            _previousButtonSlide = null;
            _playPauseButtonSlide = null;
            _nextButtonSlide = null;
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
                Dispatcher.BeginInvoke(new Action(UpdateVuMeterOffset), System.Windows.Threading.DispatcherPriority.Loaded);
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
        /// Finds the taskbar's own ApplicationWindow for the current media session's source app -
        /// shared by ActivateSourceApp (click-to-activate) and UpdateAlbumArt (icon fallback when
        /// there's no album art).
        ///
        /// SMTC's SourceAppUserModelId is a real AppUserModelID for UWP apps, matching
        /// ApplicationWindow.AppUserModelID directly - but for a classic desktop app that never
        /// explicitly registered one (foobar2000, Spotify, Discord, ...; empirically most of
        /// them), it falls back to "{exe filename}.exe", which ManagedShell's AppUserModelID
        /// never populates (it stays "") - so that case is matched against the executable
        /// filename portion of WinFileName instead, which is populated for every window.
        /// </summary>
        private ApplicationWindow FindSourceAppWindow()
        {
            return FindAppWindow(_sessionManager?.SourceAppUserModelId);
        }

        // A browser's media session has an opaque AppUserModelID that no window carries, but the browser
        // puts the playing page's title in its window title - good enough to tell which app it is.
        private ApplicationWindow FindWindowByTitle(string mediaTitle)
        {
            if (string.IsNullOrWhiteSpace(mediaTitle) || mediaTitle.Length < 6 || ShellManager?.Tasks?.GroupedWindows == null)
            {
                return null;
            }

            foreach (object item in ShellManager.Tasks.GroupedWindows)
            {
                if (item is ApplicationWindow window && window.Title != null &&
                    window.Title.Contains(mediaTitle, StringComparison.OrdinalIgnoreCase))
                {
                    return window;
                }
            }

            return null;
        }

        // "Helium" (from "Helium.BRVOO...") -> a window of the app with that name, for its icon.
        private ApplicationWindow FindWindowByAppName(string appName)
        {
            if (string.IsNullOrWhiteSpace(appName) || ShellManager?.Tasks?.GroupedWindows == null)
            {
                return null;
            }

            foreach (object item in ShellManager.Tasks.GroupedWindows)
            {
                if (item is ApplicationWindow window &&
                    (string.Equals(window.WinFileDescription, appName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(System.IO.Path.GetFileNameWithoutExtension(window.WinFileName), appName, StringComparison.OrdinalIgnoreCase) ||
                     // a browser window's title ends with the browser's name: "New tab - Helium"
                     (window.Title != null && window.Title.EndsWith(" " + appName, StringComparison.OrdinalIgnoreCase))))
                {
                    return window;
                }
            }

            return null;
        }

        // A browser's AppUserModelID is a machine-generated hash ("BRVOONR7XJLVKDHZURXJZLAJOM"), not a name.
        private static bool IsOpaqueAppId(string aumid)
        {
            if (string.IsNullOrEmpty(aumid) || aumid.Length < 16)
            {
                return false;
            }

            foreach (char c in aumid)
            {
                if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
                {
                    return false;
                }
            }

            return true;
        }

        // For such an opaque ID, when the playing tab isn't the one its window's title shows: the browser
        // window itself still gives the name and icon. A Chromium window's title ends with the browser's
        // name ("New tab - Helium"), which tells browsers from other Chromium-based apps; only trusted
        // when exactly one browser is running, since nothing links the ID to a particular one.
        private ApplicationWindow FindBrowserWindow(string aumid)
        {
            if (!IsOpaqueAppId(aumid) || ShellManager?.Tasks?.GroupedWindows == null)
            {
                return null;
            }

            ApplicationWindow found = null;

            foreach (object item in ShellManager.Tasks.GroupedWindows)
            {
                if (item is not ApplicationWindow window || window.ClassName != "Chrome_WidgetWin_1" ||
                    string.IsNullOrWhiteSpace(window.WinFileDescription) || string.IsNullOrEmpty(window.Title) ||
                    !window.Title.EndsWith(window.WinFileDescription, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (found != null && !string.Equals(found.WinFileName, window.WinFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                found ??= window;
            }

            return found;
        }

        private ApplicationWindow FindAppWindow(string aumid)
        {
            if (string.IsNullOrEmpty(aumid) || ShellManager?.Tasks?.GroupedWindows == null)
            {
                return null;
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

                if (isMatch)
                {
                    return window;
                }
            }

            return null;
        }

        /// <summary>
        /// Toggles the current media session's app window - the same thing clicking its taskbar
        /// button would do: brings it to the foreground if it isn't already active, or minimizes
        /// it if it is (so clicking the album art a second time puts the app back out of the way
        /// instead of doing nothing).
        /// </summary>
        private void ActivateSourceApp(object sender, MouseButtonEventArgs e)
        {
            ApplicationWindow window = FindSourceAppWindow();
            if (window == null)
            {
                return;
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
        }

        #region Seek popup

        // Set right before SeekPopup.IsOpen flips to false because the SAME click that's about
        // to reach TrackTextCanvas_OnMouseLeftButtonUp already dismissed it (see
        // SeekPopup_OnClosed) - without this, clicking the track text a second time to close the
        // popup would instead close-then-immediately-reopen it: Popup's own StaysOpen="False"
        // dismisses it on that click before our handler even runs, so by the time our handler
        // checks IsOpen it already reads false and toggles it back open.
        private bool _ignoreNextTrackTextClick;

        // Right-click on the track text: choose which app's media to show when several have a session
        // (a browser tab and a music player, say) - or "Automatic" to follow whichever Windows says is
        // current. The choice is shared by every monitor's player (they share one session manager).
        private async void TrackTextCanvas_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            if (_sessionManager == null)
            {
                return;
            }

            MediaSessionManager manager = _sessionManager;
            System.Collections.Generic.List<MediaSessionInfo> sessions = await manager.GetSessionsAsync();

            string automaticLabel = TryFindResource("media_source_automatic") as string ?? "Automatic";
            string noneLabel = TryFindResource("media_source_none") as string ?? "No media playing";

            var menu = new ContextMenu
            {
                PlacementTarget = TrackTextCanvas,
                Placement = Settings.Instance.Edge switch
                {
                    ManagedShell.AppBar.AppBarEdge.Left => System.Windows.Controls.Primitives.PlacementMode.Right,
                    ManagedShell.AppBar.AppBarEdge.Right => System.Windows.Controls.Primitives.PlacementMode.Left,
                    ManagedShell.AppBar.AppBarEdge.Top => System.Windows.Controls.Primitives.PlacementMode.Bottom,
                    _ => System.Windows.Controls.Primitives.PlacementMode.Top,
                },
            };

            var automatic = new MenuItem
            {
                Header = automaticLabel,
                IsChecked = string.IsNullOrEmpty(Settings.Instance.MediaPreferredApp),
            };
            automatic.Click += (s, args) => manager.SelectApp("");
            menu.Items.Add(automatic);

            menu.Items.Add(MakeMenuSeparator());

            if (sessions.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = noneLabel, IsEnabled = false });
            }

            foreach (MediaSessionInfo info in sessions)
            {
                string title = string.IsNullOrEmpty(info.Title) ? "" : $" \u2014 {info.Title}";
                string state = info.State == MediaPlaybackState.Playing ? "  \u25B6" : "";

                // Some apps (browsers especially) use an opaque AppUserModelID; their window's own file
                // description ("Opera GX Internet Browser") is a better name than the ID.
                ApplicationWindow appWindow = FindAppWindow(info.AppUserModelId) ?? FindWindowByTitle(info.Title) ?? FindWindowByAppName(info.DisplayName) ?? FindBrowserWindow(info.AppUserModelId);
                string description = appWindow?.WinFileDescription;
                string appName = !string.IsNullOrWhiteSpace(description) ? description
                    : IsOpaqueAppId(info.AppUserModelId) ? (TryFindResource("media_source_browser") as string ?? "Browser")
                    : info.DisplayName;

                var item = new MenuItem
                {
                    Header = $"{appName}{title}{state}",
                    IsChecked = info.IsShown,
                };

                string aumid = info.AppUserModelId;
                item.Click += (s, args) => manager.SelectApp(aumid);
                menu.Items.Add(item);
            }

            menu.IsOpen = true;
        }

        // The theme's menus draw a plain Separator as empty space; this one is a visible bevelled line.
        private static Separator MakeMenuSeparator()
        {
            var line = new FrameworkElementFactory(typeof(StackPanel));
            line.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 1, 2.5, 1));

            foreach (string brush in new[] { "ButtonShadow", "ButtonHighlight" })
            {
                var rect = new FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
                rect.SetValue(FrameworkElement.HeightProperty, 1.0);
                rect.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
                line.AppendChild(rect);
            }

            return new Separator { Template = new ControlTemplate(typeof(Separator)) { VisualTree = line } };
        }

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
            Dispatcher.BeginInvoke(new Action(UpdateVuMeterOffset));
        }
    }
}
