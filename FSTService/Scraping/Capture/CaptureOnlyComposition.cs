using System.Net;
using FSTService.Scraping.Replay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping.Capture;

internal sealed record CaptureOnlyCompositionOverrides(
    ICaptureAuthenticator? Authenticator = null,
    ICaptureCatalogSource? CatalogSource = null,
    ICapturePageSource? PageSource = null,
    ICaptureStorageProbe? StorageProbe = null,
    TimeProvider? TimeProvider = null,
    ICaptureOnlyRunner? Runner = null);

internal static class CaptureOnlyComposition
{
    internal static IServiceCollection CreateServiceCollection(
        CaptureOnlyExecutionEnvironment environment,
        ScraperOptions options,
        CaptureOnlyCompositionOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(options);
        var needsLiveTransport =
            overrides?.Runner is null &&
            (overrides?.Authenticator is null ||
             overrides.CatalogSource is null ||
             overrides.PageSource is null);
        ValidateOptions(
            environment,
            options,
            needsLiveTransport);

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.None);
        });
        services.AddSingleton(environment);
        services.AddSingleton(options);
        services.AddSingleton<IOptions<ScraperOptions>>(
            Options.Create(options));
        services.AddSingleton(
            overrides?.TimeProvider ??
            TimeProvider.System);

        if (overrides?.Runner is not null)
        {
            services.AddSingleton<ICaptureOnlyRunner>(
                overrides.Runner);
            return services;
        }

        if (overrides?.StorageProbe is not null)
        {
            services.AddSingleton<ICaptureStorageProbe>(
                overrides.StorageProbe);
        }
        else
        {
            services.AddSingleton<
                ICaptureStorageProbe,
                CaptureFileSystemStorageProbe>();
        }

        if (needsLiveTransport)
            AddLiveTransport(
                services,
                environment,
                options);

        if (overrides?.Authenticator is not null)
        {
            services.AddSingleton<ICaptureAuthenticator>(
                overrides.Authenticator);
        }
        else
        {
            services.AddSingleton<
                ICaptureAuthenticator,
                EpicCaptureAuthenticator>();
        }

        if (overrides?.CatalogSource is not null)
        {
            services.AddSingleton<ICaptureCatalogSource>(
                overrides.CatalogSource);
        }
        else
        {
            services.AddSingleton<
                ICaptureCatalogSource,
                EpicCaptureCatalogSource>();
        }

        if (overrides?.PageSource is not null)
        {
            services.AddSingleton<ICapturePageSource>(
                overrides.PageSource);
        }
        else
        {
            services.AddSingleton<
                ICapturePageSource,
                EpicCapturePageSource>();
        }

        services.AddSingleton<CaptureOnlyRunner>();
        services.AddSingleton<ICaptureOnlyRunner>(
            static provider =>
                provider.GetRequiredService<
                    CaptureOnlyRunner>());
        return services;
    }

    private static void AddLiveTransport(
        IServiceCollection services,
        CaptureOnlyExecutionEnvironment environment,
        ScraperOptions options)
    {
        services.AddSingleton<
            IProxyContainerRecycler>(provider =>
                options.ProxyContainerSelfHealEnabled
                    ? new GluetunContainerRecycler(
                        provider.GetRequiredService<
                            ILogger<
                                GluetunContainerRecycler>>())
                    : new DisabledProxyContainerRecycler(
                        provider.GetRequiredService<
                            ILogger<
                                DisabledProxyContainerRecycler>>()));
        services.AddSingleton(provider =>
            new ProxyPool(
                options,
                provider.GetRequiredService<
                    ILogger<ProxyPool>>(),
                provider.GetRequiredService<
                    IProxyContainerRecycler>()));
        services.AddSingleton<CaptureHttpClientSet>();
        services.AddSingleton<EpicTrafficCoordinator>();
        services.AddSingleton<ScrapeProgressTracker>();
        services.AddSingleton(provider =>
            new AdaptiveConcurrencyLimiter(
                initialDop: Math.Clamp(
                    options.InitialDop,
                    1,
                    options.DegreeOfParallelism),
                minDop: Math.Min(
                    Math.Max(1, options.InitialDop),
                    options.DegreeOfParallelism),
                maxDop:
                    options.DegreeOfParallelism,
                provider.GetRequiredService<
                    ILogger<AdaptiveConcurrencyLimiter>>(),
                maxRequestsPerSecond:
                    options.MaxRequestsPerSecond));
        services.AddSingleton(provider =>
        {
            var scraper = new GlobalLeaderboardScraper(
                provider.GetRequiredService<
                    CaptureHttpClientSet>()
                    .Leaderboard,
                provider.GetRequiredService<
                    ScrapeProgressTracker>(),
                provider.GetRequiredService<
                    ILogger<
                        GlobalLeaderboardScraper>>(),
                festivalService: null,
                trafficCoordinator:
                    provider.GetRequiredService<
                        EpicTrafficCoordinator>(),
                proxyHealth:
                    provider.GetRequiredService<
                        ProxyPool>(),
                pathDataStore: null);
            scraper.Executor.CurlFallbackTempDirectory =
                CaptureScratchPath.Validate(
                    options.ProxyCurlTempDirectory,
                    environment.RootPolicy
                        .ApprovedRoot);
            scraper.Executor.CurlResponseMaximumBytes =
                CapturePackageFormat
                    .MaximumResponseRecordBytes;
            scraper.Executor.CurlScratchValidator =
                path =>
                    _ = CaptureScratchPath.Validate(
                        path,
                        environment.RootPolicy
                            .ApprovedRoot);
            return scraper;
        });
    }

    private static void ValidateOptions(
        CaptureOnlyExecutionEnvironment environment,
        ScraperOptions options,
        bool needsLiveTransport)
    {
        if (string.IsNullOrWhiteSpace(
                options.DeviceAuthPath) ||
            options.MaxRequestsPerSecond < 0 ||
            options.DegreeOfParallelism <= 0 ||
            options.PageConcurrency <= 0 ||
            options.MaxPagesPerLeaderboard < 0 ||
            options.BandMaxPagesPerLeaderboard < 0 ||
            options.ValidEntryTarget < 0 ||
            options.BandValidEntryTarget < 0 ||
            options.OverThresholdExtraPages <= 0 ||
            !double.IsFinite(
                options.OverThresholdMultiplier) ||
            options.OverThresholdMultiplier <= 0 ||
            !double.IsFinite(
                options.ValidCutoffMultiplier) ||
            options.ValidCutoffMultiplier <= 0 ||
            options.ProxyUrls is null ||
            options.ControlUrls is null ||
            options.VpnProviders is null ||
            options.ContainerNames is null)
        {
            throw Usage();
        }

        var enabledSolo =
            ScrapeOrchestrator.GetEnabledInstruments(
                options);
        var enabledBands =
            options.EnableBandScraping
                ? BandInstrumentMapping.AllBandTypes
                : [];
        if (enabledSolo.Count + enabledBands.Count == 0)
            throw Usage();

        try
        {
            ProxyPool.ValidateExpectedConfiguration(
                options);
        }
        catch (InvalidOperationException exception)
        {
            throw Usage(exception);
        }

        if (!needsLiveTransport)
            return;
        var scratch = CaptureScratchPath.Validate(
            options.ProxyCurlTempDirectory,
            environment.RootPolicy
                .ApprovedRoot);

        var existing = Directory.Exists(scratch)
            ? scratch
            : Path.GetDirectoryName(scratch);
        if (string.IsNullOrWhiteSpace(existing) ||
            !Directory.Exists(existing))
        {
            throw Usage();
        }
        try
        {
            TierZeroPackagePath
                .EnsureNoSymbolicLinkAncestors(existing);
            if (!string.Equals(
                    ReplayRootAdmission
                        .GetFileSystemDeviceIdentity(
                            existing),
                    ReplayRootAdmission
                        .GetFileSystemDeviceIdentity(
                            environment.RootPolicy
                                .ApprovedRoot),
                    StringComparison.Ordinal))
            {
                throw Usage();
            }
        }
        catch (CaptureOnlyException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            TierZeroPackageException)
        {
            throw Usage(exception);
        }
    }

    private static CaptureOnlyException Usage(
        Exception? innerException = null) =>
        new(
            CaptureOnlyFailureKind.Usage,
            CaptureOnlyExitCode.Usage,
            "Capture configuration is invalid.",
            innerException);
}

