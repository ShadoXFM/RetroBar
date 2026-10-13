using ManagedShell;
using ManagedShell.Common.Logging;
using ManagedShell.WindowsTasks;
using RetroBar.Converters;
using RetroBar.Utilities;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace RetroBar.Controls
{
    /// <summary>
    /// The audio visualizers: the VU meter behind the track text and the spectrum in the popup, and the colour the latter takes from the cover.
    /// </summary>
    public partial class MediaPlayer
    {
        // Capture only runs while there's an active session to visualize, started/stopped
        // alongside the transport buttons themselves in UpdateFromSession/Stop. Feeds the VU
        // meter behind the track text (VisualizerCapture_OnLevelUpdated) - the only visualizer
        // RetroBar has left, now that the old in-place and relocated-spectrum-bars displays are
        // both gone.
        private AudioSpectrumCapture _visualizerCapture;

        // The Windows accent color at full saturation read as too strong/colorful for a subtle
        // background fill - blending it toward its own perceptual gray (rather than just adding
        // transparency, which is Opacity's own separate job - see VuMeterAccentBrush's own
        // remarks in the XAML) mutes the color itself while keeping it recognizably tinted.
        // Mutates the brush's Color in place, so every {DynamicResource VuMeterAccentBrush}
        // usage picks it up without needing the resource entry itself replaced. Computed once
        // (SystemColors.HighlightColor itself is a static read, not something that would ever
        // change without the whole app restarting anyway).
        private const double VuMeterAccentDesaturation = 0.95;

        // The most common colour of the cover art, for a gradient on the spectrum's bars: for Spotify's art only, none for any other player.
        private Color? _seekAccentColor;

        private ImageSource _seekAccentSource;

        private static bool IsSpotify(string aumid) => aumid != null && aumid.Contains("spotify", StringComparison.OrdinalIgnoreCase);

        private void UpdateSeekAccent(ImageSource art)
        {
            if (!IsSpotify(_sessionManager?.SourceAppUserModelId) || art == null)
            {
                _seekAccentSource = null;
                _seekAccentColor = null;
            }
            else if (!ReferenceEquals(art, _seekAccentSource))
            {
                _seekAccentSource = art;
                _seekAccentColor = CoverColor.MostCommon(art);
            }

            ApplySeekSpectrumFill();
        }

        // White at the bottom of the spectrum, the cover's colour at the top; the text's own colour for every other player.
        private void ApplySeekSpectrumFill()
        {
            if (_seekAccentColor is Color accent && SeekTitleText.Foreground is SolidColorBrush foreground)
            {
                // Full colour from 70% of the way up, so the bars that are not the tallest ones have it at their tops too.
                var brush = new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(foreground.Color, 0),
                    new GradientStop(accent, 0.7),
                    new GradientStop(accent, 1),
                }, new Point(0, 1), new Point(0, 0));
                brush.Freeze();
                SeekSpectrum.Fill = brush;
            }
            else
            {
                SeekSpectrum.Fill = SeekTitleText.Foreground;
            }
        }

        // The capture is opened and closed on a pool thread (opening an audio device takes a good part of a second, which on the UI thread
        // stopped everything that moves, the sliding text included, for that long) and, once paused, kept open for a while: pausing and
        // playing again does not open it again each time.
        private static readonly TimeSpan VisualizerPausedGrace = TimeSpan.FromSeconds(15);
        private DispatcherTimer _visualizerStopTimer;

        private void StartVisualizer()
        {
            _visualizerStopTimer?.Stop();

            try
            {
                if (_visualizerCapture != null)
                {
                    return;
                }

                var capture = new AudioSpectrumCapture();
                capture.LevelUpdated += VisualizerCapture_OnLevelUpdated;
                capture.SpectrumUpdated += VisualizerCapture_OnSpectrumUpdated;
                _visualizerCapture = capture;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        capture.Start();
                    }
                    catch (Exception ex)
                    {
                        ShellLogger.Error($"MediaPlayer: starting the audio capture threw: {ex}");
                    }
                });
            }
            catch (Exception ex)
            {
                ShellLogger.Error($"MediaPlayer: StartVisualizer threw: {ex}");
            }
        }

        // Paused: the capture goes after a while, if nothing plays again by then.
        private void ScheduleVisualizerStop()
        {
            if (_visualizerCapture == null)
            {
                return;
            }

            _visualizerStopTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = VisualizerPausedGrace };
            _visualizerStopTimer.Tick -= VisualizerStopTimer_OnTick;
            _visualizerStopTimer.Tick += VisualizerStopTimer_OnTick;
            _visualizerStopTimer.Stop();
            _visualizerStopTimer.Start();
        }

        private void VisualizerStopTimer_OnTick(object sender, EventArgs e)
        {
            _visualizerStopTimer.Stop();
            StopVisualizer();
        }

        private void StopVisualizer()
        {
            _visualizerStopTimer?.Stop();

            AudioSpectrumCapture capture = _visualizerCapture;
            if (capture == null)
            {
                return;
            }

            capture.LevelUpdated -= VisualizerCapture_OnLevelUpdated;
            capture.SpectrumUpdated -= VisualizerCapture_OnSpectrumUpdated;
            _visualizerCapture = null;
            System.Threading.Tasks.Task.Run(capture.Dispose);
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

        // The spectrum in the seek popup: each bar is 3 device pixels wide with 1 between, as tall as the title and artist together plus 4 DIP, a
        // pixel at the least (the bar's baseline). A bar rises at once but sinks gradually, so it doesn't flicker. Only drawn while
        // the popup is open.
        private const int SeekSpectrumBarWidth = 3;

        private const int SeekSpectrumBarGap = 1;

        private readonly float[] _seekSpectrumLevels = new float[AudioSpectrumCapture.SpectrumBands];

        // A cap a pixel high stays where each bar peaked and falls slowly. With nothing playing the bars sink to a dash each, and the dashes
        // go one after the other from the left (see SeekSpectrumDashLife), which is all that is left of the spectrum when it is still.
        private readonly float[] _seekSpectrumPeaks = new float[AudioSpectrumCapture.SpectrumBands];

        private DateTime? _seekSpectrumQuietSince;

        private const double SeekSpectrumDashLife = 500;

        private const double SeekSpectrumDashStagger = 60;

        private void VisualizerCapture_OnSpectrumUpdated(float[] bands)
        {
            // Fires on the capture thread.
            Dispatcher.BeginInvoke(new Action(() => UpdateSeekSpectrum(bands)), DispatcherPriority.Background);
        }

        private DateTime _lastSeekSpectrumUpdate = DateTime.MinValue;

        // The popup's setting (Properties window): the spectrum.
        private void ApplyMediaPopupSettings()
        {
            SeekSpectrumHost.Visibility = Settings.Instance.ShowMediaPopupSpectrum ? Visibility.Visible : Visibility.Collapsed;
        }

        // The VU meter (Properties window): behind the track text while a track plays. The audio is captured while the meter shows or the
        // popup is open (for its spectrum).
        private void ApplyVuMeterSetting()
        {
            bool isPlaying = _sessionManager?.PlaybackState == MediaPlaybackState.Playing;
            UpdateVisualizerActiveState(isPlaying);
            UpdateVisualizerCapture(isPlaying);
        }

        private void UpdateVisualizerCapture(bool isPlaying)
        {
            if (isPlaying && (Settings.Instance.ShowVuMeter || SeekPopup.IsOpen))
            {
                StartVisualizer();
            }
            else
            {
                ScheduleVisualizerStop();
            }
        }

        private void UpdateSeekSpectrum(float[] bands)
        {
            if (!SeekPopup.IsOpen || !Settings.Instance.ShowMediaPopupSpectrum)
            {
                return;
            }

            _lastSeekSpectrumUpdate = DateTime.UtcNow;

            bool playing = _sessionManager?.PlaybackState == MediaPlaybackState.Playing;
            bool sound = false;

            for (int i = 0; i < _seekSpectrumLevels.Length; i++)
            {
                float level = playing && bands != null ? bands[i] : 0;
                sound |= level > 0.05f;
                _seekSpectrumLevels[i] = Math.Max(level, _seekSpectrumLevels[i] - 0.08f);
                _seekSpectrumPeaks[i] = Math.Max(_seekSpectrumLevels[i], _seekSpectrumPeaks[i] - (sound ? 0.02f : 0.05f));
            }

            double quietMs = 0;
            if (sound)
            {
                _seekSpectrumQuietSince = null;
            }
            else
            {
                _seekSpectrumQuietSince ??= DateTime.UtcNow;
                quietMs = (DateTime.UtcNow - _seekSpectrumQuietSince.Value).TotalMilliseconds;
            }

            DpiScale dpi = GetSeekDpi();
            int count = _seekSpectrumLevels.Length;
            int width = count * SeekSpectrumBarWidth + (count - 1) * SeekSpectrumBarGap;
            int height = Math.Max(2, (int)Math.Round((SeekTextPanel.ActualHeight + 4) * dpi.DpiScaleY));

            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                // The gradient fills the geometry's bounds: these two points make them the whole box, not just the tallest bar.
                context.BeginFigure(new Point(0, 0), true, true);
                context.LineTo(new Point(0, 0), false, false);
                context.BeginFigure(new Point(width, height), true, true);
                context.LineTo(new Point(width, height), false, false);

                for (int i = 0; i < count; i++)
                {
                    // Once it has been quiet for a while a bar's dash goes too, each a little after the one on its left.
                    if (quietMs > SeekSpectrumDashLife + i * SeekSpectrumDashStagger)
                    {
                        continue;
                    }

                    int barHeight = Math.Clamp((int)Math.Round(_seekSpectrumLevels[i] * height), 1, height);
                    int x = i * (SeekSpectrumBarWidth + SeekSpectrumBarGap);
                    context.BeginFigure(new Point(x, height - barHeight), true, true);
                    context.LineTo(new Point(x + SeekSpectrumBarWidth, height - barHeight), true, false);
                    context.LineTo(new Point(x + SeekSpectrumBarWidth, height), true, false);
                    context.LineTo(new Point(x, height), true, false);

                    // The cap, a pixel high, when the bar has fallen a couple of pixels below where it peaked.
                    int capTop = height - Math.Clamp((int)Math.Round(_seekSpectrumPeaks[i] * height), 1, height);
                    if (capTop + 2 <= height - barHeight)
                    {
                        context.BeginFigure(new Point(x, capTop), true, true);
                        context.LineTo(new Point(x + SeekSpectrumBarWidth, capTop), true, false);
                        context.LineTo(new Point(x + SeekSpectrumBarWidth, capTop + 1), true, false);
                        context.LineTo(new Point(x, capTop + 1), true, false);
                    }
                }
            }

            geometry.Transform = new ScaleTransform(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY);
            geometry.Freeze();
            SeekSpectrum.Width = width / dpi.DpiScaleX;
            SeekSpectrum.Height = height / dpi.DpiScaleY;
            SeekSpectrum.Data = geometry;
        }
    }
}
