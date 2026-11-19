using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Obsidian.Services;

public enum AppTheme
{
    System = 0,
    Dark = 1,
    Light = 2,
}

public enum AccentPreset
{
    Violet = 0,
    Cyan = 1,
    Green = 2,
    Amber = 3,
    Red = 4,
}

/// <summary>
/// Runtime theme engine. The palettes live in
/// <c>Themes/Palette.Dark.xaml</c> / <c>Themes/Palette.Light.xaml</c> and are
/// merged as dictionary index 0 of the application resources; the style sheet
/// (<c>Themes/Theme.xaml</c>) only uses <c>{DynamicResource}</c> so swapping the
/// palette recolours the whole shell without a restart.
///
/// See docs/THEME-CONTRACT.md — the token names and the public surface below
/// are frozen.
/// </summary>
public static class ThemeService
{
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string LightThemeValue = "AppsUseLightTheme";

    private static bool _initialised;
    private static bool _systemEventsHooked;

    public static AppTheme Mode { get; private set; } = AppTheme.System;
    public static bool IsDark { get; private set; } = true;
    public static AccentPreset Accent { get; private set; } = AccentPreset.Red;

    /// <summary>
    /// Fired after the palette swap, on the UI thread. Cached brushes (syntax
    /// highlighter, toast accents) listen to this event.
    /// </summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>
    /// Called from <c>App.OnStartup</c> before the main window is constructed, so
    /// the first frame already uses the persisted palette.
    /// </summary>
    public static void Initialise(AppTheme mode, AccentPreset accent)
    {
        HookSystemEvents();
        _initialised = true;
        Apply(mode, accent);
    }

