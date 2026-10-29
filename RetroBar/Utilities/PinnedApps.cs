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
            if (string.IsNullOrEmpty(exe))
            {
                return null;
            }

            return GetLinks().FirstOrDefault(link => GetTargetExe(link).Equals(exe, StringComparison.OrdinalIgnoreCase));
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
                List<string> found = Directory.GetFiles(folder, "*.lnk").ToList();
                found.AddRange(ImplicitLinks(folder).Where(implicitLink =>
                    !found.Any(f => string.Equals(Path.GetFileName(f), Path.GetFileName(implicitLink), StringComparison.OrdinalIgnoreCase))));
                List<string> files = found
                    .OrderBy(file =>
                    {
                        // A pin that is in another folder now (see ImplicitLinks) keeps the place it had.
                        int index = order.FindIndex(o => o.Equals(file, StringComparison.OrdinalIgnoreCase));
                        if (index < 0)
                        {
                            index = order.FindIndex(o => string.Equals(Path.GetFileName(o), Path.GetFileName(file), StringComparison.OrdinalIgnoreCase));
                        }

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

        private static readonly object ImplicitLock = new();
        private static (DateTime At, string Folder, List<string> Links) _implicitCache = (DateTime.MinValue, null, new List<string>());

        /// <summary>
        /// The pins that Windows keeps beside the ones in its taskbar's folder: a program pinned from its entry in the Start menu (Helium, a
        /// program that has its own application id) has its shortcut in ImplicitAppShortcuts, in a folder of its own, and only the taskbar's
        /// own list (the Favorites value of the Taskband key) says it is pinned - the other shortcuts there are the ones Windows made for
        /// every program that was started from the Start menu. Only when the pins are the Windows taskbar's (the folder is the default).
        /// </summary>
        public static List<string> ImplicitLinks(string folder)
        {
            lock (ImplicitLock)
            {
                if (_implicitCache.Folder == folder && DateTime.UtcNow - _implicitCache.At < TimeSpan.FromSeconds(1))
                {
                    return _implicitCache.Links;
                }
            }

            var result = new List<string>();
            try
            {
                string windows = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
                if (string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), windows, StringComparison.OrdinalIgnoreCase))
                {
                    string root = Path.Combine(Path.GetDirectoryName(windows), "ImplicitAppShortcuts");
                    using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
                    if (Directory.Exists(root) && key?.GetValue("Favorites") is byte[] favorites)
                    {
                        foreach (string directory in Directory.GetDirectories(root))
                        {
                            foreach (string link in Directory.GetFiles(directory, "*.lnk"))
                            {
                                string name = Path.GetFileName(link);
                                if (Contains(favorites, System.Text.Encoding.Unicode.GetBytes(name)) || Contains(favorites, System.Text.Encoding.Latin1.GetBytes(name)))
                                {
                                    result.Add(link);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ShellLogger.Error("PinnedApps: Unable to read the pins Windows keeps in ImplicitAppShortcuts", ex);
            }

            lock (ImplicitLock)
            {
                _implicitCache = (DateTime.UtcNow, folder, result);
            }

            return result;
        }

        private static bool Contains(byte[] data, byte[] pattern) => data.AsSpan().IndexOf(pattern) >= 0;

        private static readonly Dictionary<string, (DateTime Written, string Path)> TargetPathCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The full path of the program a pin starts, or an empty string (File Explorer's pin, a program started through an updater, ...).</summary>
        public static string TargetPath(string link)
        {
            try
            {
                DateTime written = File.GetLastWriteTimeUtc(link);
                lock (TargetPathCache)
                {
                    if (TargetPathCache.TryGetValue(link, out var cached) && cached.Written == written)
                    {
                        return cached.Path;
                    }
                }

                string path = "";
                try
                {
                    Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                    dynamic shell = Activator.CreateInstance(shellType);
                    dynamic shortcut = shell.CreateShortcut(link);
                    string target = shortcut.TargetPath;
                    string arguments = shortcut.Arguments;
                    // A program started through an updater ("Update.exe --processStart Discord.exe") has its icon in another file.
                    if (!string.IsNullOrEmpty(target) && File.Exists(target) && (arguments == null || arguments.IndexOf("--processStart ", StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        path = target;
                    }
                }
                catch (Exception)
                {
                }

                lock (TargetPathCache)
                {
                    TargetPathCache[link] = (written, path);
                }

                return path;
            }
            catch (Exception)
            {
                return "";
            }
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
