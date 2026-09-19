using ManagedShell.Common.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Per-monitor visual adjustments (see MonitorOffset.cs / MonitorOffsetValue.cs), loaded from
    /// a hand-editable JSON file instead of a Properties window tab. This lives in its own file
    /// rather than inside settings.json, because settings.json is rewritten wholesale by
    /// SettingsManager on every unrelated setting change, which makes it a bad place for
    /// something meant to be hand-edited in a text editor - an edit could get clobbered by an
    /// unrelated setting changing a moment later.
    ///
    /// File location: %LocalAppData%\RetroBar\monitor-adjustments.json (created automatically,
    /// with a documentation-only template, the first time RetroBar starts without one).
    ///
    /// Format:
    /// {
    ///   "\\\\.\\DISPLAY1": {
    ///     "TaskIcon": { "Y": 1 },
    ///     "MediaGlyphPrevious": { "Scale": 1.1, "TextRendering": "ClearType" }
    ///   }
    /// }
    ///
    /// The outer key is a monitor's AppBarScreen.DeviceName (Windows' internal display name,
    /// typically \.\DISPLAY1, \.\DISPLAY2, etc. - JSON-escaped that's "\\\\.\\DISPLAY1" since
    /// each backslash needs doubling; if you're not sure which is which, just try one and see
    /// whether the adjustment shows up on the monitor you expected). The inner
    /// key is whatever string an element's utilities:MonitorOffset.Tag="..." attribute uses in
    /// the app's XAML (TaskIcon, TaskIconActive, TaskLabel, MediaGlyphPrevious,
    /// MediaGlyphPlayPause, MediaGlyphNext, MediaAlbumArt, MediaTrackText, TrayToggleButton, or
    /// a new one you tag yourself). Every
    /// field on MonitorOffsetValue is optional - X, Y, Width, Height, Scale, TextRendering,
    /// BitmapScaling - omit whatever you don't want to change.
    ///
    /// The file is re-read automatically whenever it changes on disk (save it in any editor
    /// while RetroBar is running and the change applies immediately - no restart needed).
    /// Comments and trailing commas are allowed when reading, even though that's not strictly
    /// valid JSON, since this file is meant to be hand-edited.
    /// </summary>
    public static class MonitorAdjustments
    {
        /// <summary>Raised (on a background thread) whenever the file is reloaded after changing on disk.</summary>
        public static event EventHandler Changed;

        private static readonly string FilePath = "monitor-adjustments.json".InLocalAppData();

        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private const string Template =
            "{\n" +
            "  // Per-monitor visual adjustments for RetroBar. See MonitorAdjustments.cs for the\n" +
            "  // full format, or use this example (uncomment/edit it, and remove the outer braces\n" +
            "  // duplication if you paste it in):\n" +
            "  //\n" +
            "  // \"\\\\\\\\.\\\\DISPLAY1\": {\n" +
            "  //   \"TaskIcon\": { \"Y\": 1 },\n" +
            "  //   \"MediaGlyphPrevious\": { \"Scale\": 1.1, \"TextRendering\": \"ClearType\" }\n" +
            "  // }\n" +
            "}\n";

        private static Dictionary<string, Dictionary<string, MonitorOffsetValue>> _offsets;
        private static FileSystemWatcher _watcher;

        static MonitorAdjustments()
        {
            EnsureFileExists();
            _offsets = Load();
            _watcher = CreateWatcher();
        }

        public static MonitorOffsetValue Get(string deviceName, string tag)
        {
            if (!string.IsNullOrEmpty(deviceName) && !string.IsNullOrEmpty(tag)
                && _offsets.TryGetValue(deviceName, out var perTag)
                && perTag.TryGetValue(tag, out var value))
            {
                return value ?? new MonitorOffsetValue();
            }

            return new MonitorOffsetValue();
        }

        private static void EnsureFileExists()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? string.Empty);
                File.WriteAllText(FilePath, Template);
            }
            catch (Exception ex)
            {
                ShellLogger.Warning($"MonitorAdjustments: Unable to create {FilePath}: {ex.Message}");
            }
        }

        private static Dictionary<string, Dictionary<string, MonitorOffsetValue>> Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return new Dictionary<string, Dictionary<string, MonitorOffsetValue>>();
                }

                string json = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new Dictionary<string, Dictionary<string, MonitorOffsetValue>>();
                }

                var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, MonitorOffsetValue>>>(json, ReadOptions);
                return loaded ?? new Dictionary<string, Dictionary<string, MonitorOffsetValue>>();
            }
            catch (Exception ex)
            {
                ShellLogger.Error($"MonitorAdjustments: Error reading {FilePath}, ignoring it until it's fixed: {ex.Message}");
                return new Dictionary<string, Dictionary<string, MonitorOffsetValue>>();
            }
        }

        private static FileSystemWatcher CreateWatcher()
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                Directory.CreateDirectory(dir ?? string.Empty);

                var watcher = new FileSystemWatcher(dir, Path.GetFileName(FilePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };

                watcher.Changed += OnFileChanged;
                watcher.Created += OnFileChanged;
                watcher.Renamed += OnFileChanged;

                return watcher;
            }
            catch (Exception ex)
            {
                ShellLogger.Warning($"MonitorAdjustments: Unable to watch {FilePath} for changes: {ex.Message}");
                return null;
            }
        }

        private static void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            // Whatever just saved the file may still be mid-write; give it a moment so a
            // half-written file isn't read as empty or invalid.
            System.Threading.Thread.Sleep(150);
            _offsets = Load();
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
