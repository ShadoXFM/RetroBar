using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace RetroBar.Utilities
{
    /// <summary>
    /// The light a Windows 7 taskbar button gives under the pointer: its glass takes the colour of the button's icon (the colour most of the
    /// icon is: red for Chrome, yellow for a folder), with a bright glow at the bottom edge that follows the pointer along the button; when
    /// the pointer leaves, the colour goes slowly. A program that starts has its button come up in that colour at once and settle (Launch).
    ///
    ///   <Border utilities:HotTrackGlow.Enabled="True" Opacity="0" IsHitTestVisible="False" />
    ///
    /// The border is in the template of a button (it is drawn over the button's glass and under its icon). Its Opacity is driven here.
    /// </summary>
    public static class HotTrackGlow
    {
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(HotTrackGlow), new PropertyMetadata(false, OnEnabledChanged));

        public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
        public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

        /// <summary>A colour the light always has, instead of the icon's (the template of a tab that flashes for attention: the amber of its glass).</summary>
        public static readonly DependencyProperty FixedColorProperty = DependencyProperty.RegisterAttached(
            "FixedColor", typeof(Color?), typeof(HotTrackGlow), new PropertyMetadata(null));

        public static Color? GetFixedColor(DependencyObject obj) => (Color?)obj.GetValue(FixedColorProperty);
        public static void SetFixedColor(DependencyObject obj, Color? value) => obj.SetValue(FixedColorProperty, value);

        private static readonly ConditionalWeakTable<ImageSource, object> ColorCache = new();
        private static readonly ConditionalWeakTable<Border, Action<DateTime>> Launches = new();
        private static readonly ConditionalWeakTable<Border, object> PendingLaunches = new();
        private static readonly ConditionalWeakTable<Border, Action> Resubscribers = new();

        private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Border glow || e.NewValue is not true)
            {
                return;
            }

            glow.Loaded += (s, args) => Attach(glow);
        }

        /// <summary>
        /// The button comes up in the colour of its icon, as the button of a program that has just been started does in Windows 7.
        /// <paramref name="started"/>: when the coming up began (a button whose template was changed for another one, as the tab of a window
        /// that becomes the active one is, goes on from where it was in the new template).
        /// </summary>
        public static void Launch(Border glow, DateTime? started = null)
        {
            if (glow == null)
            {
                return;
            }

            DateTime at = started ?? DateTime.UtcNow;
            if (Launches.TryGetValue(glow, out Action<DateTime> launch))
            {
                launch(at);
            }
            else
            {
                // The light is not made yet (it is when the button's template is loaded, which can come after this): it comes up when it is.
                PendingLaunches.AddOrUpdate(glow, at);
            }
        }

        private static Color Lighter(Color color, double amount) =>
            Color.FromRgb((byte)(color.R + (255 - color.R) * amount), (byte)(color.G + (255 - color.G) * amount), (byte)(color.B + (255 - color.B) * amount));

        private static void Attach(Border glow)
        {
            if (glow.Tag is "attached")
            {
                // Back in the tree after having left it: its handlers (taken off when it left) go back on the button.
                if (Resubscribers.TryGetValue(glow, out Action resubscribe))
                {
                    resubscribe();
                }

                return;
            }

            if (glow.TemplatedParent is not ButtonBase button)
            {
                return;
            }

            glow.Tag = "attached";

            // The glass, in the colour of the icon: over the button's own glass (which is grey), under the icon.
            var tint = new Border
            {
                IsHitTestVisible = false,
                Opacity = 0,
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(2),
            };
            // Strongest under the pointer, fading to the sides and the top (it follows the pointer along the button, as the light does).
            var tintBrush = new RadialGradientBrush { RadiusX = 0.55, RadiusY = 0.9, Center = new Point(0.5, 0.85), GradientOrigin = new Point(0.5, 0.85) };
            tintBrush.GradientStops.Add(new GradientStop(Color.FromArgb(130, 255, 255, 255), 0));
            tintBrush.GradientStops.Add(new GradientStop(Color.FromArgb(70, 255, 255, 255), 0.55));
            tintBrush.GradientStops.Add(new GradientStop(Color.FromArgb(12, 255, 255, 255), 1));
            tint.Background = tintBrush;

            // The colour of a program that has just started: stronger than the one under the pointer, over the bright glass of the new tab so
            // that the glass is seen in the colour from its first moment, and going on its own whether or not the pointer is over it yet.
            var flash = new Border
            {
                IsHitTestVisible = false,
                Opacity = 0,
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(2),
            };
            var flashBrush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            flashBrush.GradientStops.Add(new GradientStop(Color.FromArgb(120, 255, 255, 255), 0));
            flashBrush.GradientStops.Add(new GradientStop(Color.FromArgb(165, 255, 255, 255), 0.55));
            flashBrush.GradientStops.Add(new GradientStop(Color.FromArgb(135, 255, 255, 255), 1));
            flash.Background = flashBrush;

            // The light at the bottom edge: a bright half ellipse that fades upwards and to the sides.
            var brush = new RadialGradientBrush { RadiusX = 0.9, RadiusY = 0.95, Center = new Point(0.5, 1.0), GradientOrigin = new Point(0.5, 1.0) };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(140, 255, 255, 255), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(95, 255, 255, 255), 0.4));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
            glow.Background = brush;
            if (glow.Parent is Panel tintPanel)
            {
                tintPanel.Children.Insert(tintPanel.Children.IndexOf(glow), tint);
                tintPanel.Children.Insert(tintPanel.Children.IndexOf(glow), flash);
            }

            void Recolor()
            {
                Color color = GetFixedColor(glow) ?? IconColor(button);
                Color light = Lighter(color, 0.12);
                // Under the pointer the glass is only a little in the colour (the grey glass of the tab shows, as in Windows 7): the colour is
                // mostly the light at the bottom edge.
                tintBrush.GradientStops[0].Color = Color.FromArgb(160, color.R, color.G, color.B);
                tintBrush.GradientStops[1].Color = Color.FromArgb(92, color.R, color.G, color.B);
                tintBrush.GradientStops[2].Color = Color.FromArgb(18, color.R, color.G, color.B);
                flashBrush.GradientStops[0].Color = Color.FromArgb(120, color.R, color.G, color.B);
                flashBrush.GradientStops[1].Color = Color.FromArgb(165, color.R, color.G, color.B);
                flashBrush.GradientStops[2].Color = Color.FromArgb(135, color.R, color.G, color.B);
                brush.GradientStops[0].Color = Color.FromArgb(140, 255, 255, 255);
                brush.GradientStops[1].Color = Color.FromArgb(135, light.R, light.G, light.B);
                brush.GradientStops[2].Color = Color.FromArgb(0, light.R, light.G, light.B);
            }

            void Follow(MouseEventArgs args)
            {
                double width = glow.ActualWidth > 0 ? glow.ActualWidth : button.ActualWidth;
                if (width <= 0)
                {
                    return;
                }

                double x = Math.Max(0, Math.Min(1, args.GetPosition(glow).X / width));
                brush.Center = new Point(x, 1.0);
                brush.GradientOrigin = new Point(x, 1.0);
                tintBrush.Center = new Point(x, 0.85);
                tintBrush.GradientOrigin = new Point(x, 0.85);
            }

            // Whether the light is up for the pointer. While the pointer is on the tab's preview, that window takes the mouse input and WPF
            // never hears the pointer leave the tab (no MouseLeave, and no MouseEnter when it comes back): the cursor is read from the system
            // while the light is up, and the light goes when it is not over the tab.
            bool hot = false;
            var watch = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(60) };

            void GoUp(MouseEventArgs args)
            {
                hot = true;
                Recolor();
                if (args != null)
                {
                    Follow(args);
                }

                tint.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.1)));
                glow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.1)));
                watch.Start();
            }

            void GoDown()
            {
                hot = false;
                watch.Stop();

                // The colour goes in a moment.
                tint.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(0.22)));
                glow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(0.16)));
            }

            watch.Tick += (s, args) =>
            {
                if (!button.IsVisible || !CursorOver(button))
                {
                    GoDown();
                    // WPF can go on thinking the pointer is over the button (its own hover look stays too) when it left while another window
                    // took the mouse (the preview, or another program): it is made to look again.
                    if (button.IsMouseOver)
                    {
                        Mouse.Synchronize();
                    }
                }
            };

            // The handlers sit on the button, which outlives the template this light is in: a tab whose template is made again (the tab of a
            // window that becomes the active one, or stops being) would keep every light it ever had - each with its whole tree - and run all
            // their handlers at every move of the pointer. They are taken off when the light leaves the tree, and put back if it returns.
            MouseEventHandler onEnter = (s, args) => GoUp(args);
            MouseEventHandler onMove = (s, args) =>
            {
                if (!hot)
                {
                    // Back on the tab from its preview: no MouseEnter comes.
                    GoUp(args);
                    return;
                }

                Follow(args);
            };
            MouseEventHandler onLeave = (s, args) => GoDown();

            void Subscribe()
            {
                button.MouseEnter -= onEnter;
                button.MouseMove -= onMove;
                button.MouseLeave -= onLeave;
                button.MouseEnter += onEnter;
                button.MouseMove += onMove;
                button.MouseLeave += onLeave;
                // A button whose template was just made again under the pointer: nothing tells this light when the pointer goes, so it looks.
                if (button.IsMouseOver)
                {
                    watch.Start();
                }
            }

            void Unsubscribe()
            {
                button.MouseEnter -= onEnter;
                button.MouseMove -= onMove;
                button.MouseLeave -= onLeave;
                hot = false;
                watch.Stop();
            }

            Subscribe();
            glow.Unloaded += (s, args) => Unsubscribe();
            Resubscribers.AddOrUpdate(glow, Subscribe);
            static bool PointerInside(FrameworkElement element)
            {
                // (IsMouseOver is not up to date for a button that has just come under the pointer.)
                try
                {
                    Point p = Mouse.GetPosition(element);
                    return p.X >= 0 && p.Y >= 0 && p.X < element.ActualWidth && p.Y < element.ActualHeight;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }

            Action<DateTime> launchNow = started =>
            {
                // The glass comes up in the colour (the light at the bottom with it), holds a moment, and settles to what it is when the
                // pointer is on it, or goes if the pointer is not.
                Recolor();
                bool over = button.IsMouseOver || PointerInside(button);
                TimeSpan elapsed = DateTime.UtcNow - started;
                if (elapsed > TimeSpan.FromMilliseconds(1800))
                {
                    return;
                }

                double offset = elapsed > TimeSpan.Zero ? elapsed.TotalMilliseconds : 0;
                // Slowly in (it starts gently and does not reach the colour in a few frames), a moment of it, then easing out. The time counted is the
                // time that is drawn (see StallProofAnimation): the first part of it was lost to the thread being busy with a program that was starting.
                var rise = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
                var fall = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
                flash.BeginAnimation(UIElement.OpacityProperty, new StallProofAnimation(new (double, double, IEasingFunction)[]
                {
                    (0, 0, null), (480, 1, rise), (720, 1, null), (1700, 0, fall),
                }, offset));
                glow.BeginAnimation(UIElement.OpacityProperty, new StallProofAnimation(new (double, double, IEasingFunction)[]
                {
                    (0, 0, null), (480, 1, rise), (680, 1, null), (1400, over ? 1 : 0, fall),
                }, offset)
                {
                    FillBehavior = over ? FillBehavior.HoldEnd : FillBehavior.Stop,
                });

                // The icon may not be there yet at the first instant (its colour is read from it, and without it the colour is a pale one that is
                // hardly seen): read again for a while as the colour comes up.
                int rereads = 0;
                var reread = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                reread.Tick += (s, args) =>
                {
                    Recolor();
                    if (++rereads >= 20)
                    {
                        reread.Stop();
                    }
                };
                reread.Start();

                if (over)
                {
                    // The light stays up while the pointer is on it: the cursor is watched (no MouseLeave comes if the pointer goes onto the
                    // preview, and the light stayed on the tab for good).
                    hot = true;
                    watch.Start();
                    tint.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(800)));

                    // If the pointer has gone by the time it has settled, the colour goes (nothing else would tell it to).
                    var check = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1900) };
                    check.Tick += (s, args) =>
                    {
                        check.Stop();
                        if (!button.IsMouseOver && !PointerInside(button))
                        {
                            tint.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(0.22)));
                            glow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(0.16)));
                        }
                    };
                    check.Start();
                }
            };

            Launches.AddOrUpdate(glow, launchNow);
            if (PendingLaunches.TryGetValue(glow, out object pending))
            {
                PendingLaunches.Remove(glow);
                launchNow(pending is DateTime at ? at : DateTime.UtcNow);
            }
        }

        // Whether the system's cursor is over the button, by its screen rectangle (a few pixels more below it: its clickable area reaches the
        // screen's edge).
        private static bool CursorOver(FrameworkElement button)
        {
            if (!GetCursorPos(out POINT cursor) || PresentationSource.FromVisual(button) == null)
            {
                return false;
            }

            try
            {
                Point topLeft = button.PointToScreen(new Point(0, 0));
                Point bottomRight = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
                double extra = 4 * VisualTreeHelper.GetDpi(button).DpiScaleY;
                return cursor.X >= topLeft.X && cursor.X < bottomRight.X && cursor.Y >= topLeft.Y && cursor.Y < bottomRight.Y + extra;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT point);

        // The colour the button's icon is: what most of it is (the hue that most of its seen pixels have), kept bright and strong enough to
        // show as the colour of glass. Of the icon the button shows, not the window's own small one that a tab keeps hidden under it.
        private static Color IconColor(DependencyObject button)
        {
            Image best = null;
            double bestArea = 0;

            // A tab shows its icon in an image of its own over the button, not in the button's template: that one first.
            DependencyObject up = button;
            while (up != null && up is not RetroBar.Controls.TaskButton)
            {
                up = VisualTreeHelper.GetParent(up);
            }

            if (up is RetroBar.Controls.TaskButton tab && tab.IconOnlyImage.Source != null && tab.IconOnlyImage.Visibility == Visibility.Visible)
            {
                best = tab.IconOnlyImage;
            }
            else
            {
                Find(button, ref best, ref bestArea);
            }

            if (best?.Source is not BitmapSource source)
            {
                return Color.FromRgb(180, 200, 255);
            }

            if (ColorCache.TryGetValue(source, out object cached) && cached is Color known)
            {
                return known;
            }

            Color color = Dominant(source);
            try
            {
                ColorCache.Add(source, color);
            }
            catch (ArgumentException)
            {
            }

            return color;
        }

        private static void Find(DependencyObject parent, ref Image best, ref double bestArea)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                // The icon the button shows: not the window's own small icon (the one of its title bar), which a tab keeps under the one it
                // shows, hidden - the colour of the light is the colour of what is seen on the taskbar.
                if (child is Image image && image.Source != null && image.IsVisible && image.Opacity > 0.01)
                {
                    double area = image.ActualWidth * image.ActualHeight;
                    if (area > bestArea)
                    {
                        best = image;
                        bestArea = area;
                    }
                }

                Find(child, ref best, ref bestArea);
            }
        }

        private static Color Dominant(BitmapSource source)
        {
            try
            {
                var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                int width = converted.PixelWidth, height = converted.PixelHeight;
                int stride = width * 4;
                var pixels = new byte[stride * height];
                converted.CopyPixels(pixels, stride, 0);

                const int bins = 24;
                var weight = new double[bins];
                var sumR = new double[bins];
                var sumG = new double[bins];
                var sumB = new double[bins];
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    double a = pixels[i + 3] / 255.0;
                    double pr = pixels[i + 2], pg = pixels[i + 1], pb = pixels[i];
                    double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                    // Not what is clear, nor dark (an outline), nor grey.
                    if (a < 0.5 || max < 70 || max <= 0)
                    {
                        continue;
                    }

                    double saturation = (max - min) / max;
                    if (saturation < 0.18)
                    {
                        continue;
                    }

                    double delta = max - min;
                    double hue;
                    if (max == pr) { hue = ((pg - pb) / delta) % 6; }
                    else if (max == pg) { hue = (pb - pr) / delta + 2; }
                    else { hue = (pr - pg) / delta + 4; }

                    hue *= 60;
                    if (hue < 0) { hue += 360; }

                    int bin = (int)(hue / (360.0 / bins)) % bins;
                    // By how much of the icon it is (a little more for a stronger colour).
                    double w = a * Math.Sqrt(saturation);
                    weight[bin] += w;
                    sumR[bin] += pr * w;
                    sumG[bin] += pg * w;
                    sumB[bin] += pb * w;
                }

                // The hue with the most of the icon, together with the bins on each side of it.
                int peak = 0;
                double peakWeight = 0;
                for (int b = 0; b < bins; b++)
                {
                    double around = weight[b] + weight[(b + 1) % bins] + weight[(b + bins - 1) % bins];
                    if (around > peakWeight)
                    {
                        peakWeight = around;
                        peak = b;
                    }
                }

                if (peakWeight <= 0)
                {
                    return Color.FromRgb(190, 200, 215);
                }

                double r = 0, g = 0, bl = 0, total = 0;
                for (int d = -1; d <= 1; d++)
                {
                    int b = (peak + d + bins) % bins;
                    r += sumR[b];
                    g += sumG[b];
                    bl += sumB[b];
                    total += weight[b];
                }

                r /= total; g /= total; bl /= total;
                double peakValue = Math.Max(r, Math.Max(g, bl));
                if (peakValue > 0 && peakValue < 215)
                {
                    double lift = 215 / peakValue;
                    r = Math.Min(255, r * lift); g = Math.Min(255, g * lift); bl = Math.Min(255, bl * lift);
                }

                return Color.FromRgb((byte)r, (byte)g, (byte)bl);
            }
            catch (Exception)
            {
                return Color.FromRgb(180, 200, 255);
            }
        }
    }
}
