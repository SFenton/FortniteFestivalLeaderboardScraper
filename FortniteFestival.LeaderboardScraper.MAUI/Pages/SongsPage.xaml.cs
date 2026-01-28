using FortniteFestival.Core;
using FortniteFestival.Core.Config;
using FortniteFestival.Core.Persistence;
using FortniteFestival.LeaderboardScraper.MAUI.ViewModels;

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class SongsPage : ContentPage
{
    private readonly SongsViewModel _vm;
    private readonly ProcessViewModel _processVm;
    private readonly ISettingsPersistence _settingsPersistence;
    private string _pendingExchangeCode = string.Empty;
    private bool _updateButtonWidthLocked;
    private double _updateButtonWidth;

    // Filter snapshot
    private bool _snapMissingPadFCs, _snapMissingProFCs, _snapMissingPadScores, _snapMissingProScores;
    private bool _snapIncludeLead, _snapIncludeBass, _snapIncludeDrums, _snapIncludeVocals, _snapIncludeProGuitar, _snapIncludeProBass;
    // Sort snapshot
    private bool _snapSortTitle, _snapSortArtist, _snapSortHasFC;
    private List<string> _snapInstrumentOrder = new();

    public SongsPage(SongsViewModel vm, ProcessViewModel processVm, ISettingsPersistence settingsPersistence)
    {
        InitializeComponent();
        _vm = vm;
        _processVm = processVm;
        _settingsPersistence = settingsPersistence;
        BindingContext = _vm;
        _processVm.PropertyChanged += ProcessVmOnPropertyChanged;
        UpdateScoresButton.SizeChanged += OnUpdateScoresButtonSizeChanged;
        SizeChanged += (_, _) => _vm.AdaptForWidth(Width);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        try { _vm.Service.ScoreUpdated += OnAnyScoreUpdated; } catch { }
        
        // Defer all heavy work to allow the page to render first (shows spinner immediately)
        Dispatcher.Dispatch(async () =>
        {
            await EnsureInitializedWithRetryAsync();
            _vm.AdaptForWidth(Width);
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        try { _vm.Service.ScoreUpdated -= OnAnyScoreUpdated; } catch { }
    }

    private async Task EnsureInitializedWithRetryAsync()
    {
        while (true)
        {
            InitSpinner.IsRunning = true;
            LoadingOverlay.IsVisible = true;
            MainContentGrid.IsVisible = false;
            try
            {
                await _processVm.EnsureInitializedAsync();
                break;
            }
            catch
            {
                bool retry = await DisplayAlert("Error", "An error occurred. Please be sure you are online and have enough disk space.", "Retry", "Cancel");
                if (!retry) { InitSpinner.IsRunning = false; return; }
            }
        }
        
        // Load settings and apply to visible rows to filter instrument icons
        var settings = await _settingsPersistence.LoadSettingsAsync() ?? new Settings();
        _vm.ApplySettingsToVisibleRows(settings);
        
        InitSpinner.IsRunning = false;
        LoadingOverlay.IsVisible = false;
        MainContentGrid.IsVisible = true;
        _vm.Refresh();
    }

    private void ProcessVmOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProcessViewModel.IsFetching))
            MainThread.BeginInvokeOnMainThread(UpdateFloatingButtonState);
    }

    private void UpdateFloatingButtonState()
    {
        if (UpdateScoresButton == null) return;
        bool fetching = _processVm.IsFetching;
        UpdateScoresButton.Opacity = fetching ? 0.7 : 1.0;
        UpdateScoresButton.InputTransparent = fetching;
        if (UpdateScoresLabel != null) UpdateScoresLabel.IsVisible = !fetching;
        if (UpdateScoresSpinner != null)
        {
            UpdateScoresSpinner.IsVisible = fetching;
            UpdateScoresSpinner.IsRunning = fetching;
        }
        if (_updateButtonWidthLocked) UpdateScoresButton.WidthRequest = _updateButtonWidth;
    }

    private void OnAnyScoreUpdated(LeaderboardData _) { }

    private async void OnUpdateScoresTapped(object sender, TappedEventArgs e)
    {
        if (_processVm.IsFetching) return;
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
        if (sender is VisualElement el) await AnimatePressAsync(el);
        _vm.ResetFiltersToDefaults();
        _vm.ApplyAdvancedFilters();
        SnapshotFilters();
    }

    private async void OnApplyFilter(object sender, EventArgs e)
    {
        if (sender is VisualElement el) await AnimatePressAsync(el);
        FilterModal.IsVisible = false;
        _ = RunListOperationAsync(async () => { await Task.Yield(); _vm.ApplyAdvancedFilters(); }, "Updating songs...");
    }

    private async void OnCloseSort(object sender, EventArgs e)
    {
        await AnimatePressAsync(CloseSortButton);
        RestoreSortSnapshot();
        SortModal.IsVisible = false;
        _ = RunListOperationAsync(async () => { await Task.Yield(); _vm.Refresh(); }, "Updating songs...");
    }

    private async void OnResetSort(object sender, EventArgs e)
    {
        if (sender is VisualElement el) await AnimatePressAsync(el);
        _vm.IsSortByTitle = true;
        _vm.IsSortByArtist = false;
        _vm.IsSortByHasFC = false;
        ResetInstrumentOrder();
        SnapshotSort();
    }

    private async void OnApplySort(object sender, EventArgs e)
    {
        if (sender is VisualElement el) await AnimatePressAsync(el);
        SortModal.IsVisible = false;
        _ = RunListOperationAsync(async () => { await Task.Yield(); _vm.Refresh(); }, "Updating songs...");
    }

    private async void OnSortDirectionTapped(object sender, TappedEventArgs e)
    {
        await AnimatePressAsync(SortDirectionButton);
        _vm.ToggleSortDirection();
        if (!SortModal.IsVisible)
            _ = RunListOperationAsync(async () => { await Task.Yield(); _vm.Refresh(); }, "Updating songs...");
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

    private async void OnSortOptionTapped(object sender, TappedEventArgs e)
    {
        if (sender is VisualElement el) await AnimatePressAsync(el);
        var mode = e.Parameter as string;
        if (string.IsNullOrEmpty(mode)) return;
        switch (mode)
        {
            case "title": _vm.IsSortByTitle = true; break;
            case "artist": _vm.IsSortByArtist = true; break;
            case "hasfc": _vm.IsSortByHasFC = true; break;
        }
    }

    private async void OnSongRowTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not SongDisplayRow row) return;
        if (sender is VisualElement ve) await AnimatePressAsync(ve);
        var order = _vm.PrimaryInstrumentOrder.Select(i => i.Key).ToList();
        var vm = new SongInfoViewModel(row, order, _vm.Service);
        await Shell.Current.Navigation.PushAsync(new SongInfoPage(vm));
    }

    private void OnUpdateScoresButtonSizeChanged(object? sender, EventArgs e)
    {
        if (_updateButtonWidthLocked) return;
        if (UpdateScoresButton.Width <= 0 || UpdateScoresLabel == null || !UpdateScoresLabel.IsVisible) return;
        _updateButtonWidth = UpdateScoresButton.Width;
        UpdateScoresButton.WidthRequest = _updateButtonWidth;
        _updateButtonWidthLocked = true;
        UpdateScoresButton.SizeChanged -= OnUpdateScoresButtonSizeChanged;
    }

    private void SnapshotFilters()
    {
        _snapMissingPadFCs = _vm.MissingPadFCs;
        _snapMissingProFCs = _vm.MissingProFCs;
        _snapMissingPadScores = _vm.MissingPadScores;
        _snapMissingProScores = _vm.MissingProScores;
        _snapIncludeLead = _vm.IncludeLead;
        _snapIncludeBass = _vm.IncludeBass;
        _snapIncludeDrums = _vm.IncludeDrums;
        _snapIncludeVocals = _vm.IncludeVocals;
        _snapIncludeProGuitar = _vm.IncludeProGuitar;
        _snapIncludeProBass = _vm.IncludeProBass;
    }

    private void RestoreSnapshot()
    {
        _vm.MissingPadFCs = _snapMissingPadFCs;
        _vm.MissingProFCs = _snapMissingProFCs;
        _vm.MissingPadScores = _snapMissingPadScores;
        _vm.MissingProScores = _snapMissingProScores;
        _vm.IncludeLead = _snapIncludeLead;
        _vm.IncludeBass = _snapIncludeBass;
        _vm.IncludeDrums = _snapIncludeDrums;
        _vm.IncludeVocals = _snapIncludeVocals;
        _vm.IncludeProGuitar = _snapIncludeProGuitar;
        _vm.IncludeProBass = _snapIncludeProBass;
        _vm.ApplyAdvancedFilters();
    }

    private void SnapshotSort()
    {
        _snapSortTitle = _vm.IsSortByTitle;
        _snapSortArtist = _vm.IsSortByArtist;
        _snapSortHasFC = _vm.IsSortByHasFC;
        _snapInstrumentOrder = _vm.PrimaryInstrumentOrder.Select(i => i.Key).ToList();
    }

    private void RestoreSortSnapshot()
    {
        _vm.IsSortByTitle = _snapSortTitle;
        _vm.IsSortByArtist = _snapSortArtist;
        _vm.IsSortByHasFC = _snapSortHasFC;
        var current = _vm.PrimaryInstrumentOrder;
        var keyToItem = current.ToDictionary(i => i.Key);
        current.Clear();
        foreach (var k in _snapInstrumentOrder)
            if (keyToItem.TryGetValue(k, out var it)) current.Add(it);
    }

    private void ResetInstrumentOrder()
    {
        var current = _vm.PrimaryInstrumentOrder;
        var map = current.ToDictionary(i => i.Key);
        string[] defaultOrder = ["guitar", "drums", "vocals", "bass", "pro_guitar", "pro_bass"];
        current.Clear();
        foreach (var k in defaultOrder)
            if (map.TryGetValue(k, out var it)) current.Add(it);
    }

    private async Task RunListOperationAsync(Func<Task> action, string message)
    {
        try
        {
            if (SongListLoadingLabel != null) SongListLoadingLabel.Text = message;
            if (SongListLoadingOverlay != null) SongListLoadingOverlay.IsVisible = true;
            if (SongListLoadingSpinner != null) SongListLoadingSpinner.IsRunning = true;
            await action();
        }
        finally
        {
            if (SongListLoadingSpinner != null) SongListLoadingSpinner.IsRunning = false;
            if (SongListLoadingOverlay != null) SongListLoadingOverlay.IsVisible = false;
        }
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
}
