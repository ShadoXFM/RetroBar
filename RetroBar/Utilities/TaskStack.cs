using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The stack of a tab that stands for several windows of one program, as in Windows 7: the glass face is a little narrower, and one
    /// or two more tabs show behind it, at its right. A theme's tab template that wants it has a panel named FaceHost (the face and
    /// everything drawn on it) and, in the tab's grid before it, panels named StackLayer1 and StackLayer2, each the frame and the glass of
    /// a tab (they are kept as the face is: its state, the glass under the pointer, the colour of its icon); a template without them is
    /// left as it is. A change in the number of layers is a short animation: the face shrinks to the left and the layers come out from
    /// behind it.
    /// </summary>
    public static class TaskStack
    {
        public static readonly DependencyProperty LayersProperty = DependencyProperty.RegisterAttached(
            "Layers", typeof(int), typeof(TaskStack), new PropertyMetadata(0));

        public static int GetLayers(DependencyObject element) => (int)element.GetValue(LayersProperty);

        private static readonly DependencyProperty AppliedProperty = DependencyProperty.RegisterAttached(
            "Applied", typeof(int), typeof(TaskStack), new PropertyMetadata(-1));

        private static readonly DependencyProperty RunProperty = DependencyProperty.RegisterAttached(
            "Run", typeof(RunState), typeof(TaskStack), new PropertyMetadata(null));

        // A change that is being animated: what to do to end it at once, the number of layers it started from, and what is to be done when it is over.
        private sealed class RunState
        {
            public Action<bool> Finish;
            public int From;
            public Action Done;
            public DateTime Started;
        }

        private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
            "Hooked", typeof(bool), typeof(TaskStack), new PropertyMetadata(false));

        private static readonly System.ComponentModel.DependencyPropertyDescriptor TemplateDescriptor =
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Control.TemplateProperty, typeof(Control));

        /// <summary>Whether the tab's face and layers are moving to their new places: what is read from how the tab is drawn (where its icon goes) waits.</summary>
        public static bool IsAnimating(DependencyObject element) => element.GetValue(RunProperty) != null;

        // Each layer shows this many device pixels of itself (its right edge, as Windows 7 draws them), whatever the scale.
        private const int StripPixels = 3;

        // How opaque the first and the second layer are: only just noticeably less than the face, and the second a little less than the first.
        private static readonly double[] LayerOpacity = { 0.85, 0.62 };

        public static readonly Duration Time = new Duration(TimeSpan.FromMilliseconds(280));

        // The same curve for the face, the layers and the icon, so they arrive together: it starts fast and settles gently.
        public static IEasingFunction Ease { get; } = MakeEase();

        private static IEasingFunction MakeEase()
        {
            var ease = new PowerEase { Power = 3.5, EasingMode = EasingMode.EaseOut };
            ease.Freeze();
            return ease;
        }

        /// <summary>
        /// Gives the tab this many layers (0 to 2). <paramref name="moveIcon"/> is told how far (DIPs) the middle of the face moves, when it
        /// is animated, and <paramref name="done"/> is called when the tab has settled (also at once if there is no animation).
        /// </summary>
        public static void Set(Button button, int layers, Action<double> moveIcon = null, Action done = null)
        {
            layers = Math.Max(0, Math.Min(2, layers));
            button.SetValue(LayersProperty, layers);
            Refresh(button, true, moveIcon, done, TimeSpan.Zero);
        }

        public static void Refresh(Button button) => Refresh(button, false, null, null, TimeSpan.Zero);

        private static FrameworkElement Part(Button button, string name) => button.Template?.FindName(name, button) as FrameworkElement;

        private static void Refresh(Button button, bool animate, Action<double> moveIcon, Action done, TimeSpan elapsed)
        {
            if (button == null)
            {
                return;
            }

            button.ApplyTemplate();
            FrameworkElement face = Part(button, "FaceHost");
            FrameworkElement[] layerParts = { Part(button, "StackLayer1"), Part(button, "StackLayer2") };
            if (face == null || layerParts[0] == null || layerParts[1] == null)
            {
                return;
            }

            if (!(bool)button.GetValue(HookedProperty))
            {
                button.SetValue(HookedProperty, true);
                // (A tab put back brings the layers back as they were; and the tab of a window that becomes the active one, or stops being, has
                // another template: its layers are made again, and the change that was on its way goes on in it.)
                button.Loaded += (s, e) => Refresh(button);
                TemplateDescriptor.AddValueChanged(button, (s, e) => button.Dispatcher.BeginInvoke(new Action(() => TemplateChanged(button)), DispatcherPriority.Loaded));
            }

            if (!(bool)layerParts[0].GetValue(HookedProperty))
            {
                foreach (FrameworkElement part in layerParts)
                {
                    FrameworkElement hooked = part;
                    hooked.SetValue(HookedProperty, true);
                    hooked.SizeChanged += (s, e) => ClipLayer(hooked);
                }
            }

            int layers = Math.Max(0, Math.Min(2, GetLayers(button)));
            int before = (int)button.GetValue(AppliedProperty);

            if (button.GetValue(RunProperty) is RunState running)
            {
                if (before == layers)
                {
                    return;
                }

                running.Finish(false);
            }

            double scale = VisualTreeHelper.GetDpi(button).DpiScaleX;
            double pixel = 1 / (scale > 0 ? scale : 1);
            double pitch = StripPixels * pixel;

            // The face is narrower by the layers (and the pixel the frame's bright edge takes), so everything drawn on it - the frame, the glass
            // under the pointer, the icon - ends where the layers begin; the layers end where the frame would have.
            double marginBefore = before <= 0 ? 0 : before * pitch + pixel;
            double margin = layers == 0 ? 0 : layers * pitch + pixel;
            // (As wide as it is laid out now, with the margin it has now; a template that has just been put on the tab is not laid out yet, and the
            // tab's own width is what it will have.)
            double fullWidth = face.ActualWidth > 0 ? face.ActualWidth + face.Margin.Right : button.ActualWidth;
            bool run = animate && before >= 0 && before != layers && button.IsLoaded && fullWidth > 0;
            double faceWidthBefore = fullWidth - marginBefore;
            face.Margin = new Thickness(0, 0, margin, 0);

            for (int i = 1; i <= 2; i++)
            {
                FrameworkElement layer = layerParts[i - 1];
                // (Each layer a very little more see-through than the one in front of it.)
                layer.Opacity = LayerOpacity[i - 1];
                if (i <= layers)
                {
                    layer.Margin = new Thickness(0, 0, margin - i * pitch, 0);
                    layer.Visibility = Visibility.Visible;
                }
                else if (run && i <= before)
                {
                    // Going away: stays where it was while it fades.
                    layer.Margin = new Thickness(0, 0, marginBefore - i * pitch, 0);
                    layer.Visibility = Visibility.Visible;
                }
                else
                {
                    layer.Visibility = Visibility.Collapsed;
                }
            }

            button.SetValue(AppliedProperty, layers);
            SyncGlass(face, layerParts);
            // (The colour of the icon and the light under the pointer are made when the tab's glass is loaded: the layers copy them after.)
            button.Dispatcher.BeginInvoke(new Action(() => SyncGlass(face, layerParts)), DispatcherPriority.Loaded);

            if (!run)
            {
                ClipLayer(layerParts[0]);
                ClipLayer(layerParts[1]);
                done?.Invoke();
                return;
            }

            double shift = margin - marginBefore;
            double newWidth = faceWidthBefore + marginBefore - margin;
            if (newWidth <= 0)
            {
                done?.Invoke();
                return;
            }

            bool finished = false;

            void Finish(bool callDone)
            {
                if (finished)
                {
                    return;
                }

                finished = true;
                face.ClearValue(UIElement.RenderTransformProperty);
                for (int i = 1; i <= 2; i++)
                {
                    FrameworkElement layer = layerParts[i - 1];
                    layer.BeginAnimation(UIElement.OpacityProperty, null);
                    layer.ClearValue(UIElement.RenderTransformProperty);
                    if (i > layers)
                    {
                        layer.Visibility = Visibility.Collapsed;
                    }
                }

                button.ClearValue(RunProperty);
                if (callDone)
                {
                    done?.Invoke();
                }
            }

            button.SetValue(RunProperty, new RunState { Finish = Finish, From = before, Done = done, Started = DateTime.UtcNow - elapsed });

            // The face: as wide as it was, narrowing to what it is now.
            double from = faceWidthBefore / newWidth;
            var faceScale = new ScaleTransform(from, 1);
            face.RenderTransformOrigin = new Point(0, 0);
            face.RenderTransform = faceScale;
            var narrow = new DoubleAnimation(from, 1, Time) { EasingFunction = Ease, BeginTime = -elapsed };
            narrow.Completed += (s, e) => Finish(true);
            faceScale.BeginAnimation(ScaleTransform.ScaleXProperty, narrow);

            for (int i = 1; i <= 2; i++)
            {
                FrameworkElement layer = layerParts[i - 1];
                if (layer.Visibility != Visibility.Visible)
                {
                    continue;
                }

                ClipLayer(layer);
                if (i > layers)
                {
                    // Going away: it stays as it is, whole, while the face grows over it, and goes in the last of it - gone by the end, so that nothing
                    // of it (a line of its edge, seen through the glass) is left to disappear all at once when it is taken off.
                    var fade = new DoubleAnimationUsingKeyFrames { BeginTime = -elapsed, Duration = Time };
                    fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(LayerOpacity[i - 1], KeyTime.FromPercent(0)));
                    fade.KeyFrames.Add(new LinearDoubleKeyFrame(LayerOpacity[i - 1], KeyTime.FromPercent(0.5)));
                    fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                    layer.BeginAnimation(UIElement.OpacityProperty, fade);
                    continue;
                }

                var move = new TranslateTransform(shift, 0);
                layer.RenderTransform = move;
                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(shift, 0, Time) { EasingFunction = Ease, BeginTime = -elapsed });
                if (i > before)
                {
                    layer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, LayerOpacity[i - 1], Time) { EasingFunction = Ease, BeginTime = -elapsed });
                }
            }

            moveIcon?.Invoke(-shift / 2);
        }

        private static void TemplateChanged(Button button)
        {
            if (button.GetValue(RunProperty) is RunState run)
            {
                // The change goes on, from the start, in the new template (the icon's own movement is not made again).
                // (From where it had got to, so the face, the layers and the icon, which goes on on its own, stay together.)
                TimeSpan elapsed = DateTime.UtcNow - run.Started;
                run.Finish(false);
                button.SetValue(AppliedProperty, run.From);
                Refresh(button, true, null, run.Done, elapsed);
            }
            else
            {
                Refresh(button);
            }
        }

        // Only the right edge of a layer is seen (what the face, in front, does not cover): its right StripPixels, counted from where its frame is
        // drawn (a monitor's TaskFrame adjustment can make that a little short of the layer's edge).
        private static void ClipLayer(FrameworkElement layer)
        {
            if (layer.ActualWidth <= 0 || layer.ActualHeight <= 0)
            {
                return;
            }

            double scale = VisualTreeHelper.GetDpi(layer).DpiScaleX;
            double pitch = StripPixels / (scale > 0 ? scale : 1);
            double inset = 0;
            try
            {
                if (layer is Panel panel && panel.Children.Count > 0 && panel.Children[0] is FrameworkElement frame && frame.ActualWidth > 0)
                {
                    Rect bounds = frame.TransformToAncestor(layer).TransformBounds(new Rect(0, 0, frame.ActualWidth, frame.ActualHeight));
                    inset = Math.Max(0, Math.Min(4, layer.ActualWidth - bounds.Right));
                }
            }
            catch (InvalidOperationException)
            {
            }

            double left = Math.Max(0, layer.ActualWidth - inset - pitch);
            var clip = new RectangleGeometry(new Rect(left, 0, layer.ActualWidth - left, layer.ActualHeight));
            clip.Freeze();
            layer.Clip = clip;
        }

        // The layers have what the face has over its frame: a border for each of the glows put there (the tint and flash of the icon's colour, the
        // light at the bottom, which follows the pointer), showing what those show.
        private static void SyncGlass(FrameworkElement face, FrameworkElement[] layers)
        {
            if (face is not Panel facePanel)
            {
                return;
            }

            var sources = new List<Border>();
            foreach (UIElement child in facePanel.Children)
            {
                if (child is Border border && border.Background != null)
                {
                    sources.Add(border);
                }
            }

            foreach (FrameworkElement layer in layers)
            {
                if (layer is not Panel panel || panel.Tag is int count && count == sources.Count)
                {
                    continue;
                }

                // (The first two are the frame and the glass under the pointer, from the template.)
                while (panel.Children.Count > 2)
                {
                    panel.Children.RemoveAt(2);
                }

                foreach (Border source in sources)
                {
                    var mirror = new Border { IsHitTestVisible = false, Margin = source.Margin, CornerRadius = source.CornerRadius };
                    BindingOperations.SetBinding(mirror, Border.BackgroundProperty, new Binding(nameof(Border.Background)) { Source = source });
                    BindingOperations.SetBinding(mirror, UIElement.OpacityProperty, new Binding(nameof(UIElement.Opacity)) { Source = source });
                    panel.Children.Add(mirror);
                }

                panel.Tag = sources.Count;
            }
        }
    }
}
