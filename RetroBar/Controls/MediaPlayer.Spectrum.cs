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

        // Spotify's own app, and Spotifast (another client of the same service, with its own media session and the same tracks).
        private static bool IsSpotify(string aumid) => aumid != null
            && (aumid.Contains("spotify", StringComparison.OrdinalIgnoreCase) || aumid.Contains("spotifast", StringComparison.OrdinalIgnoreCase));

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
                _seekAccentColor = CoverColor.Vibrant(art);
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

            // What has been played of the track, in the groove of the seek bar, is in the cover's colour too.
            if (AccentBrush() is SolidColorBrush progress)
            {
                progress.Freeze();
                SeekSlider.Resources["MediaSeekProgress"] = progress;
            }
            else
            {
                SeekSlider.Resources.Remove("MediaSeekProgress");
            }

            // The artist's name is in the cover's colour.
            if (AccentBrush(ArtistAccentLighten) is SolidColorBrush artist)
            {
                artist.Freeze();
                SeekArtistText.Foreground = artist;
            }
            else
            {
                SeekArtistText.ClearValue(TextBlock.ForegroundProperty);
            }

            ApplyVuMeterFill();
            ApplyLyricsColor();
        }

        // The VU meter's bars go from the cover's colour at their left to a darker grey at their tip (as translucent as the accent they
        // have otherwise: the system's accent colour, muted). Every other player's stay that.
        private void ApplyVuMeterFill()
        {
            if (_seekAccentColor is Color accent && TryFindResource("ClockForeground") is SolidColorBrush foreground)
            {
                double opacity = (TryFindResource("VuMeterAccentBrush") as Brush)?.Opacity ?? 0.2;
                // The tip is the text's colour at half its strength, at 60% of that; the cover's colour at the left is at its full strength.
                Color tip = Color.FromArgb(153, (byte)(foreground.Color.R / 2), (byte)(foreground.Color.G / 2), (byte)(foreground.Color.B / 2));
                Color left = Color.FromArgb(255, accent.R, accent.G, accent.B);
                var brush = new LinearGradientBrush(left, tip, new Point(0, 0), new Point(1, 0)) { Opacity = opacity };
                brush.Freeze();
                VuMeterFillLeft.Fill = brush;
                VuMeterFillRight.Fill = brush;
            }
            else
            {
                VuMeterFillLeft.SetResourceReference(Shape.FillProperty, "VuMeterAccentBrush");
                VuMeterFillRight.SetResourceReference(Shape.FillProperty, "VuMeterAccentBrush");
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
                // The meters are drawn in every frame, from the last two readings (the audio is analysed every 40 ms).
                CompositionTarget.Rendering -= VisualizerFrame;
                CompositionTarget.Rendering += VisualizerFrame;
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
            CompositionTarget.Rendering -= VisualizerFrame;
            lock (_vizLock)
            {
                _vuStamp = 0;
                _spectrumStamp = 0;
                _spectrumTo = null;
            }

            System.Threading.Tasks.Task.Run(capture.Dispose);
        }

        // Fires on AudioSpectrumCapture's own capture thread, not the UI thread. Only actually
        // moves the bars when the VU-meter style is selected and VuMeterContainer is currently
        // Visible (see UpdateVisualizerActiveState) - no need to keep computing a Width nobody can
        // see, and this way a track that's paused (Visibility already Collapsed) doesn't leave
        // either bar holding whatever width it last had once playback resumes and they reappear.
        private void VisualizerCapture_OnLevelUpdated(float left, float right)
        {
            // Only notes the reading: what is drawn goes from the last one to this one over the time between them, in every frame (VisualizerFrame).
            lock (_vizLock)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_vuStamp != 0)
                {
                    _vuIntervalMs = Math.Clamp((now - _vuStamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 4, 100);
                }

                _vuLeftFrom = _vuLeftShown;
                _vuRightFrom = _vuRightShown;
                _vuLeftTo = left;
                _vuRightTo = right;
                _vuStamp = now;
            }
        }

        // A bar rises towards the reading with this time constant (it is 63% of the way there after this long, 90% after twice that), and falls
        // as the reading does (those fall smoothly already).
        private const double VisualizerAttackMs = 4;

        private long _vizFrameStamp;

        // When the sound starts the bars glide up to the level, as they glide down to nothing when it stops: for a while they rise towards the
        // reading with a long time constant (63% of the way after VuMeterRiseTauMs), not at once, and then as they always do. What is drawn is
        // still the reading, not the reading scaled, so the bars move as they do in play.
        private const double VuMeterRiseMs = 300;
        private const double VuMeterRiseTauMs = 55;
        private long _vuRiseStamp;

        private void StartVuMeterRise()
        {
            lock (_vizLock)
            {
                // (What the bars showed before it stopped is not what the first frames are to start from.)
                _vuLeftShown = _vuRightShown = _vuLeftTo = _vuRightTo = _vuLeftFrom = _vuRightFrom = 0;
            }

            _vuLeftScale.ScaleX = 0;
            _vuRightScale.ScaleX = 0;
            _vuRiseStamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private bool VuMeterRising(long now)
        {
            if (_vuRiseStamp == 0)
            {
                return false;
            }

            if ((now - _vuRiseStamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= VuMeterRiseMs)
            {
                _vuRiseStamp = 0;
                return false;
            }

            return true;
        }

        // A rise goes towards the reading by the time constant; a fall goes on in every frame, reaching the reading when the next is due (readings
        // can come as seldom as every 50 ms, and a bar that waited for each to step down moved at their rate, not the screen's).
        private static float Smooth(float shown, float target, float attack, double frameMs, double remainingMs)
        {
            if (target > shown)
            {
                return shown + (target - shown) * attack;
            }

            return shown + (target - shown) * (float)Math.Min(1, frameMs / Math.Max(frameMs, remainingMs));
        }

        private readonly object _vizLock = new();
        private float _vuLeftFrom, _vuRightFrom, _vuLeftTo, _vuRightTo, _vuLeftShown, _vuRightShown;
        private long _vuStamp;
        private double _vuIntervalMs = 40;
        private float[] _spectrumTo;
        private readonly float[] _spectrumFrom = new float[AudioSpectrumCapture.SpectrumBands];
        private readonly float[] _spectrumShown = new float[AudioSpectrumCapture.SpectrumBands];
        private long _spectrumStamp;
        private double _spectrumIntervalMs = 40;

        // Every frame: the bars of the VU meter and of the popup's spectrum are where they are between the last two readings, so they move as
        // smoothly as the screen can show, not in steps of the 40 ms the audio is analysed at (a reading is shown one interval after it is taken).
        private void VisualizerFrame(object sender, EventArgs e)
        {
            if (_visualizerCapture == null || RetroBar.Utilities.PopupSlide.ActiveWindowSlides > 0)
            {
                return;
            }

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            // How much of the way to the reading a rise goes in this frame: the same whatever the frame rate (a short time constant), so the
            // bars rise quickly and without the jitter of jumping to every reading, which now come every 8 and 20 ms.
            double frameMs = _vizFrameStamp == 0 ? 8 : Math.Min(50, (now - _vizFrameStamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _vizFrameStamp = now;
            float attack = (float)(1 - Math.Exp(-frameMs / VisualizerAttackMs));

            if (VuMeterContainer.Visibility == Visibility.Visible && !_vuMeterFading)
            {
                float left, right;
                lock (_vizLock)
                {
                    if (_vuStamp == 0)
                    {
                        return;
                    }

                    double remaining = _vuIntervalMs - (now - _vuStamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    float vuAttack = VuMeterRising(now) ? (float)(1 - Math.Exp(-frameMs / VuMeterRiseTauMs)) : attack;
                    left = _vuLeftShown = Smooth(_vuLeftShown, _vuLeftTo, vuAttack, frameMs, remaining);
                    right = _vuRightShown = Smooth(_vuRightShown, _vuRightTo, vuAttack, frameMs, remaining);
                }

                _vuLeftScale.ScaleX = Math.Clamp(left, 0, 1);
                _vuRightScale.ScaleX = Math.Clamp(right, 0, 1);
            }

            if (SeekPopup.IsOpen)
            {
                float[] bands = null;
                lock (_vizLock)
                {
                    if (_spectrumTo != null && _spectrumStamp != 0)
                    {
                        double remaining = _spectrumIntervalMs - (now - _spectrumStamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        for (int i = 0; i < _spectrumShown.Length; i++)
                        {
                            _spectrumShown[i] = Smooth(_spectrumShown[i], _spectrumTo[i], attack, frameMs, remaining);
                        }

                        bands = _spectrumShown;
                    }
                }

                UpdateSeekSpectrum(bands);
            }
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

        // A bar rises at once and falls by this much of its height per second (the whole bar in a second and a half).
        private const double SeekSpectrumFallPerSecond = 0.65;

        private const double SeekSpectrumDashLife = 500;

        private const double SeekSpectrumDashStagger = 60;

        // When sound comes back the bars that have gone (their dash too) come back one after the other from the left, each a little after the
        // one on its left, as they went; the ones that have not gone yet are just there.
        private const double SeekSpectrumAppearStagger = 22;

        private readonly bool[] _seekBarGone = new bool[AudioSpectrumCapture.SpectrumBands];

        private readonly DateTime[] _seekBarAppearAt = new DateTime[AudioSpectrumCapture.SpectrumBands];

        // Set when the popup opens on a track that is playing: the bars are simply there then, they do not appear one by one.
        private bool _seekSpectrumSkipAppear;

        private void VisualizerCapture_OnSpectrumUpdated(float[] bands)
        {
            // Fires on the capture thread. Only notes the reading (see VisualizerFrame).
            lock (_vizLock)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_spectrumStamp != 0)
                {
                    _spectrumIntervalMs = Math.Clamp((now - _spectrumStamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 4, 100);
                }

                if (_spectrumTo == null)
                {
                    Array.Copy(bands, _spectrumShown, Math.Min(bands.Length, _spectrumShown.Length));
                }

                Array.Copy(_spectrumShown, _spectrumFrom, _spectrumFrom.Length);
                _spectrumTo = (float[])bands.Clone();
                _spectrumStamp = now;
            }
        }

        private DateTime _lastSeekSpectrumUpdate = DateTime.MinValue;

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
            if (!SeekPopup.IsOpen)
            {
                return;
            }

            // The bars fall by the same amount per second however often this is called.
            double elapsed = Math.Min((DateTime.UtcNow - _lastSeekSpectrumUpdate).TotalSeconds, 0.2);
            float fall = (float)(SeekSpectrumFallPerSecond * elapsed);
            _lastSeekSpectrumUpdate = DateTime.UtcNow;

            bool playing = _sessionManager?.PlaybackState == MediaPlaybackState.Playing;
            bool sound = false;

            for (int i = 0; i < _seekSpectrumLevels.Length; i++)
            {
                float level = playing && bands != null ? bands[i] : 0;
                sound |= level > 0.05f;
                _seekSpectrumLevels[i] = Math.Max(level, _seekSpectrumLevels[i] - fall);
                // (By time: a step of 0.02 or 0.05 was for the 40 ms the readings came at.)
                _seekSpectrumPeaks[i] = Math.Max(_seekSpectrumLevels[i], _seekSpectrumPeaks[i] - (sound ? 0.02f : 0.05f) * (float)(elapsed / 0.04));
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

            int count = _seekSpectrumLevels.Length;
            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < count; i++)
            {
                if (quietMs > SeekSpectrumDashLife + i * SeekSpectrumDashStagger)
                {
                    _seekBarGone[i] = true;
                }
            }

            if (sound && Array.IndexOf(_seekBarGone, true) >= 0)
            {
                // Sound is back: the bars that are gone come back. After the whole spectrum has gone they come from the left; while it is still
                // vanishing (from the left) it goes back the way it went: the last to go is the first to return.
                bool vanishing = Array.IndexOf(_seekBarGone, false) >= 0;
                int appearing = 0;
                for (int n = 0; n < count; n++)
                {
                    int i = vanishing ? count - 1 - n : n;
                    if (_seekBarGone[i])
                    {
                        _seekBarGone[i] = false;
                        _seekBarAppearAt[i] = _seekSpectrumSkipAppear ? DateTime.MinValue : now.AddMilliseconds(appearing * SeekSpectrumAppearStagger);
                        appearing++;
                    }
                }
            }

            if (sound)
            {
                _seekSpectrumSkipAppear = false;
            }

            DpiScale dpi = GetSeekDpi();
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
                    if (_seekBarGone[i] || now < _seekBarAppearAt[i])
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

