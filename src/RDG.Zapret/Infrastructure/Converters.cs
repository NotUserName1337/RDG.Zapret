using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RDG.Zapret.Models;

namespace RDG.Zapret.Infrastructure;

internal static class UiBrushes
{
    public static readonly SolidColorBrush Success = Frozen("#34D399");
    public static readonly SolidColorBrush Warning = Frozen("#FBBF24");
    public static readonly SolidColorBrush Danger = Frozen("#FB7185");
    public static readonly SolidColorBrush Info = Frozen("#60A5FA");
    public static readonly SolidColorBrush Muted = Frozen("#5F7085");
    public static readonly SolidColorBrush Accent = Frozen("#7C5CFF");

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is not null;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StringEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is string s && !string.IsNullOrWhiteSpace(s);
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase))
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value is int i ? i : 0;
        var visible = count > 0;
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase))
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                return brush;
            }
            catch
            {
                // ignored
            }
        }

        return UiBrushes.Muted;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ProbeStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ProbeStatus status
            ? status switch
            {
                ProbeStatus.Available => UiBrushes.Success,
                ProbeStatus.Partial => UiBrushes.Warning,
                ProbeStatus.Checking => UiBrushes.Accent,
                ProbeStatus.Blocked or ProbeStatus.Timeout or ProbeStatus.Dns or ProbeStatus.Tls or ProbeStatus.Error => UiBrushes.Danger,
                _ => UiBrushes.Muted
            }
            : UiBrushes.Muted;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ProbeStatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ProbeStatus status
            ? status switch
            {
                ProbeStatus.Available => "Доступен",
                ProbeStatus.Partial => "Ограничен",
                ProbeStatus.Checking => "Проверка…",
                ProbeStatus.Blocked => "Заблокирован",
                ProbeStatus.Timeout => "Тайм-аут",
                ProbeStatus.Dns => "DNS-ошибка",
                ProbeStatus.Tls => "TLS-блокировка",
                ProbeStatus.Error => "Ошибка",
                _ => "Не проверено"
            }
            : "Не проверено";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ProbeStatusToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ProbeStatus status
            ? status switch
            {
                ProbeStatus.Available => "\uE73E",
                ProbeStatus.Partial => "\uE7BA",
                ProbeStatus.Checking => "\uE895",
                ProbeStatus.Blocked => "\uE711",
                ProbeStatus.Timeout => "\uE823",
                ProbeStatus.Dns => "\uE774",
                ProbeStatus.Tls => "\uE72E",
                ProbeStatus.Error => "\uE783",
                _ => "\uE946"
            }
            : "\uE946";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ScoreToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var score = value is double d ? d : value is int i ? i : 0;
        return score >= 80 ? UiBrushes.Success : score >= 55 ? UiBrushes.Warning : score > 0 ? UiBrushes.Danger : UiBrushes.Muted;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LogLevel level
            ? level switch
            {
                LogLevel.Debug => UiBrushes.Muted,
                LogLevel.Success => UiBrushes.Success,
                LogLevel.Warn => UiBrushes.Warning,
                LogLevel.Error => UiBrushes.Danger,
                _ => UiBrushes.Info
            }
            : UiBrushes.Info;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DiagnosticStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DiagnosticStatus status
            ? status switch
            {
                DiagnosticStatus.Ok => UiBrushes.Success,
                DiagnosticStatus.Warn => UiBrushes.Warning,
                DiagnosticStatus.Fail => UiBrushes.Danger,
                _ => UiBrushes.Info
            }
            : UiBrushes.Info;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DiagnosticStatusToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DiagnosticStatus status
            ? status switch
            {
                DiagnosticStatus.Ok => "\uE73E",
                DiagnosticStatus.Warn => "\uE7BA",
                DiagnosticStatus.Fail => "\uE711",
                _ => "\uE946"
            }
            : "\uE946";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DoctorStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DoctorStepStatus status
            ? status switch
            {
                DoctorStepStatus.Ok => UiBrushes.Success,
                DoctorStepStatus.Fixed => UiBrushes.Success,
                DoctorStepStatus.Warn => UiBrushes.Warning,
                DoctorStepStatus.Error => UiBrushes.Danger,
                DoctorStepStatus.Skipped => UiBrushes.Muted,
                _ => UiBrushes.Info
            }
            : UiBrushes.Info;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DoctorStatusToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DoctorStepStatus status
            ? status switch
            {
                DoctorStepStatus.Ok => "\uE73E",
                DoctorStepStatus.Fixed => "\uE930",
                DoctorStepStatus.Warn => "\uE7BA",
                DoctorStepStatus.Error => "\uE711",
                DoctorStepStatus.Skipped => "\uE946",
                _ => "\uE946"
            }
            : "\uE946";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BlockReasonToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is BlockReason reason
            ? reason switch
            {
                BlockReason.Open => UiBrushes.Success,
                BlockReason.NoNetwork or BlockReason.ServerDown => UiBrushes.Danger,
                BlockReason.Unknown => UiBrushes.Warning,
                _ => UiBrushes.Warning
            }
            : UiBrushes.Muted;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
