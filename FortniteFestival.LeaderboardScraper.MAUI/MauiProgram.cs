using System.IO;
using FortniteFestival.Core;
using FortniteFestival.Core.Config;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Services;
using Microsoft.Extensions.Logging;

namespace FortniteFestival.LeaderboardScraper.MAUI;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

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

        builder.Services.AddSingleton<Pages.ProcessPage>();
        builder.Services.AddSingleton<Pages.SongsPage>();
        builder.Services.AddSingleton<Pages.ScoresPage>();
        builder.Services.AddSingleton<Pages.LogPage>();
        builder.Services.AddSingleton<Pages.OptionsPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
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
