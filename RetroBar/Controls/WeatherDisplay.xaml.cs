using RetroBar.Utilities;
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
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

        private void WeatherDisplay_Unloaded(object sender, System.Windows.RoutedEventArgs e)
        {
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
        private static double _latitude;
        private static double _longitude;

        private static string _cachedLocation;
        private static string _cachedTemp;
        private static string _cachedIconPath;
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

        public WeatherViewModel()
        {
            SharedStateChanged += OnSharedStateChanged;
            Settings.Instance.PropertyChanged += Settings_PropertyChanged;
        }

        public void Dispose()
        {
            SharedStateChanged -= OnSharedStateChanged;
            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
        }

        private void OnSharedStateChanged()
        {
            ApplyShared();
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.WeatherLocation))
            {
                // A new location is shown as soon as it's fetched, not on the next minute tick.
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => _ = UpdateWeatherAsync()));
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

            if (cacheMatches && DateTime.UtcNow - _lastSuccessUtc < SuccessRefresh)
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
            return _cachedTemp != null && string.Equals(location, _cachedLocation, StringComparison.OrdinalIgnoreCase);
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
            try
            {
                if (!string.Equals(location, _geocodedLocation, StringComparison.OrdinalIgnoreCase)
                    && !await GeocodeAsync(location))
                {
                    throw new InvalidOperationException("Location not found");
                }

                string url = string.Format(CultureInfo.InvariantCulture,
                    "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=temperature_2m,weather_code,is_day&daily=moon_phase&timezone=auto&temperature_unit=celsius",
                    _latitude, _longitude);

                string json = await _httpClient.GetStringAsync(url);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement current = doc.RootElement.GetProperty("current");
                JsonElement daily = doc.RootElement.GetProperty("daily");

                double temperature = current.GetProperty("temperature_2m").GetDouble();
                int weatherCode = current.GetProperty("weather_code").GetInt32();
                bool isDay = current.GetProperty("is_day").GetInt32() != 0;
                double moonPhase = daily.GetProperty("moon_phase")[0].GetDouble();

                _cachedLocation = location;
                _cachedTemp = FormatTemperature(temperature);
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

            if (!string.IsNullOrWhiteSpace(location) && HasCacheFor(location))
            {
                WeatherTemp = _cachedTemp;
                WeatherIconPath = _cachedIconPath;
            }
            else
            {
                WeatherTemp = "N/A";
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
            _geocodedLocation = location;
            return true;
        }

        private static string FormatTemperature(double celsius)
        {
            int rounded = (int)Math.Round(celsius, MidpointRounding.AwayFromZero);
            string sign = rounded > 0 ? "+" : rounded < 0 ? "-" : "";
            return $"{sign}{Math.Abs(rounded)}°C";
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
}
