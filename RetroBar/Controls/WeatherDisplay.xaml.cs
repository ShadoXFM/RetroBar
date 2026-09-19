using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
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
            _timer.Interval = TimeSpan.FromSeconds(30);

            _timer.Tick += async (sender, args) => await vm.UpdateWeatherAsync();
            _timer.Start();
        }
    }

    public class WeatherViewModel : INotifyPropertyChanged
    {
        private static readonly HttpClient _httpClient = new HttpClient();

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
            string location = "Perpignan";

            try
            {
                string encodedLocation = Uri.EscapeDataString(location);

                Task<string> conditionTask = _httpClient.GetStringAsync($"https://wttr.in/{encodedLocation}?format=%C");
                Task<string> tempTask = _httpClient.GetStringAsync($"https://wttr.in/{encodedLocation}?format=%t");

                await Task.WhenAll(conditionTask, tempTask);

                string condition = (await conditionTask).ToLower().Trim();
                WeatherTemp = (await tempTask).Trim();

                if (condition.Contains("thunder") || condition.Contains("lightning") || condition.Contains("storm"))
                {
                    WeatherIconPath = GetImagePath("thunder.png");
                }
                else if (condition.Contains("snow") || condition.Contains("blizzard") || condition.Contains("sleet") || condition.Contains("ice"))
                {
                    WeatherIconPath = GetImagePath("snow.png");
                }
                else if ((condition.Contains("partly") || condition.Contains("patchy") || condition.Contains("scattered")) && condition.Contains("rain"))
                {
                    WeatherIconPath = GetImagePath("partly_cloudy_rain.png");
                }
                else if (condition.Contains("rain") || condition.Contains("shower") || condition.Contains("drizzle"))
                {
                    WeatherIconPath = GetImagePath("rain.png");
                }
                else if (condition.Contains("fog") || condition.Contains("mist") || condition.Contains("haze") || condition.Contains("smoke"))
                {
                    WeatherIconPath = GetImagePath("fog.png");
                }
                else if (condition.Contains("partly") || condition.Contains("patchy") || condition.Contains("scattered"))
                {
                    WeatherIconPath = GetImagePath("partly_cloudy.png");
                }
                else if (condition.Contains("cloud") || condition.Contains("overcast"))
                {
                    WeatherIconPath = GetImagePath("cloud.png");
                }
                else if (condition.Contains("sun") || condition.Contains("clear") || condition.Contains("fair"))
                {
                    WeatherIconPath = GetImagePath("sun.png");
                }
                else
                {
                    WeatherIconPath = GetImagePath("default.png");
                }
            }
            catch
            {
                WeatherTemp = "N/A";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}