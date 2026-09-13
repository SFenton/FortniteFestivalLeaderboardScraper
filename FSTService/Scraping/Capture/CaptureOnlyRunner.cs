using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping.Replay;

namespace FSTService.Scraping.Capture;

internal sealed record CaptureOnlyResult(
    string CaptureId,
    string PackageRootHash,
    int ScopeCount,
    int PageCount,
    long EntryCount,
    long RequestCount,
    long ResponseBytes);

internal interface ICaptureOnlyRunner
{
    Task<CaptureOnlyResult> ExecuteAsync(
        CaptureOnlyCommand command,
        CancellationToken cancellationToken);
}

internal sealed class CaptureOnlyRunner : ICaptureOnlyRunner
{
    private static readonly string NoDatabaseSchemaFingerprint =
        TierZeroCanonicalJson.Sha256Hex(
            Encoding.UTF8.GetBytes(
                "fst.capture-no-database.v1"));
    private static readonly string PendingCatalogHash =
        TierZeroCanonicalJson.Sha256Hex(
            Encoding.UTF8.GetBytes(
                "fst.capture-catalog-pending.v1"));

    private readonly CaptureOnlyExecutionEnvironment _environment;
    private readonly ScraperOptions _options;
    private readonly ICaptureAuthenticator _authenticator;
    private readonly ICaptureCatalogSource _catalogSource;
    private readonly ICapturePageSource _pageSource;
    private readonly ICaptureStorageProbe _storage;
    private readonly TimeProvider _timeProvider;

    public CaptureOnlyRunner(
        CaptureOnlyExecutionEnvironment environment,
        ScraperOptions options,
        ICaptureAuthenticator authenticator,
        ICaptureCatalogSource catalogSource,
        ICapturePageSource pageSource,
        ICaptureStorageProbe storage,
        TimeProvider timeProvider)
    {
        _environment = environment;
        _options = options;
        _authenticator = authenticator;
        _catalogSource = catalogSource;
        _pageSource = pageSource;
        _storage = storage;
        _timeProvider = timeProvider;
    }

