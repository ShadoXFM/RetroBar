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
        private void IconOnly_OnLayoutUpdated(object sender, EventArgs e)
        {
            if (_iconPlaced || _iconOnlyHost?.IconOnly != true)
            {
                LayoutUpdated -= IconOnly_OnLayoutUpdated;
                return;
            }

            PlaceIcon();
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

        // How far (device pixels, for an icon drawn shownWidth by shownHeight) the middle of the icon's picture - the pixels that are seen
        // (opaque, and not dark) - is from the middle of its image. (0, 0) when it cannot be told.
        private (double X, double Y) ArtCenterOffset(double shownWidth, double shownHeight)
        {
            if (IconOnlyImage.Source is not System.Windows.Media.Imaging.BitmapSource source || source.PixelWidth <= 0 || source.PixelHeight <= 0)
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

        private void CenterIconInFrame()
        {
            if (_iconOnlyHost?.IconOnly != true || !IsLoaded || AppButton.IsPressed ||
                AppButton.Template?.FindName("ButtonOuterBottomBorder", AppButton) is not FrameworkElement frame || frame.ActualWidth <= 0 || IconOnlyImage.ActualWidth <= 0)
            {
                return;
            }

            try
            {
                // In device pixels, on the screen (what is drawn, after the snapping of the icon to whole pixels, which the layout does not
                // show): the icon is moved when its middle is a pixel or more off the middle of the frame.
                Point frameTopLeft = frame.PointToScreen(new Point(0, 0));
                Point frameBottomRight = frame.PointToScreen(new Point(frame.ActualWidth, frame.ActualHeight));
                Point iconTopLeft = IconOnlyImage.PointToScreen(new Point(0, 0));
                Point iconBottomRight = IconOnlyImage.PointToScreen(new Point(IconOnlyImage.ActualWidth, IconOnlyImage.ActualHeight));
                double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                // The middle of the picture, not of the image: an icon with more empty space at one side (foobar2000's, Opera's) is not
                // in the middle of its frame by its box, and is by what is seen.
                (double artX, double artY) = ArtCenterOffset(iconBottomRight.X - iconTopLeft.X, iconBottomRight.Y - iconTopLeft.Y);
                double dxPixels = ((frameTopLeft.X + frameBottomRight.X) - (iconTopLeft.X + iconBottomRight.X)) / 2 - artX;
                double dyPixels = ((frameTopLeft.Y + frameBottomRight.Y) - (iconTopLeft.Y + iconBottomRight.Y)) / 2 - artY;
                if (Math.Abs(dxPixels) >= 0.99 || Math.Abs(dyPixels) >= 0.99)
                {
                    // Only when it is off by the same amount twice running, a render apart: the icon takes a layout pass or two to get where a
                    // margin put it (the snapping works on the layout), and a correction made on what the last one had not reached yet went on
                    // adding up, a pixel a time, until the icon was out of its frame.
                    if (_pendingFix is (double px, double py) && px == dxPixels && py == dyPixels)
                    {
                        _pendingFix = null;
                        // Whole device pixels added after the snapping (MonitorOffset.Nudge): a margin of one pixel moved some icons by two (the
                        // rounding of a half pixel), and the correction went back and forth over the middle.
                        _iconFixX = Math.Clamp(_iconFixX + Math.Round(dxPixels), -4, 4);
                        _iconFixY = Math.Clamp(_iconFixY + Math.Round(dyPixels), -4, 4);
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
                }
            }
            catch (InvalidOperationException)
            {
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

                // The badge at the lower right of the icon, as the classic tab has it.
                IconOnlyBadge.Margin = new Thickness(Math.Round((iconLeft + IconOnlyImage.Width - IconOnlyBadge.Width + 2) / step) * step,
                    Math.Round((iconTop + IconOnlyImage.Height - IconOnlyBadge.Height + 1) / step) * step, 0, 0);
                IconOnlyImage.Opacity = 1;
                IconOnlyBadge.Opacity = 1;
                _iconPlaced = true;

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
