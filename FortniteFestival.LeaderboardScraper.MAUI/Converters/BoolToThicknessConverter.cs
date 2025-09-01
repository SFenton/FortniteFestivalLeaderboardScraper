using System.Globalization;

namespace FortniteFestival.LeaderboardScraper.MAUI.Converters;

public class BoolToThicknessConverter : IValueConverter
{
    public Thickness TrueThickness { get; set; } = new Thickness(2);
    public Thickness FalseThickness { get; set; } = new Thickness(0);
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return TrueThickness;
        return FalseThickness;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
