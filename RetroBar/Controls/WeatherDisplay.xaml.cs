using RetroBar.Utilities;
using System;
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

        public WeatherDisplay()
        {
            InitializeComponent();

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

            // Fetch immediately on load (or, if another monitor's display already has the reading,
            // show it right away - see WeatherViewModel)
            _ = _viewModel.UpdateWeatherAsync();

            // Set up the timer
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMinutes(1);

            _timer.Tick += async (sender, args) => await _viewModel.UpdateWeatherAsync();
            _timer.Start();
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

            // Open on the side of the widget facing into the screen, whichever edge the taskbar is on, and
            // centered on the widget along the taskbar (see PlaceForecast).
            ForecastPopup.PlacementTarget = WeatherPanel;
            ForecastPopup.Placement = PlacementMode.Custom;
            ForecastPopup.CustomPopupPlacementCallback = PlaceForecast;

            // Show the latest reading, and refresh it in the background if it has gone stale.
            _ = _viewModel?.UpdateWeatherAsync();
            ShowForecast();
        }

        // The popup slides in from the taskbar's edge while fading in, and back out the same way. The
        // slide is the popup's own offset along the axis facing away from the taskbar (added to the
        // placement in PlaceForecast); the fade is the opacity of its frame.
        private const double ForecastSlideDistance = 12;
        private static readonly Duration ForecastShowDuration = new(TimeSpan.FromMilliseconds(180));
        private static readonly Duration ForecastHideDuration = new(TimeSpan.FromMilliseconds(130));

        private bool _forecastClosing;

        // The offset property to slide, and where it starts (the resting offset is 0).
        private static (DependencyProperty Property, double Start) ForecastSlideAxis()
        {
            return Settings.Instance.Edge switch
            {
                ManagedShell.AppBar.AppBarEdge.Left => (Popup.HorizontalOffsetProperty, -ForecastSlideDistance),
                ManagedShell.AppBar.AppBarEdge.Right => (Popup.HorizontalOffsetProperty, ForecastSlideDistance),
                ManagedShell.AppBar.AppBarEdge.Top => (Popup.VerticalOffsetProperty, -ForecastSlideDistance),
                _ => (Popup.VerticalOffsetProperty, ForecastSlideDistance),
            };
        }

        private void ClearForecastAnimations()
        {
            ForecastPopup.BeginAnimation(Popup.HorizontalOffsetProperty, null);
            ForecastPopup.BeginAnimation(Popup.VerticalOffsetProperty, null);
            ForecastFrame.BeginAnimation(UIElement.OpacityProperty, null);
        }

        private void ShowForecast()
        {
            (DependencyProperty axis, double start) = ForecastSlideAxis();

            // Still on screen (sliding away): carry on from where it is.
            bool resuming = ForecastPopup.IsOpen;
            double fromOffset = resuming ? (double)ForecastPopup.GetValue(axis) : start;
            double fromOpacity = resuming ? ForecastFrame.Opacity : 0;

            _forecastClosing = false;
            ClearForecastAnimations();
            ForecastFrame.Opacity = fromOpacity;
            ForecastPopup.IsOpen = true;

            ForecastPopup.BeginAnimation(axis, new DoubleAnimation
            {
                From = fromOffset,
                Duration = ForecastShowDuration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
            ForecastFrame.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(fromOpacity, 1, ForecastShowDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
        }

        private void HideForecast()
        {
            if (!ForecastPopup.IsOpen || _forecastClosing)
            {
                return;
            }

            _forecastClosing = true;
            (DependencyProperty axis, double start) = ForecastSlideAxis();

            ForecastPopup.BeginAnimation(axis, new DoubleAnimation
            {
                From = (double)ForecastPopup.GetValue(axis),
                To = start,
                Duration = ForecastHideDuration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            });

            var fade = new DoubleAnimation(ForecastFrame.Opacity, 0, ForecastHideDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            fade.Completed += (s, e) =>
            {
                // Only if it wasn't brought back (or closed some other way) while sliding away.
                if (_forecastClosing)
                {
                    _forecastClosing = false;
                    ClearForecastAnimations();
                    ForecastPopup.IsOpen = false;
                }
            };
            ForecastFrame.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private static CustomPopupPlacement[] PlaceForecast(Size popupSize, Size targetSize, Point offset)
        {
            double centeredX = (targetSize.Width - popupSize.Width) / 2;
            double centeredY = (targetSize.Height - popupSize.Height) / 2;

            // offset is the popup's slide animation (see ShowForecast); zero while at rest.
            return Settings.Instance.Edge switch
            {
                ManagedShell.AppBar.AppBarEdge.Left => new[] { new CustomPopupPlacement(new Point(targetSize.Width + offset.X, centeredY), PopupPrimaryAxis.Vertical) },
                ManagedShell.AppBar.AppBarEdge.Right => new[] { new CustomPopupPlacement(new Point(-popupSize.Width + offset.X, centeredY), PopupPrimaryAxis.Vertical) },
                ManagedShell.AppBar.AppBarEdge.Top => new[] { new CustomPopupPlacement(new Point(centeredX, targetSize.Height + offset.Y), PopupPrimaryAxis.Horizontal) },
                _ => new[] { new CustomPopupPlacement(new Point(centeredX, -popupSize.Height + offset.Y), PopupPrimaryAxis.Horizontal) },
            };
        }

        private void WeatherDisplay_Unloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            _forecastOpenTimer?.Stop();
            _forecastCloseTimer?.Stop();
            _forecastClosing = false;
            ClearForecastAnimations();
            ForecastPopup.IsOpen = false;
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
    /// until Settings.WeatherLocation changes. It also reports is_day and the current lunar
    /// phase, which a clear sky (WMO 0/1) uses to show the correct one of the 8 moon phase icons
    /// at night instead of the sun icon - see GetIconFileName/GetMoonPhaseIconFileName.
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
            else if (e.PropertyName == nameof(Settings.WeatherLocation))
            {
                // A new location is shown as soon as it's fetched, not on the next minute tick.
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => _ = UpdateWeatherAsync()));
            }
        }

        /// <summary>The next few days, for the forecast popup.</summary>
        public ObservableCollection<ForecastDay> Forecast { get; } = new();

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
                }
            }
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
                    "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=temperature_2m,weather_code,is_day&daily=weather_code,temperature_2m_max,temperature_2m_min,moon_phase&forecast_days=7&timezone=auto&temperature_unit=celsius",
                    _latitude, _longitude);

                string json = await _httpClient.GetStringAsync(url);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement current = doc.RootElement.GetProperty("current");
                JsonElement daily = doc.RootElement.GetProperty("daily");

                double temperature = current.GetProperty("temperature_2m").GetDouble();
                int weatherCode = current.GetProperty("weather_code").GetInt32();
                bool isDay = current.GetProperty("is_day").GetInt32() != 0;
                double moonPhase = daily.GetProperty("moon_phase")[0].GetDouble();

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

                _cachedLocation = location;
                _cachedCelsius = temperature;
                _cachedForecast = forecast;
                _cachedIconPath = GetImagePath(GetIconFileName(weatherCode, isDay, moonPhase));
                _lastSuccessUtc = DateTime.UtcNow;
                _consecutiveFailures = 0;
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

        // Shows the shared reading if it's for the location currently set; otherwise N/A.
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
                WeatherIconPath = _cachedIconPath;

                Forecast.Clear();
                foreach (DailyForecast day in _cachedForecast)
                {
                    Forecast.Add(new ForecastDay(
                        day.Date.ToString("ddd d", CultureInfo.CurrentUICulture),
                        GetImagePath(GetIconFileName(day.WeatherCode, true, 0)),
                        $"{FormatShortTemperature(day.HighCelsius)} / {FormatShortTemperature(day.LowCelsius)}"));
                }
            }
            else
            {
                WeatherTemp = "N/A";
                Forecast.Clear();
            }
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
        // been reliably smooth, so the shapes are delivered as bitmaps instead. Only clear sky
        // (0/1) gets a night variant - the only assets on hand are the 8 moon phases, and
        // overcast/rain/snow/fog/thunder icons don't have a sun/moon in them to begin with, so
        // there's nothing for a "night" version to change. Falls back to cloud.png for any
        // WMO code Open-Meteo might add later that isn't one of the above.
        private static string GetIconFileName(int weatherCode, bool isDay, double moonPhase) => weatherCode switch
        {
            0 or 1 => isDay ? "sun.png" : GetMoonPhaseIconFileName(moonPhase),
            2 => "partly_cloudy.png",
            3 => "cloud.png",
            45 or 48 => "fog.png",
            51 or 53 or 55 or 61 or 63 or 65 or 81 or 82 => "rain.png",
            56 or 57 or 66 or 67 or 71 or 73 or 75 or 77 or 85 or 86 => "snow.png",
            80 => "partly_cloudy_rain.png",
            95 or 96 or 99 => "thunder.png",
            _ => "cloud.png",
        };

        // moonPhase is a 0-1 fraction of the lunar cycle (0/1 = new, 0.25 = first quarter,
        // 0.5 = full, 0.75 = last quarter). Bucketed into 8 equal slices, each centered on one
        // of the 8 standard phase names.
        private static string GetMoonPhaseIconFileName(double moonPhase)
        {
            double phase = moonPhase - Math.Floor(moonPhase); // normalize into [0, 1)
            int bucket = (int)Math.Round(phase / 0.125) % 8;

            return bucket switch
            {
                0 => "moon_new.png",
                1 => "moon_waxing_crescent.png",
                2 => "moon_first_quarter.png",
                3 => "moon_waxing_gibbous.png",
                4 => "moon_full.png",
                5 => "moon_waning_gibbous.png",
                6 => "moon_last_quarter.png",
                _ => "moon_waning_crescent.png",
            };
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>One day of the forecast as fetched (raw Celsius, so °C/°F can be switched without refetching).</summary>
    public record DailyForecast(DateTime Date, int WeatherCode, double HighCelsius, double LowCelsius);

    /// <summary>One row of the forecast popup.</summary>
    public record ForecastDay(string DayName, string IconPath, string Range);
}
