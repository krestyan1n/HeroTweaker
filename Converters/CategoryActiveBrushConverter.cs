using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace HeroTweaker.Converters;

/// <summary>Recognises the active navigation brush without relying on screen coordinates.</summary>
public sealed class CategoryActiveBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is SolidColorBrush brush && brush.Color == Color.FromRgb(0x26, 0x32, 0x38);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
