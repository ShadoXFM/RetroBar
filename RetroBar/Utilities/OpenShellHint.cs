using ManagedShell.AppBar;
using ManagedShell.Common.Logging;
using Microsoft.Win32;
using System;

namespace RetroBar.Utilities
{
    /// <summary>
    /// What the Open Shell menu is told about the taskbar, in the registry (HKCU\Software\RetroBar\OpenShell), for it to be placed where the
    /// Windows 11 style wants it: Win11Style while that style is on (the menu is then at the left edge when the Start button was pressed,
    /// in the middle of the screen when it was not), and ButtonTick, when the Start button was pressed (as GetTickCount64 has the time).
    /// The menu has to be put in its place by Open Shell itself, before its slide: RetroBar moving it was applied when the slide was done.
    /// </summary>
    public static class OpenShellHint
    {
        private const string KeyPath = @"Software\RetroBar\OpenShell";

        /// <summary>Whether the Windows 11 style is on (on a taskbar at the top or the bottom), or off (also when RetroBar closes).</summary>
        public static void SetWindows11Style(bool on)
        {
            Write("Win11Style", on ? 1 : 0, RegistryValueKind.DWord);
        }

        /// <summary>The Start button was pressed now.</summary>
        public static void StartButtonPressed()
        {
            Write("ButtonTick", Environment.TickCount64, RegistryValueKind.QWord);
        }

        /// <summary>The setting as it is now.</summary>
        public static void Update()
        {
            SetWindows11Style(Settings.Instance.TaskbarStyle == TaskbarStyleOption.Windows11 &&
                              Settings.Instance.Edge is AppBarEdge.Bottom or AppBarEdge.Top);
        }

        private static void Write(string name, object value, RegistryValueKind kind)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath);
                key?.SetValue(name, value, kind);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
                ShellLogger.Warning($"OpenShellHint: Unable to write {name}: {ex.Message}");
            }
        }
    }
}
