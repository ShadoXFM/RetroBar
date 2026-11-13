using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RetroBar.Utilities
{
    /// <summary>The side of the popup's window its content slides away to when hidden (the side of the taskbar's edge).</summary>
    public enum SlideDirection
    {
        Down,
        Up,
        Left,
        Right,
    }

    /// <summary>
    /// The slide of a popup out of the taskbar's edge and back into it, shared by the media popup and the weather forecast: the content
    /// of the popup's window (slideHost) moves inside the window, which stays where it is and clips what is outside it, from a window's
    /// size away on the side of the taskbar - so it comes from the edge, without a fade.
    ///
    /// While it slides the popup can also be clipped (clipRoot) to what is above the taskbar (the window reaches a little over the
    /// taskbar, for the shadow); at rest it has no transform and no clip at all (any render transform switches off pixel snapping,
    /// which blurs the frame's lines).
    /// </summary>
    public sealed class PopupSlide
    {


        private static readonly Duration ShowDuration = new(TimeSpan.FromMilliseconds(200));
        private static readonly Duration HideDuration = new(TimeSpan.FromMilliseconds(150));

        private readonly Popup _popup;
        private readonly FrameworkElement _clipRoot;
        private readonly FrameworkElement _slideHost;
        private readonly Func<SlideDirection> _direction;
        private readonly Func<double?> _clipOverlap;
        private readonly TranslateTransform _transform = new();
        private bool _sliding;

        /// <param name="popup">The popup opened and closed with the slide.</param>
        /// <param name="clipRoot">The popup's content that clips to its own bounds: what is cut at the taskbar while sliding.</param>
        /// <param name="slideHost">The element inside it that moves.</param>
        /// <param name="direction">Where the content goes when hidden.</param>
        /// <param name="clipOverlap">How many DIPs the popup's window reaches below the taskbar's top edge (the bottom of the content is cut
        /// there while it slides), or null for no clip.</param>
        public PopupSlide(Popup popup, FrameworkElement clipRoot, FrameworkElement slideHost, Func<SlideDirection> direction, Func<double?> clipOverlap)
        {
            _popup = popup;
            _clipRoot = clipRoot;
            _slideHost = slideHost;
            _direction = direction;
            _clipOverlap = clipOverlap;

            // The clip follows the size while it slides (the popup's first layout comes after it opens).
            _clipRoot.SizeChanged += (s, e) => UpdateClip();

            // The popup's window is in its place when it has opened: the clip is measured from where it is (see MeasureOverlap).
            _popup.Opened += (s, e) => UpdateClip();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int Size;
            public RECT Monitor;
            public RECT Work;
            public int Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

        private FrameworkElement _edgeFrame;
        private double _edgeRoom;

        /// <summary>
        /// A popup next to the edge of the screen is kept inside it (its window is moved in), which left the shadow's whole room between its
        /// frame and the edge: more than the gap the frame has to the taskbar. When the window is against the edge of the monitor, the
        /// frame's margin on that side is made what it takes for the drawn frame to be as far from the edge as it is from the taskbar (the
        /// two are measured from where the windows are on the screen, in whole pixels), so the gaps are the same all round.
        /// </summary>
        /// <param name="frame">The frame inside the window, with the room for the shadow as its margin.</param>
        /// <param name="room">That margin (DIPs).</param>
        public void KeepFrameNearEdges(FrameworkElement frame, double room)
        {
            _edgeFrame = frame;
            _edgeRoom = room;
            _popup.Opened += (s, e) => FitEdges();
        }

        private void FitEdges()
        {
            // The room it has on both sides until the window is seen against an edge.
            Thickness margin = _edgeFrame.Margin;
            _edgeFrame.Margin = new Thickness(_edgeRoom, margin.Top, _edgeRoom, margin.Bottom);

            // A theme with a strong shadow keeps the room for it at the edge of the screen too (PopupShadowKeepRoom): the frame there is as far
            // from the edge as the shadow needs, not as near as it is to the taskbar - the shadow was cut by the edge of the screen on that side
            // and was a fraction of what it is on the others.
            if (_edgeFrame.TryFindResource("PopupShadowKeepRoom") is true)
            {
                return;
            }

            _edgeFrame.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Window.GetWindow(_popup) is not Window taskbar ||
                    PresentationSource.FromVisual(taskbar) is not HwndSource barSource ||
                    PresentationSource.FromVisual(_clipRoot) is not HwndSource source ||
                    !GetWindowRect(barSource.Handle, out RECT bar) ||
                    !GetWindowRect(source.Handle, out RECT window))
                {
                    return;
                }

                // A frame that is a control (a thumbnail's) has its own margin round what is drawn; one that is a border is what is drawn.
                Thickness chrome = _edgeFrame is Control && VisualTreeHelper.GetChildrenCount(_edgeFrame) > 0 && VisualTreeHelper.GetChild(_edgeFrame, 0) is Border border
                    ? border.Margin
                    : default;

                var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(MonitorFromWindow(source.Handle, 2), ref info))
                {
                    return;
                }

                bool atLeft = window.Left <= info.Monitor.Left + 1;
                bool atRight = window.Right >= info.Monitor.Right - 1;
                if (!atLeft && !atRight)
                {
                    return;
                }

                // The gap the drawn frame has above the taskbar: from the frame's bottom (the window's bottom less the room and the style's
                // own margin round the drawn frame) to the taskbar's top.
                DpiScale dpi = VisualTreeHelper.GetDpi(_clipRoot);
                Thickness m = _edgeFrame.Margin;
                double frameBottom = window.Bottom - (m.Bottom + chrome.Bottom) * dpi.DpiScaleY;
                // (The frame is drawn on whole pixels: the half a pixel the layout can have is not part of the gap.)
                double gapPixels = Math.Floor(bar.Top - frameBottom + 0.01);
                if (gapPixels <= 0)
                {
                    return;
                }

                // So much margin on the side at the edge, for that gap from the edge to the drawn frame (the style's margin is part of it).
                double fit = gapPixels / dpi.DpiScaleX - chrome.Left;
                if (fit < 0 || fit >= _edgeRoom)
                {
                    return;
                }

                _edgeFrame.Margin = new Thickness(atLeft ? fit : m.Left, m.Top, atRight ? fit : m.Right, m.Bottom);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// How many DIPs the open popup's window reaches below the top of the taskbar's window, from where the two windows are on the screen
        /// (null if either is not there yet): the popup is placed in whole pixels, so a number worked out from the layout can be a pixel out,
        /// which left a pixel of the popup over the taskbar's edge while it slid.
        /// </summary>
        public static double? MeasureOverlap(FrameworkElement clipRoot, Window taskbar)
        {
            if (PresentationSource.FromVisual(clipRoot) is not HwndSource popupSource
                || PresentationSource.FromVisual(taskbar) is not HwndSource taskbarSource
                || !GetWindowRect(popupSource.Handle, out RECT popup)
                || !GetWindowRect(taskbarSource.Handle, out RECT bar))
            {
                return null;
            }

            // Negative when the popup's window ends above the taskbar (the widgets' popups sit a good way over it): the edge it is cut at while
            // it slides is still the top of the taskbar, not where the window ends (it was cut there, above the bar, with a gap).
            double scale = VisualTreeHelper.GetDpi(clipRoot).DpiScaleY;
            return (popup.Bottom - bar.Top) / scale;
        }

        /// <summary>True while it slides away (the popup closes when that is done).</summary>
        public bool Closing { get; private set; }

        /// <summary>True while the popup is open (sliding in or away included).</summary>
        public bool IsOpen => _popup.IsOpen;

        /// <summary>Raised when the popup has closed, after it slid away or by CloseNow.</summary>
        public event Action Closed;

        private (DependencyProperty Property, double Hidden) Axis()
        {
            SlideDirection direction = _direction();
            bool sideways = direction is SlideDirection.Left or SlideDirection.Right;
            double size = sideways ? _slideHost.ActualWidth : _slideHost.ActualHeight;
            if (size <= 0)
            {
                size = sideways ? 300 : 400;
            }

            return direction switch
            {
                SlideDirection.Left => (TranslateTransform.XProperty, -size),
                SlideDirection.Right => (TranslateTransform.XProperty, size),
                SlideDirection.Up => (TranslateTransform.YProperty, -size),
                _ => (TranslateTransform.YProperty, size),
            };
        }

        /// <summary>Opens the popup, sliding in (from where it is, if it was still on screen sliding away).</summary>
        public void Show()
        {
            if (_winActive)
            {
                // It was sliding away as a window (see BeginWindowMode): it comes back in from where it is.
                Closing = false;
                ++_showId;
                StartWindowAnimation(_winD, 0, ShowDuration, new CubicEase { EasingMode = EasingMode.EaseOut }, FinishWindowShow);
                return;
            }

            (DependencyProperty axis, double hidden) = Axis();
            double from = _popup.IsOpen ? (double)_transform.GetValue(axis) : hidden;

            Closing = false;
            Reset();
            _slideHost.RenderTransform = _transform;
            UseSlideCache();
            _transform.SetValue(axis, from);
            _sliding = true;
            UpdateClip();
            bool wasOpen = _popup.IsOpen;
            _popup.IsOpen = true;

            // The window as it is made is out of sight at once (an empty region, before anything is drawn in it), and the popup's picture is
            // drawn in its place, not moved out: the slide of the window starts once that picture is there. With the picture moved out, the
            // window was shown with nothing in it for the first frames, and its blur showed alone, a grey box.
            bool early = !wasOpen && TryHideWindowEarly();
            if (early)
            {
                _transform.SetValue(axis, 0.0);
                _slideHost.RenderTransform = null;
            }

            // The slide starts once the popup has had its first layout and been drawn (and its Opened handlers have run): the window is
            // created, the content laid out and filled in right after IsOpen, which takes a good part of the 200 ms, and an animation started
            // before that lost its first frames to it - the slide jumped instead of gliding, most visibly on a screen at a high refresh rate.
            int showId = ++_showId;
            _showPending = true;
            _slideHost.Dispatcher.BeginInvoke(new Action(() =>
            {
                _showPending = false;
                if (showId != _showId || Closing || !_popup.IsOpen)
                {
                    return;
                }

                if (TryWindowShow())
                {
                    return;
                }

                if (early)
                {
                    UndoHideWindowEarly();
                    _slideHost.RenderTransform = _transform;
                    _transform.SetValue(axis, from);
                }

                var slide = new DoubleAnimation(from, 0, ShowDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                slide.Completed += (s, e) =>
                {
                    if (!Closing && showId == _showId)
                    {
                        Reset();
                    }
                };
                _transform.BeginAnimation(axis, slide);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private int _showId;
        private bool _showPending;

        // --- Sliding the window itself ---
        // A popup that lets what is behind it show through is drawn by WPF into a bitmap that is handed to the system again for every frame
        // (a layered window): sliding its content that way cost two 3-5 ms render passes per frame, so the slide ran at around 80 frames a second
        // with gaps on a screen that does 144. Moving the window is left to the compositor, which moves what was drawn once, and the part that is
        // on the taskbar's side of its edge is cut off with the window's region. This is for the edge the media popup and the widgets' popups
        // are at; for the other edges the content slides as before.
        // On: the popup's blur is a window of its own that the system draws a few frames late when it moves, so a popup that is moved as a window
        // has no blur while it slides (its glass is darker then, see AddSlideDim) and the blur comes in when it has stopped. Off: the content
        // slides inside a window that stays, with the blur following it, at the cost of the slide being no smoother than ~80-100 frames a second.
        private const bool UseWindowSlide = true;

        private bool _winActive;
        private IntPtr _winHandle;
        private int _winLeft, _winTop, _winWidth, _winHeight, _winEdge;
        private double _winDistance;
        private double _winD;
        private int _winAnimId;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        /// <summary>True while the popup slides (in or away).</summary>
        public bool Sliding => _sliding;

        // The window is where it rests: its place, size and where the taskbar's edge cuts it are taken.
        private bool BeginWindowMode()
        {
            if (!UseWindowSlide || _direction() != SlideDirection.Down
                || PresentationSource.FromVisual(_clipRoot) is not HwndSource source
                || !GetWindowRect(source.Handle, out RECT r))
            {
                return false;
            }

            _sliding = true;
            double? overlap = _clipRoot.ActualHeight > 0 ? _clipOverlap() : null;
            if (overlap == null)
            {
                return false;
            }

            int edge = r.Bottom - (int)Math.Round(overlap.Value * VisualTreeHelper.GetDpi(_clipRoot).DpiScaleY);            if (edge - r.Top <= 0 || r.Right - r.Left <= 0)
            {
                return false;
            }

            // Above the taskbar's window while it slides over it (what is below the bar's edge is cut off by the region).
            SetWindowPos(source.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);

            _winHandle = source.Handle;
            _winLeft = r.Left;
            _winTop = r.Top;
            _winWidth = r.Right - r.Left;
            _winHeight = r.Bottom - r.Top;
            _winEdge = edge;
            PopupBlur.SetEdge(_winHandle, edge);
            _winDistance = edge - r.Top;
            _winD = 0;
            _winActive = true;
            return true;
        }

        // d pixels down from where the window rests; what is below the taskbar's edge is not shown.
        // The window's region and its place are two calls, and the compositor takes what it has been given at the start of each frame it
        // composes: when that start fell between the two calls the frame had one of them and not the other, and for that frame the popup was
        // either over the taskbar (up to the distance it moves in a frame, 20-30 px at the fastest) or cut short above it. Measured on a window
        // of the same kind: 12-20% of the frames with the calls made at a random time, and none with both made 0.5 to 3 ms after the compositor
        // had finished a frame (see StartWindowAnimation). So the cut is where the window is, with no lead.
        // What is left of that, when a frame still has one call and not the other, is made the harmless one: going up (coming in) the window is
        // moved first and cut after, so a frame that has only the move shows the popup cut a little short above the bar; going down (away) it
        // is cut first and moved after, so a frame that has only the cut shows it short too. The other way round each was the popup over the
        // bar for a frame (measured: the cut first going up, and the move first going down, were the only ones with a popup over the bar).
        private void ApplyWindowD(double d)
        {
            // Measured on the real popup (a solid colour over its frame, the screen recorded): the move first and the cut after was right in every
            // frame (open: 274 of 274), and the cut first and the move after had the popup cut short by a frame of travel in the frames going
            // down - the redraw the cut asks for was drawn at the place the window had before the move, for a frame. So always this order.
            const bool goingUp = true;
            _winD = d;
            int top = _winTop + (int)Math.Round(d);
            int visible = Math.Max(0, Math.Min(_winHeight, _winEdge - top));
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long t1, t2;
            if (goingUp)
            {
                SetWindowPos(_winHandle, IntPtr.Zero, _winLeft, top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                SetWindowRgn(_winHandle, CreateRectRgn(0, 0, _winWidth, visible), true);
                t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            else
            {
                SetWindowRgn(_winHandle, CreateRectRgn(0, 0, _winWidth, visible), true);
                t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                SetWindowPos(_winHandle, IntPtr.Zero, _winLeft, top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (_winRun != null)
            {
                _winRun.RgnTotal += (goingUp ? t2 - t1 : t1 - t0) * f;
                _winRun.PosTotal += (goingUp ? t1 - t0 : t2 - t1) * f;
            }
        }

        private IntPtr _earlyHandle;

        private bool TryHideWindowEarly()
        {
            if (!UseWindowSlide || _direction() != SlideDirection.Down || PresentationSource.FromVisual(_clipRoot) is not HwndSource source || source.Handle == IntPtr.Zero)
            {
                return false;
            }

            _earlyHandle = source.Handle;
            SetWindowRgn(_earlyHandle, CreateRectRgn(0, 0, 0, 0), false);
            PopupBlur.Suppress(_earlyHandle, true);
            AddSlideDim();
            return true;
        }

        private void UndoHideWindowEarly()
        {
            if (_earlyHandle == IntPtr.Zero)
            {
                return;
            }

            IntPtr handle = _earlyHandle;
            _earlyHandle = IntPtr.Zero;
            RemoveSlideDim();
            PopupBlur.Suppress(handle, false);
            if (_popup.IsOpen && IsWindow(handle))
            {
                SetWindowRgn(handle, IntPtr.Zero, false);
            }
        }

        private bool TryWindowShow()
        {
            if (!BeginWindowMode())
            {
                return false;
            }

            // Out of sight (it was already: see TryHideWindowEarly), and the popup's picture is in the window by now.
            ApplyWindowD(_winDistance);
            _earlyHandle = IntPtr.Zero;
            _transform.BeginAnimation(TranslateTransform.YProperty, null);
            _transform.Y = 0;
            _slideHost.RenderTransform = null;
            UpdateClip();
            StartWindowAnimation(_winDistance, 0, ShowDuration, new CubicEase { EasingMode = EasingMode.EaseOut }, FinishWindowShow);
            return true;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        // The calls that move the window are made at the right time in the compositor's cycle: a thread of its own waits for the compositor to
        // finish a frame (DwmFlush), waits a moment more, and has the window moved on the UI thread, from the time (not from a count of
        // frames: a frame that comes late does not make the slide late). A frame that the UI thread could not take up by then is left out
        // (the next one is as good) rather than moved at a time when the compositor may be starting its own.
        private const double SlidePhaseMilliseconds = 1.0;
        private const double SlideLateMilliseconds = 2.2;

        private sealed class SlideRun
        {
            public volatile bool Cancelled;
            public bool Counted;
            public int Skipped;
            public int Moved, Left, Late;
            public double MaxLate, ApplyTotal, ApplyMax;
            public double RgnTotal, PosTotal, GapTotal;
            public System.Windows.Threading.DispatcherTimer Fallback;
        }

        private SlideRun _winRun;

        // While a popup's window is moved (and cut) nothing else of this program that is drawn in a layered window should be redrawn in every
        // frame (the taskbar's VU meter, the lyrics): the system takes the window's place and its cut in two calls, and a window that is drawn
        // again at that time holds them up (the move took 3-4 ms instead of 0.7, and 40-70% of the frames had the popup over the bar or cut
        // short above it). Those draw nothing for the 150-200 ms of the slide.
        public static int ActiveWindowSlides { get; private set; }

        /// <summary>Raised (on the UI thread) when the first popup window starts to slide (true) and when the last one has stopped (false):
        /// what draws in every frame holds still for it (the taskbar's title slides, see MediaPlayer.Marquee).</summary>
        public static event Action<bool> WindowSlideChanged;

        /// <summary>The same for a popup window that is dragged by the user (the media popup): what draws in every frame holds still while it is.</summary>
        public static void CountMotion(int change) => CountSlide(change);

        private static void CountSlide(int change)
        {
            int before = ActiveWindowSlides;
            ActiveWindowSlides = Math.Max(0, ActiveWindowSlides + change);
            if ((before == 0) != (ActiveWindowSlides == 0))
            {
                WindowSlideChanged?.Invoke(ActiveWindowSlides > 0);
            }
        }

        private void StartWindowAnimation(double from, double to, Duration duration, IEasingFunction ease, Action done)
        {
            StopWindowAnimation();
            int id = ++_winAnimId;
            var run = new SlideRun();
            _winRun = run;
            CountSlide(1);
            run.Counted = true;
            System.Windows.Threading.Dispatcher dispatcher = _slideHost.Dispatcher;
            double length = duration.HasTimeSpan ? duration.TimeSpan.TotalMilliseconds : 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool finished = false;

            void Finish()
            {
                if (finished || run.Cancelled || id != _winAnimId || !_winActive)
                {
                    return;
                }

                finished = true;
                run.Cancelled = true;
                run.Fallback?.Stop();
                if (run.Counted)
                {
                    run.Counted = false;
                    CountSlide(-1);
                }

                ApplyWindowD(to);
                done();
            }

            void Frame(long flushed)
            {
                if (run.Cancelled || id != _winAnimId || !_winActive)
                {
                    return;
                }

                double t = length > 0 ? clock.Elapsed.TotalMilliseconds / length : 1;
                if (t >= 1)
                {
                    Finish();
                    return;
                }

                double late = (System.Diagnostics.Stopwatch.GetTimestamp() - flushed) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (late > SlideLateMilliseconds && run.Skipped < 2)
                {
                    run.Skipped++;
                    run.Left++;
                    return;
                }

                if (late > SlideLateMilliseconds)
                {
                    run.Late++;
                }

                run.MaxLate = Math.Max(run.MaxLate, late);
                run.Moved++;
                run.Skipped = 0;
                long applyStart = System.Diagnostics.Stopwatch.GetTimestamp();
                ApplyWindowD(from + (to - from) * ease.Ease(t));
                double took = (System.Diagnostics.Stopwatch.GetTimestamp() - applyStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                run.ApplyTotal += took;
                run.ApplyMax = Math.Max(run.ApplyMax, took);
            }

            ApplyWindowD(from);

            // One thread only waits for the compositor, and goes on waiting at once: how long a move takes on the UI thread (a few ms, with what
            // the popup does when its window moves) does not make it miss the next frame, which a single loop did (a move every other frame).
            long lastFlush = 0;
            var flushed = new System.Threading.AutoResetEvent(false);
            var flusher = new System.Threading.Thread(() =>
            {
                while (!run.Cancelled)
                {
                    if (DwmFlush() != 0)
                    {
                        System.Threading.Thread.Sleep(4);
                    }

                    System.Threading.Volatile.Write(ref lastFlush, System.Diagnostics.Stopwatch.GetTimestamp());
                    flushed.Set();
                }
            })
            {
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.AboveNormal,
                Name = "PopupSlide frames",
            };
            var mover = new System.Threading.Thread(() =>
            {
                while (!run.Cancelled)
                {
                    if (!flushed.WaitOne(50))
                    {
                        continue;
                    }

                    long flush = System.Threading.Volatile.Read(ref lastFlush);
                    long until = flush + (long)(SlidePhaseMilliseconds * System.Diagnostics.Stopwatch.Frequency / 1000.0);
                    while (System.Diagnostics.Stopwatch.GetTimestamp() < until)
                    {
                        System.Threading.Thread.SpinWait(20);
                    }

                    if (run.Cancelled)
                    {
                        break;
                    }

                    try
                    {
                        dispatcher.Invoke(() => Frame(flush), System.Windows.Threading.DispatcherPriority.Send);
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }
            })
            {
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.AboveNormal,
                Name = "PopupSlide moves",
            };
            flusher.Start();
            mover.Start();

            // If the compositor gives no frames (nothing is being shown) the slide still ends.
            run.Fallback = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(length + 300) };
            run.Fallback.Tick += (s, e) => Finish();
            run.Fallback.Start();
        }

        private void StopWindowAnimation()
        {
            if (_winRun != null)
            {
                _winRun.Cancelled = true;
                _winRun.Fallback?.Stop();
                if (_winRun.Counted)
                {
                    _winRun.Counted = false;
                    CountSlide(-1);
                }

                _winRun = null;
            }

            ++_winAnimId;
        }

        private void FinishWindowShow()
        {
            if (!Closing)
            {
                EndWindowMode(true);
                FadeSlideDimOut();
            }
        }

        // --- The darker glass while the window slides (no blur then) ---
        private Border _slideDim;
        private Panel _slideDimParent;

        // Off: the glass is the darker tint all the time while attached (see the theme's MediaPopupFrame), so there is nothing to change when it moves.
        private const bool UseSlideDim = false;

        private bool AddSlideDim(bool force = false, byte alpha = 130)
        {
            if (!UseSlideDim && !force)
            {
                return false;
            }

            if (_slideDim != null)
            {
                _slideDim.BeginAnimation(UIElement.OpacityProperty, null);
                _slideDim.Opacity = 1;
                return false;
            }

            Controls.PopupShadow shadow = FindShadow(_slideHost);
            if (shadow == null || PopupBlur.FrameOf(shadow) is not Panel frame)
            {
                return false;
            }

            double radius = shadow.TryFindResource("PopupBlurCornerRadius") is double r ? r : 0;
            _slideDim = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0)),
                CornerRadius = new CornerRadius(radius),
                IsHitTestVisible = false,
            };
            _slideDimParent = frame;
            frame.Children.Insert(Math.Min(1, frame.Children.Count), _slideDim);
            return true;
        }

        private static Controls.PopupShadow FindShadow(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is Controls.PopupShadow found)
                {
                    return found;
                }

                if (FindShadow(child) is Controls.PopupShadow deeper)
                {
                    return deeper;
                }
            }

            return null;
        }

        private void RemoveSlideDim()
        {
            if (_slideDim != null)
            {
                _slideDim.BeginAnimation(UIElement.OpacityProperty, null);
                _slideDimParent?.Children.Remove(_slideDim);
                _slideDim = null;
                _slideDimParent = null;
            }
        }

        /// <summary>The popup's glass goes from the darker tint (the one it has attached to the taskbar) to its own, as the blur window comes in under
        /// it: the blur seems to fade in.</summary>
        public void FadeGlassIn()
        {
            if (AddSlideDim(true, 176) || _slideDim != null)
            {
                FadeSlideDimOut();
            }
        }

        // The glass goes from the darker one to its own as the blur comes in under it.
        private void FadeSlideDimOut()
        {
            Border dim = _slideDim;
            if (dim == null)
            {
                return;
            }

            var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(160)));
            fade.Completed += (s, e) =>
            {
                if (_slideDim == dim)
                {
                    RemoveSlideDim();
                }
            };
            dim.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // Back to a window that rests where it was placed, with no region (if it is still there to be put back).
        private void EndWindowMode(bool restore)
        {
            StopWindowAnimation();
            if (!_winActive)
            {
                return;
            }

            _winActive = false;
            PopupBlur.SetEdge(_winHandle, null);
            PopupBlur.Suppress(_winHandle, false);
            _transform.BeginAnimation(TranslateTransform.YProperty, null);
            _transform.Y = 0;
            if (restore && _popup.IsOpen && IsWindow(_winHandle))
            {
                SetWindowPos(_winHandle, IntPtr.Zero, _winLeft, _winTop, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                SetWindowRgn(_winHandle, IntPtr.Zero, false);
            }

            _sliding = false;
            UpdateClip();
        }

        /// <summary>The slide away that makes room for another popup: shorter, so that popup does not wait long.</summary>
        public static readonly TimeSpan QuickHideTime = TimeSpan.FromMilliseconds(90);

        /// <summary>Slides away and closes the popup (in the time given, or the usual one).</summary>
        public void Hide(TimeSpan? time = null)
        {
            if (!_popup.IsOpen || Closing)
            {
                return;
            }

            Closing = true;
            Duration duration = time is TimeSpan quickTime ? new Duration(quickTime) : HideDuration;
            if (_winActive || (!_showPending && BeginWindowMode()))
            {
                bool dimmed = AddSlideDim();
                int hideId = ++_winAnimId;
                Action start = () =>
                {
                    if (hideId != _winAnimId || !_winActive || !Closing)
                    {
                        return;
                    }

                    // (The blur goes when the darker glass is there to take its place.)
                    PopupBlur.Suppress(_winHandle, true);
                    PopupBlur.Refresh(_winHandle);
                    // (A quadratic ease: the cubic one ended at three times the average speed, and a frame with the cut and the move not together
                    // showed the popup short by that much - 20 px - where this ends at twice, 13.)
                    StartWindowAnimation(_winD, _winDistance, duration, new QuadraticEase { EasingMode = EasingMode.EaseIn }, () =>
                    {
                        if (Closing)
                        {
                            Closing = false;
                            EndWindowMode(false);
                            _popup.IsOpen = false;
                            Reset();
                            Closed?.Invoke();
                        }
                    });
                };

                // The darker glass is drawn once before the window moves (a window that moved with the old picture showed it change half way).
                if (dimmed)
                {
                    _slideHost.Dispatcher.BeginInvoke(start, System.Windows.Threading.DispatcherPriority.Loaded);
                }
                else
                {
                    start();
                }

                return;
            }

            (DependencyProperty axis, double hidden) = Axis();
            _slideHost.RenderTransform = _transform;
            UseSlideCache();
            _sliding = true;
            UpdateClip();

            var slide = new DoubleAnimation((double)_transform.GetValue(axis), hidden, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            slide.Completed += (s, e) =>
            {
                // Only if it wasn't brought back (or closed some other way) while sliding away.
                if (Closing)
                {
                    Closing = false;
                    _popup.IsOpen = false;
                    Reset();
                    Closed?.Invoke();
                }
            };
            _transform.BeginAnimation(axis, slide);
        }

        /// <summary>Closes the popup at once, with no slide.</summary>
        public void CloseNow()
        {
            Closing = false;
            _popup.IsOpen = false;
            Reset();
            Closed?.Invoke();
        }

        /// <summary>Stops any slide and takes the transform and the clip off.</summary>
        public void Reset()
        {
            UndoHideWindowEarly();
            RemoveSlideDim();
            if (_winActive)
            {
                EndWindowMode(true);
            }

            _transform.BeginAnimation(TranslateTransform.XProperty, null);
            _transform.BeginAnimation(TranslateTransform.YProperty, null);
            _transform.X = 0;
            _transform.Y = 0;
            _slideHost.RenderTransform = null;
            _slideHost.CacheMode = null;
            _sliding = false;
            UpdateClip();
        }

        // While it slides the popup is drawn once into a bitmap that is moved: redrawing all of it (the shadow, the lyrics with their blur) for
        // every frame took most of a frame at a high refresh rate. At rest there is no cache, so the frame's lines stay on whole pixels.
        private const bool UseSlideCachedBitmap = false;

        private void UseSlideCache()
        {
            // Off: the cached picture sat on whole pixels a little differently from the live drawing, and the popup was a pixel or more to the right
            // while it slid, and jumped back at the end.
            if (UseSlideCachedBitmap && _slideHost.CacheMode == null)
            {
                _slideHost.CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(_slideHost).DpiScaleX) { SnapsToDevicePixels = true };
            }
        }

        /// <summary>Sets the clip again (the taskbar moved, or the popup's size changed).</summary>
        public void UpdateClip()
        {
            // A window that slides is cut by its region, not by a clip of its content.
            double? overlap = _sliding && !_winActive && _clipRoot.ActualHeight > 0 ? _clipOverlap() : null;

            // The popup's blur is cut at the taskbar's edge while the content slides (it blurred the bar's own strip under the window).
            if (!_winActive && PresentationSource.FromVisual(_clipRoot) is HwndSource source && source.Handle != IntPtr.Zero)
            {
                if (overlap != null && GetWindowRect(source.Handle, out RECT r))
                {
                    PopupBlur.SetEdge(source.Handle, r.Bottom - (int)Math.Round(overlap.Value * VisualTreeHelper.GetDpi(_clipRoot).DpiScaleY));
                }
                else
                {
                    PopupBlur.SetEdge(source.Handle, null);
                }
            }

            if (overlap == null)
            {
                _clipRoot.ClearValue(UIElement.ClipProperty);
                return;
            }

            _clipRoot.Clip = new RectangleGeometry(new Rect(0, 0, _clipRoot.ActualWidth, Math.Max(0, _clipRoot.ActualHeight - Math.Max(0, overlap.Value))));
        }
    }
}

