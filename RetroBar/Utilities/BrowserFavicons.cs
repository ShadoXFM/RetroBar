using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The icon of a web site, as a Chromium-based browser (Chrome, Edge, Brave, Vivaldi, Opera, Helium...) has kept it in the Favicons file
    /// of the profile it used last. The file is read where it is (the browser keeps it open and locked), through the SQLite that Windows
    /// ships (winsqlite3.dll), so no library is added.
    /// </summary>
    public static class BrowserFavicons
    {
        private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // The icons found, by site (null for one that has none), so a menu that is opened again does not read the file again.
        private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The Favicons file of the browser whose executable this is, or null when it is not a browser that is known here.</summary>
        public static string FindFile(string exe)
        {
            if (string.IsNullOrEmpty(exe))
            {
                return null;
            }

            foreach (string dataDirectory in DataDirectories(exe))
            {
                if (!Directory.Exists(dataDirectory))
                {
                    continue;
                }

                // Opera keeps its one profile in the folder itself.
                string direct = Path.Combine(dataDirectory, "Favicons");
                if (File.Exists(direct))
                {
                    return direct;
                }

                string favicons = Path.Combine(dataDirectory, LastUsedProfile(dataDirectory) ?? "Default", "Favicons");
                if (File.Exists(favicons))
                {
                    return favicons;
                }
            }

            return null;
        }

        private static IEnumerable<string> DataDirectories(string exe)
        {
            string directory = Path.GetDirectoryName(exe) ?? "";

            // A browser that keeps its data next to itself (a portable one, Helium: ...\Helium\Application\chrome.exe and ...\Helium\User Data).
            yield return Path.GetFullPath(Path.Combine(directory, "..", "User Data"));
            yield return Path.Combine(directory, "User Data");

            switch (Path.GetFileName(exe).ToLowerInvariant())
            {
                case "chrome.exe":
                    yield return Path.Combine(LocalAppData, "Google", "Chrome", "User Data");
                    yield return Path.Combine(LocalAppData, "Chromium", "User Data");
                    break;
                case "msedge.exe":
                    yield return Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data");
                    break;
                case "brave.exe":
                    yield return Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "User Data");
                    break;
                case "vivaldi.exe":
                    yield return Path.Combine(LocalAppData, "Vivaldi", "User Data");
                    break;
                case "opera.exe":
                case "launcher.exe":
                    yield return Path.Combine(AppData, "Opera Software", exe.Contains("GX", StringComparison.OrdinalIgnoreCase) ? "Opera GX Stable" : "Opera Stable");
                    break;
            }
        }

        // "Local State" names the profile the browser used last (profile.last_used).
        private static string LastUsedProfile(string dataDirectory)
        {
            try
            {
                using JsonDocument state = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDirectory, "Local State")));
                if (state.RootElement.TryGetProperty("profile", out JsonElement profile) && profile.TryGetProperty("last_used", out JsonElement lastUsed))
                {
                    return lastUsed.GetString();
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }

            return null;
        }

        /// <summary>The icon of the page at this address (or, failing that, of another page of the same site), or null when the browser has none.</summary>
        public static ImageSource Get(string exe, string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri page) || (page.Scheme != Uri.UriSchemeHttp && page.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            string key = Path.GetFileName(exe) + "|" + page.Host;
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out ImageSource cached))
                {
                    return cached;
                }
            }

            ImageSource icon = null;
            string file = FindFile(exe);
            if (file != null)
            {
                byte[] image = ReadIcon(file, url) ?? ReadIcon(file, page.Scheme + "://" + page.Host + "/", prefix: true);
                if (image != null)
                {
                    icon = Decode(image);
                }
            }

            lock (Cache)
            {
                Cache[key] = icon;
            }

            return icon;
        }

        private static ImageSource Decode(byte[] image)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(image);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException or ArgumentException)
            {
                return null;
            }
        }

        // The picture of an icon of the page (32 pixels wide when there is one, so that it is sharp on a scaled screen, else the widest).
        private static byte[] ReadIcon(string file, string address, bool prefix = false)
        {
            IntPtr db = IntPtr.Zero;
            IntPtr statement = IntPtr.Zero;
            try
            {
                // immutable: read the file as it is, without taking the locks the browser holds on it.
                string uri = "file:///" + file.Replace('\\', '/').Replace("#", "%23").Replace("?", "%3F").Replace(" ", "%20") + "?immutable=1";
                if (Native.sqlite3_open_v2(uri, out db, Native.SQLITE_OPEN_READONLY | Native.SQLITE_OPEN_URI, null) != 0)
                {
                    return null;
                }

                string where = prefix ? "im.page_url LIKE ? ESCAPE '\\'" : "im.page_url = ?";
                string query = "SELECT fb.image_data FROM icon_mapping im JOIN favicon_bitmaps fb ON fb.icon_id = im.icon_id WHERE " + where +
                               " ORDER BY ABS(fb.width - 32) LIMIT 1";
                if (Native.sqlite3_prepare_v2(db, query, -1, out statement, IntPtr.Zero) != 0)
                {
                    return null;
                }

                string parameter = prefix ? address.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%" : address;
                if (Native.sqlite3_bind_text(statement, 1, parameter, -1, new IntPtr(-1)) != 0)
                {
                    return null;
                }

                if (Native.sqlite3_step(statement) != Native.SQLITE_ROW)
                {
                    return null;
                }

                IntPtr data = Native.sqlite3_column_blob(statement, 0);
                int length = Native.sqlite3_column_bytes(statement, 0);
                if (data == IntPtr.Zero || length <= 0)
                {
                    return null;
                }

                var image = new byte[length];
                Marshal.Copy(data, image, 0, length);
                return image;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
            {
                return null;
            }
            finally
            {
                if (statement != IntPtr.Zero)
                {
                    Native.sqlite3_finalize(statement);
                }

                if (db != IntPtr.Zero)
                {
                    Native.sqlite3_close(db);
                }
            }
        }

        // The SQLite that Windows 10 and later come with (winsqlite3.dll).
        private static class Native
        {
            public const int SQLITE_ROW = 100;
            public const int SQLITE_OPEN_READONLY = 0x1;
            public const int SQLITE_OPEN_URI = 0x40;

            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, [MarshalAs(UnmanagedType.LPUTF8Str)] string vfs);

            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);

            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_bind_text(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int length, IntPtr destructor);

            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_step(IntPtr statement);

            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);

            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_column_bytes(IntPtr statement, int column);

            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_finalize(IntPtr statement);

            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_close(IntPtr db);
        }
    }
}
