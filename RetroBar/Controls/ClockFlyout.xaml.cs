using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using RetroBar.Utilities;
using System.Windows.Threading;

namespace RetroBar.Controls
{
    /// <summary>
    /// The content of the clock's flyout: the digital time and the date, and a month to look at (the arrows, the wheel
    /// and a click on the title move through the months; the title brings back this one). Drawn here, in the popups' own fonts and colours.
    /// </summary>
    public partial class ClockFlyout : UserControl
    {
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        private CultureInfo _culture = CultureInfo.CurrentCulture;
        private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
        private DateTime _today = DateTime.Today;
        private Border[] _cells = Array.Empty<Border>();
        private TextBlock[] _cellText = Array.Empty<TextBlock>();
        private NineSlice[] _cellFace = Array.Empty<NineSlice>();
        private DateTime _lastText = DateTime.MinValue;
        private bool _arrowsStyled;

        public ClockFlyout()
        {
            InitializeComponent();
            _timer.Tick += (s, e) => Tick();
        }

        /// <summary>The user's culture (names of the days and months, first day of the week, formats).</summary>
        public CultureInfo Culture
        {
            get => _culture;
            set
            {
                _culture = value ?? CultureInfo.CurrentCulture;
                BuildCalendar();
                Tick(true);
            }
        }

        /// <summary>Shows this month again (the flyout is opened on today's).</summary>
        public void GoToToday()
        {
            _today = DateTime.Today;
            _month = new DateTime(_today.Year, _today.Month, 1);
            BuildCalendar();
            Tick(true);
        }

        private void ClockFlyout_OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_arrowsStyled)
            {
                _arrowsStyled = true;
                StyleArrow(PreviousButton, PreviousFace, -1);
                StyleArrow(NextButton, NextFace, 1);
                DrawArrows();
            }

