using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PCleaner.Core;
using PCleaner.Core.Engine;
using PCleaner.Core.Logging;
using PCleaner.Core.Model;
using PCleaner.Core.Privacy;

namespace PCleaner.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible ? !Invert : Invert;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        var visible = Invert ? isNull : !isNull;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int i => i,
            long l => (int)Math.Min(int.MaxValue, l),
            System.Collections.ICollection c => c.Count,
            _ => 0,
        };
        var visible = Invert ? count == 0 : count > 0;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BytesToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is long bytes ? Scanner.FormatBytes(bytes) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Count + noun with a proper English plural: ConverterParameter is the singular ("entry" → "1 entry" / "6 entries").</summary>
public sealed class CountLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int i => i,
            long l => l,
            System.Collections.ICollection c => c.Count,
            _ => 0L,
        };
        return TextFormat.Count(count, parameter as string ?? "item");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class RiskToBrushConverter : IValueConverter
{
    public Brush Safe { get; set; } = Brushes.LimeGreen;

    public Brush Moderate { get; set; } = Brushes.Orange;

    public Brush Privacy { get; set; } = Brushes.MediumPurple;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RiskLevel.Safe => Safe,
        RiskLevel.Moderate => Moderate,
        RiskLevel.Privacy => Privacy,
        _ => Safe,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps an <see cref="InsightLevel"/> to a brush, glyph or label (one converter, four resources).</summary>
public sealed class InsightLevelConverter : IValueConverter
{
    /// <summary>What to return: "brush", "soft", "border", "glyph" or "label".</summary>
    public string Output { get; set; } = "brush";

    public Brush AccountBrush { get; set; } = Brushes.Orange;

    public Brush LocalBrush { get; set; } = Brushes.DeepSkyBlue;

    public Brush SystemBrush { get; set; } = Brushes.MediumPurple;

    public Brush ClearBrush { get; set; } = Brushes.LimeGreen;

    public Brush AccountSoft { get; set; } = Brushes.Transparent;

    public Brush LocalSoft { get; set; } = Brushes.Transparent;

    public Brush SystemSoft { get; set; } = Brushes.Transparent;

    public Brush ClearSoft { get; set; } = Brushes.Transparent;

    public Brush AccountBorder { get; set; } = Brushes.Transparent;

    public Brush LocalBorder { get; set; } = Brushes.Transparent;

    public Brush SystemBorder { get; set; } = Brushes.Transparent;

    public Brush ClearBorder { get; set; } = Brushes.Transparent;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value is InsightLevel l ? l : InsightLevel.Clear;
        return Output switch
        {
            "glyph" => level switch
            {
                InsightLevel.Account => "\uE77B",
                InsightLevel.Local => "\uE928",
                InsightLevel.Device => "\uE81C",
                InsightLevel.System => "\uE7F8",
                _ => "\uE73E",
            },
            "label" => level switch
            {
                InsightLevel.Account => "Account",
                InsightLevel.Local => "This browser",
                InsightLevel.Device => "This PC",
                InsightLevel.System => "Windows",
                _ => "Clear",
            },
            "soft" => level switch
            {
                InsightLevel.Account => AccountSoft,
                InsightLevel.Local or InsightLevel.Device => LocalSoft,
                InsightLevel.System => SystemSoft,
                _ => ClearSoft,
            },
            "border" => level switch
            {
                InsightLevel.Account => AccountBorder,
                InsightLevel.Local or InsightLevel.Device => LocalBorder,
                InsightLevel.System => SystemBorder,
                _ => ClearBorder,
            },
            _ => level switch
            {
                InsightLevel.Account => AccountBrush,
                InsightLevel.Local or InsightLevel.Device => LocalBrush,
                InsightLevel.System => SystemBrush,
                _ => ClearBrush,
            },
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class LogLevelToBrushConverter : IValueConverter
{
    public Brush Info { get; set; } = Brushes.Gainsboro;

    public Brush Warning { get; set; } = Brushes.Orange;

    public Brush Error { get; set; } = Brushes.OrangeRed;

    public Brush Debug { get; set; } = Brushes.Gray;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogLevel.Warning => Warning,
        LogLevel.Error => Error,
        LogLevel.Debug => Debug,
        _ => Info,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EqualityToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is not null ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}

public sealed class ListJoinConverter : IValueConverter
{
    public string Separator { get; set; } = ", ";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is System.Collections.IEnumerable items and not string
            ? string.Join(Separator, items.Cast<object?>().Select(o => o?.ToString()).Where(s => !string.IsNullOrEmpty(s)))
            : value?.ToString() ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SkipReasonToBrushConverter : IValueConverter
{
    public Brush Ready { get; set; } = Brushes.LimeGreen;

    public Brush Blocked { get; set; } = Brushes.Orange;

    public Brush Muted { get; set; } = Brushes.Gray;

    public Brush Error { get; set; } = Brushes.OrangeRed;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RuleScanResult { Skip: SkipReason.None } => Ready,
        RuleScanResult { Skip: SkipReason.ApplicationRunning or SkipReason.RequiresAdministrator } => Blocked,
        RuleScanResult { Skip: SkipReason.Error } => Error,
        RuleScanResult => Muted,
        _ => Muted,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Multiplies a fraction (0..1) by the available width to size breakdown bars.</summary>
public sealed class FractionToWidthConverter : IMultiValueConverter
{
    public object Convert(object[]? values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not { Length: 2 } || values[0] is not double fraction || values[1] is not double width || double.IsNaN(width))
        {
            return 0d;
        }

        return Math.Max(0d, Math.Min(1d, fraction)) * width;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps a rule item's state to a Segoe Fluent icon glyph (bound to the whole view model).</summary>
public sealed class StatusGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PCleaner.App.ViewModels.RuleItemViewModel item)
        {
            return string.Empty;
        }

        if (item.CleanResult is { } clean)
        {
            return clean.IsSkipped ? "\uE7BA" : "\uE73E"; // warning / check
        }

        if (item.Scan is null)
        {
            return "\uE915"; // hollow circle (not analyzed)
        }

        return item.Scan.Skip switch
        {
            SkipReason.None when item.Scan.Items.Count == 0 && !item.Scan.IsEstimate => "\uE73E",
            SkipReason.None => "\uE930", // completed
            SkipReason.ApplicationRunning => "\uE7BA",
            SkipReason.RequiresAdministrator => "\uEA18", // shield
            SkipReason.LocationNotFound => "\uE738", // remove
            SkipReason.Error => "\uEA39",
            _ => "\uE915",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps a rule item's state to a status brush (bound to the whole view model).</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public Brush Ready { get; set; } = Brushes.LimeGreen;

    public Brush Blocked { get; set; } = Brushes.Orange;

    public Brush Muted { get; set; } = Brushes.Gray;

    public Brush Error { get; set; } = Brushes.OrangeRed;

    public Brush Cleaned { get; set; } = Brushes.Turquoise;

    public Brush Idle { get; set; } = Brushes.DimGray;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PCleaner.App.ViewModels.RuleItemViewModel item)
        {
            return Idle;
        }

        if (item.CleanResult is { } clean)
        {
            return clean.IsSkipped ? Blocked : Cleaned;
        }

        if (item.Scan is null)
        {
            return Idle;
        }

        return item.Scan.Skip switch
        {
            SkipReason.None => Ready,
            SkipReason.ApplicationRunning or SkipReason.RequiresAdministrator => Blocked,
            SkipReason.Error => Error,
            _ => Muted,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}