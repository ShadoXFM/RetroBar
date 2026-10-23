using RetroBar.Converters;
using RetroBar.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RetroBar.Controls
{
    // The jump list of the program a tab stands for, at the top of the tab's right-click menu (as the taskbar's button menu has it): what
    // the user pinned in it, the recent (for File Explorer, the frequent) documents or folders, and what the program put there itself
    // (its categories, its tasks and its links), each with its icon. Read from the files Windows keeps in the Recent folder (see
    // JumpLists) each time the menu is opened; the text the programs wrote in the language of the system is given in English.
    public partial class TaskButton
    {
        private const int JumpListSectionMax = 6;
        private const int JumpListMax = 24;

        // What was put in the menu the last time it opened, to be taken out the next time.
        private readonly List<object> _jumpListMenuItems = new();

        private string JumpListString(string key, string fallback) => TryFindResource(key) as string ?? fallback;

        private void UpdateJumpListMenu()
        {
            ContextMenu menu = AppButton.ContextMenu;
            if (menu == null)
            {
                return;
            }

            foreach (object old in _jumpListMenuItems)
            {
                menu.Items.Remove(old);
            }

            _jumpListMenuItems.Clear();

            if (Window == null)
            {
                return;
            }

            var sections = new List<(string Heading, List<JumpListItem> Items)>();
            try
            {
                string exe = Window.IsUWP ? null : Window.WinFileName;
                var ids = new List<string>();
                string explicitId = Window.AppUserModelID;
                if (!string.IsNullOrEmpty(explicitId) && !(exe != null && explicitId.Equals(Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase)))
                {
                    ids.Add(explicitId);
                }

                string pin = Window.IsUWP ? null : PinnedApps.FindLink(Window);
                if (pin != null)
                {
                    ids.AddRange(JumpLists.AppIdCandidates(pin));
                }

                ids.AddRange(JumpLists.AppIdCandidates(exe, null, null));
                List<JumpListItem> items = JumpLists.ForApp(ids, 200);

                sections.Add((JumpListString("jump_list_pinned", "Pinned"), items.Where(i => i.Pinned).ToList()));

                // File Explorer lists the folders used most; every other program the documents used last.
                IEnumerable<JumpListItem> documents = items.Where(i => !i.Pinned && !i.FromProgram);
                if (TaskIconConverter.IsFileExplorerWindow(Window))
                {
                    sections.Add((JumpListString("jump_list_frequent", "Frequent"), documents.OrderByDescending(i => i.AccessCount).ThenByDescending(i => i.Time).ToList()));
                }
                else
                {
                    sections.Add((JumpListString("jump_list_recent", "Recent"), documents.ToList()));
                }

                foreach (IGrouping<string, JumpListItem> category in items.Where(i => i.FromProgram && !string.IsNullOrEmpty(i.Group)).GroupBy(i => i.Group))
                {
                    sections.Add((JumpLists.ToEnglish(category.Key), category.ToList()));
                }

                // The program's tasks, then its links to web pages (a browser's most visited sites, or some promoted ones) last.
                static bool IsLink(JumpListItem i) => i.Arguments != null && i.Arguments.TrimStart().StartsWith("http", StringComparison.OrdinalIgnoreCase);
                sections.Add((JumpListString("jump_list_tasks", "Tasks"), items.Where(i => i.FromProgram && string.IsNullOrEmpty(i.Group) && !IsLink(i)).ToList()));
                sections.Add((JumpListString("jump_list_links", "Links"), items.Where(i => i.FromProgram && string.IsNullOrEmpty(i.Group) && IsLink(i)).ToList()));
            }
            catch (Exception ex)
            {
                ManagedShell.Common.Logging.ShellLogger.Error("TaskButton: Unable to read the jump list", ex);
                return;
            }

            int index = 0;
            int total = 0;
            foreach ((string heading, List<JumpListItem> section) in sections)
            {
                if (section.Count == 0 || total >= JumpListMax)
                {
                    continue;
                }

                AddJumpListMenuItem(menu, ref index, new MenuItem { Header = MenuText(heading), IsEnabled = false, FontStyle = FontStyles.Italic, Style = MenuItemStyle.Style });
                foreach (JumpListItem item in section.Take(Math.Min(JumpListSectionMax, JumpListMax - total)))
                {
                    JumpListItem chosen = item;
                    var entry = new MenuItem { Header = MenuText(JumpLists.ToEnglish(item.Title)), Icon = JumpListIconImage(item), Style = MenuItemStyle.Style };
                    entry.Click += (s, args) => StartJumpListItem(chosen);
                    AddJumpListMenuItem(menu, ref index, entry);
                    total++;
                }
            }

            if (total > 0)
            {
                AddJumpListMenuItem(menu, ref index, new Separator());
            }
        }

        private void AddJumpListMenuItem(ContextMenu menu, ref int index, object item)
        {
            menu.Items.Insert(index++, item);
            _jumpListMenuItems.Add(item);
        }

        // A menu text: the underscore is the access key's marker, and a long name is cut.
        private static string MenuText(string text)
        {
            text = (text ?? "").Replace("_", "__");
            return text.Length > 60 ? text.Substring(0, 59) + "…" : text;
        }

        private static void StartJumpListItem(JumpListItem item)
        {
            try
            {
                var start = new ProcessStartInfo(item.Target) { UseShellExecute = true };
                if (!string.IsNullOrEmpty(item.Arguments))
                {
                    start.Arguments = item.Arguments;
                }

                if (!string.IsNullOrEmpty(item.WorkingDirectory) && Directory.Exists(item.WorkingDirectory))
                {
                    start.WorkingDirectory = item.WorkingDirectory;
                }

                Process.Start(start);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ManagedShell.Common.Logging.ShellLogger.Error($"TaskButton: Unable to start {item.Target} from a jump list", ex);
            }
        }

        #region Icons
        private static readonly Dictionary<string, ImageSource> JumpListIcons = new(StringComparer.OrdinalIgnoreCase);

        // The icon of what an entry starts (a folder's, a document's, the program's own for a task), made the way the program's icon at the
        // top of the menu is (the same 16 DIP image and per-monitor adjustments), so they are as sharp as that one.
        private static Image JumpListIconImage(JumpListItem item)
        {
            ImageSource source = JumpListIcon(item.Target);
            if (source == null)
            {
                return null;
            }

            var image = new Image { Source = source, Width = 16, Height = 16, UseLayoutRounding = true, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            MonitorOffset.SetTag(image, "PreviewTitleIcon");
            return image;
        }

        private static ImageSource JumpListIcon(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            bool folder = Directory.Exists(path);
            if (!folder && !File.Exists(path))
            {
                return null;
            }

            // One icon for every document of a kind; the ones that have an icon of their own are asked one by one.
            string extension = Path.GetExtension(path).ToLowerInvariant();
            string key = folder || extension is ".exe" or ".lnk" or ".ico" or "" ? path : extension;

            lock (JumpListIcons)
            {
                if (JumpListIcons.TryGetValue(key, out ImageSource cached))
                {
                    return cached;
                }
            }

            ImageSource icon = null;
            var info = new SHFILEINFO();
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_SMALLICON) != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                try
                {
                    BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    icon = bitmap;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or COMException)
                {
                }
                finally
                {
                    DestroyIcon(info.hIcon);
                }
            }

            lock (JumpListIcons)
            {
                JumpListIcons[key] = icon;
            }

            return icon;
        }

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_SMALLICON = 0x1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);
        #endregion
    }
}
