using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using RetroBar.Utilities;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RetroBar.Controls
{
    /// <summary>
    /// Interaction logic for ShowDesktopButton.xaml
    /// </summary>
    public partial class ShowDesktopButton : UserControl
    {
        private const int TOGGLE_DESKTOP = 407;
        // Process.GetCurrentProcess().MainWindowHandle (the original source of this value) is a
        // legacy WinForms-era concept that doesn't reliably track a specific WPF Window,
        // especially here where RetroBar can have several Taskbar windows (one per monitor) and
        // none of them is the OS's own notion of "the" main window - it was observed to resolve
        // to IntPtr.Zero here. Set from SetupButton instead, once this control's own Window is
        // guaranteed to exist and be interop-realized (have a real HWND).
        private IntPtr taskbarHandle;
        private bool isWindows81OrBetter = EnvironmentHelper.IsWindows81OrBetter;
        private bool isLoaded;
        private DelayedActivationHandler dragHandler;

        // Real Windows doesn't peek the instant the cursor touches the corner - a short hover
        // delay (SystemParameters.MouseHoverTime, the same value Windows already uses for its own
        // hover-triggered UI like tooltips) avoids it firing on a mouse just passing through.
        // DwmActivateLivePreview's own fade transition is rendered by DWM itself once called -
        // there's no parameter to control it from here, but a still cursor (rather than an
        // instant trigger-and-often-immediately-cancel on a passing mouse) gives it room to
        // actually play instead of being interrupted by a MouseLeave a moment later.
        private DispatcherTimer peekHoverTimer;
        private bool isPeeking;

        public static DependencyProperty TasksServiceProperty = DependencyProperty.Register(nameof(TasksService), typeof(TasksService), typeof(ShowDesktopButton), new PropertyMetadata(TasksChangedCallback));

        public TasksService TasksService
        {
            get { return (TasksService)GetValue(TasksServiceProperty); }
            set { SetValue(TasksServiceProperty, value); }
        }

        public ShowDesktopButton()
        {
            InitializeComponent();
        }

        private static void TasksChangedCallback(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is ShowDesktopButton button && e.OldValue == null && e.NewValue != null)
            {
                button.SetupButton();
            }
        }

        private void SetIconSize()
        {
            string deviceName = (Window.GetWindow(this) as Taskbar)?.Screen.DeviceName;

            // DISPLAY1 gets its own Medium variant when the active theme defines one (only
            // Windows XFM does, via desktopMe2k-md.png); DISPLAY2 always gets Small. Falls back
            // to the normal Small/Large pick (by DpiHelper.DpiScale on the primary monitor) for
            // every other monitor, or if the active theme has no Medium variant.
            string resourceKey;
            if (deviceName == "\\\\.\\DISPLAY1" && TryFindResource("ShowDesktopIconImageMedium") is not null)
            {
                resourceKey = "ShowDesktopIconImageMedium";
            }
            else if (deviceName == "\\\\.\\DISPLAY2")
            {
                resourceKey = "ShowDesktopIconImageSmall";
            }
            else
            {
                resourceKey = DpiHelper.DpiScale > 1 ? "ShowDesktopIconImageLarge" : "ShowDesktopIconImageSmall";
            }

            ShowDesktopIcon.Source = (System.Windows.Media.ImageSource)FindResource(resourceKey);
        }

        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            PeekAtDesktopItem.IsEnabled = true;
        }

        private void ToggleDesktop()
        {
            NativeMethods.SendMessage(WindowHelper.FindWindowsTray(IntPtr.Zero),
                (int)NativeMethods.WM.COMMAND, (IntPtr)TOGGLE_DESKTOP, IntPtr.Zero);
        }

        // DwmIsCompositionEnabled() used to gate this - historically meaningful for detecting a
        // Remote Desktop session (where live preview genuinely can't render), but composition has
        // been permanently on for every other case since Windows 8, and it was observed to
        // (incorrectly) report false on a plain local Windows 11 session too. Since
        // DwmActivateLivePreview itself is declared PreserveSig=true (it returns its raw HRESULT
        // as a plain uint that this code already ignores, rather than throwing on failure), there
        // was nothing that check was actually protecting against - safe to just always attempt
        // it.
        private void PeekAtDesktop(uint shouldPeek)
        {
            if (!Settings.Instance.PeekAtDesktop)
            {
                return;
            }

            isPeeking = shouldPeek != 0;

            if (isWindows81OrBetter)
            {
                NativeMethods.DwmActivateLivePreview(shouldPeek, taskbarHandle,
                    IntPtr.Zero, NativeMethods.AeroPeekType.Desktop, IntPtr.Zero);
            }
            else
            {
                NativeMethods.DwmActivateLivePreview(shouldPeek, taskbarHandle,
                    IntPtr.Zero, NativeMethods.AeroPeekType.Desktop);
            }
        }

        private void ShowDesktop_OnMouseEnter(object sender, RoutedEventArgs e)
        {
            peekHoverTimer.Stop();
            peekHoverTimer.Start();
        }

        private void ShowDesktop_OnMouseLeave(object sender, RoutedEventArgs e)
        {
            peekHoverTimer.Stop();

            if (isPeeking)
            {
                PeekAtDesktop(0);
            }
        }

        private void PeekHoverTimer_Tick(object sender, EventArgs e)
        {
            peekHoverTimer.Stop();
            PeekAtDesktop(1);
        }

        private void ShowDesktop_OnClick(object sender, RoutedEventArgs e)
        {
            // If the user activates a window other than the desktop, HandleWindowActivated will deselect the button.
            ToggleDesktop();
        }

        private void OpenDisplayPropertiesCpl()
        {
            ShellHelper.StartProcess("desk.cpl");
        }

        private void PropertiesItem_OnClick(object sender, RoutedEventArgs e)
        {
            OpenDisplayPropertiesCpl();
        }

        private void HandleWindowActivated(object sender, WindowEventArgs e)
        {
            if (ShowDesktop.IsChecked == true)
            {
                ShowDesktop.IsChecked = false;
            }
        }

        // The taskbar's own corner-hotspot width when only PeekAtDesktop (not ShowDesktopButton)
        // is on. An Adorner-hosted hit-test overlay was tried here to get a true zero-width
        // footprint, but MouseEnter/MouseLeave on RenderTransform'd Adorner content proved
        // unreliable - a missed MouseLeave left DWM's live preview (DwmActivateLivePreview(1,
        // ...)) stuck active, hiding every window behind the desktop. Plain, untransformed
        // hit-testing directly on ShowDesktop itself doesn't have that failure mode.
        private const double CompactHotspotWidth = 1;

        // Whether the full visible button (theme chrome, icon, tooltip, context menu - all still
        // themeable per-Style, untouched here) is wanted, or just a corner hotspot to peek from.
        // Opacity, not Visibility - Visibility="Collapsed"/"Hidden" would also drop it out of hit
        // testing, which is the one thing the hotspot mode still needs MouseEnter/MouseLeave for.
        private void UpdateCompactMode()
        {
            if (Settings.Instance.ShowDesktopButton)
            {
                ShowDesktop.ClearValue(WidthProperty);
                ShowDesktop.ClearValue(OpacityProperty);
            }
            else
            {
                ShowDesktop.Width = CompactHotspotWidth;
                ShowDesktop.Opacity = 0;
            }
        }

        private void Settings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.TaskbarScale))
            {
                SetIconSize();
            }
            else if (e.PropertyName == nameof(Settings.ShowDesktopButton))
            {
                UpdateCompactMode();
            }
        }

        private void SetupButton()
        {
            if (!isLoaded && TasksService != null)
            {
                taskbarHandle = new WindowInteropHelper(Window.GetWindow(this)).Handle;
                SetIconSize();
                UpdateCompactMode();
                TasksService.WindowActivated += HandleWindowActivated;

                peekHoverTimer = new DispatcherTimer { Interval = SystemParameters.MouseHoverTime };
                peekHoverTimer.Tick += PeekHoverTimer_Tick;

                Settings.Instance.PropertyChanged += Settings_PropertyChanged;

                dragHandler = new DelayedActivationHandler(() =>
                {
                    if (ShowDesktop.IsChecked == false)
                    {
                        ToggleDesktop();
                        ShowDesktop.IsChecked = true;
                    }
                });

                isLoaded = true;
            }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            SetupButton();
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (isLoaded && TasksService != null)
            {
                TasksService.WindowActivated -= HandleWindowActivated;
                Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
                dragHandler?.Dispose();
                peekHoverTimer?.Stop();
                if (peekHoverTimer != null)
                {
                    peekHoverTimer.Tick -= PeekHoverTimer_Tick;
                }
                isLoaded = false;
            }
        }

        private void ShowDesktop_DragEnter(object sender, DragEventArgs e)
        {
            dragHandler?.OnDragEnter(e);
        }

        private void ShowDesktop_DragLeave(object sender, DragEventArgs e)
        {
            dragHandler?.OnDragLeave();
        }
    }
}