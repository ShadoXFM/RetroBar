using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;

namespace RetroBar.Utilities
{
    public class DictionaryManager : IDisposable
    {
        private const string DICT_DEFAULT = "System";
        private const string DICT_EXT = "xaml";

        private const string LANG_DEFAULT = DICT_DEFAULT;
        private const string LANG_FALLBACK = "English";
        private const string LANG_FOLDER = "Languages";
        private const string LANG_EXT = DICT_EXT;

        public const string THEME_DEFAULT = DICT_DEFAULT;
        private const string THEME_FOLDER = "Themes";
        private const string THEME_EXT = DICT_EXT;

        public DictionaryManager()
        {
            Settings.Instance.PropertyChanged += Settings_PropertyChanged;
            StartThemeWatchers();
        }

        // ---- applying the theme again when its file is saved, like monitor-adjustments.json is read again
        private readonly System.Collections.Generic.List<FileSystemWatcher> _themeWatchers = new();
        private System.Threading.Timer _themeReloadTimer;

        private void StartThemeWatchers()
        {
            // The two places a theme is loaded from besides the app's own folder (see SetDictionary): the app's Themes folder,
            // and the one in %LocalAppData%\RetroBar.
            foreach (string dir in new[] { Path.Combine(AppDomain.CurrentDomain.BaseDirectory, THEME_FOLDER), THEME_FOLDER.InLocalAppData() })
            {
                try
                {
                    if (!Directory.Exists(dir))
                    {
                        continue;
                    }

                    var watcher = new FileSystemWatcher(dir, "*." + THEME_EXT)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        EnableRaisingEvents = true,
                    };

                    watcher.Changed += ThemeFileChanged;
                    watcher.Created += ThemeFileChanged;
                    watcher.Renamed += ThemeFileChanged;
                    _themeWatchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    ManagedShell.Common.Logging.ShellLogger.Warning($"DictionaryManager: Unable to watch {dir} for theme changes: {ex.Message}");
                }
            }
        }