    public async Task<CaptureOnlyResult> ExecuteAsync(
        CaptureOnlyCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var admission = new CaptureRootAdmission(
            _environment.RootPolicy,
            _storage);
        var admitted = admission.AdmitOutput(
            command.OutputPath);
        if (!string.IsNullOrWhiteSpace(
                _options.ProxyCurlTempDirectory))
        {
            _ = CaptureScratchPath.Validate(
                _options.ProxyCurlTempDirectory,
                admitted.ApprovedRoot,
                admitted.OutputPackage);
        }
        CaptureResponseShardCapacity.EnsureAdmittedBudgetFits(
            _environment.ResponseShardMaximumBytes,
            _environment.StoragePolicy
                .MaximumPackageBytes);
        long transientScratchBytes;
        try
        {
            transientScratchBytes = checked(
                (long)CapturePageConcurrency() *
                CapturePackageFormat
                    .MaximumResponseRecordBytes);
        }
        catch (OverflowException exception)
        {
            throw new CaptureOnlyException(
                CaptureOnlyFailureKind
                    .AdmissionRejected,
                CaptureOnlyExitCode
                    .AdmissionRejected,
                "Capture storage admission failed.",
                exception);
        }
        await using var captureAdmission =
            await admission.AcquireCaptureAdmissionAsync(
                admitted,
                _environment.StoragePolicy,
                cancellationToken,
                transientScratchBytes);

        var startedAt = _timeProvider.GetUtcNow();
        var configuration = CreateConfigurationFingerprint();
        CapturePackageWriter? writer = null;
        var stage = CaptureStage.Authentication;
        try
        {
            var authentication =
                await AuthenticateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            stage = CaptureStage.Catalog;
            var selectedCatalog =
                ValidateCatalogAcquisition(
                    await _catalogSource.FetchAsync(
                        cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            configuration =
                CreateConfigurationFingerprint(
                    selectedCatalog
                        .PaginationMaximumScores);
            var catalog = selectedCatalog.Catalog!;
            var enabledSolo =
                ScrapeOrchestrator.GetEnabledInstruments(
                    _options);
            IReadOnlyList<string> enabledBands =
                _options.EnableBandScraping
                    ? BandInstrumentMapping.AllBandTypes
                        .ToArray()
                    : [];
            int expectedScopeCount;
            try
            {
                expectedScopeCount = checked(
                    catalog.SongCount *
                    (enabledSolo.Count +
                     enabledBands.Count));
            }
            catch (OverflowException)
            {
                throw CatalogRejected();
            }
            if (expectedScopeCount <= 0 ||
                expectedScopeCount >
                    CapturePackageFormat
                        .MaximumScopeRecords)
            {
                throw CatalogRejected();
            }
            var catalogBytes =
                CapturePackageContract.SerializeCatalog(
                    catalog);
            long responseArtifactBudget;
            try
            {
                responseArtifactBudget = checked(
                    _environment.StoragePolicy
                        .MaximumPackageBytes -
                    catalogBytes.LongLength -
                    CaptureRootAdmission
                        .PreMetadataFinalPackageAllowanceBytes);
            }
            catch (OverflowException)
            {
                throw AdmissionRejected();
            }
            if (responseArtifactBudget <= 0)
                throw AdmissionRejected();

            stage = CaptureStage.PackageWriting;
            writer = await CapturePackageWriter.CreateAsync(
                admitted.OutputPackage,
                CreateDraft(
                    command.CaptureId,
                    selectedCatalog,
                    catalogBytes,
                    configuration,
                    startedAt),
                cancellationToken);
            await writer.AddArtifactAsync(
                new TierZeroArtifactRegistration(
                    CapturePackageFormat.CatalogOwner,
                    CapturePackageFormat.CatalogPath,
                    CapturePackageFormat.JsonMediaType,
                    CapturePackageFormat
                        .CatalogSchemaVersion,
                    catalog.SongCount,
                    catalogBytes.LongLength),
                catalogBytes,
                cancellationToken);

            stage = CaptureStage.Capturing;
            var requests =
                new List<CaptureRequestDescriptor>();
            var scopes =
                new List<CaptureScopeDescriptor>();
            using var shardWriter =
                new CaptureResponseShardWriter(
                    writer,
                    _environment
                        .ResponseShardMaximumBytes,
                    responseArtifactBudget);

            foreach (var song in
                     catalog.Songs)
            {
                foreach (var instrument in enabledSolo)
                {
                    await CaptureScopeAsync(
                        authentication,
                        song,
                        CaptureScopeKind.Solo,
                        instrument,
                        requests,
                        scopes,
                        shardWriter,
                        selectedCatalog
                            .PaginationMaximumScores,
                        cancellationToken);
                }
                foreach (var bandType in enabledBands)
                {
                    await CaptureScopeAsync(
                        authentication,
                        song,
                        CaptureScopeKind.Band,
                        bandType,
                        requests,
                        scopes,
                        shardWriter,
                        selectedCatalog
                            .PaginationMaximumScores,
                        cancellationToken);
                }
            }
            await shardWriter.CompleteAsync(
                cancellationToken);

            stage = CaptureStage.CatalogVerification;
            var finalCatalog =
                ValidateCatalogAcquisition(
                    await _catalogSource.FetchAsync(
                        cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(
                    selectedCatalog
                        .ProviderContentSha256,
                    finalCatalog.ProviderContentSha256,
                    StringComparison.Ordinal) ||
                !CapturePackageContract.CanonicalEquals(
                    catalog,
                    finalCatalog.Catalog!) ||
                !MaximumScoresEqual(
                    selectedCatalog
                        .PaginationMaximumScores,
                    finalCatalog
                        .PaginationMaximumScores) ||
                !string.Equals(
                    selectedCatalog
                        .PaginationMaximumScoresSha256,
                    finalCatalog
                        .PaginationMaximumScoresSha256,
                    StringComparison.Ordinal))
            {
                throw CatalogRejected();
            }

            stage = CaptureStage.Capturing;
            var completedAt =
                _timeProvider.GetUtcNow();
            var definition =
                CapturePackageContract.ValidateDefinition(
                    new CapturePackageDefinition(
                        command.CaptureId,
                        catalog,
                        enabledSolo,
                        enabledBands,
                        startedAt,
                        completedAt,
                        scopes.Count,
                        requests.Count,
                        requests.Sum(static request =>
                            request.CapturedEntryCount),
                        requests.Sum(static request =>
                            request.CapturedRequestCount),
                        requests.Sum(static request =>
                            request.CapturedResponseBytes),
                        shardWriter.ShardCount,
                        requests,
                        scopes,
                        CapturePackageStatus.Complete));
            var requestMeasurement =
                CapturePackageContract
                    .MeasureRequestPlan(definition);
            var scopeMeasurement =
                CapturePackageContract
                    .MeasureScopeCompleteness(
                        definition);
            captureAdmission.RecheckBeforeMetadata(
                checked(
                    requestMeasurement.Bytes +
                    scopeMeasurement.Bytes),
                cancellationToken);

            stage = CaptureStage.Sealing;
            var sealedPackage = await writer.SealAsync(
                definition,
                _timeProvider.GetUtcNow(),
                innerCancellationToken =>
                {
                    captureAdmission.RecheckBeforeSeal(
                        innerCancellationToken);
                    return Task.CompletedTask;
                },
                cancellationToken);
            var rootHash =
                sealedPackage.Envelope.PackageRootHash;
            if (string.IsNullOrWhiteSpace(rootHash))
            {
                throw new CaptureOnlyException(
                    CaptureOnlyFailureKind.SealFailed,
                    CaptureOnlyExitCode.SealFailed,
                    "Capture package sealing failed.");
            }

            return new CaptureOnlyResult(
                command.CaptureId,
                rootHash,
                definition.TotalScopeCount,
                definition.TotalPageCount,
                definition.TotalEntryCount,
                definition.TotalRequestCount,
                definition.TotalResponseBytes);
        }
        catch (OperationCanceledException)
            when (cancellationToken
                .IsCancellationRequested)
        {
            await TryMarkInterruptedAsync(
                writer,
                admitted.OutputPackage,
                command.CaptureId,
                configuration,
                startedAt,
                "capture-cancelled");
            throw;
        }
        catch (OperationCanceledException)
        {
            var mapped = MapFailure(stage);
            await TryMarkInterruptedAsync(
                writer,
                admitted.OutputPackage,
                command.CaptureId,
                configuration,
                startedAt,
                FailureMarker(mapped.Kind));
            throw mapped;
        }
        catch (CaptureOnlyException exception)
        {
            await TryMarkInterruptedAsync(
                writer,
                admitted.OutputPackage,
                command.CaptureId,
                configuration,
                startedAt,
                FailureMarker(exception.Kind));
            throw;
        }
        catch (Exception)
        {
            var mapped = MapFailure(stage);
            await TryMarkInterruptedAsync(
                writer,
                admitted.OutputPackage,
                command.CaptureId,
                configuration,
                startedAt,
                FailureMarker(mapped.Kind));
            throw mapped;
        }
        finally
        {
            if (_pageSource is
                ICapturePageSourceMetrics metrics)
            {
                await metrics.QuiesceAsync(
                    CancellationToken.None);
            }
        }
    }

    private async Task<CaptureAuthentication>
        AuthenticateAsync(
            CancellationToken cancellationToken)
    {
        try
        {
            return await _authenticator.AuthenticateAsync(
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken
                .IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new CaptureOnlyException(
                CaptureOnlyFailureKind.AuthenticationFailed,
                CaptureOnlyExitCode.AuthenticationFailed,
                "Capture authentication failed.");
        }
        catch (CaptureOnlyException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new CaptureOnlyException(
                CaptureOnlyFailureKind.AuthenticationFailed,
                CaptureOnlyExitCode.AuthenticationFailed,
                "Capture authentication failed.");
        }
    }

    private async Task CaptureScopeAsync(
        CaptureAuthentication authentication,
        CaptureCatalogSong song,
        CaptureScopeKind scopeKind,
        string leaderboardType,
        List<CaptureRequestDescriptor> requests,
        List<CaptureScopeDescriptor> scopes,
        CaptureResponseShardWriter shardWriter,
        IReadOnlyDictionary<string, SongMaxScores>?
            maximumScores,
        CancellationToken cancellationToken)
    {
        var scopeOrdinal = scopes.Count;
        var support = song.ScopeSupport.Single(
            item =>
                item.ScopeKind == scopeKind &&
                string.Equals(
                    item.LeaderboardType,
                    leaderboardType,
                    StringComparison.Ordinal));
        if (support.Status ==
            CaptureCatalogSupportStatus.Unsupported)
        {
            scopes.Add(
                new CaptureScopeDescriptor(
                    scopeOrdinal,
                    song.SongId,
                    scopeKind,
                    leaderboardType,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    CaptureScopeStatus.Unsupported,
                    CapturePackageContract
                        .ComputeScopeContentSha256(
                            Array.Empty<
                                CaptureRequestDescriptor>()),
                    CaptureScopeCompletionReason
                        .Unsupported));
            return;
        }

        if (requests.Count >=
            CapturePackageFormat.MaximumRequestRecords)
        {
            throw CaptureFailed();
        }
        var wireRequestStart =
            (_pageSource as
                ICapturePageSourceMetrics)?
            .TotalWireRequestCount;
        var first = await FetchPageAsync(
            authentication,
            scopeKind,
            song.SongId,
            leaderboardType,
            0,
            cancellationToken);
        ValidatePage(first, 0, expectedTotalPages: null);
        var pages = new List<CapturePageAcquisition>
        {
            first,
        };
        var totalPages = first.ProviderReportedTotalPages;
        var reportedEntries =
            first.ProviderReportedTotalEntries!.Value;
        if (reportedEntries >
                CapturePackageFormat
                    .MaximumScopeEntries ||
            totalPages >
                CapturePackageFormat
                    .MaximumRequestRecords -
                requests.Count)
        {
            throw CaptureFailed();
        }
        CaptureScopeCompletionReason completionReason;
        if (first.Status ==
            CapturePageAcquisitionStatus.EventNotFound)
        {
            completionReason =
                CaptureScopeCompletionReason.EventNotFound;
        }
        else if (scopeKind == CaptureScopeKind.Solo)
        {
            completionReason =
                await CaptureSoloPagesAsync(
                    authentication,
                    song.SongId,
                    leaderboardType,
                    totalPages,
                    pages,
                    maximumScores,
                    cancellationToken);
        }
        else
        {
            completionReason =
                await CaptureBandPagesAsync(
                    authentication,
                    song.SongId,
                    leaderboardType,
                    totalPages,
                    pages,
                    cancellationToken);
        }
        await ReconcileScopeWireRequestsAsync(
            pages,
            wireRequestStart,
            cancellationToken);

        var reportedEntryCounts = pages
            .Where(static page =>
                page.ProviderReportedTotalEntries
                    .HasValue)
            .Select(static page =>
                page.ProviderReportedTotalEntries!
                    .Value)
            .Distinct()
            .ToArray();
        if (reportedEntryCounts.Length != 1 ||
            reportedEntryCounts[0] !=
                reportedEntries ||
            requests.Count + pages.Count >
                CapturePackageFormat.MaximumRequestRecords)
        {
            throw CaptureFailed();
        }

        var requestStart = requests.Count;
        var responses = pages
            .Select((page, index) =>
                new CaptureResponseArtifact(
                    CapturePackageFormat
                        .ResponseFormatId,
                    CapturePackageFormat
                        .ResponseSchemaVersion,
                    checked(requestStart + index),
                    scopeOrdinal,
                    index,
                    scopeKind ==
                        CaptureScopeKind.Solo
                        ? CaptureResponseKind
                            .SoloLeaderboardPage
                        : CaptureResponseKind
                            .BandLeaderboardPage,
                    song.SongId,
                    scopeKind,
                    leaderboardType,
                    page.PageIndex,
                    page.PageSize,
                    totalPages,
                    reportedEntries,
                    page.Entries.Count,
                    page.Entries,
                    page.Status ==
                        CapturePageAcquisitionStatus
                            .EventNotFound
                        ? CaptureResponseOrigin
                            .EventNotFound
                        : CaptureResponseOrigin
                            .ProviderHttpSuccess))
            .ToArray();
        var provisionalScope =
            new CaptureScopeDescriptor(
                scopeOrdinal,
                song.SongId,
                scopeKind,
                leaderboardType,
                responses.Length,
                responses.Sum(static response =>
                    checked((long)response.EntryCount)),
                totalPages,
                reportedEntries,
                responses.Length,
                responses.Sum(static response =>
                    checked((long)response.EntryCount)),
                pages.Sum(static page =>
                    page.WireRequestCount),
                1,
                CaptureScopeStatus.Complete,
                new string('0', 64),
                completionReason);
        var capturedEntries =
            CapturePackageContract
                .ValidateScopeResponseEntries(
                    provisionalScope,
                    responses);

        foreach (var response in responses)
        {
            var location = await shardWriter.AppendAsync(
                response,
                cancellationToken);
            var page = pages[
                response.ScopeRequestOrdinal];
            requests.Add(
                new CaptureRequestDescriptor(
                    response.RequestOrdinal,
                    scopeOrdinal,
                    response.ScopeRequestOrdinal,
                    song.SongId,
                    scopeKind,
                    leaderboardType,
                    response.PageIndex,
                    response.PageSize,
                    totalPages,
                    reportedEntries,
                    1,
                    response.EntryCount,
                    page.WireRequestCount,
                    location.Length,
                    CaptureRequestStatus.Complete,
                    location.Path,
                    location.Offset,
                    location.Length,
                    location.Sha256));
        }

        var requestCount =
            requests.Count - requestStart;
        scopes.Add(
            new CaptureScopeDescriptor(
                scopeOrdinal,
                song.SongId,
                scopeKind,
                leaderboardType,
                pages.Count,
                capturedEntries,
                totalPages,
                reportedEntries,
                requestCount,
                capturedEntries,
                requests
                    .Skip(requestStart)
                    .Take(requestCount)
                    .Sum(static request =>
                        request.CapturedRequestCount),
                requests
                    .Skip(requestStart)
                    .Take(requestCount)
                    .Sum(static request =>
                        request.CapturedResponseBytes),
                CaptureScopeStatus.Complete,
                CapturePackageContract
                    .ComputeScopeContentSha256(
                        requests,
                        requestStart,
                        requestCount),
                completionReason));
    }

    private async Task<CaptureScopeCompletionReason>
        CaptureSoloPagesAsync(
            CaptureAuthentication authentication,
            string songId,
            string leaderboardType,
            int providerReportedTotalPages,
            List<CapturePageAcquisition> pages,
            IReadOnlyDictionary<string, SongMaxScores>?
                maximumScores,
            CancellationToken cancellationToken)
    {
        if (providerReportedTotalPages == 0)
        {
            return CaptureScopeCompletionReason
                .ProviderExhausted;
        }

        var initialPageCount =
            LeaderboardPaginationPlanner.InitialPageCount(
                providerReportedTotalPages,
                _options.MaxPagesPerLeaderboard);
        SongMaxScores? songMaximums = null;
        var hasMaximumScoreSnapshot =
            maximumScores?.TryGetValue(
                songId,
                out songMaximums) == true;
        if (initialPageCount <
                providerReportedTotalPages &&
            !_options.SequentialScrape &&
            !hasMaximumScoreSnapshot)
        {
            throw CaptureFailed();
        }
        await AddPageRangeAsync(
            authentication,
            CaptureScopeKind.Solo,
            songId,
            leaderboardType,
            pages,
            1,
            initialPageCount,
            cancellationToken);

        if (_options.SequentialScrape)
        {
            return pages.Count ==
                    providerReportedTotalPages
                ? CaptureScopeCompletionReason
                    .ProviderExhausted
                : CaptureScopeCompletionReason
                    .ConfiguredPageLimit;
        }

        var maximumScore =
            songMaximums?.GetByInstrument(
                leaderboardType);
        if (!LeaderboardPaginationPlanner
                .TryGetSoloThresholds(
                    maximumScore,
                    _options.OverThresholdMultiplier,
                    _options.ValidCutoffMultiplier,
                    out var triggerThreshold,
                    out var validCutoff) ||
            !LeaderboardPaginationPlanner
                .ShouldDeepScrapeSolo(
                    ReadSoloEntries(pages[0])
                        .Max(static entry =>
                            entry.Score),
                    triggerThreshold) ||
            pages.Count >=
                providerReportedTotalPages)
        {
            return pages.Count ==
                    providerReportedTotalPages
                ? CaptureScopeCompletionReason
                    .ProviderExhausted
                : CaptureScopeCompletionReason
                    .ConfiguredPageLimit;
        }

        if (_options.ValidEntryTarget > 0)
        {
            var validCount = pages.Sum(page =>
                ReadSoloEntries(page).Count(entry =>
                    entry.Score <= validCutoff));
            var nextPage = pages.Count;
            if (!LeaderboardPaginationPlanner
                .NeedsTargetDrivenSoloExtension(
                    deepScrapeTriggered: true,
                    nextPage,
                    providerReportedTotalPages,
                    validCount,
                    _options.ValidEntryTarget))
            {
                return pages.Count ==
                        providerReportedTotalPages
                    ? CaptureScopeCompletionReason
                        .ProviderExhausted
                    : CaptureScopeCompletionReason
                        .ValidEntryTargetReached;
            }

            var job = new DeepScrapeCoordinator
                .DeepScrapeJob
            {
                SongId = songId,
                Instrument = leaderboardType,
                ValidCutoff = validCutoff,
                ValidEntryTarget =
                    _options.ValidEntryTarget,
                ReportedPages =
                    providerReportedTotalPages,
                Wave2Start = nextPage,
                ValidCount = validCount,
            };
            var capturedPages =
                new ConcurrentDictionary<
                    int,
                    CapturePageAcquisition>();
            _ = await DeepScrapeCoordinator
                .RunCaptureAsync(
                    [job],
                    _options.OverThresholdExtraPages,
                    async (_, pageIndex, token) =>
                    {
                        var page = await FetchPageAsync(
                            authentication,
                            CaptureScopeKind.Solo,
                            songId,
                            leaderboardType,
                            pageIndex,
                            token);
                        ValidatePage(
                            page,
                            pageIndex,
                            providerReportedTotalPages);
                        capturedPages[pageIndex] = page;
                        return new DeepScrapeCoordinator
                            .DeepScrapeFetchedPage(
                            new GlobalLeaderboardScraper
                                .ParsedPage
                            {
                                Page = page.PageIndex,
                                TotalPages =
                                    page.ProviderReportedTotalPages,
                                ProviderReportedTotalEntries =
                                    page.ProviderReportedTotalEntries,
                                ProviderEntryCount =
                                    page.Entries.Count,
                                Entries = ReadSoloEntries(page)
                                    .Select(ToLeaderboardEntry)
                                    .ToList(),
                            },
                            checked((int)Math.Min(
                                int.MaxValue,
                                page.ProviderResponseBytes)),
                            GlobalLeaderboardScraper
                                .FetchStatus.Success);
                    },
                    cancellationToken);

            if (job.CursorPage < nextPage)
                throw CaptureFailed();
            var committedPages =
                Enumerable.Range(
                        nextPage,
                        checked(
                            job.CursorPage -
                            nextPage + 1))
                    .Select(pageIndex =>
                    {
                        if (!capturedPages.TryGetValue(
                                pageIndex,
                                out var page))
                        {
                            throw CaptureFailed();
                        }
                        return page;
                    })
                    .ToArray();
            var extraPages = capturedPages
                .Where(pair =>
                    pair.Key > job.CursorPage)
                .Select(static pair => pair.Value)
                .ToArray();
            if (extraPages.Length > 0)
            {
                var last = committedPages[^1];
                try
                {
                    committedPages[^1] = last with
                    {
                        WireRequestCount = checked(
                            last.WireRequestCount +
                            extraPages.Sum(static page =>
                                page.WireRequestCount)),
                        ProviderResponseBytes = checked(
                            last.ProviderResponseBytes +
                            extraPages.Sum(static page =>
                                page.ProviderResponseBytes)),
                    };
                }
                catch (OverflowException)
                {
                    throw CaptureFailed();
                }
            }
            pages.AddRange(committedPages);
            if (pages.Select(static page => page.PageIndex)
                .SequenceEqual(
                    Enumerable.Range(0, pages.Count)))
            {
                return pages.Count ==
                        providerReportedTotalPages
                    ? CaptureScopeCompletionReason
                        .ProviderExhausted
                    : string.Equals(
                        job.CompletionReason,
                        "target_met",
                        StringComparison.Ordinal)
                        ? CaptureScopeCompletionReason
                            .ValidEntryTargetReached
                        : throw CaptureFailed();
            }
            throw CaptureFailed();
        }

        var lastOverThresholdPage = pages
            .Where(page => ReadSoloEntries(page)
                .Any(entry =>
                    entry.Score > validCutoff))
            .Select(static page => page.PageIndex)
            .DefaultIfEmpty(0)
            .Max();
        var extensionEnd =
            LeaderboardPaginationPlanner
                .LegacySoloExtensionEnd(
                    pages.Count,
                    providerReportedTotalPages,
                    lastOverThresholdPage,
                    _options.OverThresholdExtraPages);
        await AddPageRangeAsync(
            authentication,
            CaptureScopeKind.Solo,
            songId,
            leaderboardType,
            pages,
            pages.Count,
            extensionEnd,
            cancellationToken);
        return pages.Count ==
                providerReportedTotalPages
            ? CaptureScopeCompletionReason
                .ProviderExhausted
            : CaptureScopeCompletionReason
                .ConfiguredPageLimit;
    }

    private async Task<CaptureScopeCompletionReason>
        CaptureBandPagesAsync(
            CaptureAuthentication authentication,
            string songId,
            string leaderboardType,
            int providerReportedTotalPages,
            List<CapturePageAcquisition> pages,
            CancellationToken cancellationToken)
    {
        if (providerReportedTotalPages == 0)
        {
            return CaptureScopeCompletionReason
                .ProviderExhausted;
        }

        var pageCount =
            LeaderboardPaginationPlanner.InitialPageCount(
                providerReportedTotalPages,
                _options.MaxPagesPerLeaderboard);
        await AddPageRangeAsync(
            authentication,
            CaptureScopeKind.Band,
            songId,
            leaderboardType,
            pages,
            1,
            pageCount,
            cancellationToken);

        if (pages.Count ==
            providerReportedTotalPages)
        {
            return CaptureScopeCompletionReason
                .ProviderExhausted;
        }
        return CaptureScopeCompletionReason
            .ConfiguredPageLimit;
    }

    private async Task AddPageRangeAsync(
        CaptureAuthentication authentication,
        CaptureScopeKind scopeKind,
        string songId,
        string leaderboardType,
        List<CapturePageAcquisition> pages,
        int startPage,
        int endPageExclusive,
        CancellationToken cancellationToken)
    {
        if (endPageExclusive <= startPage)
            return;
        var count = checked(
            endPageExclusive - startPage);
        var fetched =
            new CapturePageAcquisition[count];
        await Parallel.ForEachAsync(
            Enumerable.Range(startPage, count),
            new ParallelOptions
            {
                CancellationToken =
                    cancellationToken,
                MaxDegreeOfParallelism =
                    CapturePageConcurrency(),
            },
            async (pageIndex, innerCancellationToken) =>
            {
                var page = await FetchPageAsync(
                    authentication,
                    scopeKind,
                    songId,
                    leaderboardType,
                    pageIndex,
                    innerCancellationToken);
                ValidatePage(
                    page,
                    pageIndex,
                    pages[0]
                        .ProviderReportedTotalPages);
                fetched[pageIndex - startPage] =
                    page;
            });
        pages.AddRange(fetched);
    }

    private int CapturePageConcurrency() =>
        Math.Max(
            1,
            _options.SequentialScrape
                ? _options.PageConcurrency
                : _options.DegreeOfParallelism);

    private async Task ReconcileScopeWireRequestsAsync(
        List<CapturePageAcquisition> pages,
        long? wireRequestStart,
        CancellationToken cancellationToken)
    {
        if (wireRequestStart is null ||
            _pageSource is not
                ICapturePageSourceMetrics metrics)
        {
            return;
        }
        await metrics.QuiesceAsync(
            cancellationToken);
        long measured;
        long accounted;
        try
        {
            measured = checked(
                metrics.TotalWireRequestCount -
                wireRequestStart.Value);
            accounted = pages.Sum(
                static page =>
                    checked(page.WireRequestCount));
        }
        catch (OverflowException)
        {
            throw CaptureFailed();
        }
        if (measured < accounted ||
            pages.Count == 0)
        {
            throw CaptureFailed();
        }
        var unassigned = measured - accounted;
        if (unassigned == 0)
            return;
        try
        {
            var terminal = pages[^1];
            pages[^1] = terminal with
            {
                WireRequestCount = checked(
                    terminal.WireRequestCount +
                    unassigned),
            };
        }
        catch (OverflowException)
        {
            throw CaptureFailed();
        }
    }

    private static IReadOnlyList<CaptureSoloLeaderboardEntry>
        ReadSoloEntries(CapturePageAcquisition page) =>
        page.Entries
            .Select(static entry =>
                entry.Deserialize<
                    CaptureSoloLeaderboardEntry>(
                    TierZeroCanonicalJson
                        .SerializerOptions) ??
                throw CaptureFailed())
            .ToArray();

    private static LeaderboardEntry ToLeaderboardEntry(
        CaptureSoloLeaderboardEntry entry) =>
        new()
        {
            AccountId = entry.AccountId,
            Rank = entry.Rank,
            Percentile = entry.Percentile,
            Score = entry.Score,
            Accuracy = entry.Accuracy,
            IsFullCombo = entry.IsFullCombo,
            Stars = entry.Stars,
            Season = entry.Season,
            Difficulty = entry.Difficulty,
            EndTime = entry.EndTime,
        };

    private async Task<CapturePageAcquisition>
        FetchPageAsync(
            CaptureAuthentication authentication,
            CaptureScopeKind scopeKind,
            string songId,
            string leaderboardType,
            int pageIndex,
            CancellationToken cancellationToken)
    {
        CapturePageAcquisition page;
        try
        {
            page = await _pageSource.FetchAsync(
                authentication,
                scopeKind,
                songId,
                leaderboardType,
                pageIndex,
                cancellationToken);
            cancellationToken
                .ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
            when (cancellationToken
                .IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw CaptureFailed();
        }
        catch (ScrapeAuthenticationException)
        {
            throw new CaptureOnlyException(
                CaptureOnlyFailureKind.AuthenticationFailed,
                CaptureOnlyExitCode.AuthenticationFailed,
                "Capture authentication failed.");
        }
        catch (CaptureOnlyException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new CaptureOnlyException(
                CaptureOnlyFailureKind.CaptureFailed,
                CaptureOnlyExitCode.CaptureFailed,
                "Leaderboard capture failed.");
        }

        if (page.Status ==
            CapturePageAcquisitionStatus.Unauthorized)
        {
            throw new CaptureOnlyException(
                CaptureOnlyFailureKind.AuthenticationFailed,
                CaptureOnlyExitCode.AuthenticationFailed,
                "Capture authentication failed.");
        }
        if (page.Status is not
                (CapturePageAcquisitionStatus.Success or
                 CapturePageAcquisitionStatus.EventNotFound) ||
            !page.IsExact)
        {
            throw CaptureFailed();
        }
        return page;
    }

    private static void ValidatePage(
        CapturePageAcquisition page,
        int expectedPage,
        int? expectedTotalPages)
    {
        if (page.Entries is null ||
            page.PageIndex != expectedPage ||
            page.PageSize <= 0 ||
            page.PageSize >
                CapturePackageFormat.MaximumPageSize ||
            page.ProviderReportedTotalPages < 0 ||
            page.ProviderReportedTotalPages >
                CapturePackageFormat.MaximumRequestRecords ||
            !page.ProviderReportedTotalEntries.HasValue ||
            page.ProviderReportedTotalEntries.Value < 0 ||
            page.ProviderReportedTotalEntries.Value >
                CapturePackageFormat
                    .MaximumScopeEntries ||
            page.WireRequestCount <= 0 ||
            page.ProviderResponseBytes < 0 ||
            page.Entries.Count > page.PageSize ||
            page.Entries.Any(static entry =>
                entry.ValueKind !=
                    JsonValueKind.Object) ||
            expectedTotalPages.HasValue &&
            page.ProviderReportedTotalPages !=
                expectedTotalPages.Value ||
            page.Status ==
                CapturePageAcquisitionStatus.EventNotFound &&
            (expectedPage != 0 ||
             page.ProviderReportedTotalPages != 0 ||
             page.ProviderReportedTotalEntries != 0 ||
             page.Entries.Count != 0) ||
            page.Status ==
                CapturePageAcquisitionStatus.Success &&
            page.ProviderReportedTotalPages == 0 &&
            page.ProviderReportedTotalEntries != 0)
        {
            throw CaptureFailed();
        }
    }

    private CaptureCatalogAcquisition
        ValidateCatalogAcquisition(
            CaptureCatalogAcquisition acquisition)
    {
        if (!acquisition.ProviderRequestSucceeded ||
            !acquisition.IsExact ||
            acquisition.SafetyMergeApplied ||
            acquisition.IsReconstructed ||
            acquisition.ParseFailureCount != 0 ||
            acquisition.Catalog is null ||
            !TierZeroCanonicalJson.IsSha256(
                acquisition
                    .ProviderContentSha256))
        {
            throw CatalogRejected();
        }

        var catalog =
            CapturePackageContract.ValidateCatalog(
                acquisition.Catalog);
        IReadOnlyDictionary<string, SongMaxScores>?
            normalizedMaximumScores = null;
        if (acquisition.PaginationMaximumScores is
            { } maximumScores)
        {
            var songIds = catalog.Songs
                .Select(static song => song.SongId)
                .ToHashSet(StringComparer.Ordinal);
            if (maximumScores.Any(pair =>
                    pair.Value is null ||
                    !songIds.Contains(pair.Key) ||
                    CapturePackageFormat
                        .SoloInstrumentOrder.Any(
                            instrument =>
                                pair.Value
                                    .GetByInstrument(
                                        instrument) is <= 0)))
            {
                throw CatalogRejected();
            }
            if (!TierZeroCanonicalJson.IsSha256(
                    acquisition
                        .PaginationMaximumScoresSha256))
            {
                throw CatalogRejected();
            }
            normalizedMaximumScores = maximumScores
                .OrderBy(
                    static pair => pair.Key,
                    StringComparer.Ordinal)
                .ToDictionary(
                    static pair => pair.Key,
                    static pair =>
                        CloneMaximumScores(
                            pair.Value),
                    StringComparer.Ordinal);
        }
        else if (acquisition
                 .PaginationMaximumScoresSha256 is not null)
        {
            throw CatalogRejected();
        }
        if (catalog.CatalogVersion !=
            CaptureCatalogBuilder
                .CatalogVersionFromProviderHash(
                    acquisition
                        .ProviderContentSha256!))
        {
            throw CatalogRejected();
        }
        return acquisition with
        {
            Catalog = catalog,
            ProviderContentSha256 =
                acquisition.ProviderContentSha256!
                    .ToLowerInvariant(),
            PaginationMaximumScores =
                normalizedMaximumScores,
            PaginationMaximumScoresSha256 =
                acquisition
                    .PaginationMaximumScoresSha256?
                    .ToLowerInvariant(),
        };
    }

    private static SongMaxScores CloneMaximumScores(
        SongMaxScores source) =>
        new()
        {
            MaxLeadScore = source.MaxLeadScore,
            MaxBassScore = source.MaxBassScore,
            MaxDrumsScore = source.MaxDrumsScore,
            MaxVocalsScore = source.MaxVocalsScore,
            MaxProLeadScore = source.MaxProLeadScore,
            MaxProBassScore = source.MaxProBassScore,
            MaxProCymbalsScore =
                source.MaxProCymbalsScore,
            MaxProDrumsScore =
                source.MaxProDrumsScore,
            ExpectedInstruments =
                source.ExpectedInstruments?.ToArray()
                ?? [],
        };

    private static bool MaximumScoresEqual(
        IReadOnlyDictionary<string, SongMaxScores>?
            left,
        IReadOnlyDictionary<string, SongMaxScores>?
            right)
    {
        left ??=
            new Dictionary<string, SongMaxScores>(
                StringComparer.Ordinal);
        right ??=
            new Dictionary<string, SongMaxScores>(
                StringComparer.Ordinal);
        if (left.Count != right.Count)
            return false;
        foreach (var (songId, leftScores) in left)
        {
            if (!right.TryGetValue(
                    songId,
                    out var rightScores) ||
                CapturePackageFormat
                    .SoloInstrumentOrder.Any(
                        instrument =>
                            leftScores.GetByInstrument(
                                instrument) !=
                            rightScores.GetByInstrument(
                                instrument)))
            {
                return false;
            }
        }
        return true;
    }

    private TierZeroPackageDraft CreateDraft(
        string captureId,
        CaptureCatalogAcquisition catalog,
        ReadOnlySpan<byte> catalogBytes,
        TierZeroConfigurationFingerprint configuration,
        DateTimeOffset createdAt) =>
        new(
            captureId,
            new TierZeroSourceIdentity(
                null,
                null,
                createdAt,
                new TierZeroCatalogIdentity(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"epic-catalog-{catalog.Catalog!.CatalogVersion}"),
                    TierZeroCanonicalJson
                        .Sha256Hex(catalogBytes))),
            _environment.Implementation,
            NoDatabaseIdentity(),
            configuration,
            TierZeroSummaryReferences.Empty,
            catalog.PaginationMaximumScoresSha256 is
            { } maximumScoresSha256
                ? [
                    new TierZeroParentRootHash(
                        "capture-pagination-max-scores",
                        maximumScoresSha256),
                    new TierZeroParentRootHash(
                        "provider-song-catalog",
                        catalog.ProviderContentSha256!),
                ]
                : [
                    new TierZeroParentRootHash(
                        "provider-song-catalog",
                        catalog.ProviderContentSha256!),
                ],
            1,
            _environment.ProducerIdentity,
            createdAt);

    private TierZeroPackageDraft CreatePendingDraft(
        string captureId,
        TierZeroConfigurationFingerprint configuration,
        DateTimeOffset createdAt) =>
        new(
            captureId,
            new TierZeroSourceIdentity(
                null,
                null,
                createdAt,
                new TierZeroCatalogIdentity(
                    "capture-catalog-pending-v1",
                    PendingCatalogHash)),
            _environment.Implementation,
            NoDatabaseIdentity(),
            configuration,
            TierZeroSummaryReferences.Empty,
            [
                new TierZeroParentRootHash(
                    "capture-attempt",
                    PendingCatalogHash),
            ],
            1,
            _environment.ProducerIdentity,
            createdAt);

    private TierZeroConfigurationFingerprint
        CreateConfigurationFingerprint(
            IReadOnlyDictionary<string, SongMaxScores>?
                maximumScores = null)
    {
        var enabledSolo =
            ScrapeOrchestrator.GetEnabledInstruments(
                _options);
        var enabledBands =
            _options.EnableBandScraping
                ? BandInstrumentMapping.AllBandTypes
                : [];
        var values =
            new Dictionary<string, string?>
            {
                ["Capture:ConfiguredRouting"] =
                    (_options.ProxyUrls.Count > 0)
                        .ToString(
                            CultureInfo.InvariantCulture),
                ["Capture:ContainerSelfHeal"] =
                    _options.ProxyContainerSelfHealEnabled
                        .ToString(
                            CultureInfo.InvariantCulture),
                ["Capture:EnabledBandTypes"] =
                    string.Join(',', enabledBands),
                ["Capture:EnabledSoloTypes"] =
                    string.Join(',', enabledSolo),
                ["Capture:GlobalRequestsPerSecond"] =
                    _options.MaxRequestsPerSecond
                        .ToString(
                            CultureInfo.InvariantCulture),
                ["Capture:InitialDegreeOfParallelism"] =
                    _options.InitialDop.ToString(
                        CultureInfo.InvariantCulture),
                ["Capture:MaximumDegreeOfParallelism"] =
                    _options.DegreeOfParallelism.ToString(
                        CultureInfo.InvariantCulture),
                ["Capture:MaximumPackageBytes"] =
                    _environment.StoragePolicy
                        .MaximumPackageBytes
                        .ToString(
                            CultureInfo.InvariantCulture),
                ["Capture:MaximumRetainedSealedPackages"] =
                    _environment.StoragePolicy
                        .MaximumRetainedSealedPackages
                        .ToString(
                            CultureInfo.InvariantCulture),
                ["Capture:MinimumFreeSpaceReserveBytes"] =
                    _environment.StoragePolicy
                        .MinimumFreeSpaceReserveBytes
                        .ToString(
                            CultureInfo.InvariantCulture),
                ["Capture:PerRoutePacing"] =
                    (_options
                            .ProxyMaxRequestsPerSecondPerEndpoint >
                        0)
                    .ToString(
                        CultureInfo.InvariantCulture),
                ["Capture:BandMaximumPages"] =
                    _options.BandMaxPagesPerLeaderboard
                    .ToString(
                        CultureInfo.InvariantCulture),
                ["Capture:BandValidEntryTarget"] =
                    _options.BandValidEntryTarget
                    .ToString(
                        CultureInfo.InvariantCulture),
                ["Capture:SoloDeepScrapeBatchPages"] =
                    _options.OverThresholdExtraPages
                    .ToString(
                        CultureInfo.InvariantCulture),
                ["Capture:SoloMaximumPages"] =
                    _options.MaxPagesPerLeaderboard
                    .ToString(
                        CultureInfo.InvariantCulture),
                ["Capture:SoloOverThresholdMultiplier"] =
                    _options.OverThresholdMultiplier
                    .ToString(
                        "R",
                        CultureInfo.InvariantCulture),
                ["Capture:SoloValidCutoffMultiplier"] =
                    _options.ValidCutoffMultiplier
                    .ToString(
                        "R",
                        CultureInfo.InvariantCulture),
                ["Capture:SoloValidEntryTarget"] =
                    _options.ValidEntryTarget
                    .ToString(
                        CultureInfo.InvariantCulture),
                ["Capture:PageConcurrency"] =
                    _options.PageConcurrency.ToString(
                    CultureInfo.InvariantCulture),
                ["Capture:SequentialScrape"] =
                    _options.SequentialScrape.ToString(
                    CultureInfo.InvariantCulture),
                ["Capture:PaginationMaximumScores"] =
                    MaximumScoresFingerprint(
                    maximumScores),
                ["Capture:ResponseShardMaximumBytes"] =
                    _environment
                        .ResponseShardMaximumBytes
                        .ToString(
                            CultureInfo.InvariantCulture),
            };
        return TierZeroConfigurationFingerprinter.Create(
            values,
            values.Keys);
    }

    private static string MaximumScoresFingerprint(
        IReadOnlyDictionary<string, SongMaxScores>?
            maximumScores)
    {
        if (maximumScores is null ||
            maximumScores.Count == 0)
        {
            return TierZeroCanonicalJson.Sha256Hex(
                Encoding.UTF8.GetBytes(
                    "fst.capture-no-pagination-maxima.v1"));
        }

        var values = maximumScores
            .OrderBy(
                static pair => pair.Key,
                StringComparer.Ordinal)
            .Select(pair => new
            {
                songId = pair.Key,
                maxima =
                    CapturePackageFormat
                        .SoloInstrumentOrder
                        .Select(instrument => new
                        {
                            instrument,
                            value = pair.Value
                                .GetByInstrument(
                                    instrument),
                        })
                        .ToArray(),
            })
            .ToArray();
        return TierZeroCanonicalJson.Sha256Hex(
            TierZeroCanonicalJson.Serialize(values));
    }

    private async Task TryMarkInterruptedAsync(
        CapturePackageWriter? writer,
        string outputPath,
        string captureId,
        TierZeroConfigurationFingerprint configuration,
        DateTimeOffset startedAt,
        string marker)
    {
        try
        {
            if (writer?.IsSealed == true)
                return;
            writer ??= await CapturePackageWriter.CreateAsync(
                outputPath,
                CreatePendingDraft(
                    captureId,
                    configuration,
                    startedAt),
                CancellationToken.None);
            await writer.MarkInterruptedAsync(
                marker,
                _timeProvider.GetUtcNow(),
                CancellationToken.None);
        }
        catch
        {
        }
    }

    private static TierZeroDatabaseIdentity
        NoDatabaseIdentity() =>
        new(
            1,
            [],
            NoDatabaseSchemaFingerprint);

    private static CaptureOnlyException MapFailure(
        CaptureStage stage) =>
        stage switch
        {
            CaptureStage.Authentication =>
                new CaptureOnlyException(
                    CaptureOnlyFailureKind
                        .AuthenticationFailed,
                    CaptureOnlyExitCode
                        .AuthenticationFailed,
                    "Capture authentication failed."),
            CaptureStage.Catalog or
            CaptureStage.CatalogVerification =>
                CatalogRejected(),
            CaptureStage.Capturing =>
                CaptureFailed(),
            CaptureStage.PackageWriting or
            CaptureStage.Sealing =>
                new CaptureOnlyException(
                    CaptureOnlyFailureKind.SealFailed,
                    CaptureOnlyExitCode.SealFailed,
                    "Capture package sealing failed."),
            _ =>
                new CaptureOnlyException(
                    CaptureOnlyFailureKind.UnexpectedFailure,
                    CaptureOnlyExitCode.UnexpectedFailure,
                    "Capture failed unexpectedly."),
        };

    private static CaptureOnlyException CatalogRejected() =>
        new(
            CaptureOnlyFailureKind.CatalogRejected,
            CaptureOnlyExitCode.CatalogRejected,
            "Exact provider catalog validation failed.");

    private static CaptureOnlyException CaptureFailed() =>
        new(
            CaptureOnlyFailureKind.CaptureFailed,
            CaptureOnlyExitCode.CaptureFailed,
            "Leaderboard capture was incomplete or inconsistent.");

    private static CaptureOnlyException AdmissionRejected() =>
        new(
            CaptureOnlyFailureKind.AdmissionRejected,
            CaptureOnlyExitCode.AdmissionRejected,
            "Capture storage admission was rejected.");

    private static string FailureMarker(
        CaptureOnlyFailureKind kind) =>
        kind switch
        {
            CaptureOnlyFailureKind.AuthenticationFailed =>
                "capture-authentication-failed",
            CaptureOnlyFailureKind.CatalogRejected =>
                "capture-catalog-rejected",
            CaptureOnlyFailureKind.CaptureFailed =>
                "capture-incomplete",
            CaptureOnlyFailureKind.SealFailed =>
                "capture-seal-failed",
            CaptureOnlyFailureKind.AdmissionRejected =>
                "capture-admission-rejected",
            _ => "capture-unexpected-failure",
        };

    private enum CaptureStage
    {
        Authentication,
        Catalog,
        PackageWriting,
        Capturing,
        CatalogVerification,
        Sealing,
    }
}

internal sealed record CaptureResponseLocation(
    string Path,
    long Offset,
    int Length,
    string Sha256);

internal sealed class CaptureResponseShardWriter
    : IDisposable
{
    private readonly CapturePackageWriter _writer;
    private readonly int _maximumShardBytes;
    private readonly long _maximumResponseArtifactBytes;
    private MemoryStream _buffer = new();
    private int _shardOrdinal;
    private int _recordCount;
    private long _responseArtifactBytes;

    internal CaptureResponseShardWriter(
        CapturePackageWriter writer,
        int maximumShardBytes,
        long maximumResponseArtifactBytes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (maximumShardBytes <= 0 ||
            maximumShardBytes >
                CapturePackageFormat
                    .MaximumResponseShardBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumShardBytes));
        }
        if (maximumResponseArtifactBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumResponseArtifactBytes));
        }
        _writer = writer;
        _maximumShardBytes = maximumShardBytes;
        _maximumResponseArtifactBytes =
            maximumResponseArtifactBytes;
    }

