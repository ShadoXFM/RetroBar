using OpenHardwareMonitor.Hardware;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace RetroBar.CpuTempHelper
{
    /// <summary>
    /// Always-elevated, standalone process that polls CPU temperature sensors and writes the
    /// result to a shared file for RetroBar.exe's own (non-elevated) CpuTemperatureMonitor to
    /// read - see that class's own doc comment for why the split exists and how this is meant to
    /// be scheduled (Task Scheduler, "Run with highest privileges", at logon).
    /// </summary>
    internal static class Program
    {
        private static readonly string OutputPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RetroBar", "cputemp.json");

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

        private static void Main()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));

            var computer = new Computer { IsCpuEnabled = true };

            try
            {
                computer.Open(false);
            }
            catch
            {
                // No admin rights, or the underlying kernel driver couldn't load - nothing to do.
                // RetroBar's own CpuTemperatureMonitor just treats an absent/stale file as
                // "unavailable" rather than needing an explicit failure signal here.
                return;
            }

            try
            {
                IHardware cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                if (cpu == null)
                {
                    return;
                }

                while (true)
                {
                    WriteTemperature(cpu);
                    Thread.Sleep(PollInterval);
                }
            }
            finally
            {
                computer.Close();
            }
        }

        private static void WriteTemperature(IHardware cpu)
        {
            try
            {
                cpu.Update();

                float[] temperatures = cpu.Sensors
                    .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue)
                    .Select(s => s.Value.Value)
                    .ToArray();

                if (temperatures.Length == 0)
                {
                    return;
                }

                var payload = new CpuTempPayload
                {
                    Temperature = temperatures.Average(),
                    UpdatedUtc = DateTime.UtcNow,
                };

                // Write to a temp file and rename over the real one, rather than writing the real
                // path directly - so a reader on the other (unelevated) process never sees a
                // half-written file mid-poll.
                string tempPath = OutputPath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(payload));
                File.Move(tempPath, OutputPath, true);
            }
            catch
            {
                // A transient sensor read failure just means this poll's update is skipped -
                // CpuTemperatureMonitor treats a stale file as "unavailable" rather than crashing
                // on a single missed write.
            }
        }
    }
}
