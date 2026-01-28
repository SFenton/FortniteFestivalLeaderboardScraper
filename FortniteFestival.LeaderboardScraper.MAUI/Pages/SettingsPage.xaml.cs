using System.Diagnostics;
using FortniteFestival.Core;
using FortniteFestival.Core.Config;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;

namespace FortniteFestival.LeaderboardScraper.MAUI.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly IFestivalService _service;
    private readonly Settings _settings;
    private readonly ISettingsPersistence _persistence;
    private bool _isLoadingSettings;

    public SettingsPage(IFestivalService service)
    {
        try
        {
            InitializeComponent();
            _service = service;
            
            // Get settings from DI
            _settings = ServiceProviderHelper.ServiceProvider?.GetService<Settings>() ?? new Settings();
            _persistence = ServiceProviderHelper.ServiceProvider?.GetService<ISettingsPersistence>() 
                           ?? new JsonSettingsPersistence(Path.Combine(FileSystem.AppDataDirectory, "FNFLS_settings.json"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] Constructor error: {ex}");
            throw;
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _service.ScoreUpdated += OnScoreUpdated;
        
        // Show spinner and hide content while loading
        LoadingSpinner.IsVisible = true;
        LoadingSpinner.IsRunning = true;
        MainScroll.IsVisible = false;
        
        // Run loading on background thread
        _ = LoadSettingsAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _service.ScoreUpdated -= OnScoreUpdated;
    }

    private async Task LoadSettingsAsync()
    {
        _isLoadingSettings = true;
        try
        {
            var loaded = await _persistence.LoadSettingsAsync().ConfigureAwait(false);
            if (loaded != null)
            {
                _settings.QueryLead = loaded.QueryLead;
                _settings.QueryBass = loaded.QueryBass;
                _settings.QueryDrums = loaded.QueryDrums;
                _settings.QueryVocals = loaded.QueryVocals;
                _settings.QueryProLead = loaded.QueryProLead;
                _settings.QueryProBass = loaded.QueryProBass;
                _settings.DegreeOfParallelism = loaded.DegreeOfParallelism;
            }

            // Update UI on main thread
            MainThread.BeginInvokeOnMainThread(() =>
            {
                GuitarSwitch.IsToggled = _settings.QueryLead;
                BassSwitch.IsToggled = _settings.QueryBass;
                DrumsSwitch.IsToggled = _settings.QueryDrums;
                VocalsSwitch.IsToggled = _settings.QueryVocals;
                ProGuitarSwitch.IsToggled = _settings.QueryProLead;
                ProBassSwitch.IsToggled = _settings.QueryProBass;
                ParallelismSlider.Value = _settings.DegreeOfParallelism;
                ParallelismValueLabel.Text = _settings.DegreeOfParallelism.ToString();
                
                // Show content, hide spinner
                MainScroll.IsVisible = true;
                LoadingSpinner.IsVisible = false;
                LoadingSpinner.IsRunning = false;
                _isLoadingSettings = false;
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] Error loading settings: {ex.Message}");
            MainThread.BeginInvokeOnMainThread(() =>
            {
                MainScroll.IsVisible = true;
                LoadingSpinner.IsVisible = false;
                LoadingSpinner.IsRunning = false;
                _isLoadingSettings = false;
            });
        }
    }

    private void LoadSettings()
    {
        _ = LoadSettingsAsync();
    }

    private async void SaveSettings()
    {
        if (_isLoadingSettings) return;
        
        try
        {
            await _persistence.SaveSettingsAsync(_settings);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] Error saving settings: {ex.Message}");
        }
    }

    private void OnInstrumentToggled(object? sender, ToggledEventArgs e)
    {
        if (_isLoadingSettings) return;

        _settings.QueryLead = GuitarSwitch.IsToggled;
        _settings.QueryBass = BassSwitch.IsToggled;
        _settings.QueryDrums = DrumsSwitch.IsToggled;
        _settings.QueryVocals = VocalsSwitch.IsToggled;
        _settings.QueryProLead = ProGuitarSwitch.IsToggled;
        _settings.QueryProBass = ProBassSwitch.IsToggled;
        
        SaveSettings();
    }

    private void OnParallelismChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_isLoadingSettings) return;

        int value = (int)Math.Round(e.NewValue);
        _settings.DegreeOfParallelism = value;
        ParallelismValueLabel.Text = value.ToString();
        
        SaveSettings();
    }

    private void OnScoreUpdated(LeaderboardData obj)
    {
        // DrawerLayout handles suggestions visibility automatically
    }
}
