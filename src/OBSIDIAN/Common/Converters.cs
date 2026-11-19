using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Obsidian.Dumper.Core.Diagnostics;
using Obsidian.Dumper.Core.Inspection;
using Obsidian.Services;

namespace Obsidian.Converters;

/// <summary>
/// Shared converter helpers. Converters run inside WPF's binding engine, where an
/// exception is swallowed and logged as a binding error, leaving the UI in a
/// half-rendered state. Every converter therefore has a deterministic fallback and
/// never throws for a null or unexpected value.
/// </summary>
internal static class ConverterHelpers
{
    /// <summary>Both spellings are accepted: older XAML used "inverse", newer uses "invert".</summary>
    public static bool IsInverted(object? parameter)
    {
        if (parameter is not string text)
            return false;
        return text.Equals("invert", StringComparison.OrdinalIgnoreCase)
            || text.Equals("inverse", StringComparison.OrdinalIgnoreCase);
    }

    public static double ToDouble(object? value) => value switch
    {
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        decimal m => (double)m,
        _ => 0d,
    };

    public static Brush FromRgb(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    public static Brush FromRgba(byte red, byte green, byte blue, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Resolves palette brushes for the converters and caches them per theme. The
/// palette is swapped at runtime by <see cref="ThemeService"/>; the cache is
/// invalidated on <see cref="ThemeService.ThemeChanged"/> so a live theme switch
/// recolours log rows, severity hints and status dots without a page reload.
/// </summary>
internal static class ThemeBrushes
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.Ordinal);
    private static bool _subscribed;

    public static Brush Get(string token, Brush fallback)
    {
        EnsureSubscribed();
        lock (Gate)
        {
            if (Cache.TryGetValue(token, out Brush? cached))
                return cached;
        }

        Brush resolved;
        try
        {
            resolved = ThemeService.GetBrush(token) ?? fallback;
        }
        catch
        {
            resolved = fallback;
        }

        lock (Gate)
            Cache[token] = resolved;
        return resolved;
    }

    private static void EnsureSubscribed()
    {
        if (_subscribed)
            return;
        _subscribed = true;
        try
        {
            ThemeService.ThemeChanged += (_, _) =>
            {
                lock (Gate)
                    Cache.Clear();
            };
        }
        catch
        {
            // Design-time / unit-test hosts may not have a theme service.
        }
    }
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is true;
        if (ConverterHelpers.IsInverted(parameter))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value != null;
        if (ConverterHelpers.IsInverted(parameter))
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// "Empty state" converter: returns <see cref="Visibility.Visible"/> when the bound
/// count is zero (so the message telling the user the list is empty shows up), and
/// Collapsed otherwise. Pass "invert" in <c>ConverterParameter</c> for the opposite
/// (e.g. to hide an empty badge when the collection is empty).
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int count;
        try
        {
            count = value switch
            {
                int i => i,
                long l => (int)Math.Clamp(l, 0, int.MaxValue),
                ICollection collection => collection.Count,
                _ => 0,
            };
        }
        catch
        {
            // A collection whose Count getter throws is treated as empty.
            count = 0;
        }

        bool visible = count == 0;
        if (ConverterHelpers.IsInverted(parameter))
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

public sealed class StringEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool empty = string.IsNullOrWhiteSpace(value as string);
        if (ConverterHelpers.IsInverted(parameter))
            empty = !empty;
        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class LogLevelBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => For(value as LogLevel?);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static readonly Brush FallbackInfo = ConverterHelpers.FromRgb(0x8F, 0xA0, 0xBF);
    private static readonly Brush FallbackTrace = ConverterHelpers.FromRgb(0x5E, 0x6A, 0x80);
    private static readonly Brush FallbackSuccess = ConverterHelpers.FromRgb(0x35, 0xD3, 0x99);
    private static readonly Brush FallbackWarning = ConverterHelpers.FromRgb(0xF5, 0xB8, 0x4B);
    private static readonly Brush FallbackError = ConverterHelpers.FromRgb(0xFB, 0x71, 0x85);

    public static Brush For(LogLevel? level) => level switch
    {
        LogLevel.Success => ThemeBrushes.Get("LogSuccess", FallbackSuccess),
        LogLevel.Warning => ThemeBrushes.Get("LogWarning", FallbackWarning),
        LogLevel.Error => ThemeBrushes.Get("LogError", FallbackError),
        LogLevel.Trace => ThemeBrushes.Get("LogTrace", FallbackTrace),
        _ => ThemeBrushes.Get("LogInfo", FallbackInfo),
    };
}

public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => For(value as ProtectionSeverity?);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static readonly Brush FallbackCritical = ConverterHelpers.FromRgb(0xFB, 0x71, 0x85);
    private static readonly Brush FallbackSuspicious = ConverterHelpers.FromRgb(0xF5, 0xB8, 0x4B);
    private static readonly Brush FallbackInfo = ConverterHelpers.FromRgb(0x22, 0xD3, 0xEE);
    private static readonly Brush FallbackUnknown = ConverterHelpers.FromRgb(0x8F, 0xA0, 0xBF);

    public static Brush For(ProtectionSeverity? severity) => severity switch
    {
        ProtectionSeverity.Critical => ThemeBrushes.Get("SeverityCritical", FallbackCritical),
        ProtectionSeverity.Suspicious => ThemeBrushes.Get("SeveritySuspicious", FallbackSuspicious),
        ProtectionSeverity.Info => ThemeBrushes.Get("SeverityInfo", FallbackInfo),
        _ => FallbackUnknown,
    };
}

public sealed class BoolToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? ThemeBrushes.Get("StatusOnline", FallbackOnline)
            : ThemeBrushes.Get("StatusOffline", FallbackOffline);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static readonly Brush FallbackOnline = ConverterHelpers.FromRgb(0x35, 0xD3, 0x99);
    private static readonly Brush FallbackOffline = ConverterHelpers.FromRgb(0x5E, 0x6A, 0x80);
}

/// <summary>Scales a value by a fixed factor (kept for compatibility with older XAML).</summary>
public sealed class PercentageWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2)
            return 0d;

        double value = ConverterHelpers.ToDouble(values[0]);
        double total = ConverterHelpers.ToDouble(values[1]);
        if (double.IsNaN(value) || double.IsNaN(total) || total <= 0)
            return 0d;

        double scale = 1d;
        if (parameter is string factor &&
            double.TryParse(factor, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            scale = Math.Clamp(parsed, 0d, 1d);
        }

        return Math.Clamp(value / total, 0d, 1d) * scale;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a 0..100 percentage onto a measured width (entropy bars).</summary>
public sealed class PercentWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2)
            return 0d;

        double total = ConverterHelpers.ToDouble(values[1]);
        if (double.IsNaN(total) || total <= 0)
            return 0d;

        double percent = ConverterHelpers.ToDouble(values[0]);
        if (double.IsNaN(percent) || percent <= 0)
            return 0d;

        return Math.Clamp(percent / 100d, 0d, 1d) * total;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Formats a byte count for compact table cells (invariant, never throws).</summary>
public sealed class BytesConverter : IValueConverter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        long bytes = value switch
        {
            long l => l,
            int i => i,
            uint u => u,
            ulong ul => ul > long.MaxValue ? long.MaxValue : (long)ul,
            _ => -1,
        };

        if (bytes < 0)
            return "—";

        return Format(bytes);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    internal static string Format(long bytes)
    {
        if (bytes < 0)
            return "—";

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : value.ToString("0.##", CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}
