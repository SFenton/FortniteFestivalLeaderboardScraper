using System.Globalization;

namespace FortniteFestival.LeaderboardScraper.MAUI.Converters;

public class PctToProgressConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double d)
            return d / 100.0;
        return 0d;
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture
    ) => throw new NotImplementedException();
}
