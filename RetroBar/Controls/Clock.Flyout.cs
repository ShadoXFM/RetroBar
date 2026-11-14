using RetroBar.Utilities;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace RetroBar.Controls
{
    /// <summary>
    /// The clock's own flyout (ClockClickOption.OpenGadgetCalendar): two popups like the widgets' (their glass frame, their slide), which come
    /// in from the right together - the calendar (the clock and the month of ClockFlyout) over the taskbar, and over it the notifications
    /// (NotificationsPanel). It opens on a click on the clock and closes on a click elsewhere, on Escape, or on a second click on the clock.
    /// </summary>
    public partial class Clock
    {
        private const double FlyoutShadowRoom = 6;
        private const double FlyoutFrameStyleMargin = 2.4;
        // How many pixels less than the calendar's the gap to the monitor's right edge is made (by the room the window has there for the shadow).
        private const double FlyoutRightReduce = 4;
        // (A test, off: the frames against the right edge of the screen.)
        private const bool FlyoutAgainstRightEdge = false;
        // The space between the two frames as drawn (the shadows' rooms overlap).
        private const double FlyoutPanelGap = 4;

        // One popup of the flyout: its frame (the glass), the slide that brings it in and takes it away.
        private sealed class FlyoutPanel
        {
            public Popup Popup;
            public PopupSlide Slide;
            public Grid Frame;
            public ContentControl Content;
        }

        private FlyoutPanel _sidebarPanel;
        private ClockFlyout _sidebarFlyout;
        private NotificationsPanel _sidebarNotifications;
        private FlyoutPanel _calendarPanel;
        private FlyoutPanel _notificationsPanel;
        // A frame of its own, with nothing in it, at the notifications' bottom edge: while the list opens up and closes the window of the
        // notifications is moved and cut off at the bottom, and the bottom of the frame (its border, its corners, its shadow) is this one's.
        private FlyoutPanel _bottomEdgePanel;
        private TextBlock _bottomEdgeLabel;
        private MenuSeparatorLine _bottomEdgeLine;
        private ClockFlyout _flyoutContent;
        private NotificationsPanel _notificationsContent;
        private InputWatcher _flyoutInput;

        [StructLayout(LayoutKind.Sequential)]
        private struct FlyoutRect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out FlyoutRect rect);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        // Where the panels come from: the right (the taskbar being at the bottom, as it is for most); on the other edges, as the widgets' popups do.
        private static SlideDirection FlyoutSlideDirection() => Settings.Instance.Edge switch
        {
            ManagedShell.AppBar.AppBarEdge.Left => SlideDirection.Left,
            ManagedShell.AppBar.AppBarEdge.Right => SlideDirection.Right,
            ManagedShell.AppBar.AppBarEdge.Top => SlideDirection.Up,
            _ => SlideDirection.Right,
        };

        private FlyoutPanel MakeFlyoutPanel(FrameworkElement inner, Func<double?> overlap)
        {
            var panel = new FlyoutPanel();
            var shadow = new PopupShadow();
            panel.Content = new ContentControl
            {
                Margin = new Thickness(6),
                Tag = "WidgetPopup",
                Content = new Grid
                {
                    Margin = new Thickness(5.4, 5.8, 5.4, 5.8),
                    Children = { inner },
                },
            };
            panel.Content.SetResourceReference(StyleProperty, "TaskButtonThumbnail");

            var slideHost = new Grid { Children = { shadow, panel.Content } };
            panel.Frame = new Grid
            {
                ClipToBounds = true,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                Children = { slideHost },
            };
            TextOptions.SetTextFormattingMode(panel.Frame, TextFormattingMode.Display);
            // A right click in it does nothing (it would open the taskbar's menu over it).
            panel.Frame.PreviewMouseRightButtonDown += (s, e) => e.Handled = true;
            panel.Frame.PreviewMouseRightButtonUp += (s, e) => e.Handled = true;

            panel.Popup = new Popup
            {
                StaysOpen = true,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.None,
                Child = panel.Frame,
            };

            PopupBlur.SetGate(panel.Frame, () => false);
            panel.Slide = new PopupSlide(panel.Popup, panel.Frame, slideHost, FlyoutSlideDirection, overlap);
            panel.Slide.KeepFrameNearEdges(panel.Content, FlyoutShadowRoom);
            // On a taskbar at the top or bottom the panels come in from the right edge of the screen as windows that are moved (what is past the
            // monitor's edge is cut off), which keeps up with the monitor's refresh rate where sliding the content inside the window does not.
            panel.Slide.SidewaysWindowSlide = Settings.Instance.Edge is not (ManagedShell.AppBar.AppBarEdge.Left or ManagedShell.AppBar.AppBarEdge.Right);
            return panel;
        }

        private void EnsureFlyout()
        {
            if (_calendarPanel != null)
            {
                return;
            }

            _flyoutContent = new ClockFlyout();
            _notificationsContent = new NotificationsPanel();
            _notificationsContent.NotificationActivated += (s, e) => _calendarPanel?.Slide.Hide();

            _calendarPanel = MakeFlyoutPanel(_flyoutContent, FlyoutWindowOverlap);
            _notificationsPanel = MakeFlyoutPanel(_notificationsContent, () => null);
            // (as wide as the notifications: their content is 200 wide with 4 each side)
            // (its text is the button the list has under it, and the line over it: made as they are, and put where they are when the list moves)
            _bottomEdgeLabel = new TextBlock
            {
                FontSize = 11, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(8, 1, 8, 0),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = 14,
            };
            _bottomEdgeLabel.SetResourceReference(TextBlock.ForegroundProperty, "ButtonForeground");
            _bottomEdgeLabel.SetResourceReference(TextBlock.FontFamilyProperty, "WidgetLabelFontFamily");
            _bottomEdgeLine = new MenuSeparatorLine { VerticalAlignment = VerticalAlignment.Bottom };
            _bottomEdgePanel = MakeFlyoutPanel(new Border { Width = 208, Height = 70, Child = new Grid { Children = { _bottomEdgeLine, _bottomEdgeLabel } } }, () => null);
            _bottomEdgePanel.Popup.Opened += (s, e) =>
            {
                ShowBottomEdge(0);
                Dispatcher.BeginInvoke(new Action(AlignRightGap), System.Windows.Threading.DispatcherPriority.Loaded);
            };
            // The window of the bottom edge shows what the moving window's cut hides: it goes in the same breath as the cut is taken off (one
            // after the other, with a frame between, there were both - darker, glass over glass - or neither - a hole in the panel).
            _notificationsPanel.Slide.GrowCleared += () => ShowBottomEdge(0);
            _notificationsContent.Growing = (growing, strip, look) =>
            {
                if (growing)
                {
                    _bottomEdgeLabel.Text = look?.Label ?? "";
                    _bottomEdgeLabel.Margin = new Thickness(0, 4, 0, look?.LabelBottom ?? 0);
                    // (the list is 200 wide with 4 on each side, as is the window)
                    _bottomEdgeLine.Margin = new Thickness(4, 0, 4, look?.LineBottom ?? 0);
                    // (the pointer is on it: it has just been clicked)
                    _bottomEdgeLabel.Opacity = look?.Hot == true ? 1 : 0.7;
                    _bottomEdgeLabel.TextDecorations = look?.Hot == true ? TextDecorations.Underline : null;
                    _bottomEdgeLabel.UpdateLayout();
                    ShowBottomEdge(strip);
                }
                else
                {
                    // (the bottom edge's window goes when the moving window's cut is taken off: see GrowCleared)
                }
            };
            _calendarPanel.Slide.Closed += StopWatchingFlyout;
            _calendarPanel.Slide.Closed += () => _bottomEdgePanel.Popup.IsOpen = false;
            // The notifications are moved by the calendar's slide (one animation for both windows).
            _notificationsPanel.Slide.Leader = _calendarPanel.Slide;
            _notificationsContent.Slide = _notificationsPanel.Slide;
            // They go away together.
            _calendarPanel.Slide.Hiding += time => _notificationsPanel.Slide.Hide(time);
            // (at the priority at which the popup's own work for the edge is done, and after it, and before the slide starts: later, the frames moved
            // by those pixels after the slide had ended)
            _calendarPanel.Popup.Opened += (s, e) => Dispatcher.BeginInvoke(new Action(AlignRightGap), System.Windows.Threading.DispatcherPriority.Loaded);
            // The frame at the edge of the screen is as far from it as the calendar's is (the notifications are not over the taskbar, so they
            // cannot work that gap out themselves).
            _notificationsPanel.Popup.Opened += (s, e) => Dispatcher.BeginInvoke(new Action(AlignRightGap), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // The room at the right of the three windows' frames: what the calendar's has (made for it by PopupSlide.KeepFrameNearEdges, so that the
        // frame is as far from the edge of the screen as it is from the taskbar), a few pixels less, and the same for all three.
        private double? _calendarRightRoom;

        // The margin the style has round its frame (the template's grid): the drawn frame is that much inside the control, and the control is the
        // room for the shadow inside the window.
        private static Thickness FrameChrome(FlyoutPanel panel)
        {
            panel.Content.ApplyTemplate();
            return VisualTreeHelper.GetChildrenCount(panel.Content) > 0 && VisualTreeHelper.GetChild(panel.Content, 0) is FrameworkElement element
                ? element.Margin
                : default;
        }

        private void AlignRightGap()
        {
            if (_calendarPanel == null || _notificationsPanel == null || _bottomEdgePanel == null)
            {
                return;
            }

            _calendarRightRoom ??= _calendarPanel.Content.Margin.Right;
            double right = Math.Max(0, _calendarRightRoom.Value - FlyoutRightReduce / VisualTreeHelper.GetDpi(this).DpiScaleX);
            foreach (FlyoutPanel panel in new[] { _calendarPanel, _notificationsPanel, _bottomEdgePanel })
            {
                Thickness margin = panel.Content.Margin;
                double room = right;
                if (FlyoutAgainstRightEdge)
                {
                    // The frame itself against the edge of the screen: no room at all on that side, and the style's own margin round the frame taken
                    // off too (a negative margin).
                    room = -FrameChrome(panel).Right;
                }

                panel.Content.Margin = new Thickness(margin.Left, margin.Top, room, margin.Bottom);
            }
        }

        private void ToggleFlyout()
        {
            if (SidebarMode)
            {
                ToggleSidebar();
                return;
            }

            EnsureFlyout();
            if (_calendarPanel.Popup.IsOpen && !_calendarPanel.Slide.Closing)
            {
                _calendarPanel.Slide.Hide();
                return;
            }

            // Not one of the widgets' hover popups (those are one at a time, and the pointer going over another widget closes the open one): the
            // flyout stays when the pointer goes over the widgets; what is open of theirs slides away first, as for the media popup.
            if (StartMenuMonitor.IsStartMenuVisible)
            {
                return;
            }

            HoverPopups.CloseThen(() =>
            {
                _calendarRightRoom = null;
                _flyoutContent.Culture = LanguageCulture.Current;
                _flyoutContent.GoToToday();
                _notificationsContent.LoadNotifications();

                _calendarPanel.Popup.PlacementTarget = this;
                _calendarPanel.Popup.Placement = PlacementMode.Custom;
                _calendarPanel.Popup.CustomPopupPlacementCallback = PlaceFlyout;
                _notificationsPanel.Popup.PlacementTarget = this;
                _notificationsPanel.Popup.Placement = PlacementMode.Custom;
                _notificationsPanel.Popup.CustomPopupPlacementCallback = PlaceNotifications;

                _bottomEdgePanel.Popup.PlacementTarget = this;
                _bottomEdgePanel.Popup.Placement = PlacementMode.Custom;
                _bottomEdgePanel.Popup.CustomPopupPlacementCallback = PlaceNotifications;

                _calendarPanel.Slide.Show();
                _notificationsPanel.Slide.Show();
                _bottomEdgePanel.Popup.IsOpen = true;
                StartWatchingFlyout();
            });
        }

        // The bottom edge's window shows the bottom 'strip' pixels of itself (0: nothing at all, it cannot be seen or clicked).
        private void ShowBottomEdge(int strip)
        {
            if (_bottomEdgePanel?.Popup is not { IsOpen: true } || PresentationSource.FromVisual(_bottomEdgePanel.Frame) is not HwndSource source ||
                !GetWindowRect(source.Handle, out FlyoutRect window))
            {
                return;
            }

            int width = window.Right - window.Left, height = window.Bottom - window.Top;
            SetWindowRgn(source.Handle, CreateRectRgn(0, strip <= 0 ? 0 : Math.Max(0, height - strip), strip <= 0 ? 0 : width, strip <= 0 ? 0 : height), true);
        }

        // ---- The sidebar ----
        // One panel, as high as the screen above the taskbar, against the right edge of the screen: the notifications over the calendar, at the
        // bottom of it, in glass that comes in from the right like the Windows Vista sidebar (only the glass's left edge shows: its top, right and
        // bottom edges are put outside the window).

        private const double SidebarWidth = 232;

        // The sidebar is a style of its own (the clock's click action in the properties); it needs the taskbar at the bottom, and is the
        // panels over the calendar otherwise.
        private bool SidebarMode => Settings.Instance.ClockClickAction == ClockClickOption.OpenGadgetSidebar &&
                                    Settings.Instance.Edge == ManagedShell.AppBar.AppBarEdge.Bottom;

        [StructLayout(LayoutKind.Sequential)]
        private struct SidebarMonitorInfo
        {
            public int Size;
            public FlyoutRect Monitor, Work;
            public int Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref SidebarMonitorInfo info);

        // The screen's top and right, and where the taskbar's top is (pixels), of the monitor the clock is on.
        private bool GetSidebarBounds(out int top, out int right, out int barTop)
        {
            top = right = barTop = 0;
            if (Window.GetWindow(this) is not Window bar || PresentationSource.FromVisual(bar) is not HwndSource source ||
                !GetWindowRect(source.Handle, out FlyoutRect barRect))
            {
                return false;
            }

            var info = new SidebarMonitorInfo { Size = Marshal.SizeOf<SidebarMonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(source.Handle, 2), ref info))
            {
                return false;
            }

            top = info.Monitor.Top;
            right = info.Monitor.Right;
            barTop = barRect.Top;
            return true;
        }

        private StackPanel _sidebarCalendar;

        private void EnsureSidebar()
        {
            if (_sidebarPanel != null)
            {
                return;
            }

            _sidebarFlyout = new ClockFlyout();
            _sidebarNotifications = new NotificationsPanel();
            _sidebarNotifications.NotificationActivated += (s, e) => _sidebarPanel?.Slide.Hide();

            // The notifications from the top, the calendar at the bottom, a line over it. (Centred in the glass by hand: 12 each side of the 208.)
            var calendar = new StackPanel { Children = { new MenuSeparatorLine { Margin = new Thickness(4, 0, 4, 4) }, _sidebarFlyout } };
            _sidebarCalendar = calendar;
            var stack = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 208,
                Margin = new Thickness(7, 11, 0, 12),
                RowDefinitions = { new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }, new RowDefinition { Height = GridLength.Auto } },
            };
            _sidebarNotifications.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(_sidebarNotifications, 0);
            Grid.SetRow(calendar, 1);
            stack.Children.Add(_sidebarNotifications);
            stack.Children.Add(calendar);

            // The style's frame, with its top, right and bottom edges (and corners) outside the window: its left edge is at the window's.
            var content = new ContentControl
            {
                Margin = new Thickness(-4, -12, -12, -12),
                Tag = "WidgetPopup",
                Content = stack,
            };
            content.SetResourceReference(StyleProperty, "TaskButtonThumbnail");

            // The shadow control is what the system's blur (OpenGlass) is put behind the frame by, as the other popups' glass has it.
            var slideHost = new Grid { Children = { new PopupShadow(), content } };
            var frame = new Grid
            {
                ClipToBounds = true,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                Width = SidebarWidth,
                Children = { slideHost },
            };
            TextOptions.SetTextFormattingMode(frame, TextFormattingMode.Display);
            frame.PreviewMouseRightButtonDown += (s, e) => e.Handled = true;
            frame.PreviewMouseRightButtonUp += (s, e) => e.Handled = true;

            var popup = new Popup
            {
                StaysOpen = true,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.None,
                Child = frame,
            };
            var slide = new PopupSlide(popup, frame, slideHost, () => SlideDirection.Right, () => null) { SidewaysWindowSlide = true };
            _sidebarPanel = new FlyoutPanel { Popup = popup, Slide = slide, Frame = frame, Content = content };
            slide.Closed += StopWatchingFlyout;
        }

        private void ToggleSidebar()
        {
            EnsureSidebar();
            if (_sidebarPanel.Popup.IsOpen && !_sidebarPanel.Slide.Closing)
            {
                _sidebarPanel.Slide.Hide();
                return;
            }

            if (StartMenuMonitor.IsStartMenuVisible || !GetSidebarBounds(out int top, out _, out int barTop))
            {
                return;
            }

            HoverPopups.CloseThen(() =>
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                double height = (barTop - top) / dpi.DpiScaleY;
                _sidebarPanel.Frame.Height = height;
                _sidebarFlyout.Culture = LanguageCulture.Current;
                _sidebarFlyout.GoToToday();
                // As many notifications as the glass has room for over the calendar: its height, less the calendar's, the notifications' own title
                // and the room round them.
                _sidebarCalendar.Measure(new Size(SidebarWidth, double.PositiveInfinity));
                _sidebarNotifications.FitHeight = Math.Max(60, height - _sidebarCalendar.DesiredSize.Height - 23 - 60);
                _sidebarNotifications.LoadNotifications();

                _sidebarPanel.Popup.PlacementTarget = this;
                _sidebarPanel.Popup.Placement = PlacementMode.Custom;
                _sidebarPanel.Popup.CustomPopupPlacementCallback = PlaceSidebar;
                _sidebarPanel.Slide.Show();
                StartWatchingFlyout();
            });
        }

        // At the top of the monitor, against its right edge (relative to the clock, in pixels).
        private CustomPopupPlacement[] PlaceSidebar(Size popupSize, Size targetSize, Point offset)
        {
            if (!GetSidebarBounds(out int top, out int right, out _))
            {
                return new[] { new CustomPopupPlacement(new Point(0, 0), PopupPrimaryAxis.Horizontal) };
            }

            Point clock = PointToScreen(new Point(0, 0));
            return new[] { new CustomPopupPlacement(new Point(right - popupSize.Width - clock.X, top - clock.Y), PopupPrimaryAxis.Horizontal) };
        }

        private void StartWatchingFlyout()
        {
            _flyoutInput ??= new InputWatcher();
            _flyoutInput.MouseDown -= FlyoutInput_OnMouseDown;
            _flyoutInput.MouseDown += FlyoutInput_OnMouseDown;
            _flyoutInput.KeyDown -= FlyoutInput_OnKeyDown;
            _flyoutInput.KeyDown += FlyoutInput_OnKeyDown;
            _flyoutInput.Start();
        }

        private void StopWatchingFlyout() => _flyoutInput?.Stop();

        private static bool CursorIsOn(FlyoutPanel panel, int x, int y)
        {
            return panel != null && panel.Popup.IsOpen && PresentationSource.FromVisual(panel.Frame) is HwndSource source &&
                   GetWindowRect(source.Handle, out FlyoutRect window) &&
                   x >= window.Left && x < window.Right && y >= window.Top && y < window.Bottom;
        }

        private bool CursorIsOnFlyout(int x, int y) => CursorIsOn(_sidebarPanel, x, y) || CursorIsOn(_calendarPanel, x, y) || CursorIsOn(_notificationsPanel, x, y);

        // The slide of the flyout that is open (the sidebar, or the calendar with the notifications over it), if any.
        private PopupSlide OpenFlyoutSlide =>
            _sidebarPanel is { Popup.IsOpen: true } ? _sidebarPanel.Slide : _calendarPanel is { Popup.IsOpen: true } ? _calendarPanel.Slide : null;

        private bool CursorIsOnClock(int x, int y)
        {
            try
            {
                Point topLeft = PointToScreen(new Point(0, 0));
                Point bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
                return x >= topLeft.X && x < bottomRight.X && y >= topLeft.Y && y < bottomRight.Y;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        // A click anywhere else closes it (a click on the clock is the clock's own: it toggles).
        private void FlyoutInput_OnMouseDown(int x, int y)
        {
            PopupSlide open = OpenFlyoutSlide;
            if (open == null || open.Closing || CursorIsOnFlyout(x, y) || CursorIsOnClock(x, y))
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() => open.Hide()), System.Windows.Threading.DispatcherPriority.Send);
        }

        private void FlyoutInput_OnKeyDown(int virtualKey)
        {
            PopupSlide open = OpenFlyoutSlide;
            if (virtualKey != 0x1B || open == null || open.Closing)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() => open.Hide()), System.Windows.Threading.DispatcherPriority.Send);
        }

        // On a taskbar at the bottom: how far the popup's window reaches below the taskbar's top, so that only what is above it shows while it
        // slides (see RamDisplay.GraphWindowOverlap).
        private double? FlyoutWindowOverlap()
        {
            if (Window.GetWindow(this) is not Taskbar taskbar ||
                Settings.Instance.Edge is ManagedShell.AppBar.AppBarEdge.Left or ManagedShell.AppBar.AppBarEdge.Right or ManagedShell.AppBar.AppBarEdge.Top)
            {
                return null;
            }

            if (PopupSlide.MeasureOverlap(_calendarPanel.Frame, taskbar) is double measured)
            {
                return measured;
            }

            double? frameBottom = taskbar.MediaPlayerControl.GetSeekFrameBottom(taskbar);
            if (frameBottom == null)
            {
                try
                {
                    frameBottom = TransformToAncestor(taskbar).Transform(new Point(0, 0)).Y;
                }
                catch (InvalidOperationException)
                {
                    frameBottom = 0;
                }
            }

            return Math.Max(0, frameBottom.Value + FlyoutShadowRoom + FlyoutFrameStyleMargin);
        }

        // The clock is a few pixels short of the right edge of the bar (the tray's gap), and the panels' windows with it: a window that is not at the
        // edge of the screen is not given the margin that makes its frame as far from the edge as from the taskbar (see PopupSlide.KeepFrameNearEdges),
        // and the gap on that side was a few pixels more than on the others. A window that is that near the edge is put against it.
        private double FlushRight(double x, double popupWidth)
        {
            try
            {
                if (Window.GetWindow(this) is Window bar && PresentationSource.FromVisual(bar) is HwndSource source &&
                    GetWindowRect(source.Handle, out FlyoutRect barRect))
                {
                    double windowRight = PointToScreen(new Point(0, 0)).X + x + popupWidth;
                    double shortBy = barRect.Right - windowRight;
                    if (shortBy > 0 && shortBy <= 12 * VisualTreeHelper.GetDpi(this).DpiScaleX)
                    {
                        return x + shortBy;
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }

            return x;
        }

        // The calendar's place: its right edge is on the clock's (it is at the right of the bar), above it, as the other widgets' popups are
        // above theirs.
        private CustomPopupPlacement CalendarPlacement(Size popupSize, Size targetSize, Point offset)
        {
            double sameAsMedia = 0;
            if (Window.GetWindow(this) is Taskbar taskbar && taskbar.MediaPlayerControl.GetSeekFrameBottom(taskbar) is double mediaBottom)
            {
                try
                {
                    sameAsMedia = mediaBottom - TransformToAncestor(taskbar).Transform(new Point(0, 0)).Y;
                }
                catch (InvalidOperationException)
                {
                }
            }

            double rightX = FlushRight(targetSize.Width - popupSize.Width + FlyoutShadowRoom, popupSize.Width);
            double centeredY = (targetSize.Height - popupSize.Height) / 2;
            return Settings.Instance.Edge switch
            {
                ManagedShell.AppBar.AppBarEdge.Left => new CustomPopupPlacement(new Point(targetSize.Width + offset.X - FlyoutShadowRoom, centeredY), PopupPrimaryAxis.Vertical),
                ManagedShell.AppBar.AppBarEdge.Right => new CustomPopupPlacement(new Point(-popupSize.Width + offset.X + FlyoutShadowRoom, centeredY), PopupPrimaryAxis.Vertical),
                ManagedShell.AppBar.AppBarEdge.Top => new CustomPopupPlacement(new Point(rightX, targetSize.Height + offset.Y - FlyoutShadowRoom), PopupPrimaryAxis.Horizontal),
                _ => new CustomPopupPlacement(new Point(rightX, -popupSize.Height + offset.Y + FlyoutShadowRoom + FlyoutFrameStyleMargin + sameAsMedia), PopupPrimaryAxis.Horizontal),
            };
        }

        private CustomPopupPlacement[] PlaceFlyout(Size popupSize, Size targetSize, Point offset) =>
            new[] { CalendarPlacement(popupSize, targetSize, offset) };

        // The notifications are over the calendar (under it, with the taskbar at the top), the same distance from the edge.
        private CustomPopupPlacement[] PlaceNotifications(Size popupSize, Size targetSize, Point offset)
        {
            // The sizes the callback gets are in device pixels: the calendar's (known without waiting for it to be on the screen) is made one too.
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            _calendarPanel.Frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var calendarSize = new Size(_calendarPanel.Frame.DesiredSize.Width * dpi.DpiScaleX, _calendarPanel.Frame.DesiredSize.Height * dpi.DpiScaleY);
            CustomPopupPlacement calendar = CalendarPlacement(calendarSize, targetSize, offset);
            double room = FlyoutShadowRoom * dpi.DpiScaleY, gap = FlyoutPanelGap * dpi.DpiScaleY;

            double x = FlushRight(targetSize.Width - popupSize.Width + FlyoutShadowRoom, popupSize.Width);
            bool horizontalEdge = Settings.Instance.Edge is not (ManagedShell.AppBar.AppBarEdge.Left or ManagedShell.AppBar.AppBarEdge.Right);
            double y = Settings.Instance.Edge == ManagedShell.AppBar.AppBarEdge.Top
                ? calendar.Point.Y + calendarSize.Height - 2 * room + gap
                : calendar.Point.Y + 2 * room - gap - popupSize.Height;
            if (!horizontalEdge)
            {
                x = calendar.Point.X + (Settings.Instance.Edge == ManagedShell.AppBar.AppBarEdge.Left ? 0 : calendarSize.Width - popupSize.Width);
            }

            return new[] { new CustomPopupPlacement(new Point(x, y), horizontalEdge ? PopupPrimaryAxis.Horizontal : PopupPrimaryAxis.Vertical) };
        }
    }
}
