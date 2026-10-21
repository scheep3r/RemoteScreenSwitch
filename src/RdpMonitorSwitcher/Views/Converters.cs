using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RdpMonitorSwitcher.Core.Models;

namespace RdpMonitorSwitcher.Views;

public static class Converters
{
    public static IValueConverter NotNullToVisibility { get; } = new Lambda(v =>
        v is null || v is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible);

    public static IValueConverter StateToBrush { get; } = new Lambda(v => v switch
    {
        ConnectionState.Connected => Application.Current.Resources["OkBrush"],
        ConnectionState.Disconnected => Application.Current.Resources["OffBrush"],
        _ => Application.Current.Resources["BusyBrush"],
    });

    public static IValueConverter FullscreenLabel { get; } = new Lambda(v => v is true ? "⤡ Fenster" : "⛶ Vollbild");

    public static IValueConverter ScalingLabel { get; } = new Lambda(v => v is ScalingMode.Original ? "1:1" : "Anpassen");

    private sealed class Lambda(Func<object?, object?> convert) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