    /// <summary>Swaps the palette dictionary and applies the accent overrides.</summary>
    public static void Apply(AppTheme mode, AccentPreset accent)
    {
        void ApplyCore()
        {
            Mode = mode;
            Accent = accent;
            IsDark = mode switch
            {
                AppTheme.Dark => true,
                AppTheme.Light => false,
                _ => IsSystemDark(),
            };

            ResourceDictionary palette = BuildPalette(IsDark, accent);
            Application? application = Application.Current;
            if (application != null)
            {
                var merged = application.Resources.MergedDictionaries;
                int index = FindPaletteIndex(merged);
                if (index < 0)
                {
                    if (merged.Count == 0)
                        merged.Add(palette);
                    else
                        merged.Insert(0, palette);
                }
                else
                {
                    merged[index] = palette;
                }
            }

            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            ApplyCore();
        else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            dispatcher.Invoke(ApplyCore, DispatcherPriority.Normal);
    }

    /// <summary>
    /// True when the Windows personalisation is set to the light app theme.
    /// Missing/denied registry access intentionally resolves to dark.
    /// </summary>
    public static bool IsSystemDark()
    {
        try
        {
            object? value = Registry.GetValue(PersonalizeKey, LightThemeValue, 0);
            return value switch
            {
                int number => number == 0,
                long number => number == 0,
                string text when int.TryParse(text, out int parsed) => parsed == 0,
                _ => true,
            };
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Resolves a palette brush for code-behind/converters. Returns <c>null</c>
    /// when the resource is missing (e.g. during shutdown) so callers can fall
    /// back safely. Must be called from the UI thread.
    /// </summary>
    public static Brush? GetBrush(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        try
        {
            return Application.Current?.TryFindResource(token) as Brush;
        }
        catch
        {
            return null;
        }
    }

    public static Color GetColor(string token)
    {
        Brush? brush = GetBrush(token);
        return brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;
    }

    /// <summary>
    /// Swatch colour for an accent preset in the currently effective mode; used
    /// by the settings page so the preset chips are legible in both themes.
    /// </summary>
    public static Brush? GetAccentPresetBrush(AccentPreset preset)
    {
        try
        {
            return Brush(AccentColorsFor(preset, IsDark).Accent);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Unhooks the OS listeners; called from <c>App.OnExit</c>.</summary>
    public static void Shutdown()
    {
        if (_systemEventsHooked)
        {
            try
            {
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            }
            catch
            {
                // Best effort during shutdown.
            }

            _systemEventsHooked = false;
        }

        _initialised = false;
    }

    private static void HookSystemEvents()
    {
        if (_systemEventsHooked)
            return;

        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _systemEventsHooked = true;
        }
        catch
        {
            // SystemEvents is unavailable in some hosted scenarios; the System
            // mode then keeps the value resolved at startup.
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!_initialised || Mode != AppTheme.System)
            return;

        if (e.Category is not (UserPreferenceCategory.General or
            UserPreferenceCategory.Color or
            UserPreferenceCategory.VisualStyle or
            UserPreferenceCategory.Window))
        {
            return;
        }

        // The OS event runs on a dedicated thread; Apply marshals to the UI
        // dispatcher itself.
        if (IsSystemDark() != IsDark)
            Apply(AppTheme.System, Accent);
    }

    private static int FindPaletteIndex(IList<ResourceDictionary> merged)
    {
        for (int i = 0; i < merged.Count; i++)
        {
            Uri? source = merged[i].Source;
            if (source is null)
                continue;

            string text = source.OriginalString;
            if (text.Contains("Palette.Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Palette.Light.xaml", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static ResourceDictionary BuildPalette(bool dark, AccentPreset accent)
    {
        string assembly = typeof(ThemeService).Assembly.GetName().Name ?? "OBSIDIAN";
        var palette = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/{assembly};component/Themes/Palette.{(dark ? "Dark" : "Light")}.xaml",
                UriKind.Absolute),
        };

        AccentColors colors = AccentColorsFor(accent, dark);

        palette["Accent"] = Brush(colors.Accent);
        palette["AccentHover"] = Brush(colors.Hover);
        palette["AccentPressed"] = Brush(colors.Pressed);
        palette["AccentSoft"] = Brush(colors.Soft);
        palette["AccentTint"] = Brush(colors.Tint);
        palette["AccentBorder"] = Brush(colors.Border);
        palette["AccentBorderStrong"] = Brush(colors.BorderStrong);
        palette["AccentText"] = Brush(colors.Text);
        // White is not readable on every preset (amber/green are bright); pick
        // the text tone with the higher WCAG contrast ratio instead.
        palette["TextOnAccent"] = Brush(OnAccent(colors.Accent));
        // Everything that visually belongs to the accent follows the preset:
        // focus rings, input focus, check marks, text selection and checked chips.
        palette["FocusRing"] = Brush(colors.Accent);
        palette["InputBorderFocus"] = Brush(colors.Accent);
        palette["CheckBorderChecked"] = Brush(colors.Accent);
        palette["ChipTextChecked"] = Brush(colors.Text);
        palette["Selection"] = Brush(colors.Tint);
        palette["CodeSelection"] = Brush(colors.Tint);
        palette["AccentGradient"] = Gradient(colors.Accent, colors.Hover, horizontal: false);
        palette["AccentGradientH"] = Gradient(colors.Accent, colors.Hover, horizontal: true);
        palette["AccentGlow"] = new DropShadowEffect
        {
            Color = ParseColor(colors.Glow),
            BlurRadius = 0,
            ShadowDepth = 0,
            Opacity = 0,
        };

        return palette;
    }

    private static Brush Brush(string hex)
    {
        var brush = new SolidColorBrush(ParseColor(hex));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Gradient(string from, string to, bool horizontal)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = horizontal ? new Point(1, 0) : new Point(0, 1),
        };
        brush.GradientStops.Add(new GradientStop(ParseColor(from), 0));
        brush.GradientStops.Add(new GradientStop(ParseColor(to), 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Picks white or graphite for text drawn on the accent fill, based on the
    /// WCAG relative luminance of the accent (DESIGN_SYSTEM.md §19).
    /// </summary>
    private static string OnAccent(string accentHex)
    {
        Color color = ParseColor(accentHex);
        double luminance =
            0.2126 * Linear(color.R / 255.0) +
            0.7152 * Linear(color.G / 255.0) +
            0.0722 * Linear(color.B / 255.0);
        double whiteContrast = 1.05 / (luminance + 0.05);
        double darkContrast = (luminance + 0.05) / 0.0578; // #12161B
        return whiteContrast >= darkContrast ? "#FFFFFF" : "#12161B";
    }

    private static double Linear(double channel)
        => channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

    private static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Colors.Transparent;
        }
    }

    private static AccentColors AccentColorsFor(AccentPreset accent, bool dark) => (accent, dark) switch
    {
        // Steel — cool neutral, low saturation (DESIGN_SYSTEM.md §1.3).
        (AccentPreset.Cyan, true) => new("#47758C", "#53849C", "#3A6478", "#8FBBD3", "#12222A", "#234450", "#356B80", "#9CC7D8", "#47758C"),
        (AccentPreset.Cyan, false) => new("#2A6A83", "#357892", "#225A70", "#3A88A2", "#E8F3F7", "#CFE4EC", "#9DC7D6", "#2A6A83", "#2A6A83"),
        (AccentPreset.Green, true) => new("#2F7D5C", "#388F6B", "#276B4E", "#7FD0AB", "#10241C", "#1D4234", "#2E6650", "#8FD6B6", "#2F7D5C"),
        (AccentPreset.Green, false) => new("#1B7A55", "#228A61", "#166847", "#2A9A6C", "#E7F5EE", "#CDE7DA", "#9BCFB7", "#1E7A55", "#1B7A55"),
        (AccentPreset.Amber, true) => new("#B5822A", "#C4912F", "#96691F", "#E0B86A", "#241C0E", "#4A3A18", "#6E5726", "#E8C07A", "#B5822A"),
        (AccentPreset.Amber, false) => new("#96680F", "#A5761A", "#7C550B", "#B5822A", "#FBF2E1", "#F0E0BE", "#D9BE84", "#93610F", "#96680F"),
        (AccentPreset.Violet, true) => new("#7E5FBC", "#8D6FCB", "#6B4FA3", "#B4A0E0", "#1C1626", "#3B2C52", "#57447A", "#C4B2EA", "#7E5FBC"),
        (AccentPreset.Violet, false) => new("#6E4FB0", "#7D5EC0", "#5D4199", "#8A6BC8", "#F1ECF9", "#DDD2EE", "#B9A6D9", "#5B3FC4", "#6E4FB0"),
        // Ember is the OBSIDIAN identity and the default preset.
        (_, true) => new("#D0453C", "#DE5A50", "#B33A32", "#F08A82", "#241414", "#4A2626", "#7A3A36", "#F08A82", "#D0453C"),
        (_, false) => new("#B5372E", "#C2443B", "#932C25", "#C24A40", "#FBEAE8", "#E9C4BF", "#C98B84", "#A5342C", "#B5372E"),
    };

    private sealed record AccentColors(
        string Accent,
        string Hover,
        string Pressed,
        string Soft,
        string Tint,
        string Border,
        string BorderStrong,
        string Text,
        string Glow);
}
