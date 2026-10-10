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
    /// icon (or an explorer.ico / explorer.png in %LOCALAPPDATA%RetroBarIcons, when there is one). Explorer windows report the icon of the folder they are showing (Downloads, This PC, ...) - and with tabs
    /// in one window, whichever tab is current - which makes the taskbar button change whenever the user moves around.
    ///
    /// A window that has no icon found for it, or a dialog of Explorer's process that was given Explorer's icon (Run only
    /// has a small one, which isn't looked for), gets the icon the window itself has.
    ///
    /// Values: the window, then its Icon (only there so the binding updates when the icon does), then the number of
    /// Explorer windows open (likewise: a lone one has its own icon, see Convert).
    /// </summary>
    public class TaskIconConverter : IMultiValueConverter
    {
        private static ImageSource _fileExplorerIcon;

        // A File Explorer icon of the user's own: %LOCALAPPDATA%\RetroBar\Icons\explorer.ico (or .png) takes the place of the
        // one in explorer.exe - the old Windows 10 one, say, which Windows 11 does not have. An .ico with several sizes gives
        // the 32 pixel one (what the icon from the file is), or the next one up.
        private static ImageSource LoadCustomFileExplorerIcon()
        {
            try
            {
                foreach (string name in new[] { "explorer.ico", "explorer.png" })
                {
                    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RetroBar", "Icons", name);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    BitmapDecoder decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    BitmapFrame frame = null;
                    foreach (BitmapFrame candidate in decoder.Frames)
                    {
                        bool better = frame == null
                            || (frame.PixelWidth < 32 && candidate.PixelWidth > frame.PixelWidth)
                            || (frame.PixelWidth >= 32 && candidate.PixelWidth >= 32 && candidate.PixelWidth < frame.PixelWidth);
                        if (better)
                        {
                            frame = candidate;
                        }
                    }

                    if (frame != null)
                    {
                        frame.Freeze();
                        return frame;
                    }
                }
            }
            catch (Exception)
            {
                // A damaged file is the same as none.
            }

            return null;
        }

        private static ImageSource GetFileExplorerIcon()
        {
            if (_fileExplorerIcon == null)
            {
                _fileExplorerIcon = LoadCustomFileExplorerIcon();
            }

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

        // What was found for a window, and when: asking a window for its icon sends it a message, which a window that is
        // busy or hung answers late (the wait is capped, but it is still a wait, and the converter runs on the UI thread
        // each time a preview is built). A window that gave nothing is not asked again for a few seconds either.
        private sealed class CachedIcon
        {
            public ImageSource Icon;
            public DateTime Taken;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ApplicationWindow, System.Collections.Generic.Dictionary<(bool, int), CachedIcon>> IconCache = new();
        private static readonly TimeSpan IconCacheLife = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MissCacheLife = TimeSpan.FromSeconds(5);

        private static ImageSource GetFallbackIcon(ApplicationWindow window, bool preferLarge = false, int dpi = 0)
        {
            var perWindow = IconCache.GetValue(window, _ => new System.Collections.Generic.Dictionary<(bool, int), CachedIcon>());
            lock (perWindow)
            {
                if (perWindow.TryGetValue((preferLarge, dpi), out CachedIcon cached) &&
                    DateTime.UtcNow - cached.Taken < (cached.Icon == null ? MissCacheLife : IconCacheLife))
                {
                    return cached.Icon;
                }
            }

            ImageSource found = QueryIcon(window, preferLarge, dpi);

            lock (perWindow)
            {
                perWindow[(preferLarge, dpi)] = new CachedIcon { Icon = found, Taken = DateTime.UtcNow };
            }

            return found;
        }

        private static ImageSource QueryIcon(ApplicationWindow window, bool preferLarge, int dpi)
        {
            try
            {
                // What the title bar shows: ICON_SMALL2, ICON_SMALL, ICON_BIG, then the window class' icons (GCLP_HICONSM, GCLP_HICON)
                IntPtr handle = IntPtr.Zero;
                // (The large icon first when asked: it is what the taskbar's own icons are made from, so it is as sharp.)
                foreach (int which in preferLarge ? new[] { 1, 2, 0 } : new[] { 2, 0, 1 })
                {
                    if (SendMessageTimeout(window.Handle, WM_GETICON, (IntPtr)which, (IntPtr)dpi, SMTO_ABORTIFHUNG, 50, out IntPtr result) != IntPtr.Zero && result != IntPtr.Zero)
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

        internal static bool IsFileExplorerWindow(ApplicationWindow window)
        {
            return !window.IsUWP &&
                   (window.ClassName == "CabinetWClass" || window.ClassName == "ExploreWClass") &&
                   string.Equals(Path.GetFileName(window.WinFileName), "explorer.exe", StringComparison.OrdinalIgnoreCase);
        }

        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            // The parameter "window" asks for the window's own icon (the one in its title bar), Explorer windows included.
            bool ownIconWanted = parameter as string == "window";

            // A lone Explorer window shows its own icon (the folder it is in), several of them the regular Explorer icon, so
            // that they look alike. values[2], when there, is the number of Explorer windows (see ExplorerWindowCount) - there
            // only so that the binding asks again when it changes.
            int explorerWindows = values.Length > 2 && values[2] is int counted ? counted : 2;

            if (!ownIconWanted && explorerWindows >= 2 && values.Length > 0 && values[0] is ApplicationWindow window && IsFileExplorerWindow(window))
            {
                ImageSource icon = GetFileExplorerIcon();
                if (icon != null)
                {
                    return icon;
                }
            }

            object own = values.Length > 1 ? values[1] : null;

            // A lone File Explorer window has the icon in its title bar, not whatever icon the shell reports for the
            // window's button.
            if (!ownIconWanted && explorerWindows < 2 && values.Length > 0 && values[0] is ApplicationWindow lone && IsFileExplorerWindow(lone))
            {
                // Asked for the way the title bar's icon is (and a tab's preview shows it): the small one, at a large size.
                ImageSource titleBar = GetFallbackIcon(lone, preferLarge: false, dpi: 192);
                if (titleBar != null)
                {
                    return titleBar;
                }
            }

            // A window's preview shows the icon in the window's title bar. A DPI is asked for along with it (a window
            // that has icons of several sizes then hands back a large one), so it can be scaled down to the size of
            // the taskbar's icons rather than up from the small one.
            if (ownIconWanted && values.Length > 0 && values[0] is ApplicationWindow titled && !titled.IsUWP)
            {
                ImageSource titleBarIcon = GetFallbackIcon(titled, preferLarge: false, dpi: 192);
                if (titleBarIcon != null)
                {
                    return titleBarIcon;
                }
            }

            // A dialog of Explorer's own process (Run, ...) is given Explorer's icon when it has no big one of its
            // own, so take the icon the window itself has; the same for any window with no icon found at all.
            if (values.Length > 0 && values[0] is ApplicationWindow other &&
                (own == null || (!other.IsUWP && !IsFileExplorerWindow(other) && string.Equals(Path.GetFileName(other.WinFileName), "explorer.exe", StringComparison.OrdinalIgnoreCase))))
            {
                return GetFallbackIcon(other, preferLarge: true) ?? own;
            }

            return own;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
