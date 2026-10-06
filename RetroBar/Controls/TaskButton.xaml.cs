using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Animation;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using RetroBar.Converters;
using RetroBar.Utilities;

namespace RetroBar.Controls
{
    /// <summary>
    /// Interaction logic for TaskButton.xaml
    /// </summary>
    public partial class TaskButton : UserControl
    {
        public static DependencyProperty HostProperty = DependencyProperty.Register(nameof(Host), typeof(TaskList), typeof(TaskButton));

        public TaskList Host
        {
            get { return (TaskList)GetValue(HostProperty); }
            set { SetValue(HostProperty, value); }
        }

        private ApplicationWindow Window;
        private TaskButtonStyleConverter StyleConverter = new TaskButtonStyleConverter();
        private ApplicationWindow.WindowState PressedWindowState = ApplicationWindow.WindowState.Inactive;

        /// <summary>Buttons loaded before this moment don't play their slide-in animation (set by a
        /// tab drag-and-drop, which recreates the dragged tab's button).</summary>
        public static DateTime SuppressSlideInUntilUtc = DateTime.MinValue;

        private DelayedActivationHandler dragHandler;
        private bool _isLoaded;

        public TaskButton()
        {
            InitializeComponent();
            SetStyle();
            SetMonitorTags();
            InitHoverablePreview();
            AppButton.Tag = GroupWindows;
        }

        private static readonly TaskStateToMonitorTagConverter TagConverter = new TaskStateToMonitorTagConverter();

        /// <summary>
        /// The windows this tab stands for, in tab order: just its own window, or - with
        /// Settings.GroupTaskWindows on - every window of the same app (see TaskList.GetGroupWindows).
        /// Kept in AppButton.Tag so the preview, which is a separate popup, can show one thumbnail each.
        /// </summary>
        public ObservableCollection<ApplicationWindow> GroupWindows { get; } = new();

        public static readonly DependencyProperty IsGroupActiveProperty = DependencyProperty.Register(
            nameof(IsGroupActive), typeof(bool), typeof(TaskButton), new PropertyMetadata(false));

        /// <summary>Whether this tab stands for several windows and one of them is the active one.</summary>
        public bool IsGroupActive
        {
            get { return (bool)GetValue(IsGroupActiveProperty); }
            private set { SetValue(IsGroupActiveProperty, value); }
        }

        private TaskList _groupHost;

        private void RefreshGroup()
        {
            if (Window == null)
            {
                return;
            }

            List<ApplicationWindow> members = Host?.GetGroupWindows(Window) ?? new List<ApplicationWindow> { Window };

            foreach (ApplicationWindow gone in GroupWindows.Where(w => !members.Contains(w)).ToList())
            {
                gone.PropertyChanged -= GroupWindow_PropertyChanged;
                GroupWindows.Remove(gone);
            }

            for (int i = 0; i < members.Count; i++)
            {
                if (i < GroupWindows.Count && ReferenceEquals(GroupWindows[i], members[i]))
                {
                    continue;
                }

                int existing = GroupWindows.IndexOf(members[i]);
                if (existing >= 0)
                {
                    GroupWindows.Move(existing, i);
                }
                else
                {
                    members[i].PropertyChanged += GroupWindow_PropertyChanged;
                    GroupWindows.Insert(i, members[i]);
                }
            }

            UpdateGroupActive();
        }

        private void ReleaseGroup()
        {
            foreach (ApplicationWindow member in GroupWindows)
            {
                member.PropertyChanged -= GroupWindow_PropertyChanged;
            }

            GroupWindows.Clear();

            if (_groupHost != null)
            {
                _groupHost.GroupsChanged -= Host_GroupsChanged;
                _groupHost = null;
            }
        }

        private void Host_GroupsChanged(object sender, EventArgs e)
        {
            RefreshGroup();
        }

