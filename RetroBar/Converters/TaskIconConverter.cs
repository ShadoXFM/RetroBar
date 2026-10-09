using ManagedShell.WindowsTasks;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RetroBar.Converters
{
    /// <summary>
    /// The icon shown for a window: its own, except that a File Explorer window always gets the regular File Explorer
    /// icon. Explorer windows report the icon of the folder they are showing (Downloads, This PC, ...) - and with tabs
    /// in one window, whichever tab is current - which makes the taskbar button change whenever the user moves around.
    ///
    /// A window that has no icon found for it, or a dialog of Explorer's process that was given Explorer's icon (Run only
    /// has a small one, which isn't looked for), gets the icon the window itself has.
    ///
    /// Values: the window, then its Icon (only there so the binding updates when the icon does).
    /// </summary>
    public class TaskIconConverter : IMultiValueConverter
    {
        private static ImageSource _fileExplorerIcon;

        private static ImageSource GetFileExplorerIcon()
        {
            if (_fileExplorerIcon == null)
            {
                try
                {
                    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                    // The icon is taken straight from the file: IconImageConverter's can hand back a placeholder
                    // while the shell is busy (it did right after startup), which would then be kept for good.
                    using (System.Drawing.Icon file = System.Drawing.Icon.ExtractAssociatedIcon(path))
                    {
                        _fileExplorerIcon = Imaging.CreateBitmapSourceFromHIcon(file.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    }

                    if (_fileExplorerIcon is Freezable freezable && freezable.CanFreeze)
                    {
                        freezable.Freeze();
                    }
                }
                catch (Exception)
                {
                    // Fall back to the window's own icon.
                }
            }

            return _fileExplorerIcon;
        }

        private const uint WM_GETICON = 0x007F;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
        private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int index);

        private static ImageSource GetFallbackIcon(ApplicationWindow window)
        {
            try
            {
                // ICON_SMALL2, ICON_SMALL, ICON_BIG, then the window class' icons (GCLP_HICONSM, GCLP_HICON)
                IntPtr handle = IntPtr.Zero;
                foreach (int which in new[] { 2, 0, 1 })
                {
                    if (SendMessageTimeout(window.Handle, WM_GETICON, (IntPtr)which, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out IntPtr result) != IntPtr.Zero && result != IntPtr.Zero)
                    {
                        handle = result;
                        break;
                    }
                }

                if (handle == IntPtr.Zero)
                {
                    handle = GetClassLongPtr(window.Handle, -34);
                }

                if (handle == IntPtr.Zero)
                {
                    handle = GetClassLongPtr(window.Handle, -14);
                }

                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                BitmapSource icon = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                icon.Freeze();
                return icon;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsFileExplorerWindow(ApplicationWindow window)
        {
            return !window.IsUWP &&
                   (window.ClassName == "CabinetWClass" || window.ClassName == "ExploreWClass") &&
                   string.Equals(Path.GetFileName(window.WinFileName), "explorer.exe", StringComparison.OrdinalIgnoreCase);
        }

        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            // The parameter "window" asks for the window's own icon, Explorer windows included.
            bool ownIconWanted = parameter as string == "window";

            if (!ownIconWanted && values.Length > 0 && values[0] is ApplicationWindow window && IsFileExplorerWindow(window))
            {
                ImageSource icon = GetFileExplorerIcon();
                if (icon != null)
                {
                    return icon;
                }
            }

            object own = values.Length > 1 ? values[1] : null;

            // A dialog of Explorer's own process (Run, ...) is given Explorer's icon when it has no big one of its
            // own, so take the icon the window itself has; the same for any window with no icon found at all.
            if (values.Length > 0 && values[0] is ApplicationWindow other &&
                (own == null || (!other.IsUWP && !IsFileExplorerWindow(other) && string.Equals(Path.GetFileName(other.WinFileName), "explorer.exe", StringComparison.OrdinalIgnoreCase))))
            {
                return GetFallbackIcon(other) ?? own;
            }

            return own;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
