using ManagedShell.WindowsTasks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The name of the program a window belongs to ("Google Chrome", "Discord"), as its executable describes itself,
    /// for labeling a tab with it instead of the window's title.
    /// </summary>
    public static class ProgramName
    {
        private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The name, or null when there is none to be found (a UWP app's window, or no known executable).</summary>
        public static string Get(ApplicationWindow window)
        {
            string path = window.WinFileName;
            if (window.IsUWP || string.IsNullOrEmpty(path))
            {
                return null;
            }

            lock (Cache)
            {
                if (Cache.TryGetValue(path, out string cached))
                {
                    return cached;
                }
            }

            string name = Read(path);

            lock (Cache)
            {
                Cache[path] = name;
            }

            return name;
        }

        private static string Read(string path)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);

                foreach (string candidate in new[] { info.FileDescription, info.ProductName })
                {
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        return candidate.Trim();
                    }
                }
            }
            catch (Exception)
            {
                // Fall back to the file's name.
            }

            string fileName = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrEmpty(fileName) ? null : fileName;
        }
    }
}
