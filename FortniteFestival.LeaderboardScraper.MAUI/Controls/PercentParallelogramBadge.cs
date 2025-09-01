using System.ComponentModel;

namespace FortniteFestival.LeaderboardScraper.MAUI.Controls;

public class PercentParallelogramBadge : ContentView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(PercentParallelogramBadge), string.Empty, propertyChanged: OnTextChanged);

    private readonly GraphicsView _gfx;
    private readonly Label _label;
    private readonly BadgeDrawable _drawable;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public PercentParallelogramBadge()
    {
        // Match SeasonBadge visual height (26) and keep compact
        HeightRequest = 26;
        MinimumHeightRequest = 26;
        Padding = 0;
        _drawable = new BadgeDrawable(this);
        _gfx = new GraphicsView { Drawable = _drawable, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill };
        _label = new Label
        {
            // Match SeasonBadge font sizing / style
            FontSize = 12,
            FontFamily = "NotoSansBold",
            FontAttributes = FontAttributes.Bold | FontAttributes.Italic,
            TextColor = Colors.White,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            Padding = new Thickness(0),
            Margin = 0
        };
        _label.SetBinding(Label.TextProperty, new Binding(nameof(Text), source: this));

        var grid = new Grid { Padding = 0 };        
        grid.Children.Add(_gfx);
        grid.Children.Add(_label);
        Content = grid;

    SizeChanged += (_, _) => { _gfx.Invalidate(); UpdateDesiredWidth(); };
        PropertyChanged += OnAnyPropertyChanged;
    }

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Text))
            _gfx.Invalidate();
    }

    private static void OnTextChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is PercentParallelogramBadge b)
        {
            b._gfx.Invalidate();
            b.UpdateDesiredWidth();
            b.UpdateTextColor();
        }
    }

    private void UpdateDesiredWidth()
    {
        // Ensure we have a valid height to base skew offset upon
        double h = HeightRequest > 0 ? HeightRequest : Height;
        if (h <= 0) return;
        // Measure label intrinsic width
    var measuredSize = _label.Measure(double.PositiveInfinity, h);
    var measured = measuredSize.Width;
    // Emulate SeasonBadge: inner drawable height = total - 4 (top/bottom padding)
    double innerH = Math.Max(1, h - 4);
    var skewOffset = innerH * 0.35; // same proportion as SeasonBadge
    double horizontalPadding = 20; // padding including right side spacing
    // Width: text + slant offset + padding
    var target = measured + skewOffset + horizontalPadding;
        if (Math.Abs(WidthRequest - target) > 0.5)
            WidthRequest = target;
    // Center text horizontally by counteracting half the slant + half left padding
    _label.TranslationX = -(skewOffset * 0.5) - 2; // small nudge for visual centering
    _label.TranslationY = -1; // vertical optical alignment
    }

    private void UpdateTextColor()
    {
        // If exactly 100% then make text gold, else white
        var txt = Text?.Trim();
        if (string.Equals(txt, "100%", StringComparison.Ordinal))
            _label.TextColor = Color.FromArgb("#FFD700");
        else
            _label.TextColor = Colors.White;
    }

    private class BadgeDrawable : IDrawable
    {
        private readonly PercentParallelogramBadge _owner;
        public BadgeDrawable(PercentParallelogramBadge owner) => _owner = owner;
        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.SaveState();
            var h = dirtyRect.Height;
            var w = dirtyRect.Width;
            if (w <= 0 || h <= 0) { canvas.RestoreState(); return; }
            // Parallelogram identical style to SeasonBadge (but gold stroke, no fill)
            // Draw with 2px vertical inset so overall control height matches SeasonBadge (26 vs 22 drawable)
            float insetY = 2f;
            float innerH = h - insetY * 2f;
            float offset = innerH * 0.35f;
            var path = new PathF();
            path.MoveTo(offset, insetY);
            path.LineTo(w, insetY);
            path.LineTo(w - offset, insetY + innerH);
            path.LineTo(0, insetY + innerH);
            path.Close();
            var stroke = Color.FromArgb("#FFD700");
            canvas.StrokeColor = stroke;
            canvas.StrokeSize = 2;
            canvas.DrawPath(path);
            canvas.RestoreState();
        }
    }
}
