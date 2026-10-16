using ManagedShell.WindowsTasks;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Decides the order of the taskbar's tabs: the windows of the pinned programs first, in the order of the pins (the quick launch
    /// order), and every other window after them, as it comes (the order of the list, which is the order the windows appeared in).
    /// There is no arrangement of the user's own kept, and nothing is ordered by when a window was opened.
    ///
    /// The order is applied to the underlying list (one Move at a time), not as a permanent sort on the tab view, so dragging a tab to
    /// rearrange - which moves items in that list - works until the list is sorted again (when a window appears).
    /// </summary>
    public static class TaskOpenOrderComparer
    {
        /// <summary>Reorders the list (in place, one Move at a time, so bound views just see the tabs shift): the pinned programs' windows
        /// first, in the order of the pins, then the rest in the order they already have.</summary>
        public static void SortSource(ObservableCollection<ApplicationWindow> source)
        {
            if (source == null)
            {
                return;
            }

            List<string> pinned = PinnedApps.Get();

            // A stable sort: windows of one pinned program (and the unpinned ones) keep the order they have.
            ApplicationWindow[] sorted = source
                .OrderBy(window =>
                {
                    int pin = PinnedApps.IndexOf(window, pinned);
                    return pin >= 0 ? pin : int.MaxValue;
                })
                .ToArray();

            for (int target = 0; target < sorted.Length; target++)
            {
                int current = source.IndexOf(sorted[target]);
                if (current >= 0 && current != target)
                {
                    source.Move(current, target);
                }
            }
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
    }
}
