using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ViewLight.Services;

namespace ViewLight.Converters;

/// <summary>Colours a log line by its severity.</summary>
/// <remarks>
/// Deliberately mid-tone rather than tuned for one theme: a converter cannot follow the theme the way
/// a <c>DynamicResource</c> can, so these are picked to stay readable on the dark and the light
/// surface alike.
/// </remarks>
public sealed class LogLevelToBrushConverter : IValueConverter {

    private static readonly IBrush Info = new SolidColorBrush(Color.Parse("#7B8290"));
    private static readonly IBrush Success = new SolidColorBrush(Color.Parse("#26915F"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#9A6E1E"));
    private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#C24A42"));
    private static readonly IBrush Server = new SolidColorBrush(Color.Parse("#78849B"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LogLevel level
            ? level switch {
                LogLevel.Success => Success,
                LogLevel.Warning => Warning,
                LogLevel.Error => Error,
                LogLevel.Server => Server,
                _ => Info,
            }
            : Info;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
