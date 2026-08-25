using System.Diagnostics;
using FortniteFestival.Core;
using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

/// <summary>
/// Publication-safe scrape-pass path ingestion (Phase B).
/// Runs once per scrape pass, after <c>StartScrapeRun</c> has captured the
/// working publication snapshot and before the scrape opens its publication
/// read scope. Generations are staged into the candidate snapshot only; live
/// <c>songs</c> rows are promoted later, inside the publication commit
/// transaction.
/// </summary>
/// <remarks>
/// Staging is best-effort infrastructure for a scrape pass, never a gate on
/// it. Every failure mode other than caller cancellation is contained here and
/// reported as an explicit result, so a staging subsystem error cannot abort
/// the scrape.
/// </remarks>
public sealed class ScrapePassPathIngestion
{
    internal const string WorkerOperationKey = "scrape.path_staging";

    private readonly PathGenerationCoordinator _coordinator;
    private readonly IPathDataStore _store;
    private readonly IMetaDatabase _meta;
    private readonly IOptions<ScraperOptions> _options;
    private readonly ILogger<ScrapePassPathIngestion> _log;
    private readonly WorkerStatusPublisher? _workerStatus;

    public ScrapePassPathIngestion(
        PathGenerationCoordinator coordinator,
        IPathDataStore store,
        IMetaDatabase meta,
        IOptions<ScraperOptions> options,
        ILogger<ScrapePassPathIngestion> log,
        WorkerStatusPublisher? workerStatus = null)
    {
        _coordinator = coordinator;
        _store = store;
        _meta = meta;
        _options = options;
        _log = log;
        _workerStatus = workerStatus;
    }

    public bool IsEnabled
    {
        get
        {
            var options = _options.Value;
            return options.EnablePathGeneration
                && options.EnableScrapePassPathGeneration;
        }
    }

    /// <summary>
    /// Stages pending path generations into the working publication snapshot.
    /// Per-song failures are warnings: the song stays pending, its candidate
    /// and live rows stay untouched, and the batch continues. Subsystem
    /// failures return an aborted result instead of throwing. Only caller
    /// cancellation propagates.
    /// </summary>
    public async Task<ScrapePassPathIngestionResult> IngestAsync(
        long scrapeId,
        long publicationId,
        IReadOnlyList<Song> catalogSongs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalogSongs);
        if (!IsEnabled)
            return ScrapePassPathIngestionResult.Disabled;

