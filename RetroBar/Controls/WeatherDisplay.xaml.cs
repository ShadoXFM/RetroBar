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

        public WeatherDisplay()
        {
            InitializeComponent();

            // Wait until the control is fully loaded before starting the timer
            this.Loaded += WeatherDisplay_Loaded;
        }

        private void WeatherDisplay_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            var vm = new WeatherViewModel();
            DataContext = vm;

            // Fetch immediately on load
            _ = vm.UpdateWeatherAsync();

            // Set up the timer
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMinutes(1);

            _timer.Tick += async (sender, args) => await vm.UpdateWeatherAsync();
            _timer.Start();
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
    public class WeatherViewModel : INotifyPropertyChanged
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        private string _geocodedLocation;
        private double _latitude;
        private double _longitude;

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

        private string GetImagePath(string fileName)
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

            try
            {
                if (!string.Equals(location, _geocodedLocation, StringComparison.OrdinalIgnoreCase)
                    && !await GeocodeAsync(location))
                {
                    WeatherTemp = "N/A";
                    return;
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

                WeatherTemp = FormatTemperature(temperature);
                WeatherIconPath = GetImagePath(GetIconFileName(weatherCode, isDay, moonPhase));
            }
            catch
            {
                WeatherTemp = "N/A";
            }
        }

        private async Task<bool> GeocodeAsync(string location)
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
