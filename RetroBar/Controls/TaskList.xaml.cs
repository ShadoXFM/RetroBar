using ManagedShell.AppBar;
using ManagedShell.WindowsTasks;
using ManagedShell.Common.Helpers;
using RetroBar.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace RetroBar.Controls
{
    /// <summary>
    /// Interaction logic for TaskList.xaml
    /// </summary>
    public partial class TaskList : UserControl
    {
        private bool isLoaded;
        private bool isScrollable;
        private double DefaultButtonWidth;
        private double TaskButtonLeftMargin;
        private double TaskButtonRightMargin;
        private ICollectionView taskbarItems;

        public static DependencyProperty ButtonWidthProperty = DependencyProperty.Register(nameof(ButtonWidth), typeof(double), typeof(TaskList), new PropertyMetadata(new double()));

        public double ButtonWidth
        {
            get { return (double)GetValue(ButtonWidthProperty); }
            set { SetValue(ButtonWidthProperty, value); }
        }

        public static DependencyProperty ButtonsPerRowProperty = DependencyProperty.Register(nameof(ButtonsPerRow), typeof(int), typeof(TaskList), new PropertyMetadata(0));

        public int ButtonsPerRow
        {
            get { return (int)GetValue(ButtonsPerRowProperty); }
            set { SetValue(ButtonsPerRowProperty, value); }
        }

        // Number of columns in a full row that get one extra pixel, so the row fills the
        // taskbar exactly instead of leaving the floored remainder empty.
        public static DependencyProperty ExtraWidthCountProperty = DependencyProperty.Register(nameof(ExtraWidthCount), typeof(int), typeof(TaskList), new PropertyMetadata(0));

        public int ExtraWidthCount
        {
            get { return (int)GetValue(ExtraWidthCountProperty); }
            set { SetValue(ExtraWidthCountProperty, value); }
        }

        // The DIP size of one physical device pixel on whatever monitor this taskbar is
        // currently on (e.g. 0.8 at 125% scale, 1 at 100%). ButtonWidth is snapped to a multiple
        // of this so every button's width is exactly representable in physical pixels - without
        // that, identical buttons that all share the same non-grid-aligned DIP width (e.g. 118 at
        // 125% scale, where 118*1.25=147.5 isn't a whole pixel) can each drift the same direction
        // when WPF's layout rounding snaps them, and that drift accumulates across a whole row of
        // buttons until it's enough to wrap the last one into a row the taskbar's fixed height
        // has no room for - which looked like a tab just disappearing.
        public static DependencyProperty PixelStepProperty = DependencyProperty.Register(nameof(PixelStep), typeof(double), typeof(TaskList), new PropertyMetadata(1d));

        public double PixelStep
        {
            get { return (double)GetValue(PixelStepProperty); }
            set { SetValue(PixelStepProperty, value); }
        }

        public static DependencyProperty TasksProperty = DependencyProperty.Register(nameof(Tasks), typeof(Tasks), typeof(TaskList), new PropertyMetadata(TasksChangedCallback));

        public Tasks Tasks
        {
            get { return (Tasks)GetValue(TasksProperty); }
            set { SetValue(TasksProperty, value); }
        }

        public static DependencyProperty HostProperty = DependencyProperty.Register(nameof(Host), typeof(Taskbar), typeof(TaskList), new PropertyMetadata(TasksChangedCallback));

        public Taskbar Host
        {
            get { return (Taskbar)GetValue(HostProperty); }
            set { SetValue(HostProperty, value); }
        }

        public TaskList()
        {
            InitializeComponent();
        }

        private void SetStyles()
        {
            DefaultButtonWidth = Application.Current.FindResource("TaskButtonWidth") as double? ?? 0;
            Thickness buttonMargin;

            if (Settings.Instance.Edge == AppBarEdge.Left || Settings.Instance.Edge == AppBarEdge.Right)
            {
                buttonMargin = Application.Current.FindResource("TaskButtonVerticalMargin") as Thickness? ?? new Thickness();
            }
            else
            {
                buttonMargin = Application.Current.FindResource("TaskButtonMargin") as Thickness? ?? new Thickness();
            }

            TaskButtonLeftMargin = buttonMargin.Left;
            TaskButtonRightMargin = buttonMargin.Right;
        }

        private void TaskList_OnLoaded(object sender, RoutedEventArgs e)
        {
            SetStyles();
            SetTasksCollection();
        }

        private void SetTasksCollection()
        {
            if (!isLoaded && Tasks != null && Host != null)
            {
                taskbarItems = Tasks.CreateGroupedWindowsCollection();
                if (taskbarItems != null)
                {
                    // Tabs start out in the order their windows were opened (see TaskOpenOrderComparer).
                    // Applied to the underlying list during startup only, so later drag-rearranging
                    // isn't sorted back.
                    if (taskbarItems is ListCollectionView listView && listView.SourceCollection is ObservableCollection<ApplicationWindow> source)
                    {
                        startupSortSource = source;
                        ScheduleStartupSort();
                    }

                    taskbarItems.CollectionChanged += GroupedWindows_CollectionChanged;
                    taskbarItems.Filter = Tasks_Filter;

                    if (startupSortSource != null)
                    {
                        startupSortSource.CollectionChanged += SourceWindows_CollectionChanged;
                    }
                }

                TasksList.ItemsSource = taskbarItems;

                // Drag-to-rearrange: see TaskListDropHandler.
                GongSolutions.Wpf.DragDrop.DragDrop.SetDropHandler(TasksList, new TaskListDropHandler(TasksList));

                Settings.Instance.PropertyChanged += Settings_PropertyChanged;
                Host.hotkeyManager.TaskbarHotkeyPressed += TaskList_TaskbarHotkeyPressed;

                isLoaded = true;
            }
        }

        private static void TasksChangedCallback(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is TaskList taskList && e.OldValue == null && e.NewValue != null)
            {
                taskList.SetTasksCollection();
            }
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.GroupTaskWindows))
            {
                ScheduleGroupSync();
            }
            else if (e.PropertyName == nameof(Settings.MultiMonMode))
            {
                taskbarItems?.Refresh();
            }
            else if (e.PropertyName == nameof(Settings.ShowMultiMon))
            {
                if (Settings.Instance.MultiMonMode != MultiMonOption.AllTaskbars)
                {
                    taskbarItems?.Refresh();
                }
            }
        }
        private void TaskList_TaskbarHotkeyPressed(object sender, HotkeyManager.TaskbarHotkeyEventArgs e)
        {
            if (Settings.Instance.WinNumHotkeysAction == WinNumHotkeysOption.SwitchTasks && Host.Screen.Primary)
            {
                try
                {
                    bool exists = taskbarItems.MoveCurrentToPosition(e.index);

                    if (exists)
                    {
                        ApplicationWindow window = taskbarItems.CurrentItem as ApplicationWindow;

                        if (e.isShiftPressed)
                        {
                            // Open new instance when Shift is pressed
                            ShellHelper.StartProcess(window.IsUWP ? "appx:" + window.AppUserModelID : window.WinFileName);
                        }
                        else
                        {
                            // Normal behavior - switch to existing window
                            if (window.State == ApplicationWindow.WindowState.Active && window.CanMinimize)
                            {
                                window.Minimize();
                            }
                            else
                            {
                                window.BringToFront();
                            }
                        }
                    }

                }
                catch (ArgumentOutOfRangeException) { }
            }
        }

        /// <summary>
        /// Raised when the set of windows combined under each tab may have changed (a window opened or
        /// closed, or the setting was switched), so the tabs can refresh their counts and previews.
        /// </summary>
        public event EventHandler GroupsChanged;

        /// <summary>
        /// The windows that share this window's tab, in tab order (the window itself first). Just the
        /// window, unless Settings.GroupTaskWindows is on.
        /// </summary>
        public List<ApplicationWindow> GetGroupWindows(ApplicationWindow window)
        {
            var group = new List<ApplicationWindow>();

            if (window != null && Settings.Instance.GroupTaskWindows && startupSortSource != null)
            {
                string app = TaskOpenOrderComparer.AppKey(window);

                foreach (ApplicationWindow other in startupSortSource)
                {
                    if (TaskOpenOrderComparer.AppKey(other) == app && PassesBaseFilter(other))
                    {
                        group.Add(other);
                    }
                }
            }

            if (window != null && !group.Contains(window))
            {
                group.Insert(0, window);
            }

            return group;
        }

        // The tab is shown for the first window of each app, in list order, and the rest are hidden behind it.
        private bool IsShownWindow(ApplicationWindow window)
        {
            if (!PassesBaseFilter(window))
            {
                return false;
            }

            if (!Settings.Instance.GroupTaskWindows || startupSortSource == null)
            {
                return true;
            }

            string app = TaskOpenOrderComparer.AppKey(window);

            foreach (ApplicationWindow other in startupSortSource)
            {
                if (ReferenceEquals(other, window))
                {
                    return true;
                }

                if (TaskOpenOrderComparer.AppKey(other) == app && PassesBaseFilter(other))
                {
                    return false;
                }
            }

            return true;
        }

        private bool groupSyncPending;

        private void SourceWindows_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            ScheduleGroupSync();
        }

        // The view only filters an item as it is added, so when a window closes (or the setting changes)
        // the window that should now be shown for its app has to be re-checked by hand. Re-checking just
        // those items keeps the other tabs as they are; refreshing the whole view would recreate them all.
        private void ScheduleGroupSync()
        {
            if (groupSyncPending)
            {
                return;
            }

            groupSyncPending = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                groupSyncPending = false;
                SyncGroupMembership();
                GroupsChanged?.Invoke(this, EventArgs.Empty);
            }), DispatcherPriority.Background);
        }

        private void SyncGroupMembership()
        {
            if (taskbarItems == null || startupSortSource == null || taskbarItems is not IEditableCollectionView editable)
            {
                return;
            }

            foreach (ApplicationWindow window in startupSortSource.ToArray())
            {
                if (Tasks_Filter(window) == taskbarItems.Contains(window))
                {
                    continue;
                }

                try
                {
                    editable.EditItem(window);
                    editable.CommitEdit();
                }
                catch (Exception)
                {
                    taskbarItems.Refresh();
                    return;
                }
            }
        }

        private bool Tasks_Filter(object obj)
        {
            return obj is not ApplicationWindow window || IsShownWindow(window);
        }

        private bool PassesBaseFilter(ApplicationWindow window)
        {
            if (window != null)
            {
                if (!window.ShowInTaskbar)
                {
                    return false;
                }

                if (!Settings.Instance.ShowMultiMon || Settings.Instance.MultiMonMode == MultiMonOption.AllTaskbars)
                {
                    return true;
                }

                if (Settings.Instance.MultiMonMode == MultiMonOption.SameAsWindowAndPrimary && Host.Screen.Primary)
                {
                    return true;
                }

                IntPtr hMonitor = window.HMonitor;
                if (Host.Screen.Primary && !Host.windowManager.IsValidHMonitor(hMonitor))
                {
                    return true;
                }

                if (hMonitor != Host.Screen.HMonitor)
                {
                    return false;
                }
            }

            return true;
        }

        private void TaskList_OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (taskbarItems != null)
            {
                taskbarItems.CollectionChanged -= GroupedWindows_CollectionChanged;
                taskbarItems.Filter = null;

                if (startupSortSource != null)
                {
                    startupSortSource.CollectionChanged -= SourceWindows_CollectionChanged;
                }
            }

            if (Host != null)
            {
                Host.hotkeyManager.TaskbarHotkeyPressed -= TaskList_TaskbarHotkeyPressed;
            }

            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;

            isLoaded = false;
        }

        private void GroupedWindows_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            SetTaskButtonWidth();

            // Windows trickle in while RetroBar is starting, and new ones appear later: put each where the
            // user's saved arrangement says (or on the right, if its app isn't in it) once they stop arriving.
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
            {
                ScheduleStartupSort();
            }
        }

        private ObservableCollection<ApplicationWindow> startupSortSource;
        private DispatcherTimer startupSortTimer;

        // Debounced: sorts 600ms after the last window was added.
        private void ScheduleStartupSort()
        {
            if (startupSortSource == null)
            {
                return;
            }

            if (startupSortTimer == null)
            {
                startupSortTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                startupSortTimer.Tick += (s, args) =>
                {
                    startupSortTimer.Stop();
                    TaskOpenOrderComparer.SortSource(startupSortSource);
                };
            }

            startupSortTimer.Stop();
            startupSortTimer.Start();
        }

        private void TaskList_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            SetTaskButtonWidth();
        }

        private void SetTaskButtonWidth()
        {
            if (Host is null)
                return; // The state is trashed, but presumably it's just a transition

            if (Settings.Instance.Edge == AppBarEdge.Left || Settings.Instance.Edge == AppBarEdge.Right)
            {
                ExtraWidthCount = 0;
                ButtonWidth = ActualWidth;
                SetScrollable(true); // while technically not always scrollable, we don't run into DPI-specific issues with it enabled while vertical
                return;
            }

            double height = ActualHeight;
            int rows = Host.Rows;

            int taskCount = TasksList.Items.Count;
            TasksList.AlternationCount = taskCount; // keep in sync for correct AlternationIndex

            double dpiScale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            PixelStep = dpiScale > 0 ? 1.0 / dpiScale : 1.0;

            double margin = TaskButtonLeftMargin + TaskButtonRightMargin;
            ButtonsPerRow = Math.Max(1, (int)Math.Ceiling((double)taskCount / rows));
            double maxWidth = TasksList.ActualWidth / ButtonsPerRow;
            double defaultWidth = DefaultButtonWidth + margin;

            if (maxWidth > defaultWidth)
            {
                // Room to spare: keep the default width (snapped to a multiple of PixelStep, same
                // reasoning as the shrink-to-fit branch below) and leave the trailing gap. Without
                // this snap, defaultWidth's own fractional-PixelStep remainder (e.g. 123 DIPs at
                // 125% scale, where 123/0.8 = 153.75 isn't a whole number of physical pixels)
                // accumulates identically across every button's slot, drifting a little further
                // off the physical pixel grid each time until it crosses a whole device pixel -
                // at which point WPF's per-element layout rounding snaps that one button's edge
                // the other way, showing up as an inconsistent (sometimes 1px narrower) gap on a
                // non-100%-scale monitor.
                ExtraWidthCount = 0;
                ButtonWidth = Math.Round(defaultWidth / PixelStep) * PixelStep;
                SetScrollable(false);
            }
            else
            {
                // Shrink every button to fit exactly ButtonsPerRow columns x rows rows - even
                // below the "MinButtonWidth" design target, if there are enough windows open to
                // force it - so a button is always visible (if cramped) instead of disappearing.
                // This used to special-case going below that minimum by setting ButtonWidth to a
                // fixed defaultWidth/2, decoupled from ButtonsPerRow/maxWidth entirely; that let
                // the WrapPanel wrap into more rows than the taskbar's fixed height (from
                // Host.Rows) could show, and the scroll viewer's tiny paging buttons didn't
                // reliably surface the overflow - tabs past that point just vanished.
                //
                // ButtonWidth is snapped down to a multiple of PixelStep (not just floored to a
                // whole DIP) so it's exactly representable in physical pixels on this monitor -
                // otherwise every button sharing the same non-grid-aligned width can drift the
                // same direction under layout rounding, and that drift accumulates across a row
                // until it's enough to push the last button into an unplanned extra row on a
                // non-100%-scale monitor. The "extra 1 DIP" per-column bonus becomes "extra one
                // PixelStep" for the same reason (see TaskButtonWidthConverter).
                double baseWidth = Math.Floor(maxWidth / PixelStep) * PixelStep;
                ButtonWidth = baseWidth;
                double totalGridSafeWidth = Math.Floor(TasksList.ActualWidth / PixelStep) * PixelStep;
                ExtraWidthCount = Math.Max(0, (int)Math.Round((totalGridSafeWidth - baseWidth * ButtonsPerRow) / PixelStep));
                SetScrollable(false);
            }
        }

        private void SetScrollable(bool canScroll)
        {
            if (canScroll == isScrollable) return;

            if (canScroll)
            {
                TasksScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            else
            {
                TasksScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }

            isScrollable = canScroll;
        }

        private void TasksScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (!isScrollable && Settings.Instance.TaskWheelAction == TaskWheelActionOption.DoNothing)
            {
                e.Handled = true;
            }
        }
    }
}