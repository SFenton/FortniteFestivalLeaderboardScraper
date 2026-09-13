using System.Globalization;
using System.Text.Json;
using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FSTService.Auth;
using FSTService.Persistence;
using FSTService.Scraping.Replay;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping.Capture;

internal sealed record CaptureCatalogAcquisition(
    bool ProviderRequestSucceeded,
    bool IsExact,
    bool SafetyMergeApplied,
    bool IsReconstructed,
    int ParseFailureCount,
    string? ProviderContentSha256,
    CaptureCatalogArtifact? Catalog,
    IReadOnlyDictionary<string, SongMaxScores>?
        PaginationMaximumScores = null,
    string? PaginationMaximumScoresSha256 = null);

internal interface ICaptureCatalogSource
{
    Task<CaptureCatalogAcquisition> FetchAsync(
        CancellationToken cancellationToken);
}

internal sealed class EpicCaptureCatalogSource
    : ICaptureCatalogSource
{
    private readonly FestivalService _festivalService;
    private readonly string? _maximumScoresPath;
    private readonly string? _approvedRoot;

    public EpicCaptureCatalogSource(
        CaptureHttpClientSet clients,
        CaptureOnlyExecutionEnvironment environment)
        : this(
            clients.Catalog,
            environment.PaginationMaximumScoresPath,
            environment.RootPolicy.ApprovedRoot)
    {
    }

    internal EpicCaptureCatalogSource(
        HttpClient catalogClient,
        string? maximumScoresPath = null,
        string? approvedRoot = null)
    {
        _festivalService = new FestivalService(
            persistence: null,
            contentClient: catalogClient);
        _maximumScoresPath =
            maximumScoresPath;
        _approvedRoot = approvedRoot;
    }

    public async Task<CaptureCatalogAcquisition> FetchAsync(
        CancellationToken cancellationToken)
    {
        var result = await _festivalService
            .SyncSongsWithResultAsync()
            .WaitAsync(cancellationToken);
        if (!result.ProviderRequestSucceeded ||
            !result.IsExact ||
            result.SafetyMergeApplied ||
            result.DroppedProviderObjectCount != 0)
        {
            return new CaptureCatalogAcquisition(
                result.ProviderRequestSucceeded,
                result.IsExact,
                result.SafetyMergeApplied,
                IsReconstructed: false,
                result.DroppedProviderObjectCount,
                null,
                null);
        }

        try
        {
            var songs = _festivalService.Songs.ToArray();
            var providerSnapshot =
                SongCatalogSnapshotBuilder.Create(songs);
            if (providerSnapshot.SongCount !=
                    result.ProviderSongCount ||
                providerSnapshot.SongCount !=
                    result.CatalogSongCount)
            {
                return new CaptureCatalogAcquisition(
                    ProviderRequestSucceeded: true,
                    IsExact: false,
                    SafetyMergeApplied: false,
                    IsReconstructed: false,
                    ParseFailureCount: 1,
                    ProviderContentSha256: null,
                    Catalog: null);
            }

            var catalog = CaptureCatalogBuilder.Build(
                songs,
                providerSnapshot.ContentHash);
            var maximumScores =
                await CapturePaginationMaximums
                    .LoadAsync(
                        _maximumScoresPath,
                        _approvedRoot ?? "",
                        catalog,
                        providerSnapshot.ContentHash,
                        cancellationToken);
            return new CaptureCatalogAcquisition(
                ProviderRequestSucceeded: true,
                IsExact: true,
                SafetyMergeApplied: false,
                IsReconstructed: false,
                ParseFailureCount: 0,
                providerSnapshot.ContentHash,
                catalog,
                maximumScores?.Scores,
                maximumScores?.ContentSha256);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            ArgumentException or
            JsonException)
        {
            return new CaptureCatalogAcquisition(
                ProviderRequestSucceeded: true,
                IsExact: false,
                SafetyMergeApplied: false,
                IsReconstructed: false,
                ParseFailureCount: 1,
                ProviderContentSha256: null,
                Catalog: null);
        }
    }
}

