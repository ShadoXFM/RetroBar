using RetroBar.Utilities;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace RetroBar.Controls
{
    /// <summary>
    /// Interaction logic for ToolbarButton.xaml
    /// </summary>
    public partial class ToolbarButton : UserControl
    {
        // True for a pinned program among the tabs of the Windows 11 style: the button is as big as the control is (the size of a tab's
        // frame, which the task list gives the control), not the size of the per-monitor ToolbarButtonBox tag.
        public static readonly DependencyProperty StretchedProperty = DependencyProperty.Register(
            nameof(Stretched), typeof(bool), typeof(ToolbarButton), new PropertyMetadata(false, (d, e) => ((ToolbarButton)d).ApplyStretched()));

        public bool Stretched
        {
            get => (bool)GetValue(StretchedProperty);
            set => SetValue(StretchedProperty, value);
        }

        // The place of the icon in the button, as the task list works it out (the icon of a pin is put where the icon of a tab is, in a
        // button that is the size of a tab's frame). Used while Stretched.
        public static readonly DependencyProperty IconMarginProperty = DependencyProperty.Register(
            nameof(IconMargin), typeof(Thickness), typeof(ToolbarButton), new PropertyMetadata(new Thickness(), (d, e) => ((ToolbarButton)d).ApplyIconMargin()));

        public Thickness IconMargin
        {
            get => (Thickness)GetValue(IconMarginProperty);
            set => SetValue(IconMarginProperty, value);
        }

        private void ApplyIconMargin()
        {
            if (Stretched)
            {
                ToolbarIcon.Margin = IconMargin;
            }
        }

        // Where the middle of the picture of a tab's icon is in the frame of its tab (device pixels from the frame's top left), as the task
        // list reads it from a tab at rest; NaN until it is known. This icon is put at that place of its own box (which is the size and place a
        // tab's frame has), by whole device pixels added after its snapping (MonitorOffset.Nudge) - an icon with more empty space at one side,
        // like Opera's, was a pixel or two off its tab's when the program opened or closed.
        public static readonly DependencyProperty IconTargetProperty = DependencyProperty.Register(
            nameof(IconTarget), typeof(Vector), typeof(ToolbarButton), new PropertyMetadata(new Vector(double.NaN, double.NaN), (d, e) => ((ToolbarButton)d).QueueCenterIcon(true)));

        public Vector IconTarget
        {
            get => (Vector)GetValue(IconTargetProperty);
            set => SetValue(IconTargetProperty, value);
        }

        private (double X, double Y)? _pendingMove;

        // Where each program's icon was last put (whole device pixels): a pin made anew when its program closes starts there, and is not
        // seen moving to it.
        private static readonly System.Collections.Generic.Dictionary<string, Vector> LastNudges = new(StringComparer.OrdinalIgnoreCase);
        private string _programExe;
        private DispatcherTimer _revealTimer;
        private bool _revealed = true;

        // If it has not come to rest by now (the pointer is over it, or the layout does not stop), it is shown where it is.
        private void RevealTimer_Tick(object sender, EventArgs e)
        {
            _revealTimer.Stop();
            _revealed = true;
            // Centered for the size it has now, then seen: the first frame it is seen in was a pixel or two off.
            CenterIconNow();
            ToolbarIcon.ClearValue(OpacityProperty);
        }
        private bool _centerQueued;
        private Point _lastBoxOrigin = new(double.NaN, double.NaN);

        private void QueueCenterIcon(bool reset)
        {
            QueueCenterIcon(reset, DispatcherPriority.ApplicationIdle);
        }

        private void QueueCenterIcon(bool reset, DispatcherPriority priority)
        {
            if (reset)
            {
                _pendingMove = null;
            }

            if (_centerQueued)
            {
                return;
            }

            _centerQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _centerQueued = false;
                CenterIcon();
            }), priority);
        }

        // How far (device pixels) the middle of the icon's picture is off where it goes; false if it cannot be told yet.
        private bool MeasureIcon(out double dx, out double dy, out int press)
        {
            dx = dy = 0;
            // The theme moves what is in a pressed button down and to the right by a DIP, which the snapping of the icon rounds as it
            // likes (a pixel down and nothing to the right, for one): it is put a pixel down and to the right of where it is at rest.
            press = LauncherButton.IsPressed ? 1 : 0;
            try
            {
                var box = LauncherButton.Template?.FindName("ButtonRightBottomBorder", LauncherButton) as FrameworkElement;
                if (!Stretched || box == null || box.ActualWidth <= 0 || !ToolbarIcon.IsVisible || ToolbarIcon.ActualWidth <= 0 || double.IsNaN(IconTarget.X) || ToolbarIcon.Source == null)
                {
                    return false;
                }

                Point boxTopLeft = box.PointToScreen(new Point(0, 0));
                Point iconTopLeft = ToolbarIcon.PointToScreen(new Point(0, 0));
                Point iconBottomRight = ToolbarIcon.PointToScreen(new Point(ToolbarIcon.ActualWidth, ToolbarIcon.ActualHeight));
                (double artX, double artY) = TaskButton.ArtCenterOffset(ToolbarIcon.Source, iconBottomRight.X - iconTopLeft.X, iconBottomRight.Y - iconTopLeft.Y);
                dx = IconTarget.X + press - ((iconTopLeft.X + iconBottomRight.X) / 2 + artX - boxTopLeft.X);
                dy = IconTarget.Y + press - ((iconTopLeft.Y + iconBottomRight.Y) / 2 + artY - boxTopLeft.Y);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        // Here and now, the layout brought up to date each time (a few times at most): a pin made anew when a program closes is seen from its
        // first frame, in its place, not a pixel off for a moment and not hidden for one.
        private void CenterIconNow()
        {
            for (int pass = 0; pass < 4; pass++)
            {
                UpdateLayout();
                if (!MeasureIcon(out double dx, out double dy, out _))
                {
                    return;
                }

                if (TaskButton.WholePixels(dx) == 0 && TaskButton.WholePixels(dy) == 0)
                {
                    _pendingMove = null;
                    if (_programExe != null)
                    {
                        LastNudges[_programExe] = MonitorOffset.GetNudge(ToolbarIcon);
                    }

                    // Seen once the button has its size (see ApplyProgramIcon).
                    if (_revealed && !double.IsNaN(Height))
                    {
                        ToolbarIcon.ClearValue(OpacityProperty);
                    }

                    return;
                }

                Vector nudge = MonitorOffset.GetNudge(ToolbarIcon);
                MonitorOffset.SetNudge(ToolbarIcon, new Vector(Math.Clamp(nudge.X + TaskButton.WholePixels(dx), -6, 6), Math.Clamp(nudge.Y + TaskButton.WholePixels(dy), -6, 6)));
            }
        }

        private void CenterIcon()
        {
            if (!MeasureIcon(out double dx, out double dy, out int press))
            {
                return;
            }

            if (TaskButton.WholePixels(dx) == 0 && TaskButton.WholePixels(dy) == 0)
            {
                _pendingMove = null;
                if (_programExe != null)
                {
                    LastNudges[_programExe] = MonitorOffset.GetNudge(ToolbarIcon);
                }

                return;
            }

            // Only when it is off by the same amount twice running, a render apart (the snapping works on the layout, and a move made on
            // what the last one had not reached yet added up): as a tab's icon is.
            if (_pendingMove is (double px, double py) && px == dx && py == dy)
            {
                _pendingMove = null;
                Vector nudge = MonitorOffset.GetNudge(ToolbarIcon);
                MonitorOffset.SetNudge(ToolbarIcon, new Vector(Math.Clamp(nudge.X + TaskButton.WholePixels(dx), -6, 6), Math.Clamp(nudge.Y + TaskButton.WholePixels(dy), -6, 6)));
            }
            else
            {
                _pendingMove = (dx, dy);
            }

            QueueCenterIcon(false, press == 1 ? DispatcherPriority.Render : DispatcherPriority.ApplicationIdle);
        }

        private void LauncherButton_OnPressedChanged(object sender, EventArgs e) => QueueCenterIcon(true, DispatcherPriority.Render);

        private void ToolbarIcon_OnSourceChanged(object sender, EventArgs e) => QueueCenterIcon(true);

        // The icon of the program the pin starts, the one its tab shows when it is open (not the small icon of the link, which is another
        // picture, with its own empty space at the sides, and was a pixel or two off its tab's icon when the program opened or closed).
        private void ApplyProgramIcon()
        {
            if (!Stretched)
            {
                return;
            }

            ImageSource program = null;
            try
            {
                if (DataContext is ManagedShell.ShellFolders.ShellFile file && !string.IsNullOrEmpty(file.Path))
                {
                    string exe = PinnedApps.TargetPath(file.Path);
                    if (!string.IsNullOrEmpty(exe) && !exe.Contains("\\WindowsApps\\",StringComparison.OrdinalIgnoreCase))
                    {
                        program = TaskButton.ProgramIconOf(exe, null);
                        _programExe = program != null ? exe : null;
                    }
                }
            }
            catch (Exception)
            {
            }

            if (program != null)
            {
                BindingOperations.ClearBinding(ToolbarIcon, Image.SourceProperty);
                ToolbarIcon.Source = program;
                // The button has its size from the task list a moment after it is made, and its icon moves by a pixel or two with it: not seen
                // until it has it (and been centered for it, before it is drawn), or for a moment at most.
                // Hidden for a few frames, which is not seen: the first frame it is in is a pixel or so off, and it was seen going to its place.
                _revealed = false;
                {
                    ToolbarIcon.Opacity = 0;
                    _revealTimer ??= new DispatcherTimer(DispatcherPriority.Render);
                    _revealTimer.Interval = TimeSpan.FromMilliseconds(30);
                    _revealTimer.Stop();
                    _revealTimer.Tick -= RevealTimer_Tick;
                    _revealTimer.Tick += RevealTimer_Tick;
                    _revealTimer.Start();
                }
            }
            else
            {
                _programExe = null;
                setIconBinding("LargeIcon");
            }
        }

        // Where this button is on the screen moves with the tabs (they slide, a window opens or closes): where the icon falls on the pixels
        // depends on it, so it is looked at again.
        private void ToolbarButton_OnLayoutUpdated(object sender, EventArgs e)
        {
            if (!Stretched)
            {
                return;
            }

            try
            {
                Point origin = PointToScreen(new Point(0, 0));
                if (origin != _lastBoxOrigin)
                {
                    _lastBoxOrigin = origin;
                    // Where the button is on the screen decides how its icon falls on the pixels: centered again at once, before it is drawn,
                    // and then again when things have settled.
                    if (IsLoaded)
                    {
                        Dispatcher.BeginInvoke(new Action(CenterIconNow), DispatcherPriority.Render);
                    }

                    QueueCenterIcon(true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void ApplyStretched()
        {
            if (Stretched)
            {
                MonitorOffset.SetTag(LauncherButton, null);
                LauncherButton.ClearValue(WidthProperty);
                LauncherButton.ClearValue(HeightProperty);
                LauncherButton.Margin = new Thickness(0);
                LauncherButton.ClearValue(RenderTransformProperty);
                LauncherButton.HorizontalAlignment = HorizontalAlignment.Stretch;
                LauncherButton.VerticalAlignment = VerticalAlignment.Stretch;
                // The button is only as wide as a tab's frame, so no room is spared for the icon's own margin (it was cut at the right).
                ToolbarIcon.Margin = IconMargin;
                // Drawn as the icon of a tab is (its size, and the pixels it snaps to), so the two are the same when a program opens and closes.
                MonitorOffset.SetTag(ToolbarIcon, "TaskIconBar");
                // From the top left of the button, not its middle: the size the task list gives the button changes a moment after it is made
                // (by a pixel), and an icon in the middle of it moved by a pixel or two with it, for a frame. The whole pixels that put it in
                // the middle of the frame are added to that place (CenterIcon).
                ToolbarIcon.HorizontalAlignment = HorizontalAlignment.Left;
                ToolbarIcon.VerticalAlignment = VerticalAlignment.Top;
                // The shell's large icon, made to the size of the tab's icon the same way (a tab makes its icon from the large one, and the small
                // one made bigger or smaller by a fraction of a pixel looked soft).
                RenderOptions.SetBitmapScalingMode(ToolbarIcon, BitmapScalingMode.HighQuality);
                ToolbarIcon.UseLayoutRounding = true;
                ToolbarIcon.SnapsToDevicePixels = true;
                ApplyProgramIcon();
                QueueCenterIcon(true);
            }
            else
            {
                MonitorOffset.SetTag(ToolbarIcon, "ToolbarIcon");
                ToolbarIcon.ClearValue(HorizontalAlignmentProperty);
                ToolbarIcon.ClearValue(VerticalAlignmentProperty);
                MonitorOffset.SetNudge(ToolbarIcon, new Vector(0, 0));
                ToolbarIcon.ClearValue(RenderOptions.BitmapScalingModeProperty);
                ToolbarIcon.ClearValue(UseLayoutRoundingProperty);
                ToolbarIcon.ClearValue(SnapsToDevicePixelsProperty);
                setIconBinding();
                ToolbarIcon.ClearValue(MarginProperty);
                LauncherButton.ClearValue(HorizontalAlignmentProperty);
                LauncherButton.ClearValue(VerticalAlignmentProperty);
                MonitorOffset.SetTag(LauncherButton, "ToolbarButtonBox");
            }
        }

        public ToolbarButton()
        {
            InitializeComponent();

            setIconBinding();
            DataContextChanged += (s, e) => ApplyProgramIcon();

            // The source comes after the icon is laid out, and the place of the picture in it is only known then.
            DependencyPropertyDescriptor sourceDescriptor = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
            DependencyPropertyDescriptor pressedDescriptor = DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, typeof(Button));
            Loaded += (s, e) =>
            {
                sourceDescriptor.AddValueChanged(ToolbarIcon, ToolbarIcon_OnSourceChanged);
                pressedDescriptor.AddValueChanged(LauncherButton, LauncherButton_OnPressedChanged);
                LayoutUpdated += ToolbarButton_OnLayoutUpdated;
                CenterIconNow();
                QueueCenterIcon(true);
            };
            Unloaded += (s, e) =>
            {
                sourceDescriptor.RemoveValueChanged(ToolbarIcon, ToolbarIcon_OnSourceChanged);
                pressedDescriptor.RemoveValueChanged(LauncherButton, LauncherButton_OnPressedChanged);
                LayoutUpdated -= ToolbarButton_OnLayoutUpdated;
            };
            ToolbarIcon.SizeChanged += (s, e) => QueueCenterIcon(true);

            // The button gets its size from the task list a moment after it is made (a binding), and its icon, in the middle of it, moves by
            // a pixel or two with it: centered again as soon as it has the size, before it is drawn.
            SizeChanged += (s, e) =>
            {
                if (Stretched && IsLoaded && (e.HeightChanged || e.WidthChanged))
                {
                    Dispatcher.BeginInvoke(new Action(CenterIconNow), DispatcherPriority.Render);
                }
            };
        }

        private void setIconBinding(string forcedPath = null)
        {
            string bindingPath = "SmallIcon";
            bool useLargeIcons = Settings.Instance.TaskbarScale > 1 || (Application.Current.FindResource("UseLargeIcons") as bool? ?? false);

            if (useLargeIcons || forcedPath != null)
            {
                bindingPath = forcedPath ?? "LargeIcon";
            }

            Binding iconBinding = new Binding(bindingPath);
            iconBinding.Mode = BindingMode.OneWay;
            ToolbarIcon.SetBinding(Image.SourceProperty, iconBinding);
        }
    }
}