    internal int ShardCount { get; private set; }

    internal async Task<CaptureResponseLocation>
        AppendAsync(
            CaptureResponseArtifact response,
            CancellationToken cancellationToken)
    {
        var bytes =
            CapturePackageContract.SerializeResponse(
                response);
        var memberBytes = checked(bytes.Length + 1);
        if (memberBytes > _maximumShardBytes)
        {
            throw new CaptureOnlyException(
                CaptureOnlyFailureKind.CaptureFailed,
                CaptureOnlyExitCode.CaptureFailed,
                "A capture response exceeds the configured shard bound.");
        }
        if (checked(
                _responseArtifactBytes +
                _buffer.Length +
                memberBytes) >
            _maximumResponseArtifactBytes)
        {
            throw new CaptureOnlyException(
                CaptureOnlyFailureKind
                    .AdmissionRejected,
                CaptureOnlyExitCode
                    .AdmissionRejected,
                "Capture storage admission was rejected.");
        }
        if (_recordCount > 0 &&
            _buffer.Length + memberBytes >
                _maximumShardBytes)
        {
            await FlushAsync(cancellationToken);
        }
        if (_shardOrdinal >=
            CapturePackageFormat.MaximumResponseShards)
        {
            throw new CaptureOnlyException(
                CaptureOnlyFailureKind.CaptureFailed,
                CaptureOnlyExitCode.CaptureFailed,
                "Capture response shards exceed the contract limit.");
        }

        var path = CapturePackageFormat
            .ResponseShardPath(_shardOrdinal);
        var offset = _buffer.Length;
        await _buffer.WriteAsync(
            bytes,
            cancellationToken);
        _buffer.WriteByte((byte)'\n');
        _recordCount++;
        return new CaptureResponseLocation(
            path,
            offset,
            bytes.Length,
            TierZeroCanonicalJson.Sha256Hex(
                bytes));
    }

