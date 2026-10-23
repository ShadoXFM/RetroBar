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

                // The pinned programs have their own order (the pins'), so tabs are not dragged about.
                GongSolutions.Wpf.DragDrop.DragDrop.SetIsDragSource(TasksList, false);
                GongSolutions.Wpf.DragDrop.DragDrop.SetIsDropTarget(TasksList, false);
                TasksList.ItemsSource = _mergedItems;
                RebuildMerged();
            }
            else if (_mergedItems != null)
            {
                taskbarItems.CollectionChanged -= TasksChangedForMerge;
                TasksList.ItemsSource = taskbarItems;
                GongSolutions.Wpf.DragDrop.DragDrop.SetIsDragSource(TasksList, true);
                GongSolutions.Wpf.DragDrop.DragDrop.SetIsDropTarget(TasksList, true);
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
