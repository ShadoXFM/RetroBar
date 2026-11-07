using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The blur Windows gives the glass behind a window (the taskbar's own, asked for in the same way: the accent of the window), for the
    /// popups (a tab's preview, the weather's forecast, the media popup, a menu), whose frames are see-through.
    ///
    /// A popup's window is a layered one, and the blur of such a window fills the whole of it whatever is drawn in it and whatever region it is
    /// cut to (the window has room round its frame for its shadow, so the blur was a box bigger than the frame). So the blur is a window of its
    /// own: the size of the frame, just under the popup in the stacking order, which follows it (the popup keeps its shadow).
    /// </summary>
    internal static class PopupBlur
    {
        private const string ClassName = "RetroBarPopupBackdrop";

        private const uint WsPopup = 0x80000000;
        private const uint WsExTopmost = 0x8, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
        private const uint SwpNoActivate = 0x10, SwpNoOwnerZOrder = 0x200, SwpNoSize = 0x1, SwpNoMove = 0x2;
        private const int SwShowNoActivate = 4, SwHide = 0;
        private const uint GwHwndNext = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClassEx
        {
            public int Size;
            public uint Style;
            public IntPtr WndProc;
            public int ClsExtra;
            public int WndExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            public string MenuName;
            public string ClassName;
            public IntPtr IconSmall;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WndClassEx wndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct DwmBlurBehind
        {
            public int Flags;
            public bool Enable;
            public IntPtr Region;
            public bool TransitionOnMaximized;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind blurBehind);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string module);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("gdi32.dll")]
        private static extern IntPtr GetStockObject(int index);

        private static bool _classRegistered;

        // The window of each popup's shadow control that has one (the control is what knows where the popup's frame is).
        private sealed class Backdrop
        {
            public IntPtr Handle;
            public (int, int, int, int) Place;
            public (int, int, int, int) LastSeen;
            public (int, int, int, int) Clip;
            public long LastChange;
            public int PreviousTop;
            public long PreviousTicks;
            public bool Shown;
            public (int, int) RoundedFor;
            public int Kind;
        }

        private static readonly ConditionalWeakTable<FrameworkElement, Backdrop> Backdrops = new();

        // A popup can say when it is to have the blur window (the media popup: only when it is a window of its own, moved or on top; attached to
        // the taskbar and sliding, a blur window the system draws a few frames late was never right, and the glass there is a darker tint).
        // The gate is on an element of the popup (its root); a popup with none has the blur as the theme asks.
        private static readonly ConditionalWeakTable<FrameworkElement, Func<bool>> Gates = new();

        public static void SetGate(FrameworkElement root, Func<bool> gate)
        {
            Gates.Remove(root);
            Gates.Add(root, gate);
        }

        private static bool Allowed(FrameworkElement element)
        {
            for (DependencyObject current = element; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is FrameworkElement fe && Gates.TryGetValue(fe, out Func<bool> gate))
                {
                    return gate();
                }
            }

            return true;
        }

        // While a popup's window slides over the taskbar (see PopupSlide), what is below the taskbar's edge is not shown: nor may the blur be, or
        // it blurs the bar's own content. The edge is in screen pixels, by the popup's window.
        private static readonly System.Collections.Generic.Dictionary<IntPtr, int> Edges = new();
        private static readonly System.Collections.Generic.Dictionary<IntPtr, (FrameworkElement Element, Thickness Room)> LastUpdated = new();

        // A popup that is sliding away has no blur under it: the blur window was a few frames behind it, a grey block where the popup had been.
        private static readonly System.Collections.Generic.HashSet<IntPtr> Suppressed = new();

        public static void Suppress(IntPtr popup, bool suppress)
        {
            if (suppress)
            {
                Suppressed.Add(popup);
            }
            else
            {
                Suppressed.Remove(popup);
            }
        }

        public static void SetEdge(IntPtr popup, int? edge)
        {
            if (edge is int e)
            {
                Edges[popup] = e;
            }
            else
            {
                Edges.Remove(popup);
            }
        }

        /// <summary>Puts the blur where the popup's frame is now (for a popup whose window was moved).</summary>
        public static void Refresh(IntPtr popup)
        {
            if (LastUpdated.TryGetValue(popup, out var last))
            {
                UpdateInner(last.Element, last.Room);
            }
        }

        /// <summary>True when the theme asks for it: its glass blurs what is behind (AllowsBlurBehind), and its popups' frames are see-through.</summary>
        private static bool Wanted(FrameworkElement element) =>
            Settings.Instance.AllowBlurBehind &&
            element.TryFindResource("AllowsBlurBehind") is true &&
            element.TryFindResource("PopupBlurBehind") is true;

        private static void EnsureClass()
        {
            if (_classRegistered)
            {
                return;
            }

            _classRegistered = true;
            // A window that draws nothing of its own (black is no colour to the blur): the default window procedure, a black background.
            var wndClass = new WndClassEx
            {
                Size = Marshal.SizeOf<WndClassEx>(),
                WndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
                Instance = GetModuleHandle(null),
                Background = GetStockObject(4 /* BLACK_BRUSH */),
                ClassName = ClassName,
            };
            RegisterClassEx(ref wndClass);
        }

        private static void AskForBlur(IntPtr hwnd, int kind)
        {
            var accent = new AccentPolicy { AccentState = kind };
            int size = Marshal.SizeOf(accent);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, buffer, false);
                var data = new WindowCompositionAttributeData { Attribute = 19 /* WCA_ACCENT_POLICY */, SizeOfData = size, Data = buffer };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>How opaque the element is on the screen (its own opacity and its parents', a tooltip's included).</summary>
        private static double EffectiveOpacity(DependencyObject element)
        {
            double opacity = 1;
            for (DependencyObject current = element; current != null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            {
                if (current is UIElement ui)
                {
                    opacity *= ui.Opacity;
                }
            }

            return opacity;
        }

        /// <summary>Puts the blur window under the popup's frame (made when it is needed), or takes it away when the popup is not to be seen.</summary>
        // How long the frame has to have stayed where it is before the blur is put under it (milliseconds): a popup slides in and away, and a
        // blur that followed it was a few frames behind (a strip of it was seen below the popup), and took time of every frame it moved.
        private const long RestMilliseconds = 80;

        // How far ahead of the popup's picture the blur is, in milliseconds of the popup's travel.
        private const double LeadMilliseconds = 16;

        // The same going down (the popup sliding away).
        private const double DownLeadMilliseconds = 16;

        public static void Update(FrameworkElement element, Thickness room) => UpdateInner(element, room);

        private static void UpdateInner(FrameworkElement element, Thickness room)
        {
            if (!Wanted(element) || !Allowed(element) || !element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0 ||
                PresentationSource.FromVisual(element) is not HwndSource source || source.Handle == IntPtr.Zero ||
                source.RootVisual is not Visual root || source.CompositionTarget == null)
            {
                Hide(element);
                return;
            }

            try
            {
                IntPtr popup = source.Handle;
                LastUpdated[popup] = (element, room);
                if (Suppressed.Contains(popup))
                {
                    Hide(element);
                    return;
                }

                if (!IsWindowVisible(popup) || !GetWindowRect(popup, out RECT window))
                {
                    Hide(element);
                    return;
                }

                // The frame on the screen (what of it is in the popup's window): the window is at its own top left, in device pixels.
                // The frame on the screen, from the border it draws (the frame control has a margin inside its template, so its own box is bigger
                // than what is seen: the blur was a pixel or two off the frame's edges, and its square corners showed outside the rounded ones).
                Matrix toDevice = source.CompositionTarget.TransformToDevice;
                FrameworkElement drawn = DrawnFrame(element);
                Point elementOrigin = element.PointToScreen(new Point(0, 0));
                Point frameTopLeft, frameBottomRight;
                if (drawn != null)
                {
                    frameTopLeft = drawn.PointToScreen(new Point(0, 0));
                    frameBottomRight = drawn.PointToScreen(new Point(drawn.ActualWidth, drawn.ActualHeight));
                }
                else
                {
                    Rect byRoom = new Rect(room.Left, room.Top,
                        Math.Max(0, element.ActualWidth - room.Left - room.Right),
                        Math.Max(0, element.ActualHeight - room.Top - room.Bottom));
                    frameTopLeft = element.PointToScreen(byRoom.TopLeft);
                    frameBottomRight = element.PointToScreen(byRoom.BottomRight);
                }

                int left = Math.Max(window.Left, (int)Math.Round(frameTopLeft.X));
                int top = Math.Max(window.Top, (int)Math.Round(frameTopLeft.Y));
                int right = Math.Min(window.Right, (int)Math.Round(frameBottomRight.X));
                int bottom = Math.Min(window.Bottom, (int)Math.Round(frameBottomRight.Y));
                if (Edges.TryGetValue(popup, out int edge))
                {
                    bottom = Math.Min(bottom, edge);
                }

                if (right <= left || bottom <= top)
                {
                    Hide(element);
                    return;
                }

                var place = (left, top, right - left, bottom - top);
                Backdrop backdrop = Backdrops.GetOrCreateValue(element);
                long now = Environment.TickCount64;

                // A popup whose control fills its window (they do: the room round the frame is for the shadow) has its frame where the window
                // is, less that room, once it has slid in. The blur window is that size and place, and stays (a window that is resized or
                // made again every frame is slow, and its blur is seen late): what changes while the popup slides is the part of it that shows.
                double dpiX = toDevice.M11, dpiY = toDevice.M22;
                bool fills = Math.Abs(element.ActualWidth * dpiX - (window.Right - window.Left)) < 2 &&
                             Math.Abs(element.ActualHeight * dpiY - (window.Bottom - window.Top)) < 2;
                if (fills && EffectiveOpacity(element) >= 0.9)
                {
                    double slideX = elementOrigin.X - window.Left, slideY = elementOrigin.Y - window.Top;
                    int restLeft = (int)Math.Round(frameTopLeft.X - slideX);
                    int restTop = (int)Math.Round(frameTopLeft.Y - slideY);
                    int restRight = (int)Math.Round(frameBottomRight.X - slideX);
                    int restBottom = (int)Math.Round(frameBottomRight.Y - slideY);
                    FollowSlide(element, backdrop, popup, (restLeft, restTop, restRight - restLeft, restBottom - restTop), place, dpiX);
                    return;
                }

                // Moving (sliding in or away) or fading: no blur until it is still.
                if (place != backdrop.LastSeen)
                {
                    backdrop.LastSeen = place;
                    backdrop.LastChange = now;
                    Hide(element);
                    return;
                }

                if (now - backdrop.LastChange < RestMilliseconds || EffectiveOpacity(element) < 0.95)
                {
                    Hide(element);
                    return;
                }

                if (backdrop.Handle == IntPtr.Zero)
                {
                    EnsureClass();
                    backdrop.Handle = CreateWindowEx(WsExToolWindow | WsExNoActivate | WsExTopmost | WsExTransparent, ClassName, "", WsPopup,
                        0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
                    if (backdrop.Handle == IntPtr.Zero)
                    {
                        return;
                    }

                    int kind = element.TryFindResource("BlurBehindKind") is int theme && theme != 4 ? theme : 3;
                    AskForBlur(backdrop.Handle, kind);
                }

                // Just under the popup: put there again when the popup was moved up over it or the frame has moved or changed size.
                bool underPopup = GetWindow(popup, GwHwndNext) == backdrop.Handle;
                if (!backdrop.Shown || place != backdrop.Place || !underPopup)
                {
                    SetWindowPos(backdrop.Handle, popup, place.Item1, place.Item2, place.Item3, place.Item4, SwpNoActivate | SwpNoOwnerZOrder);
                    if (place.Item3 != backdrop.Place.Item3 || place.Item4 != backdrop.Place.Item4)
                    {
                        // The corners are the frame's (the blur is not seen outside its rounded corners): this window is a plain one, which a region cuts.
                        double radius = element.TryFindResource("PopupBlurCornerRadius") is double r ? r : 0;
                        int diameter = (int)Math.Round(2 * radius * toDevice.M11);
                        SetWindowRgn(backdrop.Handle, diameter > 0
                            ? CreateRoundRectRgn(0, 0, place.Item3 + 1, place.Item4 + 1, diameter, diameter)
                            : IntPtr.Zero, true);
                    }

                    backdrop.Place = place;
                }

                if (!backdrop.Shown)
                {
                    backdrop.Shown = true;
                    ShowWindow(backdrop.Handle, SwShowNoActivate);
                    // (Shown, it is put under the popup again: showing a window can bring it up.)
                    SetWindowPos(backdrop.Handle, popup, 0, 0, 0, 0, SwpNoActivate | SwpNoOwnerZOrder | SwpNoMove | SwpNoSize);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>
        /// The blur window the size and place of what of the frame is in view (the frame slides in and away inside the popup's window, or with
        /// it): a region cut from a window that is the size of the whole frame was not taken by the blur for the first frames, which showed a box
        /// of it over the popup's resting place while the popup was still coming in. The corners are the frame's once it is all in view.
        /// </summary>
        private static void FollowSlide(FrameworkElement element, Backdrop backdrop, IntPtr popup, (int, int, int, int) rest, (int, int, int, int) visible, double dpi)
        {
            if (backdrop.Handle == IntPtr.Zero)
            {
                EnsureClass();
                backdrop.Handle = CreateWindowEx(WsExToolWindow | WsExNoActivate | WsExTopmost | WsExTransparent, ClassName, "", WsPopup,
                    0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
                if (backdrop.Handle == IntPtr.Zero)
                {
                    return;
                }

                int kind = element.TryFindResource("BlurBehindKind") is int theme && theme != 4 ? theme : 3;
                backdrop.Kind = kind;
                AskForBlur(backdrop.Handle, kind);

                // The corners are rounded by the system (the blur is cut to them, which a region on this window was not: the frame's rounded
                // corners had square blur behind them).
                int corners = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(backdrop.Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref corners, sizeof(int));
            }

            bool whole = visible.Item3 >= rest.Item3 - 1 && visible.Item4 >= rest.Item4 - 1;

            // The blur is a few milliseconds ahead of the popup's picture (which WPF hands on a frame later): while the popup comes up fast
            // its top edge was a gray band above the popup. The top is held back by what the popup moves in that time.
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            int rawTop = visible.Item2;
            // (A window that is moved by the slide has its picture at once: the blur is not ahead of it, and is behind it by the system's own delay.)
            double leadMs = LeadMilliseconds;
            if (backdrop.PreviousTicks != 0 && rawTop != backdrop.PreviousTop && !whole && leadMs != 0)
            {
                double elapsedMs = (now - backdrop.PreviousTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (elapsedMs > 0 && elapsedMs < 100)
                {
                    // Up: its top is held back by what the popup moves in that time. Down (the popup going away): the same, the other way, or a band
                    // of blur stayed above the popup where it had been.
                    double speed = Math.Abs(backdrop.PreviousTop - rawTop) / elapsedMs;
                    int hold = (int)Math.Min(visible.Item4 - 1, Math.Round(speed * (rawTop < backdrop.PreviousTop ? leadMs : DownLeadMilliseconds)));
                    if (hold > 0)
                    {
                        visible = (visible.Item1, visible.Item2 + hold, visible.Item3, visible.Item4 - hold);
                    }
                }
            }

            backdrop.PreviousTop = rawTop;
            backdrop.PreviousTicks = now;
            bool underPopup = GetWindow(popup, GwHwndNext) == backdrop.Handle;
            if (visible != backdrop.Place || !underPopup)
            {
                SetWindowPos(backdrop.Handle, popup, visible.Item1, visible.Item2, visible.Item3, visible.Item4, SwpNoActivate | SwpNoOwnerZOrder);
                backdrop.Place = visible;
            }

            // The system's rounding of the window is not always there (a theme or a glass tool can switch it off, and the blur then showed
            // square corners behind the frame's rounded ones): the window's own region rounds it too, once the frame is all in view.
            (int, int) roundedFor = whole ? (visible.Item3, visible.Item4) : (0, 0);
            if (roundedFor != backdrop.RoundedFor)
            {
                backdrop.RoundedFor = roundedFor;
                double radius = element.TryFindResource("PopupBlurCornerRadius") is double r ? r : 0;
                int diameter = (int)Math.Round(2 * radius * dpi);
                SetWindowRgn(backdrop.Handle, whole && diameter > 0
                    ? CreateRoundRectRgn(0, 0, visible.Item3 + 1, visible.Item4 + 1, diameter, diameter)
                    : IntPtr.Zero, true);
                // (The blur takes the region it is asked for after it: a region set later was not cut to.)
                AskForBlur(backdrop.Handle, whole && diameter > 0 ? 0 : backdrop.Kind);
                if (whole && diameter > 0)
                {
                    // Glass tools that draw the blur themselves take its shape from this call, not from the window's region.
                    var blur = new DwmBlurBehind
                    {
                        Flags = 3, // DWM_BB_ENABLE | DWM_BB_BLURREGION
                        Enable = true,
                        Region = CreateRoundRectRgn(0, 0, visible.Item3 + 1, visible.Item4 + 1, diameter, diameter),
                    };
                    DwmEnableBlurBehindWindow(backdrop.Handle, ref blur);
                    DeleteObject(blur.Region);
                }
            }

            if (!backdrop.Shown)
            {
                backdrop.Shown = true;
                ShowWindow(backdrop.Handle, SwShowNoActivate);
                SetWindowPos(backdrop.Handle, popup, 0, 0, 0, 0, SwpNoActivate | SwpNoOwnerZOrder | SwpNoMove | SwpNoSize);
            }
        }

        /// <summary>The element that draws the frame: the outermost border of the template of the control that comes after the shadow in its grid
        /// (the frame is what the shadow is put behind); null when there is none to be found.</summary>
        /// <summary>The element that draws the frame the shadow control is put behind (null when there is none to be found).</summary>
        internal static FrameworkElement FrameOf(FrameworkElement shadow) => DrawnFrame(shadow);

        private static FrameworkElement DrawnFrame(FrameworkElement element)
        {
            if (VisualTreeHelper.GetParent(element) is Panel panel)
            {
                int index = panel.Children.IndexOf(element);
                for (int i = index + 1; index >= 0 && i < panel.Children.Count; i++)
                {
                    if (panel.Children[i] is FrameworkElement sibling && sibling.IsVisible && sibling.ActualWidth > 0 && sibling.ActualHeight > 0)
                    {
                        if (sibling is Control && VisualTreeHelper.GetChildrenCount(sibling) > 0 && VisualTreeHelper.GetChild(sibling, 0) is FrameworkElement frame &&
                            (frame is Border || frame is Panel) && frame.ActualWidth > 0 && frame.ActualHeight > 0)
                        {
                            return frame;
                        }

                        return sibling;
                    }
                }
            }

            return null;
        }

        private static void Hide(FrameworkElement element)
        {
            if (Backdrops.TryGetValue(element, out Backdrop backdrop) && backdrop.Shown)
            {
                backdrop.Shown = false;
                ShowWindow(backdrop.Handle, SwHide);
            }
        }

        /// <summary>Takes the blur window away when the popup is gone.</summary>
        public static void Remove(FrameworkElement element)
        {
            if (Backdrops.TryGetValue(element, out Backdrop backdrop))
            {
                if (backdrop.Handle != IntPtr.Zero)
                {
                    DestroyWindow(backdrop.Handle);
                }

                Backdrops.Remove(element);
            }

            foreach (IntPtr key in new System.Collections.Generic.List<IntPtr>(LastUpdated.Keys))
            {
                if (LastUpdated[key].Element == element)
                {
                    LastUpdated.Remove(key);
                    Edges.Remove(key);
                }
            }
        }
    }
}
