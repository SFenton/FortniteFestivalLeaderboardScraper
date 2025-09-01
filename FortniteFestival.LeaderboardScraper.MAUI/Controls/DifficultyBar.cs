using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace FortniteFestival.LeaderboardScraper.MAUI.Controls;

/// <summary>
/// Small parallelogram bar used to visualize instrument difficulty (filled or empty).
/// Uses the same drawing technique as SeasonBadge / PercentParallelogramBadge for consistency.
/// </summary>
public class DifficultyBar : GraphicsView
{
    public static readonly BindableProperty FillColorProperty = BindableProperty.Create(
        nameof(FillColor), typeof(Color), typeof(DifficultyBar), Colors.White, propertyChanged: OnFillColorChanged);

    public Color FillColor
    {
        get => (Color)GetValue(FillColorProperty);
        set => SetValue(FillColorProperty, value);
    }

    private readonly DifficultyBarDrawable _drawable;

    public DifficultyBar()
    {
        HeightRequest = 32; // default; caller can override
        WidthRequest = 10;  // default; caller can override
        _drawable = new DifficultyBarDrawable(this);
        Drawable = _drawable;
        BackgroundColor = Colors.Transparent;
        // Slight margin to visually separate if parent spacing very tight
        Margin = 0;
    }

    private static void OnFillColorChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is DifficultyBar bar)
            bar.Invalidate();
    }

    private sealed class DifficultyBarDrawable : IDrawable
    {
        private readonly DifficultyBar _bar;
        public DifficultyBarDrawable(DifficultyBar bar) => _bar = bar;
        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            var w = dirtyRect.Width;
            var h = dirtyRect.Height;
            if (w <= 0 || h <= 0) return;
            canvas.SaveState();
            canvas.FillColor = _bar.FillColor;
            // For small narrow bars we reduce slant so they remain visibly thick.
            // Use a height-based ratio but clamp so offset never exceeds 40% of width.
            float desired = (float)(h * 0.18f); // gentler than badges
            float offset = Math.Min(desired, w * 0.40f);
            var path = new PathF();
            path.MoveTo(offset, 0);
            path.LineTo(w, 0);
            path.LineTo(w - offset, h);
            path.LineTo(0, h);
            path.Close();
            canvas.FillPath(path);
            canvas.RestoreState();
        }
    }
}
