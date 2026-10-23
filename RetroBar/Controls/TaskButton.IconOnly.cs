using RetroBar.Utilities;
using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
        private ImageSource _programSource;

        private static readonly Dictionary<(ImageSource, int), ImageSource> ScaledIcons = new();

        // The icon made to the exact size it is drawn at, in whole device pixels, once: drawn in a size that is not the one of its bitmap (a
        // 40 pixel icon in 18, say), the icon is resampled at a place that is not on the pixel grid, and its top comes out flat.
        private static ImageSource IconAtSize(ImageSource source, int pixels)
        {
            if (source is not BitmapSource bitmap || pixels <= 0 || (bitmap.PixelWidth == pixels && bitmap.PixelHeight == pixels))
            {
                return source;
            }

            lock (ScaledIcons)
            {
                if (ScaledIcons.TryGetValue((source, pixels), out ImageSource cached))
                {
                    return cached;
                }
            }

            ImageSource result = source;
            try
            {
                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                int stride = converted.PixelWidth * 4;
                byte[] data = new byte[stride * converted.PixelHeight];
                converted.CopyPixels(data, stride, 0);

                using var from = new System.Drawing.Bitmap(converted.PixelWidth, converted.PixelHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                System.Drawing.Imaging.BitmapData fromData = from.LockBits(new System.Drawing.Rectangle(0, 0, from.Width, from.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                System.Runtime.InteropServices.Marshal.Copy(data, 0, fromData.Scan0, data.Length);
                from.UnlockBits(fromData);

                using var to = new System.Drawing.Bitmap(pixels, pixels, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(to))
                {
                    g.Clear(System.Drawing.Color.Transparent);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    g.DrawImage(from, new System.Drawing.Rectangle(0, 0, pixels, pixels), 0, 0, from.Width, from.Height, System.Drawing.GraphicsUnit.Pixel);
                }

                System.Drawing.Imaging.BitmapData toData = to.LockBits(new System.Drawing.Rectangle(0, 0, pixels, pixels), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                byte[] scaled = new byte[toData.Stride * pixels];
                System.Runtime.InteropServices.Marshal.Copy(toData.Scan0, scaled, 0, scaled.Length);
                int scaledStride = toData.Stride;
                to.UnlockBits(toData);

                BitmapSource made = BitmapSource.Create(pixels, pixels, 96, 96, PixelFormats.Bgra32, null, scaled, scaledStride);
                made.Freeze();
                result = made;
            }
            catch (Exception)
            {
            }

            lock (ScaledIcons)
            {
                ScaledIcons[(source, pixels)] = result;
            }

            return result;
        }

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
            ApplyIconOnly();
        }

        private void ReleaseIconOnly()
        {
            AppButton.SizeChanged -= IconOnly_OnButtonSizeChanged;
            MonitorAdjustments.Changed -= IconOnly_OnAdjustmentsChanged;
            if (_iconOnlyHost != null)
            {
                _iconOnlyDescriptor?.RemoveValueChanged(_iconOnlyHost, IconOnly_OnChanged);
                _iconOnlyHost = null;
            }
        }

        private void IconOnly_OnChanged(object sender, EventArgs e) => ApplyIconOnly();

        private void IconOnly_OnAdjustmentsChanged(object sender, EventArgs e)
        {
            if (_iconOnlyHost?.IconOnly == true)
            {
                Dispatcher.BeginInvoke(new Action(QueuePlaceIcon));
            }
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
            IconOnlyBadge.Opacity = only ? 1 : 0;
            IconOnlyImage.Visibility = only ? Visibility.Visible : Visibility.Collapsed;

            if (only)
            {
                ImageSource program = ProgramIconForTab();
                if (program != null)
                {
                    BindingOperations.ClearBinding(IconOnlyImage, Image.SourceProperty);
                    _programSource = program;
                    IconOnlyImage.Source = program;
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
                _programSource = null;
                IconOnlyImage.Source = null;
            }
        }

        private void QueuePlaceIcon()
        {
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
                Rect bounds = reference.TransformToAncestor(this).TransformBounds(new Rect(0, 0, reference.ActualWidth, reference.ActualHeight));

                double step = _iconOnlyHost.PixelStep > 0 ? _iconOnlyHost.PixelStep : 1;

                // The size the classic style draws the icon at (the Scale its monitor's TaskIcon adjustment gives it), in whole device pixels:
                // at full size the icon fills the frame to its top highlight, and it looks cut there.
                double scale = MonitorAdjustments.Get(MonitorOffset.FindDeviceName(this), "TaskIcon").Scale ?? 1;
                double side = Math.Max(step, Math.Round(16 * scale / step) * step);
                IconOnlyImage.Width = side;
                IconOnlyImage.Height = side;
                if (_programSource != null)
                {
                    // 96 dpi bitmap: one image pixel is one DIP, so it is the device pixels that are asked for and the size is set to match.
                    int pixels = (int)Math.Round(side / step);
                    DpiScale dpi = VisualTreeHelper.GetDpi(this);
                    IconOnlyImage.Source = IconAtSize(_programSource, pixels);
                    RenderOptions.SetBitmapScalingMode(IconOnlyImage, pixels > 0 && IconOnlyImage.Source is BitmapSource shown && shown.PixelWidth == pixels ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
                }
                double left = bounds.Left + (bounds.Width - IconOnlyImage.Width) / 2;
                double top = bounds.Top + (bounds.Height - IconOnlyImage.Height) / 2;
                double iconLeft = Math.Round(left / step) * step;
                double iconTop = Math.Round(top / step) * step;
                IconOnlyImage.Margin = new Thickness(iconLeft, iconTop, 0, 0);

                // The badge at the lower right of the icon, as the classic tab has it.
                IconOnlyBadge.Margin = new Thickness(Math.Round((iconLeft + IconOnlyImage.Width - IconOnlyBadge.Width + 2) / step) * step,
                    Math.Round((iconTop + IconOnlyImage.Height - IconOnlyBadge.Height + 1) / step) * step, 0, 0);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
