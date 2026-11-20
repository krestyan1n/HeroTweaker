using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace HeroTweaker.Converters;

public sealed class IndexActiveBrushConverter : IMultiValueConverter
{
    private static readonly SolidColorBrush Active = new((Color)ColorConverter.ConvertFromString("#17313D"));
    private static readonly SolidColorBrush Inactive = new((Color)ColorConverter.ConvertFromString("#131A1D"));

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 2 && int.TryParse(values[0]?.ToString(), out var current) && int.TryParse(values[1]?.ToString(), out var target) && current == target)
            return Active;
        return Inactive;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
