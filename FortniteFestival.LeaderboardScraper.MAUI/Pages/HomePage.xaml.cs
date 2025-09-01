using FortniteFestival.Core;
using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;
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
    SizeChanged += (_, _) => SongsViewModel.AdaptForWidth(Width);
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
                SongsContent.IsVisible = false;
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
                SongsContent.IsVisible = true;
            }
        }
    }
}
