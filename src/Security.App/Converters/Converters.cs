using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Security.Core.Enums;
using Security.Core.Models;

namespace Security.App.Converters;

/// <summary>
/// Maps a tone token ("ok" / "warn" / "bad" / "idle") to a status colour.
/// Kept as data rather than a Brush so view models stay UI-framework free.
/// </summary>
public sealed class ToneToBrushConverter : IValueConverter
{
    private static readonly Brush Ok = Make("#34D399");
    private static readonly Brush Warn = Make("#FBBF24");
    private static readonly Brush Bad = Make("#F87171");
    private static readonly Brush Idle = Make("#5E6B7D");
    private static readonly Brush Accent = Make("#22D3EE");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "ok" => Ok,
        "warn" => Warn,
        "bad" => Bad,
        "accent" => Accent,
        _ => Idle,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Make(string hex) => BoolToBrushConverter.Solid(hex);
}

/// <summary>Inverts a boolean (used for "not enrolling" style states).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value ?? false;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value ?? false;
}

/// <summary>Shows an element only when the bound value is null/empty/whitespace.</summary>
public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasText = value is string s && !string.IsNullOrWhiteSpace(s);
        var visible = Invert ? !hasText : hasText;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Localises a UTC timestamp for display.</summary>
public sealed class UtcToLocalConverter : IValueConverter
{
    public string Format { get; set; } = "yyyy-MM-dd HH:mm:ss";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime dt)
            return string.Empty;

        var format = parameter as string ?? Format;
        if (string.IsNullOrEmpty(format))
            format = "yyyy-MM-dd HH:mm:ss";

        return dt.ToLocalTime().ToString(format);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Formats a nullable confidence value for the events table.</summary>
public sealed class ConfidenceConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? d.ToString("0.000") : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Colours an event's outcome for scannable logs.</summary>
public sealed class EventResultToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Security.Core.Enums.SecurityEventResult result)
            return Brush("8B98A9");

        return result switch
        {
            Security.Core.Enums.SecurityEventResult.Success => Brush("34D399"),
            Security.Core.Enums.SecurityEventResult.Known => Brush("34D399"),
            Security.Core.Enums.SecurityEventResult.Failure => Brush("F87171"),
            Security.Core.Enums.SecurityEventResult.Denied => Brush("F87171"),
            Security.Core.Enums.SecurityEventResult.Unknown => Brush("F87171"),
            Security.Core.Enums.SecurityEventResult.Warning => Brush("FBBF24"),
            _ => Brush("8B98A9"),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Brush(string hex) => BoolToBrushConverter.Solid(hex);
}

/// <summary>Renders a boolean as one of two short labels (e.g. ENROLLED / NOT ENROLLED).</summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public string TrueText { get; set; } = "Yes";

    public string FalseText { get; set; } = "No";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is string overrideText)
            return overrideText;

        return value is true ? TrueText : FalseText;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Visibility for a boolean, optionally inverted via ConverterParameter.</summary>
public sealed class InvertBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        return flag ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Green when true, muted grey when false — used for "camera running".</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public string TrueHex { get; set; } = "#34D399";

    public string FalseHex { get; set; } = "#5E6B7D";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Solid(value is true ? TrueHex : FalseHex);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>
    /// Build a frozen brush from a hex colour, tolerating a missing '#' prefix
    /// and never throwing.
    ///
    /// This used to call <see cref="ColorConverter.ConvertFromString(string)"/>
    /// directly with unprefixed values such as "8B98A9". WPF rejects those with
    /// FormatException("Token is not valid."), and because a value converter runs
    /// on the dispatcher during layout, that exception was unhandled and crashed
    /// the whole application the first time an event row was rendered. A colour
    /// is decoration — it must never be able to take the app down.
    /// </summary>
    internal static SolidColorBrush Solid(string? hex)
    {
        try
        {
            var text = string.IsNullOrWhiteSpace(hex) ? null : hex.Trim();
            if (text is not null && !text.StartsWith('#'))
                text = "#" + text;

            if (text is not null && ColorConverter.ConvertFromString(text) is Color color)
            {
                var parsed = new SolidColorBrush(color);
                parsed.Freeze();
                return parsed;
            }
        }
        catch (Exception)
        {
            // Fall through to the neutral colour below.
        }

        var fallback = new SolidColorBrush(Muted);
        fallback.Freeze();
        return fallback;
    }

    private static readonly Color Muted = Color.FromRgb(0x5E, 0x6B, 0x7D);
}

/// <summary>Colours the verdict headline by recognition status.</summary>
public sealed class VerdictToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FaceRecognitionResult result)
            return BoolToBrushConverter.Solid("#5E6B7D");

        return result.Status switch
        {
            RecognitionStatus.Known => BoolToBrushConverter.Solid("#34D399"),
            RecognitionStatus.Unknown => BoolToBrushConverter.Solid("#F87171"),
            _ => BoolToBrushConverter.Solid("#FBBF24"),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Visible only when the bound integer is zero (empty-list placeholder).</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Renders an event's nullable session state for the SESSION column:
/// the short caption (LOCKED / UNLOCKED / …) when recorded, an em dash for
/// events written before Phase 3. Null is never guessed as a real state.
/// </summary>
public sealed class SessionStateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SessionState state ? state.ToDisplayText() : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
