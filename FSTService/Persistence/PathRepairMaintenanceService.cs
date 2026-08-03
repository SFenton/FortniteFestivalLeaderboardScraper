using System.Text.Json;
using FortniteFestival.Core;
using FSTService.Api;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService.Persistence;

public sealed class PathRepairMaintenanceService
{
    internal const string RankingFreezeReason =
        "path-repair-ranking-rebuild";

    private readonly PathGenerationCoordinator _pathGeneration;
    private readonly IPathDataStore _pathStore;
    private readonly IMetaDatabase _metaDatabase;
    private readonly IPathRepairMaintenanceLeaseProvider _leaseProvider;
    private readonly IOptions<ScraperOptions> _options;
    private readonly SongsCacheService _songsCache;
    private readonly IPathRepairRankingExecutor _rankingExecutor;
    private readonly ILogger<PathRepairMaintenanceService> _log;

    public PathRepairMaintenanceService(
        PathGenerationCoordinator pathGeneration,
        IPathDataStore pathStore,
        IMetaDatabase metaDatabase,
        IPathRepairMaintenanceLeaseProvider leaseProvider,
        IOptions<ScraperOptions> options,
        SongsCacheService songsCache,
        IPathRepairRankingExecutor rankingExecutor,
        ILogger<PathRepairMaintenanceService> log)
    {
        _pathGeneration = pathGeneration;
        _pathStore = pathStore;
        _metaDatabase = metaDatabase;
        _leaseProvider = leaseProvider;
        _options = options;
        _songsCache = songsCache;
        _rankingExecutor = rankingExecutor;
        _log = log;
    }

