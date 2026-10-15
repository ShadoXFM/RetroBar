using ManagedShell;
using ManagedShell.Common.Logging;
using ManagedShell.WindowsTasks;
using RetroBar.Converters;
using RetroBar.Utilities;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace RetroBar.Controls
{
    /// <summary>
    /// The seek popup: opening and closing with its slide, the controls (slider, timer, skip buttons), the track menu.
    /// </summary>
    public partial class MediaPlayer
    {
        // Set right before SeekPopup.IsOpen flips to false because the SAME click that's about
        // to reach TrackTextCanvas_OnMouseLeftButtonUp already dismissed it (see
        // SeekPopup_OnClosed) - without this, clicking the track text a second time to close the
        // popup would instead close-then-immediately-reopen it: Popup's own StaysOpen="False"
        // dismisses it on that click before our handler even runs, so by the time our handler
        // checks IsOpen it already reads false and toggles it back open.
        private bool _ignoreNextTrackTextClick;

        private DateTime _ignoreTrackTextClickUntil;

        // Right-click on the track text: choose which app's media to show when several have a session
        // (a browser tab and a music player, say) - or "Automatic" to follow whichever Windows says is
        // current. The choice is shared by every monitor's player (they share one session manager).
        private async void TrackTextCanvas_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            if (_sessionManager == null)
            {
                return;
            }

            MediaSessionManager manager = _sessionManager;
            System.Collections.Generic.List<MediaSessionInfo> sessions = await manager.GetSessionsAsync();

            string automaticLabel = TryFindResource("media_source_automatic") as string ?? "Automatic";
            string noneLabel = TryFindResource("media_source_none") as string ?? "No media playing";

            var menu = new ContextMenu
            {
                PlacementTarget = TrackTextCanvas,
                Placement = Settings.Instance.Edge switch
                {
                    ManagedShell.AppBar.AppBarEdge.Left => System.Windows.Controls.Primitives.PlacementMode.Right,
                    ManagedShell.AppBar.AppBarEdge.Right => System.Windows.Controls.Primitives.PlacementMode.Left,
                    ManagedShell.AppBar.AppBarEdge.Top => System.Windows.Controls.Primitives.PlacementMode.Bottom,
                    _ => System.Windows.Controls.Primitives.PlacementMode.Top,
                },
            };

            var automatic = new MenuItem
            {
                Header = automaticLabel,
                IsChecked = string.IsNullOrEmpty(Settings.Instance.MediaPreferredApp),
            };
            automatic.Click += (s, args) => manager.SelectApp("");
            menu.Items.Add(automatic);

            menu.Items.Add(MakeMenuSeparator());

            if (sessions.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = noneLabel, IsEnabled = false });
            }

            foreach (MediaSessionInfo info in sessions)
            {
                string title = string.IsNullOrEmpty(info.Title) ? "" : $" \u2014 {info.Title}";
                string state = info.State == MediaPlaybackState.Playing ? "  \u25B6" : "";

                // Some apps (browsers especially) use an opaque AppUserModelID; their window's own file
                // description ("Opera GX Internet Browser") is a better name than the ID.
                ApplicationWindow appWindow = FindAppWindow(info.AppUserModelId) ?? FindWindowByTitle(info.Title) ?? FindWindowByAppName(info.DisplayName) ?? FindBrowserWindow(info.AppUserModelId);
                string description = appWindow?.WinFileDescription;
                string appName = !string.IsNullOrWhiteSpace(description) ? description
                    : IsOpaqueAppId(info.AppUserModelId) ? (TryFindResource("media_source_browser") as string ?? "Browser")
                    : info.DisplayName;

                var item = new MenuItem
                {
                    Header = $"{appName}{title}{state}",
                    IsChecked = info.IsShown,
                };

                string aumid = info.AppUserModelId;
                item.Click += (s, args) => manager.SelectApp(aumid);
                menu.Items.Add(item);
            }

            menu.IsOpen = true;
        }

        // The theme's menus draw a plain Separator as empty space; this one is a visible bevelled line (see MenuSeparator).
        private static Separator MakeMenuSeparator()
        {
            return new Separator { Style = MenuSeparator.Style };
        }

        // The wheel over the media part of the taskbar (the text, the album art, the buttons) seeks 5 seconds a notch (up forward), as it does
        // over the popup. The album art and the buttons are in adorners, which this control does not get the wheel from: they call it too.
        private void MediaPlayer_OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta != 0 && CanSeekSource)
            {
                _ = SeekRelativeAsync(TimeSpan.FromSeconds(5 * Math.Sign(e.Delta)));
                e.Handled = true;
            }
        }

        private void TrackTextCanvas_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Only the click that closed the popup (its mouse-up arrives right after): a popup closed some other way - by this very click
            // while it stays open on its own, or from its menu - left the flag set and ate the next, real click.
            bool ignore = _ignoreNextTrackTextClick && DateTime.UtcNow < _ignoreTrackTextClickUntil;
            _ignoreNextTrackTextClick = false;
            if (ignore)
            {
                return;
            }

            if (_sessionManager != null && _sessionManager.HasSession)
            {
                if (SeekPopup.IsOpen)
                {
                    // A popup that has been moved or is always on top is a window of its own, not up from the taskbar: it just goes, as
                    // it does from its menu.
                    if (_seekPopupMovedTo != null || _seekPopupPinned)
                    {
                        CloseSeekPopupNow();
                    }
                    else
                    {
                        CloseSeekPopupAnimated();
                    }
                }
                else
                {
                    OpenSeekPopupAnimated();
                }

                _ignoreNextTrackTextClick = false;
            }
        }

        // The popup's buttons' glyphs, drawn as whole device pixels (see PixelShape) at the height of the taskbar's own, and the
        // pause glyph with the taskbar's own bars (2.4 DIP wide, 1.6 apart). All three buttons are as wide as the widest glyph
        // plus a pixel, and the glyphs are set in them so that the gap between one glyph and the next is the same pixel count
        // either side of the pause: the arrows keep to the outside of their boxes, one to each side, the pause is centered.
        private DpiScale GetSeekDpi() => VisualTreeHelper.GetDpi(SeekPopupRoot.IsLoaded ? SeekPopupRoot : this);

        private void ApplySeekGlyphs()
        {
            DpiScale dpi = GetSeekDpi();
            int boxHeight = (int)Math.Round(7 * dpi.DpiScaleY);
            int height = boxHeight % 2 == 1 ? boxHeight : boxHeight + 1;
            bool playing = _sessionManager?.PlaybackState == MediaPlaybackState.Playing;

            string[] rewind = PixelShape.DoubleTriangle(height, false);
            string[] forward = PixelShape.DoubleTriangle(height, true);
            string[] middle = playing
                ? PixelShape.Pause(height, Math.Max(2, (int)Math.Round(2.4 * dpi.DpiScaleX)), Math.Max(2, (int)Math.Round(1.6 * dpi.DpiScaleX)))
                : PixelShape.Play(height);
            int boxWidth = Math.Max(rewind[0].Length, middle[0].Length) + 1;

            void Apply(System.Windows.Shapes.Path path, string[] rows, int offsetX)
            {
                path.Stretch = Stretch.None;
                path.Width = boxWidth / dpi.DpiScaleX;
                path.Height = boxHeight / dpi.DpiScaleY;
                path.Data = PixelShape.FromRows(rows, offsetX, Math.Max(0, (boxHeight - rows.Length) / 2), dpi);
            }

            // The skip glyphs are narrower than the rewind and fast-forward ones, with the bar (as wide as the pause's bars) on the outer
            // side. They face the next glyph with the point of their triangle, which reads as a wider gap than a bar or a base does,
            // so each leaves a pixel less to it.
            int bar = Math.Max(2, (int)Math.Round(1.6 * dpi.DpiScaleX));
            string[] previous = PixelShape.Skip(height, bar, false);
            string[] next = PixelShape.Skip(height, bar, true);

            // At 125% each is another pixel out, away from its neighbor.
            int skipOut = dpi.DpiScaleX < 1.125 ? 0 : 1;
            Apply(SeekPreviousGlyph, previous, Math.Max(0, boxWidth - previous[0].Length - 1 - skipOut));
            Apply(SeekNextGlyph, next, Math.Min(1 + skipOut, boxWidth - next[0].Length));
            Apply(SeekBackGlyph, rewind, 0);
            Apply(SeekPlayPauseGlyph, middle, (boxWidth - middle[0].Length) / 2);
            Apply(SeekForwardGlyph, forward, boxWidth - forward[0].Length);

            // The buttons' margins are whole pixels too (a DIP at 125% is a pixel and a quarter, which rounded differently from
            // one button to the next and left the gaps between the glyphs uneven).
            double pixel = 1 / dpi.DpiScaleX;
            // The button style's own padding leaves 7 pixels between one glyph and the next at 100%, a pixel too many next to the
            // 125% layout's proportions: there each of the two gaps is pulled in by a pixel.
            double pull = dpi.DpiScaleX < 1.125 ? pixel : 0;
            // The row starts 6 pixels in, less 1 at 125% and 2 at 100% (the slider takes up what that gives up).
            double shift = (dpi.DpiScaleX < 1.125 ? 2 : 1) * pixel;
            SeekPreviousButton.Margin = new Thickness(6 * pixel - shift, 0, 0, 0);
            SeekPlayPauseButton.Margin = new Thickness(-pull, 0, 0, 0);
            SeekNextButton.Margin = new Thickness(-pull, 0, 0, 0);

            // The slider takes up the width the buttons' margins gave up (2 pixels each side, and the pulled-in gaps), so the row,
            // the popup and the art keep their size. 2 DIP less of gap to the buttons than that, which the slider is longer by (its width
            // deduction in the XAML is 2 less to match).
            // At 100% another 4 pixels of it (2 DIP is no more than that there), given back on the other side of the slider.
            double closer = dpi.DpiScaleX < 1.125 ? 4 * pixel : 0;
            // The slider reaches 2 pixels further at each end, into what was the gaps (so they are 2 pixels smaller) - it is 4 pixels
            // longer: its width is the same binding as in the XAML, with that much less deducted.
            SeekSlider.Margin = new Thickness(4 + 2 * pull + shift - closer, 0, 6 + closer, 2 * pixel);

            var width = new MultiBinding
            {
                Converter = (IMultiValueConverter)FindResource("remainingWidthConverter"),
                ConverterParameter = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{12.8 - 4 * pixel},30"),
            };
            foreach (FrameworkElement source in new FrameworkElement[] { SeekAlbumArtImage, SeekPlayPauseButton, SeekPreviousButton, SeekNextButton, SeekTimeText })
            {
                width.Bindings.Add(new Binding(nameof(ActualWidth)) { Source = source });
            }
            SeekSlider.SetBinding(WidthProperty, width);

            // Two pixels higher than the row's center (the slider one, its own margin above), which the digits' shapes leave looking low
            // next to the buttons.
            SeekTimeText.Margin = new Thickness(2, 0, 6, 4 / dpi.DpiScaleY);
        }

        // Whether the source can be sought: it says so to Windows, or it is foobar2000, which is sought through its command line.
        private bool CanSeekSource => _sessionManager != null
            && (_sessionManager.CanSeek || Foobar2000Seeker.IsFoobar2000(_sessionManager.SourceAppUserModelId));

        // Seeks the source to a position, which it is at now at `from`.
        private async System.Threading.Tasks.Task SeekSourceAsync(TimeSpan target, TimeSpan from)
        {
            if (_sessionManager == null)
            {
                return;
            }

            if (_sessionManager.CanSeek)
            {
                await _sessionManager.SeekAsync(target);
            }
            else if (Foobar2000Seeker.IsFoobar2000(_sessionManager.SourceAppUserModelId))
            {
                await Foobar2000Seeker.SeekByAsync(target - from);
            }
        }

        // The wheel over the popup seeks 5 seconds a notch (up forward).
        private void SeekPopupRoot_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta != 0 && CanSeekSource)
            {
                _ = SeekRelativeAsync(TimeSpan.FromSeconds(5 * Math.Sign(e.Delta)));
            }

            e.Handled = true;
        }

        // The popup slides up out of the taskbar's edge, and back down into it (see PopupSlide): a click elsewhere closes it the same way
        // (see the click-away watching in MediaPlayer.PopupWindow.cs).
        private PopupSlide _seekSlide;

        // Where the bottom of the popup's frame is, in DIPs from the top of the element given (the taskbar's window): the 10 is the frame's
        // margin for the shadow, which is part of the popup's window. The weather forecast puts its own frame's bottom here too.
        private const double SeekFrameShadowRoom = 10;

        private double? _lastSeekFrameBottom;

        public double? GetSeekFrameBottom(Visual relativeTo)
        {
            try
            {
                if (IsVisible && TrackTextCanvas.ActualWidth > 0)
                {
                    _lastSeekFrameBottom = TrackTextCanvas.TransformToAncestor(relativeTo).Transform(new Point(0, 0)).Y + SeekPopup.VerticalOffset - SeekFrameShadowRoom;
                }
            }
            catch (InvalidOperationException)
            {
            }

            return _lastSeekFrameBottom;
        }

        private void InitializeSeekSlide()
        {
            // The slider handles the press itself (it moves the thumb to the point) and marks it handled, so an ordinary handler never hears of
            // it: without this the display was not held still while the button was down, and the release seeked to the old position.
            SeekSlider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(SeekSlider_OnPreviewMouseDown), true);
            _seekSlide = new PopupSlide(SeekPopup, SeekPopupRoot, SeekSlideHost, () => SlideDirection.Down, () =>
            {
                // How far the popup's window reaches below the taskbar's top (the shadow's room under the frame).
                return Window.GetWindow(this) is Taskbar taskbar
                    ? Math.Max(0, (GetSeekFrameBottom(taskbar) ?? 0) + SeekFrameShadowRoom)
                    : null;
            });
        }

        private void OpenSeekPopupAnimated() => _seekSlide.Show();

        // Gone at once, with no slide.
        private void CloseSeekPopupNow() => _seekSlide.CloseNow();

        private void CloseSeekPopupAnimated() => _seekSlide.Hide();

        private void SeekPopup_OnOpened(object sender, EventArgs e)
        {
            // The spectrum starts as it is when nothing has been heard for a while: no bars, no dashes (not the last popup's bars sinking
            // and its dashes going again). Sound brings the bars up from there.
            Array.Clear(_seekSpectrumLevels);
            Array.Clear(_seekSpectrumPeaks);
            _seekSpectrumQuietSince = DateTime.UtcNow - TimeSpan.FromHours(1);
            _lastSeekSpectrumUpdate = DateTime.UtcNow;
            SeekSpectrum.Data = null;
            Array.Fill(_seekBarGone, true);
            _seekSpectrumSkipAppear = _sessionManager?.PlaybackState == MediaPlaybackState.Playing;

            StartSeekWatching();
            UpdateVisualizerCapture(_sessionManager?.PlaybackState == MediaPlaybackState.Playing);

            // One popup at a time, whichever monitor's player it belongs to.
            if (_openSeekPlayer != null && _openSeekPlayer != this)
            {
                _openSeekPlayer.CloseSeekPopupAnimated();
            }

            _openSeekPlayer = this;
            Dispatcher.BeginInvoke(new Action(ApplySeekPopupTopmost), DispatcherPriority.Loaded);
            _seekPopupSource = PresentationSource.FromVisual(SeekPopupRoot) as HwndSource;
            SeekPopupRoot.ClearValue(LayoutTransformProperty);
            if (_seekPopupSource != null)
            {
                _seekPopupHwndDpi = _seekPopupSource.CompositionTarget?.TransformToDevice.M11 ?? 0;
                _seekPopupSource.DpiChanged += SeekPopupSource_OnDpiChanged;
                _seekPopupSource.AddHook(SeekPopupHook);
            }

            // Takes down the track text's tooltip, if it is up from the mouse resting on the text that was clicked.
            object trackToolTip = ToolTip;
            ToolTip = null;
            ToolTip = trackToolTip;

            ApplySeekGlyphs();
            Dispatcher.BeginInvoke(new Action(SyncMarquees), System.Windows.Threading.DispatcherPriority.Loaded);

            SeekSlider.IsEnabled = CanSeekSource;

            RefreshSeekDisplay();

            _seekPopupTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _seekPopupTimer.Tick -= SeekPopupTimer_OnTick;
            _seekPopupTimer.Tick += SeekPopupTimer_OnTick;
            _seekPopupTimer.Start();
        }

        private void SeekPopup_OnClosed(object sender, EventArgs e)
        {
            _seekPopupTimer?.Stop();
            StopSeekWatching();
            UpdateVisualizerCapture(_sessionManager?.PlaybackState == MediaPlaybackState.Playing);

            if (_seekSliderTip != null)
            {
                _seekSliderTip.IsOpen = false;
            }

            Array.Clear(_seekSpectrumLevels);
            Array.Clear(_seekSpectrumPeaks);
            _seekSpectrumQuietSince = null;
            if (_seekPopupSource != null)
            {
                _seekPopupSource.DpiChanged -= SeekPopupSource_OnDpiChanged;
                _seekPopupSource.RemoveHook(SeekPopupHook);
                _seekPopupSource = null;
            }

            if (_openSeekPlayer == this)
            {
                _openSeekPlayer = null;
            }

            _seekPopupMonitor = null;
            lock (_seekPopupDragLock)
            {
                _seekPopupDragging = false;
                _seekPopupMovedTo = null;
            }
            _seekPopupHeldX = null;

            // Moved or on top is for the one opening: the next one starts as an ordinary popup again.
            _seekPopupPinned = false;
            SeekPopupRoot.ClearValue(MonitorOffset.DeviceNameOverrideProperty);

            // Nothing is set to ignore the next click on the text here: the click that closes the popup from outside it sets that itself
            // when the button goes down (see SeekInput_OnMouseDown), and by the time the popup has finished closing that button is up -
            // a flag set now would eat the click that opens it again.
        }

        private void SeekPopupTimer_OnTick(object sender, EventArgs e)
        {
            // The popup has settled above the track text by now: that is where it stays while it is open.
            if (_seekPopupHeldX == null && _seekPopupMovedTo == null && !_seekPopupPinned
                && PresentationSource.FromVisual(SeekPopupRoot) is HwndSource source && GetWindowRect(source.Handle, out RECT window))
            {
                _seekPopupHeldX = window.Left;
            }

            RefreshSeekDisplay();
        }

        // Skipped while the user has the thumb held down, so the timer's poll doesn't fight
        // their drag - RefreshSeekDisplay resumes updating the moment they let go.
        private void RefreshSeekDisplay()
        {
            // A button release the slider never saw (the pointer let go over something else) must not leave the display frozen, nor lose the seek.
            if (_seekSliderDragging && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
            {
                FinishSliderSeek();
            }

            if (_sessionManager == null || _seekSliderDragging)
            {
                return;
            }

            (TimeSpan position, TimeSpan duration) = GetTimeline();

            // Just after a seek the source still reports where it was: the position it is sought to stays shown until it catches up.
            if (DateTime.UtcNow < _seekHoldUntil && Math.Abs((position - _seekHoldPosition).TotalSeconds) > 1.5)
            {
                return;
            }

            SetSeekDisplay(position, duration);
        }

        // What the source reports to Windows; for one that reports no length (foobar2000), the times in its window's title if its format has
        // them (see TitleTimeline) - the only exact ones there are.
        private (TimeSpan Position, TimeSpan Duration) GetTimeline()
        {
            (TimeSpan position, TimeSpan duration) = _sessionManager.GetTimeline();
            if (duration <= TimeSpan.Zero && TitleTimeline.TryParse(FindSourceAppWindow()?.Title, out TimeSpan titlePosition, out TimeSpan titleLength))
            {
                return (SmoothTitlePosition(titlePosition), titleLength);
            }

            _titleHasEstimate = false;
            return (position, duration);
        }

        // The title's time changes once a second, by whole seconds, and not at the moments the popup reads it: shown as it is, the slider
        // stood still for a poll and then jumped two. The position runs on by the clock between the title's changes instead, and is only
        // pulled towards the title (which shows the second that has begun, so the true time is half a second on from it) when it changes.
        private bool _titleHasEstimate;
        private TimeSpan _titleEstimate;
        private TimeSpan _titleLastValue;
        private DateTime _titleEstimateAt;

        private TimeSpan SmoothTitlePosition(TimeSpan titlePosition)
        {
            DateTime now = DateTime.UtcNow;
            bool playing = _sessionManager.PlaybackState == MediaPlaybackState.Playing;
            TimeSpan estimate = _titleHasEstimate ? _titleEstimate + (playing ? now - _titleEstimateAt : TimeSpan.Zero) : titlePosition;

            if (!_titleHasEstimate || titlePosition != _titleLastValue)
            {
                _titleLastValue = titlePosition;
                TimeSpan error = titlePosition + TimeSpan.FromSeconds(0.5) - estimate;
                if (!_titleHasEstimate || Math.Abs(error.TotalSeconds) > 1.5)
                {
                    // A seek, a new track, or the first reading: take the title's time.
                    estimate = titlePosition + (playing ? TimeSpan.FromSeconds(0.5) : TimeSpan.Zero);
                }
                else
                {
                    estimate += TimeSpan.FromSeconds(error.TotalSeconds * 0.3);
                }
            }
            else if (!playing)
            {
                estimate = titlePosition;
            }

            if (estimate < TimeSpan.Zero)
            {
                estimate = TimeSpan.Zero;
            }

            _titleHasEstimate = true;
            _titleEstimate = estimate;
            _titleEstimateAt = now;
            return estimate;
        }

        private DateTime _seekHoldUntil = DateTime.MinValue;

        private TimeSpan _seekHoldPosition;

        private void HoldSeek(TimeSpan target)
        {
            _seekHoldPosition = target;
            _seekHoldUntil = DateTime.UtcNow.AddSeconds(2);
        }

        /// <summary>Seeks to a position (a line of the lyrics): shown at once, as the wheel and the slider do.</summary>
        private async System.Threading.Tasks.Task SeekToAsync(TimeSpan target)
        {
            if (_sessionManager == null)
            {
                return;
            }

            TimeSpan from = _lastSeekPosition;
            SetSeekDisplay(target, _lastSeekDuration);
            HoldSeek(target);
            await SeekSourceAsync(target, from);
        }

        // Separated from RefreshSeekDisplay so a skip/seek can paint the target position
        // immediately (optimistically, before the source app's own position catches up and the
        // next real poll confirms it) - GetTimeline() right after sending a seek command often
        // still reflects the OLD position for a moment, which would otherwise make the bar look
        // like it ignored the click until the next 500ms poll happens to land after the source
        // updates.
        // The time passed or the time left (a click on the timer switches), for as long as the app runs.
        private static bool _seekShowRemaining;

        private TimeSpan _lastSeekPosition;

        private DateTime _lastSeekPositionAt;

        private TimeSpan _lastSeekDuration;

        private void SetSeekDisplay(TimeSpan position, TimeSpan duration)
        {
            _lastSeekPosition = position;
            _lastSeekPositionAt = DateTime.UtcNow;
            _lastSeekDuration = duration;
            SeekSlider.Maximum = duration.TotalSeconds > 0 ? duration.TotalSeconds : 1;
            // A source that does not say how long the track is (foobar2000's) leaves the thumb at the start, not pinned at the end.
            SeekSlider.Value = duration.TotalSeconds > 0 ? position.TotalSeconds : 0;

            EnsureLyrics(duration);
            UpdateLyricLine(position);

            string first = _seekShowRemaining && duration > position ? "-" + FormatTime(duration - position) : FormatTime(position);
            SeekTimeText.Text = $"{first} / {FormatTime(duration)}";
        }

        private void SeekTimeText_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _seekShowRemaining = !_seekShowRemaining;
            SetSeekDisplay(_lastSeekPosition, _lastSeekDuration);
            e.Handled = true;
        }

        // The time under the pointer, in a small tooltip above it, while the pointer is on the slider.
        private ToolTip _seekSliderTip;

        private void SeekSlider_OnMouseMove(object sender, MouseEventArgs e)
        {
            if (SeekSlider.Template?.FindName("PART_Track", SeekSlider) is not System.Windows.Controls.Primitives.Track track)
            {
                return;
            }

            Point position = e.GetPosition(SeekSlider);
            double value = Math.Clamp(track.ValueFromPoint(e.GetPosition(track)), SeekSlider.Minimum, SeekSlider.Maximum);

            _seekSliderTip ??= new ToolTip
            {
                Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
                PlacementTarget = SeekSlider,
                StaysOpen = true,
                Focusable = false,
                IsHitTestVisible = false,
            };
            _seekSliderTip.Content = FormatTime(TimeSpan.FromSeconds(value));
            _seekSliderTip.HorizontalOffset = position.X - 14;
            _seekSliderTip.VerticalOffset = -26;
            _seekSliderTip.IsOpen = true;
        }

        private void SeekSlider_OnMouseLeave(object sender, MouseEventArgs e)
        {
            if (_seekSliderTip != null)
            {
                _seekSliderTip.IsOpen = false;
            }
        }

        private static string FormatTime(TimeSpan time)
        {
            return time.Hours > 0
                ? $"{time.Hours}:{time.Minutes:D2}:{time.Seconds:D2}"
                : $"{time.Minutes}:{time.Seconds:D2}";
        }

        // Where the thumb was when the button went down: a click on the thumb itself changes nothing, and is not a seek (to where the
        // thumb was a moment ago).
        private double _seekSliderDownValue;

        private void SeekSlider_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _seekSliderDragging = true;

            // The slider has already moved the thumb to the point pressed by now: the position shown before is the one to compare with.
            _seekSliderDownValue = _lastSeekDuration > TimeSpan.Zero ? _lastSeekPosition.TotalSeconds : SeekSlider.Value;
        }

        private void SeekSlider_OnPreviewMouseUp(object sender, MouseButtonEventArgs e) => FinishSliderSeek();

        private async void FinishSliderSeek()
        {
            if (!_seekSliderDragging)
            {
                return;
            }

            _seekSliderDragging = false;

            if (_sessionManager == null)
            {
                return;
            }

            if (Math.Abs(SeekSlider.Value - _seekSliderDownValue) < 0.5)
            {
                return;
            }

            // Where the source is now, not where it was when the display was last updated (the button may have been down a while).
            TimeSpan target = TimeSpan.FromSeconds(SeekSlider.Value);
            TimeSpan from = GetTimeline().Position;
            SetSeekDisplay(target, TimeSpan.FromSeconds(SeekSlider.Maximum));
            HoldSeek(target);

            await SeekSourceAsync(target, from);
        }

        private async System.Threading.Tasks.Task SeekRelativeAsync(TimeSpan delta)
        {
            if (_sessionManager == null)
            {
                return;
            }

            (TimeSpan position, TimeSpan duration) = GetTimeline();
            TimeSpan target = position + delta;
            if (target < TimeSpan.Zero)
            {
                target = TimeSpan.Zero;
            }
            else if (duration > TimeSpan.Zero && target > duration)
            {
                target = duration;
            }

            // Paint the target position immediately rather than waiting for the seek command to
            // round-trip and the source app's own position to catch up - see SetSeekDisplay.
            SetSeekDisplay(target, duration);
            HoldSeek(target);

            await SeekSourceAsync(target, position);
        }
    }
}
