using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using ManagedShell.Common.Logging;

namespace RetroBar.Utilities
{
    /// <summary>One toast of the notification history.</summary>
    public class ToastItem
    {
        public long Id;
        public string AppId;
        public string AppName;
        public ImageSource Icon;
        public DateTime Time;
        public string Title;
        public string Body;
        public string Launch;
        public string ActivationType;
    }

    /// <summary>
    /// The notifications Windows keeps (the ones the notification center lists), read from its own database
    /// (%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db, opened read-only, with the SQLite that comes with Windows). The
    /// WinRT listener (UserNotificationListener) would give them too, but only to an app with a package identity, which this one has not.
    /// What is cleared here is only hidden from this list (Windows keeps its own): the ids are remembered in a file of ours.
    /// </summary>
    public static class NotificationHistory
    {
        private static readonly string DatabasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications", "wpndatabase.db");

        private static readonly string HiddenPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RetroBar", "hidden-notifications.txt");

        private static HashSet<long> _hidden;
        private static readonly Dictionary<string, (string name, ImageSource icon)> Apps = new(StringComparer.OrdinalIgnoreCase);

        #region SQLite (winsqlite3.dll)

        private const string Sqlite = "winsqlite3.dll";

        [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out IntPtr db, int flags, IntPtr vfs);

        [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr db);

