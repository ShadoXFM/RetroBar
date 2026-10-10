using GongSolutions.Wpf.DragDrop;
using ManagedShell.WindowsTray;
using RetroBar.Extensions;
using RetroBar.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Tray = ManagedShell.WindowsTray;

namespace RetroBar.Controls
{
    /// <summary>
    /// Interaction logic for NotifyIconList.xaml
    /// </summary>
    public partial class NotifyIconList : UserControl
    {
        private bool _isLoaded;
        private bool _toggleGlyphHooked;
        private ObservableCollection<Tray.NotifyIcon> promotedIcons = new ObservableCollection<Tray.NotifyIcon>();
        private NotifyIconDropHandler dropHandler;
        private ListCollectionView collectionView;

        public static DependencyProperty NotificationAreaProperty = DependencyProperty.Register(nameof(NotificationArea), typeof(NotificationArea), typeof(NotifyIconList), new PropertyMetadata(NotificationAreaChangedCallback));

        public NotificationArea NotificationArea
        {
            get { return (NotificationArea)GetValue(NotificationAreaProperty); }
            set { SetValue(NotificationAreaProperty, value); }
        }

        public NotifyIconList()
        {
            InitializeComponent();
        }

        private bool IsCollapsed()
        {
            return Settings.Instance.CollapseNotifyIcons && NotifyIconToggleButton.IsChecked != true;
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.CollapseNotifyIcons))
            {
                if (Settings.Instance.CollapseNotifyIcons)
                {
                    SetToggleVisibility();
                }
                else
                {
                    NotifyIconToggleButton.IsChecked = false;
                    NotifyIconToggleButton.Visibility = Visibility.Collapsed;
                }
                collectionView?.Refresh();
            }
            else if (e.PropertyName == nameof(Settings.InvertIconsMode) || e.PropertyName == nameof(Settings.InvertNotifyIcons) || e.PropertyName == nameof(Settings.NotifyIconOrder))
            {
                // Reload icons
                collectionView?.Refresh();
            }
        }

        private void SetNotificationAreaCollections()
        {
            if (!_isLoaded && NotificationArea != null)
            {
                NotificationArea.NotificationBalloonShown += NotificationArea_NotificationBalloonShown;
                NotificationArea.UnpinnedIcons.CollectionChanged += UnpinnedIcons_CollectionChanged;
                NotificationArea.UnpinnedIcons.Filter = UnpinnedNotifyIcons_Filter;
                Settings.Instance.PropertyChanged += Settings_PropertyChanged;

                collectionView = new ListCollectionView(NotificationArea.TrayIcons);
                collectionView.CustomSort = new NotifyIconComparer(this);
                collectionView.Filter = NotifyIcons_Filter;
                var collectionViewShaping = collectionView as ICollectionViewLiveShaping;
                collectionViewShaping.IsLiveFiltering = true;
                collectionViewShaping.LiveFilteringProperties.Add("IsHidden");
                collectionViewShaping.LiveFilteringProperties.Add("IsPinned");
                NotifyIcons.ItemsSource = collectionView;

                if (Settings.Instance.CollapseNotifyIcons)
                {
                    SetToggleVisibility();
                }

                _isLoaded = true;
            }
        }

        private static void NotificationAreaChangedCallback(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is NotifyIconList notifyIconList && e.OldValue == null && e.NewValue != null)
            {
                notifyIconList.SetNotificationAreaCollections();
            }
        }

        private bool NotifyIcons_Filter(object icon)
        {
            if (icon is Tray.NotifyIcon notifyIcon)
            {
                return (!IsCollapsed() || notifyIcon.IsPinned)
                    && !notifyIcon.IsHidden
                    && notifyIcon.GetBehavior() != NotifyIconBehavior.Remove;
            }
            return false;
        }

        private bool UnpinnedNotifyIcons_Filter(object obj)
        {
            // This filter is used when we check if the toggle should hide
            if (obj is Tray.NotifyIcon notifyIcon)
            {
                return !notifyIcon.IsPinned && !notifyIcon.IsHidden && notifyIcon.GetBehavior() != NotifyIconBehavior.Remove;
            }

            return true;
        }

        private void NotificationArea_NotificationBalloonShown(object sender, NotificationBalloonEventArgs e)
        {
            // This is used to promote unpinned icons to show when the tray is collapsed.

            if (NotificationArea == null)
            {
                return;
            }

            Tray.NotifyIcon notifyIcon = e.Balloon.NotifyIcon;

            if (NotificationArea.PinnedIcons.Contains(notifyIcon))
            {
                // Do not promote pinned icons (they're already there!)
                return;
            }

            if (notifyIcon.GetBehavior() != NotifyIconBehavior.HideWhenInactive)
            {
                // Do not promote icons that are always hidden
                return;
            }

            if (promotedIcons.Contains(notifyIcon))
            {
                // Do not duplicate promoted icons
                return;
            }

            notifyIcon.IsPinned = true;
            promotedIcons.Add(notifyIcon);

            DispatcherTimer unpromoteTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(e.Balloon.Timeout + 500) // Keep it around for a few ms for the animation to complete
            };
            unpromoteTimer.Tick += (object sender, EventArgs e) =>
            {
                if (promotedIcons.Contains(notifyIcon))
                {
                    if (!(Settings.Instance.NotifyIconBehaviors.Find(setting => setting.Identifier == notifyIcon.Identifier) is NotifyIconBehaviorSetting iconSetting && iconSetting.Behavior == NotifyIconBehavior.AlwaysShow))
                    {
                        // Don't unpin if settings were changed to always show
                        notifyIcon.IsPinned = false;
                    }
                    promotedIcons.Remove(notifyIcon);
                }
                unpromoteTimer.Stop();
            };
            unpromoteTimer.Start();
        }

        // The "show hidden icons" arrow is the pixel double chevron the original RetroBar uses (two chevrons pointing
        // left; turned half way round, so pointing right, while the hidden icons are shown), not a "^": where a theme's
        // button has the TextBlock "ArrowText" (the ones that draw the arrow as text), this puts the glyph in it, and the
        // theme's own turning of that TextBlock still applies. Drawn on whole device pixels at any scale: each pixel of the
        // glyph is one unit and a layout transform of 1 / DPI scale makes a unit one device pixel.
        private static readonly string[] ToggleGlyphRows =
        {
            "..##..##",
            ".##..##.",
            "##..##..",
            ".##..##.",
            "..##..##",
        };

        private static readonly Geometry ToggleGlyphGeometry = CreateToggleGlyphGeometry();

        private static Geometry CreateToggleGlyphGeometry()
        {
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            for (int y = 0; y < ToggleGlyphRows.Length; y++)
            {
                string row = ToggleGlyphRows[y];
                int x = 0;
                while (x < row.Length)
                {
                    if (row[x] != '#')
                    {
                        x++;
                        continue;
                    }

                    int start = x;
                    while (x < row.Length && row[x] == '#')
                    {
                        x++;
                    }

                    group.Children.Add(new RectangleGeometry(new Rect(start, y, x - start, 1)));
                }
            }

            group.Freeze();
            return group;
        }

        private Path _toggleGlyph;
        private string _toggleGlyphKey;

        private void UpdateToggleGlyph()
        {
            NotifyIconToggleButton.ApplyTemplate();
            if (NotifyIconToggleButton.Template?.FindName("ArrowText", NotifyIconToggleButton) is not TextBlock arrowText)
            {
                return;
            }

            double dpi = VisualTreeHelper.GetDpi(NotifyIconToggleButton).DpiScaleX;
            if (dpi <= 0)
            {
                dpi = 1;
            }

            // A monitor can have a glyph of its own ("TrayToggleGlyph" in monitor-adjustments.json: a Geometry in device pixels,
            // from 0,0), for instance a bigger one on a monitor where this one comes out small.
            string deviceName = (Window.GetWindow(this) as Taskbar)?.Screen.DeviceName;
            string figures = MonitorAdjustments.Get(deviceName, "TrayToggleGlyph").Geometry;
            Geometry data = ToggleGlyphGeometry;
            double glyphWidth = ToggleGlyphRows[0].Length;
            double glyphHeight = ToggleGlyphRows.Length;
            if (!string.IsNullOrWhiteSpace(figures))
            {
                try
                {
                    Geometry custom = Geometry.Parse(figures);
                    custom.Freeze();
                    Rect bounds = custom.Bounds;
                    data = custom;
                    glyphWidth = Math.Ceiling(bounds.Right);
                    glyphHeight = Math.Ceiling(bounds.Bottom);
                }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException)
                {
                    figures = null;
                }
            }
            else
            {
                figures = null;
            }

            string key = figures ?? "";

            // Already in this TextBlock (the template was not replaced) and the same glyph: only the scale may have changed.
            if (_toggleGlyph != null && _toggleGlyphKey == key && arrowText.Inlines.FirstInline is InlineUIContainer container && ReferenceEquals(container.Child, _toggleGlyph))
            {
                _toggleGlyph.LayoutTransform = new ScaleTransform(1 / dpi, 1 / dpi);
                return;
            }

            _toggleGlyphKey = key;
            _toggleGlyph = new Path
            {
                Data = data,
                Width = glyphWidth,
                Height = glyphHeight,
                Stretch = Stretch.None,
                SnapsToDevicePixels = true,
                LayoutTransform = new ScaleTransform(1 / dpi, 1 / dpi),
            };
            _toggleGlyph.SetBinding(Shape.FillProperty, new Binding(nameof(TextBlock.Foreground)) { Source = arrowText });

            // The theme moves the "^" (up by 3, down by 4, as a margin) while the hidden icons are shown, to make up for it
            // turning half way round off its line; this glyph turns about its own center, so it stays where it is (a local
            // value, which a template trigger does not override).
            arrowText.Margin = new Thickness(0);

            arrowText.Text = "";
            arrowText.Inlines.Clear();
            arrowText.Inlines.Add(new InlineUIContainer(_toggleGlyph) { BaselineAlignment = BaselineAlignment.Center });
        }

        private void QueueUpdateToggleGlyph()
        {
            Dispatcher.BeginInvoke(new Action(UpdateToggleGlyph), DispatcherPriority.Loaded);
        }

        private void NotifyIconList_Loaded(object sender, RoutedEventArgs e)
        {
            SetNotificationAreaCollections();

            // Again whenever the button's template is replaced (another theme) or the scale changes.
            if (!_toggleGlyphHooked)
            {
                _toggleGlyphHooked = true;
                DependencyPropertyDescriptor.FromProperty(Control.TemplateProperty, typeof(Control))
                    .AddValueChanged(NotifyIconToggleButton, (s, args) => { _toggleGlyph = null; QueueUpdateToggleGlyph(); });
                if (Window.GetWindow(this) is Window window)
                {
                    window.DpiChanged += (s, args) => QueueUpdateToggleGlyph();
                }
            }

            QueueUpdateToggleGlyph();

            // Set up drag/drop handler
            if (dropHandler == null)
            {
                dropHandler = new NotifyIconDropHandler(this);
                GongSolutions.Wpf.DragDrop.DragDrop.SetDropHandler(NotifyIcons, dropHandler);
            }
        }

        private void NotifyIconList_OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded)
            {
                return;
            }

            if (NotificationArea != null)
            {
                NotificationArea.NotificationBalloonShown -= NotificationArea_NotificationBalloonShown;
                NotificationArea.UnpinnedIcons.CollectionChanged -= UnpinnedIcons_CollectionChanged;
                NotificationArea.UnpinnedIcons.Filter = UnpinnedNotifyIcons_Filter;
                Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
            }

            _isLoaded = false;
        }

        private void UnpinnedIcons_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            SetToggleVisibility();
        }

        private void NotifyIconToggleButton_OnClick(object sender, RoutedEventArgs e)
        {
            collectionView?.Refresh();
        }

        private void SetToggleVisibility()
        {
            if (!Settings.Instance.CollapseNotifyIcons) return;

            if (NotificationArea.UnpinnedIcons.IsEmpty)
            {
                NotifyIconToggleButton.Visibility = Visibility.Collapsed;

                if (NotifyIconToggleButton.IsChecked == true)
                {
                    NotifyIconToggleButton.IsChecked = false;
                }
            }
            else
            {
                NotifyIconToggleButton.Visibility = Visibility.Visible;
            }
        }

        public void UpdateIconOrder(IDropInfo dropInfo)
        {
            if (NotificationArea == null || collectionView == null) return;

            var visibleIcons = collectionView.Cast<Tray.NotifyIcon>().ToList();

            if (IsCollapsed())
            {
                // Do not save temporary promoted icons
                visibleIcons = visibleIcons.Where(i => !promotedIcons.Contains(i)).ToList();
            }

            // Update the dragged icon's position in the list
            if (dropInfo.Data is Tray.NotifyIcon draggedIcon)
            {
                int insertIndex = dropInfo.InsertIndex;
                if (insertIndex > 0 && visibleIcons.IndexOf(draggedIcon) < insertIndex && visibleIcons.Remove(draggedIcon))
                {
                    insertIndex--;
                }
                else
                {
                    visibleIcons.Remove(draggedIcon);
                }
                visibleIcons.Insert(insertIndex, draggedIcon);
            }
            else
            {
                return;
            }
            
            // Never overwrite the list to prevent clearing out settings for non-visible icons
            var oldOrder = Settings.Instance.NotifyIconOrder ?? new List<string>();
            var result = new List<string>();
            int replaceIndex = 0;
            
            foreach (var id in oldOrder)
            {
                if (visibleIcons.Find(i => i.IsEqualByIdentifier(id)) != null)
                {
                    if (replaceIndex < visibleIcons.Count)
                    {
                        result.Add(visibleIcons[replaceIndex++].Identifier);
                    }
                }
                else
                {
                    result.Add(id);
                }
            }

            while (replaceIndex < visibleIcons.Count)
            {
                result.Add(visibleIcons[replaceIndex++].Identifier);
            }

            Settings.Instance.NotifyIconOrder = result;
        }

        public class NotifyIconComparer : System.Collections.IComparer
        {
            private NotifyIconList _host;

            public NotifyIconComparer(NotifyIconList host)
            {
                _host = host;
            }

            public int Compare(object x, object y)
            {
                if (x is Tray.NotifyIcon xIcon && y is Tray.NotifyIcon yIcon && Settings.Instance.NotifyIconOrder is List<string> setting)
                {
                    if (_host.IsCollapsed())
                    {
                        bool xPromoted = _host.promotedIcons.Contains(xIcon);
                        bool yPromoted = _host.promotedIcons.Contains(yIcon);
                        if (xPromoted && !yPromoted)
                        {
                            return -1;
                        }
                        if (!xPromoted && yPromoted)
                        {
                            return 1;
                        }
                    }
                    int xIndex = setting.FindIndex(s => xIcon.IsEqualByIdentifier(s));
                    int yIndex = setting.FindIndex(s => yIcon.IsEqualByIdentifier(s));
                    return xIndex.CompareTo(yIndex);
                }
                return 0;
            }
        }
    }
}