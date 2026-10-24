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
        private double TaskButtonTopMargin;
        private double TaskButtonBottomMargin;
        private ICollectionView taskbarItems;

        public static DependencyProperty ButtonWidthProperty = DependencyProperty.Register(nameof(ButtonWidth), typeof(double), typeof(TaskList), new PropertyMetadata(new double()));

        public double ButtonWidth
        {
            get { return (double)GetValue(ButtonWidthProperty); }
            set { SetValue(ButtonWidthProperty, value); }
        }

        // In the Windows 11 style of the taskbar the tabs are small and show only their icons (see Taskbar.Layout.cs), and the list is as wide as
        // they are, in the middle; AvailableWidth is the room it has.
        public static DependencyProperty IconOnlyProperty = DependencyProperty.Register(nameof(IconOnly), typeof(bool), typeof(TaskList),
            new PropertyMetadata(false, (d, e) => ((TaskList)d).IconOnlyChanged()));

        public bool IconOnly
        {
            get { return (bool)GetValue(IconOnlyProperty); }
            set { SetValue(IconOnlyProperty, value); }
        }

        public static DependencyProperty AvailableWidthProperty = DependencyProperty.Register(nameof(AvailableWidth), typeof(double), typeof(TaskList),
            new PropertyMetadata(0d, (d, e) => ((TaskList)d).SetTaskButtonWidth()));

        public double AvailableWidth
        {
            get { return (double)GetValue(AvailableWidthProperty); }
            set { SetValue(AvailableWidthProperty, value); }
        }

        // The windows of an app are one tab when the setting says so, and always in the Windows 11 style (where an app is one icon).
        private bool CombinesWindows => Settings.Instance.GroupTaskWindows || IconOnly;

        private (double ButtonWidth, double ButtonHeight, double SlotWidth)? _lastTabMeasure;

        // The size of the button of the first tab (a window's, not a launcher's) and of the slot it is in, or null before there is one. A tab
        // that is in use (the active window's, or with its menu open) is another size in some themes, and measured, made every tab larger when
        // it was the first: only the tabs at rest are measured, and the last measure stands while there is none.
        private (double ButtonWidth, double ButtonHeight, double SlotWidth)? MeasureTabButton()
        {
            for (int i = 0; i < TasksList.Items.Count; i++)
            {
                if (TasksList.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject container &&
                    FindTabButton(container) is TaskButton tab && tab.AppButton.ActualHeight > 0 && tab.AppButton.ActualWidth > 0 &&
                    !tab.IsInUse)
                {
                    // The frame that is drawn (the theme's outer border), which is smaller than the button's own box; the box if there is none.
                    var frame = tab.AppButton.Template?.FindName("ButtonOuterBottomBorder", tab.AppButton) as FrameworkElement;
                    if (frame == null || frame.ActualHeight <= 0 || frame.ActualWidth <= 0)
                    {
                        frame = tab.AppButton;
                    }

                    try
                    {
                        // Where the frame is in its slot: the room it leaves at the left less the room at the right (the theme's margins are not even).
                        Rect bounds = frame.TransformToAncestor(tab).TransformBounds(new Rect(0, 0, frame.ActualWidth, frame.ActualHeight));
                        _frameShift = bounds.Left - (tab.ActualWidth - bounds.Right);
                        _frameLeft = bounds.Left;
                        _frameTop = bounds.Top;
                        _frameWidth = bounds.Width;
                        _frameHeight = bounds.Height;
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    _lastTabMeasure = (frame.ActualWidth, frame.ActualHeight, tab.ActualWidth);
                    return _lastTabMeasure;
                }
            }

            return _lastTabMeasure;
        }

        private static TaskButton FindTabButton(DependencyObject parent)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is TaskButton tab)
                {
                    return tab;
                }

                if (FindTabButton(child) is TaskButton found)
                {
                    return found;
                }
            }

            return null;
        }

        // The tab's button is this much wider than it is tall.
        private const double TabExtraWidth = 2;

        private int _squareChecks;

        // Once the tabs have their size, the button is measured and the width set again if it is not a square (a few times at most).
        private void CheckSquare()
        {
            AlignLauncherIcons();
            if (!IconOnly || _squareChecks > 6 || MeasureTabButton() is not (double w, double h, double _))
            {
                return;
            }

            if (Math.Abs(w - (h + TabExtraWidth)) > PixelStep * 0.6)
            {
                _squareChecks++;
                Dispatcher.BeginInvoke(new Action(SetTaskButtonWidth), DispatcherPriority.Background);
            }
            else
            {
                _squareChecks = 0;
            }
        }

        // The width of a launcher (a pinned program that is not running) in the Windows 11 style: its icon with a little room each side. It
        // has no frame, so being as wide as a tab only looked like a lot of empty room round it.
        public static DependencyProperty LauncherWidthProperty = DependencyProperty.Register(nameof(LauncherWidth), typeof(double), typeof(TaskList), new PropertyMetadata(22d));

        public double LauncherWidth
        {
            get { return (double)GetValue(LauncherWidthProperty); }
            set { SetValue(LauncherWidthProperty, value); }
        }

        // The width of the button of a launcher: the least that shows its icon whole (the button has a border and a padding of 3 either side of it).
        private const double LauncherButtonWidth = 24;

        // How far the frame of a tab is to the right in its slot (the room at its left less the room at its right), and the margin that puts a
        // launcher's icon in the middle of the free room between the frames of the tabs either side of it, as far from the one as the other.
        private double _frameShift;
        private double _frameLeft;
        private double _frameTop;
        private double _frameWidth;
        private double _frameHeight;

        public static DependencyProperty LauncherHeightProperty = DependencyProperty.Register(nameof(LauncherHeight), typeof(double), typeof(TaskList), new PropertyMetadata(double.NaN));

        public double LauncherHeight
        {
            get { return (double)GetValue(LauncherHeightProperty); }
            set { SetValue(LauncherHeightProperty, value); }
        }

        public static DependencyProperty LauncherIconMarginProperty = DependencyProperty.Register(nameof(LauncherIconMargin), typeof(Thickness), typeof(TaskList), new PropertyMetadata(new Thickness()));

        public Thickness LauncherIconMargin
        {
            get { return (Thickness)GetValue(LauncherIconMarginProperty); }
            set { SetValue(LauncherIconMarginProperty, value); }
        }

        // What the box of a pin is off the frame of a tab (as measured) and what its icon is off the place a tab's icon has in its frame, taken up
        // in the size and margins of the button and in the margin of the icon.
        private double _boxDx, _boxDy, _boxDw, _boxDh, _iconDx, _iconDy;

        // A tab of the Windows 11 style overlaps the next one by this much (a pixel) of the empty margin the theme gives its button: the gap
        // between the frames is that much less.
        public static DependencyProperty TabMarginProperty = DependencyProperty.Register(nameof(TabMargin), typeof(Thickness), typeof(TaskList), new PropertyMetadata(new Thickness()));

        public Thickness TabMargin
        {
            get { return (Thickness)GetValue(TabMarginProperty); }
            set { SetValue(TabMarginProperty, value); }
        }

        // While the tabs slide after one has closed, the list is wider than its tabs by this much each side (they are put at its middle, so
        // they are where they would be): the tab that is farthest out slides in from beyond the list's new edge, and was cut by it.
        private double _slideSlack;
        private double _pitch;
        private DispatcherTimer _slackTimer;

        private void BeginSlideSlack()
        {
            _slideSlack = _pitch + PixelStep;
            _slackTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _slackTimer.Stop();
            _slackTimer.Tick -= SlackTimer_Tick;
            _slackTimer.Tick += SlackTimer_Tick;
            _slackTimer.Start();
        }

        private void SlackTimer_Tick(object sender, EventArgs e)
        {
            _slackTimer.Stop();
            _slideSlack = 0;
            SetTaskButtonWidth();
        }

        private double _launcherShift;
        private double _launcherLift;
        private double _launcherTop;
        private double _launcherLeft;
        private double _launcherRight;
        private int _alignChecks;

        private void ApplyLauncherMargin()
        {
            // The side margins keep the slot as wide as it was; the top and bottom ones, equal and opposite, move the icon without changing the button's size.
            if (_frameWidth > 0 && _frameHeight > 0)
            {
                LauncherWidth = Math.Max(PixelStep, Math.Round(_frameWidth / PixelStep) * PixelStep + _boxDw);
                LauncherHeight = Math.Max(PixelStep, Math.Round(_frameHeight / PixelStep) * PixelStep + _boxDh);
                _launcherTop = Math.Round(_frameTop / PixelStep) * PixelStep + _boxDy;
                _launcherLeft = Math.Round(_frameLeft / PixelStep) * PixelStep + _boxDx;
                _launcherRight = _pitch - LauncherWidth - _launcherLeft;
                LauncherMargin = new Thickness(_launcherLeft, _launcherTop, _launcherRight, 0);
                // An image in the middle of its button moves by half its margin on the other side.
                LauncherIconMargin = new Thickness(Math.Max(0, 2 * _iconDx), Math.Max(0, 2 * _iconDy), Math.Max(0, -2 * _iconDx), Math.Max(0, -2 * _iconDy));
            }
            else
            {
                LauncherMargin = new Thickness(_launcherLeft, _launcherLift, _launcherRight, -_launcherLift);
                LauncherIconMargin = new Thickness();
            }
        }

        // The icon of a launcher is put at the height of the icon of a tab (the button of a launcher has its own offsets, so it sat lower): the
        // two are measured and the difference taken up in the margin, a few times at most.
        private bool _launchersAligned;
        private bool _alignPending;

        // After a correction the layout event comes several times before the new margins are in the layout: the next measure waits for it.
        private void HoldAlignment()
        {
            _alignPending = true;
            Dispatcher.BeginInvoke(new Action(() => _alignPending = false), DispatcherPriority.Loaded);
        }

        private void AlignLauncherIcons()
        {
            // Once, until the sizes are worked out again: a pressed launcher has its icon nudged by the theme (down and to the right), and
            // measuring that took the nudge for a place to correct, which moved all the pins up.
            if (!IconOnly || _alignChecks > 6 || _launchersAligned || _alignPending)
            {
                return;
            }

            ToolbarButton launcher = null;
            TaskButton tab = null;
            FrameworkElement launcherSlot = null;
            FrameworkElement tabSlot = null;
            for (int i = 0; i < TasksList.Items.Count && (launcher == null || tab == null); i++)
            {
                if (TasksList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)
                {
                    if (tab == null && FindTabButton(container) is TaskButton foundTab)
                    {
                        tab = foundTab;
                        tabSlot = container;
                    }

                    if (launcher == null && FindLauncherButton(container) is ToolbarButton foundLauncher)
                    {
                        launcher = foundLauncher;
                        launcherSlot = container;
                    }
                }
            }

            if (launcher == null || tab == null || !tab.IconOnlyImage.IsVisible || launcher.ToolbarIcon.ActualHeight <= 0 || tab.IconOnlyImage.ActualHeight <= 0 ||
                launcher.IsMouseOver || launcher.IsMouseCaptureWithin || Mouse.LeftButton == MouseButtonState.Pressed)
            {
                return;
            }

            try
            {
                // Each icon where it is in its own slot (the two are not in the same place in the row).
                Rect tabIcon = tab.IconOnlyImage.TransformToAncestor(tabSlot).TransformBounds(new Rect(0, 0, tab.IconOnlyImage.ActualWidth, tab.IconOnlyImage.ActualHeight));
                Rect launcherIcon = launcher.ToolbarIcon.TransformToAncestor(launcherSlot).TransformBounds(new Rect(0, 0, launcher.ToolbarIcon.ActualWidth, launcher.ToolbarIcon.ActualHeight));

                if (_frameWidth > 0 && _frameHeight > 0)
                {
                    // First the box (the drawn frame of the button, which has its own padding and borders) is made the frame of a tab, then
                    // the icon is put where a tab's icon is in its frame.
                    var box = launcher.LauncherButton.Template?.FindName("ButtonRightBottomBorder", launcher.LauncherButton) as FrameworkElement;
                    if (box == null || box.ActualWidth <= 0)
                    {
                        return;
                    }

                    Rect boxRect = box.TransformToAncestor(launcherSlot).TransformBounds(new Rect(0, 0, box.ActualWidth, box.ActualHeight));
                    double dl = _frameLeft - boxRect.Left, dt = _frameTop - boxRect.Top, dw = _frameWidth - boxRect.Width, dh = _frameHeight - boxRect.Height;
                    if (Math.Max(Math.Max(Math.Abs(dl), Math.Abs(dt)), Math.Max(Math.Abs(dw), Math.Abs(dh))) >= PixelStep * 0.75)
                    {
                        _alignChecks++;
                        HoldAlignment();
                        _boxDx += Math.Round(dl / PixelStep) * PixelStep;
                        _boxDy += Math.Round(dt / PixelStep) * PixelStep;
                        _boxDw += Math.Round(dw / PixelStep) * PixelStep;
                        _boxDh += Math.Round(dh / PixelStep) * PixelStep;
                        ApplyLauncherMargin();
                        return;
                    }

                    double wantX = (tabIcon.Left + tabIcon.Width / 2) - (_frameLeft + _frameWidth / 2) + (boxRect.Left + boxRect.Width / 2);
                    double wantY = (tabIcon.Top + tabIcon.Height / 2) - (_frameTop + _frameHeight / 2) + (boxRect.Top + boxRect.Height / 2);
                    double ix = wantX - (launcherIcon.Left + launcherIcon.Width / 2);
                    double iy = wantY - (launcherIcon.Top + launcherIcon.Height / 2);
                    if (Math.Abs(ix) >= PixelStep * 0.75 || Math.Abs(iy) >= PixelStep * 0.75)
                    {
                        _alignChecks++;
                        HoldAlignment();
                        _iconDx += Math.Round(ix / PixelStep) * PixelStep;
                        _iconDy += Math.Round(iy / PixelStep) * PixelStep;
                        ApplyLauncherMargin();
                        return;
                    }

                    _launchersAligned = true;
                    return;
                }

                double delta = (tabIcon.Top + tabIcon.Height / 2) - (launcherIcon.Top + launcherIcon.Height / 2);
                if (Math.Abs(delta) >= PixelStep * 0.75)
                {
                    _alignChecks++;
                    _launcherLift += Math.Round(delta / PixelStep) * PixelStep;
                    ApplyLauncherMargin();
                }
                else
                {
                    _launchersAligned = true;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static ToolbarButton FindLauncherButton(DependencyObject parent)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is ToolbarButton button)
                {
                    return button;
                }

                if (FindLauncherButton(child) is ToolbarButton found)
                {
                    return found;
                }
            }

            return null;
        }

        public static DependencyProperty LauncherMarginProperty = DependencyProperty.Register(nameof(LauncherMargin), typeof(Thickness), typeof(TaskList), new PropertyMetadata(new Thickness()));

        public Thickness LauncherMargin
        {
            get { return (Thickness)GetValue(LauncherMarginProperty); }
            set { SetValue(LauncherMarginProperty, value); }
        }

        // How many of the items are launchers (and not tabs).
        private int LauncherCount => _mergedItems?.Count(item => item is ManagedShell.ShellFolders.ShellFile) ?? 0;

        private void IconOnlyChanged()
        {
            _squareChecks = 0;
            TabMargin = new Thickness();
            TasksList.Margin = new Thickness();
            _slideSlack = 0;
            _alignChecks = 0;
            _launcherLift = 0;
            _boxDx = _boxDy = _boxDw = _boxDh = _iconDx = _iconDy = 0;
            TasksList.LayoutUpdated -= TasksList_LayoutUpdatedForSquare;
            if (IconOnly)
            {
                TasksList.LayoutUpdated += TasksList_LayoutUpdatedForSquare;
            }

            if (!IconOnly)
            {
                ClearValue(WidthProperty);
            }

            ScheduleGroupSync();
            ApplyPinnedMerge();
            SetTaskButtonWidth();
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
            TaskButtonTopMargin = buttonMargin.Top;
            TaskButtonBottomMargin = buttonMargin.Bottom;
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
                    // Tabs start out with the pinned programs' first, in the order of the pins (see TaskOpenOrderComparer).
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

                // Drag-to-rearrange: see TaskListDropHandler (and, in the Windows 11 style, TaskList.Pinned.cs, which replaces it).
                GongSolutions.Wpf.DragDrop.DragDrop.SetDropHandler(TasksList, new TaskListDropHandler(TasksList));
                ApplyPinnedMerge();

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

        // A theme has other margins, frames and icon places: what was measured of the tabs, and the corrections made to the pins and the icons
        // for it, are dropped and worked out again once the new theme is in.
        private void ThemeChanged()
        {
            _lastTabMeasure = null;
            _frameShift = _frameLeft = _frameTop = _frameWidth = _frameHeight = 0;
            _boxDx = _boxDy = _boxDw = _boxDh = _iconDx = _iconDy = 0;
            _launcherLift = 0;
            _launchersAligned = false;
            _alignChecks = 0;
            _squareChecks = 0;
            SetStyles();
            if (IconOnly)
            {
                // The button of a pin is the one of the new theme, whose size is the tag's, until it is measured against a tab again.
                LauncherHeight = double.NaN;
                Dispatcher.BeginInvoke(new Action(SetTaskButtonWidth), DispatcherPriority.Loaded);
            }
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.Theme))
            {
                Dispatcher.BeginInvoke(new Action(ThemeChanged), DispatcherPriority.Loaded);
            }
            else if (e.PropertyName == nameof(Settings.GroupTaskWindows))
            {
                ScheduleGroupSync();
            }
            else if (e.PropertyName == nameof(Settings.QuickLaunchOrder) || e.PropertyName == nameof(Settings.QuickLaunchPath))
            {
                if (_mergedItems != null)
                {
                    Dispatcher.BeginInvoke(new Action(RebuildMerged));
                }
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

            if (window != null && CombinesWindows && startupSortSource != null)
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

            if (!CombinesWindows || startupSortSource == null)
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

        private void TasksList_LayoutUpdatedForSquare(object sender, EventArgs e) => CheckSquare();

        private bool groupSyncPending;

        private void SourceWindows_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // A window of a program that already has a tab (the combined tab shows one window of an app) raises nothing on the view, so
            // nothing sorted it: it stayed where the list had added it, on the right, and the tab that took over from the window
            // that closed (Discord's loading window, then its main window) came up there instead of in its pin's place.
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
            {
                ScheduleStartupSort();
                Dispatcher.BeginInvoke(new Action(() => TaskOpenOrderComparer.SortSource(startupSortSource)), DispatcherPriority.Normal);
            }

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
                    if (!taskbarItems.Contains(window))
                    {
                        // A window that was held back (another window of its app had the tab) and now has to be shown: the view cannot take
                        // it by an edit (CommitEdit throws, and refreshing the whole view after that rebuilt every tab). Taken out of the
                        // list and put back at the same place, it comes to the view as a new item, which the filter now lets in.
                        int index = startupSortSource.IndexOf(window);
                        startupSortSource.RemoveAt(index);
                        startupSortSource.Insert(index, window);
                    }
                    else
                    {
                        editable.EditItem(window);
                        editable.CommitEdit();
                    }
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
            // The list of tabs and pins is made again when the list is loaded again (the Windows 11 style moves it, and changing the style
            // back and forth unloads and loads it): a merged list kept from before was not subscribed to the new view of the windows, and
            // showed a tab only after a pin changed - the program open had no tab, and closed it had.
            ReleasePinnedMerge();

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

        // The windows that have just come into the list. A tab of the Windows 11 style is also made again when it is moved (a pinned program
        // that starts goes from the end of the list to the place of its pin, and the tabs after it are made again): only a window that is
        // new comes in with the opening animation.
        private readonly Dictionary<ApplicationWindow, DateTime> _openedAt = new();

        public bool JustOpened(ApplicationWindow window) =>
            window != null && _openedAt.TryGetValue(window, out DateTime at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(2);

        private void GroupedWindows_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (IconOnly && e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove &&
                Settings.Instance.SlideTaskbarButtons && DateTime.UtcNow > TaskButton.SuppressSlideInUntilUtc)
            {
                BeginSlideSlack();
            }

            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                DateTime now = DateTime.UtcNow;
                foreach (ApplicationWindow old in _openedAt.Where(p => now - p.Value > TimeSpan.FromSeconds(10)).Select(p => p.Key).ToList())
                {
                    _openedAt.Remove(old);
                }

                foreach (ApplicationWindow added in e.NewItems.OfType<ApplicationWindow>())
                {
                    _openedAt[added] = now;
                }
            }

            SetTaskButtonWidth();

            // A tab that closes: the ones after it slide left into the gap (their containers are still where they were
            // until the layout pass that follows). A tab removed by a drag-and-drop slides on its own (TaskListDropHandler).
            // In the Windows 11 style the tabs are centered, so one that opens moves the others too (they slide as the list is made, and the
            // new one comes in as it appears); in the classic style the new tab grows from nothing and pushes the others (TaskButton.Animate).
            if ((e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove ||
                 (IconOnly && e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)) &&
                Settings.Instance.SlideTaskbarButtons && Host?.Orientation == Orientation.Horizontal &&
                DateTime.UtcNow > TaskButton.SuppressSlideInUntilUtc)
            {
                ItemSlideAnimation.Play(TasksList, ItemSlideAnimation.Capture(TasksList));
            }

            // Windows trickle in while RetroBar is starting, and new ones appear later: the pinned programs' go in the order of the pins,
            // the others on the right.
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
            {
                ScheduleStartupSort();

                // A window goes to its place at once (before the next frame is drawn), instead of showing up on the right and
                // jumping there when the debounce above ends.
                Dispatcher.BeginInvoke(new Action(() => TaskOpenOrderComparer.SortSource(startupSortSource)), DispatcherPriority.Normal);
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

            if (IconOnly && Settings.Instance.Edge != AppBarEdge.Left && Settings.Instance.Edge != AppBarEdge.Right)
            {
                // One row of small tabs: the height of the taskbar wide, or narrower if they do not all fit.
                ButtonsPerRow = Math.Max(1, taskCount);
                ExtraWidthCount = 0;
                // The button itself (inside the margins the theme gives it) is a square: as wide as it is tall. What a tab's button measures
                // is the truth (its style may have margins and borders of its own), so it is measured; before there is a tab, from the margins.
                double side = Math.Max(16, ActualHeight - TaskButtonTopMargin - TaskButtonBottomMargin);
                double overhead = TaskButtonLeftMargin + TaskButtonRightMargin;
                if (MeasureTabButton() is (double buttonWidth, double buttonHeight, double slotWidth) && buttonHeight > 8)
                {
                    side = buttonHeight;
                    overhead = slotWidth - buttonWidth;
                }

                double iconWidth = Math.Round((side + overhead + TabExtraWidth) / PixelStep) * PixelStep;
                // What a tab takes in the row: its width less the pixel it overlaps the next by.
                double pitch = iconWidth - PixelStep;
                double room = AvailableWidth > 0 ? AvailableWidth : pitch * ButtonsPerRow;
                int launchers = Math.Min(LauncherCount, ButtonsPerRow);
                if (pitch * ButtonsPerRow > room)
                {
                    pitch = Math.Max(PixelStep, Math.Floor(room / Math.Max(1, ButtonsPerRow) / PixelStep) * PixelStep);
                    iconWidth = pitch + PixelStep;
                }

                TabMargin = new Thickness(0, 0, -PixelStep, 0);
                _pitch = pitch;
                _launchersAligned = false;
                _alignChecks = 0;
                // What the list has beside its tabs is measured with the margin they had (the slack of the slide that is ending is still in the
                // list's size until the next layout), not the one they are given now.
                double slackBefore = TasksList.Margin.Left + TasksList.Margin.Right;
                TasksList.Margin = new Thickness(_slideSlack, 0, _slideSlack, 0);

                // A pinned program that is not running takes the room of a tab, so the row does not change size when it starts (and the pins
                // are no closer together than the tabs are). Its button keeps the width its icon needs, with margins to make up the slot and
                // to put the icon in the middle of the frame a tab has in the same place.
                double launcherWidth = pitch;
                if (_frameWidth > 0 && _frameHeight > 0)
                {
                    // Once there is a tab to measure, the button of a pin is as big as the frame of a tab, in the same place in its slot, so
                    // it can be hovered and pressed all over the frame a tab has (see ApplyLauncherMargin and AlignLauncherIcons).
                }
                else
                {
                    double launcherButton = Math.Round(LauncherButtonWidth / PixelStep) * PixelStep;
                    // (A tab's frame is in the middle of the width it has, which reaches a pixel past its place in the row.)
                    _launcherShift = Math.Round((_frameShift + PixelStep) / PixelStep) * PixelStep;
                    _launcherLeft = Math.Round((launcherWidth - launcherButton + _launcherShift) / 2 / PixelStep) * PixelStep;
                    _launcherRight = launcherWidth - launcherButton - _launcherLeft;
                    _launcherTop = 0;
                    LauncherWidth = launcherButton;
                    LauncherHeight = double.NaN;
                }

                ApplyLauncherMargin();

                ButtonWidth = iconWidth;
                // Over what they need by what the list has beside its tabs (the grip at its left): the row wraps, and its last tab is out of
                // sight, when it is short.
                double beside = ActualWidth > 0 && TasksList.ActualWidth > 0 ? Math.Max(0, ActualWidth - TasksList.ActualWidth - slackBefore) : 6;
                Width = pitch * ButtonsPerRow + beside + 2 * PixelStep + 2 * _slideSlack;
                SetScrollable(false);
                return;
            }

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