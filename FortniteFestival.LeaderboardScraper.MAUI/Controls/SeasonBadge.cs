namespace FortniteFestival.LeaderboardScraper.MAUI.Controls;

public class SeasonBadge : ContentView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(SeasonBadge), string.Empty, propertyChanged: OnTextChanged);

    private readonly Grid _root;
    private readonly Frame _skewFrame; // using Frame for easier clipping
    private readonly Label _label;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public SeasonBadge()
    {
        _label = new Label
        {
            FontSize = 12,
            FontFamily = "NotoSansBold",
            FontAttributes = FontAttributes.Bold | FontAttributes.Italic,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            TextColor = Colors.White,
            Padding = new Thickness(0),
        };

        // Simulate a parallelogram by skewing the background path via a GraphicsView overlay
        var background = new GraphicsView
        {
            HeightRequest = 22,
            WidthRequest = 48,
            Drawable = new ParallelogramDrawable(() => BackgroundColorInternal)
        };

        _root = new Grid
        {
            Padding = 0,
            WidthRequest = 60,
            HeightRequest = 26
        };

        _root.Children.Add(background);
        _root.Children.Add(_label);
        Content = _root;
    }

    private Color BackgroundColorInternal => Color.FromArgb("063a7d");

    private static void OnTextChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is SeasonBadge b)
        {
            b._label.Text = newValue as string ?? string.Empty;
        }
    }

    private class ParallelogramDrawable : IDrawable
    {
        private readonly Func<Color> _colorProvider;
        public ParallelogramDrawable(Func<Color> colorProvider) => _colorProvider = colorProvider;
        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.SaveState();
            var baseColor = _colorProvider();
            canvas.FillColor = baseColor;
            // Create a simple parallelogram polygon
            var w = dirtyRect.Width;
            var h = dirtyRect.Height;
            var offset = h * 0.35f;
            var path = new PathF();
            path.MoveTo(offset, 0);
            path.LineTo(w, 0);
            path.LineTo(w - offset, h);
            path.LineTo(0, h);
            path.Close();
            canvas.FillPath(path);
            // subtle border
            canvas.StrokeColor = Color.FromArgb("0b509f");
            canvas.StrokeSize = 1;
            canvas.DrawPath(path);
            canvas.RestoreState();
        }
    }
}
