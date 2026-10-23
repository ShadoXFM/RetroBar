using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RetroBar.Utilities
{
    /// <summary>A page a browser has been to.</summary>
    public sealed record BrowserPage(string Title, string Url);

    /// <summary>
    /// The pages a Chromium-based browser (Chrome, Edge, Brave, Vivaldi, Opera, Helium...) has been to most recently, from the History
    /// file of the profile it used last. The file is read where it is, as it is (the browser keeps it open and locked), so a page
    /// visited a moment ago may not be in it yet.
    /// </summary>
    public static class BrowserHistory
    {
        private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        /// <summary>The History file of the browser whose executable this is, or null when it is not a browser that is known here.</summary>
        public static string FindHistoryFile(string exe)
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
                string direct = Path.Combine(dataDirectory, "History");
                if (File.Exists(direct))
                {
                    return direct;
                }

                string profile = LastUsedProfile(dataDirectory) ?? "Default";
                string history = Path.Combine(dataDirectory, profile, "History");
                if (File.Exists(history))
                {
                    return history;
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

        /// <summary>The most recently visited pages (each address and each title once), newest first.</summary>
        public static List<BrowserPage> Recent(string exe, int max)
        {
            var pages = new List<BrowserPage>();
            string file = FindHistoryFile(exe);
            if (file == null)
            {
                return pages;
            }

            IntPtr db = IntPtr.Zero;
            IntPtr statement = IntPtr.Zero;
            try
            {
                // immutable: read the file as it is, without taking the locks the browser holds on it.
                string uri = "file:///" + file.Replace('\\', '/').Replace("#", "%23").Replace("?", "%3F").Replace(" ", "%20") + "?immutable=1";
                if (Native.sqlite3_open_v2(uri, out db, Native.SQLITE_OPEN_READONLY | Native.SQLITE_OPEN_URI, null) != 0)
                {
                    return pages;
                }

                const string query = "SELECT u.title, u.url FROM visits v JOIN urls u ON u.id = v.url WHERE u.hidden = 0 ORDER BY v.visit_time DESC LIMIT 300";
                if (Native.sqlite3_prepare_v2(db, query, -1, out statement, IntPtr.Zero) != 0)
                {
                    return pages;
                }

                var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (pages.Count < max && Native.sqlite3_step(statement) == Native.SQLITE_ROW)
                {
                    string title = Marshal.PtrToStringUTF8(Native.sqlite3_column_text(statement, 0)) ?? "";
                    string url = Marshal.PtrToStringUTF8(Native.sqlite3_column_text(statement, 1)) ?? "";
                    if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    int fragment = url.IndexOf('#');
                    if (!addresses.Add(fragment > 0 ? url.Substring(0, fragment) : url))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(title))
                    {
                        title = Uri.TryCreate(url, UriKind.Absolute, out Uri parsed) ? parsed.Host : url;
                    }

                    // The same page under another address (a video with a different tracking parameter) is listed once.
                    if (!titles.Add(title.Trim()))
                    {
                        continue;
                    }

                    pages.Add(new BrowserPage(title.Trim(), url));
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
            {
                ManagedShell.Common.Logging.ShellLogger.Warning($"BrowserHistory: Unable to read {file}: {ex.Message}");
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

            return pages;
        }

        // The SQLite that Windows 10 and later come with (winsqlite3.dll), so no library of our own is shipped.
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
            public static extern int sqlite3_step(IntPtr statement);

            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_finalize(IntPtr statement);

            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern int sqlite3_close(IntPtr db);
        }
    }
}