internal static class CaptureCatalogBuilder
{
    internal static CaptureCatalogArtifact Build(
        IEnumerable<Song> songs,
        string providerContentSha256)
    {
        ArgumentNullException.ThrowIfNull(songs);
        if (!TierZeroCanonicalJson.IsSha256(
                providerContentSha256))
        {
            throw new ArgumentException(
                "Provider catalog content hash is invalid.",
                nameof(providerContentSha256));
        }

        var canonicalSongs = songs
            .Select(static song =>
            {
                if (song is null ||
                    string.IsNullOrWhiteSpace(
                        song.track?.su))
                {
                    throw new InvalidOperationException(
                        "Provider catalog contains an invalid song.");
                }
                return new
                {
                    Song = song,
                    SongId = song.track!.su!,
                };
            })
            .OrderBy(
                static item => item.SongId,
                StringComparer.Ordinal)
            .ToArray();
        if (canonicalSongs.Length == 0)
        {
            throw new InvalidOperationException(
                "Provider catalog cannot be empty.");
        }
        for (var index = 1;
             index < canonicalSongs.Length;
             index++)
        {
            if (string.Equals(
                    canonicalSongs[index - 1].SongId,
                    canonicalSongs[index].SongId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Provider catalog contains duplicate song IDs.");
            }
        }

        var catalogSongs = canonicalSongs
            .Select(static item =>
                new CaptureCatalogSong(
                    item.SongId,
                    BuildSupport(item.Song)))
            .ToArray();
        return CapturePackageContract.ValidateCatalog(
            new CaptureCatalogArtifact(
                CapturePackageFormat.CatalogFormatId,
                CapturePackageFormat.CatalogSchemaVersion,
                CatalogVersionFromProviderHash(
                    providerContentSha256),
                catalogSongs.Length,
                catalogSongs));
    }

    private static IReadOnlyList<CaptureCatalogScopeSupport>
        BuildSupport(Song song) =>
        CapturePackageFormat.SoloInstrumentOrder
            .Select(instrument =>
                new CaptureCatalogScopeSupport(
                    CaptureScopeKind.Solo,
                    instrument,
                    GlobalLeaderboardScraper
                        .TrackSupportsInstrument(
                            song.track,
                            instrument)
                        ? CaptureCatalogSupportStatus
                            .Supported
                        : CaptureCatalogSupportStatus
                            .Unsupported))
            .Concat(
                CapturePackageFormat.BandTypeOrder.Select(
                    static bandType =>
                        new CaptureCatalogScopeSupport(
                            CaptureScopeKind.Band,
                            bandType,
                            CaptureCatalogSupportStatus
                                .Supported)))
            .ToArray();

    internal static long CatalogVersionFromProviderHash(
        string providerContentSha256)
    {
        var value = ulong.Parse(
            providerContentSha256.AsSpan(0, 16),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture) &
            long.MaxValue;
        return value == 0
            ? 1
            : checked((long)value);
    }
}

internal sealed class CaptureAuthentication
{
    internal CaptureAuthentication(
        string accessToken,
        string accountId,
        ScrapeAccessTokenProvider? refreshProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        AccessToken = accessToken;
        AccountId = accountId;
        RefreshProvider = refreshProvider;
    }

    internal string AccessToken { get; }
    internal string AccountId { get; }
    internal ScrapeAccessTokenProvider? RefreshProvider { get; }

    public override string ToString() =>
        "CaptureAuthentication(redacted)";
}

internal interface ICaptureAuthenticator
{
    Task<CaptureAuthentication> AuthenticateAsync(
        CancellationToken cancellationToken);
}

internal sealed class EpicCaptureAuthenticator
    : ICaptureAuthenticator
{
    private readonly CaptureHttpClientSet _clients;
    private readonly ScraperOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public EpicCaptureAuthenticator(
        CaptureHttpClientSet clients,
        IOptions<ScraperOptions> options,
        ILoggerFactory loggerFactory)
    {
        _clients = clients;
        _options = options.Value;
        _loggerFactory = loggerFactory;
    }

    public async Task<CaptureAuthentication> AuthenticateAsync(
        CancellationToken cancellationToken)
    {
        var clientId =
            Environment.GetEnvironmentVariable(
                "EPIC_CLIENT_ID");
        var clientSecret =
            Environment.GetEnvironmentVariable(
                "EPIC_CLIENT_SECRET");
        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            throw AuthenticationFailed();
        }

        try
        {
            var auth = new EpicAuthService(
                _clients.Authentication,
                _loggerFactory
                    .CreateLogger<EpicAuthService>(),
                clientId,
                clientSecret);
            var credentialPath = Path.GetFullPath(
                _options.DeviceAuthPath);
            var store = new FileCredentialStore(
                credentialPath,
                _loggerFactory
                    .CreateLogger<FileCredentialStore>());
            var tokenManager = new TokenManager(
                auth,
                store,
                _loggerFactory.CreateLogger<TokenManager>());
            var accessToken = await tokenManager
                .GetAccessTokenAsync(cancellationToken);
            cancellationToken
                .ThrowIfCancellationRequested();
            var accountId = tokenManager.AccountId;
            if (string.IsNullOrWhiteSpace(accessToken) ||
                string.IsNullOrWhiteSpace(accountId))
            {
                throw AuthenticationFailed();
            }

            var refreshProvider =
                new ScrapeAccessTokenProvider(
                    tokenManager,
                    accessToken,
                    _loggerFactory.CreateLogger(
                        "CaptureAccessToken"));
            return new CaptureAuthentication(
                accessToken,
                accountId,
                refreshProvider);
        }
        catch (OperationCanceledException)
            when (cancellationToken
                .IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw AuthenticationFailed();
        }
        catch (CaptureOnlyException)
        {
            throw;
        }
        catch (Exception)
        {
            throw AuthenticationFailed();
        }
    }

