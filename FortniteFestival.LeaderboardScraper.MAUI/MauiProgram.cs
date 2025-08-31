using Microsoft.Extensions.Logging;
using FortniteFestival.Core.Services;
using FortniteFestival.Core.Persistence;
using FortniteFestival.Core.Config;
using FortniteFestival.Core;
using System.IO;

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

		builder.Services.AddSingleton<IFestivalService>(sp =>
		{
			var dataDir = FileSystem.AppDataDirectory; var dbPath = Path.Combine(dataDir, "scores.db");
			return new FestivalService(new SqlitePersistence(dbPath));
		});
		builder.Services.AddSingleton<ISettingsPersistence>(sp =>
		{
			var dataDir = FileSystem.AppDataDirectory; var settingsPath = Path.Combine(dataDir, "FNFLS_settings.json");
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
}
