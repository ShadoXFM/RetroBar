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
    // goes on, yellow when paused, red on an error, and a block sliding to and fro when it has no value.
    public partial class TaskButton
    {
        private const double ProgressMax = 65534.0;

        private static readonly Brush ProgressNormal = Frozen(Color.FromRgb(0x06, 0xB0, 0x25));
        private static readonly Brush ProgressPaused = Frozen(Color.FromRgb(0xF6, 0xC0, 0x00));
        private static readonly Brush ProgressError = Frozen(Color.FromRgb(0xDA, 0x00, 0x00));

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private TranslateTransform _progressSlide;
        private bool _progressSliding;

        private void InitProgressBar()
        {
            AppButton.SizeChanged += (s, e) => UpdateProgressBar();
        }

        private void UpdateProgressBar()
        {
            ApplicationWindow reporting = GroupWindows.FirstOrDefault(w => w.ProgressState != NativeMethods.TBPFLAG.TBPF_NOPROGRESS)
                ?? (Window != null && Window.ProgressState != NativeMethods.TBPFLAG.TBPF_NOPROGRESS ? Window : null);

            if (reporting == null || TaskProgressBar.Parent is not FrameworkElement host || host.ActualWidth <= 0)
            {
                StopProgressSlide();
                TaskProgressBar.Visibility = Visibility.Collapsed;
                return;
            }

            double width = host.ActualWidth;
            NativeMethods.TBPFLAG state = reporting.ProgressState;

            TaskProgressBar.Fill = state.HasFlag(NativeMethods.TBPFLAG.TBPF_ERROR) ? ProgressError
                : state.HasFlag(NativeMethods.TBPFLAG.TBPF_PAUSED) ? ProgressPaused
                : ProgressNormal;

            if (state.HasFlag(NativeMethods.TBPFLAG.TBPF_INDETERMINATE))
            {
                TaskProgressBar.Width = Math.Max(4, width * 0.3);
                TaskProgressBar.Visibility = Visibility.Visible;
                StartProgressSlide(width - TaskProgressBar.Width);
                return;
            }

            StopProgressSlide();
            double fraction = Math.Clamp(reporting.ProgressValue / ProgressMax, 0, 1);
            TaskProgressBar.Width = Math.Round(width * fraction * VisualTreeHelper.GetDpi(this).DpiScaleX) / VisualTreeHelper.GetDpi(this).DpiScaleX;
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