    private static CaptureOnlyException AuthenticationFailed() =>
        new(
            CaptureOnlyFailureKind.AuthenticationFailed,
            CaptureOnlyExitCode.AuthenticationFailed,
            "Capture authentication failed.");
}

internal enum CapturePageAcquisitionStatus
{
    Success,
    EventNotFound,
    Unauthorized,
    ParseFailure,
    RequestFailure,
}

internal sealed record CapturePageAcquisition(
    CapturePageAcquisitionStatus Status,
    int PageIndex,
    int PageSize,
    int ProviderReportedTotalPages,
    long? ProviderReportedTotalEntries,
    IReadOnlyList<JsonElement> Entries,
    long WireRequestCount,
    long ProviderResponseBytes,
    bool IsExact);

internal interface ICapturePageSource
{
    Task<CapturePageAcquisition> FetchAsync(
        CaptureAuthentication authentication,
        CaptureScopeKind scopeKind,
        string songId,
        string leaderboardType,
        int pageIndex,
        CancellationToken cancellationToken);
}

internal interface ICapturePageSourceMetrics
{
    long TotalWireRequestCount { get; }

    Task QuiesceAsync(
        CancellationToken cancellationToken);
}

internal sealed class EpicCapturePageSource
    : ICapturePageSource,
      ICapturePageSourceMetrics
{
    private const int SoloPageSize = 100;
    private const int BandPageSize = 25;
    internal static readonly TimeSpan
        DefaultPageTransportDeadline =
            TimeSpan.FromMinutes(10);

    private readonly GlobalLeaderboardScraper _scraper;
    private readonly AdaptiveConcurrencyLimiter _limiter;
    private readonly TimeSpan _pageTransportDeadline;

    public EpicCapturePageSource(
        GlobalLeaderboardScraper scraper,
        AdaptiveConcurrencyLimiter limiter,
        TimeSpan? pageTransportDeadline = null)
    {
        _scraper = scraper;
        _limiter = limiter;
        _pageTransportDeadline =
            pageTransportDeadline ??
            DefaultPageTransportDeadline;
        if (_pageTransportDeadline <=
            TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageTransportDeadline));
        }
    }

    public long TotalWireRequestCount =>
        _scraper.Executor.TotalHttpSends;

    public Task QuiesceAsync(
        CancellationToken cancellationToken) =>
        _scraper.Executor.QuiesceCdnProbeAsync(
            cancellationToken);

    public async Task<CapturePageAcquisition> FetchAsync(
        CaptureAuthentication authentication,
        CaptureScopeKind scopeKind,
        string songId,
        string leaderboardType,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        using var deadline =
            CancellationTokenSource
                .CreateLinkedTokenSource(
                    cancellationToken);
        deadline.CancelAfter(
            _pageTransportDeadline);
        var pageToken = deadline.Token;
        try
        {
            var measured = await _scraper.Executor
                .MeasureHttpSendsAsync(
                    () => _scraper.Executor
                        .WithCdnResilienceAsync(
                            work: () => scopeKind switch
                            {
                                CaptureScopeKind.Solo =>
                                    FetchSoloAsync(
                                        authentication,
                                        songId,
                                        leaderboardType,
                                        pageIndex,
                                        pageToken),
                                CaptureScopeKind.Band =>
                                    FetchBandAsync(
                                        authentication,
                                        songId,
                                        leaderboardType,
                                        pageIndex,
                                        pageToken),
                                _ => throw new
                                    InvalidOperationException(
                                    "Capture scope kind is unsupported."),
                            },
                            pageToken,
                            acquireSlot: () =>
                                _scraper.AcquireEpicSlotAsync(
                                    _limiter,
                                    pageToken),
                            releaseSlot:
                                _limiter.Release));
            return measured.Result with
            {
                WireRequestCount =
                    Math.Max(
                        1,
                        measured.HttpSends),
            };
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken
                      .IsCancellationRequested &&
                  deadline.IsCancellationRequested)
        {
            await _scraper.Executor
                .QuiesceCdnProbeAsync(
                    CancellationToken.None);
            throw new HttpRequestException(
                "Capture page transport deadline was exceeded.",
                exception);
        }
    }

    private async Task<CapturePageAcquisition> FetchSoloAsync(
        CaptureAuthentication authentication,
        string songId,
        string leaderboardType,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var (page, bodyLength, status) =
            await _scraper.FetchPageAsync(
                songId,
                leaderboardType,
                pageIndex,
                authentication.AccessToken,
                authentication.AccountId,
                _limiter,
                cancellationToken,
                authentication.RefreshProvider,
                captureProjection: true);
        return CreateResult(
            page,
            status,
            pageIndex,
            SoloPageSize,
            bodyLength,
            wireRequestCount: 0);
    }

    private async Task<CapturePageAcquisition> FetchBandAsync(
        CaptureAuthentication authentication,
        string songId,
        string leaderboardType,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var (page, bodyLength, status) =
            await _scraper.FetchBandPageAsync(
                songId,
                leaderboardType,
                pageIndex,
                authentication.AccessToken,
                authentication.AccountId,
                _limiter,
                cancellationToken,
                authentication.RefreshProvider,
                captureProjection: true);
        return CreateResult(
            page,
            status,
            pageIndex,
            BandPageSize,
            bodyLength,
            wireRequestCount: 0);
    }

    private CapturePageAcquisition CreateResult(
        GlobalLeaderboardScraper.ParsedPage? page,
        GlobalLeaderboardScraper.FetchStatus status,
        int pageIndex,
        int pageSize,
        int bodyLength,
        long wireRequestCount) =>
        page is null
            ? Failure(
                status,
                pageIndex,
                pageSize,
                bodyLength,
                wireRequestCount)
            : new CapturePageAcquisition(
                page.IsSyntheticEventNotFound
                    ? CapturePageAcquisitionStatus
                        .EventNotFound
                    : CapturePageAcquisitionStatus.Success,
                page.Page,
                pageSize,
                page.TotalPages,
                page.ProviderReportedTotalEntries,
                page.Entries
                    .Select(CaptureEntryContracts.Project)
                    .ToArray(),
                wireRequestCount,
                Math.Max(0, bodyLength),
                page.IsSyntheticEventNotFound ||
                page.ProviderReportedTotalEntries.HasValue &&
                page.ProviderEntryCount ==
                    page.Entries.Count);

    private CapturePageAcquisition CreateResult(
        GlobalLeaderboardScraper.ParsedBandPage? page,
        GlobalLeaderboardScraper.FetchStatus status,
        int pageIndex,
        int pageSize,
        int bodyLength,
        long wireRequestCount) =>
        page is null
            ? Failure(
                status,
                pageIndex,
                pageSize,
                bodyLength,
                wireRequestCount)
            : new CapturePageAcquisition(
                page.IsSyntheticEventNotFound
                    ? CapturePageAcquisitionStatus
                        .EventNotFound
                    : CapturePageAcquisitionStatus.Success,
                page.Page,
                pageSize,
                page.TotalPages,
                page.ProviderReportedTotalEntries,
                page.Entries
                    .Select(CaptureEntryContracts.Project)
                    .ToArray(),
                wireRequestCount,
                Math.Max(0, bodyLength),
                page.IsSyntheticEventNotFound ||
                page.ProviderReportedTotalEntries.HasValue &&
                page.ProviderEntryCount ==
                    page.Entries.Count);

    private CapturePageAcquisition Failure(
        GlobalLeaderboardScraper.FetchStatus status,
        int pageIndex,
        int pageSize,
        int bodyLength,
        long wireRequestCount) =>
        new(
            status == GlobalLeaderboardScraper.FetchStatus
                    .Unauthorized
                ? CapturePageAcquisitionStatus.Unauthorized
                : status == GlobalLeaderboardScraper.FetchStatus
                    .ParseFailure
                    ? CapturePageAcquisitionStatus.ParseFailure
                    : CapturePageAcquisitionStatus.RequestFailure,
            pageIndex,
            pageSize,
            0,
            null,
            [],
            wireRequestCount,
            Math.Max(0, bodyLength),
            IsExact: false);
}
