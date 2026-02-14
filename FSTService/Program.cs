using FSTService;
using FSTService.Auth;
using FSTService.Scraping;

var builder = Host.CreateApplicationBuilder(args);

// Bind configuration
builder.Services.Configure<ScraperOptions>(
    builder.Configuration.GetSection(ScraperOptions.Section));

// Parse CLI arguments and overlay onto options
builder.Services.PostConfigure<ScraperOptions>(opts =>
{
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].Equals("--setup", StringComparison.OrdinalIgnoreCase))
        {
            opts.SetupOnly = true;
        }
        else if (args[i].Equals("--test", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            opts.TestSongQuery = args[++i];
        }
    }
});

var scraperOpts = builder.Configuration
    .GetSection(ScraperOptions.Section)
    .Get<ScraperOptions>() ?? new ScraperOptions();

// HTTP client for Epic auth
builder.Services.AddHttpClient<EpicAuthService>();

// HTTP client for global leaderboard scraper (with gzip/deflate)
builder.Services.AddHttpClient<GlobalLeaderboardScraper>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.GZip
                               | System.Net.DecompressionMethods.Deflate,
    });

// Device auth persistence
builder.Services.AddSingleton<ICredentialStore>(sp =>
{
    var path = Path.GetFullPath(scraperOpts.DeviceAuthPath);
    var log = sp.GetRequiredService<ILogger<FileCredentialStore>>();
    return new FileCredentialStore(path, log);
});

// Auth services
builder.Services.AddSingleton<EpicAuthService>();
builder.Services.AddSingleton<TokenManager>();

// Background worker
builder.Services.AddHostedService<ScraperWorker>();

var host = builder.Build();
host.Run();
