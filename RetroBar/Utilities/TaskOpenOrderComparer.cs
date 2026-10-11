using ManagedShell.WindowsTasks;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Decides the order of the taskbar's tabs: the user's own arrangement where there is one, and the
    /// order the windows were opened in for anything else.
    ///
    /// The user's arrangement (Settings.TaskOrder) is a list of application keys, saved whenever they
    /// drag a tab (<see cref="SaveOrder"/>) and applied whenever the list is (re)loaded or a window
    /// appears (<see cref="SortSource"/>), so it survives restarts and theme changes. A window is
    /// identified across restarts by its application - the exe path, or the AppUserModelID for
    /// packaged apps - plus a number for each additional window of the same app (windows of one app
    /// are numbered in the order they were opened). A window whose app isn't in the saved list goes
    /// after everything that is, ordered by when it was opened.
    ///
    /// "When it was opened": Windows doesn't expose a window's creation time, and the order TasksService
    /// first enumerates existing windows in at startup is effectively arbitrary, so it is derived - a
    /// window already open when RetroBar starts uses its owning process's start time (a good
    /// approximation, and stable across restarts), while one that appears afterwards is stamped with
    /// the moment RetroBar first sees it.
    ///
    /// The order is applied to the underlying list (one Move at a time), not as a permanent sort on the
    /// tab view, so the user's drag-to-rearrange - which moves items in that list - is respected.
    /// </summary>
    public static class TaskOpenOrderComparer
    {
        // Windows seen within this long of RetroBar starting count as "already open" - the
        // initial enumeration (and any windows that appear while it settles) shouldn't be treated
        // as brand new.
        private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(10);

        private static readonly DateTime AppStartUtc = DateTime.UtcNow;

        // Keyed by the ApplicationWindow object itself (not its HWND), so a recycled handle can't
        // inherit a closed window's old timestamp.
        private static readonly ConditionalWeakTable<ApplicationWindow, object> OpenedAt = new();

        // Windows first seen after startup, and those among them already put at their pin's place.
        private static readonly ConditionalWeakTable<ApplicationWindow, object> Fresh = new();
        private static readonly ConditionalWeakTable<ApplicationWindow, object> PinPlaced = new();

        public static bool InStartupGrace => DateTime.UtcNow - AppStartUtc < StartupGrace;

        /// <summary>
        /// Reorders the list (in place, one Move at a time, so bound views just see the tabs shift)
        /// into the saved arrangement, with anything unsaved after it in opened-at order.
        /// </summary>
        public static void SortSource(ObservableCollection<ApplicationWindow> source)
        {
            if (source == null)
            {
                return;
            }

            Dictionary<ApplicationWindow, string> keys = ComputeKeys(source);
            List<string> saved = Settings.Instance.TaskOrder ?? new List<string>();

            List<string> pinned = PinnedApps.Get();

            // A pinned program's window that opened after startup is put where its pin is (once), whatever the saved
            // arrangement says - see below.
            bool IsFreshPinned(ApplicationWindow window) =>
                Fresh.TryGetValue(window, out _) && !PinPlaced.TryGetValue(window, out _) && PinnedApps.IndexOf(window, pinned) >= 0;

            int Rank(ApplicationWindow window)
            {
                if (IsFreshPinned(window))
                {
                    return int.MaxValue;
                }

                int index = saved.IndexOf(keys[window]);
                return index >= 0 ? index : int.MaxValue;
            }

            ApplicationWindow[] sorted = source.ToArray();
            Array.Sort(sorted, (a, b) =>
            {
                int byRank = Rank(a).CompareTo(Rank(b));
                return byRank != 0 ? byRank : CompareOpened(a, b);
            });

            // A pinned program that has no place of its own yet goes where its pin is - after the tab of the nearest
            // pinned program before it (or before the nearest one after it) - not at the far right.
            List<ApplicationWindow> placed = sorted.ToList();

            foreach (ApplicationWindow window in sorted.Where(w => Rank(w) == int.MaxValue && PinnedApps.IndexOf(w, pinned) >= 0))
            {
                int pin = PinnedApps.IndexOf(window, pinned);
                placed.Remove(window);

                int before = placed.Select(w => PinnedApps.IndexOf(w, pinned)).Where(i => i >= 0 && i <= pin).DefaultIfEmpty(-1).Max();
                if (before >= 0)
                {
                    placed.Insert(placed.FindLastIndex(w => PinnedApps.IndexOf(w, pinned) == before) + 1, window);
                    continue;
                }

                int after = placed.Select(w => PinnedApps.IndexOf(w, pinned)).Where(i => i > pin).DefaultIfEmpty(-1).Min();
                if (after >= 0)
                {
                    placed.Insert(placed.FindIndex(w => PinnedApps.IndexOf(w, pinned) == after), window);
                }
                else
                {
                    placed.Add(window);
                }
            }

            sorted = placed.ToArray();

            for (int target = 0; target < sorted.Length; target++)
            {
                int current = source.IndexOf(sorted[target]);
                if (current >= 0 && current != target)
                {
                    source.Move(current, target);
                }
            }

            ApplicationWindow[] freshPinned = sorted.Where(IsFreshPinned).ToArray();
            if (freshPinned.Length > 0)
            {
                foreach (ApplicationWindow window in freshPinned)
                {
                    PinPlaced.Add(window, null);
                }

                // Saved, so that the next sort doesn't take it back to where an older arrangement had it.
                SaveOrder(source);
            }
        }

        /// <summary>
        /// Stores the list's current order as the user's arrangement. Apps that aren't open right now
        /// keep their place relative to their old neighbors, so reopening one puts it back where it was.
        /// </summary>
        public static void SaveOrder(ObservableCollection<ApplicationWindow> source)
        {
            if (source == null)
            {
                return;
            }

            Dictionary<ApplicationWindow, string> keys = ComputeKeys(source);
            List<string> current = source.Select(window => keys[window]).ToList();
            List<string> previous = Settings.Instance.TaskOrder ?? new List<string>();

            List<string> merged = new List<string>(current);

            foreach (string absent in previous.Where(key => !current.Contains(key)))
            {
                // Insert after the nearest earlier key (in the old order) that is in the new list.
                int oldIndex = previous.IndexOf(absent);
                int insertAt = 0;

                for (int i = oldIndex - 1; i >= 0; i--)
                {
                    int found = merged.IndexOf(previous[i]);
                    if (found >= 0)
                    {
                        insertAt = found + 1;
                        break;
                    }
                }

                merged.Insert(insertAt, absent);
            }

            if (!merged.SequenceEqual(previous))
            {
                Settings.Instance.TaskOrder = merged;
            }
        }

        // application key + "#n": n counts that app's windows in the order they were opened (0 for the
        // first), so two windows of one app keep distinct, restart-stable keys.
        private static Dictionary<ApplicationWindow, string> ComputeKeys(IEnumerable<ApplicationWindow> windows)
        {
            var counts = new Dictionary<string, int>();
            var keys = new Dictionary<ApplicationWindow, string>();

            List<ApplicationWindow> byOpened = windows.ToList();
            byOpened.Sort(CompareOpened);

            foreach (ApplicationWindow window in byOpened)
            {
                string app = GetAppKey(window);
                counts.TryGetValue(app, out int count);
                counts[app] = count + 1;
                keys[window] = $"{app}#{count}";
            }

            return keys;
        }

        /// <summary>Identifies a window's application (exe path, or AppUserModelID for packaged apps): windows with
        /// the same key are the same app, and are combined into one tab when Settings.GroupTaskWindows is on.</summary>
        public static string AppKey(ApplicationWindow window) => GetAppKey(window);

        /// <summary>
        /// After a tab was dragged: moves the other windows of its app (hidden behind it while windows are
        /// combined) to follow it, so it stays the first - and so the shown - window of its app instead of
        /// handing the tab over to a sibling that is still where the dragged one used to be.
        /// </summary>
        public static void KeepAppWindowsTogether(ObservableCollection<ApplicationWindow> source, ApplicationWindow dragged)
        {
            if (source == null || dragged == null)
            {
                return;
            }

            string app = GetAppKey(dragged);
            int position = source.IndexOf(dragged);

            foreach (ApplicationWindow sibling in source.Where(w => !ReferenceEquals(w, dragged) && GetAppKey(w) == app).ToList())
            {
                int current = source.IndexOf(sibling);
                int target = current < position ? position : position + 1;

                if (current != target)
                {
                    source.Move(current, target);
                }

                position = source.IndexOf(dragged);
            }
        }

        private static string GetAppKey(ApplicationWindow window)
        {
            string app = window.IsUWP && !string.IsNullOrEmpty(window.AppUserModelID)
                ? window.AppUserModelID
                : window.WinFileName;

            if (string.IsNullOrEmpty(app))
            {
                app = window.ClassName ?? "";
            }

            return app.ToLowerInvariant();
        }

        private static int CompareOpened(ApplicationWindow a, ApplicationWindow b)
        {
            int byTime = GetOpenedAt(a).CompareTo(GetOpenedAt(b));
            return byTime != 0 ? byTime : a.Handle.ToInt64().CompareTo(b.Handle.ToInt64());
        }

        private static DateTime GetOpenedAt(ApplicationWindow window)
        {
            if (OpenedAt.TryGetValue(window, out object stamped))
            {
                return (DateTime)stamped;
            }

            DateTime openedAt = InStartupGrace ? GetProcessStartUtc(window) : DateTime.UtcNow;
            if (!InStartupGrace)
            {
                Fresh.Add(window, null);
            }

            OpenedAt.Add(window, openedAt);
            return openedAt;
        }

        private static DateTime GetProcessStartUtc(ApplicationWindow window)
        {
            try
            {
                using Process process = Process.GetProcessById((int)window.ProcId);
                return process.StartTime.ToUniversalTime();
            }
            catch
            {
                // Process gone or inaccessible (e.g. an elevated one) - sort it to the far left
                // rather than guessing a time.
                return DateTime.MinValue;
            }
        }
    }
}
