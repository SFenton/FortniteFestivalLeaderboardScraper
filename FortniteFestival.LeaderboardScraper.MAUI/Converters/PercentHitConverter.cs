using System.Globalization;
using Microsoft.Maui.Controls;

namespace FortniteFestival.LeaderboardScraper.MAUI.Converters;

public class PercentHitConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if(value is int i)
        {
            // Core stores percentHit scaled by 10000 (100% == 1000000)
            var pct = i / 10000.0; return pct.ToString("0.00") + "%";
        }
        return "";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