        var operationStarted = false;
        var completedNormally = false;
        try
        {
            var result = await IngestCoreAsync(
                scrapeId,
                publicationId,
                catalogSongs,
                ct,
                () => operationStarted = true);
            completedNormally = !result.Aborted;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation is a scrape-level decision, not a staging
            // failure: propagate it unchanged.
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Scrape-pass path staging failed for scrape {ScrapeId} publication {PublicationId}. "
                + "The scrape continues with the unchanged candidate snapshot.",
                scrapeId,
                publicationId);
            return new ScrapePassPathIngestionResult
            {
                Enabled = true,
                Aborted = true,
                FailureReason = ex.Message,
            };
        }
        finally
        {
            if (operationStarted)
            {
                if (completedNormally)
                {
                    _workerStatus?.CompleteOperation(WorkerOperationKey);
                }
                else
                {
                    _workerStatus?.CompleteOperation(
                        WorkerOperationKey,
                        status: "failed",
                        detail: "Path staging did not complete.");
                }
            }
        }
    }

    private async Task<ScrapePassPathIngestionResult> IngestCoreAsync(
        long scrapeId,
        long publicationId,
        IReadOnlyList<Song> catalogSongs,
        CancellationToken ct,
        Action markOperationStarted)
    {
        var options = _options.Value;
        var selection = SelectCandidates(catalogSongs, options);
        if (selection.Selected.Count == 0)
        {
            _log.LogInformation(
                "Scrape-pass path staging found nothing to stage. Pending={Pending}, Eligible={Eligible}, Selected=0.",
                selection.PendingCount,
                selection.EligibleCount);
            return new ScrapePassPathIngestionResult
            {
                Enabled = true,
                Pending = selection.PendingCount,
                Eligible = selection.EligibleCount,
            };
        }

        markOperationStarted();
        _workerStatus?.BeginOperation(
            WorkerOperationKey,
            "Staging path generations",
            phase: "Scraping",
            subOperation: "path_staging",
            detail:
                $"{selection.Selected.Count} eligible pending song(s) selected "
                + $"of {selection.PendingCount}.");

        var stopwatch = Stopwatch.StartNew();
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.ScrapePassPathGenerationTimeout);

        // Completed results survive a later song's timeout cancellation.
        var completed = new List<PathGenerationAttemptResult>();
        IReadOnlyList<PathGenerationAttemptResult> attempts;
        var timedOut = false;
        try
        {
            attempts = await _coordinator.StagePathsSerialAsync(
                selection.Selected,
                stopOnFailure: false,
                timeout.Token,
                completed);
        }
        catch (OperationCanceledException)
            when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            attempts = completed;
            _log.LogWarning(
                "Scrape-pass path staging exceeded its {Timeout} budget after {ElapsedMs:N0} ms. "
                + "{CompletedCount} of {SelectedCount} song(s) completed before the budget ran out.",
                options.ScrapePassPathGenerationTimeout,
                stopwatch.Elapsed.TotalMilliseconds,
                completed.Count,
                selection.Selected.Count);
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            await CleanupUnreferencedAttemptsBestEffortAsync(
                selection,
                completed);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Admission, provider, runtime-identity, or transport failures
            // must not abort the scrape. Keep whatever completed.
            attempts = completed;
            _log.LogError(
                ex,
                "Scrape-pass path staging aborted after {CompletedCount} of {SelectedCount} song(s). "
                + "The scrape continues with the partially updated candidate snapshot.",
                completed.Count,
                selection.Selected.Count);
            var partial = await ApplyAttemptsWithCleanupOnFailureAsync(
                scrapeId,
                publicationId,
                selection,
                attempts,
                inFlightIndex: null,
                options,
                ct);
            return partial with
            {
                Enabled = true,
                Aborted = true,
                FailureReason = ex.Message,
                Pending = selection.PendingCount,
                Eligible = selection.EligibleCount,
                Selected = selection.Selected.Count,
                Duration = stopwatch.Elapsed,
            };
        }

        var result = await ApplyAttemptsWithCleanupOnFailureAsync(
            scrapeId,
            publicationId,
            selection,
            attempts,
            inFlightIndex: timedOut && attempts.Count < selection.Selected.Count
                ? attempts.Count
                : null,
            options,
            ct);
        result = result with
        {
            Enabled = true,
            Pending = selection.PendingCount,
            Eligible = selection.EligibleCount,
            Selected = selection.Selected.Count,
            TimedOut = timedOut,
            Duration = stopwatch.Elapsed,
        };

        _log.LogInformation(
            "Scrape-pass path staging finished for scrape {ScrapeId} publication {PublicationId}. "
            + "Pending={Pending}, Eligible={Eligible}, Selected={Selected}, Staged={Staged}, Applied={Applied}, "
            + "Bootstrap={Bootstrap}, IdenticalRefresh={IdenticalRefresh}, ChangedBlocked={ChangedBlocked}, "
            + "Failed={Failed}, Conflicted={Conflicted}, Deferred={Deferred}, Remaining={Remaining}, "
            + "TimedOut={TimedOut}, ElapsedMs={ElapsedMs:N0}.",
            scrapeId,
            publicationId,
            result.Pending,
            result.Eligible,
            result.Selected,
            result.Staged,
            result.Applied,
            result.Bootstrap,
            result.IdenticalRefresh,
            result.ChangedBlocked,
            result.Failed,
            result.Conflicted,
            result.Deferred,
            result.Remaining,
            result.TimedOut,
            stopwatch.Elapsed.TotalMilliseconds);
        return result;
    }

    private async Task<ScrapePassPathIngestionResult>
        ApplyAttemptsWithCleanupOnFailureAsync(
            long scrapeId,
            long publicationId,
            CandidateSelection selection,
            IReadOnlyList<PathGenerationAttemptResult> attempts,
            int? inFlightIndex,
            ScraperOptions options,
            CancellationToken ct)
    {
        try
        {
            return await ApplyAttemptsAsync(
                scrapeId,
                publicationId,
                selection,
                attempts,
                inFlightIndex,
                options,
                ct);
        }
        catch
        {
            await CleanupUnreferencedAttemptsBestEffortAsync(
                selection,
                attempts);
            throw;
        }
    }

    private CandidateSelection SelectCandidates(
        IReadOnlyList<Song> catalogSongs,
        ScraperOptions options)
    {
        var pendingCount = _store.GetPendingPathGenerationSongIds().Count;
        if (pendingCount == 0)
            return new CandidateSelection([], 0, 0);

        var eligible = _store.GetAutomaticPathGenerationCandidates(
            DateTime.UtcNow);
        if (eligible.Count == 0)
            return new CandidateSelection([], pendingCount, 0);

        var eligibleIds = eligible
            .Select(static candidate => candidate.SongId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var liveStates = _store.GetLivePathGenerationStates();
        var selected = new List<(
            SongPathRequest Request,
            PathGenerationState State)>();
        foreach (var song in catalogSongs
            .Where(static song => song.track?.su is not null)
            .OrderBy(static song => song.track!.su, StringComparer.Ordinal))
        {
            var songId = song.track!.su!;
            if (!eligibleIds.Contains(songId))
                continue;
            if (SongPathRequest.FromSong(song) is not { } request)
                continue;

            liveStates.TryGetValue(songId, out var state);
            selected.Add((
                request,
                state ?? EmptyState(songId, request)));
            if (selected.Count >= options.ScrapePassPathGenerationMaxSongs)
                break;
        }

        return new CandidateSelection(
            selected,
            pendingCount,
            eligible.Count);
    }

    private static PathGenerationState EmptyState(
        string songId,
        SongPathRequest request) =>
        new(
            songId,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            new SongMaxScores(),
            CatalogLastModified: request.LastModified,
            PathGenerationPending: true);

    private async Task<ScrapePassPathIngestionResult> ApplyAttemptsAsync(
        long scrapeId,
        long publicationId,
        CandidateSelection selection,
        IReadOnlyList<PathGenerationAttemptResult> attempts,
        int? inFlightIndex,
        ScraperOptions options,
        CancellationToken ct)
    {
        var staged = 0;
        var applied = 0;
        var bootstrap = 0;
        var identicalRefresh = 0;
        var changedBlocked = 0;
        var failed = 0;
        var conflicted = 0;
        var deferred = 0;

        for (var index = 0; index < selection.Selected.Count; index++)
        {
            var (request, state) = selection.Selected[index];
            var catalogIdentity =
                state.CatalogLastModified ?? request.LastModified;
            if (index >= attempts.Count)
            {
                // The song that consumed the remaining budget is backed off so
                // it cannot monopolize every pass; never-attempted songs are
                // left untouched and stay first in line next pass.
                if (inFlightIndex == index)
                {
                    failed++;
                    deferred += await TryScheduleRetryAsync(
                        request.SongId,
                        "Scrape-pass staging exceeded its batch budget while "
                        + "this song was generating.",
                        catalogIdentity,
                        ct);
                }

                continue;
            }

            var attempt = attempts[index];
            var promotion = attempt.StagedPromotion;
            if (attempt.Outcome != PathGenerationAttemptOutcome.Staged
                || promotion is null)
            {
                failed++;
                _log.LogWarning(
                    "Scrape-pass path staging failed for {SongId} at {Stage}: {Detail}. The song stays pending.",
                    request.SongId,
                    attempt.FailureStage ?? "unknown",
                    attempt.Detail ?? "no detail");
                deferred += await TryScheduleRetryAsync(
                    request.SongId,
                    $"Path generation failed at {attempt.FailureStage ?? "unknown"}: "
                    + (attempt.Detail ?? "no detail"),
                    catalogIdentity,
                    ct);
                continue;
            }

            staged++;
            try
            {
                var validated =
                    PathArtifactResolver.ValidateImmutableGeneration(
                        options.DataDirectory,
                        promotion.SongId,
                        promotion.ArtifactGenerationId);
                ValidateStagedGeneration(promotion, validated);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _log.LogWarning(
                    ex,
                    "Scrape-pass path staging produced an invalid immutable generation for {SongId}. The candidate is unchanged.",
                    request.SongId);
                deferred += await TryScheduleRetryAsync(
                    request.SongId,
                    $"Staged generation validation failed: {ex.Message}",
                    catalogIdentity,
                    ct);
                await CleanupRejectedGenerationBestEffortAsync(
                    request,
                    promotion);
                continue;
            }

            var classification = Classify(state, promotion);
            if (classification == StagedGenerationKind.ChangedMaxima
                && !options.ScrapePassPathGenerationAllowChangedMaxima)
            {
                changedBlocked++;
                _log.LogWarning(
                    "Scrape-pass path staging blocked {SongId}: the existing generation's maxima changed. "
                    + "Candidate, live row, and pending flag are unchanged pending review.",
                    request.SongId);
                deferred += await TryBlockForReviewAsync(
                    request,
                    promotion,
                    catalogIdentity,
                    ct);
                await CleanupRejectedGenerationBestEffortAsync(
                    request,
                    promotion);
                continue;
            }

            PublicationPathPromotionOutcome outcome;
            try
            {
                outcome = _meta.ApplyWorkingPublicationPathPromotion(
                    new PublicationPathPromotionRequest(
                        publicationId,
                        scrapeId,
                        promotion.SongId,
                        state.Revision,
                        state.ArtifactGenerationId,
                        catalogIdentity,
                        promotion));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                conflicted++;
                _log.LogError(
                    ex,
                    "Scrape-pass path staging could not persist {SongId} into publication {PublicationId}. "
                    + "The candidate and live row are unchanged and the scrape continues.",
                    request.SongId,
                    publicationId);
                // A persistent repository failure must not head-of-line block
                // the rest of the pending catalog on every pass.
                deferred += await TryScheduleRetryAsync(
                    request.SongId,
                    $"Candidate promotion could not be persisted: {ex.Message}",
                    catalogIdentity,
                    ct);
                await CleanupRejectedGenerationBestEffortAsync(
                    request,
                    promotion);
                continue;
            }

            switch (outcome)
            {
                case PublicationPathPromotionOutcome.Applied:
                    applied++;
                    if (classification == StagedGenerationKind.Bootstrap)
                        bootstrap++;
                    else if (classification
                        == StagedGenerationKind.IdenticalMaxima)
                        identicalRefresh++;
                    // Identifies the immutable generation that becomes an
                    // orphan on disk if this candidate is later failed.
                    _log.LogInformation(
                        "Staged generation {GenerationId} for {SongId} applied to candidate publication {PublicationId} ({Classification}).",
                        promotion.ArtifactGenerationId,
                        request.SongId,
                        publicationId,
                        classification);
                    break;
                case PublicationPathPromotionOutcome.Conflict:
                case PublicationPathPromotionOutcome.SongMissing:
                case PublicationPathPromotionOutcome.PublicationNotStaging:
                default:
                    conflicted++;
                    _log.LogWarning(
                        "Scrape-pass path staging could not apply {SongId} to publication {PublicationId} ({Outcome}). "
                        + "The candidate and live row are unchanged.",
                        request.SongId,
                        publicationId,
                        outcome);
                    // A song that keeps conflicting would otherwise consume
                    // the per-pass cap forever.
                    deferred += await TryScheduleRetryAsync(
                        request.SongId,
                        $"Candidate promotion outcome was {outcome}.",
                        catalogIdentity,
                        ct);
                    await CleanupRejectedGenerationBestEffortAsync(
                        request,
                        promotion);
                    break;
            }
        }

        return new ScrapePassPathIngestionResult
        {
            Staged = staged,
            Applied = applied,
            Bootstrap = bootstrap,
            IdenticalRefresh = identicalRefresh,
            ChangedBlocked = changedBlocked,
            Failed = failed,
            Conflicted = conflicted,
            Deferred = deferred,
            Remaining = Math.Max(
                0,
                selection.PendingCount - applied),
        };
    }

    private async Task CleanupUnreferencedAttemptsBestEffortAsync(
        CandidateSelection selection,
        IReadOnlyList<PathGenerationAttemptResult> attempts)
    {
        for (var index = 0;
             index < attempts.Count
             && index < selection.Selected.Count;
             index++)
        {
            if (attempts[index].StagedPromotion is not { } promotion)
                continue;

            await CleanupRejectedGenerationBestEffortAsync(
                selection.Selected[index].Request,
                promotion);
        }
    }

    private async Task CleanupRejectedGenerationBestEffortAsync(
        SongPathRequest request,
        PathGenerationPromotion promotion)
    {
        try
        {
            if (_meta.IsPathArtifactGenerationReferenced(
                    promotion.SongId,
                    promotion.ArtifactGenerationId))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            await RecordOrphanCleanupFailureBestEffortAsync(
                request,
                promotion,
                "Could not verify whether the rejected immutable "
                + $"generation is referenced; retained it: {ex.Message}");
            _log.LogWarning(
                ex,
                "Retaining rejected path generation {GenerationId} for "
                + "{SongId} because reference verification failed.",
                promotion.ArtifactGenerationId,
                promotion.SongId);
            return;
        }

        try
        {
            var generationDirectory =
                PathArtifactResolver.GetGenerationDirectory(
                    _options.Value.DataDirectory,
                    promotion.SongId,
                    promotion.ArtifactGenerationId);
            if (Directory.Exists(generationDirectory))
            {
                Directory.Delete(
                    generationDirectory,
                    recursive: true);
            }
            _log.LogInformation(
                "Removed unreachable rejected path generation "
                + "{GenerationId} for {SongId}.",
                promotion.ArtifactGenerationId,
                promotion.SongId);
        }
        catch (Exception ex)
        {
            await RecordOrphanCleanupFailureBestEffortAsync(
                request,
                promotion,
                "Could not remove the unreachable rejected immutable "
                + $"generation: {ex.Message}");
            _log.LogWarning(
                ex,
                "Could not remove rejected path generation "
                + "{GenerationId} for {SongId}.",
                promotion.ArtifactGenerationId,
                promotion.SongId);
        }
    }

    private async Task RecordOrphanCleanupFailureBestEffortAsync(
        SongPathRequest request,
        PathGenerationPromotion promotion,
        string detail)
    {
        try
        {
            await _store.AppendPathGenerationErrorAsync(
                new PathGenerationError(
                    promotion.AttemptId,
                    promotion.SongId,
                    promotion.DatFileHash,
                    promotion.Runtime.Version,
                    promotion.Runtime.BinarySha256,
                    promotion.Runtime.Profile,
                    promotion.ExpectedInstruments,
                    "orphan_cleanup",
                    null,
                    null,
                    detail,
                    DateTime.UtcNow),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Could not append orphan-cleanup error for "
                + "{SongId}/{AttemptId}.",
                request.SongId,
                promotion.AttemptId);
        }
    }

    /// <summary>
    /// Records the blocked max-score change and durably defers the song for
    /// review. <c>path_generation_pending</c> is deliberately left true.
    /// </summary>
    private async Task<int> TryBlockForReviewAsync(
        SongPathRequest request,
        PathGenerationPromotion promotion,
        string? catalogIdentity,
        CancellationToken ct)
    {
        try
        {
            await _store.AppendPathGenerationErrorAsync(
                new PathGenerationError(
                    promotion.AttemptId,
                    promotion.SongId,
                    promotion.DatFileHash,
                    promotion.Runtime.Version,
                    promotion.Runtime.BinarySha256,
                    promotion.Runtime.Profile,
                    promotion.ExpectedInstruments,
                    PublicationPathArtifactSchema
                        .ChangedMaximaFailureStage,
                    null,
                    null,
                    "The regenerated maxima differ from the current "
                    + "published generation. Publication-safe staging "
                    + "requires explicit max-score maintenance review.",
                    DateTime.UtcNow),
                ct);
            await _store.MarkPathGenerationReviewRequiredAsync(
                promotion.SongId,
                PublicationPathArtifactSchema.ChangedMaximaFailureStage,
                catalogIdentity,
                ct);
            return 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                ex,
                "Could not durably defer the blocked max-score change for {SongId}. "
                + "It may be reattempted on the next pass.",
                request.SongId);
            return 0;
        }
    }

    private async Task<int> TryScheduleRetryAsync(
        string songId,
        string reason,
        string? catalogIdentity,
        CancellationToken ct)
    {
        try
        {
            await _store.SchedulePathGenerationRetryAsync(
                songId,
                reason,
                catalogIdentity,
                ct);
            return 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                ex,
                "Could not schedule the next automatic path staging attempt for {SongId}.",
                songId);
            return 0;
        }
    }

    internal static StagedGenerationKind Classify(
        PathGenerationState state,
        PathGenerationPromotion promotion)
    {
        if (state.ArtifactGenerationId is null
            && state.Revision == 0
            && !HasAnyMaximum(state.MaxScores))
        {
            return StagedGenerationKind.Bootstrap;
        }

        return MaximaEqual(state.MaxScores, promotion.MaxScores)
            ? StagedGenerationKind.IdenticalMaxima
            : StagedGenerationKind.ChangedMaxima;
    }

    private static bool HasAnyMaximum(SongMaxScores scores)
        => scores.MaxLeadScore.HasValue
           || scores.MaxBassScore.HasValue
           || scores.MaxDrumsScore.HasValue
           || scores.MaxVocalsScore.HasValue
           || scores.MaxProLeadScore.HasValue
           || scores.MaxProBassScore.HasValue
           || scores.MaxProCymbalsScore.HasValue
           || scores.MaxProDrumsScore.HasValue;

    private static bool MaximaEqual(SongMaxScores left, SongMaxScores right)
        => left.MaxLeadScore == right.MaxLeadScore
           && left.MaxBassScore == right.MaxBassScore
           && left.MaxDrumsScore == right.MaxDrumsScore
           && left.MaxVocalsScore == right.MaxVocalsScore
           && left.MaxProLeadScore == right.MaxProLeadScore
           && left.MaxProBassScore == right.MaxProBassScore
           && left.MaxProCymbalsScore == right.MaxProCymbalsScore
           && left.MaxProDrumsScore == right.MaxProDrumsScore;

    private static void ValidateStagedGeneration(
        PathGenerationPromotion promotion,
        ValidatedPathGeneration validated)
    {
        if (!string.Equals(
                validated.Manifest.GenerationId,
                promotion.ArtifactGenerationId,
                StringComparison.Ordinal)
            || !string.Equals(
                validated.Manifest.SongId,
                promotion.SongId,
                StringComparison.Ordinal)
            || !string.Equals(
                validated.Manifest.DatFileHash,
                promotion.DatFileHash,
                StringComparison.Ordinal)
            || !ProviderTimestampIdentity.Equivalent(
                validated.Manifest.SongLastModified,
                promotion.SongLastModified)
            || !string.Equals(
                validated.Manifest.ChoptVersion,
                promotion.Runtime.Version,
                StringComparison.Ordinal)
            || !string.Equals(
                validated.Manifest.ChoptBinarySha256,
                promotion.Runtime.BinarySha256,
                StringComparison.Ordinal)
            || !string.Equals(
                validated.Manifest.GenerationProfile,
                promotion.Runtime.Profile,
                StringComparison.Ordinal)
            || !validated.Manifest.ExpectedInstruments.SequenceEqual(
                promotion.ExpectedInstruments,
                StringComparer.Ordinal)
            || !MaximaEqual(validated.MaxScores, promotion.MaxScores))
        {
            throw new InvalidOperationException(
                $"Staged immutable generation identity mismatch for {promotion.SongId}.");
        }
    }

    private sealed record CandidateSelection(
        IReadOnlyList<(
            SongPathRequest Request,
            PathGenerationState State)> Selected,
        int PendingCount,
        int EligibleCount);
}