    internal Task CompleteAsync(
        CancellationToken cancellationToken) =>
        FlushAsync(cancellationToken);

    private async Task FlushAsync(
        CancellationToken cancellationToken)
    {
        if (_recordCount == 0)
            return;

        var path = CapturePackageFormat
            .ResponseShardPath(_shardOrdinal);
        var content = _buffer.ToArray();
        try
        {
            await _writer.AddArtifactAsync(
                new TierZeroArtifactRegistration(
                    CapturePackageFormat
                        .ResponseShardOwner,
                    path,
                    CapturePackageFormat
                        .JsonLinesMediaType,
                    CapturePackageFormat
                        .ResponseSchemaVersion,
                    _recordCount,
                    content.LongLength),
                content,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            TierZeroPackageException or
            CapturePackageException)
        {
            throw new CaptureOnlyException(
                CaptureOnlyFailureKind.SealFailed,
                CaptureOnlyExitCode.SealFailed,
                "Capture package artifact write failed.",
                exception);
        }
        _buffer.Dispose();
        _buffer = new MemoryStream();
        _recordCount = 0;
        _shardOrdinal++;
        ShardCount++;
        _responseArtifactBytes = checked(
            _responseArtifactBytes +
            content.LongLength);
    }

    public void Dispose() =>
        _buffer.Dispose();
}
