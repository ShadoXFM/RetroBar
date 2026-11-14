using RetroBar.Utilities;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RetroBar.Controls
{
    /// <summary>
    /// The notifications Windows keeps, as a popup of their own over the clock's calendar: a few of them (the newest), each of which can be
    /// clicked (it is opened) or cleared, a "clear all", and a button that opens the list up to more of them with more of their text.
    /// </summary>
    /// <summary>What the window of the bottom edge shows while the list moves: the line and the button of the list, where they are.</summary>
    public class BottomEdgeLook
    {
        public string Label;
        // How far above the bottom of the panel's content (DIPs) the bottom of the button and of the line over it are.
        public double LabelBottom, LineBottom;
        // The pointer is on the button (it has just been clicked).
        public bool Hot;
    }

    public partial class NotificationsPanel : UserControl
    {
        private const int MaxCollapsed = 3;
        /// <summary>
        /// For a panel that has the room (the sidebar): the list is never opened up or closed, it lists as many notifications as fit in this
        /// many DIPs (0: not used, the list is 3 notifications, opened up to ExpandedRows).
        /// </summary>
        public double FitHeight { get; set; }

        /// <summary>How many notifications the list is opened up to (as many as there is room for, where the owner knows).</summary>
        public int ExpandedRows { get; set; } = 8;

        // It stays as it was left, from one opening to the next.
        private static bool _expanded;

        public NotificationsPanel()
        {
            InitializeComponent();
        }

        /// <summary>True while the list is opened up.</summary>
        public static bool Expanded => _expanded;


        /// <summary>Raised when what is listed changed in a way that changes the flyout's size (one is cleared).</summary>
        public event EventHandler NotificationsChanged;

        /// <summary>Raised when a notification is clicked (it is activated; the flyout can close).</summary>
        public event EventHandler NotificationActivated;

        /// <summary>Reads the notifications again and lists them.</summary>
        public void LoadNotifications()
        {
            int count = Fill(NotificationList);
            ClearAll.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
            NoNotifications.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // The list, built into a panel: the notifications with a line between them, and under them how many more there are.
        private int Fill(Panel list)
        {
            bool fit = FitHeight > 0 && list == NotificationList;
            List<ToastItem> items = NotificationHistory.Read(fit ? 60 : Expanded ? ExpandedRows : MaxCollapsed, out int more);
            list.Children.Clear();
            int shown = 0;
            foreach (ToastItem item in items)
            {
                int before = list.Children.Count;
                // A line between them: the menus' separator, as in the weather forecast.
                if (list.Children.Count > 0)
                {
                    list.Children.Add(new MenuSeparatorLine { Margin = new Thickness(0, 1, 0, 1) });
                }

                list.Children.Add(BuildNotification(item));
                if (fit)
                {
                    // As many as fit: the one that does not goes, with the line before it, and the rest are not looked at.
                    list.Measure(new Size(200, double.PositiveInfinity));
                    if (list.DesiredSize.Height > FitHeight && shown > 0)
                    {
                        while (list.Children.Count > before)
                        {
                            list.Children.RemoveAt(list.Children.Count - 1);
                        }

                        break;
                    }
                }

                shown++;
            }

            if (fit)
            {
                more += items.Count - shown;
                if (more > 0)
                {
                    // (Not a button: there is no list to open up.)
                    var rest = new TextBlock
                    {
                        Text = "+" + more + " " + (TryFindResource("clock_flyout_more") as string ?? "more"), FontSize = 11, Opacity = 0.6,
                        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0),
                    };
                    rest.SetResourceReference(TextBlock.ForegroundProperty, "ButtonForeground");
                    rest.SetResourceReference(TextBlock.FontFamilyProperty, "WidgetLabelFontFamily");
                    list.Children.Add(rest);
                }

                return shown;
            }

            // Under the list: how many more there are (a click opens it up to them, and to more of the text), and "show less" when it is open.
            if (more > 0 || (Expanded && items.Count > MaxCollapsed))
            {
                // Opened up it always says "show less" (even with more that are not shown: a click on it closes the list, it does not open it more).
                string label = Expanded
                    ? TryFindResource("clock_flyout_show_less") as string ?? "Show less"
                    : "+" + more + " " + (TryFindResource("clock_flyout_more") as string ?? "more");
                var text = new TextBlock
                {
                    Text = label, FontSize = 11, Opacity = 0.7, Cursor = Cursors.Hand, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(8, 1, 8, 0),
                    // (the same line height for "+5 autres" and "Réduire": the accent made the second one taller)
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = 14,
                };
                text.SetResourceReference(TextBlock.ForegroundProperty, "ButtonForeground");
                text.SetResourceReference(TextBlock.FontFamilyProperty, "WidgetLabelFontFamily");
                text.MouseEnter += (s, e) => Light(text, true);
                text.MouseLeave += (s, e) => Light(text, false);
                text.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    Toggle();
                };
                // A line above it: the notifications end there (the moving window is cut at it while the list opens up and closes).
                list.Children.Insert(list.Children.Count, new MenuSeparatorLine());
                list.Children.Add(text);
            }

            return items.Count;
        }

        private string Ago(DateTime time)
        {
            TimeSpan age = DateTime.Now - time;
            bool french = LanguageCulture.Current.TwoLetterISOLanguageName == "fr";
            if (age.TotalMinutes < 1)
            {
                return french ? "à l'instant" : "now";
            }

            if (age.TotalHours < 1)
            {
                return (int)age.TotalMinutes + " min";
            }

            if (age.TotalDays < 1)
            {
                return (int)age.TotalHours + " h";
            }

            return time.ToString("d MMM", LanguageCulture.Current);
        }

        private UIElement BuildNotification(ToastItem item)
        {
            var title = new TextBlock { Text = item.Title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetResourceReference(TextBlock.ForegroundProperty, "ButtonForeground");
            title.SetResourceReference(TextBlock.FontFamilyProperty, "WidgetBoldFontFamily");
            title.SetResourceReference(TextBlock.FontWeightProperty, "WidgetFontWeight");

            var body = new TextBlock
            {
                Text = item.Body, FontSize = 11, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, MaxHeight = 30,
                TextTrimming = TextTrimming.CharacterEllipsis, Visibility = item.Body.Length == 0 ? Visibility.Collapsed : Visibility.Visible,
            };
            body.SetResourceReference(TextBlock.ForegroundProperty, "ButtonForeground");
            body.SetResourceReference(TextBlock.FontFamilyProperty, "WidgetLabelFontFamily");

            var app = new TextBlock { Text = item.AppName + " · " + Ago(item.Time), FontSize = 11, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis };
            app.SetResourceReference(TextBlock.ForegroundProperty, "ButtonForeground");
            app.SetResourceReference(TextBlock.FontFamilyProperty, "WidgetLabelFontFamily");

            var icon = new Image { Width = 16, Height = 16, Source = item.Icon, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Top };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

            // A cross, drawn as two lines, that shows while the pointer is on the row.
            var close = new Border
            {
                Width = 16, Height = 16, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Top, Opacity = 0,
                Child = new Path
                {
                    Data = Geometry.Parse("M0,0 L7,7 M7,0 L0,7"), Stroke = Brushes.White, StrokeThickness = 1.2, Width = 7, Height = 7,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
                },
            };

            var texts = new StackPanel { Children = { app, title, body } };
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
            };
            Grid.SetColumn(texts, 1);
            Grid.SetColumn(close, 2);
            grid.Children.Add(icon);
            grid.Children.Add(texts);
            grid.Children.Add(close);

            // Under the pointer the row has the glass of the visual style (the same as the calendar's days), under its text.
            var face = new NineSlice { Margins = new Thickness(4, 4, 4, 4), StateCount = 4, State = 1, Visibility = Visibility.Collapsed, Opacity = 0.55 };
            face.SetResourceReference(NineSlice.SourceProperty, "Win7GlassButtonImage");
            grid.Margin = new Thickness(5, 4, 3, 4);
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, Children = { face, grid } };
            // (the rows do not light up while the panel moves under a pointer that does not: see LockHover)
            Action<bool> light = on =>
            {
                on = on && !_hoverLocked;
                face.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                close.Opacity = on ? 0.8 : 0;
            };
            row.Tag = light;
            row.MouseEnter += (s, e) => light(true);
            row.MouseLeave += (s, e) => light(false);
            close.MouseEnter += (s, e) => close.Opacity = _hoverLocked ? 0 : 1;
            close.MouseLeave += (s, e) => close.Opacity = row.IsMouseOver && !_hoverLocked ? 0.8 : 0;
            close.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                NotificationHistory.Hide(item.Id);
                LoadNotifications();
                NotificationsChanged?.Invoke(this, EventArgs.Empty);
            };
            row.MouseLeftButtonUp += (s, e) =>
            {
                NotificationHistory.Activate(item);
                NotificationHistory.Hide(item.Id);
                NotificationActivated?.Invoke(this, EventArgs.Empty);
            };
            return row;
        }

        // The new button under the list takes over from the copy in the window of the bottom edge: lit, if the pointer is on it (a button that is
        // made under a pointer that does not move is not told so, and was a flash from the copy's light to the plain text).
        private void LightButtonUnderPointer(bool wasHot)
        {
            // (the window is not where it rests yet, so the pointer cannot be asked where it is on the button: it was on it when the list was clicked,
            // and it is still, if it has not moved. The light is put on now so that it has been drawn when the copy in the bottom edge's window goes.)
            if (wasHot && NotificationList.Children.Count > 0 && NotificationList.Children[NotificationList.Children.Count - 1] is TextBlock button)
            {
                Point now = PointToScreenSafe();
                if (Math.Abs(now.X - _lockedAt.X) < 2 && Math.Abs(now.Y - _lockedAt.Y) < 2)
                {
                    Light(button, true);
                }
            }
        }

        // Text that is a button: brighter and underlined under the pointer.
        private static void Light(TextBlock text, bool on)
        {
            text.Opacity = on ? 1 : 0.7;
            text.TextDecorations = on ? TextDecorations.Underline : null;
        }

        private void ClearAll_OnMouseEnter(object sender, MouseEventArgs e) => Light(ClearAll, true);

        private void ClearAll_OnMouseLeave(object sender, MouseEventArgs e) => Light(ClearAll, false);

        /// <summary>The slide of this panel's popup, which moves its window for the list opening up and closing.</summary>
        public PopupSlide Slide { get; set; }

        /// <summary>
        /// Called when the list starts to open up or close (true, with what the button and its line look like, and the height in pixels of the strip at the bottom of the panel that is
        /// kept still: the frame's bottom edge and the little above it) and when it has finished (false). The owner shows the bottom of the frame
        /// in a window of its own for that time, as what is cut off by the moving window.
        /// </summary>
        public Action<bool, int, BottomEdgeLook> Growing { get; set; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RectPx rect);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RectPx
        {
            public int Left, Top, Right, Bottom;
        }

        // The panel moves while the list opens up or closes, and the rows that go under the pointer lit up one after the other: the rows stay as
        // they are until the pointer itself moves.
        private bool _hoverLocked;
        private Point _lockedAt;

        private void LockHover()
        {
            _hoverLocked = true;
            _lockedAt = PointToScreenSafe();
            PreviewMouseMove -= UnlockHover;
            PreviewMouseMove += UnlockHover;
            foreach (UIElement child in NotificationList.Children)
            {
                if (child is FrameworkElement { Tag: Action<bool> light })
                {
                    light(false);
                }
            }
        }

        private Point PointToScreenSafe()
        {
            try
            {
                return PointToScreen(System.Windows.Input.Mouse.GetPosition(this));
            }
            catch (InvalidOperationException)
            {
                return default;
            }
        }

        private void UnlockHover(object sender, MouseEventArgs e)
        {
            Point now = PointToScreenSafe();
            if (Math.Abs(now.X - _lockedAt.X) < 2 && Math.Abs(now.Y - _lockedAt.Y) < 2)
            {
                return;
            }

            _hoverLocked = false;
            PreviewMouseMove -= UnlockHover;
            foreach (UIElement child in NotificationList.Children)
            {
                if (child is FrameworkElement { Tag: Action<bool> light })
                {
                    light(child.IsMouseOver);
                }
            }
        }

        // Where the line and the button under the list are, from the bottom of the content: the same in both states of the list, so what the window
        // of the bottom edge shows is put exactly where they are (and where they will be).
        private BottomEdgeLook MeasureBottomEdge(string label)
        {
            var look = new BottomEdgeLook { Label = label };
            int count = NotificationList.Children.Count;
            if (count >= 2 && NotificationList.Children[count - 1] is TextBlock button && NotificationList.Children[count - 2] is MenuSeparatorLine line)
            {
                try
                {
                    look.LabelBottom = ActualHeight - button.TransformToAncestor(this).Transform(new Point(0, button.ActualHeight)).Y;
                    look.LineBottom = ActualHeight - line.TransformToAncestor(this).Transform(new Point(0, line.ActualHeight)).Y;
                    look.Hot = button.IsMouseOver;
                }
                catch (InvalidOperationException)
                {
                }
            }

            return look;
        }

        // How far the top of the button under the list (its margin included) is above the bottom of the popup's window (pixels): what is below it
        // is not the list's, and is where the window of the bottom edge shows the frame's bottom and the button.
        private int ButtonAreaHeight(System.Windows.Interop.HwndSource source, double scale)
        {
            // The line above the button is where the list ends.
            int lineAt = NotificationList.Children.Count - 2;
            if (lineAt >= 0 && NotificationList.Children[lineAt] is MenuSeparatorLine button && source.RootVisual is FrameworkElement root)
            {
                try
                {
                    double top = button.TransformToAncestor(root).Transform(new Point(0, 0)).Y - button.Margin.Top;
                    return Math.Max(0, (int)Math.Round((root.ActualHeight - top) * scale));
                }
                catch (InvalidOperationException)
                {
                }
            }

            return FrameBottomInset(scale) + (int)Math.Round(26 * scale);
        }

        // How far the drawn frame's bottom edge is above the bottom of the popup's window (pixels): the room for the shadow and the style's own margin.
        private int FrameBottomInset(double scale)
        {
            for (DependencyObject d = VisualTreeHelper.GetParent(this); d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is ContentControl { Tag: "WidgetPopup" } control)
                {
                    double inset = control.Margin.Bottom;
                    if (VisualTreeHelper.GetChildrenCount(control) > 0 && VisualTreeHelper.GetChild(control, 0) is FrameworkElement chrome)
                    {
                        inset += chrome.Margin.Bottom;
                    }

                    return (int)Math.Round(inset * scale);
                }
            }

            return 0;
        }

        // The list opens up (or closes) with the panel growing upwards. The popup's window is not resized in every frame (that took 45 ms a frame:
        // its frame and its shadow are drawn again in software each time): its window is moved, and cut at the panel's bottom edge, as the popups'
        // windows are when they slide, which the compositor does at the monitor's pace. Opening: the window is made as big as it will be, and moved
        // down and cut to the old height, then moved up, so the top of the panel goes up with the header and the rows come out under it. Closing: the
        // same with the old list, to the new height, and the new list is put in when it has got there.
        private void Toggle()
        {
            bool grows = !_expanded;
            double before = NotificationList.ActualHeight;
            LockHover();
            if (Slide == null || !Slide.IsOpen || Slide.Closing || Slide.IsGrowing ||
                System.Windows.PresentationSource.FromVisual(this) is not System.Windows.Interop.HwndSource source ||
                !GetWindowRect(source.Handle, out RectPx old))
            {
                _expanded = grows;
                LoadNotifications();
                NotificationsChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            // The new list's height, from the list built in a panel of its own.
            _expanded = grows;
            var probe = new StackPanel { Width = Math.Max(1, NotificationList.ActualWidth) };
            Fill(probe);
            // (in the tree, in a place that has no size, so that it is measured as it will be: the lines of the lists are of whole pixels)
            ProbeHost.Children.Add(probe);
            ProbeHost.UpdateLayout();
            double after = probe.DesiredSize.Height;
            ProbeHost.Children.Clear();
            // The button the list will have under it (it is shown by the window of the bottom edge while the list moves).
            string label = probe.Children.Count > 1 && probe.Children[probe.Children.Count - 1] is TextBlock last ? last.Text : null;
            BottomEdgeLook look = MeasureBottomEdge(label);
            _expanded = !grows;

            double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
            // The moving window is cut above the frame's bottom edge by as much as the room of the button under the list (and the frame's rounded
            // corners), and that room, with the frame's bottom, is shown by a window of its own that does not move: a row that is cut by the edge
            // does not show where the button is to be.
            int inset = ButtonAreaHeight(source, scale);
            int delta = (int)Math.Round((after - before) * scale);
            int width = old.Right - old.Left, height = old.Bottom - old.Top;
            var time = TimeSpan.FromMilliseconds(260);
            if (delta == 0)
            {
                _expanded = grows;
                LoadNotifications();
                return;
            }

            if (grows)
            {
                // The window as it will rest: as big as the list needs, with its bottom where it is. The list is changed once the window is held
                // where it is (see PopupSlide.WindowGrow).
                Growing?.Invoke(true, inset, look);
                Slide.WindowGrow(old.Left, old.Bottom - (height + delta), width, height + delta, old.Bottom, inset, delta, 0, time, () =>
                {
                    _expanded = true;
                    LoadNotifications();
                    UpdateLayout();
                    LightButtonUnderPointer(look.Hot);
                    // How high the popup's window is now: it is resized as the layout is done, or else it is the height of what is in it.
                    if (GetWindowRect(source.Handle, out RectPx now) && now.Bottom - now.Top != height)
                    {
                        return now.Bottom - now.Top;
                    }

                    return source.RootVisual is FrameworkElement root ? (int)Math.Round(root.ActualHeight * scale) : 0;
                }, () => Growing?.Invoke(false, 0, null));
            }
            else
            {
                Growing?.Invoke(true, inset, look);
                Slide.WindowGrow(old.Left, old.Top, width, height, old.Bottom, inset, 0, -delta, time, null, () =>
                {
                    _expanded = false;
                    LoadNotifications();
                    UpdateLayout();
                    LightButtonUnderPointer(look.Hot);
                    Growing?.Invoke(false, 0, null);
                });
            }
        }

        private void ClearAll_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var ids = new List<long>();
            foreach (ToastItem item in NotificationHistory.Read(1000, out _))
            {
                ids.Add(item.Id);
            }

            NotificationHistory.Hide(ids.ToArray());
            LoadNotifications();
            NotificationsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
