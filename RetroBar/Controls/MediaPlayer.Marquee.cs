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
    /// The track text that slides when it is too long for its room (the taskbar's and the popup's), the fades at its ends and the speed per monitor.
    /// </summary>
    public partial class MediaPlayer
    {
        // Marquee tuning.
        private const double MarqueePixelsPerSecond = 30d;

        private const double MarqueeEndPauseSeconds = 2d;

        // True when they changed.
        private bool LoadMarqueeSettings()
        {
            string deviceName = (Window.GetWindow(this) as Taskbar)?.Screen.DeviceName;
            MonitorOffsetValue value = MonitorAdjustments.Get(deviceName, "MediaTrackText");
            double speed = value.ScrollSpeed is > 0 ? value.ScrollSpeed.Value : MarqueePixelsPerSecond;
            double pause = value.ScrollPause is >= 0 ? value.ScrollPause.Value : MarqueeEndPauseSeconds;
            bool changed = speed != _marqueeSpeed || pause != _marqueePause;
            _marqueeSpeed = speed;
            _marqueePause = pause;
            return changed;
        }

        // Ignore sub-pixel overflow so we don't animate text that already fits.
        private const double MarqueeOverflowThreshold = 1d;

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
        // The most the track text shows before it slides: the style's own MaxWidth, or a monitor's own "MediaTrackTextMax" Width in
        // monitor-adjustments.json (the same text takes a little different room at another scale, from how the font rounds to pixels).
        private double GetTrackTextMaxWidth()
        {
            string deviceName = (Window.GetWindow(this) as Taskbar)?.Screen.DeviceName;
            return MonitorAdjustments.Get(deviceName, "MediaTrackTextMax").Width ?? TrackTextHost.MaxWidth;
        }

        // The font's line height can be shorter than its tallest glyphs (g, j, p, y hang below it), so the
        // clip that keeps the marquee inside the row leaves room under the text for them.
        private const double TrackTextDescenderRoom = 4;

        private void TrackTextClip_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            TrackTextClip.Clip = new RectangleGeometry(new Rect(0, 0, TrackTextClip.ActualWidth, TrackTextClip.ActualHeight + TrackTextDescenderRoom));
            UpdateTrackTextFadeOffsets();
        }

        // A line too long for its room fades out at the end it is sliding away from or towards (not at the end that is showing all of it), so
        // the text neither starts nor ends in a faded edge. How much of each fade there is follows where the slide is (read on every frame,
        // so it can not be out of step with it): the taskbar's text has a mask on the text only (the VU meter behind it is not faded), and
        // the popup's lines have a gradient over each end in the popup's colour.
        private const double MarqueeFadeWidth = 32;

        // The taskbar's text fades over less (a smaller text in a smaller room).
        private const double TrackTextFadeWidth = 22;

        private LinearGradientBrush _trackTextMask;

        private GradientStop _trackTextMaskLeft;

        private GradientStop _trackTextMaskRight;

        private bool _fadeRenderingHooked;

        private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

        private void EnsureFadeRendering()
        {
            if (!_fadeRenderingHooked)
            {
                _fadeRenderingHooked = true;
                CompositionTarget.Rendering += FadeRendering;
            }
        }

        private void FadeRendering(object sender, EventArgs e)
        {
            UpdateFades();

            // Only while a line slides: every frame of an idle taskbar is a frame it would not have to draw.
            if (_trackTextMask == null && !AnySeekLineSliding())
            {
                CompositionTarget.Rendering -= FadeRendering;
                _fadeRenderingHooked = false;
            }
        }

        private bool AnySeekLineSliding()
        {
            return SeekPopup.IsOpen &&
                   ((_seekMarqueeOverflow.TryGetValue(SeekTitleText, out double title) && title > 0) ||
                    (_seekMarqueeOverflow.TryGetValue(SeekArtistText, out double artist) && artist > 0));
        }

        private void UpdateFades()
        {
            if (_trackTextMask != null)
            {
                // The text is at TrackTextTransform.X (0 to -overflow): the fade at the left end is as far as it has slid, at the right
                // end as far as is left to slide.
                double x = TrackTextTransform.X;
                _trackTextMaskLeft.Color = Color.FromArgb((byte)Math.Round(255 * (1 - Clamp01(-x / TrackTextFadeWidth))), 0, 0, 0);
                _trackTextMaskRight.Color = Color.FromArgb((byte)Math.Round(255 * (1 - Clamp01((_marqueeOverflow + x) / TrackTextFadeWidth))), 0, 0, 0);
            }

            if (SeekPopup.IsOpen)
            {
                UpdateSeekFade(SeekTitleText, SeekTitleFadeLeft, SeekTitleFade);
                UpdateSeekFade(SeekArtistText, SeekArtistFadeLeft, SeekArtistFade);
            }
        }

        private void UpdateSeekFade(TextBlock text, Rectangle fadeLeft, Rectangle fadeRight)
        {
            if (!_seekMarqueeOverflow.TryGetValue(text, out double overflow) || overflow <= 0)
            {
                return;
            }

            double x = Canvas.GetLeft(text);
            fadeLeft.Opacity = Clamp01(-x / MarqueeFadeWidth);
            fadeRight.Opacity = Clamp01((overflow + x) / MarqueeFadeWidth);
        }

        private void StartTrackTextFade()
        {
            double width = TrackTextClip.ActualWidth;
            if (width <= TrackTextFadeWidth * 2)
            {
                StopTrackTextFade();
                return;
            }

            _trackTextMaskLeft = new GradientStop(Colors.Black, 0);
            _trackTextMaskRight = new GradientStop(Colors.Black, 1);
            _trackTextMask = new LinearGradientBrush(new GradientStopCollection
            {
                _trackTextMaskLeft,
                new GradientStop(Colors.Black, TrackTextFadeWidth / width),
                new GradientStop(Colors.Black, 1 - TrackTextFadeWidth / width),
                _trackTextMaskRight,
            }, new Point(0, 0), new Point(1, 0));

            // In DIPs of the clip, not relative to the element's bounds: those include the whole text, which is much wider than the clip
            // (it sits on a Canvas), so the fade did not reach the end of what shows.
            _trackTextMask.MappingMode = BrushMappingMode.Absolute;
            _trackTextMask.EndPoint = new Point(width, 0);
            for (int i = 1; i <= 2; i++)
            {
                _trackTextMask.GradientStops[i].Offset = i == 1 ? TrackTextFadeWidth / width : 1 - TrackTextFadeWidth / width;
            }

            TrackTextClip.OpacityMask = _trackTextMask;
            EnsureFadeRendering();
            UpdateFades();
        }

        private void StopTrackTextFade()
        {
            _trackTextMask = null;
            TrackTextClip.ClearValue(UIElement.OpacityMaskProperty);
        }

        // The fades keep their width in DIPs when the clip's width changes.
        private void UpdateTrackTextFadeOffsets()
        {
            double width = TrackTextClip.ActualWidth;
            if (_trackTextMask == null && _marqueeRunning && width > TrackTextFadeWidth * 2)
            {
                // The slide began before the clip had a width, so it started without a mask.
                StartTrackTextFade();
                return;
            }

            if (_trackTextMask == null || width <= TrackTextFadeWidth * 2)
            {
                return;
            }

            _trackTextMask.EndPoint = new Point(width, 0);
            _trackTextMask.GradientStops[1].Offset = TrackTextFadeWidth / width;
            _trackTextMask.GradientStops[2].Offset = 1 - TrackTextFadeWidth / width;
        }

        private double _trackTextMarginWidth;

        // The text's Margin changed (its file was saved): the host's width has to follow.
        private void TrackTextCanvas_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (Math.Abs(TrackTextCanvas.Margin.Left + TrackTextCanvas.Margin.Right - _trackTextMarginWidth) > 0.01)
            {
                Dispatcher.BeginInvoke(new Action(UpdateMarquee), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

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
            LoadMarqueeSettings();

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

            double max = GetTrackTextMaxWidth();
            double visibleWidth = double.IsInfinity(max) || double.IsNaN(max) || max <= 0
                ? fullWidth
                : Math.Min(fullWidth, max);

            // A Margin on the text (monitor-adjustments.json, MediaTrackText) is room around it, not a cut into it: the host is wider by it.
            _trackTextMarginWidth = TrackTextCanvas.Margin.Left + TrackTextCanvas.Margin.Right;
            double hostWidth = Math.Max(1, visibleWidth + _trackTextMarginWidth);
            if (double.IsNaN(TrackTextHost.Width) || Math.Abs(TrackTextHost.Width - hostWidth) > 0.5)
            {
                TrackTextHost.Width = hostWidth;
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

        private DoubleAnimationUsingKeyFrames CreateMarqueeAnimation(double overflow)
        {
            double travelSeconds = Math.Max(overflow / _marqueeSpeed, 0.5);

            TimeSpan pause = TimeSpan.FromSeconds(_marqueePause);
            TimeSpan travel = TimeSpan.FromSeconds(travelSeconds);

            TimeSpan atStartEnd = pause;
            TimeSpan atEnd = atStartEnd + travel;
            TimeSpan atEndPause = atEnd + pause;
            TimeSpan atFinish = atEndPause + travel;

            var animation = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(atFinish),
                RepeatBehavior = RepeatBehavior.Forever
            };

            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(atStartEnd)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(atEnd)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(atEndPause)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(atFinish)));

            return animation;
        }

        private void StartMarquee(double overflow)
        {
            DoubleAnimationUsingKeyFrames animation = CreateMarqueeAnimation(overflow);

            TrackTextTransform.BeginAnimation(TranslateTransform.XProperty, animation);

            _marqueeRunning = true;
            _marqueeOverflow = overflow;
            StartTrackTextFade();
        }

        // The seek popup's title and artist slide the same way, along their Canvases (Canvas.Left, so a position set for them in the
        // monitor file, which is a RenderTransform, is left alone).
        private readonly System.Collections.Generic.Dictionary<TextBlock, double> _seekMarqueeOverflow = new();

        private void SeekClip_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var clip = (Border)sender;
            clip.Clip = new RectangleGeometry(new Rect(0, 0, clip.ActualWidth, clip.ActualHeight + TrackTextDescenderRoom));
            Dispatcher.BeginInvoke(new Action(() => UpdateSeekMarquees(false)), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void SeekText_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateSeekMarquees(false)), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        // The taskbar's text and the popup's lines start their slides at the same moment (each waiting at its start for the same time, and
        // sliding at the same speed: how long that takes to the end is each line's own, by how much of it is cut off).
        private void SyncMarquees()
        {
            if (_marqueeRunning)
            {
                StartMarquee(_marqueeOverflow);
            }

            UpdateSeekMarquees(true);
        }

        private void UpdateSeekMarquees(bool restart)
        {
            LoadMarqueeSettings();
            UpdateSeekMarquee(SeekTitleText, SeekTitleClip, restart);
            UpdateSeekMarquee(SeekArtistText, SeekArtistClip, restart);
        }

        private void UpdateSeekMarquee(TextBlock text, Border clip, bool restart)
        {
            double overflow = Math.Ceiling(Math.Ceiling(text.ActualWidth) - clip.ActualWidth);
            if (clip.ActualWidth <= 0 || overflow <= MarqueeOverflowThreshold)
            {
                overflow = 0;
            }

            Rectangle fadeRight = text == SeekTitleText ? SeekTitleFade : SeekArtistFade;
            Rectangle fadeLeft = text == SeekTitleText ? SeekTitleFadeLeft : SeekArtistFadeLeft;
            Visibility fadeVisibility = overflow > 0 ? Visibility.Visible : Visibility.Collapsed;
            fadeRight.Visibility = fadeVisibility;
            fadeLeft.Visibility = fadeVisibility;
            if (overflow > 0 && TryFindResource("ButtonFace") is SolidColorBrush face)
            {
                Color color = face.Color;
                Color clear = Color.FromArgb(0, color.R, color.G, color.B);
                fadeRight.Fill = new LinearGradientBrush(clear, color, new Point(0, 0), new Point(1, 0));
                fadeLeft.Fill = new LinearGradientBrush(color, clear, new Point(0, 0), new Point(1, 0));
            }

            _seekMarqueeOverflow.TryGetValue(text, out double running);
            if (!restart && Math.Abs(running - overflow) < 0.5)
            {
                return;
            }

            _seekMarqueeOverflow[text] = overflow;
            text.BeginAnimation(Canvas.LeftProperty, null);
            Canvas.SetLeft(text, 0);
            fadeLeft.Opacity = 0;
            fadeRight.Opacity = 1;

            if (overflow > 0)
            {
                text.BeginAnimation(Canvas.LeftProperty, CreateMarqueeAnimation(overflow));
                EnsureFadeRendering();
            }
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
            StopTrackTextFade();
        }
    }
}
