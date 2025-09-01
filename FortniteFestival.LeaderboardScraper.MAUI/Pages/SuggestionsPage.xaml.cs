using FortniteFestival.Core.Services;
using FortniteFestival.Core;
using FortniteFestival.Core.Suggestions;
using System.Linq;
using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;
using System.Collections.ObjectModel;
using Microsoft.Maui.Controls.Shapes;

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class SuggestionsPage : ContentPage
{
    private readonly IFestivalService _service;
    private SuggestionGenerator _generator;
    private bool _isLoading;
    private bool _endReached; // when generator exhausted
    private const int InitialBatchSize = 10;
    private const int SubsequentBatchSize = 4;

    public SuggestionsPage(IFestivalService service)
    {
        InitializeComponent();
        _service = service;
        DrawerRoot.Service = service; // supply service to drawer for suggestions visibility + navigation
        _generator = new SuggestionGenerator(service);
        ApplyState();
        try { _service.ScoreUpdated += OnScoreUpdated; } catch { }
    SizeChanged += OnSizeChanged;
    }

    private void ApplyState()
    {
        bool hasAny = _service.ScoresIndex != null && _service.ScoresIndex.Count > 0;
        EmptyState.IsVisible = !hasAny;
        ContentStack.IsVisible = hasAny;
        if (!hasAny) return; // keep empty state
        if (ContentStack.Children.Count == 0)
        {
            _endReached = false;
            LoadInitial();
        }
    }

    private void OnScoreUpdated(LeaderboardData obj) => MainThread.BeginInvokeOnMainThread(ApplyState);

    private void OnScrolled(object sender, ScrolledEventArgs e)
    {
        if (_endReached || _isLoading) return;
        // near bottom threshold (within 300px)
        double remaining = MainScroll.ContentSize.Height - (e.ScrollY + MainScroll.Height);
        if (remaining < 300) LoadMore();
    }

    private void LoadInitial()
    {
        if (_isLoading) return;
        _isLoading = true;
        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;
        try
        {
            int remaining = InitialBatchSize;
            while (remaining > 0)
            {
                var next = _generator.GetNext(remaining).ToList();
                if (next.Count == 0)
                {
                    _endReached = true;
                    break;
                }
                foreach (var cat in next) ContentStack.Children.Add(BuildCategoryView(cat));
                remaining -= next.Count;
                // Safety break to avoid infinite loop if generator misbehaves
                if (next.Count == 0) break;
            }
        }
        finally
        {
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
            _isLoading = false;
            ContentStack.IsVisible = ContentStack.Children.Count > 0;
        }
    }

    private void LoadMore()
    {
        if (_isLoading) return;
        _isLoading = true;
        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;
        try
        {
            int remaining = SubsequentBatchSize;
            while (remaining > 0)
            {
                var next = _generator.GetNext(remaining).ToList();
                if (next.Count == 0) { _endReached = true; break; }
                foreach (var cat in next) ContentStack.Children.Add(BuildCategoryView(cat));
                remaining -= next.Count;
                if (next.Count == 0) break;
            }
            // If we have fewer than 6 categories remaining unseen (i.e., pipeline near exhaustion), reset for endless feed.
            if (_endReached)
            {
                _generator.ResetForEndless();
                _endReached = false;
            }
        }
        finally
        {
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
            _isLoading = false;
            ContentStack.IsVisible = ContentStack.Children.Count > 0;
        }
    }

    private View BuildCategoryView(SuggestionCategory cat)
    {
        // Build SongDisplayRow list for this category
        var rows = new ObservableCollection<SongDisplayRow>();
        foreach (var s in cat.Songs)
        {
            var song = _service.Songs.FirstOrDefault(x => x.track.su == s.SongId);
            if (song != null)
            {
                var row = new SongDisplayRow(song, _service) { UseCompactLayout = Width < 900 };
                row.RefreshScore(_service); // ensure instrument statuses populate
                rows.Add(row);
            }
        }

        var header = new VerticalStackLayout
        {
            Spacing = 2,
            Children =
            {
                new Label { Text = cat.Title, FontFamily = "NotoSansBold", FontSize = 20 },
                new Label { Text = cat.Description, FontSize = 13, Opacity = 0.85 }
            }
        };

        // DataTemplate replicating HomePage song row layout
        var rowTemplate = new DataTemplate(() =>
        {
            var root = new Grid();
            var tap = new TapGestureRecognizer();
            tap.Tapped += OnSuggestionSongTapped;
            root.GestureRecognizers.Add(tap);
            // Wide layout
            var wide = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new ColumnDefinition{ Width = new GridLength(60) }, new ColumnDefinition{ Width = GridLength.Star }, new ColumnDefinition{ Width = GridLength.Auto } }, ColumnSpacing = 8, Padding = 6 };
            var artBorder = new Border { StrokeShape = new RoundRectangle { CornerRadius = 4 }, WidthRequest = 56, HeightRequest = 56, BackgroundColor = Color.FromArgb("#444444"), HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Center };
            artBorder.Content = new Image { Aspect = Aspect.AspectFill }; artBorder.Content.SetBinding(Image.SourceProperty, "AlbumArtPath");
            wide.Add(artBorder);
            var vsl = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
            var titleLbl = new Label { FontAttributes = FontAttributes.Bold, FontFamily = "NotoSansBold", LineBreakMode = LineBreakMode.TailTruncation };
            titleLbl.SetBinding(Label.TextProperty, "Title");
            var artistLbl = new Label { FontSize = 12, FontFamily = "NotoSansRegular", LineBreakMode = LineBreakMode.TailTruncation };
            artistLbl.SetBinding(Label.TextProperty, "ArtistYearDisplay");
            vsl.Children.Add(titleLbl); vsl.Children.Add(artistLbl);
            wide.Add(vsl); Grid.SetColumn(vsl,1);
            var instStack = new HorizontalStackLayout { Spacing = 8, VerticalOptions = LayoutOptions.Center };
            instStack.SetBinding(BindableLayout.ItemsSourceProperty, "InstrumentStatuses");
            BindableLayout.SetItemTemplate(instStack, new DataTemplate(() =>
            {
                var g = new Grid { WidthRequest = 48, HeightRequest = 48 };
                var circle = new Border { StrokeShape = new Ellipse(), WidthRequest = 48, HeightRequest = 48, StrokeThickness = 3 };
                circle.SetBinding(VisualElement.IsVisibleProperty, "ShowCircle");
                circle.SetBinding(Border.BackgroundColorProperty, "CircleFillColor");
                circle.SetBinding(Border.StrokeProperty, "CircleStrokeColor");
                g.Add(circle);
                var icon = new Image { WidthRequest = 40, HeightRequest = 40, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
                icon.SetBinding(Image.SourceProperty, "Icon");
                g.Add(icon);
                return g;
            }));
            wide.Add(instStack); Grid.SetColumn(instStack,2);

            // Compact layout
            var compact = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new ColumnDefinition{ Width = new GridLength(60) }, new ColumnDefinition{ Width = GridLength.Star } }, ColumnSpacing = 8, Padding = 6 };
            var cArt = new Border { StrokeShape = new RoundRectangle { CornerRadius = 4 }, WidthRequest = 56, HeightRequest = 56, BackgroundColor = Color.FromArgb("#444444"), HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Center };
            cArt.Content = new Image { Aspect = Aspect.AspectFill }; cArt.Content.SetBinding(Image.SourceProperty, "AlbumArtPath");
            compact.Add(cArt);
            var cVsl = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
            var cTitle = new Label { FontAttributes = FontAttributes.Bold, FontFamily = "NotoSansBold", LineBreakMode = LineBreakMode.TailTruncation };
            cTitle.SetBinding(Label.TextProperty, "Title");
            var cArtist = new Label { FontSize = 12, FontFamily = "NotoSansRegular", LineBreakMode = LineBreakMode.TailTruncation };
            cArtist.SetBinding(Label.TextProperty, "ArtistYearDisplay");
            cVsl.Children.Add(cTitle); cVsl.Children.Add(cArtist);
            compact.Add(cVsl); Grid.SetColumn(cVsl,1);

            // Visibility bindings (match HomePage logic using UseCompactLayout)
            // Manual inversion since we can't easily access the XAML converter resource from here
            wide.SetBinding(VisualElement.IsVisibleProperty, new Binding("UseCompactLayout", converter: new BoolInvertConverter()));
            compact.SetBinding(VisualElement.IsVisibleProperty, "UseCompactLayout");

            root.Add(wide);
            root.Add(compact);
            return root;
        });

        var collection = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            ItemsSource = rows,
            ItemTemplate = rowTemplate,
            BackgroundColor = Colors.Transparent
        };

        // Container padding tuned so left edge of song text aligns with list rows on Songs page (hamburger uses 12px + header grid padding)
        var container = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            BackgroundColor = Color.FromArgb("#b35cd6"),
            Padding = new Thickness(16, 14, 16, 14),
            Content = new VerticalStackLayout { Spacing = 12, Children = { header, collection } }
        };
        return container;
    }

    private void OnSizeChanged(object? sender, EventArgs e)
    {
        bool compact = Width < 900;
        // Adjust outer padding to align with hamburger (which uses 12 horizontal padding)
        if (ContentStack != null)
        {
            // Horizontal 16 here pairs with container internal 16 to visually align with 12px hamburger + its label width; compact reduces slightly
            ContentStack.Padding = compact ? new Thickness(12,6,12,24) : new Thickness(16,8,16,32);
        }
        // Update existing rows' compact flag
        if (ContentStack == null) return;
        foreach (var border in ContentStack.Children.OfType<Border>())
        {
            if (border.Content is Layout layout)
            {
                UpdateCollectionViews(layout, compact);
            }
        }
    }

    private void UpdateCollectionViews(Layout root, bool compact)
    {
        foreach (var child in root.Children)
        {
            if (child is CollectionView cv && cv.ItemsSource is IEnumerable<SongDisplayRow> sr)
            {
                foreach (var r in sr) r.UseCompactLayout = compact;
                cv.ItemsSource = null; // force refresh binding
                cv.ItemsSource = sr;
            }
            else if (child is Layout nested)
            {
                UpdateCollectionViews(nested, compact);
            }
            else if (child is Border b && b.Content is Layout inner)
            {
                UpdateCollectionViews(inner, compact);
            }
        }
    }

    private void OnSuggestionSongTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is SongDisplayRow row) NavigateToRow(row);
        else if (sender is VisualElement ve && ve.BindingContext is SongDisplayRow r2) NavigateToRow(r2);
    }

    private void NavigateToRow(SongDisplayRow row)
    {
        try
        {
            var order = new List<string> { "guitar","drums","vocals","bass","pro_guitar","pro_bass" };
            var vm = new SongInfoViewModel(row, order, _service);
            var page = new SongInfoPage(vm);
            MainThread.BeginInvokeOnMainThread(async () => { try { await Navigation.PushAsync(page); } catch { } });
        }
        catch { }
    }

    private void NavigateToSong(string songId)
    {
        var song = _service.Songs.FirstOrDefault(s => s.track.su == songId);
        if (song == null) return;
        NavigateToRow(new SongDisplayRow(song, _service) { UseCompactLayout = Width < 900 });
    }
}

internal class BoolInvertConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool b) return !b; return true;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool b) return !b; return false;
    }
}
