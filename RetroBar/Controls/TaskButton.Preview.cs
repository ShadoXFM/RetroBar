using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using RetroBar.Utilities;

namespace RetroBar.Controls
{
    /// <summary>
    /// Makes the thumbnail preview behave like the regular Windows taskbar's:
    /// - it stays open while the pointer is on the tab *or* on the preview itself, instead of
    ///   vanishing the moment the pointer leaves the tab,
    /// - clicking it activates the window, and its close button closes the window,
    /// - resting the pointer on it peeks at the window (Aero Peek: every other window goes glass).
    ///
    /// The preview is the tab's ToolTip, and WPF's tooltip service closes a tooltip as soon as the
    /// pointer leaves its owner - there's no way to cancel that - so while thumbnails are on, the
    /// service is switched off for this tab and the same ToolTip is opened and closed here by hand:
    /// opened after a short hover delay, closed once the pointer has been off both the tab and the
    /// preview for a short grace period (so it can cross the gap between them).
    ///
    /// WPF also never routes mouse input to a tooltip (its window is click-through and the element
    /// stays non-hit-testable), so the preview's own mouse handling - close button hover/click,
    /// click-to-activate - is done on the raw window messages of its popup window instead.
    /// </summary>
    public partial class TaskButton
    {
        // A bit shorter than the tooltip service's default (500ms), but long enough that sweeping the
        // pointer along the taskbar doesn't flash a preview on every tab it crosses.
        private static readonly TimeSpan PreviewShowDelay = TimeSpan.FromMilliseconds(400);

        // How long the pointer may be off both the tab and the preview before the preview closes -
        // enough to cross the gap between them.
        private static readonly TimeSpan PreviewCloseGrace = TimeSpan.FromMilliseconds(250);

        // How long the pointer has to rest on the preview before the window is peeked at, so just
        // passing over it on the way somewhere else doesn't flash every other window to glass.
        private static readonly TimeSpan PeekDelay = TimeSpan.FromMilliseconds(400);

        private static readonly TimeSpan PreviewPollInterval = TimeSpan.FromMilliseconds(50);

        // Fade durations (the thumbnail inside fades along with the frame - see TaskThumbnail.GetPopupOpacity).
        private static readonly TimeSpan PreviewFadeIn = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan PreviewFadeOut = TimeSpan.FromMilliseconds(120);

        private bool _previewFadingOut;

        private DispatcherTimer _previewShowTimer;
        private DispatcherTimer _previewPollTimer;
        private DateTime? _previewOutsideSince;
        private DateTime? _previewHoverSince;
        private bool _isPeekingAtWindow;
        private HwndSource _previewSource;
        private bool _previewCloseButtonPressed;

        private void InitHoverablePreview()
        {
            Loaded += (s, e) =>
            {
                Settings.Instance.PropertyChanged += PreviewSettings_PropertyChanged;
                UpdatePreviewToolTipService();
            };

            Unloaded += (s, e) =>
            {
                Settings.Instance.PropertyChanged -= PreviewSettings_PropertyChanged;
                ClosePreview(false);
            };

            AppButton.MouseEnter += (s, e) => StartPreviewShowTimer();
            AppButton.MouseLeave += (s, e) => _previewShowTimer?.Stop();

            // Clicking the tab, or opening its context menu, dismisses the preview (the tooltip
            // service used to do this itself).
            AppButton.PreviewMouseDown += (s, e) => ClosePreview();
            AppButton.ContextMenuOpening += (s, e) => ClosePreview();
        }

