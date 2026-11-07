using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Drag to rearrange the tabs as Windows 7 and later do it: the tab being dragged follows the pointer along the row, the others slide
    /// out of its way as it passes the middle of each, and it settles into its place when it is let go. Nothing is marked and nothing is moved
    /// in the list until then (the order is changed once, on release).
    /// </summary>
    internal sealed class LiveTabDrag
    {
        private readonly ItemsControl _list;
        // Makes the list as wide as its room while a tab is carried (and as it was after), so the tab can be taken beyond the row.
        private readonly Action<bool> _room;
        private double _minShift, _maxShift;
        private readonly Func<bool> _enabled;
        private readonly Action<object, int, Dictionary<object, Point>> _commit;

        private bool _pressing;
        private bool _dragging;
        private bool _finishing;
        private double _downX;
        private FrameworkElement _grabbed;
        private object _item;
        private int _grabbedIndex;
        private int _target;
        private List<FrameworkElement> _containers;
        private double[] _lefts;
        private double[] _widths;
        // The room each tab takes in the row: from its left to the next one's, as they are laid out (a tab's width is not that on a display where the
        // tabs are a fraction of a pixel apart - they come to whole pixels one by one, 37 and 38 - and going by the widths moved every tab by up to a
        // pixel as soon as one was taken).
        private double[] _slots;
        private TranslateTransform[] _transforms;
        private double[] _shifts;

        // The tab being carried is drawn by an adorner (a copy of it that is not cut at the list's edges), so it can be taken beyond the width
        // the row has; the tab itself stays where its place is, unseen, and is moved only as far as the row goes (for the slide into place).
        private AdornerLayer _layer;
        private Ghost _ghost;
        private double _ghostLeft;
        private double _lastShift;

        /// <param name="enabled">Whether a drag may begin now (the row is horizontal, in the style the tabs are rearranged in).</param>
        /// <param name="commit">Puts the item before the one at this index of the list (as it was when the drag began); the positions are those of
        /// every item as they are seen at the moment of release, for the slide into place.</param>
        public LiveTabDrag(ItemsControl list, Func<bool> enabled, Action<object, int, Dictionary<object, Point>> commit, Action<bool> room = null)
        {
            _room = room;
            _list = list;
            _enabled = enabled;
            _commit = commit;
            list.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnDown), true);
            list.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnMove), true);
            list.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnUp), true);
            list.LostMouseCapture += OnLostCapture;
        }

        private FrameworkElement ContainerOf(DependencyObject source)
        {
            DependencyObject current = source;
            while (current != null && !ReferenceEquals(current, _list))
            {
                DependencyObject parent = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
                if (parent is Panel panel && panel.IsItemsHost && current is FrameworkElement element)
                {
                    return element;
                }

                current = parent;
            }

            return null;
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _pressing = false;
            if (_dragging || !_enabled() || e.OriginalSource is not DependencyObject source)
            {
                return;
            }

            _grabbed = ContainerOf(source);
            if (_grabbed == null)
            {
                return;
            }

            _pressing = true;
            _downX = e.GetPosition(_list).X;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_pressing && !_dragging)
            {
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                if (_dragging)
                {
                    Finish(true);
                }

                _pressing = false;
                return;
            }

            double x = e.GetPosition(_list).X;
            if (!_dragging)
            {
                if (Math.Abs(x - _downX) < Math.Max(6, SystemParameters.MinimumHorizontalDragDistance))
                {
                    return;
                }

                if (!Begin())
                {
                    _pressing = false;
                    return;
                }
            }

            Update(x);
            e.Handled = true;
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragging)
            {
                Finish(true);
                e.Handled = true;
            }

            _pressing = false;
        }

        private void OnLostCapture(object sender, MouseEventArgs e)
        {
            // The mouse was taken by something else (a menu, another window): put the tabs back where they were.
            if (_dragging && !_finishing)
            {
                Finish(false);
            }
        }

        // Where each tab is seen now, from the left of the window (its transform included): the list is laid out again when it is made as wide as
        // its room, and again when it is made as it was, and the tabs came out a pixel or two from where they were (more or less by tab, on a
        // display whose tabs are a fraction of a pixel apart) - all of them jumping as one was taken. What is seen is kept, and they glide to where
        // the layout puts them.
        private double[] VisibleLefts()
        {
            int count = _list.Items.Count;
            var lefts = new double[count];
            Visual reference = Window.GetWindow(_list) ?? (Visual)_list;
            for (int i = 0; i < count; i++)
            {
                lefts[i] = double.NaN;
                if (_list.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container && container.IsLoaded)
                {
                    try
                    {
                        lefts[i] = container.TransformToAncestor(reference).Transform(new Point(0, 0)).X;
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }

            return lefts;
        }

        private double[] _roomDelta;

        // Making the list as wide as its room while a tab is carried laid the tabs out again, a pixel or two from where they were (and from where
        // they came back to), on a display whose tabs are a fraction of a pixel apart: every tab jumped as one was taken. The tab is kept in the
        // row instead, where nothing is laid out again.
        private const bool UseRoom = false;

        // How far the tab may be taken: the room the list has when it is made as wide as it can be (measured here, in the window's coordinates, and
        // the list put back at once, in the same turn, before anything is drawn - the tabs are laid out as they were, not moved), and the tab
        // is drawn beyond the list's own edges (see ReachRoom) instead of the list being made wider while it is carried.
        private ScrollContentPresenter _unclipped;

        private void MeasureRoom(out double left, out double right)
        {
            left = right = double.NaN;
            RestoreClip();
            if (_room == null || FindViewer(_list) is not ScrollViewer viewer || Window.GetWindow(_list) is not Window window)
            {
                return;
            }

            _room(true);
            try
            {
                Rect room = viewer.TransformToAncestor(window).TransformBounds(new Rect(0, 0, viewer.ViewportWidth, viewer.ActualHeight));
                left = room.Left;
                right = room.Right;
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                _room(false);
            }
        }

        private void ReachRoom(double roomLeft, double roomRight)
        {
            if (double.IsNaN(roomLeft) || double.IsNaN(roomRight) || Window.GetWindow(_list) is not Window window)
            {
                return;
            }

            try
            {
                double at = _grabbed.TransformToAncestor(window).Transform(new Point(0, 0)).X;
                _minShift = roomLeft - at;
                _maxShift = roomRight - at - _grabbed.ActualWidth;

                // The list's viewport clips what it holds, and that cannot be turned off. It is made wider by the room there is at each side
                // (its margin goes out by that much and the list's own margin comes in by the same, so the tabs stay exactly where they are,
                // in whole pixels), and the tab that is carried is seen all the way. Put back when the tab has settled (RestoreClip).
                if (FindViewer(_list) is ScrollViewer viewer && _unclipped == null)
                {
                    Rect now = viewer.TransformToAncestor(window).TransformBounds(new Rect(0, 0, viewer.ViewportWidth, viewer.ActualHeight));
                    double dpi = Math.Max(0.1, VisualTreeHelper.GetDpi(_list).DpiScaleX);
                    double extraLeft = Math.Floor(Math.Max(0, now.Left - roomLeft) * dpi) / dpi;
                    double extraRight = Math.Floor(Math.Max(0, roomRight - now.Right) * dpi) / dpi;
                    if (extraLeft > 0 || extraRight > 0)
                    {
                        _viewer = viewer;
                        _viewerMargin = viewer.ReadLocalValue(FrameworkElement.MarginProperty);
                        _listMargin = _list.ReadLocalValue(FrameworkElement.MarginProperty);
                        Thickness viewerMargin = viewer.Margin, listMargin = ((FrameworkElement)_list).Margin;
                        viewer.Margin = new Thickness(viewerMargin.Left - extraLeft, viewerMargin.Top, viewerMargin.Right - extraRight, viewerMargin.Bottom);
                        ((FrameworkElement)_list).Margin = new Thickness(listMargin.Left + extraLeft, listMargin.Top, listMargin.Right + extraRight, listMargin.Bottom);
                        _unclipped = FindChild<ScrollContentPresenter>(viewer);
                        viewer.UpdateLayout();
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private ScrollViewer _viewer;
        private object _viewerMargin, _listMargin;

        private void RestoreClip()
        {
            if (_unclipped == null || _viewer == null)
            {
                return;
            }

            if (_viewerMargin == DependencyProperty.UnsetValue)
            {
                _viewer.ClearValue(FrameworkElement.MarginProperty);
            }
            else
            {
                _viewer.Margin = (Thickness)_viewerMargin;
            }

            if (_listMargin == DependencyProperty.UnsetValue)
            {
                _list.ClearValue(FrameworkElement.MarginProperty);
            }
            else
            {
                ((FrameworkElement)_list).Margin = (Thickness)_listMargin;
            }

            _unclipped = null;
            _viewer = null;
        }

        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T found)
                {
                    return found;
                }

                if (FindChild<T>(child) is T deeper)
                {
                    return deeper;
                }
            }

            return null;
        }

        private bool Begin()
        {
            double roomLeft = double.NaN, roomRight = double.NaN;
            if (!UseRoom)
            {
                MeasureRoom(out roomLeft, out roomRight);
            }

            double[] before = UseRoom ? VisibleLefts() : Array.Empty<double>();
            if (UseRoom)
            {
                _room?.Invoke(true);
            }

            if (!BeginInner())
            {
                if (UseRoom)
                {
                    _room?.Invoke(false);
                }

                return false;
            }

            if (!UseRoom)
            {
                _roomDelta = new double[_containers.Count];
                ReachRoom(roomLeft, roomRight);
                return true;
            }

            double[] after = VisibleLefts();
            _roomDelta = new double[_containers.Count];
            for (int i = 0; i < _containers.Count && i < before.Length && i < after.Length; i++)
            {
                double delta = before[i] - after[i];
                if (!double.IsNaN(delta) && Math.Abs(delta) > 0.01 && Math.Abs(delta) < 20)
                {
                    _roomDelta[i] = delta;
                    _transforms[i].X = delta;
                    _shifts[i] = delta;
                }
            }

            return true;
        }

        private bool BeginInner()
        {
            int count = _list.Items.Count;
            var containers = new List<FrameworkElement>(count);
            for (int i = 0; i < count; i++)
            {
                if (_list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container || !container.IsLoaded)
                {
                    return false;
                }

                containers.Add(container);
            }

            int grabbed = containers.IndexOf(_grabbed);
            if (grabbed < 0 || count < 2)
            {
                return false;
            }

            _containers = containers;
            _grabbedIndex = grabbed;
            _target = grabbed;
            _item = _list.Items[grabbed];
            _lefts = new double[count];
            _widths = new double[count];
            _transforms = new TranslateTransform[count];
            _shifts = new double[count];
            _slots = new double[count];
            for (int i = 0; i < count; i++)
            {
                _lefts[i] = VisualTreeHelper.GetOffset(containers[i]).X;
                _widths[i] = containers[i].ActualWidth;
                _transforms[i] = new TranslateTransform();
                containers[i].RenderTransform = _transforms[i];
            }

            for (int i = 0; i < count; i++)
            {
                _slots[i] = i < count - 1 ? _lefts[i + 1] - _lefts[i] : _widths[i];
            }

            // The pointer is the list's from here: no tab is hovered or pressed while one is carried, and the release comes here.
            if (!Mouse.Capture(_list, CaptureMode.Element))
            {
                Restore();
                return false;
            }

            Panel.SetZIndex(_grabbed, 100);
            // (A copy of the tab drawn over the taskbar did not come out right - it was translucent, over the real tab - so the tab itself is
            // what moves; the list's viewport is made wider for it, see ReachRoom.)
            _layer = null;
            _minShift = double.NegativeInfinity;
            _maxShift = double.PositiveInfinity;
            if (FindViewer(_list) is ScrollViewer viewer)
            {
                try
                {
                    double listLeft = _list.TransformToAncestor(viewer).Transform(new Point(0, 0)).X;
                    _minShift = -(listLeft + _lefts[grabbed]);
                    _maxShift = viewer.ViewportWidth - listLeft - _lefts[grabbed] - _widths[grabbed];
                }
                catch (InvalidOperationException)
                {
                }
            }

            if (_layer != null)
            {
                try
                {
                    Point at = _grabbed.TransformToAncestor(_list).Transform(new Point(0, 0));
                    _ghostLeft = at.X;
                    BitmapSource picture = PictureOf(_grabbed);
                    if (picture != null)
                    {
                        _ghost = new Ghost(_list, picture, _grabbed.ActualWidth, _grabbed.ActualHeight, at);
                        _layer.Add(_ghost);
                        _grabbed.Opacity = 0;
                    }
                }
                catch (InvalidOperationException)
                {
                    _ghost = null;
                }
            }

            _dragging = true;
            return true;
        }

        private void Update(double pointerX)
        {
            int count = _containers.Count;
            int g = _grabbedIndex;
            double dpi = VisualTreeHelper.GetDpi(_list).DpiScaleX;
            if (dpi <= 0)
            {
                dpi = 1;
            }

            // The carried tab follows the pointer wherever it goes (its copy does); the tab itself, which is what the others go by and what slides
            // into place at the end, is kept inside the row.
            // The tab goes as far as the viewport of the list (the whole width of the bar's room), not only as far as the row of tabs, so it can be
            // taken to either end: the list is not laid out again for it (UseRoom is off), only the tab is moved.
            double rowMin = _lefts[0] - _lefts[g];
            double rowMax = _lefts[count - 1] + _widths[count - 1] - _widths[g] - _lefts[g];
            double minShift = Math.Min(rowMin, _minShift);
            double maxShift = Math.Max(rowMax, _maxShift);
            if (double.IsInfinity(minShift) || double.IsInfinity(maxShift) || double.IsNaN(minShift) || double.IsNaN(maxShift))
            {
                minShift = rowMin;
                maxShift = rowMax;
            }
            double pointerShift = pointerX - _downX;
            // The tab goes as far as the room the list has (its viewport is made that wide while it is carried, see ReachRoom).
            double shift = Math.Max(minShift, Math.Min(maxShift, pointerShift));
            _lastShift = shift;
            _transforms[g].X = Math.Round((shift + (_roomDelta != null ? _roomDelta[g] : 0)) * dpi) / dpi;
            _ghost?.MoveTo(Math.Round((_ghostLeft + shift) * dpi) / dpi);

            // Its place: after every tab whose middle it has passed.
            double middle = _lefts[g] + shift + _widths[g] / 2;
            int target = 0;
            for (int k = 0; k < count; k++)
            {
                if (k != g && _lefts[k] + _widths[k] / 2 < middle)
                {
                    target++;
                }
            }

            if (target == _target && _initialShifted)
            {
                return;
            }

            _target = target;
            _initialShifted = true;

            // The others, in the order they have, go into the places that are left (the carried tab's place is open at the target).
            double x = _lefts[0];
            int position = 0;
            for (int k = 0; k < count; k++)
            {
                if (k == g)
                {
                    continue;
                }

                if (position == target)
                {
                    x += _slots[g];
                    position++;
                }

                double wanted = x - _lefts[k];
                x += _slots[k];
                position++;
                if (Math.Abs(wanted - _shifts[k]) > 0.01)
                {
                    _shifts[k] = wanted;
                    ItemSlideAnimation.Glide(_transforms[k], wanted, dpi);
                }
            }
        }

        private bool _initialShifted;

        private void Finish(bool commit)
        {
            if (!_dragging)
            {
                return;
            }

            _finishing = true;
            _dragging = false;
            _pressing = false;
            if (_list.IsMouseCaptured)
            {
                Mouse.Capture(null);
            }

            _finishing = false;

            int g = _grabbedIndex;
            bool hadGhost = _ghost != null;
            RemoveGhost();
            // (The list is made as wide as it was once the tabs are where they go: the slide into place is measured from where they are now,
            // which does not change with the width - they are in the middle of it.)
            double[] seenBefore = UseRoom ? VisibleLefts() : Array.Empty<double>();
            if (UseRoom)
            {
                _room?.Invoke(false);
            }

            if (UseRoom && _transforms != null)
            {
                // Seen where they were seen, whatever the layout did: from there they glide to their places.
                double[] laidOut = VisibleLefts();
                for (int i = 0; i < _transforms.Length && i < seenBefore.Length && i < laidOut.Length; i++)
                {
                    double natural = laidOut[i] - _transforms[i].X;
                    double wanted = seenBefore[i] - natural;
                    if (!double.IsNaN(wanted) && Math.Abs(wanted) < 400)
                    {
                        _transforms[i].BeginAnimation(TranslateTransform.XProperty, null);
                        _transforms[i].X = wanted;
                    }
                }
            }

            bool intact = _list.Items.Count == _containers.Count;
            if (commit && intact && _target != g)
            {
                // Where every tab is seen now, for the slide into the places the new order gives them; their transforms are dropped here, in
                // the same turn, and the slide is set up before the next frame is drawn (see ItemSlideAnimation.Play).
                Dictionary<object, Point> before = ItemSlideAnimation.Capture(_list);
                Restore();
                RestoreClip();
                _commit(_item, _target <= g ? _target : _target + 1, before);
            }
            else
            {
                // Back where they were (the carried tab from where its copy was).
                double dpi = Math.Max(0.1, VisualTreeHelper.GetDpi(_list).DpiScaleX);
                if (hadGhost && _transforms != null && g < _transforms.Length)
                {
                    _transforms[g].X = Math.Round(_lastShift * dpi) / dpi;
                }

                for (int i = 0; i < _containers.Count; i++)
                {
                    FrameworkElement container = _containers[i];
                    TranslateTransform transform = _transforms[i];
                    bool carried = i == g;
                    ItemSlideAnimation.Glide(transform, 0, dpi, () =>
                    {
                        if (carried)
                        {
                            RestoreClip();
                        }

                        if (ReferenceEquals(container.RenderTransform, transform))
                        {
                            container.RenderTransform = null;
                        }

                        Panel.SetZIndex(container, 0);
                    });
                }
            }

            _grabbed = null;
            _item = null;
            _initialShifted = false;
        }

        private static ScrollViewer FindViewer(DependencyObject from)
        {
            for (DependencyObject current = from; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is ScrollViewer viewer)
                {
                    return viewer;
                }
            }

            return null;
        }

        private void RemoveGhost()
        {
            if (_ghost != null)
            {
                _layer?.Remove(_ghost);
                _ghost = null;
            }

            _layer = null;
            if (_grabbed != null)
            {
                _grabbed.Opacity = 1;
            }
        }

        /// <summary>A picture of the tab as it is now (a live copy of it was hidden with the tab, which is hidden while it is carried).</summary>
        private static BitmapSource PictureOf(FrameworkElement tab)
        {
            double dpi = Math.Max(0.1, VisualTreeHelper.GetDpi(tab).DpiScaleX);
            double width = tab.ActualWidth, height = tab.ActualHeight;
            int pixelWidth = (int)Math.Ceiling(width * dpi), pixelHeight = (int)Math.Ceiling(height * dpi);
            if (pixelWidth <= 0 || pixelHeight <= 0)
            {
                return null;
            }

            var drawing = new DrawingVisual();
            using (DrawingContext context = drawing.RenderOpen())
            {
                context.DrawRectangle(new VisualBrush(tab) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>The tab that is carried, as a picture drawn over the taskbar (an adorner is not cut at the edges of the list).</summary>
        private sealed class Ghost : Adorner
        {
            private readonly Image _visual;
            private Point _at;

            public Ghost(UIElement adorned, BitmapSource picture, double width, double height, Point at) : base(adorned)
            {
                IsHitTestVisible = false;
                _at = at;
                _visual = new Image
                {
                    Source = picture,
                    Width = width,
                    Height = height,
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false,
                    SnapsToDevicePixels = true,
                };
                RenderOptions.SetBitmapScalingMode(_visual, BitmapScalingMode.NearestNeighbor);
                AddVisualChild(_visual);
            }

            public void MoveTo(double x)
            {
                _at = new Point(x, _at.Y);
                InvalidateArrange();
            }

            protected override int VisualChildrenCount => 1;

            protected override Visual GetVisualChild(int index) => _visual;

            protected override Size MeasureOverride(Size constraint)
            {
                _visual.Measure(new Size(_visual.Width, _visual.Height));
                return new Size(_visual.Width, _visual.Height);
            }

            protected override Size ArrangeOverride(Size finalSize)
            {
                _visual.Arrange(new Rect(_at, new Size(_visual.Width, _visual.Height)));
                return finalSize;
            }
        }

        private void Restore()
        {
            if (_containers == null)
            {
                return;
            }

            for (int i = 0; i < _containers.Count; i++)
            {
                if (ReferenceEquals(_containers[i].RenderTransform, _transforms?[i]))
                {
                    _containers[i].RenderTransform = null;
                }

                Panel.SetZIndex(_containers[i], 0);
            }
        }
    }
}
