using FortniteFestival.Core;
using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using FortniteFestival.Core.Services;
// NOTE: Legacy manual drag/drop code was removed when switching to Syncfusion SfListView. Any remaining
// references to platform drag types have been eliminated.

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class HomePage : ContentPage
{
    private readonly ProcessViewModel _processVm;
    public SongsViewModel SongsViewModel { get; }
    private readonly HomePageViewModel _vm;
    private string _pendingExchangeCode = string.Empty;
    private bool _updateButtonWidthLocked;
    private double _updateButtonWidth;
    // In-place navigation additions
    private enum Section { Songs, Suggestions, Statistics, Settings }
    private Section _currentSection = Section.Songs;
    private FortniteFestival.Core.Suggestions.SuggestionGenerator? _suggestionGenerator;
    private bool _suggestionsLoading;
    private bool _suggestionsEnd;
    private const int SuggestionsInitialBatch = 10;
    private const int SuggestionsSubsequentBatch = 4;
    // Snapshot of filters when opening modal (for Cancel)
    private bool _snapMissingPadFCs, _snapMissingProFCs, _snapMissingPadScores, _snapMissingProScores;
    private bool _snapIncludeLead, _snapIncludeBass, _snapIncludeDrums, _snapIncludeVocals, _snapIncludeProGuitar, _snapIncludeProBass;
    // Sort snapshot
    private bool _snapSortTitle, _snapSortArtist, _snapSortHasFC;
    private List<string> _snapInstrumentOrder = new();

    public HomePage(ProcessViewModel processVm, SongsViewModel songsVm)
    {
        InitializeComponent();
        _processVm = processVm;
        SongsViewModel = songsVm;
        _vm = new HomePageViewModel(processVm, songsVm);
        BindingContext = _vm;
        _processVm.PropertyChanged += ProcessVmOnPropertyChanged;
    // Capture initial width of Update Scores button once laid out
    UpdateScoresButton.SizeChanged += OnUpdateScoresButtonSizeChanged;
    SizeChanged += (_, _) =>
        {
            SongsViewModel.AdaptForWidth(Width);
            AdaptSuggestionsForWidth();
        };
    UpdateSuggestionsVisibility();
    try { SongsViewModel.Service.ScoreUpdated += OnAnyScoreUpdated; } catch { }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        SongsViewModel.Refresh();
        await EnsureInitializedWithRetryAsync();
    SongsViewModel.AdaptForWidth(Width);
    }

    private async Task EnsureInitializedWithRetryAsync()
    {
        while (true)
        {
            InitSpinner.IsRunning = true;
            LoadingOverlay.IsVisible = true;
            SongListCollection.IsVisible = false;
            try
            {
                await _processVm.EnsureInitializedAsync();
                break; // success
            }
            catch
            {
                bool retry = await DisplayAlert(
                    "Error",
                    "An error occurred. Please be sure you are online and have enough disk space.",
                    "Retry",
                    "Cancel"
                );
                if (!retry)
                {
                    // Stay on loading screen but stop spinner
                    InitSpinner.IsRunning = false;
                    return;
                }
                // else loop and retry
            }
        }
        // success
        InitSpinner.IsRunning = false;
        LoadingOverlay.IsVisible = false;
    SongListCollection.IsVisible = true;
        SongsViewModel.Refresh();
    }

    private void ProcessVmOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProcessViewModel.IsFetching))
            MainThread.BeginInvokeOnMainThread(UpdateFloatingButtonState);
    }

    private void UpdateFloatingButtonState()
    {
        if (UpdateScoresButton == null)
            return; // not yet loaded
        bool fetching = _processVm.IsFetching;
        UpdateScoresButton.Opacity = fetching ? 0.7 : 1.0;
        UpdateScoresButton.InputTransparent = fetching; // disable interaction
        if (UpdateScoresLabel != null)
            UpdateScoresLabel.IsVisible = !fetching;
        if (UpdateScoresSpinner != null)
        {
            UpdateScoresSpinner.IsVisible = fetching;
            UpdateScoresSpinner.IsRunning = fetching;
        }
        // Reapply locked width so layout changes don't affect it
        if (_updateButtonWidthLocked)
            UpdateScoresButton.WidthRequest = _updateButtonWidth;
    }

    private async void OnUpdateScoresTapped(object sender, TappedEventArgs e)
    {
        if (_processVm.IsFetching)
            return;
        await AnimatePressAsync(UpdateScoresButton);
        AuthCodeEntry.Text = string.Empty;
        _pendingExchangeCode = string.Empty;
        AuthActionButton.Text = "Get Code";
        AuthModal.IsVisible = true;
    }

    private async void OnFilterTapped(object sender, TappedEventArgs e)
    {
        await AnimatePressAsync(FilterButton);
        SnapshotFilters();
        FilterModal.IsVisible = true;
    }

    private async void OnSortTapped(object sender, TappedEventArgs e)
    {
        await AnimatePressAsync(SortButton);
        SnapshotSort();
        SortModal.IsVisible = true;
    }

    private async void OnCloseFilter(object sender, EventArgs e)
    {
        await AnimatePressAsync(CloseFilterButton);
        RestoreSnapshot();
        FilterModal.IsVisible = false;
    }
    private async void OnResetFilter(object sender, EventArgs e)
    {
        var element = sender as VisualElement;
        if (element != null) await AnimatePressAsync(element);
        SongsViewModel.ResetFiltersToDefaults();
        SongsViewModel.ApplyAdvancedFilters();
        SnapshotFilters();
    }

    private async void OnApplyFilter(object sender, EventArgs e)
    {
        var element = sender as VisualElement;
        if (element != null) await AnimatePressAsync(element);
        FilterModal.IsVisible = false;
    _ = RunListOperationAsync(async () =>
        {
            await Task.Yield();
            SongsViewModel.ApplyAdvancedFilters();
    }, "Updating songs...", innerOnly:true);
    }

    private async void OnCloseSort(object sender, EventArgs e)
    {
        await AnimatePressAsync(CloseSortButton);
        RestoreSortSnapshot();
        SortModal.IsVisible = false;
    _ = RunListOperationAsync(async () =>
        {
            await Task.Yield();
            SongsViewModel.Refresh();
    }, "Updating songs...", innerOnly:true);
    }

    private async void OnResetSort(object sender, EventArgs e)
    {
        var element = sender as VisualElement;
        if (element != null) await AnimatePressAsync(element);
        SongsViewModel.IsSortByTitle = true;
        SongsViewModel.IsSortByArtist = false;
        SongsViewModel.IsSortByHasFC = false;
        ResetInstrumentOrder();
        SnapshotSort();
    }

    private async void OnApplySort(object sender, EventArgs e)
    {
        var element = sender as VisualElement;
        if (element != null) await AnimatePressAsync(element);
        SortModal.IsVisible = false;
    _ = RunListOperationAsync(async () =>
        {
            await Task.Yield();
            SongsViewModel.Refresh();
    }, "Updating songs...", innerOnly:true);
    }

    // Manual drag-and-drop handlers removed; Syncfusion SfListView now manages reordering.

    private async void OnSortDirectionTapped(object sender, TappedEventArgs e)
    {
        await AnimatePressAsync(SortDirectionButton);
        SongsViewModel.ToggleSortDirection();
        if (!SortModal.IsVisible)
        {
        _ = RunListOperationAsync(async () =>
            {
                await Task.Yield();
                SongsViewModel.Refresh();
        }, "Updating songs...", innerOnly:true);
        }
    }

    private void OnAuthCodeChanged(object sender, TextChangedEventArgs e)
    {
        _pendingExchangeCode = e.NewTextValue ?? string.Empty;
        AuthActionButton.Text = string.IsNullOrWhiteSpace(_pendingExchangeCode) ? "Get Code" : "Authenticate";
    }

    private async void OnAuthActionButtonClicked(object sender, EventArgs e)
    {
        await AnimatePressAsync(AuthActionButton);
        if (string.IsNullOrWhiteSpace(_pendingExchangeCode))
        {
            await Launcher.OpenAsync(new Uri("https://www.epicgames.com/id/api/redirect?clientId=ec684b8c687f479fadea3cb2ad83f5c6&responseType=code"));
            return;
        }
        _processVm.ExchangeCode = _pendingExchangeCode.Trim();
        AuthModal.IsVisible = false;
        await _processVm.StartFetchAsync();
        UpdateFloatingButtonState();
    }

    private async void OnCloseAuth(object sender, EventArgs e)
    {
        await AnimatePressAsync(CloseAuthButton);
        AuthModal.IsVisible = false;
    }

    private static async Task AnimatePressAsync(VisualElement element)
    {
        if (element == null) return;
        try
        {
            uint duration = 60;
            await element.ScaleTo(0.975, duration, Easing.CubicIn);
            await element.ScaleTo(1.0, duration, Easing.CubicOut);
        }
        catch { }
    }

    private void OnUpdateScoresButtonSizeChanged(object? sender, EventArgs e)
    {
        if (_updateButtonWidthLocked)
            return;
        if (UpdateScoresButton.Width <= 0 || UpdateScoresLabel == null || !UpdateScoresLabel.IsVisible)
            return; // wait until label is visible and width measured
        _updateButtonWidth = UpdateScoresButton.Width;
        UpdateScoresButton.WidthRequest = _updateButtonWidth;
        _updateButtonWidthLocked = true;
        UpdateScoresButton.SizeChanged -= OnUpdateScoresButtonSizeChanged;
    }

    private void SnapshotFilters()
    {
        _snapMissingPadFCs = SongsViewModel.MissingPadFCs;
        _snapMissingProFCs = SongsViewModel.MissingProFCs;
        _snapMissingPadScores = SongsViewModel.MissingPadScores;
        _snapMissingProScores = SongsViewModel.MissingProScores;
        _snapIncludeLead = SongsViewModel.IncludeLead;
        _snapIncludeBass = SongsViewModel.IncludeBass;
        _snapIncludeDrums = SongsViewModel.IncludeDrums;
        _snapIncludeVocals = SongsViewModel.IncludeVocals;
        _snapIncludeProGuitar = SongsViewModel.IncludeProGuitar;
        _snapIncludeProBass = SongsViewModel.IncludeProBass;
    }

    private void RestoreSnapshot()
    {
        SongsViewModel.MissingPadFCs = _snapMissingPadFCs;
        SongsViewModel.MissingProFCs = _snapMissingProFCs;
        SongsViewModel.MissingPadScores = _snapMissingPadScores;
        SongsViewModel.MissingProScores = _snapMissingProScores;
        SongsViewModel.IncludeLead = _snapIncludeLead;
        SongsViewModel.IncludeBass = _snapIncludeBass;
        SongsViewModel.IncludeDrums = _snapIncludeDrums;
        SongsViewModel.IncludeVocals = _snapIncludeVocals;
        SongsViewModel.IncludeProGuitar = _snapIncludeProGuitar;
        SongsViewModel.IncludeProBass = _snapIncludeProBass;
        SongsViewModel.ApplyAdvancedFilters();
    }

    private void SnapshotSort()
    {
        _snapSortTitle = SongsViewModel.IsSortByTitle;
        _snapSortArtist = SongsViewModel.IsSortByArtist;
        _snapSortHasFC = SongsViewModel.IsSortByHasFC;
        _snapInstrumentOrder = SongsViewModel.PrimaryInstrumentOrder.Select(i => i.Key).ToList();
    }

    private void RestoreSortSnapshot()
    {
        SongsViewModel.IsSortByTitle = _snapSortTitle;
        SongsViewModel.IsSortByArtist = _snapSortArtist;
        SongsViewModel.IsSortByHasFC = _snapSortHasFC;
        var current = SongsViewModel.PrimaryInstrumentOrder;
        // reorder to snapshot
        var keyToItem = current.ToDictionary(i => i.Key);
        current.Clear();
        foreach (var k in _snapInstrumentOrder)
            if (keyToItem.TryGetValue(k, out var it)) current.Add(it);
    }

    private void ResetInstrumentOrder()
    {
        var current = SongsViewModel.PrimaryInstrumentOrder;
        var map = current.ToDictionary(i => i.Key);
        string[] defaultOrder = new[] { "guitar","drums","vocals","bass","pro_guitar","pro_bass" };
        current.Clear();
        foreach (var k in defaultOrder)
            if (map.TryGetValue(k, out var it)) current.Add(it);
    }

    private void OnSortModeChanged(object sender, CheckedChangedEventArgs e)
    {
    // Defer applying until Apply clicked
    }

    private async void OnSortOptionTapped(object sender, TappedEventArgs e)
    {
        var element = sender as VisualElement;
        if (element != null) await AnimatePressAsync(element);
        string? mode = e.Parameter as string;
        if (string.IsNullOrEmpty(mode)) return;
        switch (mode)
        {
            case "title": SongsViewModel.IsSortByTitle = true; break;
            case "artist": SongsViewModel.IsSortByArtist = true; break;
            case "hasfc": SongsViewModel.IsSortByHasFC = true; break;
        }
    }

    private async void OnSongRowTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not SongDisplayRow row) return;
        if (sender is VisualElement ve)
        {
            // Press scale
            await AnimatePressAsync(ve);
            // Pulse background overlay
            await PulseAsync(ve);
        }
        // Capture current instrument order
        var order = SongsViewModel.PrimaryInstrumentOrder.Select(i => i.Key).ToList();
    var vm = new SongInfoViewModel(row, order, SongsViewModel.Service);
        var page = new SongInfoPage(vm);
        await Navigation.PushAsync(page);
    }

    private void UpdateSuggestionsVisibility()
    {
        try
        {
            // Suggestions only if at least one score present
            var hasScore = false;
            try { hasScore = SongsViewModel.Service.ScoresIndex != null && SongsViewModel.Service.ScoresIndex.Count > 0; } catch { }
            if (SuggestionsNavItem != null)
                SuggestionsNavItem.IsVisible = hasScore;
        }
        catch { }
    }

    private void OnAnyScoreUpdated(LeaderboardData _)
    {
        MainThread.BeginInvokeOnMainThread(UpdateSuggestionsVisibility);
    }

    private async void OnHamburgerTapped(object sender, TappedEventArgs e)
    {
        await AnimatePressAsync(HamburgerButton);
        NavDrawerOverlay.IsVisible = true;
        UpdateSuggestionsVisibility();
    }

    private void OnCloseDrawerTapped(object sender, TappedEventArgs e)
    {
        NavDrawerOverlay.IsVisible = false;
    }

    private async void OnNavSongsTapped(object sender, TappedEventArgs e)
    {
        await AnimatePressAsync((VisualElement)sender);
        NavDrawerOverlay.IsVisible = false;
        SwitchSection(Section.Songs);
    }

    private async void OnNavSettingsTapped(object sender, TappedEventArgs e)
    {
        await AnimatePressAsync((VisualElement)sender);
        NavDrawerOverlay.IsVisible = false;
        SwitchSection(Section.Settings);
    }

    private async void OnNavStatisticsTapped(object sender, TappedEventArgs e)
    {
        await AnimatePressAsync((VisualElement)sender);
        NavDrawerOverlay.IsVisible = false;
        SwitchSection(Section.Statistics);
    }

    private async void OnNavSuggestionsTapped(object sender, TappedEventArgs e)
    {
        await AnimatePressAsync((VisualElement)sender);
        NavDrawerOverlay.IsVisible = false;
        SwitchSection(Section.Suggestions);
    }

    private static async Task PulseAsync(VisualElement element)
    {
        try
        {
            const uint dur = 260;
            var original = element.BackgroundColor;
            var pulse = Color.FromRgba(255,255,255,40); // subtle white overlay
            // If original is default (null), treat as transparent
            element.BackgroundColor = pulse;
            await Task.Delay((int)(dur * 0.55));
            // Fade back
            if (original != null)
                element.BackgroundColor = original;
            else
                element.BackgroundColor = Colors.Transparent;
        }
        catch { }
    }

    private async Task RunListOperationAsync(Func<Task> action, string message, bool innerOnly = false)
    {
        try
        {
            if (innerOnly)
            {
                if (SongListLoadingLabel != null) SongListLoadingLabel.Text = message;
                if (SongListLoadingOverlay != null) SongListLoadingOverlay.IsVisible = true;
                if (SongListLoadingSpinner != null) SongListLoadingSpinner.IsRunning = true;
            }
            else
            {
                LoadingLabel.Text = message;
                LoadingOverlay.IsVisible = true;
                InitSpinner.IsRunning = true;
                SongListCollection.IsVisible = false;
            }
            await action();
        }
        finally
        {
            if (innerOnly)
            {
                if (SongListLoadingSpinner != null) SongListLoadingSpinner.IsRunning = false;
                if (SongListLoadingOverlay != null) SongListLoadingOverlay.IsVisible = false;
            }
            else
            {
                InitSpinner.IsRunning = false;
                LoadingOverlay.IsVisible = false;
                SongListCollection.IsVisible = true;
            }
        }
    }

    private void SwitchSection(Section target)
    {
        if (_currentSection == target) return;
        _currentSection = target;
    if (SongsSection != null) SongsSection.IsVisible = target == Section.Songs;
        SuggestionsSection.IsVisible = target == Section.Suggestions;
        StatisticsSection.IsVisible = target == Section.Statistics;
        SettingsSection.IsVisible = target == Section.Settings;
        if (target == Section.Suggestions)
        {
            EnsureSuggestionGenerator();
            // Always show overlay while we check / (re)load
            if (SuggestionsInitialOverlay != null)
            {
                SuggestionsInitialOverlay.IsVisible = true;
                if (SuggestionsInitialSpinner != null) SuggestionsInitialSpinner.IsRunning = true;
            }
            if (SuggestionsContentStack.Children.Count == 0)
            {
                // Fire and forget async load so UI switches immediately
                MainThread.BeginInvokeOnMainThread(async () => await LoadInitialSuggestionsAsync());
            }
            else
            {
                HideSuggestionsInitialOverlay();
            }
        }
    }

    private void EnsureSuggestionGenerator()
    {
        if (_suggestionGenerator == null && SongsViewModel.Service != null)
            _suggestionGenerator = new FortniteFestival.Core.Suggestions.SuggestionGenerator(SongsViewModel.Service);
    }

    private async Task LoadInitialSuggestionsAsync()
    {
        if (_suggestionsLoading || _suggestionGenerator == null) { HideSuggestionsInitialOverlay(); return; }
        _suggestionsLoading = true;
        try
        {
            await Task.Delay(50); // brief pause so spinner is visible before heavy work
            int remaining = SuggestionsInitialBatch;
            while (remaining > 0)
            {
                // Run generation off UI thread
                var batch = await Task.Run(() => _suggestionGenerator.GetNext(remaining).ToList());
                if (batch.Count == 0) { _suggestionsEnd = true; break; }
                foreach (var cat in batch) SuggestionsContentStack.Children.Add(BuildSuggestionCategoryView(cat));
                remaining -= batch.Count;
                if (batch.Count == 0) break;
                await Task.Yield(); // yield to UI
            }
        }
        finally
        {
            _suggestionsLoading = false;
            SuggestionsEmptyState.IsVisible = SuggestionsContentStack.Children.Count == 0;
            HideSuggestionsInitialOverlay();
        }
    }

    private void HideSuggestionsInitialOverlay()
    {
        if (SuggestionsInitialOverlay != null)
        {
            SuggestionsInitialOverlay.IsVisible = false;
            if (SuggestionsInitialSpinner != null) SuggestionsInitialSpinner.IsRunning = false;
        }
    }

    private void LoadMoreSuggestions()
    {
        if (_suggestionsLoading || _suggestionsEnd || _suggestionGenerator == null) return;
        _suggestionsLoading = true;
        SuggestionsLoadingIndicator.IsVisible = true;
        SuggestionsLoadingIndicator.IsRunning = true;
        try
        {
            int remaining = SuggestionsSubsequentBatch;
            while (remaining > 0)
            {
                var batch = _suggestionGenerator.GetNext(remaining).ToList();
                if (batch.Count == 0) { _suggestionsEnd = true; break; }
                foreach (var cat in batch) SuggestionsContentStack.Children.Add(BuildSuggestionCategoryView(cat));
                remaining -= batch.Count;
                if (batch.Count == 0) break;
            }
            if (_suggestionsEnd)
            {
                _suggestionGenerator.ResetForEndless();
                _suggestionsEnd = false;
            }
        }
        finally
        {
            SuggestionsLoadingIndicator.IsRunning = false;
            SuggestionsLoadingIndicator.IsVisible = false;
            _suggestionsLoading = false;
            SuggestionsEmptyState.IsVisible = SuggestionsContentStack.Children.Count == 0;
        }
    }

    private View BuildSuggestionCategoryView(FortniteFestival.Core.Suggestions.SuggestionCategory cat)
    {
        var svc = SongsViewModel.Service;
        var rows = new System.Collections.ObjectModel.ObservableCollection<SongDisplayRow>();
        foreach (var s in cat.Songs)
        {
            var song = svc.Songs.FirstOrDefault(x => x.track.su == s.SongId);
            if (song != null)
            {
                var row = CreateSuggestionRow(song, svc);
                row.RefreshScore(svc);
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
            tap.Tapped += OnSuggestionSongTappedInline;
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
                circle.SetBinding(Border.StrokeProperty, "CircleStrokeColor");
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
            wide.SetBinding(VisualElement.IsVisibleProperty, new Binding("UseCompactLayout", converter: new InlineInvertConverter()));
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
        var container = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            BackgroundColor = Color.FromArgb("#b35cd6"),
            Padding = new Thickness(14,12),
            Content = new VerticalStackLayout { Spacing = 10, Children = { header, collection } }
        };
        return container;
    }

    private SongDisplayRow CreateSuggestionRow(Song song, IFestivalService svc)
    {
        return new SongDisplayRow(song, svc) { UseCompactLayout = Width < 900 };
    }

    private void AdaptSuggestionsForWidth()
    {
        try
        {
            bool compact = Width < 900;
            // Iterate category containers
            foreach (var child in SuggestionsContentStack.Children)
            {
                if (child is Border b && b.Content is VerticalStackLayout vsl)
                {
                    // Last child is the collection view (header stack + collection)
                    if (vsl.Children.OfType<CollectionView>().FirstOrDefault() is CollectionView cv && cv.ItemsSource is System.Collections.IEnumerable en)
                    {
                        foreach (var item in en)
                        {
                            if (item is SongDisplayRow row)
                                row.UseCompactLayout = compact;
                        }
                    }
                }
            }
        }
        catch { }
    }

    private void OnSuggestionSongTappedInline(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is SongDisplayRow row) NavigateToSongInfo(row);
        else if (sender is VisualElement ve && ve.BindingContext is SongDisplayRow r2) NavigateToSongInfo(r2);
    }

    private void NavigateToSongInfo(SongDisplayRow row)
    {
        try
        {
            var order = SongsViewModel.PrimaryInstrumentOrder.Select(i => i.Key).ToList();
            var vm = new SongInfoViewModel(row, order, SongsViewModel.Service);
            var page = new SongInfoPage(vm);
            MainThread.BeginInvokeOnMainThread(async () => { try { await Navigation.PushAsync(page); } catch { } });
        }
        catch { }
    }

    private void OnSuggestionsScrolled(object sender, ScrolledEventArgs e)
    {
        if (_currentSection != Section.Suggestions) return;
        double remaining = SuggestionsScroll.ContentSize.Height - (e.ScrollY + SuggestionsScroll.Height);
        if (remaining < 300) LoadMoreSuggestions();
    }

    private class InlineInvertConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is bool b ? !b : true;
        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is bool b ? !b : false;
    }
}
