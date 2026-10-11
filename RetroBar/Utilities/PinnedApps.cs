using ManagedShell.Common.Logging;
using ManagedShell.WindowsTasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The programs pinned in the quick launch folder (Settings.QuickLaunchPath), in the order of the quick launch
    /// toolbar, so a tab of a pinned program that has no place of its own yet can be put where its pin is.
    /// A program is told by its executable's file name (so a new version's folder doesn't matter).
    /// </summary>
    public static class PinnedApps
    {
        private static readonly Dictionary<string, (DateTime Written, string Exe)> TargetCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Position of the window's program among the pinned programs, or -1 when it isn't pinned.</summary>
        public static int IndexOf(ApplicationWindow window, List<string> pinned)
        {
            string exe = window.IsUWP ? null : Path.GetFileName(window.WinFileName);
            return string.IsNullOrEmpty(exe) ? -1 : pinned.FindIndex(p => p.Equals(exe, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Executable file names of the pinned programs, in the quick launch toolbar's order.</summary>
        public static List<string> Get()
        {
            var result = new List<string>();

            try
            {
                string folder = Environment.ExpandEnvironmentVariables(Settings.Instance.QuickLaunchPath ?? "");
                if (!Directory.Exists(folder))
                {
                    return result;
                }

                List<string> order = Settings.Instance.QuickLaunchOrder ?? new List<string>();
                List<string> files = Directory.GetFiles(folder, "*.lnk")
                    .OrderBy(file =>
                    {
                        int index = order.FindIndex(o => o.Equals(file, StringComparison.OrdinalIgnoreCase));
                        return index >= 0 ? index : int.MaxValue;
                    })
                    .ThenBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (string file in files)
                {
                    result.Add(GetTargetExe(file));
                }
            }
            catch (Exception ex)
            {
                ShellLogger.Error("PinnedApps: Unable to read the pinned programs", ex);
            }

            return result;
        }

        private static string GetTargetExe(string link)
        {
            DateTime written = File.GetLastWriteTimeUtc(link);

            lock (TargetCache)
            {
                if (TargetCache.TryGetValue(link, out var cached) && cached.Written == written)
                {
                    return cached.Exe;
                }
            }

            string exe = "";

            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic shortcut = shell.CreateShortcut(link);
                string target = shortcut.TargetPath;
                string arguments = shortcut.Arguments;

                // File Explorer's own pin has no target path.
                exe = string.IsNullOrEmpty(target) ? "explorer.exe" : Path.GetFileName(target);

                // Discord and the like start through an updater: "Update.exe --processStart Discord.exe".
                const string processStart = "--processStart ";
                int at = arguments?.IndexOf(processStart, StringComparison.OrdinalIgnoreCase) ?? -1;
                if (at >= 0)
                {
                    exe = arguments.Substring(at + processStart.Length).Trim().Trim('"').Split(' ')[0];
                }
            }
            catch (Exception)
            {
            }

            lock (TargetCache)
            {
                TargetCache[link] = (written, exe);
            }

            return exe;
        }
    }
}
