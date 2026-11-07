using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RetroBar.Controls
{
    // A thin bar along the bottom of a tab for the progress a window reports to the taskbar (a download, a copy, an installer: what
    // Windows draws on the taskbar button). The tab shows the progress of the first of its windows that reports any. Green while it
    // goes on, yellow when paused, red on an error, and a block sliding to and fro when it has no value. In the Windows 7 style (icon-only
    // tabs) it is what Windows 7 draws: the glass of the tab fills with colour from the left, as far as the progress has come.
    public partial class TaskButton
    {
        private const double ProgressMax = 65534.0;

        private static readonly Brush ProgressNormal = Frozen(Color.FromRgb(0x06, 0xB0, 0x25));
        private static readonly Brush ProgressPaused = Frozen(Color.FromRgb(0xF6, 0xC0, 0x00));
        private static readonly Brush ProgressError = Frozen(Color.FromRgb(0xDA, 0x00, 0x00));

        // The fill of the glass in the Windows 7 style: light at the top, deeper in the lower half, a light edge at the bottom.
        private static readonly Brush GlassNormal = GlassFill(Color.FromRgb(0x9B, 0xF0, 0x7A), Color.FromRgb(0x3C, 0xC8, 0x3A), Color.FromRgb(0x14, 0xA8, 0x26), Color.FromRgb(0x4A, 0xDC, 0x4C));
        private static readonly Brush GlassPaused = GlassFill(Color.FromRgb(0xFF, 0xF0, 0x8A), Color.FromRgb(0xF2, 0xD2, 0x2C), Color.FromRgb(0xD2, 0xA8, 0x00), Color.FromRgb(0xF4, 0xD8, 0x3C));
        private static readonly Brush GlassError = GlassFill(Color.FromRgb(0xF4, 0x9A, 0x8C), Color.FromRgb(0xE0, 0x44, 0x34), Color.FromRgb(0xB8, 0x14, 0x10), Color.FromRgb(0xE2, 0x56, 0x44));

        private static Brush GlassFill(Color top, Color upper, Color lower, Color bottom)
        {
            const byte alpha = 0xC8;
            var brush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, top.R, top.G, top.B), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, upper.R, upper.G, upper.B), 0.5));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, lower.R, lower.G, lower.B), 0.52));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, bottom.R, bottom.G, bottom.B), 1));
            brush.Freeze();
            return brush;
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        // Windows that report only a value (the copy dialog of Explorer calls SetProgressValue alone and never sets a state, which then stays
        // "no progress"): that is normal progress, as the documentation of SetProgressValue says. Such a window counts while its value is
        // strictly between nothing and everything; an explicit state, or the value going back to 0 or to the end, takes it out again.
        private readonly System.Collections.Generic.HashSet<ApplicationWindow> _valueOnlyProgress = new();

        private void NoteProgressEvent(ApplicationWindow window, string property)
        {
            if (property == "ProgressState")
            {
                _valueOnlyProgress.Remove(window);
            }
            else if (property == "ProgressValue")
            {
                if (window.ProgressState == NativeMethods.TBPFLAG.TBPF_NOPROGRESS && window.ProgressValue > 0 && window.ProgressValue < ProgressMax)
                {
                    _valueOnlyProgress.Add(window);
                }
                else
                {
                    _valueOnlyProgress.Remove(window);
                }
            }
        }

        private bool ReportsProgress(ApplicationWindow window) =>
            window != null && (window.ProgressState != NativeMethods.TBPFLAG.TBPF_NOPROGRESS || _valueOnlyProgress.Contains(window));

        private TranslateTransform _progressSlide;
        private bool _progressSliding;

        private void InitProgressBar()
        {
            AppButton.SizeChanged += (s, e) => UpdateProgressBar();
        }

        private void UpdateProgressBar()
        {
            ApplicationWindow reporting = GroupWindows.FirstOrDefault(ReportsProgress)
                ?? (ReportsProgress(Window) ? Window : null);

            if (reporting == null || TaskProgressBar.Parent is not FrameworkElement host || host.ActualWidth <= 0)
            {
                StopProgressSlide();
                TaskProgressBar.Visibility = Visibility.Collapsed;
                return;
            }

            NativeMethods.TBPFLAG state = reporting.ProgressState;

            // The Windows 7 style: the whole inside of the tab's glass (the content is inset from it by the template's margin), not a bar.
            bool glass = Host?.IconOnly == true;
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double width = host.ActualWidth;
            if (glass)
            {
                const double insetX = 2, insetY = 4;
                width += 2 * insetX;
                TaskProgressBar.Margin = new Thickness(-insetX, -insetY, 0, -insetY);
                TaskProgressBar.Height = double.NaN;
                TaskProgressBar.VerticalAlignment = VerticalAlignment.Stretch;
                TaskProgressBar.RadiusX = TaskProgressBar.RadiusY = 2;
                TaskProgressBar.Fill = state.HasFlag(NativeMethods.TBPFLAG.TBPF_ERROR) ? GlassError
                    : state.HasFlag(NativeMethods.TBPFLAG.TBPF_PAUSED) ? GlassPaused
                    : GlassNormal;
            }
            else
            {
                TaskProgressBar.Margin = new Thickness(0);
                TaskProgressBar.Height = 3;
                TaskProgressBar.VerticalAlignment = VerticalAlignment.Bottom;
                TaskProgressBar.RadiusX = TaskProgressBar.RadiusY = 0;
                TaskProgressBar.Fill = state.HasFlag(NativeMethods.TBPFLAG.TBPF_ERROR) ? ProgressError
                    : state.HasFlag(NativeMethods.TBPFLAG.TBPF_PAUSED) ? ProgressPaused
                    : ProgressNormal;
            }

            if (state.HasFlag(NativeMethods.TBPFLAG.TBPF_INDETERMINATE))
            {
                TaskProgressBar.Width = Math.Max(4, width * 0.3);
                TaskProgressBar.Visibility = Visibility.Visible;
                StartProgressSlide(width - TaskProgressBar.Width);
                return;
            }

            StopProgressSlide();
            double fraction = Math.Clamp(reporting.ProgressValue / ProgressMax, 0, 1);
            TaskProgressBar.Width = Math.Round(width * fraction * dpi) / dpi;
            TaskProgressBar.Visibility = Visibility.Visible;
        }

        private void StartProgressSlide(double travel)
        {
            _progressSlide ??= new TranslateTransform();
            TaskProgressBar.RenderTransform = _progressSlide;
            _progressSliding = true;
            _progressSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, Math.Max(0, travel), TimeSpan.FromSeconds(1.2))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }

        private void StopProgressSlide()
        {
            if (_progressSliding)
            {
                _progressSliding = false;
                _progressSlide.BeginAnimation(TranslateTransform.XProperty, null);
                TaskProgressBar.RenderTransform = null;
            }
        }
    }
}