        private void GroupWindow_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "State")
            {
                UpdateGroupActive();
            }
        }

        private void UpdateGroupActive()
        {
            IsGroupActive = GroupWindows.Count > 1 && GroupWindows.Any(w => w.State == ApplicationWindow.WindowState.Active);
        }

        // With window previews off, a click on a tab that stands for several windows goes to the next one
        // (wrapping round), rather than minimizing the window it is showing; with them on, the click shows
        // the previews instead (see ShowGroupPreview).
        private void CycleGroup()
        {
            List<ApplicationWindow> members = GroupWindows.ToList();
            int active = members.FindIndex(w => w.State == ApplicationWindow.WindowState.Active);
            ApplicationWindow target = active < 0 ? Window : members[(active + 1) % members.Count];
            target?.BringToFront();
        }

        // Whether the tab is currently shown with the Active style because its context menu is open.
        // Updated by ContextMenu_OpenedOrClosed in the same step that re-evaluates the Style, rather
        // than bound straight to ContextMenu.IsOpen: IsOpen flips a moment before the Opened/Closed
        // events fire, so a direct binding changed the tags while the old template was still on
        // screen, leaving the icon and label misaligned for a few frames on open and on close.
        public static readonly DependencyProperty IsContextMenuActiveProperty = DependencyProperty.Register(
            nameof(IsContextMenuActive), typeof(bool), typeof(TaskButton), new PropertyMetadata(false));

        public bool IsContextMenuActive
        {
            get { return (bool)GetValue(IsContextMenuActiveProperty); }
            private set { SetValue(IsContextMenuActiveProperty, value); }
        }

        // The tab shows as Active while its context menu is open (see TaskButtonStyleConverter), so
        // its per-monitor tags have to follow the menu as well as the window's State - otherwise the
        // icon and label keep their inactive offsets inside the active template and jump up/left a
        // pixel whenever the menu opens.
        private void SetMonitorTags()
        {
            var tags = new (FrameworkElement Element, string Tag)[]
            {
                (AppButton, "TaskButton"),
                (TaskIconImage, "TaskIcon"),
                (TaskOverlayIconImage, "TaskOverlayIcon"),
                (TaskLabelText, "TaskLabel"),
            };

            foreach ((FrameworkElement element, string tag) in tags)
            {
                var multiBinding = new MultiBinding { Converter = TagConverter, ConverterParameter = tag };
                multiBinding.Bindings.Add(new Binding("State"));
                multiBinding.Bindings.Add(new Binding(nameof(IsContextMenuActive)) { Source = this });
                multiBinding.Bindings.Add(new Binding(nameof(IsGroupActive)) { Source = this });
                element.SetBinding(MonitorOffset.TagProperty, multiBinding);
            }
        }

        private void SetStyle()
        {
            MultiBinding multiBinding = new MultiBinding();
            multiBinding.Converter = StyleConverter;

            multiBinding.Bindings.Add(new Binding { RelativeSource = RelativeSource.Self });
            multiBinding.Bindings.Add(new Binding("State"));
            multiBinding.Bindings.Add(new Binding(nameof(IsGroupActive)) { Source = this });

            AppButton.SetBinding(StyleProperty, multiBinding);
        }

        private void ScrollIntoView()
        {
            if (Window == null)
            {
                return;
            }

            if (Window.State == ApplicationWindow.WindowState.Active)
            {
                BringIntoView();
            }
        }

        private void Animate()
        {
            var ease = new SineEase();
            ease.EasingMode = EasingMode.EaseInOut;

            DoubleAnimation animation = new DoubleAnimation();
            animation.From = 0;
            animation.To = Host?.ButtonWidth ?? ActualWidth;
            animation.Duration = new Duration(TimeSpan.FromMilliseconds(250));
            animation.FillBehavior = FillBehavior.Stop;
            animation.EasingFunction = ease;
            Storyboard.SetTarget(animation, this);
            Storyboard.SetTargetProperty(animation, new PropertyPath(WidthProperty));

            Storyboard storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private void TaskButton_OnLoaded(object sender, RoutedEventArgs e)
        {
            Window = DataContext as ApplicationWindow;

            Settings.Instance.PropertyChanged += Settings_PropertyChanged;

            dragHandler = new DelayedActivationHandler(() =>
            {
                Window?.BringToFront();
            });

            if (Window != null)
            {
                Window.GetButtonRect += Window_GetButtonRect;
                Window.PropertyChanged += Window_PropertyChanged;
            }

            _groupHost = Host;
            if (_groupHost != null)
            {
                _groupHost.GroupsChanged += Host_GroupsChanged;
            }

            RefreshGroup();

            // A button recreated because the user just dragged it somewhere else isn't a new window
            // appearing - growing it in from zero width would push every tab to its right along with it
            // (see TaskListDropHandler, which slides the tabs into place instead).
            if (Settings.Instance.SlideTaskbarButtons && Host?.Host?.Orientation == Orientation.Horizontal
                && DateTime.UtcNow > SuppressSlideInUntilUtc)
            {
                Animate();
            }

            _isLoaded = true;
        }

        private void Window_GetButtonRect(ref NativeMethods.ShortRect rect)
        {
            if (Host?.Host?.Screen.Primary != true && Settings.Instance.MultiMonMode != MultiMonOption.SameAsWindow)
            {
                // If there are multiple instances of a button, use the button on the primary display only
                return;
            }

            Point buttonTopLeft = PointToScreen(new Point(0, 0));
            Point buttonBottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
            rect.Top = (short)buttonTopLeft.Y;
            rect.Left = (short)buttonTopLeft.X;
            rect.Bottom = (short)buttonBottomRight.Y;
            rect.Right = (short)buttonBottomRight.X;
        }

        private void Window_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "State")
            {
                ScrollIntoView();
            }
        }

        private void TaskButton_OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded)
            {
                return;
            }

            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
            dragHandler?.Dispose();
            ReleaseGroup();

            if (Window != null)
            {
                Window.GetButtonRect -= Window_GetButtonRect;
                Window.PropertyChanged -= Window_PropertyChanged;
            }

            _isLoaded = false;
        }

        private void AppButton_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (Window == null)
            {
                return;
            }

            NativeMethods.WindowShowStyle wss = Window.ShowStyle;
            int ws = Window.WindowStyles;

            // disable window operations depending on current window state. originally tried implementing via bindings but found there is no notification we get regarding maximized state
            MaximizeMenuItem.IsEnabled = wss != NativeMethods.WindowShowStyle.ShowMaximized && (ws & (int)NativeMethods.WindowStyles.WS_MAXIMIZEBOX) != 0;
            MinimizeMenuItem.IsEnabled = wss != NativeMethods.WindowShowStyle.ShowMinimized && Window.CanMinimize;
            if (RestoreMenuItem.IsEnabled = wss != NativeMethods.WindowShowStyle.ShowNormal)
            {
                CloseMenuItem.FontWeight = FontWeights.Normal;
                RestoreMenuItem.FontWeight = FontWeights.Bold;
            }
            if (!RestoreMenuItem.IsEnabled || RestoreMenuItem.IsEnabled && !MaximizeMenuItem.IsEnabled)
            {
                CloseMenuItem.FontWeight = FontWeights.Bold;
                RestoreMenuItem.FontWeight = FontWeights.Normal;
            }
            MoveMenuItem.IsEnabled = wss == NativeMethods.WindowShowStyle.ShowNormal;
            SizeMenuItem.IsEnabled = wss == NativeMethods.WindowShowStyle.ShowNormal && (ws & (int)NativeMethods.WindowStyles.WS_MAXIMIZEBOX) != 0;
        }

        private void CloseMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Window?.Close();
        }

        private void EndTaskMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            if (Window != null)
            {
                ForceEndTask();
            }
        }

        private void ForceEndTask()
        {
            try
            {
                if (Window.ProcId.HasValue && Window.ProcId.Value != 0)
                {
                    // Don't kill RetroBar itself - just close the window gracefully
                    int currentProcId = Process.GetCurrentProcess().Id;
                    if (Window.ProcId.Value == currentProcId)
                    {
                        Window?.Close();
                        return;
                    }

                    Process process = Process.GetProcessById((int)Window.ProcId.Value);
                    process.Kill();
                }
            }
            catch (Exception)
            {
                Window?.Close();
            }
        }

        private void RestoreMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Window?.Restore();
        }

        private void MoveMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Window?.Move();
        }

        private void SizeMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Window?.Size();
        }

        private void MinimizeMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Window?.Minimize();
        }

        private void MaximizeMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            Window?.Maximize();
        }

        // Whether the pointer is on the strip under the tab (see TaskList's bottom edge handling) - counts
        // as being on the tab for its preview.
        private bool _edgeHover;

        public bool IsTabHovered => AppButton.IsMouseOver || _edgeHover;

        // The tab's hover effect is a trigger on IsMouseOver, which only real pointer input can set, so with the
        // pointer on the strip under the tab the effect is applied to the template's own hover fill directly
        // (same opacity the template uses for IsMouseOver) and removed again afterwards.
        private const double EdgeHoverFillOpacity = 0.08;

        private void ApplyEdgeHoverFill()
        {
            if (AppButton.Template?.FindName("HoverFill", AppButton) is not UIElement fill)
            {
                return;
            }

            if (_edgeHover && !AppButton.IsMouseOver)
            {
                fill.Opacity = EdgeHoverFillOpacity;
            }
            else
            {
                fill.ClearValue(UIElement.OpacityProperty);
            }
        }

        // The tab's template is replaced when it turns active/inactive (a click on it, say), which drops the
        // fill set above - put it back for as long as the pointer is still on the strip.
        internal void RefreshEdgeHover()
        {
            ApplyEdgeHoverFill();
        }

        internal void SetEdgeHover(bool hovered)
        {
            _edgeHover = hovered;
            ApplyEdgeHoverFill();

            if (hovered)
            {
                StartPreviewShowTimer();
            }
            else if (!AppButton.IsMouseOver)
            {
                StopPreviewShowTimer();
            }
        }

        internal void ClickFromEdge()
        {
            PressedWindowState = Window?.State ?? ApplicationWindow.WindowState.Inactive;
            AppButton_OnClick(AppButton, new RoutedEventArgs());
        }

        internal void OpenMenuFromEdge()
        {
            if (AppButton.ContextMenu == null)
            {
                return;
            }

            AppButton_OnContextMenuOpening(AppButton, null);
            ClosePreview();
            AppButton.ContextMenu.PlacementTarget = AppButton;
            AppButton.ContextMenu.IsOpen = true;
        }

        private void AppButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (GroupWindows.Count > 1)
            {
                if (ShowsGroupPreviewOnClick)
                {
                    ShowGroupPreview();
                }
                else
                {
                    CycleGroup();
                }

                return;
            }

            if (PressedWindowState == ApplicationWindow.WindowState.Active && Window?.CanMinimize == true)
            {
                Window?.Minimize();
            }
            else
            {
                Window?.BringToFront();
            }
        }

        private void AppButton_OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Settings.Instance.TaskWheelAction == TaskWheelActionOption.DoNothing || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                return;
            }

            if (e.Delta > 0)
            {
                // SetForegroundWindow can fail if the process doesn't have foreground rights
                // We need to trigger a keyboard event for Windows to grant us this ability
                // The key code of 0xE8 is unassigned, so no apps should handle it
                var input = new NativeMethods.INPUT[]
                {
                    new() { type = NativeMethods.INPUT_KEYBOARD, mkhi = new() { ki = new() { wVk = 0xE8 } } },
                    new() { type = NativeMethods.INPUT_KEYBOARD, mkhi = new() { ki = new() { wVk = 0xE8, dwFlags = NativeMethods.KEYEVENTF_KEYUP } } }
                };
                NativeMethods.SendInput((uint)input.Length, input, Marshal.SizeOf<NativeMethods.INPUT>());
                Window?.BringToFront();
            }
            else if (Window?.CanMinimize == true)
            {
                Window?.Minimize();
            }
            e.Handled = true;
        }

        private void AppButton_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                PressedWindowState = Window.State;
            }
        }

        private void AppButton_OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                if (Window == null || Settings.Instance.TaskMiddleClickAction == TaskMiddleClickOption.DoNothing)
                {
                    return;
                }
                if (Settings.Instance.TaskMiddleClickAction == TaskMiddleClickOption.CloseTask !=
                    (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)))
                {
                    Window?.Close();
                }
                else
                {
                    ShellHelper.StartProcess(Window.IsUWP ? "appx:" + Window.AppUserModelID : Window.WinFileName);
                }
            }
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.Theme))
            {
                SetStyle();
            }
        }

        private void AppButton_OnDragEnter(object sender, DragEventArgs e)
        {
            dragHandler?.OnDragEnter(e);
        }

        private void AppButton_OnDragLeave(object sender, DragEventArgs e)
        {
            dragHandler?.OnDragLeave();
        }

        private void ContextMenu_OpenedOrClosed(object sender, RoutedEventArgs e)
        {
            // Tags and Style together, in one dispatcher turn, so no frame is rendered with the
            // active template and the inactive offsets (or the reverse).
            IsContextMenuActive = AppButton.ContextMenu?.IsOpen == true;
            BindingOperations.GetMultiBindingExpression(AppButton, StyleProperty).UpdateTarget();
        }
    }
}