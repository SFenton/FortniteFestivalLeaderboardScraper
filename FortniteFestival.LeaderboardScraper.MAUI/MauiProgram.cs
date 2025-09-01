using System.IO;
using FortniteFestival.Core;
using FortniteFestival.Core.Config;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using Microsoft.Extensions.Logging;
using Syncfusion.Maui.Core.Hosting;
using Syncfusion.Maui.ListView; // for SfListView & handler
#if WINDOWS
using Microsoft.Maui.LifecycleEvents;
using WinBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;
using WinColors = Microsoft.UI.Colors;
#endif

namespace FortniteFestival.LeaderboardScraper.MAUI;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
    // If you have a Syncfusion license key, register it here. (Optional placeholder)
    try { Syncfusion.Licensing.SyncfusionLicenseProvider.RegisterLicense(Environment.GetEnvironmentVariable("SYNCFUSION_LICENSE_KEY") ?? string.Empty); } catch { }
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureSyncfusionCore() // registers all Syncfusion handlers including SfListView
            .ConfigureFonts(f => { })
#if WINDOWS
            .ConfigureLifecycleEvents(events =>
            {
                events.AddWindows(w => w.OnWindowCreated(win =>
                {
                    try
                    {
                        var resources = Microsoft.UI.Xaml.Application.Current.Resources;
                        resources["ScrollBarForeground"] = new WinBrush(WinColors.White);
                        resources["ScrollBarBackground"] = new WinBrush(WinColors.Transparent);
                        // Extend content into title bar (hide default chrome)
                        try { win.ExtendsContentIntoTitleBar = true; } catch { }
                    }
                    catch { }
                }));
            })
#endif
            ;

        // Global exception logging
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            LogUnhandled(
                "AppDomain",
                e.ExceptionObject as Exception ?? new Exception("<null exception object>")
            );
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogUnhandled("TaskScheduler", e.Exception ?? new Exception("<null task exception>"));
            e.SetObserved();
        };

        // Initialize SQLite batteries (avoid native issues on some platforms)
        try
        {
            SQLitePCL.Batteries_V2.Init();
        }
        catch { }

        builder.Services.AddSingleton<IFestivalService>(sp =>
        {
            var dataDir = FileSystem.AppDataDirectory;
            var dbPath = Path.Combine(dataDir, "scores.db");
            WriteStartupLog($"DataDir={dataDir}\nDB={dbPath}");
            return new FestivalService(new SqlitePersistence(dbPath));
        });
        builder.Services.AddSingleton<ISettingsPersistence>(sp =>
        {
            var dataDir = FileSystem.AppDataDirectory;
            var settingsPath = Path.Combine(dataDir, "FNFLS_settings.json");
            return new JsonSettingsPersistence(settingsPath);
        });
        builder.Services.AddSingleton<Settings>();
        builder.Services.AddSingleton<AppState>();

        builder.Services.AddSingleton<ViewModels.ProcessViewModel>();
        builder.Services.AddSingleton<ViewModels.SongsViewModel>();
        builder.Services.AddSingleton<ViewModels.ScoresViewModel>();
    builder.Services.AddSingleton<ViewModels.OptionsViewModel>();

    // Legacy pages (kept temporarily) & new single HomePage
    builder.Services.AddSingleton<Pages.ProcessPage>();
    builder.Services.AddSingleton<Pages.SongsPage>();
    builder.Services.AddSingleton<Pages.ScoresPage>();
    builder.Services.AddSingleton<Pages.LogPage>();
    builder.Services.AddSingleton<Pages.OptionsPage>();
    builder.Services.AddSingleton<Pages.HomePage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

    var app = builder.Build();
    ServiceProviderHelper.ServiceProvider = app.Services;
    return app;
    }

    private static void LogUnhandled(string src, Exception ex)
    {
        try
        {
            var path = Path.Combine(FileSystem.AppDataDirectory, "fatal.log");
            string cid = FortniteFestival.Core.Services.HttpErrorHelper.ComputeCorrelationId(ex);
            File.AppendAllText(path, $"[{DateTime.Now:o}] cid={cid} {src} UNHANDLED: {ex}\n");
        }
        catch { }
    }

    private static void WriteStartupLog(string msg)
    {
        try
        {
            var path = Path.Combine(FileSystem.AppDataDirectory, "startup.log");
            File.AppendAllText(path, $"[{DateTime.Now:o}] {msg}\n");
        }
        catch { }
    }
}

public static class ServiceProviderHelper
{
    public static IServiceProvider? ServiceProvider { get; internal set; }
}