internal enum StagedGenerationKind
{
    Bootstrap,
    IdenticalMaxima,
    ChangedMaxima,
}

/// <summary>
/// Per-pass staging counters. <see cref="Applied"/> counts candidate snapshot
/// updates only; nothing here has touched live <c>songs</c> rows.
/// </summary>
public sealed record ScrapePassPathIngestionResult
{
    public static readonly ScrapePassPathIngestionResult Disabled = new();

    public bool Enabled { get; init; }
    public int Pending { get; init; }

    /// <summary>Pending songs not deferred for review or backoff.</summary>
    public int Eligible { get; init; }

    public int Selected { get; init; }
    public int Staged { get; init; }
    public int Applied { get; init; }
    public int Bootstrap { get; init; }
    public int IdenticalRefresh { get; init; }
    public int ChangedBlocked { get; init; }
    public int Failed { get; init; }
    public int Conflicted { get; init; }

    /// <summary>Songs durably deferred for review or retry this pass.</summary>
    public int Deferred { get; init; }

    public int Remaining { get; init; }
    public bool TimedOut { get; init; }

    /// <summary>
    /// True when a staging subsystem failure ended the batch early. The scrape
    /// still continues with whatever the candidate snapshot already holds.
    /// </summary>
    public bool Aborted { get; init; }

    public string? FailureReason { get; init; }
    public TimeSpan Duration { get; init; }
}
