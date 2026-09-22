using ManagedShell.Common.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    ///     "RowCount": 2,
    ///     "TaskIcon": { "Y": 1 },
    ///     "MediaGlyphPrevious": { "Scale": 1.1, "TextRendering": "ClearType" }
    ///   }
    /// }
    ///
    /// The outer key is a monitor's AppBarScreen.DeviceName (Windows' internal display name,
    /// typically \.\DISPLAY1, \.\DISPLAY2, etc. - JSON-escaped that's "\\\\.\\DISPLAY1" since
    /// each backslash needs doubling; if you're not sure which is which, just try one and see
    /// whether the adjustment shows up on the monitor you expected).
    ///
    /// "RowCount" is optional and overrides Settings.RowCount (the global row-count setting) on
    /// just this one monitor - useful since that setting is otherwise shared across every
    /// monitor, but how many task buttons fit before they start getting silently dropped (see
    /// TaskList.SetTaskButtonWidth) depends on that monitor's own available width in DIPs, which
    /// a higher-DPI monitor has less of even at the same physical resolution.
    ///
    /// Every other key is whatever string an element's utilities:MonitorOffset.Tag="..."
    /// attribute uses in the app's XAML (TaskIcon, TaskIconActive, TaskLabel, TaskLabelActive,
    /// TaskOverlayIcon, TaskOverlayIconActive, MediaButtonPrevious, MediaButtonPlayPause,
    /// MediaButtonNext (the whole button, not just its glyph), MediaGlyphPrevious,
    /// MediaGlyphPlayPause, MediaGlyphNext (the seek popup's own back/play-pause/forward glyphs
    /// share these same three tags, not separate ones, so any per-monitor Geometry override
    /// applies identically to both the main taskbar row and the popup), MediaAlbumArt,
    /// MediaTrackText, MediaSeekButtonBack, MediaSeekButtonPlayPause, MediaSeekButtonForward,
    /// MediaSeekSlider, MediaSeekTimeText (the seek popup's own buttons/slider/time text, separate
    /// from the main taskbar's),
    /// TrayToggleButton, TrayToggleButtonTop, TrayToggleButtonLeft, TrayToggleButtonBottom,
    /// TrayToggleButtonRight (its four independent 1px bevel-line Rectangles, tunable separately
    /// to close a fractional-DPI gap at any one edge without affecting the others),
    /// TrayToggleButtonArrow (the StackPanel wrapping the "^" glyph, not the glyph itself - the
    /// glyph has its own IsChecked-triggered RotateTransform, and a MonitorOffset X/Y/Scale on
    /// the same element would permanently override it, since a code-set RenderTransform is a
    /// local value and always beats a ControlTemplate trigger's Setter in WPF's precedence
    /// order), Clock,
    /// WeatherIcon, WeatherTemp, StartIcon, StartLabel, TrayIcon,
    /// TrayBox (the whole tray GroupBox as one rigid unit - media player, tray icons and clock
    /// move together), TrayBoxBevel (the inner Border that actually draws TrayBox's own visible
    /// bevel line - grow its Padding to grow the visible box itself without reflowing neighbors
    /// the way growing TrayBox's own Width/Margin would, and without shifting its content either:
    /// the content's own inset from the bevel's edge grows by the same amount the bevel's own
    /// edge moves outward by, so the two cancel out and the content stays exactly where it was),
    /// TrayBoxBevelPlaying (the same Border, but used instead of TrayBoxBevel while media is
    /// actively playing - see System.xaml's TrayBoxBevel tag Binding and Taskbar.IsMediaPlaying),
    /// or a new one you tag yourself), and every field on
    /// MonitorOffsetValue is optional - X, Y, Width, Height, Scale, TextRendering, BitmapScaling,
    /// Bold, Margin, Padding, Background, Geometry (Path mini-language Figures, only meaningful
    /// on a Path - e.g. one of the MediaGlyph* tags - to replace its vector shape entirely per
    /// monitor) - omit whatever you don't want to change.
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
            "  //   \"RowCount\": 2,\n" +
            "  //   \"TaskIcon\": { \"Y\": 1 },\n" +
            "  //   \"MediaGlyphPrevious\": { \"Scale\": 1.1, \"TextRendering\": \"ClearType\" }\n" +
            "  // }\n" +
            "}\n";

        /// <summary>One monitor's entry: an optional RowCount, plus every other (tag-keyed) field
        /// captured by JsonExtensionData rather than a fixed set of named properties.</summary>
        private class MonitorEntry
        {
            public int? RowCount { get; set; }

            [JsonExtensionData]
            public Dictionary<string, JsonElement> Tags { get; set; }
        }

        private static Dictionary<string, MonitorEntry> _monitors;
        private static FileSystemWatcher _watcher;

        static MonitorAdjustments()
        {
            EnsureFileExists();
            _monitors = Load();
            _watcher = CreateWatcher();
        }

        public static MonitorOffsetValue Get(string deviceName, string tag)
        {
            if (!string.IsNullOrEmpty(deviceName) && !string.IsNullOrEmpty(tag)
                && _monitors.TryGetValue(deviceName, out MonitorEntry entry)
                && entry.Tags != null && entry.Tags.TryGetValue(tag, out JsonElement element))
            {
                try
                {
                    return element.Deserialize<MonitorOffsetValue>() ?? new MonitorOffsetValue();
                }
                catch (JsonException ex)
                {
                    ShellLogger.Error($"MonitorAdjustments: Error reading '{tag}' for '{deviceName}', ignoring it until it's fixed: {ex.Message}");
                }
            }

            return new MonitorOffsetValue();
        }

        /// <summary>The RowCount override for this monitor, or null if it has none (meaning: use
        /// the global Settings.RowCount instead, same as before this override existed).</summary>
        public static int? GetRowCount(string deviceName)
        {
            if (!string.IsNullOrEmpty(deviceName) && _monitors.TryGetValue(deviceName, out MonitorEntry entry))
            {
                return entry.RowCount;
            }

            return null;
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

        private static Dictionary<string, MonitorEntry> Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return new Dictionary<string, MonitorEntry>();
                }

                string json = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new Dictionary<string, MonitorEntry>();
                }

                var loaded = JsonSerializer.Deserialize<Dictionary<string, MonitorEntry>>(json, ReadOptions);
                return loaded ?? new Dictionary<string, MonitorEntry>();
            }
            catch (Exception ex)
            {
                ShellLogger.Error($"MonitorAdjustments: Error reading {FilePath}, ignoring it until it's fixed: {ex.Message}");
                return new Dictionary<string, MonitorEntry>();
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
            _monitors = Load();
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
