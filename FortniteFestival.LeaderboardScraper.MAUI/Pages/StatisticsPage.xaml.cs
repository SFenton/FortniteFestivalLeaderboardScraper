using System.Diagnostics;
using System.Collections.ObjectModel;
using FortniteFestival.Core.Services;
using FortniteFestival.Core;
using FortniteFestival.Core.Config;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Suggestions;
using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;
using FortniteFestival.LeaderboardScraper.MAUI.Converters;
using FortniteFestival.LeaderboardScraper.MAUI.Helpers;
using Microsoft.Maui.Controls.Shapes;

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class StatisticsPage : ContentPage
{
    private readonly IFestivalService _service;
    private readonly ISettingsPersistence _settingsPersistence;
    private Settings? _settings;
    private StatisticsViewModel? _statsVm;

    public StatisticsPage(IFestivalService service, ISettingsPersistence settingsPersistence)
    {
        InitializeComponent();
        _service = service;
        _settingsPersistence = settingsPersistence;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _service.ScoreUpdated += OnScoreUpdated;
        
        // Show spinner and hide content while loading
        LoadingSpinner.IsVisible = true;
        LoadingSpinner.IsRunning = true;
        MainScroll.IsVisible = false;
        EmptyState.IsVisible = false;
        
        // Run heavy work on background thread
        _ = Task.Run(async () => await RebuildStatisticsAsync());
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _service.ScoreUpdated -= OnScoreUpdated;
    }

    private void OnScoreUpdated(LeaderboardData obj)
    {
        _ = Task.Run(async () => await RebuildStatisticsAsync());
    }

    private async Task RebuildStatisticsAsync()
    {
        try
        {
            // Load settings to filter by enabled instruments
            _settings = await _settingsPersistence.LoadSettingsAsync() ?? new Settings();
            
            if (_statsVm == null)
                _statsVm = new StatisticsViewModel(_service);

            _statsVm.Refresh();

            bool hasAny = _service.ScoresIndex != null && _service.ScoresIndex.Count > 0;
            bool hasData = hasAny && _statsVm.HasData;

            // Build views on main thread
            var instrumentCards = new List<View>();
            var categoryViews = new List<View>();
            
            if (_statsVm.HasData)
            {
                foreach (var instStats in _statsVm.InstrumentStats)
                {
                    // Skip disabled instruments based on sync settings
                    if (!IsInstrumentEnabled(instStats.InstrumentKey))
                        continue;
                    
                    View view = null;
                    MainThread.InvokeOnMainThreadAsync(() => view = BuildInstrumentDetailedCard(instStats)).Wait();
                    instrumentCards.Add(view);
                }

                foreach (var cat in _statsVm.TopSongCategories)
                {
                    // Filter out categories for disabled instruments
                    if (!ShouldShowCategory(cat.Key))
                        continue;
                    
                    View view = null;
                    MainThread.InvokeOnMainThreadAsync(() => view = BuildSuggestionCategoryView(cat)).Wait();
                    categoryViews.Add(view);
                }
            }

            // Update UI on main thread
            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatsStack.Children.Clear();
                EmptyState.IsVisible = !hasData;
                MainScroll.IsVisible = hasData;
                LoadingSpinner.IsVisible = false;
                LoadingSpinner.IsRunning = false;

                foreach (var v in instrumentCards)
                    StatsStack.Children.Add(v);
                foreach (var v in categoryViews)
                    StatsStack.Children.Add(v);
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StatisticsPage] Error building statistics: {ex.Message}");
            MainThread.BeginInvokeOnMainThread(() =>
            {
                LoadingSpinner.IsVisible = false;
                LoadingSpinner.IsRunning = false;
            });
        }
    }

    private void RebuildStatistics()
    {
        try
        {
            if (_statsVm == null)
                _statsVm = new StatisticsViewModel(_service);

            StatsStack.Children.Clear();
            _statsVm.Refresh();

            bool hasAny = _service.ScoresIndex != null && _service.ScoresIndex.Count > 0;
            bool hasData = hasAny && _statsVm.HasData;
            
            EmptyState.IsVisible = !hasData;
            MainScroll.IsVisible = hasData;
            LoadingSpinner.IsVisible = false;
            LoadingSpinner.IsRunning = false;

            if (!_statsVm.HasData) return;

            // Build per-instrument detailed cards
            foreach (var instStats in _statsVm.InstrumentStats)
            {
                StatsStack.Children.Add(BuildInstrumentDetailedCard(instStats));
            }

            // Build Top Songs categories
            foreach (var cat in _statsVm.TopSongCategories)
                StatsStack.Children.Add(BuildSuggestionCategoryView(cat));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StatisticsPage] Error building statistics: {ex.Message}");
            LoadingSpinner.IsVisible = false;
            LoadingSpinner.IsRunning = false;
        }
    }

    private View BuildInstrumentDetailedCard(InstrumentDetailedStats stats)
    {
        var container = new VerticalStackLayout { Spacing = 10 };

        // Header with icon and instrument name
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star }
            },
            ColumnSpacing = 12
        };

        var icon = new Image
        {
            Source = stats.Icon,
            WidthRequest = 48,
            HeightRequest = 48,
            Aspect = Aspect.AspectFit
        };
        headerGrid.Children.Add(icon);

        var headerText = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        headerText.Children.Add(new Label
        {
            Text = stats.InstrumentLabel,
            FontFamily = "NotoSansBold",
            FontSize = 22,
            TextColor = Colors.White
        });
        headerText.Children.Add(new Label
        {
            Text = $"{stats.SongsPlayed} of {stats.TotalSongsInLibrary} songs played ({stats.CompletionPercent:F1}%)",
            FontSize = 13,
            Opacity = 0.85,
            TextColor = Colors.White
        });
        headerGrid.Children.Add(headerText);
        Grid.SetColumn(headerText, 1);
        container.Children.Add(headerGrid);

        // Two-column stats layout
        var statsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Star }
            },
            ColumnSpacing = 16,
            RowSpacing = 6
        };

        int row = 0;
        AddStatCell(statsGrid, "FCs", $"{stats.FcCount} ({stats.FcPercent:F1}%)", 0, row);
        AddStatCell(statsGrid, "Gold Stars", $"{stats.GoldStarCount}", 1, row);
        row++;
        AddStatCell(statsGrid, "5 Stars", $"{stats.FiveStarCount}", 0, row);
        AddStatCell(statsGrid, "4 Stars", $"{stats.FourStarCount}", 1, row);
        row++;
        AddStatCell(statsGrid, "Avg Accuracy", $"{stats.AverageAccuracy:F2}%", 0, row);
        AddStatCell(statsGrid, "Best Accuracy", $"{stats.BestAccuracy:F2}%", 1, row);
        row++;
        AddStatCell(statsGrid, "Perfect Scores", $"{stats.PerfectScoreCount}", 0, row);
        AddStatCell(statsGrid, "Avg Stars", $"{stats.AverageStars:F2}", 1, row);
        row++;
        AddStatCell(statsGrid, "Total Score", FormatScore(stats.TotalScore), 0, row);
        AddStatCell(statsGrid, "Highest Score", FormatScore(stats.HighestScore), 1, row);
        row++;
        AddStatCell(statsGrid, "Best Rank", stats.BestRank > 0 ? $"#{stats.BestRank:N0}" : "—", 0, row);
        AddStatCell(statsGrid, "Weighted Percentile", !double.IsNaN(stats.WeightedPercentile) ? FormatPercentile(stats.WeightedPercentile) : "—", 1, row);
        row++;

        container.Children.Add(statsGrid);

        // Percentile distribution bar
        if (stats.SongsPlayed > 0)
        {
            container.Children.Add(new Label
            {
                Text = "Percentile Distribution",
                FontFamily = "NotoSansBold",
                FontSize = 14,
                TextColor = Colors.White,
                Margin = new Thickness(0, 8, 0, 4)
            });

            container.Children.Add(BuildPercentileDistributionBar(stats));
            container.Children.Add(BuildPercentileDistributionLegend(stats));
        }

        var border = LayoutConstants.CreateCard(container, GetInstrumentColor(stats.InstrumentKey));

        return border;
    }

    private static void AddStatCell(Grid grid, string label, string value, int col, int row)
    {
        while (grid.RowDefinitions.Count <= row)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var stack = new VerticalStackLayout { Spacing = 0 };
        stack.Children.Add(new Label
        {
            Text = value,
            FontFamily = "NotoSansBold",
            FontSize = 17,
            TextColor = Colors.White
        });
        stack.Children.Add(new Label
        {
            Text = label,
            FontSize = 11,
            Opacity = 0.75,
            TextColor = Colors.White
        });

        grid.Children.Add(stack);
        Grid.SetColumn(stack, col);
        Grid.SetRow(stack, row);
    }

    private static string FormatScore(double score)
    {
        if (score >= 1_000_000_000) return $"{score / 1_000_000_000:F2}B";
        if (score >= 1_000_000) return $"{score / 1_000_000:F2}M";
        if (score >= 1_000) return $"{score / 1_000:F1}K";
        return $"{score:N0}";
    }

    private static string FormatPercentile(double rawPercentile)
    {
        double topPct = Math.Max(0.01, Math.Min(100.0, rawPercentile * 100.0));
        if (topPct < 1) return $"Top {topPct:F2}%";
        return $"Top {topPct:F0}%";
    }

    private static View BuildPercentileDistributionBar(InstrumentDetailedStats stats)
    {
        var total = stats.Top1PercentCount + stats.Top5PercentCount + stats.Top10PercentCount +
                    stats.Top25PercentCount + stats.Top50PercentCount + stats.Below50PercentCount;

        if (total == 0)
            return new BoxView { HeightRequest = 20, BackgroundColor = Colors.Gray, CornerRadius = 4 };

        var barGrid = new Grid
        {
            ColumnSpacing = 1,
            HeightRequest = 24,
            HorizontalOptions = LayoutOptions.Fill
        };

        var segments = new (int count, Color color)[]
        {
            (stats.Top1PercentCount, Color.FromArgb("#FFD700")),
            (stats.Top5PercentCount, Color.FromArgb("#C0C0C0")),
            (stats.Top10PercentCount, Color.FromArgb("#CD7F32")),
            (stats.Top25PercentCount, Color.FromArgb("#4CAF50")),
            (stats.Top50PercentCount, Color.FromArgb("#2196F3")),
            (stats.Below50PercentCount, Color.FromArgb("#9E9E9E"))
        };

        int colIndex = 0;
        foreach (var (count, color) in segments)
        {
            if (count <= 0) continue;

            double width = (count / (double)total);
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });

            var box = new BoxView
            {
                BackgroundColor = color,
                CornerRadius = colIndex == 0 ? new CornerRadius(4, 0, 0, 4) :
                               (colIndex == segments.Count(s => s.count > 0) - 1 ? new CornerRadius(0, 4, 4, 0) : 0)
            };
            barGrid.Children.Add(box);
            Grid.SetColumn(box, colIndex);
            colIndex++;
        }

        return new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 4 },
            Stroke = Colors.Transparent,
            BackgroundColor = Colors.Transparent,
            Content = barGrid
        };
    }

    private static View BuildPercentileDistributionLegend(InstrumentDetailedStats stats)
    {
        var legend = new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            JustifyContent = Microsoft.Maui.Layouts.FlexJustify.Start,
            AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center
        };

        var items = new (string label, int count, Color color)[]
        {
            ("Top 1%", stats.Top1PercentCount, Color.FromArgb("#FFD700")),
            ("Top 5%", stats.Top5PercentCount, Color.FromArgb("#C0C0C0")),
            ("Top 10%", stats.Top10PercentCount, Color.FromArgb("#CD7F32")),
            ("Top 25%", stats.Top25PercentCount, Color.FromArgb("#4CAF50")),
            ("Top 50%", stats.Top50PercentCount, Color.FromArgb("#2196F3")),
            ("50%+", stats.Below50PercentCount, Color.FromArgb("#9E9E9E"))
        };

        foreach (var (label, count, color) in items)
        {
            if (count <= 0) continue;

            var item = new HorizontalStackLayout { Spacing = 4, Margin = new Thickness(0, 0, 12, 4) };
            item.Children.Add(new BoxView { BackgroundColor = color, WidthRequest = 12, HeightRequest = 12, CornerRadius = 2 });
            item.Children.Add(new Label
            {
                Text = $"{label}: {count}",
                FontSize = 11,
                TextColor = Colors.White,
                Opacity = 0.9
            });
            legend.Children.Add(item);
        }

        return legend;
    }

    private static Color GetInstrumentColor(string key)
    {
        return key switch
        {
            "guitar" => Color.FromArgb("#b35cd6"),
            "bass" => Color.FromArgb("#3498db"),
            "drums" => Color.FromArgb("#e74c3c"),
            "vocals" => Color.FromArgb("#27ae60"),
            "pro_guitar" => Color.FromArgb("#9b59b6"),
            "pro_bass" => Color.FromArgb("#2980b9"),
            _ => Color.FromArgb("#7f8c8d")
        };
    }

    private View BuildSuggestionCategoryView(SuggestionCategory cat)
    {
        var rows = new ObservableCollection<SongDisplayRow>();
        foreach (var s in cat.Songs)
        {
            var song = _service.Songs.FirstOrDefault(x => x.track.su == s.SongId);
            if (song != null)
            {
                var row = new SongDisplayRow(song, _service) { UseCompactLayout = Width < 900 };
                row.RefreshScore(_service);
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

        var rowTemplate = new DataTemplate(() =>
        {
            var root = new Grid();
            var tap = new TapGestureRecognizer();
            tap.Tapped += OnSongTapped;
            root.GestureRecognizers.Add(tap);

            var wide = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new ColumnDefinition{ Width = new GridLength(60) }, new ColumnDefinition{ Width = GridLength.Star }, new ColumnDefinition{ Width = GridLength.Auto } }, ColumnSpacing = 8, Padding = 6 };
            var artBorder = new Border { StrokeShape = new RoundRectangle { CornerRadius = 4 }, WidthRequest = 56, HeightRequest = 56, BackgroundColor = Color.FromArgb("#444444"), HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Center };
            artBorder.Content = new Image { Aspect = Aspect.AspectFill }; artBorder.Content.SetBinding(Image.SourceProperty, "AlbumArtPath");
            wide.Add(artBorder);
            var vsl = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
            var titleLbl = new Label { FontAttributes = FontAttributes.Bold, FontFamily = "NotoSansBold", LineBreakMode = LineBreakMode.TailTruncation }; titleLbl.SetBinding(Label.TextProperty, "Title");
            var artistLbl = new Label { FontSize = 12, FontFamily = "NotoSansRegular", LineBreakMode = LineBreakMode.TailTruncation }; artistLbl.SetBinding(Label.TextProperty, "ArtistYearDisplay");
            vsl.Children.Add(titleLbl); vsl.Children.Add(artistLbl); wide.Add(vsl); Grid.SetColumn(vsl,1);
            var instStack = new HorizontalStackLayout { Spacing = 8, VerticalOptions = LayoutOptions.Center }; instStack.SetBinding(BindableLayout.ItemsSourceProperty, "InstrumentStatuses");
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

            var compact = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new ColumnDefinition{ Width = new GridLength(60) }, new ColumnDefinition{ Width = GridLength.Star } }, ColumnSpacing = 8, Padding = 6 };
            var cArt = new Border { StrokeShape = new RoundRectangle { CornerRadius = 4 }, WidthRequest = 56, HeightRequest = 56, BackgroundColor = Color.FromArgb("#444444"), HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Center };
            cArt.Content = new Image { Aspect = Aspect.AspectFill }; cArt.Content.SetBinding(Image.SourceProperty, "AlbumArtPath");
            compact.Add(cArt);
            var cVsl = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
            var cTitle = new Label { FontAttributes = FontAttributes.Bold, FontFamily = "NotoSansBold", LineBreakMode = LineBreakMode.TailTruncation }; cTitle.SetBinding(Label.TextProperty, "Title");
            var cArtist = new Label { FontSize = 12, FontFamily = "NotoSansRegular", LineBreakMode = LineBreakMode.TailTruncation }; cArtist.SetBinding(Label.TextProperty, "ArtistYearDisplay");
            cVsl.Children.Add(cTitle); cVsl.Children.Add(cArtist); compact.Add(cVsl); Grid.SetColumn(cVsl,1);

            wide.SetBinding(VisualElement.IsVisibleProperty, new Binding("UseCompactLayout", converter: new InverseBoolConverter()));
            compact.SetBinding(VisualElement.IsVisibleProperty, "UseCompactLayout");
            root.Add(wide); root.Add(compact);
            return root;
        });

        var collection = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            ItemsSource = rows,
            ItemTemplate = rowTemplate,
            BackgroundColor = Colors.Transparent
        };

        var container = LayoutConstants.CreateCard(
            new VerticalStackLayout { Spacing = 10, Children = { header, collection } });
        return container;
    }

    private void OnSongTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is SongDisplayRow row) NavigateToRow(row);
        else if (sender is VisualElement ve && ve.BindingContext is SongDisplayRow r2) NavigateToRow(r2);
    }

    private void NavigateToRow(SongDisplayRow row)
    {
        try
        {
            var order = new List<string> { "guitar", "drums", "vocals", "bass", "pro_guitar", "pro_bass" };
            var vm = new SongInfoViewModel(row, order, _service);
            var page = new SongInfoPage(vm);
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try { await Shell.Current.Navigation.PushAsync(page); }
                catch (Exception ex) { Debug.WriteLine($"[StatisticsPage] Navigation error: {ex.Message}"); }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StatisticsPage] Error creating song info page: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if an instrument should be shown based on sync settings.
    /// </summary>
    private bool IsInstrumentEnabled(string instrumentKey)
    {
        if (_settings == null) return true;
        
        return instrumentKey switch
        {
            "guitar" => _settings.QueryLead,
            "bass" => _settings.QueryBass,
            "drums" => _settings.QueryDrums,
            "vocals" => _settings.QueryVocals,
            "pro_guitar" => _settings.QueryProLead,
            "pro_bass" => _settings.QueryProBass,
            _ => true
        };
    }

    /// <summary>
    /// Checks if a suggestion category should be shown based on sync settings.
    /// Categories targeting a specific instrument (via Key) are filtered out if that instrument is disabled.
    /// </summary>
    private bool ShouldShowCategory(string key)
    {
        if (_settings == null || string.IsNullOrEmpty(key)) return true;
        
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
