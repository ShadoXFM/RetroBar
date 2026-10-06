using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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

        // How long the pointer may be off both the tab and the preview before the preview closes (it
        // also has to be long enough to cross the gap between them).
        private static readonly TimeSpan PreviewCloseGrace = TimeSpan.FromMilliseconds(1000);

        // The tab whose preview is open. With the grace period this long, a preview can still be on its
        // way out when the pointer reaches the next tab, so opening one closes the other.
        private static TaskButton _previewOwner;

        // How long the pointer has to rest on the preview before the window is peeked at, so just
        // passing over it on the way somewhere else doesn't flash every other window to glass.
        private static readonly TimeSpan PeekDelay = TimeSpan.FromMilliseconds(400);

        private static readonly TimeSpan PreviewPollInterval = TimeSpan.FromMilliseconds(50);

        // Fade durations (the thumbnail inside fades along with the frame - see TaskThumbnail.GetPopupOpacity).
        private static readonly TimeSpan PreviewFadeIn = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan PreviewFadeOut = TimeSpan.FromMilliseconds(120);

        // The preview also slides in from the taskbar's edge (and back out), by this much.
        private const double PreviewSlideDistance = 12;
        private static readonly TimeSpan PreviewSlideIn = TimeSpan.FromMilliseconds(180);

        private bool _previewFadingOut;

        // The ToolTip offset property that slides the preview (along the axis facing away from the
        // taskbar - its resting offset there is 0, see the tooltip offset converters), and where the slide
        // starts from.
        private static (DependencyProperty Property, double Start) PreviewSlideAxis()
        {
            return Settings.Instance.Edge switch
            {
                ManagedShell.AppBar.AppBarEdge.Left => (System.Windows.Controls.ToolTip.HorizontalOffsetProperty, -PreviewSlideDistance),
                ManagedShell.AppBar.AppBarEdge.Right => (System.Windows.Controls.ToolTip.HorizontalOffsetProperty, PreviewSlideDistance),
                ManagedShell.AppBar.AppBarEdge.Top => (System.Windows.Controls.ToolTip.VerticalOffsetProperty, -PreviewSlideDistance),
                _ => (System.Windows.Controls.ToolTip.VerticalOffsetProperty, PreviewSlideDistance),
            };
        }

        private static void ClearPreviewSlide(ToolTip tip)
        {
            tip.BeginAnimation(System.Windows.Controls.ToolTip.HorizontalOffsetProperty, null);
            tip.BeginAnimation(System.Windows.Controls.ToolTip.VerticalOffsetProperty, null);
        }

        private DispatcherTimer _previewShowTimer;
        private DispatcherTimer _previewPollTimer;
        private DateTime? _previewOutsideSince;
        private DateTime? _previewHoverSince;
        private bool _isPeekingAtWindow;
        private HwndSource _previewSource;

        // The window whose preview the mouse button is held down on (for the hover fill's pressed look).
        private ApplicationWindow _previewPressedWindow;
        private PreviewHit _previewPressedHit;

        // The window being peeked at, and the one the pointer has been resting on for the peek delay.
        private ApplicationWindow _peekedWindow;
        private ApplicationWindow _previewHoverTarget;

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
            AppButton.PreviewMouseDown += (s, e) =>
            {
                // A tab standing for several windows keeps its preview up when clicked (see ShowGroupPreview).
                if (!ShowsGroupPreviewOnClick)
                {
                    ClosePreview();
                }
            };
            AppButton.ContextMenuOpening += (s, e) => ClosePreview();
        }

        private bool ShowsGroupPreviewOnClick => Settings.Instance.ShowTaskThumbnails && GroupWindows.Count > 1;

        // Clicking a tab that stands for several windows shows their previews straight away (instead of
        // jumping to one of them) - pick the one wanted from there.
        private void ShowGroupPreview()
        {
            _previewShowTimer?.Stop();

            if (AppButton.ToolTip is ToolTip tip && (!tip.IsOpen || _previewFadingOut))
            {
                OpenPreview();
            }
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

        private void StopPreviewShowTimer()
        {
            _previewShowTimer?.Stop();
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
            if (!IsTabHovered || AppButton.ToolTip is not ToolTip tip || AppButton.ContextMenu?.IsOpen == true)
            {
                return;
            }

            RefreshGroup();

            if (_previewOwner != null && !ReferenceEquals(_previewOwner, this))
            {
                _previewOwner.ClosePreview();
            }

            _previewOwner = this;
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
            (DependencyProperty slideAxis, double slideStart) = PreviewSlideAxis();
            double startOpacity = tip.IsOpen ? tip.Opacity : 0;
            double startOffset = tip.IsOpen ? (double)tip.GetValue(slideAxis) : slideStart;
            _previewFadingOut = false;
            tip.BeginAnimation(UIElement.OpacityProperty, null);
            ClearPreviewSlide(tip);
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
            tip.BeginAnimation(slideAxis, new DoubleAnimation
            {
                From = startOffset,
                Duration = new Duration(PreviewSlideIn),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
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
            List<PreviewItem> items = GetPreviewItems(tip);
            (PreviewItem hovered, PreviewHit hit) = overPreview ? HitTestPointer(tip, items) : (null, PreviewHit.Body);

            // The preview under the pointer is highlighted, and the close button shows hot/pressed when the
            // pointer is on it; the close buttons are always visible while the preview is open.
            UpdatePreviewItemStates(items, overPreview ? hovered : null, hit);

            // Peek at the window once the pointer has rested on its preview (not just the tab), and
            // stop as soon as it leaves the preview again or moves on to another window's.
            if (overPreview)
            {
                ApplicationWindow target = hovered?.Window ?? Window;

                if (!ReferenceEquals(_previewHoverTarget, target))
                {
                    StopPeek();
                    _previewHoverTarget = target;
                    _previewHoverSince = DateTime.UtcNow;
                }
                else if (!_isPeekingAtWindow && _previewHoverSince.HasValue && DateTime.UtcNow - _previewHoverSince.Value >= PeekDelay)
                {
                    StartPeek(target);
                }
            }
            else
            {
                _previewHoverSince = null;
                _previewHoverTarget = null;
                _previewPressedWindow = null;
                _previewPressedHit = PreviewHit.Body;
                StopPeek();
            }

            if (IsTabHovered || overPreview)
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
            _previewHoverTarget = null;
            _previewPressedWindow = null;
            _previewPressedHit = PreviewHit.Body;
            StopPeek();
            UnhookPreviewWindow();

            if (ReferenceEquals(_previewOwner, this))
            {
                _previewOwner = null;
            }

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
                ClearPreviewSlide(tip);
                tip.IsOpen = false;
                return;
            }

            if (_previewFadingOut)
            {
                return;
            }

            _previewFadingOut = true;

            (DependencyProperty slideAxis, double slideEnd) = PreviewSlideAxis();
            tip.BeginAnimation(slideAxis, new DoubleAnimation
            {
                From = (double)tip.GetValue(slideAxis),
                To = slideEnd,
                Duration = new Duration(PreviewFadeOut),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            });

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
                    ClearPreviewSlide(tip);
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

        // What the pointer is on within a window's preview.
        private enum PreviewHit
        {
            Body,
            Close,
        }

        // One window in the preview: its block of title row + thumbnail (see the DataTemplate in TaskButton.xaml).
        private sealed class PreviewItem
        {
            public FrameworkElement Root;
            public ApplicationWindow Window;
            public Button CloseButton;
            public Border Hover;
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);

                if (child is T match)
                {
                    yield return match;
                }

                foreach (T nested in Descendants<T>(child))
                {
                    yield return nested;
                }
            }
        }

        private static List<PreviewItem> GetPreviewItems(ToolTip tip)
        {
            var items = new List<PreviewItem>();

            foreach (FrameworkElement root in Descendants<FrameworkElement>(tip))
            {
                if (root.Name != "PreviewItem" || root.DataContext is not ApplicationWindow window)
                {
                    continue;
                }

                items.Add(new PreviewItem
                {
                    Root = root,
                    Window = window,
                    CloseButton = Descendants<Button>(root).FirstOrDefault(b => b.Name == "PreviewCloseButton"),
                    Hover = Descendants<Border>(root).FirstOrDefault(b => b.Name == "PreviewHover"),
                });
            }

            return items;
        }

        // Which window's preview the pointer is on, and which part of it. x/y are the client pixels of the
        // preview's window; a pointer in the frame around the previews (or the gap between them) counts as
        // the nearest one.
        private static (PreviewItem Item, PreviewHit Hit) HitTestAt(ToolTip tip, List<PreviewItem> items, int x, int y)
        {
            if (items.Count == 0 ||
                PresentationSource.FromVisual(tip) is not HwndSource source || source.RootVisual is not Visual root)
            {
                return (null, PreviewHit.Body);
            }

            try
            {
                Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
                Point pointer = fromDevice.Transform(new Point(x, y));

                PreviewItem best = null;
                double bestDistance = double.MaxValue;

                foreach (PreviewItem item in items)
                {
                    Rect bounds = item.Root.TransformToAncestor(root).TransformBounds(new Rect(0, 0, item.Root.ActualWidth, item.Root.ActualHeight));
                    double dx = Math.Max(0, Math.Max(bounds.Left - pointer.X, pointer.X - bounds.Right));
                    double dy = Math.Max(0, Math.Max(bounds.Top - pointer.Y, pointer.Y - bounds.Bottom));
                    double distance = Math.Sqrt(dx * dx + dy * dy);

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = item;
                    }
                }

                if (best?.CloseButton != null && best.CloseButton.IsVisible && best.CloseButton.ActualWidth > 0 &&
                    best.CloseButton.TransformToAncestor(root).TransformBounds(new Rect(0, 0, best.CloseButton.ActualWidth, best.CloseButton.ActualHeight)).Contains(pointer))
                {
                    return (best, PreviewHit.Close);
                }

                return (best, PreviewHit.Body);
            }
            catch (InvalidOperationException)
            {
                return (null, PreviewHit.Body);
            }
        }

        private static (PreviewItem Item, PreviewHit Hit) HitTestPointer(ToolTip tip, List<PreviewItem> items)
        {
            if (PresentationSource.FromVisual(tip) is HwndSource source && GetCursorPos(out POINT cursor) &&
                GetWindowRect(source.Handle, out RECT rect))
            {
                return HitTestAt(tip, items, cursor.X - rect.Left, cursor.Y - rect.Top);
            }

            return (null, PreviewHit.Body);
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
            List<PreviewItem> items = GetPreviewItems(tip);
            (PreviewItem item, PreviewHit hit) = HitTestAt(tip, items, x, y);

            switch (msg)
            {
                case WM_MOUSEMOVE:
                    UpdatePreviewItemStates(items, item, hit);
                    break;

                case WM_LBUTTONDOWN:
                    _previewPressedWindow = item?.Window;
                    _previewPressedHit = hit;
                    UpdatePreviewItemStates(items, item, hit);
                    break;

                case WM_LBUTTONUP:
                    ApplicationWindow pressedWindow = _previewPressedWindow;
                    PreviewHit pressedHit = _previewPressedHit;
                    _previewPressedWindow = null;
                    _previewPressedHit = PreviewHit.Body;
                    ApplicationWindow target = item?.Window ?? Window;

                    bool pressedOnButton = pressedHit != PreviewHit.Body;
                    bool releasedOnSame = hit == pressedHit && ReferenceEquals(pressedWindow, item?.Window);

                    if (pressedOnButton && !releasedOnSame)
                    {
                        // Pressed on a button, released elsewhere: cancelled, like any button.
                        UpdatePreviewItemStates(items, item, hit);
                        break;
                    }

                    // Clicking a preview anywhere but its close button activates that window; the close
                    // button closes it. Either way the preview is done.
                    bool close = pressedHit == PreviewHit.Close;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ClosePreview();

                        if (close)
                        {
                            target?.Close();
                        }
                        else
                        {
                            target?.BringToFront();
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
            foreach (PreviewItem item in GetPreviewItems(tip))
            {
                if (item.CloseButton is not Button button ||
                    button.Template?.FindName("Glyph", button) is not System.Windows.Shapes.Path glyph ||
                    glyph.Data == null || glyph.Data.Transform is ScaleTransform)
                {
                    continue;
                }

                Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
                Geometry data = glyph.Data.CloneCurrentValue();
                data.Transform = new ScaleTransform(fromDevice.M11, fromDevice.M22);
                glyph.Data = data;
            }
        }

        // The hover fill reaches out over the padding between the previews and the frame's inner edge. That
        // padding is snapped to whole device pixels (see ButtonChromeDpiSnap), so at a scale like 125% it is a
        // little more than the 3 DIPs written in the XAML - and the fill, left at 3, fell a pixel short.
        private static void FitHoverToFrame(PreviewItem item)
        {
            if (item.Hover.Tag != null)
            {
                return;
            }

            DependencyObject parent = VisualTreeHelper.GetParent(item.Root);
            while (parent != null && !(parent is Border { Name: "SnapBorder4" }))
            {
                parent = VisualTreeHelper.GetParent(parent);
            }

            if (parent is Border frame)
            {
                Thickness padding = frame.Padding;
                item.Hover.Margin = new Thickness(-padding.Left, -padding.Top, -padding.Right, -padding.Bottom);
                item.Hover.Tag = "fitted";
            }
        }

        // Which preview is highlighted (hovered or pressed), and how its close button looks: it follows its
        // Tag ("hot" / "pressed", see TaskPreviewCloseButton in System.xaml).
        private void UpdatePreviewItemStates(List<PreviewItem> items, PreviewItem hovered, PreviewHit hit)
        {
            foreach (PreviewItem item in items)
            {
                bool isHovered = ReferenceEquals(item, hovered);
                bool isPressed = isHovered && ReferenceEquals(_previewPressedWindow, item.Window);

                if (item.Hover != null)
                {
                    FitHoverToFrame(item);
                    item.Hover.Opacity = !isHovered ? 0 : (isPressed && _previewPressedHit == PreviewHit.Body ? 0.16 : 0.1);
                }

                if (item.CloseButton != null)
                {
                    bool onClose = isHovered && hit == PreviewHit.Close;
                    bool closePressed = isPressed && _previewPressedHit == PreviewHit.Close;
                    item.CloseButton.Tag = closePressed ? "pressed" : (onClose ? "hot" : null);
                }

            }
        }

        #endregion

        #region Peek

        // Aero Peek for one window: DWM hides every other top-level window (leaving glass outlines)
        // except the peeked one and the window passed to stay on top, which here is the preview
        // popup itself so it stays on screen. (Argument order is DwmActivateLivePreview's own:
        // the keep-on-top window comes before the peek target - the desktop peek in
        // ShowDesktopButton passes the taskbar in that same slot.)
        private void StartPeek(ApplicationWindow target)
        {
            if (target == null || target.Handle == IntPtr.Zero)
            {
                return;
            }

            _isPeekingAtWindow = true;
            _peekedWindow = target;
            ActivateLivePreview(1, target.Handle);
        }

        private void StopPeek()
        {
            if (!_isPeekingAtWindow)
            {
                return;
            }

            _isPeekingAtWindow = false;

            if (_peekedWindow != null)
            {
                ActivateLivePreview(0, _peekedWindow.Handle);
            }

            _peekedWindow = null;
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
