using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace HeroTweaker.Converters;

public class CategoryActiveConverter : IMultiValueConverter
{
    private static readonly SolidColorBrush ActiveBrush = new((Color)ColorConverter.ConvertFromString("#6366F1"));
    private static readonly SolidColorBrush InactiveBrush = new((Color)ColorConverter.ConvertFromString("#17171D"));

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is string current && values[1] is string selected)
        {
            if (string.Equals(current, selected, StringComparison.OrdinalIgnoreCase))
                return ActiveBrush;
        }
        return InactiveBrush;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}