using System.Globalization;
using Microsoft.Maui.Controls;

namespace FortniteFestival.LeaderboardScraper.MAUI.Converters;

public class PercentHitConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int i)
        {
            var pct = i / 10000.0; // raw scaled by 10000
            return pct.ToString("0.00") + "%";
        }
        if (value is double d)
            return d.ToString("0.00") + "%";
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
