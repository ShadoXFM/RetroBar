using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RetroBar.Utilities
{
    /// <summary>The side of the popup's window its content slides away to when hidden (the side of the taskbar's edge).</summary>
    public enum SlideDirection
    {
        Down,
        Up,
        Left,
        Right,
    }

    /// <summary>
    /// The slide of a popup out of the taskbar's edge and back into it, shared by the media popup and the weather forecast: the content
    /// of the popup's window (slideHost) moves inside the window, which stays where it is and clips what is outside it, from a window's
    /// size away on the side of the taskbar - so it comes from the edge, without a fade.
    ///
    /// While it slides the popup can also be clipped (clipRoot) to what is above the taskbar (the window reaches a little over the
    /// taskbar, for the shadow); at rest it has no transform and no clip at all (any render transform switches off pixel snapping,
    /// which blurs the frame's lines).
    /// </summary>
    public sealed class PopupSlide
    {
        private static readonly Duration ShowDuration = new(TimeSpan.FromMilliseconds(200));
        private static readonly Duration HideDuration = new(TimeSpan.FromMilliseconds(150));

        private readonly Popup _popup;
        private readonly FrameworkElement _clipRoot;
        private readonly FrameworkElement _slideHost;
        private readonly Func<SlideDirection> _direction;
        private readonly Func<double?> _clipOverlap;
        private readonly TranslateTransform _transform = new();
        private bool _sliding;

        /// <param name="popup">The popup opened and closed with the slide.</param>
        /// <param name="clipRoot">The popup's content that clips to its own bounds: what is cut at the taskbar while sliding.</param>
        /// <param name="slideHost">The element inside it that moves.</param>
        /// <param name="direction">Where the content goes when hidden.</param>
        /// <param name="clipOverlap">How many DIPs the popup's window reaches below the taskbar's top edge (the bottom of the content is cut
        /// there while it slides), or null for no clip.</param>
        public PopupSlide(Popup popup, FrameworkElement clipRoot, FrameworkElement slideHost, Func<SlideDirection> direction, Func<double?> clipOverlap)
        {
            _popup = popup;
            _clipRoot = clipRoot;
            _slideHost = slideHost;
            _direction = direction;
            _clipOverlap = clipOverlap;

            // The clip follows the size while it slides (the popup's first layout comes after it opens).
            _clipRoot.SizeChanged += (s, e) => UpdateClip();

            // The popup's window is in its place when it has opened: the clip is measured from where it is (see MeasureOverlap).
            _popup.Opened += (s, e) => UpdateClip();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        /// <summary>
        /// How many DIPs the open popup's window reaches below the top of the taskbar's window, from where the two windows are on the screen
        /// (null if either is not there yet): the popup is placed in whole pixels, so a number worked out from the layout can be a pixel out,
        /// which left a pixel of the popup over the taskbar's edge while it slid.
        /// </summary>
        public static double? MeasureOverlap(FrameworkElement clipRoot, Window taskbar)
        {
            if (PresentationSource.FromVisual(clipRoot) is not HwndSource popupSource
                || PresentationSource.FromVisual(taskbar) is not HwndSource taskbarSource
                || !GetWindowRect(popupSource.Handle, out RECT popup)
                || !GetWindowRect(taskbarSource.Handle, out RECT bar))
            {
                return null;
            }

            double scale = VisualTreeHelper.GetDpi(clipRoot).DpiScaleY;
            return Math.Max(0, (popup.Bottom - bar.Top) / scale);
        }

        /// <summary>True while it slides away (the popup closes when that is done).</summary>
        public bool Closing { get; private set; }

        /// <summary>True while the popup is open (sliding in or away included).</summary>
        public bool IsOpen => _popup.IsOpen;

        /// <summary>Raised when the popup has closed, after it slid away or by CloseNow.</summary>
        public event Action Closed;

        private (DependencyProperty Property, double Hidden) Axis()
        {
            SlideDirection direction = _direction();
            bool sideways = direction is SlideDirection.Left or SlideDirection.Right;
            double size = sideways ? _slideHost.ActualWidth : _slideHost.ActualHeight;
            if (size <= 0)
            {
                size = sideways ? 300 : 400;
            }

            return direction switch
            {
                SlideDirection.Left => (TranslateTransform.XProperty, -size),
                SlideDirection.Right => (TranslateTransform.XProperty, size),
                SlideDirection.Up => (TranslateTransform.YProperty, -size),
                _ => (TranslateTransform.YProperty, size),
            };
        }

        /// <summary>Opens the popup, sliding in (from where it is, if it was still on screen sliding away).</summary>
        public void Show()
        {
            (DependencyProperty axis, double hidden) = Axis();
            double from = _popup.IsOpen ? (double)_transform.GetValue(axis) : hidden;

            Closing = false;
            Reset();
            _slideHost.RenderTransform = _transform;
            _transform.SetValue(axis, from);
            _sliding = true;
            UpdateClip();
            _popup.IsOpen = true;

            var slide = new DoubleAnimation(from, 0, ShowDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            slide.Completed += (s, e) =>
            {
                if (!Closing)
                {
                    Reset();
                }
            };
            _transform.BeginAnimation(axis, slide);
        }

        /// <summary>The slide away that makes room for another popup: shorter, so that popup does not wait long.</summary>
        public static readonly TimeSpan QuickHideTime = TimeSpan.FromMilliseconds(90);

        /// <summary>Slides away and closes the popup (in the time given, or the usual one).</summary>
        public void Hide(TimeSpan? time = null)
        {
            if (!_popup.IsOpen || Closing)
            {
                return;
            }

            Closing = true;
            (DependencyProperty axis, double hidden) = Axis();
            _slideHost.RenderTransform = _transform;
            _sliding = true;
            UpdateClip();

            var slide = new DoubleAnimation((double)_transform.GetValue(axis), hidden, time is TimeSpan quick ? new Duration(quick) : HideDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            slide.Completed += (s, e) =>
            {
                // Only if it wasn't brought back (or closed some other way) while sliding away.
                if (Closing)
                {
                    Closing = false;
                    _popup.IsOpen = false;
                    Reset();
                    Closed?.Invoke();
                }
            };
            _transform.BeginAnimation(axis, slide);
        }

        /// <summary>Closes the popup at once, with no slide.</summary>
        public void CloseNow()
        {
            Closing = false;
            _popup.IsOpen = false;
            Reset();
            Closed?.Invoke();
        }

        /// <summary>Stops any slide and takes the transform and the clip off.</summary>
        public void Reset()
        {
            _transform.BeginAnimation(TranslateTransform.XProperty, null);
            _transform.BeginAnimation(TranslateTransform.YProperty, null);
            _transform.X = 0;
            _transform.Y = 0;
            _slideHost.RenderTransform = null;
            _sliding = false;
            UpdateClip();
        }

        /// <summary>Sets the clip again (the taskbar moved, or the popup's size changed).</summary>
        public void UpdateClip()
        {
            double? overlap = _sliding && _clipRoot.ActualHeight > 0 ? _clipOverlap() : null;
            if (overlap == null)
            {
                _clipRoot.ClearValue(UIElement.ClipProperty);
                return;
            }

            _clipRoot.Clip = new RectangleGeometry(new Rect(0, 0, _clipRoot.ActualWidth, Math.Max(0, _clipRoot.ActualHeight - overlap.Value)));
        }
    }
}
