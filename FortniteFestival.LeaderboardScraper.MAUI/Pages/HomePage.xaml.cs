using FortniteFestival.Core;
using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;
// NOTE: Legacy manual drag/drop code was removed when switching to Syncfusion SfListView. Any remaining
// references to platform drag types have been eliminated.

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class HomePage : ContentPage
{
    private readonly ProcessViewModel _processVm;
    public SongsViewModel SongsViewModel { get; }
    private string _pendingExchangeCode = string.Empty;
    private bool _updateButtonWidthLocked;
    private double _updateButtonWidth;
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
        BindingContext = this; // expose SongsViewModel via this
    _processVm.PropertyChanged += ProcessVmOnPropertyChanged;
    // Capture initial width of Update Scores button once laid out
    UpdateScoresButton.SizeChanged += OnUpdateScoresButtonSizeChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        SongsViewModel.Refresh();
        await EnsureInitializedWithRetryAsync();
    }

    private async Task EnsureInitializedWithRetryAsync()
    {
        while (true)
        {
            InitSpinner.IsRunning = true;
            LoadingOverlay.IsVisible = true;
            SongsContent.IsVisible = false;
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
    SongsContent.IsVisible = true;
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

    private void OnUpdateScoresTapped(object sender, TappedEventArgs e)
    {
        if (_processVm.IsFetching)
            return;
    _ = AnimatePressAsync(UpdateScoresButton);
        AuthCodeEntry.Text = string.Empty;
        _pendingExchangeCode = string.Empty;
        AuthActionButton.Text = "Get Code";
        AuthModal.IsVisible = true;
    }

    private void OnFilterTapped(object sender, TappedEventArgs e)
    {
        // Placeholder for future filter options modal / logic
        _ = AnimatePressAsync(FilterButton);
        SnapshotFilters();
        FilterModal.IsVisible = true;
    }

    private void OnSortTapped(object sender, TappedEventArgs e)
    {
        _ = AnimatePressAsync(SortButton);
        SnapshotSort();
        SortModal.IsVisible = true;
    }

    private void OnCloseFilter(object sender, EventArgs e)
    {
        _ = AnimatePressAsync(CloseFilterButton);
        // Revert any changes
        RestoreSnapshot();
        FilterModal.IsVisible = false;
    }
    private void OnResetFilter(object sender, EventArgs e)
    {
    _ = AnimatePressAsync((VisualElement)sender);
        // Reset to launch defaults (all include true, all missing false)
        SongsViewModel.ResetFiltersToDefaults();
        SongsViewModel.ApplyAdvancedFilters();
    // Establish this reset state as new baseline for Cancel
    SnapshotFilters();
    }

    private void OnApplyFilter(object sender, EventArgs e)
    {
    _ = AnimatePressAsync((VisualElement)sender);
        SongsViewModel.ApplyAdvancedFilters();
        FilterModal.IsVisible = false;
    }

    private void OnCloseSort(object sender, EventArgs e)
    {
        _ = AnimatePressAsync(CloseSortButton);
        RestoreSortSnapshot();
    SongsViewModel.Refresh();
    SortModal.IsVisible = false;
    }

    private void OnResetSort(object sender, EventArgs e)
    {
    _ = AnimatePressAsync((VisualElement)sender);
        SongsViewModel.IsSortByTitle = true;
        SongsViewModel.IsSortByArtist = false;
        SongsViewModel.IsSortByHasFC = false;
        // default instrument order
        ResetInstrumentOrder();
        SnapshotSort(); // new baseline
    }

    private void OnApplySort(object sender, EventArgs e)
    {
    _ = AnimatePressAsync((VisualElement)sender);
        SongsViewModel.Refresh();
        SortModal.IsVisible = false;
    }

    // Manual drag-and-drop handlers removed; Syncfusion SfListView now manages reordering.

    private void OnSortDirectionTapped(object sender, TappedEventArgs e)
    {
        _ = AnimatePressAsync(SortDirectionButton);
        SongsViewModel.ToggleSortDirection();
        // If sort modal not visible, apply immediately; otherwise defer until Apply
        if (!SortModal.IsVisible)
            SongsViewModel.Refresh();
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

    private void OnCloseAuth(object sender, EventArgs e)
    {
        _ = AnimatePressAsync(CloseAuthButton);
        AuthModal.IsVisible = false;
    }

    private static async Task AnimatePressAsync(VisualElement element)
    {
        if (element == null) return;
        try
        {
            uint duration = 70;
            await element.ScaleTo(0.92, duration, Easing.CubicIn);
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

    private void OnSortOptionTapped(object sender, TappedEventArgs e)
    {
    _ = AnimatePressAsync((VisualElement)sender);
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
        // Capture current instrument order
        var order = SongsViewModel.PrimaryInstrumentOrder.Select(i => i.Key).ToList();
    var vm = new SongInfoViewModel(row, order, SongsViewModel.Service);
        var page = new SongInfoPage(vm);
        await Navigation.PushAsync(page);
    }
}
