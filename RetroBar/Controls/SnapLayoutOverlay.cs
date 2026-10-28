using ManagedShell.AppBar;
using RetroBar.Utilities;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace RetroBar.Controls
{
    /// <summary>
    /// The snap layouts shown over a monitor while a tab is dragged out of the taskbar: a card of layouts at the side of the taskbar's edge
    /// (the layouts as small pictures of cells), and the cell the cursor is on is lit, with a shape of where the window would go drawn
    /// on the monitor. The window covers the monitor's work area and is click-through, never active, so the drag goes on under it.
    /// </summary>
    internal sealed class SnapLayoutOverlay : Window
    {
        private const double CellWidth = 68;
        private const double CellHeight = 46;
        private const double CellGap = 3;

        private readonly SnapNative.RECT _work;
        private readonly List<(Border Cell, SnapZone Zone)> _cells = new();
        private readonly Border _preview;
        private readonly Grid _root;
        private readonly Brush _cellFill;
        private readonly Brush _cellLine;
        private readonly Brush _accentFill;
        private readonly Brush _accentLine;

        public SnapNative.RECT Work => _work;

        public SnapLayoutOverlay(SnapNative.RECT work, AppBarEdge edge, Rect[][] layouts)
        {
            _work = work;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            Left = 0;
            Top = 0;
            Width = 200;
            Height = 100;

            Brush face = Frozen(Resource("ButtonFace", SystemColors.ControlBrush));
            Brush foreground = Resource("ButtonForeground", SystemColors.ControlTextBrush);
            Brush highlight = Resource("ButtonHighlight", SystemColors.ControlDarkBrush);
            Color accent = SystemParameters.WindowGlassColor;

            _cellFill = Opacity(foreground, 0.16);
            _cellLine = Opacity(foreground, 0.55);
            _accentFill = Opacity(new SolidColorBrush(accent), 0.95);
            _accentLine = Frozen(new SolidColorBrush(accent));

            _root = new Grid();
            Content = _root;

            // Where the window would go, on the monitor.
            _preview = new Border
            {
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2),
                Background = Opacity(new SolidColorBrush(accent), 0.22),
                BorderBrush = _accentLine,
                IsHitTestVisible = false,
            };
            _root.Children.Add(_preview);

            // The card of layouts, at the taskbar's edge (room round it for its shadow).
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < layouts.Length; i++)
            {
                var picture = new Canvas { Width = CellWidth, Height = CellHeight, Margin = new Thickness(i == 0 ? 0 : 10, 0, 0, 0) };
                foreach (Rect fraction in layouts[i])
                {
                    var zone = new SnapZone(fraction);
                    var cell = new Border
                    {
                        Width = Math.Max(2, fraction.Width * CellWidth - CellGap),
                        Height = Math.Max(2, fraction.Height * CellHeight - CellGap),
                        CornerRadius = new CornerRadius(2),
                        BorderThickness = new Thickness(1),
                        Background = _cellFill,
                        BorderBrush = _cellLine,
                    };
                    Canvas.SetLeft(cell, fraction.X * CellWidth + CellGap / 2);
                    Canvas.SetTop(cell, fraction.Y * CellHeight + CellGap / 2);
                    picture.Children.Add(cell);
                    _cells.Add((cell, zone));
                }

                row.Children.Add(picture);
            }

            var card = new Border
            {
                Child = row,
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Background = face,
                BorderBrush = highlight,
                Margin = new Thickness(26),
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45, Color = Colors.Black },
            };
            card.HorizontalAlignment = edge == AppBarEdge.Left ? HorizontalAlignment.Left : edge == AppBarEdge.Right ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            card.VerticalAlignment = edge == AppBarEdge.Top ? VerticalAlignment.Top : edge == AppBarEdge.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            _root.Children.Add(card);
        }

        private static Brush Resource(string key, Brush fallback) => System.Windows.Application.Current?.TryFindResource(key) as Brush ?? fallback;

        private static Brush Frozen(Brush brush)
        {
            Brush clone = brush.CloneCurrentValue();
            clone.Freeze();
            return clone;
        }

        private static Brush Opacity(Brush brush, double opacity)
        {
            Brush clone = brush.CloneCurrentValue();
            clone.Opacity = opacity;
            clone.Freeze();
            return clone;
        }

        // The window is the monitor's work area, in pixels (set after it is made and after a change of scale, which would resize it).
        private void Place()
        {
            IntPtr hWnd = new WindowInteropHelper(this).Handle;
            if (hWnd != IntPtr.Zero)
            {
                SnapNative.SetWindowPos(hWnd, SnapNative.HWND_TOPMOST, _work.Left, _work.Top, _work.Width, _work.Height, SnapNative.SWP_NOACTIVATE | SnapNative.SWP_SHOWWINDOW);
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Never in the way: the mouse goes through it, it takes no focus, and it is not in Alt+Tab.
            IntPtr hWnd = new WindowInteropHelper(this).Handle;
            int ex = SnapNative.GetWindowLong(hWnd, SnapNative.GWL_EXSTYLE);
            SnapNative.SetWindowLong(hWnd, SnapNative.GWL_EXSTYLE, ex | SnapNative.WS_EX_TRANSPARENT | SnapNative.WS_EX_NOACTIVATE | SnapNative.WS_EX_TOOLWINDOW);
            Place();
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            Place();
        }

        protected override void OnDpiChanged(DpiScale oldDpiScaleInfo, DpiScale newDpiScaleInfo)
        {
            base.OnDpiChanged(oldDpiScaleInfo, newDpiScaleInfo);
            Dispatcher.BeginInvoke(new Action(Place));
        }

        /// <summary>The cell under the cursor (in screen pixels), or null.</summary>
        public SnapZone HitTest(SnapNative.POINT cursor)
        {
            foreach ((Border cell, SnapZone zone) in _cells)
            {
                if (!cell.IsVisible || cell.ActualWidth <= 0)
                {
                    continue;
                }

                Point origin = cell.PointToScreen(new Point(0, 0));
                double scale = VisualTreeHelper.GetDpi(cell).DpiScaleX;
                // The gap between cells belongs to them too.
                double slop = CellGap / 2 * scale + 1;
                if (cursor.X >= origin.X - slop && cursor.X < origin.X + cell.ActualWidth * scale + slop &&
                    cursor.Y >= origin.Y - slop && cursor.Y < origin.Y + cell.ActualHeight * scale + slop)
                {
                    return zone;
                }
            }

            return null;
        }

        public void SetHover(SnapZone hover)
        {
            foreach ((Border cell, SnapZone zone) in _cells)
            {
                bool on = zone == hover;
                cell.Background = on ? _accentFill : _cellFill;
                cell.BorderBrush = on ? _accentLine : _cellLine;
            }

            if (hover == null || _root.ActualWidth <= 0)
            {
                _preview.Visibility = Visibility.Collapsed;
                return;
            }

            const double inset = 5;
            Rect f = hover.Fraction;
            double w = _root.ActualWidth;
            double h = _root.ActualHeight;
            _preview.Margin = new Thickness(f.X * w + inset, f.Y * h + inset, 0, 0);
            _preview.Width = Math.Max(1, f.Width * w - inset * 2);
            _preview.Height = Math.Max(1, f.Height * h - inset * 2);
            _preview.Visibility = Visibility.Visible;
        }
    }
}
