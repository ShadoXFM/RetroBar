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

        /// <summary>The pin (shortcut file) of the window's program, or null when it isn't pinned.</summary>
        public static string FindLink(ApplicationWindow window)
        {
            string exe = window.IsUWP ? null : Path.GetFileName(window.WinFileName);
            string folder = Environment.ExpandEnvironmentVariables(Settings.Instance.QuickLaunchPath ?? "");

            if (string.IsNullOrEmpty(exe) || !Directory.Exists(folder))
            {
                return null;
            }

            return Directory.GetFiles(folder, "*.lnk").FirstOrDefault(link => GetTargetExe(link).Equals(exe, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Pins the window's program: a shortcut to it in the quick launch folder.</summary>
        public static void Pin(ApplicationWindow window)
        {
            string folder = Environment.ExpandEnvironmentVariables(Settings.Instance.QuickLaunchPath ?? "");
            if (window.IsUWP || string.IsNullOrEmpty(window.WinFileName) || string.IsNullOrEmpty(folder))
            {
                return;
            }

            Directory.CreateDirectory(folder);

            string name = ProgramName.Get(window) ?? Path.GetFileNameWithoutExtension(window.WinFileName);
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, ' ');
            }

            string path = Path.Combine(folder, name + ".lnk");
            for (int copy = 1; File.Exists(path); copy++)
            {
                path = Path.Combine(folder, $"{name} ({copy}).lnk");
            }

            ManagedShell.ShellFolders.ShellLinkHelper.CreateAndSave(window.WinFileName, path);
        }

        /// <summary>Executable file names of the pinned programs, in the quick launch toolbar's order.</summary>
        public static List<string> Get() => GetLinks().Select(GetTargetExe).ToList();

        /// <summary>The program (its executable's file name) a pin starts.</summary>
        public static string TargetExe(string link) => GetTargetExe(link);

        /// <summary>Saves a new order of the pins (shortcut files, as <see cref="GetLinks"/> gives them): the toolbar and the tabs follow it.</summary>
        public static void SaveOrder(IEnumerable<string> links)
        {
            Settings.Instance.QuickLaunchOrder = links.ToList();
        }

        /// <summary>
        /// The pins in the order the tabs of their programs are in (after one was dragged): the places the running programs' pins had
        /// are filled with them in the tabs' order, and the pins of programs that are not running stay where they were.
        /// </summary>
        public static List<string> OrderByTabs(IEnumerable<ApplicationWindow> tabsInOrder)
        {
            List<string> links = GetLinks();
            List<string> programs = links.Select(GetTargetExe).ToList();

            var runningPins = new List<int>();
            foreach (ApplicationWindow tab in tabsInOrder)
            {
                int pin = IndexOf(tab, programs);
                if (pin >= 0 && !runningPins.Contains(pin))
                {
                    runningPins.Add(pin);
                }
            }

            List<int> slots = runningPins.OrderBy(i => i).ToList();
            var result = new List<string>(links);
            for (int i = 0; i < slots.Count; i++)
            {
                result[slots[i]] = links[runningPins[i]];
            }

            return result;
        }

        /// <summary>The pins (shortcut files), in the quick launch toolbar's order.</summary>
        public static List<string> GetLinks()
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

                result.AddRange(files);
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
