using ManagedShell.Common.Logging;
using System;
using System.IO;
using System.Text.Json;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Reads CPU temperature written by the separate, always-elevated RetroBar.CpuTempHelper
    /// process (see that project) into a shared file, rather than reading sensors directly in
    /// this (non-elevated) process - the underlying sensor read needs admin rights (the library
    /// loads a kernel driver to read CPU MSRs), which RetroBar.exe itself deliberately doesn't
    /// require. ReadTemperature reports unavailable (null) whenever that file doesn't exist or
    /// hasn't been updated recently, rather than trying to launch or elevate the helper itself -
    /// see the helper project's own doc comment for how it's meant to be set up (Task Scheduler,
    /// "Run with highest privileges", at logon).
    /// </summary>
    public class CpuTemperatureMonitor
    {
        private static readonly string SharedFilePath = "cputemp.json".InLocalAppData();

        // If the helper hasn't updated this file more recently than this, treat it as not
        // running (crashed, never started, or the user hasn't set up the scheduled task) rather
        // than showing a stale reading indefinitely.
        private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

        public double? ReadTemperature()
        {
            try
            {
                if (!File.Exists(SharedFilePath))
                {
                    return null;
                }

                string json = File.ReadAllText(SharedFilePath);
                CpuTempPayload payload = JsonSerializer.Deserialize<CpuTempPayload>(json);

                if (payload == null || DateTime.UtcNow - payload.UpdatedUtc > StaleAfter)
                {
                    return null;
                }

                return payload.Temperature;
            }
            catch (Exception e)
            {
                ShellLogger.Debug($"CpuTemperatureMonitor: Unable to read {SharedFilePath}: {e.Message}");
                return null;
            }
        }

        /// <summary>Kept in sync by hand with RetroBar.CpuTempHelper/Program.cs's own copy of
        /// this shape - the two projects don't share code, so property names/types have to match
        /// exactly here for System.Text.Json's default (exact-name) matching to round-trip
        /// correctly.</summary>
        private class CpuTempPayload
        {
            public double Temperature { get; set; }
            public DateTime UpdatedUtc { get; set; }
        }
    }
}
