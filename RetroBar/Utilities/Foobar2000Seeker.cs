using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ManagedShell.Common.Logging;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Seeks foobar2000, which does not let Windows seek it: through its command line, which runs its menu commands in the player that is
    /// running ("foobar2000.exe /command:"Ahead by 30 seconds""). There is no command for a position, so a seek is as many of the
    /// relative ones as it takes (a minute, 30, 10 and 5 seconds), to the nearest 5 seconds.
    /// </summary>
    public static class Foobar2000Seeker
    {
        // The longest first; the number of commands one seek runs is limited (a seek of an hour is not a hundred and twenty processes).
        private static readonly (int Seconds, string Name)[] Steps =
        {
            (60, "1 minute"),
            (30, "30 seconds"),
            (10, "10 seconds"),
            (5, "5 seconds"),
        };

        private const int MaxCommands = 30;

        public static bool IsFoobar2000(string appUserModelId) =>
            appUserModelId != null && appUserModelId.Contains("foobar2000", StringComparison.OrdinalIgnoreCase);

        /// <summary>The menu commands that seek by about that much ("Ahead by 1 minute", "Back by 5 seconds"...), longest first.</summary>
        public static IReadOnlyList<string> CommandsFor(TimeSpan delta)
        {
            int remaining = (int)(Math.Round(Math.Abs(delta.TotalSeconds) / 5) * 5);
            string direction = delta < TimeSpan.Zero ? "Back" : "Ahead";
            var commands = new List<string>();
            foreach ((int seconds, string name) in Steps)
            {
                while (remaining >= seconds && commands.Count < MaxCommands)
                {
                    commands.Add($"{direction} by {name}");
                    remaining -= seconds;
                }
            }

            return commands;
        }

        /// <summary>Seeks the running foobar2000 by about that much. Does nothing if it is not found.</summary>
        public static Task SeekByAsync(TimeSpan delta)
        {
            IReadOnlyList<string> commands = CommandsFor(delta);
            if (commands.Count == 0)
            {
                return Task.CompletedTask;
            }

            return Task.Run(() =>
            {
                string exe = FindExecutable();
                if (exe == null)
                {
                    return;
                }

                // All the commands in one run of the program, so that foobar2000 does them one straight after the other: one run for each
                // made it seek, play a moment, and seek again, a few times, which could be heard.
                string arguments = string.Join(" ", commands.Select(command => $"/command:\"{command}\""));
                try
                {
                    using Process process = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true });
                    process?.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    ShellLogger.Debug($"Foobar2000Seeker: unable to run '{arguments}': {ex.Message}");
                }
            });
        }

        // The program of the player that is running (its command line goes to that one).
        private static string FindExecutable()
        {
            foreach (Process process in Process.GetProcessesByName("foobar2000"))
            {
                try
                {
                    string path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path))
                    {
                        return path;
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Not allowed to read this one (another user's): try the next.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return null;
        }
    }
}
