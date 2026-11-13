using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RetroBar.Utilities
{
    /// <summary>
    /// A blur of what is behind the window, drawn by RetroBar itself (so its radius is the one asked for, which the blur Windows gives a
    /// window has not): the pixels of the screen under this element are copied, blurred, and drawn as its background. Put it under the
    /// glass colour of a frame:
    ///
    ///   <utilities:BackdropBlur Radius="6" CornerRadius="6" />
    ///
    /// For the copy not to take the bar's own pixels (which would feed back into themselves) the windows of this program are marked as
    /// excluded from screen capture: they are seen on the screen as ever, but not in screenshots and recordings.
    /// </summary>
    public class BackdropBlur : Border
    {
        public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
            nameof(Radius), typeof(double), typeof(BackdropBlur), new PropertyMetadata(6.0, (d, e) => ((BackdropBlur)d).ApplyRadius()));

        public static readonly DependencyProperty IntervalProperty = DependencyProperty.Register(
            nameof(Interval), typeof(double), typeof(BackdropBlur), new PropertyMetadata(50.0, (d, e) => ((BackdropBlur)d).ApplyInterval()));

        /// <summary>The blur's radius, in DIPs.</summary>
        public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

        public static readonly DependencyProperty SaturationProperty = DependencyProperty.Register(
            nameof(Saturation), typeof(double), typeof(BackdropBlur), new PropertyMetadata(1.5));

        public static readonly DependencyProperty BrightnessProperty = DependencyProperty.Register(
            nameof(Brightness), typeof(double), typeof(BackdropBlur), new PropertyMetadata(0.7));

        public static readonly DependencyProperty NoiseProperty = DependencyProperty.Register(
            nameof(Noise), typeof(double), typeof(BackdropBlur), new PropertyMetadata(0.05, (d, e) => ((BackdropBlur)d)._noise.Opacity = (double)e.NewValue));

        /// <summary>How much colour the blurred picture has (1 = as it is): acrylic makes what is behind it a little more vivid.</summary>
        public double Saturation { get => (double)GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }

        /// <summary>How light the blurred picture is (1 = as it is).</summary>
        public double Brightness { get => (double)GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }

        /// <summary>How strong the fine grain over the blur is (0 to 1).</summary>
        public double Noise { get => (double)GetValue(NoiseProperty); set => SetValue(NoiseProperty, value); }

        /// <summary>Milliseconds between two copies of the screen.</summary>
        public double Interval { get => (double)GetValue(IntervalProperty); set => SetValue(IntervalProperty, value); }

        private readonly Image _image = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        private readonly System.Windows.Shapes.Rectangle _noise = new System.Windows.Shapes.Rectangle { IsHitTestVisible = false, Opacity = 0.05 };
        private static ImageBrush _noiseBrush;
        private byte[] _last;
        private int _diag;
        private double _lastWidth, _lastHeight;
        private int _ticks;

        public BackdropBlur()
        {
            IsHitTestVisible = false;
            ClipToBounds = true;
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.Linear);
            // In a canvas, which asks no room for what is in it: the picture is bigger than the element (it has the room for the blur's edges
            // round it), and as the child of the border itself it made the element, and the frame round it, grow by that each time it was copied.
            var canvas = new Canvas { IsHitTestVisible = false };
            canvas.Children.Add(_image);
            _noise.Fill = NoiseBrush();
            canvas.Children.Add(_noise);
            Child = canvas;
            ApplyRadius();
            Loaded += (s, e) =>
            {
                ExcludeOwnWindows();
                Capture();
                if (Interval <= 0)
                {
                    CaptureLater();
                }

                StartTimer();
            };
            Unloaded += (s, e) => StopTimer();
            // A popup that is shown again is copied again (once).
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible && Interval <= 0)
                {
                    Dispatcher.BeginInvoke(new Action(() => { ExcludeOwnWindows(); Capture(); }), DispatcherPriority.Background);
                    CaptureLater();
                }

                if (IsVisible && IsLoaded)
                {
                    StartTimer();
                }
                else
                {
                    StopTimer();
                }
            };
            SizeChanged += (s, e) => UpdateClip();
        }

        // One pass of a box blur over 32 bit pixels (alpha left alone), along x or along y, the edges repeating their last pixel.
        private static void BoxBlur(byte[] data, int width, int height, int radius, bool alongX)
        {
            int length = alongX ? width : height;
            int lines = alongX ? height : width;
            int window = radius * 2 + 1;
            var line = new byte[length * 3];
            for (int l = 0; l < lines; l++)
            {
                for (int i = 0; i < length; i++)
                {
                    int index = (alongX ? l * width + i : i * width + l) * 4;
                    line[i * 3] = data[index];
                    line[i * 3 + 1] = data[index + 1];
                    line[i * 3 + 2] = data[index + 2];
                }

                for (int c = 0; c < 3; c++)
                {
                    int sum = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        sum += line[Math.Max(0, Math.Min(length - 1, k)) * 3 + c];
                    }

                    for (int i = 0; i < length; i++)
                    {
                        int index = (alongX ? l * width + i : i * width + l) * 4 + c;
                        data[index] = (byte)(sum / window);
                        int add = Math.Min(length - 1, i + radius + 1), remove = Math.Max(0, i - radius);
                        sum += line[add * 3 + c] - line[remove * 3 + c];
                    }
                }
            }
        }

        private static ImageBrush NoiseBrush()
        {
            if (_noiseBrush == null)
            {
                const int size = 96;
                var random = new Random(7);
                var data = new byte[size * size * 4];
                for (int i = 0; i < size * size; i++)
                {
                    byte grey = (byte)(random.Next(2) == 0 ? 255 : 0);
                    byte alpha = (byte)random.Next(60, 200);
                    data[i * 4] = (byte)(grey * alpha / 255);
                    data[i * 4 + 1] = (byte)(grey * alpha / 255);
                    data[i * 4 + 2] = (byte)(grey * alpha / 255);
                    data[i * 4 + 3] = alpha;
                }

                var tile = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, data, size * 4);
                tile.Freeze();
                var brush = new ImageBrush(tile) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, size, size), Stretch = Stretch.None };
                brush.Freeze();
                _noiseBrush = brush;
            }

            return _noiseBrush;
        }

        // The picture's colours: more colour and a little lighter, as acrylic has them.
        private void Colorize(byte[] data)
        {
            double saturation = Saturation, brightness = Brightness;
            for (int i = 0; i + 2 < data.Length; i += 4)
            {
                double b = data[i], g = data[i + 1], r = data[i + 2];
                double luma = 0.299 * r + 0.587 * g + 0.114 * b;
                r = (luma + (r - luma) * saturation) * brightness;
                g = (luma + (g - luma) * saturation) * brightness;
                b = (luma + (b - luma) * saturation) * brightness;
                data[i + 2] = (byte)Math.Max(0, Math.Min(255, r));
                data[i + 1] = (byte)Math.Max(0, Math.Min(255, g));
                data[i] = (byte)Math.Max(0, Math.Min(255, b));
            }
        }

        private static string RangeOf(byte[] pixels)
        {
            int min = 255, max = 0;
            for (int i = 0; i + 2 < pixels.Length; i += 4)
            {
                int v = (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3;
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }

            return min + ".." + max;
        }

        // A popup is put in its place after it is made (when it is first copied it is still at the corner of the screen it is made at): copied
        // again once it is where it stays.
        private void CaptureLater()
        {
            foreach (int milliseconds in new[] { 90, 250, 600 })
            {
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
                timer.Tick += (ts, te) =>
                {
                    timer.Stop();
                    if (IsVisible)
                    {
                        ExcludeOwnWindows();
                        Capture();
                    }
                };
                timer.Start();
            }
        }

        private void ApplyRadius()
        {
            if (IsLoaded)
            {
                Capture();
            }
        }

        // Real time: what is behind is copied again every frame or so while this is shown (a popup too, whose Interval is 0), and only redrawn
        // when it has changed. A copy that takes long makes the wait longer, so a busy machine is not made busier.
        private double BaseInterval => Interval > 0 ? Math.Max(4, Interval) : 8;

        // Driven by a timer of its own (not the dispatcher's, nor the frames: while a popup is dragged the thread is flooded with the moves
        // of its window, and the dispatcher's timer, which only runs when nothing else waits, did not get its turn, so the blur stood still
        // under it; and asking for every frame kept the whole window drawing all the time). The timer only asks the UI thread to come and
        // copy: one request waiting at a time.
        // How often it looks when nothing has moved (reading the screen costs about the same whatever the size): what moves, appears or goes
        // (a window, see the hook below) brings the fast pace back at once, so this only has to catch what changes inside a window.
        private const double StillInterval = 400;
        private System.Threading.Timer _ticker;
        private int _pending;
        private bool _running;
        private double _wait;
        private bool _changed;
        private int _stillFor;

        private void StartTimer()
        {
            if (_running)
            {
                return;
            }

            _running = true;
            lock (Live) { if (!Live.Contains(this)) { Live.Add(this); } }
            InstallHook();
            _ticker ??= new System.Threading.Timer(_ => Post(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _ticker.Change(0, System.Threading.Timeout.Infinite);
            // The graphics card tells when the screen changed behind this (see GpuDesktop): copied at once, and not at the next turn of the timer.
            _gpu ??= GpuDesktop.Subscribe(OnScreenChanged);
        }

        private GpuDesktop.Subscription _gpu;


        // The frames of the screen come as fast as the screen is drawn (and from a thread of their own): the UI thread must be left time between
        // two copies to draw and to take input - copies at its highest priority, one after the other, left it no time and the program hung.
        private const double MinimumGap = 1;
        private long _lastTickEnd;

        private void OnScreenChanged()
        {
            _stillFor = 0;
            double since = (Stopwatch.GetTimestamp() - System.Threading.Volatile.Read(ref _lastTickEnd)) * 1000.0 / Stopwatch.Frequency;
            if (since >= MinimumGap)
            {
                Post();
            }
            else
            {
                _ticker?.Change((int)Math.Max(1, Math.Ceiling(MinimumGap - since)), System.Threading.Timeout.Infinite);
            }
        }

        private void StopTimer()
        {
            _gpu?.Dispose();
            _gpu = null;
            _running = false;
            lock (Live) { Live.Remove(this); }
            _ticker?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }

        // ---- a window that moves, appears, goes or changes its place in the order, anywhere, is told to every blur on this thread: they are
        // copied again at once and at the fast pace (a drag of a window over the bar, a popup that opens, a menu)
        private static readonly List<BackdropBlur> Live = new();
        [ThreadStatic] private static IntPtr _hook;
        private static readonly WinEventProc HookProc = OnWinEvent;
        private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);

        private static void InstallHook()
        {
            if (_hook != IntPtr.Zero)
            {
                return;
            }

            // EVENT_OBJECT_SHOW .. EVENT_OBJECT_LOCATIONCHANGE (0x8002 - 0x800B), out of context (called back on this thread)
            _hook = SetWinEventHook(0x8002, 0x800B, IntPtr.Zero, HookProc, 0, 0, 0);
        }

        private static void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            long probe = SlideProbe.Start();
            try
            {
                OnWinEventCore(eventType, hwnd, idObject);
            }
            finally
            {
                SlideProbe.Stop(0, probe);
            }
        }

        private static void OnWinEventCore(uint eventType, IntPtr hwnd, int idObject)
        {
            if (idObject != 0 || hwnd == IntPtr.Zero)
            {
                return;
            }

            if (!GetWindowRect(hwnd, out RECT moved))
            {
                return;
            }

            // (A window says it has moved without its place having changed, many times a second: only a different place counts.)
            if (eventType == 0x800B && LastPlace.TryGetValue(hwnd, out RECT before) && before.Left == moved.Left && before.Top == moved.Top && before.Right == moved.Right && before.Bottom == moved.Bottom)
            {
                return;
            }

            LastPlace[hwnd] = moved;
            if (LastPlace.Count > 400)
            {
                LastPlace.Clear();
            }

            GetWindowThreadProcessId(hwnd, out uint pid);
            bool own = pid == (uint)Environment.ProcessId;
            BackdropBlur[] blurs;
            lock (Live) { blurs = Live.ToArray(); }
            foreach (var blur in blurs)
            {
                if (!blur.Dispatcher.CheckAccess())
                {
                    continue;
                }

                // A window of this program is not in the copy (it is left out of the screen's capture): only its own blur has to follow
                // it. Any other that is where the copy is.
                RECT mine = blur._screenRect;
                bool overlaps = moved.Left < mine.Right && moved.Right > mine.Left && moved.Top < mine.Bottom && moved.Bottom > mine.Top;
                if (own ? blur._ownWindow == hwnd : overlaps)
                {
                    blur.Kick();
                }
            }
        }

        [ThreadStatic] private static Dictionary<IntPtr, RECT> _lastPlace;
        private static Dictionary<IntPtr, RECT> LastPlace => _lastPlace ??= new();
        private RECT _screenRect;
        private IntPtr _ownWindow;

        private void Kick()
        {
            _stillFor = 0;
            if (_running && _pending == 0)
            {
                _ticker?.Change(0, System.Threading.Timeout.Infinite);
            }
        }

        private void Post()
        {
            if (!_running || System.Threading.Interlocked.Exchange(ref _pending, 1) != 0)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _pending = 0;
                if (!_running)
                {
                    return;
                }

                Tick();
                System.Threading.Volatile.Write(ref _lastTickEnd, Stopwatch.GetTimestamp());
                _ticker?.Change((int)Math.Max(1, _wait), System.Threading.Timeout.Infinite);
            }), DispatcherPriority.Send);
        }

        private void ApplyInterval()
        {
            // (Read at each turn of the timer.)
        }

        private void UpdateClip()
        {
            double radius = CornerRadius.TopLeft;
            if (radius > 0 && ActualWidth > 0 && ActualHeight > 0)
            {
                var clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), radius, radius);
                clip.Freeze();
                Clip = clip;
            }
            else
            {
                ClearValue(ClipProperty);
            }
        }

        private void Tick()
        {
            if (!IsVisible || ActualWidth <= 0 || ActualHeight <= 0)
            {
                return;
            }

            if (++_ticks % 20 == 0)
            {
                ExcludeOwnWindows();
            }

            var watch = Stopwatch.StartNew();
            Capture();
            watch.Stop();
            // Every frame while what is behind is moving; once it has been still for a little it is looked at less often (reading the screen
            // costs, and a bar over a still desktop would pay it all day), and a change brings back the fast pace as soon as it is seen.
            _stillFor = _changed ? 0 : _stillFor + 1;
            _wait = Math.Max(_stillFor > 12 ? StillInterval : BaseInterval, watch.Elapsed.TotalMilliseconds * 1.5);
        }

        private void Capture()
        {
            _changed = false;
            if (PresentationSource.FromVisual(this) is not HwndSource || ActualWidth <= 0 || ActualHeight <= 0)
            {
                if (_diag < 6) { _diag++; ManagedShell.Common.Logging.ShellLogger.Info($"BackdropBlur DIAG skipped: source {PresentationSource.FromVisual(this) != null} size {ActualWidth}x{ActualHeight}"); }
                return;
            }

            try
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                // The room round the element that the blur takes its edges from (so that its own edge does not fade into nothing).
                // (No room round the element: a picture that sticks out of it was counted in the size of what is drawn, and the windows of the bars
                // came out taller by that.)
                double pad = 0;

                // Pixels per DIP, from the size the window has on the screen against the size its content thinks it has (the bar of a monitor whose
                // scale Windows has changed behind its back gave a wrong place with PointToScreen: the copy was of another part of the screen).
                double scaleX = dpi.DpiScaleX, scaleY = dpi.DpiScaleY;
                Point topLeft;
                var hwndSource = (HwndSource)PresentationSource.FromVisual(this);
                if (hwndSource.RootVisual is FrameworkElement root && root.ActualWidth > 0 && root.ActualHeight > 0 && GetWindowRect(hwndSource.Handle, out RECT rect))
                {
                    scaleX = (rect.Right - rect.Left) / root.ActualWidth;
                    scaleY = (rect.Bottom - rect.Top) / root.ActualHeight;
                    Point inRoot = TransformToAncestor(root).Transform(new Point(-pad, -pad));
                    topLeft = new Point(rect.Left + inRoot.X * scaleX, rect.Top + inRoot.Y * scaleY);
                }
                else
                {
                    topLeft = PointToScreen(new Point(-pad, -pad));
                }

                int width = (int)Math.Round((ActualWidth + 2 * pad) * scaleX);
                int height = (int)Math.Round((ActualHeight + 2 * pad) * scaleY);
                _ownWindow = hwndSource.Handle;
                _screenRect = new RECT { Left = (int)Math.Round(topLeft.X), Top = (int)Math.Round(topLeft.Y), Right = (int)Math.Round(topLeft.X) + width, Bottom = (int)Math.Round(topLeft.Y) + height };
                if (width <= 0 || height <= 0 || width > 8000 || height > 4000)
                {
                    return;
                }

                // How many pixels of the screen go into one of the copy: about the radius, in device pixels.
                int factor = Math.Max(1, (int)Math.Round(Math.Max(0, Radius) * scaleX * 0.25));
                int smallWidth = Math.Max(1, width / factor), smallHeight = Math.Max(1, height / factor);

                byte[] pixels;
                int gridW, gridH;
                // The graphics card cuts the picture out of the frame of the screen and makes it smaller (a power of two): see GpuDesktop. When
                // it cannot, the screen is read the old way.
                int shift = factor > 1 ? (int)Math.Round(Math.Log2(factor)) : 0;
                bool fromGpu = false;
                pixels = null;
                gridW = gridH = 0;
                if (_gpu != null)
                {
                    _gpu.Want(_screenRect.Left, _screenRect.Top, _screenRect.Right, _screenRect.Bottom, shift);
                    if (_gpu.TryTake(out pixels, out gridW, out gridH, out bool unchanged))
                    {
                        if (unchanged)
                        {
                            return;
                        }

                        factor = 1 << shift;
                        fromGpu = true;
                    }
                }

                if (!fromGpu)
                {
                    IntPtr screen = GetDC(IntPtr.Zero);
                    IntPtr memory = CreateCompatibleDC(screen);
                    IntPtr bitmap = CreateCompatibleBitmap(screen, smallWidth, smallHeight);
                    IntPtr old = SelectObject(memory, bitmap);
                    SetStretchBltMode(memory, HALFTONE);
                    SetBrushOrgEx(memory, 0, 0, IntPtr.Zero);
                    StretchBlt(memory, 0, 0, smallWidth, smallHeight, screen, (int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y), width, height, SRCCOPY);
                    SelectObject(memory, old);

                    BitmapSource copy = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    DeleteObject(bitmap);
                    DeleteDC(memory);
                    ReleaseDC(IntPtr.Zero, screen);

                    // What is behind rarely changes: when the copy is the same as the last one nothing is redrawn (a redraw of a transparent
                    // window is what costs).
                    gridW = copy.PixelWidth;
                    gridH = copy.PixelHeight;
                    pixels = new byte[gridW * gridH * (copy.Format.BitsPerPixel / 8)];
                    copy.CopyPixels(pixels, gridW * (copy.Format.BitsPerPixel / 8), 0);
                }

                bool sameSize = _last != null && _last.Length == pixels.Length && Math.Abs(_lastWidth - ActualWidth) < 0.01 && Math.Abs(_lastHeight - ActualHeight) < 0.01;
                if (sameSize && _last.AsSpan().SequenceEqual(pixels))
                {
                    return;
                }

                _changed = true;
                _last = pixels;
                _lastWidth = ActualWidth;
                _lastHeight = ActualHeight;

                // The copy of the screen has no alpha of its own (what a screen copy leaves in that byte is 0 or 255, as it comes): made as 32 bit
                // pixels without alpha, so that it is drawn solid whatever those bytes are (with 0 in them it was drawn clear, which was no blur).
                int blurRadius = Math.Max(1, (int)Math.Round(Math.Max(0, Radius) * scaleX * 0.6 / factor));
                int smallW = gridW, smallH = gridH;
                var blurred = (byte[])pixels.Clone();
                for (int pass = 0; pass < 3; pass++)
                {
                    BoxBlur(blurred, smallW, smallH, blurRadius, true);
                    BoxBlur(blurred, smallW, smallH, blurRadius, false);
                }

                Colorize(blurred);
                BitmapSource source = BitmapSource.Create(smallW, smallH, 96, 96, PixelFormats.Bgr32, null, blurred, smallW * 4);
                source.Freeze();

                _noise.Width = ActualWidth;
                _noise.Height = ActualHeight;
                Canvas.SetLeft(_image, -pad);
                Canvas.SetTop(_image, -pad);
                _image.Width = ActualWidth + 2 * pad;
                _image.Height = ActualHeight + 2 * pad;
                _image.Source = source;
            }
            catch (Exception ex)
            {
                if (_diag < 12) { _diag++; ManagedShell.Common.Logging.ShellLogger.Info("BackdropBlur DIAG exception " + ex.Message); }
            }
        }

        // ---- the windows of this program are left out of what the screen shows to a capture (Windows 10 2004 and later)
        private static readonly HashSet<IntPtr> Excluded = new();

        private static void ExcludeOwnWindows()
        {
            // The bar stays in screenshots and recordings; a theme can say BackdropBlurExcludeCapture = True to hide its windows from them,
            // which also keeps the bar out of the blur's own copy of the screen.
            if (Application.Current?.TryFindResource("BackdropBlurExcludeCapture") is not true)
            {
                return;
            }

            uint own = (uint)Environment.ProcessId;
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == own && Excluded.Add(hwnd))
                {
                    SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
                }

                return true;
            }, IntPtr.Zero);
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        private const int SRCCOPY = 0x00CC0020;
        private const int CAPTUREBLT = 0x40000000;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        private const int HALFTONE = 4;
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
        [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    }
}
