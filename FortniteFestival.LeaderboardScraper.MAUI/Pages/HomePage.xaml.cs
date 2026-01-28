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
    // Width-lock fields for Update Scores button so width stays stable when swapping label/spinner.
    private bool _updateButtonWidthLocked;
    private double _updateButtonWidth;
    // In-place navigation additions
    private enum Section { Songs, Suggestions, Statistics }
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
    // Statistics ViewModel and staleness tracking
    private StatisticsViewModel? _statsVm;
    private bool _statsStale = true;

    public HomePage(ProcessViewModel processVm, SongsViewModel songsVm)
    {
        InitializeComponent();
        _processVm = processVm;
        SongsViewModel = songsVm;
        _vm = new HomePageViewModel(processVm, songsVm);
        BindingContext = _vm;
        _processVm.PropertyChanged += ProcessVmOnPropertyChanged;
    // Capture initial width of Update Scores button once laid out.
    UpdateScoresButton.SizeChanged += OnUpdateScoresButtonSizeChanged;
    SizeChanged += (_, _) =>
        {
            SongsViewModel.AdaptForWidth(Width);
            AdaptSuggestionsForWidth();
        };
    UpdateSuggestionsVisibility();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try { SongsViewModel.Service.ScoreUpdated += OnAnyScoreUpdated; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[HomePage] Error subscribing to ScoreUpdated: {ex.Message}"); }
        SongsViewModel.Refresh();
        await EnsureInitializedWithRetryAsync();
        SongsViewModel.AdaptForWidth(Width);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        try { SongsViewModel.Service.ScoreUpdated -= OnAnyScoreUpdated; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[HomePage] Error unsubscribing from ScoreUpdated: {ex.Message}"); }
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
            UpdateScoresLabel.IsVisible = !fetching; // hide text while spinner shows
        if (UpdateScoresSpinner != null)
        {
            UpdateScoresSpinner.IsVisible = fetching;
            UpdateScoresSpinner.IsRunning = fetching;
            if (fetching)
            {
                UpdateScoresSpinner.HorizontalOptions = LayoutOptions.Center;
                UpdateScoresSpinner.VerticalOptions = LayoutOptions.Center;
            }
        }
        // Songs section stays visible during fetch - users can navigate, sort, filter
        // Individual rows show spinners via IsUpdating binding
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
            return; // wait until label is visible (opacity 1) and width measured
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
        _statsStale = true; // Mark statistics as needing refresh
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
        // Navigate to the real SettingsPage instead of in-place section
        try
        {
            var service = SongsViewModel.Service;
            if (service == null)
            {
                System.Diagnostics.Debug.WriteLine("[HomePage] Cannot navigate to Settings: Service is null");
                return;
            }
            await Navigation.PushAsync(new SettingsPage(service));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomePage] Error navigating to Settings: {ex}");
        }
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
        else if (target == Section.Statistics)
        {
            if (_statsStale || StatisticsContentStack.Children.Count == 0)
            {
                RebuildStatisticsFromViewModel();
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

    // ===================== Statistics =====================
    private void RebuildStatisticsFromViewModel()
    {
        try
        {
            // Ensure ViewModel exists
            if (_statsVm == null && SongsViewModel.Service != null)
                _statsVm = new StatisticsViewModel(SongsViewModel.Service);

            if (_statsVm == null) return;

            // Clear existing UI
            StatisticsContentStack.Children.Clear();

            // Refresh data
            _statsVm.Refresh();
            _statsStale = false;

            if (!_statsVm.HasData)
            {
                StatisticsContentStack.Children.Add(new Label
                {
                    Text = "No statistics available. Sync your scores first.",
                    FontSize = 16,
                    FontAttributes = FontAttributes.Italic,
                    TextColor = Colors.White,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center
                });
                return;
            }

            // Build per-instrument detailed cards
            foreach (var instStats in _statsVm.InstrumentStats)
            {
                StatisticsContentStack.Children.Add(BuildInstrumentDetailedCard(instStats));
            }

            // Build Top Songs categories
            foreach (var cat in _statsVm.TopSongCategories)
                StatisticsContentStack.Children.Add(BuildSuggestionCategoryView(cat));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomePage] Error building statistics: {ex.Message}");
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

        // Row 0: FCs & Gold Stars
        AddStatCell(statsGrid, "FCs", $"{stats.FcCount} ({stats.FcPercent:F1}%)", 0, row);
        AddStatCell(statsGrid, "Gold Stars", $"{stats.GoldStarCount}", 1, row);
        row++;

        // Row 1: 5-Star & 4-Star
        AddStatCell(statsGrid, "5 Stars", $"{stats.FiveStarCount}", 0, row);
        AddStatCell(statsGrid, "4 Stars", $"{stats.FourStarCount}", 1, row);
        row++;

        // Row 2: Accuracy
        AddStatCell(statsGrid, "Avg Accuracy", $"{stats.AverageAccuracy:F2}%", 0, row);
        AddStatCell(statsGrid, "Best Accuracy", $"{stats.BestAccuracy:F2}%", 1, row);
        row++;

        // Row 3: Perfect scores
        AddStatCell(statsGrid, "Perfect Scores", $"{stats.PerfectScoreCount}", 0, row);
        AddStatCell(statsGrid, "Avg Stars", $"{stats.AverageStars:F2}", 1, row);
        row++;

        // Row 4: Scores
        AddStatCell(statsGrid, "Total Score", FormatScore(stats.TotalScore), 0, row);
        AddStatCell(statsGrid, "Highest Score", FormatScore(stats.HighestScore), 1, row);
        row++;

        // Row 5: Leaderboard rank
        if (stats.BestRank > 0)
            AddStatCell(statsGrid, "Best Rank", $"#{stats.BestRank:N0}", 0, row);
        else
            AddStatCell(statsGrid, "Best Rank", "—", 0, row);

        if (!double.IsNaN(stats.WeightedPercentile))
            AddStatCell(statsGrid, "Weighted Percentile", FormatPercentile(stats.WeightedPercentile), 1, row);
        else
            AddStatCell(statsGrid, "Weighted Percentile", "—", 1, row);
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

        var border = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            BackgroundColor = GetInstrumentColor(stats.InstrumentKey),
            Padding = new Thickness(16, 14),
            Content = container
        };

        return border;
    }

    private static void AddStatCell(Grid grid, string label, string value, int col, int row)
    {
        // Ensure grid has enough rows
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
            (stats.Top1PercentCount, Color.FromArgb("#FFD700")),   // Gold
            (stats.Top5PercentCount, Color.FromArgb("#C0C0C0")),   // Silver
            (stats.Top10PercentCount, Color.FromArgb("#CD7F32")),  // Bronze
            (stats.Top25PercentCount, Color.FromArgb("#4CAF50")),  // Green
            (stats.Top50PercentCount, Color.FromArgb("#2196F3")),  // Blue
            (stats.Below50PercentCount, Color.FromArgb("#9E9E9E")) // Gray
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

        var border = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 4 },
            Stroke = Colors.Transparent,
            BackgroundColor = Colors.Transparent,
            Content = barGrid
        };

        return border;
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
            "guitar" => Color.FromArgb("#b35cd6"),      // Purple (Lead)
            "bass" => Color.FromArgb("#3498db"),        // Blue
            "drums" => Color.FromArgb("#e74c3c"),       // Red
            "vocals" => Color.FromArgb("#27ae60"),      // Green
            "pro_guitar" => Color.FromArgb("#9b59b6"),  // Deep Purple
            "pro_bass" => Color.FromArgb("#2980b9"),    // Deep Blue
            _ => Color.FromArgb("#7f8c8d")              // Gray fallback
        };
    }

    private static string KeyToLabel(string key) => key switch
    {
        "guitar" => "Lead",
        "bass" => "Bass",
        "drums" => "Drums",
        "vocals" => "Vocals",
        "pro_guitar" => "Pro Guitar",
        "pro_bass" => "Pro Bass",
        _ => key
    };
}
