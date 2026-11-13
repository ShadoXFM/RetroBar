using System.Diagnostics;

namespace RetroBar.Utilities
{
    /// <summary>
    /// How much of the UI thread's time the hooks that hear every window's move take (all of them run on it), counted for the log of a popup's
    /// slide: a move of the popup's window took several times what the same call takes for a window that nothing listens to.
    /// 0 BackdropBlur, 1 the taskbar's blur refresh, 2 the orb's raising, 3 the orb's menu watch, 4 the orb's z-order hook.
    /// </summary>
    internal static class SlideProbe
    {
        public static readonly long[] Ticks = new long[6];
        public static readonly int[] Count = new int[6];

        public static long Start() => Stopwatch.GetTimestamp();

        public static void Stop(int which, long start)
        {
            Ticks[which] += Stopwatch.GetTimestamp() - start;
            Count[which]++;
        }
    }
}
