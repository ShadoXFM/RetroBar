using ManagedShell.Common.Logging;
using ManagedShell.ShellFolders;
using ManagedShell.WindowsTasks;
using RetroBar.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;

namespace RetroBar.Controls
{
    // In the Windows 11 style of the taskbar (IconOnly) the pinned programs are among the tabs: a pinned program that is running is its
    // tab, at the place of its pin, and one that is not running is its icon, which starts it. The tabs and the pins are one list, the pins
    // first in the order of the pins, then the tabs of the other programs in the order they have.
    public partial class TaskList
    {
        private ObservableCollection<object> _mergedItems;
        private ShellFolder _pinFolder;

        private void ApplyPinnedMerge()
        {
            if (taskbarItems == null)
            {
                return;
            }

            if (IconOnly)
            {
                if (_mergedItems == null)
                {
                    _mergedItems = new ObservableCollection<object>();
                    string folder = Environment.ExpandEnvironmentVariables(Settings.Instance.QuickLaunchPath ?? "");
                    if (Directory.Exists(folder))
                    {
                        _pinFolder = new ShellFolder(folder, IntPtr.Zero, true);
                        _pinFolder.Files.CollectionChanged += (s, e) => RebuildMerged();
                    }

                    taskbarItems.CollectionChanged += TasksChangedForMerge;
                }

                // The pins can be dragged to another place (see MergedDropHandler); the other tabs stay after them.
                GongSolutions.Wpf.DragDrop.DragDrop.SetDropHandler(TasksList, new MergedDropHandler(this));
                TasksList.ItemsSource = _mergedItems;
                RebuildMerged();
            }
            else if (_mergedItems != null)
            {
                taskbarItems.CollectionChanged -= TasksChangedForMerge;
                TasksList.ItemsSource = taskbarItems;
                GongSolutions.Wpf.DragDrop.DragDrop.SetDropHandler(TasksList, new RetroBar.Utilities.TaskListDropHandler(TasksList));
                _pinFolder?.Dispose();
                _pinFolder = null;
                _mergedItems = null;
            }
        }

        private void TasksChangedForMerge(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            RebuildMerged();
        }

        // The list the tabs show: for each pin, in order, the tab of its program if it has one, else the pin; then the other tabs.
        private void RebuildMerged()
        {
            if (_mergedItems == null || taskbarItems == null)
            {
                return;
            }

            List<string> links = PinnedApps.GetLinks();
            List<string> programs = links.Select(PinnedApps.TargetExe).ToList();
            List<ApplicationWindow> tabs = taskbarItems.OfType<ApplicationWindow>().ToList();

            var desired = new List<object>();
            var placed = new List<ApplicationWindow>();
            for (int pin = 0; pin < links.Count; pin++)
            {
                ApplicationWindow tab = tabs.FirstOrDefault(w => !placed.Any(p => ReferenceEquals(p, w)) && PinnedApps.IndexOf(w, programs) == pin);
                if (tab != null)
                {
                    desired.Add(tab);
                    placed.Add(tab);
                }
                else
                {
                    ShellFile file = _pinFolder?.Files.FirstOrDefault(f => string.Equals(f.Path, links[pin], StringComparison.OrdinalIgnoreCase));
                    if (file != null)
                    {
                        desired.Add(file);
                    }
                }
            }

            desired.AddRange(tabs.Where(w => !placed.Any(p => ReferenceEquals(p, w))));

            // In place, so a tab that stays is not made again (and the ones that move slide as the list is changed one step at a time).
            for (int i = 0; i < desired.Count; i++)
            {
                if (i < _mergedItems.Count && ReferenceEquals(_mergedItems[i], desired[i]))
                {
                    continue;
                }

                int existing = -1;
                for (int j = i + 1; j < _mergedItems.Count; j++)
                {
                    if (ReferenceEquals(_mergedItems[j], desired[i]))
                    {
                        existing = j;
                        break;
                    }
                }

                if (existing >= 0)
                {
                    _mergedItems.Move(existing, i);
                }
                else
                {
                    _mergedItems.Insert(i, desired[i]);
                }
            }

            while (_mergedItems.Count > desired.Count)
            {
                _mergedItems.RemoveAt(_mergedItems.Count - 1);
            }

            SetTaskButtonWidth();
        }

        // The pin (shortcut file) that a tab of the merged list stands for, or null for a tab of a program that is not pinned.
        private string LinkOf(object item)
        {
            if (item is ShellFile file)
            {
                return file.Path;
            }

            if (item is ApplicationWindow window)
            {
                List<string> links = PinnedApps.GetLinks();
                int pin = PinnedApps.IndexOf(window, links.Select(PinnedApps.TargetExe).ToList());
                return pin >= 0 ? links[pin] : null;
            }

            return null;
        }

        /// <summary>
        /// Drag and drop in the Windows 11 style: a pin (a tab of a pinned program, or the icon of one that is not running) goes to another
        /// place among the pins, and the order of the pins is saved - the quick launch toolbar of the classic style has it too; the tab of a
        /// program that is not pinned goes to another place among the other tabs. The pins are first: a place is taken to be the nearest
        /// one in the group the dragged tab is of.
        /// </summary>
        private sealed class MergedDropHandler : GongSolutions.Wpf.DragDrop.IDropTarget
        {
            private readonly TaskList _owner;

