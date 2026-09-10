using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using PhoneLinkPC.Core.Diagnostics;

namespace PhoneLinkPC.App.Converters;

/// <summary>Colours a diagnostic row by its status: green OK, amber warning, red failure.</summary>
public sealed class DiagnosticStatusToBrushConverter : IValueConverter
{
    public static readonly DiagnosticStatusToBrushConverter Instance = new();

    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush Fail = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush Info = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8FA3C0"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DiagnosticStatus s
            ? s switch
            {
                DiagnosticStatus.Ok => Ok,
                DiagnosticStatus.Warning => Warn,
                DiagnosticStatus.Failed => Fail,
                DiagnosticStatus.Info => Info,
                _ => Muted
            }
            : Muted;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Short glyph for a diagnostic status, readable without colour.</summary>
public sealed class DiagnosticStatusToGlyphConverter : IValueConverter
{
    public static readonly DiagnosticStatusToGlyphConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DiagnosticStatus s
            ? s switch
            {
                DiagnosticStatus.Ok => "OK",
                DiagnosticStatus.Warning => "WARN",
                DiagnosticStatus.Failed => "FEHLER",
                DiagnosticStatus.Skipped => "UEBERSPR.",
                DiagnosticStatus.Info => "INFO",
                _ => "?"
            }
            : "?";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