        private void ThemeFileChanged(object sender, FileSystemEventArgs e)
        {
            // Only the theme in use matters (System, the base of every theme, is built into the app).
            if (!string.Equals(Path.GetFileNameWithoutExtension(e.Name), Settings.Instance.Theme, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // An editor saves in several steps (a change event for each, sometimes through a temporary file), so wait
            // until it is quiet for a moment, and then read the file whole.
            _themeReloadTimer ??= new System.Threading.Timer(_ => ReloadTheme(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _themeReloadTimer.Change(300, System.Threading.Timeout.Infinite);
        }

        private void ReloadTheme()
        {
            Application app = Application.Current;
            if (app == null)
            {
                return;
            }

            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                string path = FindThemeFile(Settings.Instance.Theme);
                if (path == null)
                {
                    return;
                }

                // A theme that doesn't load (saved half way through an edit, or with a typo in it) is left alone: the theme in use
                // stays as it is, instead of the taskbar losing all its colors until the file is fixed.
                try
                {
                    _ = new ResourceDictionary { Source = new Uri(path, UriKind.RelativeOrAbsolute) };
                }
                catch (Exception ex)
                {
                    ManagedShell.Common.Logging.ShellLogger.Warning($"DictionaryManager: Theme file {path} can't be loaded yet, keeping the theme in use: {ex.Message}");
                    return;
                }

                ManagedShell.Common.Logging.ShellLogger.Info($"DictionaryManager: Theme file {path} changed, applying it again");
                Settings.Instance.NotifyThemeChanged();
            }));
        }

        /// <summary>The file a theme is loaded from (the same places SetDictionary looks in), or null.</summary>
        private static string FindThemeFile(string theme)
        {
            string[] candidates =
            {
                Path.ChangeExtension(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, THEME_FOLDER, theme), THEME_EXT),
                Path.ChangeExtension(THEME_FOLDER.InLocalAppData(theme), THEME_EXT),
                Path.ChangeExtension(Path.Combine(Path.GetDirectoryName(ExePath.GetExecutablePath()), THEME_FOLDER, theme), THEME_EXT),
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        public void SetThemeFromSettings()
        {
            SetTheme(THEME_DEFAULT);

            if (Settings.Instance.Theme.StartsWith(THEME_DEFAULT))
            {
                SetSystemThemeParams();
            }
            else
            {
                ClearSystemThemeParams();
            }

            if (Settings.Instance.Theme != THEME_DEFAULT)
            {
                SetTheme(Settings.Instance.Theme);
            }

            UpdateTaskbarFace();

            // The checkered patterns are built from the theme's colors: build them again from the new theme's, once the
            // windows have picked its resources up.
            Application.Current.Dispatcher.BeginInvoke(new Action(PixelPatternBrush.RefreshAll), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Sets the face of the things on the taskbar (the TaskbarFace resource): the theme's own ButtonFace, or nothing at all
        /// when the theme gives the taskbar a gradient (an opaque TaskbarBackgroundStart or TaskbarBackgroundEnd), so that the
        /// gradient shows through the buttons and the tray box and not just between them.
        /// </summary>
        private static void UpdateTaskbarFace()
        {
            ResourceDictionary resources = Application.Current.Resources;

            bool gradient = resources["TaskbarBackgroundStart"] is System.Windows.Media.Color start &&
                            resources["TaskbarBackgroundEnd"] is System.Windows.Media.Color end &&
                            (start.A > 0 || end.A > 0);

            resources["TaskbarFace"] = gradient
                ? System.Windows.Media.Brushes.Transparent
                : resources["ButtonFace"];

            // The taskbar's own edge line: the theme's TaskbarTopLine if it has one, else its ButtonHighlight.
            resources["TaskbarHighlight"] = resources["TaskbarTopLine"] ?? resources["ButtonHighlight"];
            resources["TaskbarOuterLine"] = resources["TaskbarTopLineOuter"] ?? resources["ButtonLight"];
        }

        private void SetSystemThemeParams()
        {
            Application.Current.Resources["GlobalFontFamily"] = SystemFonts.CaptionFontFamily;
        }

        private void ClearSystemThemeParams()
        {
            Application.Current.Resources.Remove("GlobalFontFamily");
        }

        private void SetTheme(string theme)
        {
            SetDictionary(theme, THEME_FOLDER, THEME_DEFAULT, THEME_EXT, 0);
        }

        private static Collection<ResourceDictionary> GetMergedDictionaries()
        {
            return Application.Current.Resources.MergedDictionaries;
        }

        private static ResourceDictionary GetActualThemeDictionary()
        {
            foreach (ResourceDictionary rd in GetMergedDictionaries()
                .Where(rd => rd.Source.ToString().Contains($"{THEME_FOLDER}/")))
            {
                return rd;
            }

            return null;
        }

        private void ClearPreviousThemes()
        {
            // All of them: System (the base of every theme) and the theme on top of it, whichever ones were loaded before. Only the
            // first was removed, so every theme change (and reload of a theme's file) left one more theme's dictionary behind.
            ResourceDictionary previous;
            while ((previous = GetActualThemeDictionary()) != null)
            {
                _ = GetMergedDictionaries().Remove(previous);
            }
        }

        public void SetLanguageFromSettings()
        {
            SetLanguage(LANG_FALLBACK);
            if (Settings.Instance.Language == LANG_DEFAULT)
            {
                var currentUICulture = System.Globalization.CultureInfo.CurrentUICulture;
                string systemLanguageParent = currentUICulture.Parent.NativeName;
                string systemLanguage = currentUICulture.NativeName;
                ManagedShell.Common.Logging.ShellLogger.Info
                    ($"Loading system language (if available): {systemLanguageParent}, {systemLanguage}");
                SetLanguage(systemLanguageParent);
                SetLanguage(systemLanguage);
            }
            else
            {
                SetLanguage(Settings.Instance.Language);
            }
        }

        private void SetLanguage(string language)
        {
            SetDictionary(language, LANG_FOLDER, LANG_FALLBACK, LANG_EXT, 1);
        }

        private void SetDictionary(string dictionary, string dictFolder, string dictDefault, string dictExtension, int dictType)
        {
            string dictFilePath;

            if (dictionary == dictDefault)
            {
                if (dictType == 0)
                {
                    ClearPreviousThemes();
                }
                dictFilePath = Path.ChangeExtension(Path.Combine(dictFolder, dictDefault), dictExtension);
            }
            else
            {
                // Built-in dictionary
                dictFilePath = Path.ChangeExtension(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dictFolder, dictionary),
                                                    dictExtension);

                if (!File.Exists(dictFilePath))
                {
                    // Installed dictionary in AppData directory
                    dictFilePath = Path.ChangeExtension(dictFolder.InLocalAppData(dictionary), dictExtension);

                    if (!File.Exists(dictFilePath))
                    {
                        // Custom dictionary in app directory
                        dictFilePath =
                            Path.ChangeExtension(Path.Combine(Path.GetDirectoryName(ExePath.GetExecutablePath()), dictFolder, dictionary),
                                                 dictExtension);

                        if (!File.Exists(dictFilePath))
                        {
                            return;
                        }
                    }
                }
            }

            try
            {
                GetMergedDictionaries().Add(new ResourceDictionary()
                {
                    Source = new Uri(dictFilePath, UriKind.RelativeOrAbsolute)
                });
            }
            catch (Exception e)
            {
                ManagedShell.Common.Logging.ShellLogger.Error($"Error loading dictionaries: {e.Message} {e.InnerException?.Message}");
            }
        }

        public string GetThemeInstallDir()
        {
            return THEME_FOLDER.InLocalAppData();
        }

        public List<string> GetThemes()
        {
            return GetDictionaries(THEME_DEFAULT, THEME_FOLDER, THEME_EXT);
        }

        public List<string> GetLanguages()
        {
            List<string> languages = new List<string> { LANG_DEFAULT };
            languages.AddRange(GetDictionaries(LANG_FALLBACK, LANG_FOLDER, LANG_EXT));
            return languages;
        }

        private List<string> GetDictionaries(string dictDefault, string dictFolder, string dictExtension)
        {
            List<string> dictionaries = new List<string> { dictDefault };

            // Built-in dictionaries
            dictionaries.AddFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dictFolder), dictExtension);

            // Installed AppData dictionaries
            dictionaries.AddFrom(dictFolder.InLocalAppData(), dictExtension);

            // Same-folder dictionaries
            // Because RetroBar is published as a single-file app, it gets extracted to a temp directory, so custom dictionaries won't be there.
            // Get the executable path to find the custom dictionaries directory when not a debug build.
            dictionaries.AddFrom(Path.Combine(Path.GetDirectoryName(ExePath.GetExecutablePath()), dictFolder), dictExtension, true);

            return dictionaries;
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.Language))
            {
                SetLanguageFromSettings();
            }
            if (e.PropertyName == nameof(Settings.Theme))
            {
                SetThemeFromSettings();
            }
        }

        public void Dispose()
        {
            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;

            foreach (FileSystemWatcher watcher in _themeWatchers)
            {
                watcher.Dispose();
            }

            _themeWatchers.Clear();
            _themeReloadTimer?.Dispose();
        }
    }
}