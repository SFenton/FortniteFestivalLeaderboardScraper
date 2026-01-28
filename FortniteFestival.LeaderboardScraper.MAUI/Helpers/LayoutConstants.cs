using Microsoft.Maui.Controls.Shapes;

namespace FortniteFestival.LeaderboardScraper.MAUI.Helpers;

/// <summary>
/// Centralized layout constants for consistent styling across the app.
/// </summary>
public static class LayoutConstants
{
    // Page-level padding
    public static readonly Thickness PagePadding = new(12, 8, 12, 8);
    
    // Card styling
    public static readonly Thickness CardPadding = new(16, 14, 16, 14);
    public const double CardCornerRadius = 18;
    
    // Colors
    public static readonly Color PageBackground = Color.FromArgb("#9d4dbb");
    public static readonly Color CardBackground = Color.FromArgb("#55FFFFFF");
    public static readonly Color CardStroke = Color.FromArgb("#88FFFFFF");
    public static readonly Color NavBackground = Color.FromArgb("#4B0F63");
    
    /// <summary>
    /// Creates a styled card Border with standard app styling.
    /// </summary>
    public static Border CreateCard(View content, Color? backgroundColor = null)
    {
        return new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = CardCornerRadius },
            BackgroundColor = backgroundColor ?? CardBackground,
            Stroke = CardStroke,
            StrokeThickness = 1,
            Padding = CardPadding,
            Content = content
        };
    }
}
