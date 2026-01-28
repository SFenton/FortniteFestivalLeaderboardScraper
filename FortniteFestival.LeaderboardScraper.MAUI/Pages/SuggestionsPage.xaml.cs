using FortniteFestival.Core.Services;
using FortniteFestival.Core;
using FortniteFestival.Core.Config;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Suggestions;
using System.Linq;
using System.Diagnostics;
using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;
using FortniteFestival.LeaderboardScraper.MAUI.Converters;
using FortniteFestival.LeaderboardScraper.MAUI.Helpers;
using System.Collections.ObjectModel;
using Microsoft.Maui.Controls.Shapes;

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class SuggestionsPage : ContentPage
{
    private readonly IFestivalService _service;
    private readonly ISettingsPersistence _settingsPersistence;
    private Settings? _settings;
    private SuggestionGenerator _generator;
    private readonly object _generatorLock = new object();
    private bool _isLoading;
    private bool _endReached;
    private const int InitialBatchSize = 10;
    private const int SubsequentBatchSize = 4;

    public SuggestionsPage(IFestivalService service, ISettingsPersistence settingsPersistence)
    {
        InitializeComponent();
        _service = service;
        _settingsPersistence = settingsPersistence;
        _generator = new SuggestionGenerator(service);
        ApplyState();
        _service.ScoreUpdated += OnScoreUpdated;
        SizeChanged += OnSizeChanged;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        try
        {
            _service.ScoreUpdated -= OnScoreUpdated;
            SizeChanged -= OnSizeChanged;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SuggestionsPage] Error unsubscribing events: {ex.Message}");
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _service.ScoreUpdated += OnScoreUpdated;
        SizeChanged += OnSizeChanged;
        
        // Show spinner and hide content while loading
        LoadingSpinner.IsVisible = true;
        LoadingSpinner.IsRunning = true;
        MainScroll.IsVisible = false;
        EmptyState.IsVisible = false;
        
        // Run heavy work on background thread
        _ = Task.Run(async () => await RefreshSuggestionsAsync());
    }

    private async Task RefreshSuggestionsAsync()
    {
        // Load settings to filter by enabled instruments
        _settings = await _settingsPersistence.LoadSettingsAsync() ?? new Settings();
        
        lock (_generatorLock)
        {
            _generator = new SuggestionGenerator(_service);
        }
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ContentStack.Children.Clear();
            _endReached = false;
            ApplyState();
        });
    }

    private void RefreshSuggestions()
    {
        lock (_generatorLock)
        {
            _generator = new SuggestionGenerator(_service);
        }
        ContentStack.Children.Clear();
        _endReached = false;
        ApplyState();
    }

    private void ApplyState()
    {
        bool hasAny = _service.ScoresIndex != null && _service.ScoresIndex.Count > 0;
        EmptyState.IsVisible = !hasAny;
        MainScroll.IsVisible = hasAny;
        LoadingSpinner.IsVisible = false;
        LoadingSpinner.IsRunning = false;
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
        
        // Run on background thread
        Task.Run(() =>
        {
            try
            {
                var categoriesToAdd = new List<View>();
                int remaining = InitialBatchSize;
                while (remaining > 0)
                {
                    List<SuggestionCategory> next;
                    lock (_generatorLock)
                    {
                        next = _generator.GetNext(remaining).ToList();
                    }
                    if (next.Count == 0)
                    {
                        _endReached = true;
                        break;
                    }
                    foreach (var cat in next)
                    {
                        // Skip categories for disabled instruments
                        if (!ShouldShowCategory(cat))
                            continue;
                        
                        // Build view on main thread
                        View view = null;
                        MainThread.InvokeOnMainThreadAsync(() => view = BuildCategoryView(cat)).Wait();
                        categoriesToAdd.Add(view);
                    }
                    remaining -= next.Count;
                }
                
                // Update UI on main thread
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    foreach (var v in categoriesToAdd)
                        ContentStack.Children.Add(v);
                    _isLoading = false;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SuggestionsPage] Error loading initial suggestions: {ex.Message}");
                MainThread.BeginInvokeOnMainThread(() => _isLoading = false);
            }
        });
    }

    private void LoadMore()
    {
        if (_isLoading) return;
        _isLoading = true;
        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;
        
        // Run on background thread
        Task.Run(() =>
        {
            try
            {
                var categoriesToAdd = new List<View>();
                int remaining = SubsequentBatchSize;
                while (remaining > 0)
                {
                    List<SuggestionCategory> next;
                    lock (_generatorLock)
                    {
                        next = _generator.GetNext(remaining).ToList();
                    }
                    if (next.Count == 0) { _endReached = true; break; }
                    foreach (var cat in next)
                    {
                        // Skip categories for disabled instruments
                        if (!ShouldShowCategory(cat))
                            continue;
                        
                        View view = null;
                        MainThread.InvokeOnMainThreadAsync(() => view = BuildCategoryView(cat)).Wait();
                        categoriesToAdd.Add(view);
                    }
                    remaining -= next.Count;
                }
                // If pipeline exhausted, reset for endless feed
                if (_endReached)
                {
                    lock (_generatorLock)
                    {
                        _generator.ResetForEndless();
                    }
                    _endReached = false;
                }
                
                // Update UI on main thread
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    foreach (var v in categoriesToAdd)
                        ContentStack.Children.Add(v);
                    LoadingIndicator.IsRunning = false;
                    LoadingIndicator.IsVisible = false;
                    _isLoading = false;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SuggestionsPage] Error loading more suggestions: {ex.Message}");
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    LoadingIndicator.IsRunning = false;
                    LoadingIndicator.IsVisible = false;
                    _isLoading = false;
                });
            }
        });
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
                if (_settings != null)
                    row.ApplySettingsFilter(_settings);
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
                circle.SetBinding(Border.StrokeProperty, "CircleStrokeBrush");
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
            wide.SetBinding(VisualElement.IsVisibleProperty, new Binding("UseCompactLayout", converter: new InverseBoolConverter()));
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
        var container = LayoutConstants.CreateCard(
            new VerticalStackLayout { Spacing = 12, Children = { header, collection } });
        return container;
    }

    private void OnSizeChanged(object? sender, EventArgs e)
    {
        bool compact = Width < 900;
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
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    await Shell.Current.Navigation.PushAsync(page);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SuggestionsPage] Navigation error: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SuggestionsPage] Error creating song info page: {ex.Message}");
        }
    }

    private void NavigateToSong(string songId)
    {
        var song = _service.Songs.FirstOrDefault(s => s.track.su == songId);
        if (song == null) return;
        NavigateToRow(new SongDisplayRow(song, _service) { UseCompactLayout = Width < 900 });
    }

    /// <summary>
    /// Checks if a suggestion category should be shown based on sync settings.
    /// Categories targeting a specific instrument (via Key) are filtered out if that instrument is disabled.
    /// </summary>
    private bool ShouldShowCategory(SuggestionCategory cat)
    {
        if (_settings == null) return true;
        
        var key = cat.Key ?? string.Empty;
        
        // Check for instrument-specific categories based on key patterns
        if (key.Contains("_guitar") && !key.Contains("pro_guitar"))
            return _settings.QueryLead;
        if (key.Contains("_bass") && !key.Contains("pro_bass"))
            return _settings.QueryBass;
        if (key.Contains("_drums"))
            return _settings.QueryDrums;
        if (key.Contains("_vocals"))
            return _settings.QueryVocals;
        if (key.Contains("_pro_guitar") || key.Contains("pro_guitar"))
            return _settings.QueryProLead;
        if (key.Contains("_pro_bass") || key.Contains("pro_bass"))
            return _settings.QueryProBass;
        
        // General categories are always shown
        return true;
    }
}
