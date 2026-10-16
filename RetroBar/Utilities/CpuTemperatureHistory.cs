using System;
using System.Collections.Generic;
using System.Linq;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The CPU temperatures read over the last five minutes, for the graph the CPU temperature widget opens. Shared by the taskbars (each
    /// has a widget, and each reads the helper's file): a reading is kept once, by the time the helper wrote it. Only kept in memory, from
    /// when RetroBar started.
    /// </summary>
    public static class CpuTemperatureHistory
    {
        public static readonly TimeSpan Span = TimeSpan.FromMinutes(5);

        public readonly record struct Sample(DateTime Utc, double Celsius);

        private static readonly List<Sample> Samples = new();
        private static readonly object Lock = new();

        public static void Record(double celsius, DateTime utc)
        {
            lock (Lock)
            {
                if (Samples.Count > 0 && utc <= Samples[^1].Utc)
                {
                    return;
                }

                Samples.Add(new Sample(utc, celsius));

                DateTime oldest = DateTime.UtcNow - Span - TimeSpan.FromSeconds(10);
                int stale = Samples.FindLastIndex(sample => sample.Utc < oldest);
                if (stale >= 0)
                {
                    Samples.RemoveRange(0, stale + 1);
                }
            }
        }

        /// <summary>The readings of the last five minutes, oldest first.</summary>
        public static Sample[] Last()
        {
            DateTime from = DateTime.UtcNow - Span;
            lock (Lock)
            {
                return Samples.Where(sample => sample.Utc >= from).ToArray();
            }
        }
    }
}
