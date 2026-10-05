using System;

namespace RetroBar.CpuTempHelper
{
    /// <summary>Kept in sync by hand with RetroBar.Utilities.CpuTemperatureMonitor's own copy of
    /// this shape - the two projects don't share code, so property names/types have to match
    /// exactly here for System.Text.Json's default (exact-name) matching to round-trip
    /// correctly.</summary>
    internal class CpuTempPayload
    {
        public double Temperature { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
