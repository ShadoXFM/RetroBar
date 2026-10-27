using RetroBar.Utilities;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RetroBar.Controls
{
    // In the Windows 11 style of the taskbar (TaskList.IconOnly) a tab shows only its icon, in the middle of its frame. The icon is not the
    // button's content (the theme moves, and clips, the content of a pressed and of an active tab, so it was never in the same place in the
    // three states): it is an image of its own over the button (IconOnlyImage), placed once in the middle of the frame the theme draws.
    public partial class TaskButton
    {
        private TaskList _iconOnlyHost;
        private DependencyPropertyDescriptor _iconOnlyDescriptor;
        private bool _placeIconQueued;

        private void InitIconOnly()
        {
            ReleaseIconOnly();
            _iconOnlyHost = Host;
            if (_iconOnlyHost != null)
            {
                _iconOnlyDescriptor = DependencyPropertyDescriptor.FromProperty(TaskList.IconOnlyProperty, typeof(TaskList));
                _iconOnlyDescriptor.AddValueChanged(_iconOnlyHost, IconOnly_OnChanged);
            }

            AppButton.SizeChanged -= IconOnly_OnButtonSizeChanged;
            AppButton.SizeChanged += IconOnly_OnButtonSizeChanged;
            MonitorAdjustments.Changed -= IconOnly_OnAdjustmentsChanged;
            MonitorAdjustments.Changed += IconOnly_OnAdjustmentsChanged;
            _pressedDescriptor ??= DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, typeof(System.Windows.Controls.Button));
            _pressedDescriptor.RemoveValueChanged(AppButton, IconOnly_OnPressedChanged);
            _pressedDescriptor.AddValueChanged(AppButton, IconOnly_OnPressedChanged);
            // The mouse itself as well: with several windows a click does not always leave the button pressed (the preview of the group, the
            // menu, a drag that has begun take the mouse), and the tab is still being pressed as far as whoever is pressing it can tell.
            RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(IconOnly_OnMouseDown));
            AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(IconOnly_OnMouseDown), true);
            RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(IconOnly_OnMouseUp));
            AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(IconOnly_OnMouseUp), true);

            ApplyIconOnly();
        }

        private DependencyPropertyDescriptor _pressedDescriptor;

        // The theme moves what is in a pressed button down and to the right (by a pixel); the icon here is not in the button, so its layer is
        // moved the same way while the button is pressed. A transform of the layer, whatever the icon and the badge are doing, so it works
        // whenever the tab is pressed (a margin of the icon was lost when the icon was being placed again, or not in place yet).
        private bool _mouseDownOnTab;

        private DispatcherTimer _pressTimer;

        // The press goes on as long as the button is held with the pointer on the tab: the button's own pressed state does not (the preview,
        // a drag that begins or the menu take the mouse from it a moment after the press), so the held button is read from the mouse.
        private void IconOnly_OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            _mouseDownOnTab = true;
            UpdatePressShift();
            if (_pressTimer == null)
            {
                _pressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
                _pressTimer.Tick += (s, args) =>
                {
                    if ((GetAsyncKeyState(0x01) & 0x8000) == 0 || !IsPointerOverTab())
                    {
                        _pressTimer.Stop();
                        _mouseDownOnTab = false;
                    }

                    UpdatePressShift();
                };
            }

            _pressTimer.Start();
        }

        private void IconOnly_OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            _pressTimer?.Stop();
            _mouseDownOnTab = false;
            UpdatePressShift();
        }

        private void IconOnly_OnPressedChanged(object sender, EventArgs e) => UpdatePressShift();

        private void UpdatePressShift()
        {
            bool pressed = _iconOnlyHost?.IconOnly == true && (AppButton.IsPressed || _mouseDownOnTab);
            bool iconOnly = _iconOnlyHost?.IconOnly == true;

            // One device pixel down and to the right. The icon is placed by the monitor tag's snapping (whole pixels), which takes the move
            // as an addition to what it works out (a move of the layout or of a parent was rounded away by it, on one axis or both, for some
            // tabs); the badge is not snapped, so its own transform does.
            Vector nudge = iconOnly ? new Vector(_iconFixX + (pressed ? 1 : 0), _iconFixY + (pressed ? 1 : 0)) : new Vector(0, 0);
            if (MonitorOffset.GetNudge(IconOnlyImage) != nudge)
            {
                MonitorOffset.SetNudge(IconOnlyImage, nudge);
            }

            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double shift = 1 / (dpi > 0 ? dpi : 1);
            IconOnlyBadge.RenderTransform = pressed ? new TranslateTransform(shift, shift) : null;
        }

        private void ReleaseIconOnly()
        {
            AppButton.SizeChanged -= IconOnly_OnButtonSizeChanged;
            MonitorAdjustments.Changed -= IconOnly_OnAdjustmentsChanged;
            _pressedDescriptor?.RemoveValueChanged(AppButton, IconOnly_OnPressedChanged);
            RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(IconOnly_OnMouseDown));
            RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(IconOnly_OnMouseUp));
            _pressTimer?.Stop();
            _mouseDownOnTab = false;
            if (_iconOnlyHost != null)
            {
                _iconOnlyDescriptor?.RemoveValueChanged(_iconOnlyHost, IconOnly_OnChanged);
                _iconOnlyHost = null;
            }
        }

        private void IconOnly_OnChanged(object sender, EventArgs e) => ApplyIconOnly();

        // Raised on the thread of the file watcher: nothing of the tab is touched until it is on the UI thread.
        private void IconOnly_OnAdjustmentsChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_iconOnlyHost?.IconOnly == true)
                {
                    QueuePlaceIcon();
                }
            }));
        }

        private void IconOnly_OnButtonSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_iconOnlyHost?.IconOnly == true)
            {
                QueuePlaceIcon();
            }
        }

        // The program's icon: its executable's (File Explorer's own, as the user may have set it), not the window's, which is the title
        // bar's and changes with what the window shows. Null for a window that has no executable to ask (a packaged app).
        private ImageSource ProgramIconForTab()
        {
            if (Window == null || Window.IsUWP || string.IsNullOrEmpty(Window.WinFileName))
            {
                return null;
            }

            // A packaged app (WhatsApp, say) has its logo in its manifest, not in its executable, which the shell can give only the generic
            // icon of a program for (and an icon that did not display): the window's own icon is the one.
            if (Window.WinFileName.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (Converters.TaskIconConverter.IsFileExplorerWindow(Window))
            {
                var converter = (IMultiValueConverter)FindResource("taskIconConverter");
                return converter.Convert(new object[] { Window, Window.Icon, 2 }, typeof(ImageSource), null, System.Globalization.CultureInfo.CurrentCulture) as ImageSource;
            }

            return GetProgramIcon();
        }

        private void ApplyIconOnly()
        {
            bool only = _iconOnlyHost?.IconOnly == true;
            TaskLabelText.Visibility = only ? Visibility.Collapsed : Visibility.Visible;

            // The button's own icon stays where it is (so the button is as big as ever) but is not seen in this style.
            TaskIconImage.Opacity = only ? 0 : 1;
            TaskOverlayIconImage.Opacity = only ? 0 : 1;
            IconOnlyBadge.Opacity = 0;
            IconOnlyImage.Opacity = 0;
            // Only the icon of a window that has just opened waits to be centered before it is seen (where it is put first is a pixel or so
            // off, and it was seen going to the right place); a tab made anew for a window that was there (the ones after a tab that closed
            // are) is shown at once, as it is not a stranger to its place.
            _iconCentered = !(only && Host?.JustOpened(Window) == true);
            _syncCentered = false;

            IconOnlyImage.Visibility = only ? Visibility.Visible : Visibility.Collapsed;

            if (only)
            {
                ImageSource program = ProgramIconForTab();
                if (program != null)
                {
                    BindingOperations.ClearBinding(IconOnlyImage, Image.SourceProperty);
                    IconOnlyImage.Source = program;
                    MonitorOffset.SetTag(IconOnlyImage, "TaskIconBar");
                }
                else
                {
                    // A packaged app has no executable to ask: the window's own icon.
                    IconOnlyImage.SetBinding(Image.SourceProperty, new Binding(nameof(Image.Source)) { Source = TaskIconImage });
                }

                QueuePlaceIcon();
            }
            else
            {
                BindingOperations.ClearBinding(IconOnlyImage, Image.SourceProperty);
                IconOnlyImage.Source = null;
                MonitorOffset.SetTag(IconOnlyImage, null);
            }
        }

        private bool _iconPlaced;

        // Each layout pass until the icon has a place: a tab that has just opened has no size yet when it is loaded, and the icon is
        // hidden until it has one (see PlaceIcon).
        private Point _lastOrigin = new(double.NaN, double.NaN);

        private void IconOnly_OnLayoutUpdated(object sender, EventArgs e)
        {
            if (_iconOnlyHost?.IconOnly != true)
            {
                LayoutUpdated -= IconOnly_OnLayoutUpdated;
                return;
            }

            if (!_iconPlaced)
            {
                PlaceIcon();
                return;
            }

            // The frame comes and goes with the state of the tab (an active tab has none in some themes), and a tab that has just opened has none
            // until its template is in: the icon is centered again, by the frame or by what a tab at rest has.
            bool hasFrame = DrawnFrame() != null;
            if (_frameWaiting && (hasFrame || _iconOnlyHost.IconTabTarget.X is double known && !double.IsNaN(known)))
            {
                _frameWaiting = false;
                QueuePlaceIcon();
                return;
            }

            if (hasFrame != _hadFrame)
            {
                _hadFrame = hasFrame;
                _pendingFix = null;
                _fixPasses = 0;
                Dispatcher.BeginInvoke(new Action(CenterIconInFrame), DispatcherPriority.ApplicationIdle);
            }

            // The tab has come to be somewhere else on the screen (the others slid, or a window opened or closed): where the borders and the
            // icon fall on the pixels depends on it, a pixel one way or the other, so how the icon sits in its frame is looked at again.
            try
            {
                Point origin = PointToScreen(new Point(0, 0));
                if (origin != _lastOrigin)
                {
                    _lastOrigin = origin;
                    _fixPasses = 0;
                    // At once, before it is drawn where it is now (a pixel off, when the row has moved by a whole slot or less), and again when
                    // things have settled.
                    // Not while it slides (the origin is not on a whole pixel, and it changes every frame): centering is not cheap, it reads the
                    // pixels of the tab, and the slides were jerky with a pass for every tab in every frame.
                    bool whole = Math.Abs(origin.X - Math.Round(origin.X)) < 0.01 && Math.Abs(origin.Y - Math.Round(origin.Y)) < 0.01;
                    if (whole && DateTime.UtcNow - _lastSyncCenter > TimeSpan.FromMilliseconds(120))
                    {
                        _lastSyncCenter = DateTime.UtcNow;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (_iconPlaced && _iconOnlyHost?.IconOnly == true && !AppButton.IsPressed)
                            {
                                CenterIconNow();
                            }
                        }), DispatcherPriority.Render);
                    }
                    Dispatcher.BeginInvoke(new Action(CenterIconInFrame), DispatcherPriority.ApplicationIdle);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void QueuePlaceIcon()
        {
            _iconPlaced = false;
            _fixPasses = 0;
            LayoutUpdated -= IconOnly_OnLayoutUpdated;
            LayoutUpdated += IconOnly_OnLayoutUpdated;

            if (_placeIconQueued)
            {
                return;
            }

            _placeIconQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _placeIconQueued = false;
                PlaceIcon();
            }), DispatcherPriority.Loaded);
        }

        /// <summary>Whether the tab is in use (the active window's, with its menu open or pressed): its frame is not the size of a tab at rest in some themes.</summary>
        public bool IsInUse => Window?.State == ManagedShell.WindowsTasks.ApplicationWindow.WindowState.Active || IsGroupActive || IsContextMenuActive || AppButton.IsPressed;

        // The corrections to the place of the icon were made against the frame of the old theme.
        private void ThemeChangedForIconOnly()
        {
            _iconFixX = 0;
            _iconFixY = 0;

            if (_iconOnlyHost?.IconOnly == true)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ApplyIconOnly();
                    QueuePlaceIcon();
                }), DispatcherPriority.Loaded);
            }
        }

        private (double X, double Y)? _pendingFix;

        // The bright line at the left of the frame and the end of its top line, in device pixels from the left of the tab, read from the
        // tab as it is drawn (the icon's layer left out); null if they cannot be told (a theme with no such lines).
        private (double Left, double Right)? DrawnFrameEdges()
        {
            try
            {
                double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                int width = (int)Math.Ceiling(ActualWidth * dpi);
                int height = (int)Math.Ceiling(ActualHeight * dpi);
                if (width < 8 || height < 8)
                {
                    return null;
                }

                var target = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                Visibility layer = IconOnlyLayer.Visibility;
                IconOnlyLayer.Visibility = Visibility.Hidden;
                try
                {
                    target.Render(this);
                }
                finally
                {
                    IconOnlyLayer.Visibility = layer;
                }

                int stride = width * 4;
                byte[] pixels = new byte[stride * height];
                target.CopyPixels(pixels, stride, 0);

                int Luma(int x, int y)
                {
                    int i = y * stride + x * 4;
                    return (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3;
                }

                // The bar's own colour: a pixel of the margin of the tab, at its left edge.
                int bar = Luma(0, height / 2);

                // The top line: the first row, from the top, with a long run of pixels that are not the bar's colour.
                for (int y = 0; y < height / 2; y++)
                {
                    int bestStart = -1, bestEnd = -1, start = -1;
                    for (int x = 0; x <= width; x++)
                    {
                        bool lit = x < width && Math.Abs(Luma(x, y) - bar) >= 40;
                        if (lit && start < 0)
                        {
                            start = x;
                        }
                        else if (!lit && start >= 0)
                        {
                            if (x - start > bestEnd - bestStart)
                            {
                                bestStart = start;
                                bestEnd = x;
                            }

                            start = -1;
                        }
                    }

                    if (bestEnd - bestStart >= 16)
                    {
                        return (bestStart, bestEnd);
                    }
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        // How far (device pixels, for an icon drawn shownWidth by shownHeight) the middle of the icon's picture - the pixels that are seen
        // (opaque, and not dark) - is from the middle of its image. (0, 0) when it cannot be told.
        private (double X, double Y) ArtCenterOffset(double shownWidth, double shownHeight) => ArtCenterOffset(IconOnlyImage.Source, shownWidth, shownHeight);

        /// <summary>The same for the icon of a pinned program that is not open (a launcher), so it sits where its tab's icon will.</summary>
        internal static (double X, double Y) ArtCenterOffset(ImageSource imageSource, double shownWidth, double shownHeight)
        {
            if (imageSource is not System.Windows.Media.Imaging.BitmapSource source || source.PixelWidth <= 0 || source.PixelHeight <= 0)
            {
                return (0, 0);
            }

            try
            {
                var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                int stride = converted.PixelWidth * 4;
                byte[] data = new byte[stride * converted.PixelHeight];
                converted.CopyPixels(data, stride, 0);
                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                for (int y = 0; y < converted.PixelHeight; y++)
                {
                    for (int x = 0; x < converted.PixelWidth; x++)
                    {
                        // Seen on the taskbar: opaque, and not as dark as the bar itself (the black outline of foobar2000's icon is not seen).
                        int i = y * stride + x * 4;
                        if (data[i + 3] >= 110 && Math.Max(data[i], Math.Max(data[i + 1], data[i + 2])) >= 60)
                        {
                            minX = Math.Min(minX, x);
                            maxX = Math.Max(maxX, x);
                            minY = Math.Min(minY, y);
                            maxY = Math.Max(maxY, y);
                        }
                    }
                }

                if (maxX < 0)
                {
                    return (0, 0);
                }

                // Drawn as large as fits (Uniform) and in the middle of the image's place.
                double scale = Math.Min(shownWidth / converted.PixelWidth, shownHeight / converted.PixelHeight);
                return (((minX + maxX + 1) / 2.0 - converted.PixelWidth / 2.0) * scale, ((minY + maxY + 1) / 2.0 - converted.PixelHeight / 2.0) * scale);
            }
            catch (Exception)
            {
                return (0, 0);
            }
        }
        private double _iconFixX;
        private double _iconFixY;
        private int _fixPasses;
        private bool _frameWaiting;
        private bool _iconCentered;
        private bool _syncCentered;
        private DateTime _lastSyncCenter = DateTime.MinValue;

        private DispatcherTimer _iconRevealTimer;

        private void RevealIcon()
        {
            _iconCentered = true;
            _iconRevealTimer?.Stop();
            if (_iconPlaced && _iconOnlyHost?.IconOnly == true)
            {
                IconOnlyImage.Opacity = 1;
                IconOnlyBadge.Opacity = 1;
            }
        }

        // If it could not be centered by now (no frame, nothing to go by), it is shown where it is.
        private void IconRevealTimer_Tick(object sender, EventArgs e) => RevealIcon();

        /// <summary>The whole pixels to move by to be as near the middle as can be: a half goes to the right/down, always (the middle of a frame is on a
        /// half pixel as often as not, and an icon left where it was by a half either way ended on one side or the other from one start to the next).</summary>
        internal static double WholePixels(double distance) => Math.Floor(distance + 0.5 + 1e-6);

        // Whether the tab had its frame (the theme's named outer border) when its icon was last looked at: an active tab has none in some themes,
        // and the icon is then put where it is for a tab at rest (the task list's IconTabTarget), so it does not move when the tab is or is not active.
        private bool _hadFrame;

        private FrameworkElement DrawnFrame()
        {
            return AppButton.Template?.FindName("ButtonOuterBottomBorder", AppButton) is FrameworkElement frame && frame.ActualWidth > 0 && frame.ActualHeight > 0 ? frame : null;
        }

        /// <summary>The middle of the frame as it is drawn, in device pixels from the top left of the tab, and where the frame starts in the tab; null if the tab
        /// has no frame to read (not laid out, or its template has none).</summary>
        internal (double X, double Y, double FrameLeft, double FrameTop)? FrameCenterInTab()
        {
            try
            {
                FrameworkElement frame = DrawnFrame();
                if (frame == null)
                {
                    return null;
                }

                Point origin = PointToScreen(new Point(0, 0));
                Point frameTopLeft = frame.PointToScreen(new Point(0, 0));
                Point frameBottomRight = frame.PointToScreen(new Point(frame.ActualWidth, frame.ActualHeight));
                // The frame is seen from its bright line at the left to the end of its top line (the borders at its right are nearly the colour of
                // the bar and are not seen), read from the pixels of the tab - where the borders come to be depends on the position of the tab, a
                // pixel one way or the other, so the geometry does not tell. The icon goes in the middle of that.
                double x = DrawnFrameEdges() is (double left, double right) ? (left + 1 + right) / 2 : (frameTopLeft.X + frameBottomRight.X) / 2 - origin.X;
                double y = (frameTopLeft.Y + frameBottomRight.Y) / 2 - origin.Y;
                return (x, y, frameTopLeft.X - origin.X, frameTopLeft.Y - origin.Y);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>The same for a tab that is at rest (not active, pressed or pending a correction), which the task list takes the place of the icons from.</summary>
        internal (double X, double Y, double FrameLeft, double FrameTop)? FrameCenterAtRest()
        {
            if (_iconOnlyHost?.IconOnly != true || !_iconPlaced || _pendingFix != null || IsInUse || !IsLoaded)
            {
                return null;
            }

            return FrameCenterInTab();
        }

        /// <summary>How far (device pixels) the middle of the icon's picture is off where it goes, in the frame of the tab or where a tab at rest has it;
        /// false if there is nothing to go by yet (no frame, nothing from a tab at rest) or the tab is not laid out.</summary>
        private bool MeasureCentering(out double dxPixels, out double dyPixels, out bool hasFrame)
        {
            dxPixels = dyPixels = 0;
            hasFrame = false;
            if (_iconOnlyHost?.IconOnly != true || !IsLoaded || IconOnlyImage.ActualWidth <= 0)
            {
                return false;
            }

            try
            {
                // Where the middle of the icon's picture goes, from the top left of the tab (device pixels, on the screen: what is drawn, after
                // the snapping of the icon to whole pixels, which the layout does not show).
                var frameCenter = FrameCenterInTab();
                hasFrame = frameCenter != null;
                (double X, double Y)? center = null;
                if (frameCenter is (double fx, double fy, double _, double _))
                {
                    center = (fx, fy);
                }
                else if (_iconOnlyHost.IconTabTarget is Vector target && !double.IsNaN(target.X))
                {
                    center = (target.X, target.Y);
                }

                if (center is not (double centerX, double centerY))
                {
                    return false;
                }

                Point origin = PointToScreen(new Point(0, 0));
                Point iconTopLeft = IconOnlyImage.PointToScreen(new Point(0, 0));
                Point iconBottomRight = IconOnlyImage.PointToScreen(new Point(IconOnlyImage.ActualWidth, IconOnlyImage.ActualHeight));
                // The middle of the picture, not of the image: an icon with more empty space at one side (foobar2000's, Opera's) is not
                // in the middle of its frame by its box, and is by what is seen.
                (double artX, double artY) = ArtCenterOffset(iconBottomRight.X - iconTopLeft.X, iconBottomRight.Y - iconTopLeft.Y);
                dxPixels = centerX - ((iconTopLeft.X + iconBottomRight.X) / 2 - origin.X + artX);
                dyPixels = centerY - ((iconTopLeft.Y + iconBottomRight.Y) / 2 - origin.Y + artY);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        // Centered here and now, in the pass that places the icon (a few times at most, the layout brought up to date each time): the pass
        // that comes later can be a while after (a window that has just opened keeps the thread busy), and the icon was seen where it was
        // first put, a pixel or two off, until then. False if it could not be (nothing to go by, not laid out yet).
        private bool CenterIconNow()
        {
            try
            {
                for (int pass = 0; pass < 4; pass++)
                {
                    UpdateLayout();
                    if (!MeasureCentering(out double dx, out double dy, out bool hasFrame))
                    {
                        return false;
                    }

                    _hadFrame = hasFrame;
                    _frameWaiting = false;
                    if (WholePixels(dx) == 0 && WholePixels(dy) == 0)
                    {
                        _pendingFix = null;
                        return true;
                    }

                    _iconFixX = Math.Clamp(_iconFixX + WholePixels(dx), -4, 4);
                    _iconFixY = Math.Clamp(_iconFixY + WholePixels(dy), -4, 4);
                    UpdatePressShift();
                }
            }
            catch (InvalidOperationException)
            {
            }

            return false;
        }

        private void CenterIconInFrame()
        {
            if (_iconOnlyHost?.IconOnly != true || !IsLoaded || AppButton.IsPressed || IconOnlyImage.ActualWidth <= 0)
            {
                return;
            }

            if (!MeasureCentering(out double dxPixels, out double dyPixels, out bool hasFrame))
            {
                // A tab that has just opened has no frame yet (the theme's template is not applied, or it is not laid out): its icon was placed by
                // its button's box and left there - a pixel one way or the other, from one start to the next - and not centered. It is placed
                // again, and centered, when there is something to center it by (see IconOnly_OnLayoutUpdated).
                _frameWaiting = true;
                return;
            }

            _frameWaiting = false;
            _hadFrame = hasFrame;

            if (WholePixels(dxPixels) != 0 || WholePixels(dyPixels) != 0)
            {
                // Only when it is off by the same amount twice running, a render apart: the icon takes a layout pass or two to get where a
                // margin put it (the snapping works on the layout), and a correction made on what the last one had not reached yet went on
                // adding up, a pixel a time, until the icon was out of its frame.
                if (_pendingFix is (double px, double py) && px == dxPixels && py == dyPixels)
                {
                    _pendingFix = null;
                    // Whole device pixels added after the snapping (MonitorOffset.Nudge): a margin of one pixel moved some icons by two (the
                    // rounding of a half pixel), and the correction went back and forth over the middle.
                    _iconFixX = Math.Clamp(_iconFixX + WholePixels(dxPixels), -4, 4);
                    _iconFixY = Math.Clamp(_iconFixY + WholePixels(dyPixels), -4, 4);
                    UpdatePressShift();
                }
                else
                {
                    _pendingFix = (dxPixels, dyPixels);
                    Dispatcher.BeginInvoke(new Action(CenterIconInFrame), DispatcherPriority.ApplicationIdle);
                }
            }
            else
            {
                _pendingFix = null;
                RevealIcon();
            }
        }

        // In the middle of the frame the theme draws (its outer border, which is smaller than the button's box), in whole device pixels.
        private void PlaceIcon()
        {
            if (_iconOnlyHost?.IconOnly != true || !IsLoaded || AppButton.IsPressed)
            {
                return;
            }

            try
            {
                var frame = AppButton.Template?.FindName("ButtonOuterBottomBorder", AppButton) as FrameworkElement;
                FrameworkElement reference = frame != null && frame.ActualWidth > 0 && frame.ActualHeight > 0 ? frame : AppButton;
                _frameWaiting = reference != frame && double.IsNaN(_iconOnlyHost.IconTabTarget.X);
                if (reference.ActualWidth <= 0 || reference.ActualHeight <= 0)
                {
                    // Not laid out yet (a tab that has just opened): its icon is shown once it has a place, not at the corner it would be put
                    // at now and then moved from.
                    IconOnlyImage.Opacity = 0;
                    IconOnlyBadge.Opacity = 0;
                    return;
                }

                Rect bounds = reference.TransformToAncestor(this).TransformBounds(new Rect(0, 0, reference.ActualWidth, reference.ActualHeight));

                double step = _iconOnlyHost.PixelStep > 0 ? _iconOnlyHost.PixelStep : 1;

                // The classic style's own look for the icon: its monitor's TaskIcon adjustment (the Scale, drawn by MonitorOffset as it draws the
                // classic tab's) - its X and Y, which move the icon inside the classic button, are taken off the place so the icon is in the middle.
                MonitorOffsetValue adjust = MonitorAdjustments.Get(MonitorOffset.FindDeviceName(this), "TaskIconBar");

                // With a BitmapScaling in the monitor's TaskIconBar adjustment the icon is not drawn through MonitorOffset's snapped transform but
                // as an image of the scaled size, resampled by WPF in that mode: softer than the classic tab's, which is crisp on whole pixels.
                double shiftX = adjust.X;
                double shiftY = adjust.Y;
                if (!string.IsNullOrEmpty(adjust.BitmapScaling) && Enum.TryParse(adjust.BitmapScaling, out BitmapScalingMode scalingMode))
                {
                    if (MonitorOffset.GetTag(IconOnlyImage) != null)
                    {
                        MonitorOffset.SetTag(IconOnlyImage, null);
                        IconOnlyImage.ClearValue(RenderTransformProperty);
                    }

                    double side = Math.Max(step, Math.Round(16 * (adjust.Scale ?? 1) / step) * step);
                    IconOnlyImage.Width = side;
                    IconOnlyImage.Height = side;
                    RenderOptions.SetBitmapScalingMode(IconOnlyImage, scalingMode);
                    shiftX = 0;
                    shiftY = 0;
                }
                else if (MonitorOffset.GetTag(IconOnlyImage) == null)
                {
                    IconOnlyImage.Width = 16;
                    IconOnlyImage.Height = 16;
                    MonitorOffset.SetTag(IconOnlyImage, "TaskIconBar");
                }

                double left = bounds.Left + (bounds.Width - IconOnlyImage.Width) / 2 - shiftX;
                double top = bounds.Top + (bounds.Height - IconOnlyImage.Height) / 2 - shiftY;
                double iconLeft = Math.Round(left / step) * step;
                double iconTop = Math.Round(top / step) * step;
                IconOnlyImage.Margin = new Thickness(iconLeft, iconTop, 0, 0);

                // The badge at the lower right of the icon, as the classic tab has it, moved by its monitor's TaskBadgeBar adjustment
                // (X and Y, in whole pixels).
                MonitorOffsetValue badge = MonitorAdjustments.Get(MonitorOffset.FindDeviceName(this), "TaskBadgeBar");
                IconOnlyBadge.Margin = new Thickness(Math.Round((iconLeft + IconOnlyImage.Width - IconOnlyBadge.Width + 2 + badge.X) / step) * step,
                    Math.Round((iconTop + IconOnlyImage.Height - IconOnlyBadge.Height + 1 + badge.Y) / step) * step, 0, 0);
                // Centered at once if it can be; if not (nothing to go by yet) it is not seen until it has been, a moment later: the place it is
                // put at first is a pixel or so off, and it was seen going to the right one.
                if (!_iconCentered)
                {
                    IconOnlyImage.Opacity = 0;
                    UpdatePressShift();
                    if (CenterIconNow())
                    {
                        _iconCentered = true;
                    }
                }
                else if (!_syncCentered)
                {
                    // A tab made anew for a window that was there (the style came back, a tab closed) is centered in this pass too, not a
                    // moment later: it was drawn a pixel off, and went to its place a quarter of a second after.
                    _syncCentered = true;
                    CenterIconNow();
                }

                IconOnlyImage.Opacity = _iconCentered ? 1 : 0;
                IconOnlyBadge.Opacity = _iconCentered ? 1 : 0;
                _iconPlaced = true;
                if (!_iconCentered)
                {
                    _iconRevealTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                    _iconRevealTimer.Stop();
                    _iconRevealTimer.Tick -= IconRevealTimer_Tick;
                    _iconRevealTimer.Tick += IconRevealTimer_Tick;
                    _iconRevealTimer.Start();
                }
                // Where the icon ends up is measured, and what it is off the middle of the frame (the snapped transform of the monitor's
                // adjustment rounds its own way) taken up, a few times at most.
                if (_fixPasses < 6)
                {
                    _fixPasses++;
                    Dispatcher.BeginInvoke(new Action(CenterIconInFrame), DispatcherPriority.Loaded);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