    public async Task<PathRepairStageReport> StageExactFourAsync(
        string manifestOutputPath,
        CancellationToken ct = default)
    {
        var options = _options.Value;
        if (!options.EnablePathGeneration)
        {
            throw new InvalidOperationException(
                "Exact-four staging requires path generation to be enabled.");
        }
        if (options.EnableAutomaticPathGeneration)
        {
            throw new InvalidOperationException(
                "Exact-four staging requires automatic path generation to be disabled.");
        }

        var resolvedOutputPath =
            PathRepairFileStore.ResolveNewJsonOutputPath(
                options.DataDirectory,
                manifestOutputPath);
        await using var lease = await RequireLeaseAsync(
            "path-repair-stage-exact-four",
            holdPublicationLock: false,
            ct);

        var snapshots = LoadExactRepairSnapshots();
        var requests = snapshots
            .Select(snapshot =>
            {
                var state = snapshot.State;
                if (state.Revision < 0 ||
                    state.Revision == long.MaxValue ||
                    string.IsNullOrWhiteSpace(state.CatalogLastModified) ||
                    !AllMaximaAreNull(state.MaxScores) ||
                    snapshot.Song.lastModified == DateTime.MinValue ||
                    !TryGetExactProviderLastModified(
                        snapshot.Song,
                        out var providerLastModified) ||
                    !ProviderTimestampIdentity.Equivalent(
                        providerLastModified,
                        state.CatalogLastModified))
                {
                    throw new InvalidOperationException(
                        $"Repair staging database identity is incomplete for {state.SongId}.");
                }

                var catalogLastModified =
                    ProviderTimestampIdentity.NormalizeRequired(
                        state.CatalogLastModified!,
                        nameof(state.CatalogLastModified));
                var request = SongPathRequest.FromSong(snapshot.Song)
                    ?? throw new InvalidOperationException(
                        $"Repair song {state.SongId} has no usable encrypted chart identity.");
                if (!request.ExpectedInstruments.Contains(
                        "Solo_PeripheralGuitar",
                        StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Repair song {state.SongId} has no expected Pro Lead chart.");
                }
                request = request with
                {
                    LastModified = catalogLastModified,
                    ExpectedInstruments = ["Solo_PeripheralGuitar"],
                };

                return (
                    Request: request,
                    State: state,
                    CatalogLastModified: catalogLastModified);
            })
            .ToArray();

        var attempts = await _pathGeneration.StagePathsSerialAsync(
            requests
                .Select(static request =>
                    (request.Request, request.State))
                .ToArray(),
            ct);
        var reports = new List<PathRepairStageSongReport>(
            ImprovementNotificationMaintenanceManifest.RequiredSongCount);
        var stagedPromotions = new List<PathGenerationPromotion>(
            ImprovementNotificationMaintenanceManifest.RequiredSongCount);
        for (var index = 0; index < requests.Length; index++)
        {
            var (request, state, catalogLastModified) = requests[index];
            if (index >= attempts.Count)
            {
                reports.Add(new PathRepairStageSongReport(
                    request.SongId,
                    "not_attempted",
                    state.Revision,
                    catalogLastModified,
                    state.MaxScores.MaxProLeadScore,
                    null,
                    null,
                    null,
                    null,
                    "A prior song failed; serial staging stopped."));
                continue;
            }

            var attempt = attempts[index];
            var staged = attempt.StagedPromotion;
            if (attempt.Outcome != PathGenerationAttemptOutcome.Staged ||
                staged is null)
            {
                reports.Add(new PathRepairStageSongReport(
                    request.SongId,
                    "failed",
                    state.Revision,
                    catalogLastModified,
                    state.MaxScores.MaxProLeadScore,
                    null,
                    staged?.ArtifactGenerationId,
                    staged?.DatFileHash,
                    attempt.FailureStage,
                    attempt.Detail));
                continue;
            }

            var validated = PathArtifactResolver.ValidateImmutableGeneration(
                options.DataDirectory,
                staged.SongId,
                staged.ArtifactGenerationId);
            ValidateStagedIdentity(staged, validated);
            var proposed = validated.MaxScores.MaxProLeadScore;
            if (proposed is not > 0)
            {
                throw new InvalidOperationException(
                    $"Staged generation {staged.ArtifactGenerationId} has no positive Pro Lead maximum.");
            }

            stagedPromotions.Add(staged with
            {
                MaxScores = validated.MaxScores,
            });
            reports.Add(new PathRepairStageSongReport(
                request.SongId,
                "staged",
                state.Revision,
                catalogLastModified,
                state.MaxScores.MaxProLeadScore,
                proposed,
                staged.ArtifactGenerationId,
                staged.DatFileHash,
                null,
                null));
        }

        if (stagedPromotions.Count !=
            ImprovementNotificationMaintenanceManifest.RequiredSongCount)
        {
            return new PathRepairStageReport(
                PathRepairMaintenanceCommand.StageFlag,
                Succeeded: false,
                ManifestWritten: false,
                resolvedOutputPath,
                ManifestSha256: null,
                reports);
        }

        EnsureStageSourceIdentityUnchanged(
            snapshots,
            LoadExactRepairSnapshots());
        var stagedBySong = stagedPromotions.ToDictionary(
            static promotion => promotion.SongId,
            StringComparer.Ordinal);
        var statesBySong = snapshots.ToDictionary(
            static snapshot => snapshot.State.SongId,
            static snapshot => snapshot.State,
            StringComparer.Ordinal);
        var catalogLastModifiedBySong = requests.ToDictionary(
            static request => request.State.SongId,
            static request => request.CatalogLastModified,
            StringComparer.Ordinal);
        var manifest = new ImprovementNotificationMaintenanceManifest(
            ImprovementNotificationMaintenanceManifest.CurrentManifestVersion,
            ImprovementNotificationMaintenanceManifest.RequiredSongIds
                .Select(songId =>
                {
                    var state = statesBySong[songId];
                    var staged = stagedBySong[songId];
                    return new ImprovementNotificationMaintenanceSong(
                        songId,
                        state.Revision,
                        catalogLastModifiedBySong[songId],
                        state.MaxScores.MaxProLeadScore,
                        staged.MaxScores.MaxProLeadScore!.Value,
                        staged.ArtifactGenerationId,
                        staged.DatFileHash,
                        staged.Runtime.Version,
                        staged.Runtime.BinarySha256,
                        staged.Runtime.Profile);
                })
                .ToArray())
            .ValidateAndNormalize();

        var written = await PathRepairFileStore.WriteNewJsonAsync(
            options.DataDirectory,
            resolvedOutputPath,
            manifest,
            PathRepairJson.Options,
            ct);
        var loaded = await ImprovementNotificationMaintenanceManifest.LoadAsync(
            written.FullPath,
            ct);
        if (!manifest.Songs.SequenceEqual(loaded.Songs) ||
            manifest.ManifestVersion != loaded.ManifestVersion)
        {
            throw new InvalidOperationException(
                "Written path-repair manifest did not round-trip exactly.");
        }

        return new PathRepairStageReport(
            PathRepairMaintenanceCommand.StageFlag,
            Succeeded: true,
            ManifestWritten: true,
            written.FullPath,
            written.Sha256,
            reports);
    }

    public async Task<PathRepairPromotionReport> PromoteExactFourAsync(
        string manifestPath,
        string rollbackOutputPath,
        long expectedPublishedScrapeId,
        CancellationToken ct = default)
    {
        ValidatePublishedScrapeId(expectedPublishedScrapeId);
        RequireAutomaticPathGenerationDisabled();
        var dataDirectory = _options.Value.DataDirectory;
        var resolvedManifestPath =
            PathRepairFileStore.ResolveExistingJsonInputPath(
                dataDirectory,
                manifestPath);
        var resolvedRollbackPath =
            PathRepairFileStore.ResolveNewJsonOutputPath(
                dataDirectory,
                rollbackOutputPath);
        var manifest = await ImprovementNotificationMaintenanceManifest.LoadAsync(
            resolvedManifestPath,
            ct);
        var manifestSha = await PathRepairFileStore.ComputeSha256Async(
            resolvedManifestPath,
            ct);

        await using var lease = await RequireLeaseAsync(
            "path-repair-promote-exact-four",
            holdPublicationLock: true,
            ct);
        var initialFreeze = _metaDatabase.GetPublicReadFreezeState();
        var published = ValidatePublishedRepairContext(
            expectedPublishedScrapeId,
            requireUnfrozen: !initialFreeze.IsFrozen,
            requiredFreezeReason: initialFreeze.IsFrozen
                ? RankingFreezeReason
                : null);
        var preflight = BuildPromotionPreflight(
            manifest,
            published,
            ManifestRepairPhase.PreRepair);

        var rollbackSnapshot = new PathRepairRollbackSnapshot(
            PathRepairRollbackSnapshot.CurrentSnapshotVersion,
            DateTime.UtcNow,
            expectedPublishedScrapeId,
            manifestSha,
            preflight
                .Select(static item => CreateRollbackSong(item.CurrentState))
                .ToArray());
        var rollback = await PathRepairFileStore.WriteNewJsonAsync(
            dataDirectory,
            resolvedRollbackPath,
            rollbackSnapshot,
            PathRepairJson.Options,
            ct);
        _log.LogInformation(
            "Path-repair rollback snapshot persisted before promotion at {RollbackPath} with SHA-256 {RollbackSha256}.",
            rollback.FullPath,
            rollback.Sha256);

        if (!initialFreeze.IsFrozen)
        {
            _metaDatabase.SetPublicReadFreeze(
                true,
                expectedPublishedScrapeId,
                RankingFreezeReason);
        }
        var repairFreeze = _metaDatabase.GetPublicReadFreezeState();
        if (!repairFreeze.IsFrozen ||
            repairFreeze.ScrapeId != expectedPublishedScrapeId ||
            !string.Equals(
                repairFreeze.Reason,
                RankingFreezeReason,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Path-repair promotion could not establish its fail-closed public-read maintenance freeze.");
        }

        var songReports = new List<PathRepairPromotionSongReport>(
            ImprovementNotificationMaintenanceManifest.RequiredSongCount);
        var promotedCount = 0;
        using var cacheMutation = _songsCache.BeginContentMutation();
        for (var index = 0; index < preflight.Count; index++)
        {
            var item = preflight[index];
            try
            {
                EnsurePromotionBoundaryStillIdle(
                    expectedPublishedScrapeId);
                var outcome = await _pathStore.TryPromoteGenerationAsync(
                    item.Promotion,
                    ct);
                if (outcome != PathGenerationPromotionOutcome.Promoted)
                {
                    songReports.Add(new PathRepairPromotionSongReport(
                        item.Promotion.SongId,
                        outcome == PathGenerationPromotionOutcome.Conflict
                            ? "conflict"
                            : "song_missing",
                        item.Promotion.ExpectedRevision,
                        null,
                        item.Promotion.ArtifactGenerationId,
                        "The compare-and-swap promotion did not succeed."));
                    AddNotAttemptedPromotionReports(
                        preflight,
                        index + 1,
                        songReports);
                    break;
                }

                promotedCount++;
                songReports.Add(new PathRepairPromotionSongReport(
                    item.Promotion.SongId,
                    "promoted",
                    item.Promotion.ExpectedRevision,
                    checked(item.Promotion.ExpectedRevision + 1),
                    item.Promotion.ArtifactGenerationId,
                    null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                songReports.Add(new PathRepairPromotionSongReport(
                    item.Promotion.SongId,
                    "failed",
                    item.Promotion.ExpectedRevision,
                    null,
                    item.Promotion.ArtifactGenerationId,
                    BoundDetail(ex.Message)));
                AddNotAttemptedPromotionReports(
                    preflight,
                    index + 1,
                    songReports);
                break;
            }
        }

        var succeeded = promotedCount ==
            ImprovementNotificationMaintenanceManifest.RequiredSongCount;
        var partial = promotedCount > 0 && !succeeded;
        _log.LogInformation(
            "Exact-four path repair promotion finished: promoted={PromotedCount}, succeeded={Succeeded}, partial={Partial}, rollback={RollbackPath}.",
            promotedCount,
            succeeded,
            partial,
            rollback.FullPath);
        return new PathRepairPromotionReport(
            PathRepairMaintenanceCommand.PromoteFlag,
            succeeded,
            partial,
            expectedPublishedScrapeId,
            manifestSha,
            rollback.FullPath,
            rollback.Sha256,
            PublicReadsFrozen: true,
            promotedCount,
            songReports);
    }

    public async Task<PathRepairRankingRebuildReport> RebuildRankingsAsync(
        string manifestPath,
        long expectedPublishedScrapeId,
        CancellationToken ct = default)
    {
        ValidatePublishedScrapeId(expectedPublishedScrapeId);
        RequireAutomaticPathGenerationDisabled();
        var dataDirectory = _options.Value.DataDirectory;
        var resolvedManifestPath =
            PathRepairFileStore.ResolveExistingJsonInputPath(
                dataDirectory,
                manifestPath);
        var manifest = await ImprovementNotificationMaintenanceManifest.LoadAsync(
            resolvedManifestPath,
            ct);

        await using var lease = await RequireLeaseAsync(
            "path-repair-rebuild-rankings",
            holdPublicationLock: true,
            ct);
        var published = ValidatePublishedRepairContext(
            expectedPublishedScrapeId,
            requireUnfrozen: false,
            requiredFreezeReason: RankingFreezeReason);
        BuildPromotionPreflight(
            manifest,
            published,
            ManifestRepairPhase.PostRepair);

        var freezeSet = true;
        var rebuildValidated = false;
        var readsRestored = false;
        string? detail = null;
        try
        {
            var frozen = _metaDatabase.GetPublicReadFreezeState();
            if (!frozen.IsFrozen ||
                frozen.ScrapeId != expectedPublishedScrapeId ||
                !string.Equals(
                    frozen.Reason,
                    RankingFreezeReason,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Path-repair ranking rebuild could not establish its public-read maintenance freeze.");
            }

            await _rankingExecutor.RebuildAsync(
                published.CatalogSongs,
                ct);

            var after = ValidatePublishedRepairContext(
                expectedPublishedScrapeId,
                requireUnfrozen: false,
                requiredFreezeReason: RankingFreezeReason);
            BuildPromotionPreflight(
                manifest,
                after,
                ManifestRepairPhase.PostRepair);
            rebuildValidated = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            detail = BoundDetail(ex.Message);
        }
        finally
        {
            if (freezeSet && rebuildValidated)
            {
                var freeze = _metaDatabase.GetPublicReadFreezeState();
                if (freeze.IsFrozen &&
                    freeze.ScrapeId == expectedPublishedScrapeId &&
                    string.Equals(
                        freeze.Reason,
                        RankingFreezeReason,
                        StringComparison.Ordinal))
                {
                    _metaDatabase.SetPublicReadFreeze(false);
                }

                readsRestored =
                    !_metaDatabase.GetPublicReadFreezeState().IsFrozen;
            }
        }

        var succeeded = detail is null && freezeSet && readsRestored;
        if (!readsRestored && detail is null)
        {
            detail =
                "Path-repair ranking rebuild completed but its public-read freeze was not safely restored.";
        }

        return new PathRepairRankingRebuildReport(
            PathRepairMaintenanceCommand.RebuildRankingsFlag,
            succeeded,
            expectedPublishedScrapeId,
            published.Catalog.PublicationId,
            published.Catalog.ContentHash,
            published.Catalog.SongCount,
            freezeSet,
            readsRestored,
            detail);
    }

    private IReadOnlyList<PathRepairSongSnapshot> LoadExactRepairSnapshots()
    {
        var snapshots = _pathStore.GetPathRepairSongSnapshots(
            ImprovementNotificationMaintenanceManifest.RequiredSongIds);
        if (snapshots.Count !=
            ImprovementNotificationMaintenanceManifest.RequiredSongCount ||
            !snapshots
                .Select(static snapshot => snapshot.State.SongId)
                .SequenceEqual(
                    ImprovementNotificationMaintenanceManifest.RequiredSongIds,
                    StringComparer.Ordinal) ||
            snapshots.Any(snapshot =>
                !string.Equals(
                    snapshot.Song.track?.su,
                    snapshot.State.SongId,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Path-repair database snapshot does not contain the exact approved four-song allowlist in ordinal order.");
        }

        return snapshots;
    }

    private static void EnsureStageSourceIdentityUnchanged(
        IReadOnlyList<PathRepairSongSnapshot> before,
        IReadOnlyList<PathRepairSongSnapshot> after)
    {
        for (var index = 0; index < before.Count; index++)
        {
            var expected = before[index].State;
            var actual = after[index].State;
            if (expected.SongId != actual.SongId ||
                expected.Revision != actual.Revision ||
                expected.DatFileHash != actual.DatFileHash ||
                expected.SongLastModified != actual.SongLastModified ||
                expected.GeneratedAtUtc != actual.GeneratedAtUtc ||
                expected.ChoptVersion != actual.ChoptVersion ||
                expected.ChoptBinarySha256 != actual.ChoptBinarySha256 ||
                expected.GenerationProfile != actual.GenerationProfile ||
                expected.ArtifactGenerationId != actual.ArtifactGenerationId ||
                expected.CatalogLastModified != actual.CatalogLastModified ||
                expected.PathGenerationPending != actual.PathGenerationPending ||
                !expected.ExpectedInstruments.SequenceEqual(
                    actual.ExpectedInstruments,
                    StringComparer.Ordinal) ||
                expected.MaxScores.MaxLeadScore != actual.MaxScores.MaxLeadScore ||
                expected.MaxScores.MaxBassScore != actual.MaxScores.MaxBassScore ||
                expected.MaxScores.MaxDrumsScore != actual.MaxScores.MaxDrumsScore ||
                expected.MaxScores.MaxVocalsScore != actual.MaxScores.MaxVocalsScore ||
                expected.MaxScores.MaxProLeadScore != actual.MaxScores.MaxProLeadScore ||
                expected.MaxScores.MaxProBassScore != actual.MaxScores.MaxProBassScore)
            {
                throw new InvalidOperationException(
                    $"Path-repair source identity changed during staging for {expected.SongId}; no maintenance manifest was written.");
            }
        }
    }

    private IReadOnlyList<PromotionPreflightItem> BuildPromotionPreflight(
        ImprovementNotificationMaintenanceManifest manifest,
        PublishedRepairContext published,
        ManifestRepairPhase phase)
    {
        var snapshots = LoadExactRepairSnapshots();
        var items = new List<PromotionPreflightItem>(
            ImprovementNotificationMaintenanceManifest.RequiredSongCount);
        for (var index = 0; index < manifest.Songs.Count; index++)
        {
            var expected = manifest.Songs[index];
            var snapshot = snapshots[index];
            var state = snapshot.State;
            if (!published.CatalogLastModifiedBySong.TryGetValue(
                    expected.SongId,
                    out var publishedLastModified) ||
                !ProviderTimestampIdentity.Equivalent(
                    publishedLastModified,
                    expected.ExpectedCatalogLastModified) ||
                !ProviderTimestampIdentity.Equivalent(
                    state.CatalogLastModified,
                    expected.ExpectedCatalogLastModified))
            {
                throw new InvalidOperationException(
                    $"Path-repair catalog identity mismatch for {expected.SongId}; no promotion or ranking rebuild occurred.");
            }

            var expectedRevision = phase == ManifestRepairPhase.PreRepair
                ? expected.ExpectedCurrentPathRevision
                : checked(expected.ExpectedCurrentPathRevision + 1);
            var expectedMaximum = phase == ManifestRepairPhase.PreRepair
                ? expected.CurrentOldProLeadMaxScore
                : expected.ProposedProLeadMaxScore;
            if (state.Revision != expectedRevision ||
                state.MaxScores.MaxProLeadScore != expectedMaximum)
            {
                throw new InvalidOperationException(
                    $"Path-repair database identity mismatch for {expected.SongId}; no promotion or ranking rebuild occurred.");
            }

            var validated = PathArtifactResolver.ValidateImmutableGeneration(
                _options.Value.DataDirectory,
                expected.SongId,
                expected.StagedArtifactGenerationId);
            ValidateManifestGenerationIdentity(expected, validated);
            if (!NonProLeadMaximaMatch(
                    state.MaxScores,
                    validated.MaxScores))
            {
                throw new InvalidOperationException(
                    phase == ManifestRepairPhase.PreRepair
                        ? $"Staged repair would change a non-Pro-Lead maximum for {expected.SongId}; no promotion occurred."
                        : $"Post-repair non-Pro-Lead maximum mismatch for {expected.SongId}; ranking rebuild did not start.");
            }

            if (phase == ManifestRepairPhase.PreRepair)
            {
                if (string.Equals(
                        state.ArtifactGenerationId,
                        expected.StagedArtifactGenerationId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Staged generation {expected.StagedArtifactGenerationId} is already active.");
                }

                items.Add(new PromotionPreflightItem(
                    state,
                    new PathGenerationPromotion(
                        expected.StagedArtifactGenerationId,
                        expected.SongId,
                        expected.ExpectedCurrentPathRevision,
                        expected.StagedArtifactGenerationId,
                        expected.StagedDatFileHash,
                        expected.ExpectedCatalogLastModified,
                        validated.Manifest.GeneratedAtUtc,
                        new PathGenerationRuntimeIdentity(
                            expected.StagedChoptVersion!,
                            expected.StagedChoptBinarySha256!,
                            expected.StagedGenerationProfile!),
                        validated.Manifest.ExpectedInstruments,
                        validated.MaxScores)));
            }
            else
            {
                if (!string.Equals(
                        state.ArtifactGenerationId,
                        expected.StagedArtifactGenerationId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        state.DatFileHash,
                        expected.StagedDatFileHash,
                        StringComparison.Ordinal) ||
                    !ProviderTimestampIdentity.Equivalent(
                        state.SongLastModified,
                        expected.ExpectedCatalogLastModified) ||
                    state.GeneratedAtUtc != validated.Manifest.GeneratedAtUtc ||
                    !string.Equals(
                        state.ChoptVersion,
                        expected.StagedChoptVersion,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        state.ChoptBinarySha256,
                        expected.StagedChoptBinarySha256,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        state.GenerationProfile,
                        expected.StagedGenerationProfile,
                        StringComparison.Ordinal) ||
                    !PathGenerationInstruments.NormalizeExpected(
                            state.ExpectedInstruments)
                        .SequenceEqual(
                            validated.Manifest.ExpectedInstruments,
                            StringComparer.Ordinal) ||
                    state.PathGenerationPending)
                {
                    throw new InvalidOperationException(
                        $"Post-promotion path identity mismatch for {expected.SongId}; ranking rebuild did not start.");
                }

                items.Add(new PromotionPreflightItem(
                    state,
                    new PathGenerationPromotion(
                        expected.StagedArtifactGenerationId,
                        expected.SongId,
                        expected.ExpectedCurrentPathRevision,
                        expected.StagedArtifactGenerationId,
                        expected.StagedDatFileHash,
                        expected.ExpectedCatalogLastModified,
                        validated.Manifest.GeneratedAtUtc,
                        new PathGenerationRuntimeIdentity(
                            expected.StagedChoptVersion!,
                            expected.StagedChoptBinarySha256!,
                            expected.StagedGenerationProfile!),
                        validated.Manifest.ExpectedInstruments,
                        validated.MaxScores)));
            }
        }

        return items;
    }

    private PublishedRepairContext ValidatePublishedRepairContext(
        long expectedPublishedScrapeId,
        bool requireUnfrozen,
        string? requiredFreezeReason = null)
    {
        var pointers = _metaDatabase.GetPublicationPointerState();
        if (pointers.PublishedScrapeId != expectedPublishedScrapeId ||
            pointers.CurrentPublicationId is null ||
            pointers.WorkingPublicationId is not null)
        {
            throw new InvalidOperationException(
                $"Path repair requires published scrape {expectedPublishedScrapeId} to be current with no working publication.");
        }

        var generation = _metaDatabase.GetPublicationGeneration(
            pointers.CurrentPublicationId.Value);
        if (generation is null ||
            generation.ScrapeId != expectedPublishedScrapeId ||
            !string.Equals(
                generation.Status,
                PublicationGenerationStatus.Current,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Path-repair current publication generation identity is invalid.");
        }
        var publishedRun = _metaDatabase.GetPublishedScrapeRun();
        if (publishedRun is null ||
            publishedRun.Id != expectedPublishedScrapeId ||
            publishedRun.CompletedAt is null ||
            !string.Equals(
                publishedRun.Status,
                "completed",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Path repair requires completed published scrape {expectedPublishedScrapeId}.");
        }

        var freeze = _metaDatabase.GetPublicReadFreezeState();
        if (requireUnfrozen && freeze.IsFrozen)
        {
            throw new InvalidOperationException(
                "Path repair cannot start while public reads are already frozen.");
        }
        if (requiredFreezeReason is not null &&
            (!freeze.IsFrozen ||
             freeze.ScrapeId != expectedPublishedScrapeId ||
             !string.Equals(
                 freeze.Reason,
                 requiredFreezeReason,
                 StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Path-repair public-read freeze ownership changed during ranking rebuild.");
        }

        var catalog = _metaDatabase.GetPublicationSongCatalogForScrape(
            expectedPublishedScrapeId)
            ?? throw new InvalidOperationException(
                $"Published scrape {expectedPublishedScrapeId} has no exact bound song catalog.");
        if (catalog.PublicationId != pointers.CurrentPublicationId.Value)
        {
            throw new InvalidOperationException(
                "Published path-repair catalog is not bound to the current publication.");
        }

        var songs = SongCatalogSnapshotBuilder.DeserializeCatalog(
                catalog.CatalogJson)
            .OrderBy(static song => song.track!.su, StringComparer.Ordinal)
            .ToArray();
        var rebuiltCatalog = SongCatalogSnapshotBuilder.Create(songs);
        if (songs.Length != catalog.SongCount ||
            rebuiltCatalog.SongCount != catalog.SongCount ||
            !string.Equals(
                rebuiltCatalog.ContentHash,
                catalog.ContentHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Published path-repair catalog content does not match its exact binding.");
        }

        var lastModified = new Dictionary<string, string>(
            StringComparer.Ordinal);
        foreach (var songId in
                 ImprovementNotificationMaintenanceManifest.RequiredSongIds)
        {
            var song = songs.SingleOrDefault(song =>
                string.Equals(
                    song.track?.su,
                    songId,
                    StringComparison.Ordinal));
            if (song is null ||
                song.lastModified == DateTime.MinValue ||
                !TryGetExactProviderLastModified(
                    song,
                    out var exactLastModified))
            {
                throw new InvalidOperationException(
                    $"Published path-repair catalog is missing exact song {songId}.");
            }

            lastModified[songId] = exactLastModified!;
        }

        return new PublishedRepairContext(
            pointers,
            catalog,
            songs,
            lastModified);
    }

    private async Task<IAsyncDisposable> RequireLeaseAsync(
        string operation,
        bool holdPublicationLock,
        CancellationToken ct)
        => await _leaseProvider.TryAcquireAsync(
                operation,
                holdPublicationLock,
                ct)
            ?? throw new InvalidOperationException(
                "Another path-repair, path-generation, publication, or ranking operation holds the required maintenance lease.");

    private static void ValidateStagedIdentity(
        PathGenerationPromotion staged,
        ValidatedPathGeneration validated)
    {
        if (!string.Equals(
                validated.Manifest.GenerationId,
                staged.ArtifactGenerationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                validated.Manifest.SongId,
                staged.SongId,
                StringComparison.Ordinal) ||
            !string.Equals(
                validated.Manifest.DatFileHash,
                staged.DatFileHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                validated.Manifest.SongLastModified,
                staged.SongLastModified,
                StringComparison.Ordinal) ||
            !string.Equals(
                validated.Manifest.ChoptVersion,
                staged.Runtime.Version,
                StringComparison.Ordinal) ||
            !string.Equals(
                validated.Manifest.ChoptBinarySha256,
                staged.Runtime.BinarySha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                validated.Manifest.GenerationProfile,
                staged.Runtime.Profile,
                StringComparison.Ordinal) ||
            !validated.Manifest.ExpectedInstruments.SequenceEqual(
                staged.ExpectedInstruments,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Staged generation {staged.ArtifactGenerationId} failed its immutable identity check.");
        }
    }

    private static void ValidateManifestGenerationIdentity(
        ImprovementNotificationMaintenanceSong expected,
        ValidatedPathGeneration validated)
    {
        var manifest = validated.Manifest;
        if (!string.Equals(
                manifest.GenerationId,
                expected.StagedArtifactGenerationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.SongId,
                expected.SongId,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.DatFileHash,
                expected.StagedDatFileHash,
                StringComparison.Ordinal) ||
            !ProviderTimestampIdentity.Equivalent(
                manifest.SongLastModified,
                expected.ExpectedCatalogLastModified) ||
            !string.Equals(
                manifest.ChoptVersion,
                expected.StagedChoptVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.ChoptBinarySha256,
                expected.StagedChoptBinarySha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.GenerationProfile,
                expected.StagedGenerationProfile,
                StringComparison.Ordinal) ||
            validated.MaxScores.MaxProLeadScore !=
                expected.ProposedProLeadMaxScore ||
            !manifest.ExpectedInstruments.SequenceEqual(
                ["Solo_PeripheralGuitar"],
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Immutable staged generation identity mismatch for {expected.SongId}.");
        }
    }

    private static PathRepairRollbackSong CreateRollbackSong(
        PathGenerationState state)
        => new(
            state.SongId,
            state.Revision,
            state.CatalogLastModified!,
            state.DatFileHash,
            state.SongLastModified,
            state.GeneratedAtUtc,
            state.ChoptVersion,
            state.ChoptBinarySha256,
            state.GenerationProfile,
            state.ArtifactGenerationId,
            state.ExpectedInstruments.ToArray(),
            new PathRepairMaxScores(
                state.MaxScores.MaxLeadScore,
                state.MaxScores.MaxBassScore,
                state.MaxScores.MaxDrumsScore,
                state.MaxScores.MaxVocalsScore,
                state.MaxScores.MaxProLeadScore,
                state.MaxScores.MaxProBassScore),
            state.PathGenerationPending);

    private static bool NonProLeadMaximaMatch(
        SongMaxScores current,
        SongMaxScores staged)
        => current.MaxLeadScore == staged.MaxLeadScore
            && current.MaxBassScore == staged.MaxBassScore
            && current.MaxDrumsScore == staged.MaxDrumsScore
            && current.MaxVocalsScore == staged.MaxVocalsScore
            && current.MaxProBassScore == staged.MaxProBassScore;

    private static bool AllMaximaAreNull(SongMaxScores scores)
        => scores.MaxLeadScore is null
            && scores.MaxBassScore is null
            && scores.MaxDrumsScore is null
            && scores.MaxVocalsScore is null
            && scores.MaxProLeadScore is null
            && scores.MaxProBassScore is null;

    private static void AddNotAttemptedPromotionReports(
        IReadOnlyList<PromotionPreflightItem> preflight,
        int startIndex,
        ICollection<PathRepairPromotionSongReport> reports)
    {
        for (var index = startIndex; index < preflight.Count; index++)
        {
            var pending = preflight[index].Promotion;
            reports.Add(new PathRepairPromotionSongReport(
                pending.SongId,
                "not_attempted",
                pending.ExpectedRevision,
                null,
                pending.ArtifactGenerationId,
                "A prior serial promotion failed."));
        }
    }

    private static void ValidatePublishedScrapeId(long scrapeId)
    {
        if (scrapeId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scrapeId),
                "Expected published scrape ID must be positive.");
        }
    }

    private static bool TryGetExactProviderLastModified(
        Song song,
        out string? lastModified)
    {
        lastModified = null;
        return song.providerJson is JsonElement
        {
            ValueKind: JsonValueKind.Object,
        } provider &&
            provider.TryGetProperty("lastModified", out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(
                lastModified = property.GetString());
    }

    private void RequireAutomaticPathGenerationDisabled()
    {
        if (_options.Value.EnableAutomaticPathGeneration)
        {
            throw new InvalidOperationException(
                "Exact-four path repair requires automatic path generation to remain disabled.");
        }
    }

    private void EnsurePromotionBoundaryStillIdle(
        long expectedPublishedScrapeId)
    {
        var pointers = _metaDatabase.GetPublicationPointerState();
        var freeze = _metaDatabase.GetPublicReadFreezeState();
        if (pointers.PublishedScrapeId != expectedPublishedScrapeId ||
            pointers.WorkingPublicationId is not null ||
            !freeze.IsFrozen ||
            freeze.ScrapeId != expectedPublishedScrapeId ||
            !string.Equals(
                freeze.Reason,
                RankingFreezeReason,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Path-repair promotion boundary changed before the next serial CAS.");
        }
    }

    private static string BoundDetail(string detail)
    {
        const int maximumLength = 2048;
        var sanitized = detail.Replace('\0', ' ');
        return sanitized.Length <= maximumLength
            ? sanitized
            : sanitized[..maximumLength];
    }

    private enum ManifestRepairPhase
    {
        PreRepair,
        PostRepair,
    }

    private sealed record PromotionPreflightItem(
        PathGenerationState CurrentState,
        PathGenerationPromotion Promotion);

    private sealed record PublishedRepairContext(
        PublicationPointerState Pointers,
        PublicationSongCatalogInfo Catalog,
        IReadOnlyList<Song> CatalogSongs,
        IReadOnlyDictionary<string, string> CatalogLastModifiedBySong);
}