        [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

        [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);

        [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr statement);

        [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr statement);

        [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr statement, int column);

        [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

        [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);

        [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr statement, int column);

        private const int SQLITE_ROW = 100;
        private const int SQLITE_OPEN_READONLY = 1;

        #endregion

        /// <summary>The toasts that are still in the list, the newest first (at most <paramref name="max"/>); the number of the others is in <paramref name="more"/>.</summary>
        public static List<ToastItem> Read(int max, out int more)
        {
            more = 0;
            var all = new List<ToastItem>();
            try
            {
                if (!File.Exists(DatabasePath))
                {
                    return all;
                }

                HashSet<long> hidden = Hidden();
                long now = DateTime.UtcNow.ToFileTimeUtc();
                const string sql =
                    "SELECT n.Id, h.PrimaryId, n.ArrivalTime, n.Payload FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId " +
                    "WHERE n.Type = 'toast' AND (n.ExpiryTime = 0 OR n.ExpiryTime > ?) ORDER BY n.ArrivalTime DESC LIMIT 200";

                // The time is put into the text: there is no binding to write (this is read once a click).
                if (sqlite3_open_v2(DatabasePath, out IntPtr db, SQLITE_OPEN_READONLY, IntPtr.Zero) != 0)
                {
                    if (db != IntPtr.Zero)
                    {
                        sqlite3_close(db);
                    }

                    return all;
                }

                try
                {
                    sqlite3_busy_timeout(db, 300);
                    if (sqlite3_prepare_v2(db, sql.Replace("?", now.ToString(System.Globalization.CultureInfo.InvariantCulture)), -1, out IntPtr statement, IntPtr.Zero) != 0)
                    {
                        return all;
                    }

                    try
                    {
                        while (sqlite3_step(statement) == SQLITE_ROW)
                        {
                            long id = sqlite3_column_int64(statement, 0);
                            if (hidden.Contains(id))
                            {
                                continue;
                            }

                            string appId = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 1)) ?? "";
                            long arrival = sqlite3_column_int64(statement, 2);
                            int length = sqlite3_column_bytes(statement, 3);
                            byte[] payload = new byte[length];
                            if (length > 0)
                            {
                                Marshal.Copy(sqlite3_column_blob(statement, 3), payload, 0, length);
                            }

                            ToastItem item = Parse(id, appId, arrival, payload);
                            if (item != null)
                            {
                                all.Add(item);
                            }
                        }
                    }
                    finally
                    {
                        sqlite3_finalize(statement);
                    }
                }
                finally
                {
                    sqlite3_close(db);
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException or SEHException)
            {
                ShellLogger.Debug("NotificationHistory: " + e.Message);
            }

            more = Math.Max(0, all.Count - max);
            List<ToastItem> shown = all.Take(max).ToList();
            foreach (ToastItem item in shown)
            {
                (item.AppName, item.Icon) = App(item.AppId);
            }

            return shown;
        }

        private static ToastItem Parse(long id, string appId, long arrival, byte[] payload)
        {
            string xml;
            try
            {
                xml = new UTF8Encoding(false, true).GetString(payload);
            }
            catch (ArgumentException)
            {
                // Some apps write it in the ANSI code page.
                xml = Encoding.Latin1.GetString(payload);
            }

            try
            {
                XElement toast = XDocument.Parse(xml.TrimStart('﻿')).Root;
                if (toast == null)
                {
                    return null;
                }

                var texts = toast.Descendants("binding").FirstOrDefault()?.Elements("text")
                    .Where(t => (string)t.Attribute("placement") != "attribution")
                    .Select(t => t.Value.Trim())
                    .Where(t => t.Length > 0)
                    .ToList() ?? new List<string>();
                if (texts.Count == 0)
                {
                    return null;
                }

                DateTime time;
                try
                {
                    time = DateTime.FromFileTimeUtc(arrival).ToLocalTime();
                }
                catch (ArgumentOutOfRangeException)
                {
                    time = DateTime.Now;
                }

                return new ToastItem
                {
                    Id = id,
                    AppId = appId,
                    Time = time,
                    Title = texts[0],
                    Body = string.Join("\n", texts.Skip(1).Take(2)),
                    Launch = (string)toast.Attribute("launch") ?? "",
                    ActivationType = (string)toast.Attribute("activationType") ?? "foreground",
                };
            }
            catch (System.Xml.XmlException)
            {
                return null;
            }
        }

        #region Hidden ones

        private static HashSet<long> Hidden()
        {
            if (_hidden != null)
            {
                return _hidden;
            }

            _hidden = new HashSet<long>();
            try
            {
                if (File.Exists(HiddenPath))
                {
                    foreach (string line in File.ReadAllLines(HiddenPath))
                    {
                        if (long.TryParse(line, out long id))
                        {
                            _hidden.Add(id);
                        }
                    }
                }
            }
            catch (IOException)
            {
            }

            return _hidden;
        }

        public static void Hide(params long[] ids)
        {
            HashSet<long> hidden = Hidden();
            foreach (long id in ids)
            {
                hidden.Add(id);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HiddenPath));
                // Only the newest ones matter: a toast's id only goes up, and Windows drops the old ones itself.
                File.WriteAllLines(HiddenPath, hidden.OrderByDescending(i => i).Take(1000).Select(i => i.ToString()));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        #endregion

        #region The app of a toast (its name and its icon) and what a click on it does

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(int sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            void GetImage(long size, int flags, out IntPtr hbitmap);
        }

        [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationActivationManager
        {
            int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
            int ActivateForFile(string appUserModelId, IntPtr itemArray, string verb, out uint processId);
            int ActivateForProtocol(string appUserModelId, IntPtr itemArray, out uint processId);
        }

        [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
        private class ApplicationActivationManager
        {
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IShellItem item);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        private static (string name, ImageSource icon) App(string appId)
        {
            if (Apps.TryGetValue(appId, out var known))
            {
                return known;
            }

            string name = null;
            ImageSource icon = null;
            try
            {
                Guid shellItem = typeof(IShellItem).GUID;
                SHCreateItemFromParsingName(@"shell:AppsFolder\" + appId, IntPtr.Zero, ref shellItem, out IShellItem item);
                item.GetDisplayName(0, out IntPtr text);
                name = Marshal.PtrToStringUni(text);
                Marshal.FreeCoTaskMem(text);

                // 32 pixels: it is drawn at 16 DIPs, which is 32 at 200%.
                const long size = (32L << 32) | 32;
                ((IShellItemImageFactory)item).GetImage(size, 0x4 /* icon only */, out IntPtr bitmap);
                if (bitmap != IntPtr.Zero)
                {
                    try
                    {
                        BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        source.Freeze();
                        icon = source;
                    }
                    finally
                    {
                        DeleteObject(bitmap);
                    }
                }
            }
            catch (Exception e) when (e is COMException or ArgumentException or InvalidCastException or IOException)
            {
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                // "Microsoft.Windows.Explorer" -> "Explorer", "Claude_pzs8sxrjxfjjc!Claude" -> "Claude".
                string last = appId.Split('!').Last().Split('.').Last();
                name = string.IsNullOrWhiteSpace(last) ? appId : last;
            }

            return Apps[appId] = (name, icon);
        }

        /// <summary>What a click on the toast does: what it asked for (a link, an app's page), else the app is brought up.</summary>
        public static void Activate(ToastItem item)
        {
            try
            {
                if (string.Equals(item.ActivationType, "protocol", StringComparison.OrdinalIgnoreCase) && Uri.IsWellFormedUriString(item.Launch, UriKind.Absolute))
                {
                    Process.Start(new ProcessStartInfo(item.Launch) { UseShellExecute = true });
                    return;
                }

                var manager = (IApplicationActivationManager)new ApplicationActivationManager();
                manager.ActivateApplication(item.AppId, item.ActivationType.Equals("foreground", StringComparison.OrdinalIgnoreCase) ? item.Launch : "", 0, out _);
            }
            catch (Exception e) when (e is COMException or InvalidCastException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ShellLogger.Debug("NotificationHistory: activating " + item.AppId + " failed: " + e.Message);
            }
        }

        #endregion
    }
}