        private void PreviewSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.ShowTaskThumbnails))
            {
                UpdatePreviewToolTipService();
            }
        }

        // With thumbnails on, this class opens/closes the tooltip itself, so the tooltip service must
        // not also do it; with them off the plain title tooltip is left entirely to the service.
        private void UpdatePreviewToolTipService()
        {
            bool previews = Settings.Instance.ShowTaskThumbnails;
            ToolTipService.SetIsEnabled(AppButton, !previews);

            if (!previews)
            {
                ClosePreview(false);
            }
        }

        private void StartPreviewShowTimer()
        {
            if (!Settings.Instance.ShowTaskThumbnails || AppButton.ToolTip is not ToolTip tip || (tip.IsOpen && !_previewFadingOut))
            {
                return;
            }

            if (_previewShowTimer == null)
            {
                _previewShowTimer = new DispatcherTimer();
                _previewShowTimer.Tick += (s, e) =>
                {
                    _previewShowTimer.Stop();
                    OpenPreview();
                };
            }

            _previewShowTimer.Interval = PreviewShowDelay;
            _previewShowTimer.Start();
        }

        private void OpenPreview()
        {
            if (!AppButton.IsMouseOver || AppButton.ToolTip is not ToolTip tip || AppButton.ContextMenu?.IsOpen == true)
            {
                return;
            }

            tip.PlacementTarget = AppButton;

            // The popup's own built-in fade is off: it only animates the WPF part of the preview (frame,
            // title, close button) while the thumbnail inside is drawn by DWM and would pop in at full
            // strength. Instead the whole tooltip's Opacity is animated here, and TaskThumbnail passes that
            // opacity on to DWM every frame, so frame and thumbnail fade together.
            if (tip.Parent is System.Windows.Controls.Primitives.Popup existingPopup)
            {
                existingPopup.PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.None;
            }

            // Start (or resume, if it was still fading out) from the current opacity.
            double startOpacity = tip.IsOpen ? tip.Opacity : 0;
            _previewFadingOut = false;
            tip.BeginAnimation(UIElement.OpacityProperty, null);
            tip.Opacity = startOpacity;
            tip.IsOpen = true;

            if (tip.Parent is System.Windows.Controls.Primitives.Popup openedPopup)
            {
                openedPopup.PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.None;
            }

            tip.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(startOpacity, 1, PreviewFadeIn)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
            _previewOutsideSince = null;
            _previewHoverSince = null;

            // Hooked up once the preview has actually loaded: its window doesn't exist before that,
            // and forcing the template to be built any earlier (ApplyTemplate before the tooltip is
            // open) creates the thumbnail before it has its data context, so it shows nothing.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (tip.IsOpen)
                {
                    HookPreviewWindow(tip);
                }
            }));

            if (_previewPollTimer == null)
            {
                _previewPollTimer = new DispatcherTimer { Interval = PreviewPollInterval };
                _previewPollTimer.Tick += PreviewPollTimer_Tick;
            }

            _previewPollTimer.Start();
        }

        private void PreviewPollTimer_Tick(object sender, EventArgs e)
        {
            if (AppButton.ToolTip is not ToolTip tip || !tip.IsOpen)
            {
                StopPeek();
                _previewPollTimer?.Stop();
                return;
            }

            bool overPreview = IsPointerOverPopup(tip);

            // Hot/pressed follow the pointer over the close button; it's always visible while the
            // preview is open.
            SetPreviewCloseButtonState(tip, overPreview ? (_previewCloseButtonPressed ? CloseButtonState.Pressed : GetCloseButtonHover(tip)) : CloseButtonState.Normal);

            // Peek at the window once the pointer has rested on the preview (not just the tab), and
            // stop as soon as it leaves the preview again.
            if (overPreview)
            {
                _previewHoverSince ??= DateTime.UtcNow;

                if (!_isPeekingAtWindow && DateTime.UtcNow - _previewHoverSince.Value >= PeekDelay)
                {
                    StartPeek(tip);
                }
            }
            else
            {
                _previewHoverSince = null;
                _previewCloseButtonPressed = false;
                StopPeek();
            }

            if (AppButton.IsMouseOver || overPreview)
            {
                _previewOutsideSince = null;
                return;
            }

            _previewOutsideSince ??= DateTime.UtcNow;

            if (DateTime.UtcNow - _previewOutsideSince.Value >= PreviewCloseGrace)
            {
                ClosePreview();
            }
        }

        // animate: fade out (the normal case); false closes at once (tab unloaded, thumbnails turned off).
        private void ClosePreview(bool animate = true)
        {
            _previewShowTimer?.Stop();
            _previewPollTimer?.Stop();
            _previewOutsideSince = null;
            _previewHoverSince = null;
            _previewCloseButtonPressed = false;
            StopPeek();
            UnhookPreviewWindow();

            if (AppButton.ToolTip is ToolTip tip && tip.IsOpen)
            {
                FadeOutPreview(tip, animate);
            }
        }

        private void FadeOutPreview(ToolTip tip, bool animate)
        {
            if (!animate || !AppButton.IsLoaded)
            {
                _previewFadingOut = false;
                tip.BeginAnimation(UIElement.OpacityProperty, null);
                tip.IsOpen = false;
                return;
            }

            if (_previewFadingOut)
            {
                return;
            }

            _previewFadingOut = true;

            var fade = new DoubleAnimation(tip.Opacity, 0, PreviewFadeOut)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            fade.Completed += (sender, args) =>
            {
                // Only if it wasn't reopened (or closed some other way) while fading.
                if (_previewFadingOut)
                {
                    _previewFadingOut = false;
                    tip.BeginAnimation(UIElement.OpacityProperty, null);
                    tip.IsOpen = false;
                }
            };
            tip.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        #region Preview window mouse handling

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;

        private enum CloseButtonState
        {
            Normal,
            Hot,
            Pressed,
        }

        // WPF creates a tooltip's window click-through (WS_EX_TRANSPARENT), so mouse input falls
        // straight through it to whatever is underneath. Clear the flag so the window receives mouse
        // messages, and listen to them directly.
        private void HookPreviewWindow(ToolTip tip)
        {
            if (PresentationSource.FromVisual(tip) is not HwndSource source)
            {
                return;
            }

            int style = GetWindowLong32(source.Handle, GWL_EXSTYLE);
            if ((style & WS_EX_TRANSPARENT) != 0)
            {
                SetWindowLong32(source.Handle, GWL_EXSTYLE, style & ~WS_EX_TRANSPARENT);
            }

            // A peek hides every window except the one being peeked at - including this popup, unless it
            // opts out, which this does.
            int exclude = 1;
            DwmSetWindowAttribute(source.Handle, DWMWA_EXCLUDED_FROM_PEEK, ref exclude, sizeof(int));

            ScaleCloseGlyphToPixels(tip, source);

            if (!ReferenceEquals(_previewSource, source))
            {
                UnhookPreviewWindow();
                _previewSource = source;
                _previewSource.AddHook(PreviewWndProc);
            }
        }

        private void UnhookPreviewWindow()
        {
            _previewSource?.RemoveHook(PreviewWndProc);
            _previewSource = null;
        }

        private IntPtr PreviewWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg is not (WM_MOUSEMOVE or WM_LBUTTONDOWN or WM_LBUTTONUP) || AppButton.ToolTip is not ToolTip tip)
            {
                return IntPtr.Zero;
            }

            // lParam is the pointer position in the window's client pixels.
            int x = (short)((long)lParam & 0xFFFF);
            int y = (short)(((long)lParam >> 16) & 0xFFFF);
            bool onCloseButton = IsOnCloseButton(tip, x, y);

            switch (msg)
            {
                case WM_MOUSEMOVE:
                    SetPreviewCloseButtonState(tip, _previewCloseButtonPressed ? CloseButtonState.Pressed : (onCloseButton ? CloseButtonState.Hot : CloseButtonState.Normal));
                    break;

                case WM_LBUTTONDOWN:
                    _previewCloseButtonPressed = onCloseButton;
                    SetPreviewCloseButtonState(tip, onCloseButton ? CloseButtonState.Pressed : CloseButtonState.Normal);
                    break;

                case WM_LBUTTONUP:
                    bool wasPressedOnClose = _previewCloseButtonPressed;
                    _previewCloseButtonPressed = false;
                    ApplicationWindow window = Window;

                    if (wasPressedOnClose && !onCloseButton)
                    {
                        // Pressed on the close button, released elsewhere: cancelled, like any button.
                        SetPreviewCloseButtonState(tip, CloseButtonState.Normal);
                        break;
                    }

                    // Clicking the preview anywhere but the close button activates the window; the
                    // close button closes it. Either way the preview is done.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ClosePreview();

                        if (wasPressedOnClose)
                        {
                            window?.Close();
                        }
                        else
                        {
                            window?.BringToFront();
                        }
                    }));
                    break;
            }

            handled = true;
            return IntPtr.Zero;
        }

        // The close glyph's geometry is authored with one cell per unit; dividing by the monitor's DPI
        // scale makes each cell exactly one device pixel, so the X is the same crisp shape at 100%,
        // 125%, and so on.
        private static void ScaleCloseGlyphToPixels(ToolTip tip, HwndSource source)
        {
            if (GetCloseButton(tip) is not Button button ||
                button.Template?.FindName("Glyph", button) is not System.Windows.Shapes.Path glyph ||
                glyph.Data == null || glyph.Data.Transform is ScaleTransform)
            {
                return;
            }

            Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
            Geometry data = glyph.Data.CloneCurrentValue();
            data.Transform = new ScaleTransform(fromDevice.M11, fromDevice.M22);
            glyph.Data = data;
        }

        private static Button GetCloseButton(ToolTip tip)
        {
            return tip.Template?.FindName("PreviewCloseButton", tip) as Button;
        }

        // x/y are client pixels of the preview's window.
        private static bool IsOnCloseButton(ToolTip tip, int x, int y)
        {
            Button button = GetCloseButton(tip);
            if (button == null || button.ActualWidth <= 0 ||
                PresentationSource.FromVisual(tip) is not HwndSource source || source.RootVisual is not Visual root)
            {
                return false;
            }

            try
            {
                Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
                Point pointer = fromDevice.Transform(new Point(x, y));
                Rect bounds = button.TransformToAncestor(root).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                return bounds.Contains(pointer);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private CloseButtonState GetCloseButtonHover(ToolTip tip)
        {
            if (PresentationSource.FromVisual(tip) is HwndSource source && GetCursorPos(out POINT cursor) &&
                GetWindowRect(source.Handle, out RECT rect))
            {
                return IsOnCloseButton(tip, cursor.X - rect.Left, cursor.Y - rect.Top) ? CloseButtonState.Hot : CloseButtonState.Normal;
            }

            return CloseButtonState.Normal;
        }

        // The button's look follows its Tag ("hot" / "pressed", see TaskPreviewCloseButton in
        // System.xaml).
        private static void SetPreviewCloseButtonState(ToolTip tip, CloseButtonState state)
        {
            Button button = GetCloseButton(tip);
            if (button == null)
            {
                return;
            }

            button.Tag = state switch
            {
                CloseButtonState.Hot => "hot",
                CloseButtonState.Pressed => "pressed",
                _ => null,
            };
        }

        #endregion

        #region Peek

        // Aero Peek for one window: DWM hides every other top-level window (leaving glass outlines)
        // except the peeked one and the window passed to stay on top, which here is the preview
        // popup itself so it stays on screen. (Argument order is DwmActivateLivePreview's own:
        // the keep-on-top window comes before the peek target - the desktop peek in
        // ShowDesktopButton passes the taskbar in that same slot.)
        private void StartPeek(ToolTip tip)
        {
            if (Window == null || Window.Handle == IntPtr.Zero ||
                PresentationSource.FromVisual(tip) is not HwndSource popup)
            {
                return;
            }

            _isPeekingAtWindow = true;
            ActivateLivePreview(1, Window.Handle);
        }

        private void StopPeek()
        {
            if (!_isPeekingAtWindow)
            {
                return;
            }

            _isPeekingAtWindow = false;

            if (Window != null)
            {
                ActivateLivePreview(0, Window.Handle);
            }
        }

        // Argument order, found empirically: the window to peek at comes first (DWM keeps that one
        // visible and hides the rest), the taskbar - the caller - second. The desktop peek in
        // ShowDesktopButton passes the taskbar first and nothing second, since its "target" is the
        // desktop itself.
        private void ActivateLivePreview(uint activate, IntPtr peekTarget)
        {
            IntPtr taskbar = new WindowInteropHelper(System.Windows.Window.GetWindow(this)).Handle;

            if (EnvironmentHelper.IsWindows81OrBetter)
            {
                NativeMethods.DwmActivateLivePreview(activate, peekTarget, taskbar, NativeMethods.AeroPeekType.Window, IntPtr.Zero);
            }
            else
            {
                NativeMethods.DwmActivateLivePreview(activate, peekTarget, taskbar, NativeMethods.AeroPeekType.Window);
            }
        }

        #endregion

        // Compared against the popup window's screen rectangle rather than ToolTip.IsMouseOver: the
        // thumbnail itself is drawn by DWM over the window, and WPF's own hit-testing doesn't see the
        // pointer over parts of the popup that have no background brush.
        private static bool IsPointerOverPopup(ToolTip tip)
        {
            if (PresentationSource.FromVisual(tip) is not HwndSource source ||
                !GetCursorPos(out POINT cursor) ||
                !GetWindowRect(source.Handle, out RECT rect))
            {
                return false;
            }

            return cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const int DWMWA_EXCLUDED_FROM_PEEK = 12;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    }
}
