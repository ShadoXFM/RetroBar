using RetroBar.Utilities;
using System;
using System.Linq;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RetroBar.Controls
{
    public partial class WeatherDisplay : UserControl
    {
        private DispatcherTimer _timer;
        private WeatherViewModel _viewModel;

        // The Windows 11 style's option (Settings.WidgetDetails): the weather in words and a small graph of the next day's temperature, after
        // the temperature.
        private bool _details;
        public bool Details
        {
            get => _details;
            set
            {
                _details = value;
                WeatherDescriptionText.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                WeatherGraphLine.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                UpdateGraphLine();
            }
        }

        private void UpdateGraphLine()
        {
            if (_details && _viewModel != null)
            {
                WeatherGraphLine.MinSpan = 6;
                WeatherGraphLine.SetValues(_viewModel.HourlyTrend);
            }
        }

        public WeatherDisplay()
        {
            InitializeComponent();
            InitializeForecastSlide();

            // A right click inside the popup does nothing: it went on to the taskbar's own menu, opened over the popup.
            ForecastFrame.PreviewMouseRightButtonDown += (s, e) => e.Handled = true;
            ForecastFrame.PreviewMouseRightButtonUp += (s, e) => e.Handled = true;

            // Wait until the control is fully loaded before starting the timer
            this.Loaded += WeatherDisplay_Loaded;
            this.Unloaded += WeatherDisplay_Unloaded;
        }

        private void WeatherDisplay_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                return;
            }

            _viewModel = new WeatherViewModel();
            DataContext = _viewModel;
            _viewModel.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(WeatherViewModel.HourlyTrend))
                {
                    UpdateGraphLine();
                }
            };

            // Set up the timer
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMinutes(1);

            _timer.Tick += async (sender, args) => await _viewModel.UpdateWeatherAsync();

            Settings.Instance.PropertyChanged += Settings_OnPropertyChanged;

            // Fetch immediately on load (or, if another monitor's display already has the reading,
            // show it right away - see WeatherViewModel) and start the timer - unless the weather is turned off
            ApplyShowWeather();
        }

        private void Settings_OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.ShowWeather))
            {
                Dispatcher.BeginInvoke(new Action(ApplyShowWeather));
            }
        }

        // Settings.ShowWeather off: nothing is shown or fetched (no requests, no popup); on: it starts again right away.
        private void ApplyShowWeather()
        {
            if (_viewModel == null || _timer == null)
            {
                return;
            }

            if (Settings.Instance.ShowWeather)
            {
                Visibility = Visibility.Visible;
                SetIsWeatherShown(true);
                _ = _viewModel.UpdateWeatherAsync();
                _timer.Start();
            }
            else
            {
                _timer.Stop();
                _forecastOpenTimer?.Stop();
                _forecastCloseTimer?.Stop();
                    ClearForecastAnimations();
                ForecastPopup.IsOpen = false;
                Visibility = Visibility.Collapsed;
                SetIsWeatherShown(false);
            }
        }

        private void SetIsWeatherShown(bool isShown)
        {
            if (Window.GetWindow(this) is Taskbar taskbar)
            {
                taskbar.IsWeatherShown = isShown;
            }
        }

        // Hover behavior: the forecast opens once the pointer has rested on the widget for a moment (so
        // sweeping across the taskbar doesn't flash it), and closes shortly after the pointer leaves both
        // the widget and the popup - the delay lets it cross the gap between them.
        private static readonly TimeSpan ForecastOpenDelay = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan ForecastCloseDelay = TimeSpan.FromMilliseconds(350);

        private DispatcherTimer _forecastOpenTimer;
        private DispatcherTimer _forecastCloseTimer;

        private static DispatcherTimer MakeTimer(TimeSpan interval, Action tick)
        {
            var timer = new DispatcherTimer { Interval = interval };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                tick();
            };
            return timer;
        }

        private void WeatherPanel_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _forecastCloseTimer?.Stop();

            if (ForecastPopup.IsOpen && !_forecastClosing)
            {
                return;
            }

            _forecastOpenTimer ??= MakeTimer(ForecastOpenDelay, () => OpenForecast());
            _forecastOpenTimer.Stop();

            // Going from the CPU temperature's graph to this is quick; it is the rest on the widget that waits.
            _forecastOpenTimer.Interval = HoverPopups.AnyOpen ? HoverPopups.SwitchDelay : ForecastOpenDelay;
            _forecastOpenTimer.Start();
        }

        private void WeatherPanel_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ScheduleForecastClose();
        }

        private void ForecastPopup_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _forecastCloseTimer?.Stop();

            // Back on the popup while it was sliding away: bring it back.
            if (_forecastClosing)
            {
                ShowForecast();
            }
        }

        private void ForecastPopup_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ScheduleForecastClose();
        }

        private void ScheduleForecastClose()
        {
            _forecastOpenTimer?.Stop();

            _forecastCloseTimer ??= MakeTimer(ForecastCloseDelay, HideForecast);
            _forecastCloseTimer.Stop();
            _forecastCloseTimer.Start();
        }

        private void OpenForecast()
        {
            if ((ForecastPopup.IsOpen && !_forecastClosing) || WeatherPanel == null || !WeatherPanel.IsMouseOver)
            {
                return;
            }

            // One at a time with the CPU temperature's graph: if that is open it slides away first (see HoverPopups).
            HoverPopups.Open(_slide, () =>
            {
                if (!WeatherPanel.IsMouseOver)
                {
                    return;
                }

                // Open on the side of the widget facing into the screen, whichever edge the taskbar is on, and
                // centered on the widget along the taskbar (see PlaceForecast).
                ForecastPopup.PlacementTarget = WeatherPanel;
                ForecastPopup.Placement = PlacementMode.Custom;
                ForecastPopup.CustomPopupPlacementCallback = PlaceForecast;

                // Show the latest reading, and refresh it in the background if it has gone stale.
                _ = _viewModel?.UpdateWeatherAsync();
                ShowForecast();
            });
        }

        // The popup slides out of the taskbar's edge, and back into it (see PopupSlide).
        private PopupSlide _slide;

        private void InitializeForecastSlide()
        {
            _slide = new PopupSlide(ForecastPopup, ForecastFrame, ForecastSlideHost,
                () => Settings.Instance.Edge switch
                {
                    ManagedShell.AppBar.AppBarEdge.Left => SlideDirection.Left,
                    ManagedShell.AppBar.AppBarEdge.Right => SlideDirection.Right,
                    ManagedShell.AppBar.AppBarEdge.Top => SlideDirection.Up,
                    _ => SlideDirection.Down,
                },
                ForecastWindowOverlap);
        }

        // On a taskbar at the bottom: how far the popup's window reaches below the taskbar's top (the shadow's room under the frame); the
        // forecast shows only what is above the taskbar while it slides.
        private double? ForecastWindowOverlap()
        {
            if (Window.GetWindow(this) is not Taskbar taskbar ||
                Settings.Instance.Edge is ManagedShell.AppBar.AppBarEdge.Left or ManagedShell.AppBar.AppBarEdge.Right or ManagedShell.AppBar.AppBarEdge.Top)
            {
                return null;
            }

            if (PopupSlide.MeasureOverlap(ForecastFrame, taskbar) is double measured)
            {
                return measured;
            }

            // The frame's bottom (see PlaceForecast).
            double? frameBottom = taskbar.MediaPlayerControl.GetSeekFrameBottom(taskbar);
            if (frameBottom == null)
            {
                try
                {
                    frameBottom = WeatherPanel.TransformToAncestor(taskbar).Transform(new Point(0, 0)).Y;
                }
                catch (InvalidOperationException)
                {
                    frameBottom = 0;
                }
            }

            return Math.Max(0, frameBottom.Value + ShadowRoom + FrameStyleMargin);
        }

        private bool _forecastClosing => _slide.Closing;

        private void ClearForecastAnimations() => _slide.Reset();

        private void ShowForecast() => _slide.Show();

        private void HideForecast() => _slide.Hide();

        // The margin (DIPs) the forecast frame has all round for its shadow - see WeatherDisplay.xaml.
        private const double ShadowRoom = 6;

        // The frame's style (TaskButtonThumbnail) has a margin of its own inside that: the bevel's edge is this much further in.
        private const double FrameStyleMargin = 2.4;

        private CustomPopupPlacement[] PlaceForecast(Size popupSize, Size targetSize, Point offset)
        {
            // On a taskbar at the bottom the frame's bottom is where the media popup's is (the media popup is placed from the track text,
            // which is not at the same height as this widget).
            double sameAsMedia = 0;
            if (Window.GetWindow(this) is Taskbar taskbar && taskbar.MediaPlayerControl.GetSeekFrameBottom(taskbar) is double mediaBottom)
            {
                try
                {
                    sameAsMedia = mediaBottom - WeatherPanel.TransformToAncestor(taskbar).Transform(new Point(0, 0)).Y;
                }
                catch (InvalidOperationException)
                {
                }
            }

            double centeredX = (targetSize.Width - popupSize.Width) / 2;
            double centeredY = (targetSize.Height - popupSize.Height) / 2;

            // offset is the popup's slide animation (see ShowForecast); zero while at rest. The frame has
            // ShadowRoom of margin all round for its shadow, which makes the window that much bigger on every side:
            // the popup is placed that much closer to the widget to leave the frame where it would otherwise be.
            return Settings.Instance.Edge switch
            {
                ManagedShell.AppBar.AppBarEdge.Left => new[] { new CustomPopupPlacement(new Point(targetSize.Width + offset.X - ShadowRoom, centeredY), PopupPrimaryAxis.Vertical) },
                ManagedShell.AppBar.AppBarEdge.Right => new[] { new CustomPopupPlacement(new Point(-popupSize.Width + offset.X + ShadowRoom, centeredY), PopupPrimaryAxis.Vertical) },
                ManagedShell.AppBar.AppBarEdge.Top => new[] { new CustomPopupPlacement(new Point(centeredX, targetSize.Height + offset.Y - ShadowRoom), PopupPrimaryAxis.Horizontal) },
                _ => new[] { new CustomPopupPlacement(new Point(centeredX, -popupSize.Height + offset.Y + ShadowRoom + FrameStyleMargin + sameAsMedia), PopupPrimaryAxis.Horizontal) },
            };
        }

        private void WeatherDisplay_Unloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            _forecastOpenTimer?.Stop();
            _forecastCloseTimer?.Stop();
            ClearForecastAnimations();
            ForecastPopup.IsOpen = false;
            Settings.Instance.PropertyChanged -= Settings_OnPropertyChanged;
            _timer?.Stop();
            _timer = null;
            _viewModel?.Dispose();
            _viewModel = null;
        }
    }

    /// <summary>
    /// Weather data comes from Open-Meteo (open-meteo.com) rather than wttr.in: it's free, needs
    /// no API key (important since RetroBar is distributed to other users, not just run by one
    /// person who could register their own key), has generous rate limits, and returns a proper
    /// WMO weather code instead of a free-text condition string that has to be guessed at via
    /// keyword matching. It takes latitude/longitude rather than a place name, so a location is
    /// resolved once via Open-Meteo's own (also free, keyless) geocoding endpoint and cached
    /// until Settings.WeatherLocation changes. It also reports is_day, which a clear sky (WMO 0/1)
    /// uses to show the moon instead of the sun icon at night - see GetIconFileName.
    /// </summary>
    public class WeatherViewModel : INotifyPropertyChanged, IDisposable
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        // Open-Meteo's free tier has a per-IP daily request cap (HTTP 429 once exceeded), and every
        // monitor's WeatherDisplay used to poll it every minute plus once per restart - enough to
        // burn through it. Weather barely changes minute to minute, so the result is shared
        // (static) across all instances and only refetched after SuccessRefresh.
        private static readonly TimeSpan SuccessRefresh = TimeSpan.FromMinutes(15);

        // How soon to retry after a failed fetch: short at first (so a not-yet-up network at boot, or a
        // blip, recovers within seconds instead of leaving a display on "N/A" for minutes) and backing
        // off while it keeps failing (e.g. a 429), up to FailureRetryMax.
        private static readonly TimeSpan FailureRetryMin = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan FailureRetryMax = TimeSpan.FromMinutes(5);

        private static string _geocodedLocation;
        private static string _geocodedDisplayName;
        private static string _geocodedCountryCode;
        private static double _latitude;
        private static double _longitude;

        private static string _cachedLocation;
        private static double? _cachedCelsius;
        private static string _cachedIconPath;
        private static List<DailyForecast> _cachedForecast = new();
        private static WeatherDetails _cachedDetails;
        private static List<HourlyForecast> _cachedHourly = new();
        private static int? _cachedWeatherCode;

        // Set when the network comes back (see OnNetworkAvailabilityChanged): the next update refetches
        // even though the cached reading isn't old yet, since the last attempt may have failed offline.
        private static bool _forceRefresh;
        private static DateTime _lastSuccessUtc = DateTime.MinValue;
        private static DateTime _lastAttemptUtc = DateTime.MinValue;
        private static int _consecutiveFailures;

        // The one in-flight fetch, shared by every monitor's display: a display that asks while another
        // is already fetching joins it instead of seeing "no data yet" and showing N/A.
        private static Task _fetchTask;

        /// <summary>Raised (on the UI thread) whenever the shared reading changes or a fetch
        /// finishes, so every monitor's display updates in the same moment instead of each waiting
        /// for its own next poll.</summary>
        private static event Action SharedStateChanged;

        static WeatherViewModel()
        {
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }

        public WeatherViewModel()
        {
            SharedStateChanged += OnSharedStateChanged;
            NetworkReconnected += OnNetworkReconnected;
            Settings.Instance.PropertyChanged += Settings_PropertyChanged;
        }

        public void Dispose()
        {
            SharedStateChanged -= OnSharedStateChanged;
            NetworkReconnected -= OnNetworkReconnected;
            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
        }

        private void OnSharedStateChanged()
        {
            ApplyShared();
        }

        private static event Action NetworkReconnected;

        // The connection came (back) up: refetch soon instead of waiting out the retry backoff or the
        // 15 minute refresh - a failed attempt while offline would otherwise leave N/A (or an old
        // reading) for minutes after the network is back.
        private static async void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            if (!e.IsAvailable)
            {
                return;
            }

            // DNS and routes usually aren't usable the instant the adapter reports available.
            await Task.Delay(TimeSpan.FromSeconds(3));

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                bool stale = _consecutiveFailures > 0 || DateTime.UtcNow - _lastSuccessUtc > TimeSpan.FromMinutes(5);
                if (!stale)
                {
                    return;
                }

                _forceRefresh = true;
                _consecutiveFailures = 0;
                _lastAttemptUtc = DateTime.MinValue;
                NetworkReconnected?.Invoke();
            }));
        }

        private void OnNetworkReconnected()
        {
            _ = UpdateWeatherAsync();
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.WeatherUseFahrenheit))
            {
                // °C <-> °F is just a display choice: reformat the reading we already have.
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(ApplyShared));
            }
            else if (e.PropertyName is nameof(Settings.Language) or nameof(Settings.OverrideClockFormat) or nameof(Settings.ClockFormat)
                     or nameof(Settings.OverrideAMPMDesignators) or nameof(Settings.AMDesignator) or nameof(Settings.PMDesignator))
            {
                // The days are written in the language chosen, and the times of the day as the clock's settings have them.
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(ApplyShared), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            else if (e.PropertyName == nameof(Settings.WeatherLocation))
            {
                // A new location is shown as soon as it's fetched, not on the next minute tick.
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => _ = UpdateWeatherAsync()));
            }
        }

        /// <summary>The next few days, for the forecast popup.</summary>
        public ObservableCollection<ForecastDay> Forecast { get; } = new();

        /// <summary>The next times that are a multiple of three hours, for the forecast popup.</summary>
        public ObservableCollection<HourlyItem> Hourly { get; } = new();

        private bool _hasHourly;
        public bool HasHourly
        {
            get => _hasHourly;
            set
            {
                if (_hasHourly != value)
                {
                    _hasHourly = value;
                    OnPropertyChanged();
                }
            }
        }

        private ImageSource _flagSource;
        public ImageSource FlagSource
        {
            get => _flagSource;
            set
            {
                if (!ReferenceEquals(_flagSource, value))
                {
                    _flagSource = value;
                    OnPropertyChanged();
                }
            }
        }

        // Windows has no flag emoji, so the country's flag is a small image (flagcdn.com, free and keyless)
        // - fetched once per country and kept; null (no flag shown) if it can't be loaded.
        private static readonly Dictionary<string, BitmapImage> FlagCache = new();

        private static ImageSource GetFlag(string countryCode)
        {
            if (string.IsNullOrWhiteSpace(countryCode))
            {
                return null;
            }

            string code = countryCode.Trim().ToLowerInvariant();

            if (!FlagCache.TryGetValue(code, out BitmapImage flag))
            {
                try
                {
                    flag = new BitmapImage();
                    flag.BeginInit();
                    flag.UriSource = new Uri($"https://flagcdn.com/w40/{code}.png");
                    flag.CacheOption = BitmapCacheOption.OnLoad;
                    flag.EndInit();
                    flag.DownloadFailed += (s, e) => FlagCache.Remove(code);
                }
                catch
                {
                    return null;
                }

                FlagCache[code] = flag;
            }

            return flag;
        }

        private string _locationTitle;
        public string LocationTitle
        {
            get => _locationTitle;
            set
            {
                if (_locationTitle != value)
                {
                    _locationTitle = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _weatherHeadline;

        /// <summary>The line under the location in the forecast popup: the temperature and the weather in words ("17°C · Clear").</summary>
        public string WeatherHeadline
        {
            get => _weatherHeadline;
            set
            {
                if (_weatherHeadline != value)
                {
                    _weatherHeadline = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>The conditions under the temperature in the forecast popup, two to a row.</summary>
        public ObservableCollection<DetailItem> DetailItems { get; } = new();

        private string _weatherIconPath;
        public string WeatherIconPath
        {
            get => _weatherIconPath;
            set
            {
                if (_weatherIconPath != value)
                {
                    _weatherIconPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(WeatherIconBounds));
                }
            }
        }

        // Where the drawing is in the icon's picture, as a share of it: the picture has some room round the drawing, which the big icon in
        // the forecast popup leaves out, so that the drawing is at the corner and not inset from it.
        public Rect WeatherIconBounds => IconBounds(_weatherIconPath);

        private static readonly Dictionary<string, Rect> IconBoundsCache = new();

        private static Rect IconBounds(string path)
        {
            var whole = new Rect(0, 0, 1, 1);
            if (string.IsNullOrEmpty(path))
            {
                return whole;
            }

            if (IconBoundsCache.TryGetValue(path, out Rect cached))
            {
                return cached;
            }

            Rect bounds = whole;
            try
            {
                var bitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    new System.Windows.Media.Imaging.BitmapImage(new Uri(path)), PixelFormats.Bgra32, null, 0);
                int width = bitmap.PixelWidth, height = bitmap.PixelHeight, stride = width * 4;
                var pixels = new byte[stride * height];
                bitmap.CopyPixels(pixels, stride, 0);

                int left = width, top = height, right = -1, bottom = -1;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (pixels[y * stride + x * 4 + 3] > 20)
                        {
                            left = Math.Min(left, x);
                            right = Math.Max(right, x);
                            top = Math.Min(top, y);
                            bottom = Math.Max(bottom, y);
                        }
                    }
                }

                if (right >= left && bottom >= top)
                {
                    bounds = new Rect((double)left / width, (double)top / height, (double)(right - left + 1) / width, (double)(bottom - top + 1) / height);
                }
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                // The whole picture, then.
            }

            IconBoundsCache[path] = bounds;
            return bounds;
        }

        private string _weatherTemp;
        public string WeatherTemp
        {
            get => _weatherTemp;
            set
            {
                if (_weatherTemp != value)
                {
                    _weatherTemp = value;
                    OnPropertyChanged();
                }
            }
        }

        // The temperature as the taskbar shows it: the number (with a minus below zero) and the degree sign, without the plus
        // sign or the unit letter, which the forecast popup keeps (WeatherTemp).
        private string _weatherBarTemp;
        public string WeatherBarTemp
        {
            get => _weatherBarTemp;
            set
            {
                if (_weatherBarTemp != value)
                {
                    _weatherBarTemp = value;
                    OnPropertyChanged();
                }
            }
        }

        // The weather in words (the forecast's headline has it after the temperature), and the temperatures of the next day, for the small graph.
        private string _weatherDescription;
        public string WeatherDescription
        {
            get => _weatherDescription;
            set
            {
                if (_weatherDescription != value)
                {
                    _weatherDescription = value;
                    OnPropertyChanged();
                }
            }
        }

        private double[] _hourlyTrend = Array.Empty<double>();
        public double[] HourlyTrend
        {
            get => _hourlyTrend;
            set
            {
                _hourlyTrend = value;
                OnPropertyChanged();
            }
        }

        private static string FormatBarTemperature(double celsius)
        {
            double value = Settings.Instance.WeatherUseFahrenheit ? celsius * 9 / 5 + 32 : celsius;
            return ((int)Math.Round(value, MidpointRounding.AwayFromZero)).ToString() + "°";
        }

        // The last good reading is kept on disk too (weather-cache.json), so that after a restart without a
        // connection the taskbar still shows the latest temperature that was recorded instead of "N/A". It is
        // only used for the location it was recorded for, and a fresh fetch replaces it as soon as one works
        // (_lastSuccessUtc stays at its minimum, so the first update refetches).
        private static readonly string PersistedReadingPath = "weather-cache.json".InLocalAppData();
        private static bool _persistedReadingLoaded;

        private sealed class PersistedReading
        {
            public string Location { get; set; }
            public double Celsius { get; set; }
            public string IconFile { get; set; }
            public List<DailyForecast> Forecast { get; set; }
            public WeatherDetails Details { get; set; }
            public List<HourlyForecast> Hourly { get; set; }
            public int? WeatherCode { get; set; }
            public string GeocodedDisplayName { get; set; }
            public string GeocodedCountryCode { get; set; }
            public double Latitude { get; set; }
            public double Longitude { get; set; }
        }

        private static void SavePersistedReading(string iconFile)
        {
            try
            {
                var reading = new PersistedReading
                {
                    Location = _cachedLocation,
                    Celsius = _cachedCelsius.Value,
                    IconFile = iconFile,
                    Forecast = _cachedForecast,
                    Details = _cachedDetails,
                    Hourly = _cachedHourly,
                    WeatherCode = _cachedWeatherCode,
                    GeocodedDisplayName = _geocodedDisplayName,
                    GeocodedCountryCode = _geocodedCountryCode,
                    Latitude = _latitude,
                    Longitude = _longitude,
                };
                File.WriteAllText(PersistedReadingPath, JsonSerializer.Serialize(reading));
            }
            catch
            {
                // Only a convenience: failing to save it changes nothing else.
            }
        }

        private static void LoadPersistedReading()
        {
            if (_persistedReadingLoaded)
            {
                return;
            }

            _persistedReadingLoaded = true;

            if (_cachedCelsius.HasValue)
            {
                return;
            }

            try
            {
                if (!File.Exists(PersistedReadingPath))
                {
                    return;
                }

                PersistedReading reading = JsonSerializer.Deserialize<PersistedReading>(File.ReadAllText(PersistedReadingPath));
                if (reading == null || string.IsNullOrWhiteSpace(reading.Location) || string.IsNullOrWhiteSpace(reading.IconFile))
                {
                    return;
                }

                _cachedLocation = reading.Location;
                _cachedCelsius = reading.Celsius;
                _cachedIconPath = GetImagePath(PersistedIconFile(reading.IconFile));
                _cachedForecast = reading.Forecast ?? new List<DailyForecast>();
                _cachedDetails = reading.Details;
                _cachedHourly = reading.Hourly ?? new List<HourlyForecast>();
                _cachedWeatherCode = reading.WeatherCode;

                // Without these a later fetch would skip geocoding (the location "is" geocoded) and use no coordinates.
                if (reading.Latitude != 0 || reading.Longitude != 0)
                {
                    _geocodedLocation = reading.Location;
                    _geocodedDisplayName = reading.GeocodedDisplayName;
                    _geocodedCountryCode = reading.GeocodedCountryCode;
                    _latitude = reading.Latitude;
                    _longitude = reading.Longitude;
                }
            }
            catch
            {
                // A missing or damaged file is the same as none.
            }
        }

        // The name of an icon as an earlier version kept it with the reading: the moon phases are one moon now, and the partly cloudy icons
        // have a day and a night one (the reading did not say which, and it is replaced by the next fetch).
        private static string PersistedIconFile(string iconFile) => iconFile switch
        {
            _ when iconFile.StartsWith("moon_", StringComparison.Ordinal) => "moon.png",
            "partly_cloudy.png" => "partly_cloudy_day.png",
            "partly_cloudy_rain.png" => "partly_cloudy_rain_day.png",
            _ => iconFile,
        };

        private static string GetImagePath(string fileName)
        {
            string fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", fileName);
            return new Uri(fullPath).AbsoluteUri;
        }

        public async Task UpdateWeatherAsync()
        {
            string location = Settings.Instance.WeatherLocation;

            if (string.IsNullOrWhiteSpace(location))
            {
                WeatherTemp = "N/A";
                WeatherBarTemp = "N/A";
                return;
            }

            bool cacheMatches = HasCacheFor(location);

            // Whatever is known is shown immediately - the shared reading if there is one, even a stale
            // one, rather than N/A while a fetch is pending.
            ApplyShared();

            if (cacheMatches && !_forceRefresh && DateTime.UtcNow - _lastSuccessUtc < SuccessRefresh)
            {
                return;
            }

            Task fetch = StartOrJoinFetch(location, cacheMatches);
            if (fetch != null)
            {
                await fetch;
            }
        }

        private static bool HasCacheFor(string location)
        {
            LoadPersistedReading();
            return _cachedCelsius.HasValue && string.Equals(location, _cachedLocation, StringComparison.OrdinalIgnoreCase);
        }

        // Starts the shared fetch, or joins the one already running; null if one was attempted too
        // recently (the retry backoff) and there is nothing to wait for.
        private static Task StartOrJoinFetch(string location, bool cacheMatches)
        {
            if (_fetchTask != null && !_fetchTask.IsCompleted)
            {
                return _fetchTask;
            }

            TimeSpan retryDelay = _consecutiveFailures == 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(Math.Min(FailureRetryMax.TotalSeconds, FailureRetryMin.TotalSeconds * Math.Pow(2, _consecutiveFailures - 1)));

            if (DateTime.UtcNow - _lastAttemptUtc < retryDelay)
            {
                return null;
            }

            _lastAttemptUtc = DateTime.UtcNow;
            _fetchTask = FetchAsync(location);
            return _fetchTask;
        }

        private static async Task FetchAsync(string location)
        {
            _forceRefresh = false;

            try
            {
                if (!string.Equals(location, _geocodedLocation, StringComparison.OrdinalIgnoreCase)
                    && !await GeocodeAsync(location))
                {
                    throw new InvalidOperationException("Location not found");
                }

                string url = string.Format(CultureInfo.InvariantCulture,
                    "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=temperature_2m,apparent_temperature,relative_humidity_2m,wind_speed_10m,weather_code,is_day&hourly=temperature_2m,weather_code,is_day&forecast_hours=24&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset&forecast_days=7&timezone=auto&temperature_unit=celsius",
                    _latitude, _longitude);

                string json = await _httpClient.GetStringAsync(url);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement current = doc.RootElement.GetProperty("current");
                JsonElement daily = doc.RootElement.GetProperty("daily");

                double temperature = current.GetProperty("temperature_2m").GetDouble();
                int weatherCode = current.GetProperty("weather_code").GetInt32();
                bool isDay = current.GetProperty("is_day").GetInt32() != 0;

                var forecast = new List<DailyForecast>();
                JsonElement days = daily.GetProperty("time");
                JsonElement codes = daily.GetProperty("weather_code");
                JsonElement highs = daily.GetProperty("temperature_2m_max");
                JsonElement lows = daily.GetProperty("temperature_2m_min");
                for (int i = 0; i < days.GetArrayLength(); i++)
                {
                    forecast.Add(new DailyForecast(
                        DateTime.ParseExact(days[i].GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                        codes[i].GetInt32(),
                        highs[i].GetDouble(),
                        lows[i].GetDouble()));
                }

                WeatherDetails details = null;
                if (current.TryGetProperty("apparent_temperature", out JsonElement feels) && current.TryGetProperty("relative_humidity_2m", out JsonElement humidity)
                    && current.TryGetProperty("wind_speed_10m", out JsonElement wind))
                {
                    int? rainChance = null;
                    if (daily.TryGetProperty("precipitation_probability_max", out JsonElement chances) && chances.GetArrayLength() > 0
                        && chances[0].ValueKind == JsonValueKind.Number)
                    {
                        rainChance = chances[0].GetInt32();
                    }

                    DateTime? sunrise = ReadLocalTime(daily, "sunrise");
                    DateTime? sunset = ReadLocalTime(daily, "sunset");
                    details = new WeatherDetails(feels.GetDouble(), (int)Math.Round(humidity.GetDouble()), wind.GetDouble(), rainChance, sunrise, sunset);
                }

                var hourly = new List<HourlyForecast>();
                if (doc.RootElement.TryGetProperty("hourly", out JsonElement hours))
                {
                    JsonElement times = hours.GetProperty("time");
                    JsonElement hourCodes = hours.GetProperty("weather_code");
                    JsonElement temperatures = hours.GetProperty("temperature_2m");
                    JsonElement daylight = hours.GetProperty("is_day");
                    for (int i = 0; i < times.GetArrayLength(); i++)
                    {
                        hourly.Add(new HourlyForecast(
                            DateTime.ParseExact(times[i].GetString(), "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
                            hourCodes[i].GetInt32(),
                            temperatures[i].GetDouble(),
                            daylight[i].GetInt32() != 0));
                    }
                }

                string iconFile = GetIconFileName(weatherCode, isDay);
                _cachedLocation = location;
                _cachedDetails = details;
                _cachedHourly = hourly;
                _cachedWeatherCode = weatherCode;
                _cachedCelsius = temperature;
                _cachedForecast = forecast;
                _cachedIconPath = GetImagePath(iconFile);
                _lastSuccessUtc = DateTime.UtcNow;
                _consecutiveFailures = 0;
                SavePersistedReading(iconFile);
            }
            catch
            {
                // Keep showing the last good reading through a transient failure (e.g. a 429 or a
                // dropped connection) rather than flipping to N/A; the retry backoff decides when to
                // try again.
                _consecutiveFailures++;
            }

            // Every monitor's display picks up the result (or the failure) at the same moment.
            SharedStateChanged?.Invoke();
        }

        // Today's sunrise or sunset, as the local time at the place that Open-Meteo gives it in ("2026-10-14T07:41").
        private static DateTime? ReadLocalTime(JsonElement daily, string name)
        {
            if (daily.TryGetProperty(name, out JsonElement values) && values.GetArrayLength() > 0 && values[0].ValueKind == JsonValueKind.String
                && DateTime.TryParseExact(values[0].GetString(), "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time))
            {
                return time;
            }

            return null;
        }

        // Shows the shared reading (or the one saved on disk) if it's for the location currently set; otherwise N/A.
        private void ApplyShared()
        {
            string location = Settings.Instance.WeatherLocation;
            LocationTitle = location;
            FlagSource = null;

            if (!string.IsNullOrWhiteSpace(location) && HasCacheFor(location))
            {
                if (string.Equals(location, _geocodedLocation, StringComparison.OrdinalIgnoreCase))
                {
                    LocationTitle = _geocodedDisplayName ?? location;
                    FlagSource = GetFlag(_geocodedCountryCode);
                }

                WeatherTemp = FormatTemperature(_cachedCelsius.Value);
                WeatherHeadline = FormatHeadline(_cachedCelsius.Value, _cachedWeatherCode);
                WeatherBarTemp = FormatBarTemperature(_cachedCelsius.Value);
                WeatherDescription = _cachedWeatherCode is int described ? DescribeWeather(described) : null;
                HourlyTrend = _cachedHourly.Take(24).Select(h => h.Celsius).ToArray();
                FillDetails();
                WeatherIconPath = _cachedIconPath;

                // The next times that are a multiple of three hours (0h, 3h, 6h...): the same times of the day whenever it is looked at,
                // an eighteen hours ahead in all.
                Hourly.Clear();
                DateTime now = _cachedHourly.Count > 0 ? _cachedHourly[0].Time : DateTime.MinValue;
                foreach (HourlyForecast hour in _cachedHourly.Where(h => h.Time > now && h.Time.Hour % 3 == 0).Take(6))
                {
                    string label = FormatClockTime(hour.Time, true);
                    Hourly.Add(new HourlyItem(label, GetImagePath(GetIconFileName(hour.WeatherCode, hour.IsDay)), FormatBarTemperature(hour.Celsius)));
                }

                HasHourly = Hourly.Count > 0;

                Forecast.Clear();
                foreach (DailyForecast day in _cachedForecast)
                {
                    // The day's name as the culture writes it ("ven. 9" in French), with its first letter capitalized.
                    string dayName = day.Date.ToString("ddd d", LanguageCulture.Current);
                    if (dayName.Length > 0)
                    {
                        dayName = char.ToUpper(dayName[0], LanguageCulture.Current) + dayName.Substring(1);
                    }

                    Forecast.Add(new ForecastDay(
                        dayName,
                        GetImagePath(GetIconFileName(day.WeatherCode, true)),
                        $"{FormatShortTemperature(day.HighCelsius)} / {FormatShortTemperature(day.LowCelsius)}",
                        day.Date.Date == DateTime.Today));
                }
            }
            else
            {
                WeatherTemp = "N/A";
                WeatherHeadline = "N/A";
                WeatherBarTemp = "N/A";
                WeatherDescription = null;
                HourlyTrend = Array.Empty<double>();
                DetailItems.Clear();
                Hourly.Clear();
                HasHourly = false;
                Forecast.Clear();
            }
        }

        // Under the temperature in the forecast popup, two to a row: how it feels and the humidity, the wind and the chance of rain today,
        // the sunrise and the sunset. The number of each is in bold.
        private void FillDetails()
        {
            DetailItems.Clear();
            WeatherDetails details = _cachedDetails;
            if (details == null)
            {
                return;
            }

            bool imperial = Settings.Instance.WeatherUseFahrenheit;
            double wind = imperial ? details.WindKmh * 0.621371 : details.WindKmh;

            DetailItems.Add(Detail("weather_feels_like", "Feels like {0}", FormatBarTemperature(details.FeelsLikeCelsius)));
            DetailItems.Add(Detail("weather_humidity", "Humidity {0}", details.Humidity + "%"));
            DetailItems.Add(Detail("weather_wind", "Wind {0}", Math.Round(wind) + (imperial ? " mph" : " km/h")));
            DetailItems.Add(details.RainChance is int rain
                ? Detail("weather_rain_chance", "Rain {0}", rain + "%")
                : new DetailItem("", "", ""));
            if (details.Sunrise is DateTime sunrise && details.Sunset is DateTime sunset)
            {
                DetailItems.Add(Detail("weather_sunrise", "Sunrise {0}", FormatClockTime(sunrise, false)));
                DetailItems.Add(Detail("weather_sunset", "Sunset {0}", FormatClockTime(sunset, false)));
            }
        }

        // A time of the day as the clock's settings (Properties, Clock) write it: with AM and PM (and the designators set there) if the
        // clock's format has the 12-hour hour, on the 24-hour clock if it has not. Without a format of its own, the clock's is the one of the
        // system's (regional) settings, as the clock has it. onTheHour: just the hour ("3 PM", "15:00"), not the minutes.
        private static string FormatClockTime(DateTime time, bool onTheHour)
        {
            CultureInfo language = CultureInfo.CurrentCulture;
            string pattern = Settings.Instance.OverrideClockFormat && !string.IsNullOrEmpty(Settings.Instance.ClockFormat)
                ? Settings.Instance.ClockFormat
                : language.DateTimeFormat.ShortTimePattern;

            if (!pattern.Contains('h') && !pattern.Contains("tt"))
            {
                return time.ToString("HH:mm", language);
            }

            CultureInfo culture = (CultureInfo)language.Clone();
            if (Settings.Instance.OverrideAMPMDesignators)
            {
                if (Settings.Instance.AMDesignator != "")
                {
                    culture.DateTimeFormat.AMDesignator = Settings.Instance.AMDesignator;
                }

                if (Settings.Instance.PMDesignator != "")
                {
                    culture.DateTimeFormat.PMDesignator = Settings.Instance.PMDesignator;
                }
            }

            return time.ToString(onTheHour ? "h tt" : "h:mm tt", culture);
        }

        // The text of a language string with the value in it, as what is before the value, the value, and what is after it.
        private static DetailItem Detail(string key, string fallback, string value)
        {
            string format = System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;
            char mark = (char)1;
            string[] parts = string.Format(format, mark).Split(mark);
            return new DetailItem(parts[0], value, parts.Length > 1 ? parts[1] : "");
        }

        private static async Task<bool> GeocodeAsync(string location)
        {
            string encodedLocation = Uri.EscapeDataString(location);
            string url = $"https://geocoding-api.open-meteo.com/v1/search?name={encodedLocation}&count=1&language=en&format=json";

            string json = await _httpClient.GetStringAsync(url);
            using JsonDocument doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("results", out JsonElement results) || results.GetArrayLength() == 0)
            {
                return false;
            }

            JsonElement first = results[0];
            _latitude = first.GetProperty("latitude").GetDouble();
            _longitude = first.GetProperty("longitude").GetDouble();

            // "Perpignan, France" - the place's own name and country, rather than whatever was typed.
            string name = first.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() : location;
            string country = first.TryGetProperty("country", out JsonElement countryElement) ? countryElement.GetString() : null;
            _geocodedDisplayName = string.IsNullOrWhiteSpace(country) ? name : $"{name}, {country}";
            _geocodedCountryCode = first.TryGetProperty("country_code", out JsonElement codeElement) ? codeElement.GetString() : null;

            _geocodedLocation = location;
            return true;
        }

        private static string FormatTemperature(double celsius)
        {
            bool fahrenheit = Settings.Instance.WeatherUseFahrenheit;
            int rounded = (int)Math.Round(fahrenheit ? celsius * 9 / 5 + 32 : celsius, MidpointRounding.AwayFromZero);
            string sign = rounded > 0 ? "+" : rounded < 0 ? "-" : "";
            return $"{sign}{Math.Abs(rounded)}\u00B0{(fahrenheit ? 'F' : 'C')}";
        }

        // The temperature with its unit, and the weather in words after it when it is known.
        private static string FormatHeadline(double celsius, int? weatherCode)
        {
            bool fahrenheit = Settings.Instance.WeatherUseFahrenheit;
            int rounded = (int)Math.Round(fahrenheit ? celsius * 9 / 5 + 32 : celsius, MidpointRounding.AwayFromZero);
            string temperature = $"{rounded}\u00B0";
            string description = weatherCode is int code ? DescribeWeather(code) : null;
            return description == null ? temperature : temperature + "  \u00B7  " + description;
        }

        // The weather in words, in the language chosen (WMO codes: https://open-meteo.com/en/docs).
        private static string DescribeWeather(int code)
        {
            (string key, string fallback)? words = code switch
            {
                0 => ("weather_clear", "Clear"),
                1 => ("weather_mostly_clear", "Mostly clear"),
                2 => ("weather_partly_cloudy", "Partly cloudy"),
                3 => ("weather_overcast", "Overcast"),
                45 or 48 => ("weather_fog", "Fog"),
                51 or 53 or 55 => ("weather_drizzle", "Drizzle"),
                56 or 57 or 66 or 67 => ("weather_freezing_rain", "Freezing rain"),
                61 or 63 => ("weather_rain", "Rain"),
                65 => ("weather_heavy_rain", "Heavy rain"),
                71 or 73 or 75 or 77 => ("weather_snow", "Snow"),
                80 or 81 or 82 => ("weather_showers", "Showers"),
                85 or 86 => ("weather_snow_showers", "Snow showers"),
                95 or 96 or 99 => ("weather_thunderstorm", "Thunderstorm"),
                _ => null,
            };

            return words is { } known ? System.Windows.Application.Current?.TryFindResource(known.key) as string ?? known.fallback : null;
        }

        // Without the sign or unit letter, for the forecast's "high / low" column.
        private static string FormatShortTemperature(double celsius)
        {
            double value = Settings.Instance.WeatherUseFahrenheit ? celsius * 9 / 5 + 32 : celsius;
            return $"{(int)Math.Round(value, MidpointRounding.AwayFromZero)}\u00B0";
        }

        // WMO weather codes (https://open-meteo.com/en/docs, "WMO Weather interpretation codes").
        // Icons are monochrome PNGs pre-rendered (at high resolution, then downscaled with
        // high-quality bitmap scaling) from the same vector shapes this app briefly rendered
        // live via WPF Path - live vector rendering came out visibly aliased on at least one
        // real system regardless of EdgeMode/rendering-tier/software-rendering settings (a
        // WPF/driver-level quirk outside the app's control), while bitmap rendering has always
        // been reliably smooth, so the shapes are delivered as bitmaps instead. The icons with a
        // sun in them have a night variant: clear sky (0/1) shows a single moon whatever the phase
        // (the phases were tried: the new moon has nothing to draw, and the others only their lit
        // part), and partly cloudy (2) and the showers (80) have a day and a night icon of their
        // own. The overcast/rain/snow/fog/thunder icons don't have a sun/moon in them to begin
        // with, so there's nothing for a "night" version to change. Falls back to cloud.png for any
        // WMO code Open-Meteo might add later that isn't one of the above.
        private static string GetIconFileName(int weatherCode, bool isDay) => weatherCode switch
        {
            0 or 1 => isDay ? "sun.png" : "moon.png",
            2 => isDay ? "partly_cloudy_day.png" : "partly_cloudy_night.png",
            3 => "cloud.png",
            45 or 48 => "fog.png",
            51 or 53 or 55 or 61 or 63 or 65 or 81 or 82 => "rain.png",
            56 or 57 or 66 or 67 or 71 or 73 or 75 or 77 or 85 or 86 => "snow.png",
            80 => isDay ? "partly_cloudy_rain_day.png" : "partly_cloudy_rain_night.png",
            95 or 96 or 99 => "thunder.png",
            _ => "cloud.png",
        };

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>The current conditions beyond the temperature, as fetched (Celsius and km/h, so the units can be switched without refetching).</summary>
    public record WeatherDetails(double FeelsLikeCelsius, int Humidity, double WindKmh, int? RainChance, DateTime? Sunrise = null, DateTime? Sunset = null);

    /// <summary>One hour of the forecast as fetched (raw Celsius).</summary>
    public record HourlyForecast(DateTime Time, int WeatherCode, double Celsius, bool IsDay);

    /// <summary>One of the conditions under the temperature: what is before its value, the value (in bold), what is after it.</summary>
    public record DetailItem(string Prefix, string Value, string Suffix);

    /// <summary>One hour as the forecast popup shows it.</summary>
    public record HourlyItem(string Label, string IconPath, string Temp);

    /// <summary>One day of the forecast as fetched (raw Celsius, so °C/°F can be switched without refetching).</summary>
    public record DailyForecast(DateTime Date, int WeatherCode, double HighCelsius, double LowCelsius);

    /// <summary>One row of the forecast popup.</summary>
    public record ForecastDay(string DayName, string IconPath, string Range, bool IsToday);
}
