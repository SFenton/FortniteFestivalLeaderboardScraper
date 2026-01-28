namespace FortniteFestival.LeaderboardScraper.MAUI;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell());

#if WINDOWS
    try
    {
        var displayInfo = DeviceDisplay.Current.MainDisplayInfo;
        double density = displayInfo.Density <= 0 ? 1 : displayInfo.Density;
        double deviceWidthDip = displayInfo.Width / density;
        double deviceHeightDip = displayInfo.Height / density;
            double minWidth = Math.Min(deviceWidthDip, 360);
            double minHeight = Math.Min(deviceHeightDip, 540);
            window.MinimumWidth = minWidth;
            window.MinimumHeight = minHeight;
    }
    catch { /* non-fatal: fallback to default sizing */ }
#endif
    return window;
    }
}
