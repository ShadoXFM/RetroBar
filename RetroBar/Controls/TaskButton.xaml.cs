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
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
            InitProgressBar();
            AppButton.Tag = GroupWindows;
            HitAreaExtension.SetEnabled(AppButton, true);
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

        public static readonly DependencyProperty IsGroupFlashingProperty = DependencyProperty.Register(
            nameof(IsGroupFlashing), typeof(bool), typeof(TaskButton), new PropertyMetadata(false));

        /// <summary>
        /// Whether one of the windows this tab stands for has asked for attention and has not been used since: the tab shows it, from the
        /// first flash until that window is activated. A tab that stands for several windows read only its first one, so a flash of another
        /// (a second window of Discord, say) was not shown at all; and a flash that ends on the shell's redraw (the blink's off) left
        /// nothing on the tab. While the window blinks, the tab blinks with it (what the shell reports); once it has stopped blinking, the
        /// tab stays highlighted.
        /// </summary>
        public bool IsGroupFlashing
        {
            get { return (bool)GetValue(IsGroupFlashingProperty); }
            private set { SetValue(IsGroupFlashingProperty, value); }
        }

        // The windows that have flashed and have not been activated since, for every taskbar, with when their state last changed: the blinks
        // of a flash come a half second apart, so a window that has not changed for longer has stopped blinking.
        private static readonly Dictionary<IntPtr, DateTime> NeedsAttention = new();
        private static readonly TimeSpan BlinkGap = TimeSpan.FromMilliseconds(1300);
        private System.Windows.Threading.DispatcherTimer _attentionTimer;

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
            UpdateProgressBar();
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
            if (e.PropertyName == "ProgressState" || e.PropertyName == "ProgressValue")
            {
                Dispatcher.BeginInvoke(new Action(UpdateProgressBar));
            }

            if (e.PropertyName == "State")
            {
                if (sender is ApplicationWindow changed)
                {
                    lock (NeedsAttention)
                    {
                        if (changed.State == ApplicationWindow.WindowState.Flashing)
                        {
                            NeedsAttention[changed.Handle] = DateTime.UtcNow;
                        }
                        else if (changed.State == ApplicationWindow.WindowState.Active)
                        {
                            NeedsAttention.Remove(changed.Handle);
                        }
                        else if (NeedsAttention.ContainsKey(changed.Handle))
                        {
                            // The off of a blink, or the end of the flash: told apart by whether another blink comes (see UpdateGroupActive).
                            NeedsAttention[changed.Handle] = DateTime.UtcNow;
                            _attentionTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = BlinkGap + TimeSpan.FromMilliseconds(100) };
                            _attentionTimer.Tick -= AttentionTimer_OnTick;
                            _attentionTimer.Tick += AttentionTimer_OnTick;
                            _attentionTimer.Stop();
                            _attentionTimer.Start();
                        }
                    }
                }

                UpdateGroupActive();
            }
        }

        private void AttentionTimer_OnTick(object sender, EventArgs e)
        {
            _attentionTimer.Stop();
            UpdateGroupActive();
        }

        private void UpdateGroupActive()
        {
            IsGroupActive = GroupWindows.Count > 1 && GroupWindows.Any(w => w.State == ApplicationWindow.WindowState.Active);
            lock (NeedsAttention)
            {
                DateTime now = DateTime.UtcNow;
                IsGroupFlashing = GroupWindows.Any(w => w.State == ApplicationWindow.WindowState.Flashing
                    || (w.State != ApplicationWindow.WindowState.Active && NeedsAttention.TryGetValue(w.Handle, out DateTime since) && now - since > BlinkGap));
            }
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
                SetMonitorTag(element, tag);
            }
        }

        private void SetMonitorTag(FrameworkElement element, string tag)
        {
            var multiBinding = new MultiBinding { Converter = TagConverter, ConverterParameter = tag };
            multiBinding.Bindings.Add(new Binding("State"));
            multiBinding.Bindings.Add(new Binding(nameof(IsContextMenuActive)) { Source = this });
            multiBinding.Bindings.Add(new Binding(nameof(IsGroupActive)) { Source = this });
            element.SetBinding(MonitorOffset.TagProperty, multiBinding);
        }

        private void SetStyle()
        {
            MultiBinding multiBinding = new MultiBinding();
            multiBinding.Converter = StyleConverter;

            multiBinding.Bindings.Add(new Binding { RelativeSource = RelativeSource.Self });
            multiBinding.Bindings.Add(new Binding("State"));
            multiBinding.Bindings.Add(new Binding(nameof(IsGroupActive)) { Source = this });
            multiBinding.Bindings.Add(new Binding(nameof(IsGroupFlashing)) { Source = this });

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

            InitIconOnly();
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

            ReopenPreviewAfterClose();
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
            ReleaseIconOnly();
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

            // The program's name and icon: a click starts another instance of it.
            ProgramMenuItem.Header = GetProgramDisplayName();
            // Made the way the preview's title icon is (the same icon, asked for the same way, the same 16 DIP image and per-monitor
            // adjustments - "PreviewTitleIcon" in the monitor file), so it is as sharp as that one.
            // For File Explorer that is the program's own icon - the one the tab shows when there are several windows - not the
            // folder icon in the window's title bar.
            var iconConverter = (System.Windows.Data.IMultiValueConverter)FindResource("taskIconConverter");
            ImageSource programIcon = (TaskIconConverter.IsFileExplorerWindow(Window)
                                          ? iconConverter.Convert(new object[] { Window, Window.Icon, 2 }, typeof(ImageSource), null, System.Globalization.CultureInfo.CurrentCulture)
                                          : iconConverter.Convert(new object[] { Window, Window.Icon }, typeof(ImageSource), "window", System.Globalization.CultureInfo.CurrentCulture)) as ImageSource
                                      ?? GetProgramIcon();
            var menuIcon = programIcon == null ? null : new Image { Source = programIcon, Width = 16, Height = 16, UseLayoutRounding = true, SnapsToDevicePixels = true };
            if (menuIcon != null)
            {
                RenderOptions.SetBitmapScalingMode(menuIcon, BitmapScalingMode.HighQuality);
                MonitorOffset.SetTag(menuIcon, "PreviewTitleIcon");
            }

            ProgramMenuItem.Icon = menuIcon;
            ProgramMenuItem.Visibility = GetProgramLaunchTarget() == null ? Visibility.Collapsed : Visibility.Visible;

            PinMenuItem.Visibility = Window.IsUWP || string.IsNullOrEmpty(Window.WinFileName) ? Visibility.Collapsed : Visibility.Visible;
            PinMenuItem.Header = FindResource(PinnedApps.FindLink(Window) != null ? "unpin_from_taskbar" : "pin_to_taskbar");
            CloseMenuItem.FontWeight = FontWeights.Bold;

            // The program's jump list and, for a browser, its pages, above the rest of the menu (see TaskButton.JumpList.cs).
            UpdateJumpListMenu();
        }

        private static readonly Dictionary<string, ImageSource> ProgramIcons = new(StringComparer.OrdinalIgnoreCase);

        // The icon of the program itself (its executable's), not the one the tab is showing at the moment (a folder's, a
        // document's, ...). A packaged app has no executable of its own to ask, so it keeps the tab's.
        private ImageSource GetProgramIcon()
        {
            string path = Window.IsUWP ? null : Window.WinFileName;
            if (string.IsNullOrEmpty(path))
            {
                return TaskIconImage.Source;
            }

            lock (ProgramIcons)
            {
                if (ProgramIcons.TryGetValue(path, out ImageSource cached))
                {
                    return cached;
                }
            }

            ImageSource icon = null;
            try
            {
                // The shell's small icon, the one the pinned programs have (a 32 pixel icon shrunk to the size of a tab's, as this was, lost a
                // row of its picture at the top): the size the icons of the classic style are.
                bool large = Settings.Instance.TaskbarScale > 1 || (Application.Current.FindResource("UseLargeIcons") as bool? ?? false);
                IntPtr handle = ManagedShell.Common.Helpers.IconHelper.GetIconByFilename(path, large ? ManagedShell.Common.Enums.IconSize.Large : ManagedShell.Common.Enums.IconSize.Small);
                if (handle != IntPtr.Zero)
                {
                    try
                    {
                        BitmapSource shell = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        shell.Freeze();
                        icon = shell;
                    }
                    finally
                    {
                        DestroyIcon(handle);
                    }
                }

                if (icon == null)
                {
                    using System.Drawing.Icon extracted = System.Drawing.Icon.ExtractAssociatedIcon(path);
                    if (extracted != null)
                    {
                        BitmapSource bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(extracted.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        bitmap.Freeze();
                        icon = bitmap;
                    }
                }
            }
            catch (Exception)
            {
            }

            icon ??= TaskIconImage.Source;

            lock (ProgramIcons)
            {
                ProgramIcons[path] = icon;
            }

            return icon;
        }

        private string GetProgramDisplayName()
        {
            return ProgramName.Get(Window) ??
                   (string.IsNullOrEmpty(Window.WinFileName) ? Window.Title : System.IO.Path.GetFileNameWithoutExtension(Window.WinFileName));
        }

        // What starting another instance runs: the executable, or for a packaged app its entry in the Apps folder.
        private string GetProgramLaunchTarget()
        {
            if (Window.IsUWP)
            {
                return string.IsNullOrEmpty(Window.AppUserModelID) ? null : @"shell:AppsFolder\" + Window.AppUserModelID;
            }

            return string.IsNullOrEmpty(Window.WinFileName) ? null : Window.WinFileName;
        }

        private ContextMenu _windowMenu;

        private void ProgramMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            string target = GetProgramLaunchTarget();
            if (target == null)
            {
                return;
            }

            try
            {
                var info = new ProcessStartInfo(target) { UseShellExecute = true };
                if (!Window.IsUWP)
                {
                    info.WorkingDirectory = System.IO.Path.GetDirectoryName(target);
                }

                Process.Start(info);
            }
            catch (Exception ex)
            {
                ManagedShell.Common.Logging.ShellLogger.Error("TaskButton: Unable to start another instance of the program", ex);
            }
        }

        private MenuItem WindowMenuItem(string resourceKey, string marlett, bool enabled, Action action)
        {
            var item = new MenuItem { Header = Application.Current.FindResource(resourceKey), IsEnabled = enabled, Style = MenuItemStyle.Style };
            if (marlett != null)
            {
                item.Icon = new TextBlock { FontFamily = new System.Windows.Media.FontFamily("Marlett"), Text = marlett, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }

            item.Click += (s, args) => action();
            return item;
        }

        /// <summary>Opens the menu of one window's own operations (restore, move, size, minimize, maximize, close) -
        /// what the right-click on a window's preview shows.</summary>
        private void ShowWindowMenu(ApplicationWindow window)
        {
            if (window == null)
            {
                return;
            }

            NativeMethods.WindowShowStyle wss = window.ShowStyle;
            bool canMaximize = (window.WindowStyles & (int)NativeMethods.WindowStyles.WS_MAXIMIZEBOX) != 0;
            bool restoreEnabled = wss != NativeMethods.WindowShowStyle.ShowNormal;
            bool maximizeEnabled = wss != NativeMethods.WindowShowStyle.ShowMaximized && canMaximize;

            MenuItem restore = WindowMenuItem("restore", "2", restoreEnabled, () => window.Restore());
            MenuItem close = WindowMenuItem("close", "r", true, () => window.Close());

            // The bold one is the window's default action: restore while it can be restored and maximized, close otherwise.
            (restoreEnabled && maximizeEnabled ? restore : close).FontWeight = FontWeights.Bold;

            // The preview stays up while the menu is open, and a click anywhere else closes both together (see PreviewPollTimer_Tick): WPF does not close the menu itself, so the two can be faded out in step.
            CloseWindowMenu();

            // No peek at the window while its menu is open (and a peek already on stops).
            StopPeek();

            var menu = new ContextMenu { PlacementTarget = AppButton, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint, StaysOpen = true };
            _windowMenu = menu;

            // Once the menu is gone (a click elsewhere, an item chosen, Escape) the preview goes too - unless the pointer is
            // on the preview or the tab, which deal with their own clicks.
            // Reacts to IsOpen going false, not to Closed: WPF fades a menu out before it raises Closed, so the preview would
            // start fading a moment after the menu already had.
            var isOpen = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ContextMenu.IsOpenProperty, typeof(ContextMenu));
            EventHandler onIsOpenChanged = null;
            onIsOpenChanged = (s, args) =>
            {
                if (menu.IsOpen)
                {
                    return;
                }

                isOpen.RemoveValueChanged(menu, onIsOpenChanged);
                _previewHoverSince = DateTime.UtcNow;
                _windowMenuFading = false;
                if (AppButton.ToolTip is ToolTip tip && tip.IsOpen && !_previewFadingOut && !IsPointerOverPopup(tip) && !IsPointerOverTab())
                {
                    ClosePreview();
                }
            };
            isOpen.AddValueChanged(menu, onIsOpenChanged);
            menu.SetBinding(System.Windows.Media.TextOptions.TextRenderingModeProperty, new System.Windows.Data.Binding("AllowFontSmoothingMenu")
            {
                Source = Settings.Instance,
                Converter = (System.Windows.Data.IValueConverter)FindResource("menuTextRenderingModeConverter"),
            });
            menu.Items.Add(restore);
            menu.Items.Add(WindowMenuItem("move", null, wss == NativeMethods.WindowShowStyle.ShowNormal, () => window.Move()));
            menu.Items.Add(WindowMenuItem("size", null, wss == NativeMethods.WindowShowStyle.ShowNormal && canMaximize, () => window.Size()));
            menu.Items.Add(WindowMenuItem("minimize", "0", wss != NativeMethods.WindowShowStyle.ShowMinimized && window.CanMinimize, () => window.Minimize()));
            menu.Items.Add(WindowMenuItem("maximize", "1", maximizeEnabled, () => window.Maximize()));
            menu.Items.Add(new Separator());
            menu.Items.Add(close);
            menu.IsOpen = true;
        }

        private void PinMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            if (Window == null)
            {
                return;
            }

            try
            {
                string link = PinnedApps.FindLink(Window);
                if (link != null)
                {
                    System.IO.File.Delete(link);
                }
                else
                {
                    PinnedApps.Pin(Window);
                }
            }
            catch (Exception ex)
            {
                ManagedShell.Common.Logging.ShellLogger.Error("TaskButton: Unable to pin or unpin the program", ex);
            }
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