internal sealed class CaptureHttpClientSet : IDisposable
{
    public CaptureHttpClientSet(ProxyPool proxyPool)
    {
        Authentication = new HttpClient(
            new CaptureResponseLimitHandler(
                new SocketsHttpHandler
                {
                    AutomaticDecompression =
                        DecompressionMethods.All,
                },
                CapturePackageFormat
                    .MaximumResponseRecordBytes),
            disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        Catalog = new HttpClient(
            new CaptureResponseLimitHandler(
                new SocketsHttpHandler
                {
                    AutomaticDecompression =
                        DecompressionMethods.All,
                },
                CapturePackageFormat
                    .MaximumCatalogBytes),
            disposeHandler: true)
        {
            BaseAddress = new Uri(
                "https://fortnitecontent-website-prod07.ol.epicgames.com"),
            Timeout = TimeSpan.FromSeconds(30),
        };

        HttpMessageHandler leaderboardHandler =
            proxyPool.IsEnabled
                ? new ProxyRoutingHttpMessageHandler(
                    proxyPool)
                : new SocketsHttpHandler
                {
                    MaxConnectionsPerServer = 2048,
                    PooledConnectionIdleTimeout =
                        TimeSpan.FromMinutes(2),
                    PooledConnectionLifetime =
                        TimeSpan.FromMinutes(5),
                    EnableMultipleHttp2Connections = true,
                    AutomaticDecompression =
                        DecompressionMethods.All,
                };
        Leaderboard = new HttpClient(
            new CaptureResponseLimitHandler(
                leaderboardHandler,
                CapturePackageFormat
                    .MaximumResponseRecordBytes),
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    internal HttpClient Authentication { get; }
    internal HttpClient Catalog { get; }
    internal HttpClient Leaderboard { get; }

    public void Dispose()
    {
        Authentication.Dispose();
        Catalog.Dispose();
        Leaderboard.Dispose();
    }
}