            BuildCalendar();
            Tick(true);
            _timer.Start();
        }

        private void ClockFlyout_OnUnloaded(object sender, RoutedEventArgs e) => _timer.Stop();

        private void Tick(bool force = false)
        {
            DateTime now = DateTime.Now;

            // The texts change once a second.
            if (force || now.Second != _lastText.Second || now.Date != _lastText.Date)
            {
                _lastText = now;
                TimeText.Text = now.ToString(_culture.DateTimeFormat.LongTimePattern, _culture);
                DateText.Text = now.ToString("D", _culture);
                if (now.Date != _today)
                {
                    // Midnight went by while it was open.
                    _today = now.Date;
                    BuildCalendar();
                }
            }
        }

        private int FirstDayOffset => (int)_culture.DateTimeFormat.FirstDayOfWeek;

        private void BuildCalendar()
        {
            if (WeekDays == null || Days == null)
            {
                return;
            }

            // The days of the week, from the first of the week.
            WeekDays.Children.Clear();
            WeekDays.ColumnDefinitions.Clear();
            for (int c = 0; c < 7; c++)
            {
                WeekDays.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var day = (DayOfWeek)((c + FirstDayOffset) % 7);
                string name = _culture.DateTimeFormat.GetShortestDayName(day);
                var text = new TextBlock
                {
                    Text = name.Length > 2 ? name.Substring(0, 2) : name,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 11,
                    Opacity = 0.75,
                };
                text.SetResourceReference(TextBlock.ForegroundProperty, "ButtonForeground");
                text.SetResourceReference(TextBlock.FontFamilyProperty, "WidgetLabelFontFamily");
                Grid.SetColumn(text, c);
                WeekDays.Children.Add(text);
            }

            // Six rows of seven: made once, filled for each month.
            if (_cells.Length != 42)
            {
                Days.Children.Clear();
                Days.RowDefinitions.Clear();
                Days.ColumnDefinitions.Clear();
                for (int i = 0; i < 7; i++)
                {
                    Days.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                }

                for (int i = 0; i < 6; i++)
                {
                    Days.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
                }

                _cells = new Border[42];
                _cellText = new TextBlock[42];
                _cellFace = new NineSlice[42];
                for (int i = 0; i < 42; i++)
                {
                    var text = new TextBlock
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 12,
                    };
                    text.SetResourceReference(TextBlock.FontFamilyProperty, "WidgetLabelFontFamily");
                    // The real glass of the visual style (Win7GlassButtonImage) under the number, shown for today and for the day under the pointer.
                    var face = new NineSlice { Margins = new Thickness(4, 4, 4, 4), StateCount = 4, State = 1, Visibility = Visibility.Collapsed };
                    face.SetResourceReference(NineSlice.SourceProperty, "Win7GlassButtonImage");
                    var cell = new Border
                    {
                        Margin = new Thickness(1),
                        Background = Brushes.Transparent,
                        Child = new Grid { Children = { face, text } },
                    };
                    _cellFace[i] = face;
                    int index = i;
                    cell.MouseEnter += (s, e) => Highlight(index, true);
                    cell.MouseLeave += (s, e) => Highlight(index, false);
                    Grid.SetRow(cell, i / 7);
                    Grid.SetColumn(cell, i % 7);
                    Days.Children.Add(cell);
                    _cells[i] = cell;
                    _cellText[i] = text;
                }
            }

            MonthTitle.Text = _month.ToString("Y", _culture);
            // The first cell is the first of the week that holds the 1st.
            int lead = ((int)_month.DayOfWeek - FirstDayOffset + 7) % 7;
            DateTime start = _month.AddDays(-lead);
            for (int i = 0; i < 42; i++)
            {
                DateTime date = start.AddDays(i);
                bool inMonth = date.Month == _month.Month;
                bool today = date.Date == _today;
                TextBlock text = _cellText[i];
                text.Text = date.Day.ToString(_culture);
                text.SetResourceReference(TextBlock.ForegroundProperty, "ButtonForeground");
                text.Opacity = today ? 1 : inMonth ? 0.95 : 0.38;
                text.FontWeight = today ? FontWeights.Bold : FontWeights.Normal;
                _cells[i].Tag = today;
                ApplyCell(i, false);
            }
        }

        // The look of a day: today in the glass of the visual style (its lit state), the day under the pointer in the same, fainter.
        private void ApplyCell(int i, bool hot)
        {
            bool today = _cells[i].Tag is true;
            NineSlice face = _cellFace[i];
            face.Visibility = today || hot ? Visibility.Visible : Visibility.Collapsed;
            face.State = 1;
            face.Opacity = today ? (hot ? 1 : 0.85) : 0.55;
        }

        private void Highlight(int i, bool hot) => ApplyCell(i, hot);

        private void MoveMonth(int by)
        {
            _month = _month.AddMonths(by);
            BuildCalendar();
        }

        // The arrows' triangles as whole device pixels (a drawn triangle at 125% had fractions of a pixel for its size and its place): 4 wide and
        // 7 tall (the media buttons' glyphs are made so, see PixelShape), whatever the scale.
        private void DrawArrows()
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            string[] right = { "X...", "XX..", "XXX.", "XXXX", "XXX.", "XX..", "X..." };
            string[] left = { "...X", "..XX", ".XXX", "XXXX", ".XXX", "..XX", "...X" };
            void Set(Path path, string[] rows)
            {
                path.Width = 4 / dpi.DpiScaleX;
                path.Height = 7 / dpi.DpiScaleY;
                path.Data = RetroBar.Utilities.PixelShape.FromRows(rows, 0, 0, dpi);
            }

            Set(PreviousGlyph, left);
            Set(NextGlyph, right);
        }

        // The buttons are the push button of the visual style (see Win7PushButtonImage): its normal, hot and pressed states; the month moves
        // when the button is let go over it.
        private void StyleArrow(Border arrow, NineSlice face, int by)
        {
            void Look(int state) => face.State = state;

            Look(0);
            arrow.MouseEnter += (s, e) => Look(arrow.IsMouseCaptured ? 2 : 1);
            arrow.MouseLeave += (s, e) => Look(arrow.IsMouseCaptured ? 1 : 0);
            arrow.MouseLeftButtonDown += (s, e) =>
            {
                Look(2);
                arrow.CaptureMouse();
                e.Handled = true;
            };
            arrow.MouseLeftButtonUp += (s, e) =>
            {
                bool inside = arrow.IsMouseOver;
                arrow.ReleaseMouseCapture();
                Look(inside ? 1 : 0);
                if (inside)
                {
                    MoveMonth(by);
                }

                e.Handled = true;
            };
        }

        private void MonthHost_OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            MoveMonth(e.Delta > 0 ? -1 : 1);
            e.Handled = true;
        }
    }
}
