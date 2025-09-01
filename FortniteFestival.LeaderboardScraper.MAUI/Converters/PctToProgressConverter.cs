using System.Globalization;

namespace FortniteFestival.LeaderboardScraper.MAUI.Converters;

public class PctToProgressConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
            return d / 100.0;
        if (value is int i)
            return i / 100.0; // tolerate int percentages
        return 0d;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