            public MergedDropHandler(TaskList owner)
            {
                _owner = owner;
            }

            private int PinCount => _owner._mergedItems.Count(item => _owner.LinkOf(item) != null);

            // The place in the list for a drop of this tab: among the pins for a pin, after them for any other tab.
            private int Place(object dragged, int insertIndex)
            {
                int pins = PinCount;
                return _owner.LinkOf(dragged) != null ? Math.Min(insertIndex, pins) : Math.Max(insertIndex, pins);
            }

            public void DragOver(GongSolutions.Wpf.DragDrop.IDropInfo dropInfo)
            {
                if (dropInfo.Data is not (ApplicationWindow or ShellFile))
                {
                    return;
                }

                // No mark of the library's: it is made again at every mouse move and flashes (see DropIndicator).
                dropInfo.DropTargetAdorner = null;
                dropInfo.Effects = System.Windows.DragDropEffects.Move;
                RetroBar.Utilities.DropIndicator.Show(_owner.TasksList, Place(dropInfo.Data, dropInfo.InsertIndex));
            }

            public void DragLeave(GongSolutions.Wpf.DragDrop.IDropInfo dropInfo)
            {
                RetroBar.Utilities.DropIndicator.Hide(_owner.TasksList);
            }

            public void Drop(GongSolutions.Wpf.DragDrop.IDropInfo dropInfo)
            {
                RetroBar.Utilities.DropIndicator.Hide(_owner.TasksList);

                int place = Place(dropInfo.Data, dropInfo.InsertIndex);
                Dictionary<object, System.Windows.Point> before = ItemSlideAnimation.Capture(_owner.TasksList);

                string moved = _owner.LinkOf(dropInfo.Data);
                if (moved != null)
                {
                    DropPin(moved, place);
                }
                else if (dropInfo.Data is ApplicationWindow window)
                {
                    DropTab(window, place);
                }
                else
                {
                    return;
                }

                _owner.RebuildMerged();
                ItemSlideAnimation.Play(_owner.TasksList, before);
            }

            private void DropPin(string moved, int place)
            {
                var pins = _owner._mergedItems.Select(_owner.LinkOf).Where(link => link != null).ToList();
                int from = pins.FindIndex(link => string.Equals(link, moved, StringComparison.OrdinalIgnoreCase));
                if (from < 0)
                {
                    return;
                }

                int to = Math.Min(place, pins.Count);
                if (to > from)
                {
                    to--;
                }

                if (to == from)
                {
                    return;
                }

                pins.RemoveAt(from);
                pins.Insert(to, moved);

                // The pins that are not shown keep their places after these.
                pins.AddRange(PinnedApps.GetLinks().Where(link => !pins.Any(p => string.Equals(p, link, StringComparison.OrdinalIgnoreCase))));
                PinnedApps.SaveOrder(pins);
            }

            // A tab of a program that is not pinned: its window goes to just before the one of the tab it was dropped before (or to the
            // end), with the others of its program after it.
            private void DropTab(ApplicationWindow window, int place)
            {
                ObservableCollection<ApplicationWindow> source = _owner.startupSortSource;
                if (source == null)
                {
                    return;
                }

                List<object> others = _owner._mergedItems.Where(item => !ReferenceEquals(item, window)).ToList();
                int pins = PinCount;
                int index = place - (_owner._mergedItems.IndexOf(window) < place ? 1 : 0);
                index = Math.Clamp(index, pins, others.Count);

                int from = source.IndexOf(window);
                if (from < 0)
                {
                    return;
                }

                int to;
                if (index < others.Count && others[index] is ApplicationWindow next && source.IndexOf(next) is int nextAt && nextAt >= 0)
                {
                    to = nextAt > from ? nextAt - 1 : nextAt;
                }
                else
                {
                    to = source.Count - 1;
                }

                if (to != from)
                {
                    source.Move(from, to);
                }

                TaskOpenOrderComparer.KeepAppWindowsTogether(source, window);
            }
        }

        #region Pinned programs that are not running
        private void Launcher_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is ToolbarButton button && button.DataContext is ShellFile file && InvokePinMenu(file, false))
            {
                e.Handled = true;
            }
        }

        private void Launcher_OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is ToolbarButton button && button.DataContext is ShellFile file && InvokePinMenu(file, true))
            {
                e.Handled = true;
            }
        }

        // What the quick launch toolbar does with a pin: a click starts it, a right click shows the shell's menu of the shortcut.
        private bool InvokePinMenu(ShellFile file, bool interactive)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.Path))
            {
                return false;
            }

            Mouse.Capture(null);
            try
            {
                var _ = new ShellItemContextMenu(new ShellItem[] { file }, _pinFolder, IntPtr.Zero, (action, items, allFolders) => false, interactive, false, new ShellMenuCommandBuilder(), new ShellMenuCommandBuilder());
                return true;
            }
            catch (Exception ex)
            {
                ShellLogger.Error($"TaskList: Unable to use the pin {file.Path}", ex);
                return false;
            }
        }
        #endregion
    }
}
