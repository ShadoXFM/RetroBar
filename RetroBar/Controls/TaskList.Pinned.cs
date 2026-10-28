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
    // tab, at the place of its pin, and one that is not running is its icon, which starts it. The tabs and the pins are one list, in the order
    // the user has put them in (Settings.TaskOrder): a pin and the tabs of a program that is not pinned can be among each other. Until it is
    // put in an order, the pins are first, in the order of the pins, then the tabs of the other programs in the order they have.
    public partial class TaskList
    {
        private ObservableCollection<object> _mergedItems;
        private ShellFolder _pinFolder;
        private static readonly Dictionary<string, ShellFolder> PinFolders = new(StringComparer.OrdinalIgnoreCase);

        private void PinFilesChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RebuildMerged();

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
                        // The folder of the pins is kept from one time to the next: made anew when the style came back, it had no files for a
                        // moment (they are read in the background), the pins that are not running were not there, and came a second later, when
                        // all the tabs moved to make room for them.
                        lock (PinFolders)
                        {
                            if (!PinFolders.TryGetValue(folder, out _pinFolder))
                            {
                                _pinFolder = new ShellFolder(folder, IntPtr.Zero, true);
                                PinFolders[folder] = _pinFolder;
                            }
                        }

                        _pinFolder.Files.CollectionChanged += PinFilesChanged;
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
                ReleasePinnedMerge();
                TasksList.ItemsSource = taskbarItems;
                GongSolutions.Wpf.DragDrop.DragDrop.SetDropHandler(TasksList, new RetroBar.Utilities.TaskListDropHandler(TasksList));
            }
        }

        // Lets go of the merged list (and of the folder of the pins it reads); the next ApplyPinnedMerge in the Windows 11 style makes it again.
        private void ReleasePinnedMerge()
        {
            if (taskbarItems != null)
            {
                taskbarItems.CollectionChanged -= TasksChangedForMerge;
            }

            if (_pinFolder != null)
            {
                _pinFolder.Files.CollectionChanged -= PinFilesChanged;
                _pinFolder = null;
            }

            _mergedItems = null;
        }

        private void TasksChangedForMerge(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            RebuildMerged();
        }

        // A place in the list: a pin (its tab if its program is running, else its icon) or the tab(s) of a program that is not pinned.
        private sealed record TabUnit(string Key, bool IsPin, string ProgramFile, List<object> Items);

        private static string PinKey(string link) => "pin|" + link.ToLowerInvariant();

        private static string AppUnitKey(ApplicationWindow window) => "app|" + TaskOpenOrderComparer.AppKey(window);

        // The places the list has, in the order they are shown: the order the user has put them in, and what is not in it yet (a pin, or a
        // program that has just opened) after, the pins first.
        private List<TabUnit> BuildUnits()
        {
            var units = new List<TabUnit>();
            if (taskbarItems == null)
            {
                return units;
            }

            List<string> links = PinnedApps.GetLinks();
            List<string> programs = links.Select(PinnedApps.TargetExe).ToList();
            List<ApplicationWindow> tabs = taskbarItems.OfType<ApplicationWindow>().ToList();

            var placed = new List<ApplicationWindow>();
            for (int pin = 0; pin < links.Count; pin++)
            {
                ApplicationWindow tab = tabs.FirstOrDefault(w => !placed.Any(p => ReferenceEquals(p, w)) && PinnedApps.IndexOf(w, programs) == pin);
                object item = tab;
                if (tab != null)
                {
                    placed.Add(tab);
                }
                else
                {
                    item = _pinFolder?.Files.FirstOrDefault(f => string.Equals(f.Path, links[pin], StringComparison.OrdinalIgnoreCase));
                }

                if (item != null)
                {
                    units.Add(new TabUnit(PinKey(links[pin]), true, programs[pin], new List<object> { item }));
                }
            }

            // The tabs of the programs that are not pinned, the windows of one program together (they are one tab when windows are combined).
            foreach (ApplicationWindow tab in tabs.Where(w => !placed.Any(p => ReferenceEquals(p, w))))
            {
                string key = AppUnitKey(tab);
                TabUnit unit = units.FirstOrDefault(u => !u.IsPin && u.Key == key);
                if (unit == null)
                {
                    units.Add(new TabUnit(key, false, null, new List<object>()));
                    unit = units[^1];
                }

                unit.Items.Add(tab);
            }

            List<string> order = Settings.Instance.TaskOrder ?? new List<string>();
            if (order.Count == 0)
            {
                return units;
            }

            // A pin of a program that was a tab before it was pinned takes that tab's place.
            int IndexOf(TabUnit unit)
            {
                int at = order.FindIndex(k => string.Equals(k, unit.Key, StringComparison.OrdinalIgnoreCase));
                if (at < 0 && unit.IsPin && !string.IsNullOrEmpty(unit.ProgramFile))
                {
                    at = order.FindIndex(k => k.StartsWith("app|", StringComparison.Ordinal) &&
                                              string.Equals(Path.GetFileName(k.Substring(4)), unit.ProgramFile, StringComparison.OrdinalIgnoreCase));
                }

                return at;
            }

            // Stable: what is not in the order yet keeps the order it has (the pins first), after the rest.
            return units.Select((unit, position) => (unit, position, at: IndexOf(unit)))
                .OrderBy(entry => entry.at < 0 ? int.MaxValue : entry.at)
                .ThenBy(entry => entry.position)
                .Select(entry => entry.unit)
                .ToList();
        }

        // The list the tabs show (see BuildUnits).
        private void RebuildMerged()
        {
            if (_mergedItems == null || taskbarItems == null)
            {
                return;
            }

            var desired = new List<object>();
            foreach (TabUnit unit in BuildUnits())
            {
                desired.AddRange(unit.Items);
            }

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
        /// Drag and drop in the Windows 11 style: any tab - a pin (a tab of a pinned program, or the icon of one that is not running) or the
        /// tab of a program that is not pinned - goes to any other place among the others, and the order is saved (Settings.TaskOrder). The
        /// order of the pins is saved too (the quick launch toolbar of the classic style has it).
        /// </summary>
        private sealed class MergedDropHandler : GongSolutions.Wpf.DragDrop.IDropTarget
        {
            private readonly TaskList _owner;

            public MergedDropHandler(TaskList owner)
            {
                _owner = owner;
            }

            public void DragOver(GongSolutions.Wpf.DragDrop.IDropInfo dropInfo)
            {
                if (dropInfo.Data is not (ApplicationWindow or ShellFile))
                {
                    return;
                }

                dropInfo.DropTargetAdorner = GongSolutions.Wpf.DragDrop.DropTargetAdorners.Insert;
                dropInfo.Effects = System.Windows.DragDropEffects.Move;
            }

            public void Drop(GongSolutions.Wpf.DragDrop.IDropInfo dropInfo)
            {
                if (dropInfo.Data is not (ApplicationWindow or ShellFile))
                {
                    return;
                }

                MoveToPlace(dropInfo.Data, dropInfo.InsertIndex);
            }

            /// <summary>Puts the tab (or pin) before the item that is at this index of the list, among the others, and saves the order.</summary>
            public void MoveToPlace(object data, int insertIndex)
            {
                // The tabs are made again for the new order; they are not new windows coming in (see TaskButton.Animate).
                TaskButton.SuppressSlideInUntilUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);
                Dictionary<object, System.Windows.Point> before = ItemSlideAnimation.Capture(_owner.TasksList);

                List<TabUnit> units = _owner.BuildUnits();
                int moved = units.FindIndex(unit => unit.Items.Any(item => ReferenceEquals(item, data)));
                if (moved < 0)
                {
                    return;
                }

                // The place, in places: the one the tab was dropped before (the place of a program with several tabs is all of them).
                int target = units.Count;
                int start = 0;
                for (int i = 0; i < units.Count; i++)
                {
                    if (insertIndex <= start)
                    {
                        target = i;
                        break;
                    }

                    start += units[i].Items.Count;
                    if (insertIndex < start)
                    {
                        // In the middle of a place of several tabs: before it.
                        target = i;
                        break;
                    }
                }

                TabUnit unitMoved = units[moved];
                units.RemoveAt(moved);
                if (target > moved)
                {
                    target--;
                }

                units.Insert(Math.Clamp(target, 0, units.Count), unitMoved);

                SaveOrder(units);
                _owner.RebuildMerged();
                ItemSlideAnimation.Play(_owner.TasksList, before);
            }

            // The order of the places as shown is the order saved; the places that are not shown now (a program that is closed and not
            // pinned) stay after the one that was before them.
            private static void SaveOrder(List<TabUnit> units)
            {
                List<string> shown = units.Select(unit => unit.Key).ToList();
                List<string> previous = Settings.Instance.TaskOrder ?? new List<string>();
                var result = new List<string>(shown);
                for (int i = 0; i < previous.Count; i++)
                {
                    if (shown.Any(k => string.Equals(k, previous[i], StringComparison.OrdinalIgnoreCase)) || result.Any(k => string.Equals(k, previous[i], StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    // After the nearest one before it that is in the result.
                    int after = -1;
                    for (int j = i - 1; j >= 0 && after < 0; j--)
                    {
                        after = result.FindIndex(k => string.Equals(k, previous[j], StringComparison.OrdinalIgnoreCase));
                    }

                    result.Insert(after + 1, previous[i]);
                }

                Settings.Instance.TaskOrder = result;

                // The pins in the order they are in now; the ones that are not shown keep their places after these.
                List<string> pins = units.Where(unit => unit.IsPin).Select(unit => unit.Key.Substring(4)).ToList();
                List<string> links = PinnedApps.GetLinks();
                List<string> ordered = pins
                    .Select(lowered => links.FirstOrDefault(link => string.Equals(link, lowered, StringComparison.OrdinalIgnoreCase)))
                    .Where(link => link != null)
                    .ToList();
                ordered.AddRange(links.Where(link => !ordered.Any(p => string.Equals(p, link, StringComparison.OrdinalIgnoreCase))));
                PinnedApps.SaveOrder(ordered);
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
            if (sender is ToolbarButton button && button.DataContext is ShellFile file && (ShowPinMenu(button, file) || InvokePinMenu(file, true)))
            {
                e.Handled = true;
            }
        }

        // The right-click menu of a pin that is not running: the one a tab has (see TaskButton.AppButton_OnContextMenuOpening), without what
        // is for a window - the program's jump list, the program (a click starts it), and taking the pin off. False when there is no
        // shortcut to make it from (the shell's own menu is shown then).
        private bool ShowPinMenu(ToolbarButton button, ShellFile file)
        {
            string link = file?.Path;
            if (string.IsNullOrWhiteSpace(link) || !File.Exists(link))
            {
                return false;
            }

            try
            {
                string exe = PinnedApps.TargetPath(link);
                bool explorer = string.Equals(PinnedApps.TargetExe(link), "explorer.exe", StringComparison.OrdinalIgnoreCase);

                var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
                menu.SetBinding(System.Windows.Media.TextOptions.TextRenderingModeProperty, new System.Windows.Data.Binding("AllowFontSmoothingMenu")
                {
                    Source = Settings.Instance,
                    Converter = (System.Windows.Data.IValueConverter)FindResource("menuTextRenderingModeConverter"),
                });

                foreach (object entry in TaskButton.JumpListMenuEntries(this, string.IsNullOrEmpty(exe) ? null : exe, null, link, explorer))
                {
                    menu.Items.Add(entry);
                }

                // The program's name and icon (made as the tab's menu makes them): a click starts it.
                System.Windows.Media.ImageSource icon = TaskButton.ProgramIconOf(string.IsNullOrEmpty(exe) ? link : exe, null);
                System.Windows.Controls.Image image = null;
                if (icon != null)
                {
                    image = new System.Windows.Controls.Image { Source = icon, Width = 16, Height = 16, UseLayoutRounding = true, SnapsToDevicePixels = true };
                    System.Windows.Media.RenderOptions.SetBitmapScalingMode(image, System.Windows.Media.BitmapScalingMode.HighQuality);
                    MonitorOffset.SetTag(image, "PreviewTitleIcon");
                }

                var start = new System.Windows.Controls.MenuItem
                {
                    Header = Path.GetFileNameWithoutExtension(link).Replace("_", "__"),
                    Icon = image,
                    FontWeight = System.Windows.FontWeights.Bold,
                    Style = MenuItemStyle.Style,
                };
                start.Click += (s, args) => InvokePinMenu(file, false);
                menu.Items.Add(start);

                var unpin = new System.Windows.Controls.MenuItem { Header = FindResource("unpin_from_taskbar"), Style = MenuItemStyle.Style };
                unpin.Click += (s, args) =>
                {
                    try
                    {
                        File.Delete(link);
                    }
                    catch (Exception ex)
                    {
                        ShellLogger.Error("TaskList: Unable to unpin the program", ex);
                    }
                };
                menu.Items.Add(unpin);

                Mouse.Capture(null);
                menu.IsOpen = true;
                return true;
            }
            catch (Exception ex)
            {
                ShellLogger.Error($"TaskList: Unable to show the menu of the pin {link}", ex);
                return false;
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
