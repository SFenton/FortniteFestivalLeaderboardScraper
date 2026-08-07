using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FSTService.Scraping;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

public sealed partial class MetaDatabase
{
    private static readonly Regex PublicationBandArtifactNamePattern =
        new(
            @"^(?<family>btr|btrs)_(?<kind>pubprep|retained)_(?<publicationId>[0-9]+)_(?<bandType>band_duets|band_trios|band_quad)$",
            RegexOptions.CultureInvariant
            | RegexOptions.Compiled);
    internal Action<PublicationPreparationResult>?
        PublicationPreparedTestHook { get; set; }
    internal Action? FailureIsolationTestHook { get; set; }
    internal Func<Exception?>?
        DeferredPreparationReadTestHook { get; set; }
    internal Func<Exception?>?
        PublicationCommitTestHook { get; set; }
    internal Action? DeferredTransitionTestHook { get; set; }
    internal Action? IsolationPendingTransitionTestHook { get; set; }

    public void PublishScrapeRun(
        long scrapeId,
        bool promoteCachedResponses = true,
        int? expectedPublishedScopeCount = null,
        bool queueImprovementNotifications = false,
        IReadOnlyCollection<SoloCurrentProjectionScopeKey>?
            improvementNotificationProjectionScopes = null)
    {
        var preparation = PrepareScrapePublication(
            scrapeId,
            promoteCachedResponses,
            expectedPublishedScopeCount,
            queueImprovementNotifications,
            improvementNotificationProjectionScopes);
        PublicationPreparedTestHook?.Invoke(preparation);
        if (preparation.AlreadyPublished)
        {
            ClearAlreadyPublishedCommitIntent(scrapeId);
            return;
        }

        var previousFreezeState = GetPublicReadFreezeState();
        PublicationCommitIntentHandle? commitIntent = null;
        var committed = false;
        try
        {
            commitIntent =
                BeginPublicationCommitIntent(scrapeId);
            var commit =
                CommitPreparedScrapePublication(
                    preparation,
                    commitIntent);
            committed = !commit.AlreadyPublished;
            try
            {
                CleanupPublishedScrapePublication(
                    preparation,
                    commit);
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Publication {PublicationId} committed, but post-commit artifact cleanup failed.",
                    commit.PublicationId);
                _ = SweepPublicationBandTableOrphans();
            }
        }
        finally
        {
            if (commitIntent is not null && !committed)
            {
                TryRestorePublicationCommitIntent(
                    commitIntent,
                    previousFreezeState);
            }
        }
    }

    public void FailScrapeRun(
        long scrapeId,
        string phase,
        string message,
        PublicationCommitIntentHandle?
            existingCommitIntent = null)
    {
        if (scrapeId <= 0)
            return;

        ValidatePublicationCommitOptions();
        var previousFreezeState = GetPublicReadFreezeState();
        var ownsCommitIntent =
            existingCommitIntent is null;
        PublicationCommitIntentHandle? commitIntent =
            existingCommitIntent;
        var durableFailureRecorded = false;
        try
        {
            commitIntent ??=
                BeginPublicationCommitIntent(scrapeId);
            FailureIsolationTestHook?.Invoke();
            MarkFailedScrapeCandidateDurably(
                scrapeId,
                phase,
                message);
            durableFailureRecorded = true;
            using (var schemaConnection = _ds.OpenConnection())
                EnsureScrapePublicationStateTable(schemaConnection);

            var drainStopwatch = Stopwatch.StartNew();
            var lockRejections = 0;
            while (true)
            {
                HeartbeatPublicationCommitIntent(commitIntent);
                using var conn = _ds.OpenConnection();
                using var tx = conn.BeginTransaction();
                ApplyPublicationCommitTimeouts(conn, tx);
                if (!TryAcquirePublicationAdvisoryLock(
                        conn,
                        tx,
                        shared: false))
                {
                    tx.Rollback();
                    lockRejections++;
                    if (drainStopwatch.ElapsedMilliseconds
                        >= _publicationCommitOptions
                            .DrainTimeoutMilliseconds)
                    {
                        RecordFailedScrapeWithDegradedIsolation(
                            scrapeId,
                            phase,
                            message,
                            commitIntent);
                        drainStopwatch.Stop();
                        _log.LogWarning(
                            "Failed scrape {ScrapeId} used degraded shared-lock isolation after {DrainElapsedMs:N3} ms and {LockRejections} exclusive-lock rejection(s).",
                            scrapeId,
                            drainStopwatch.Elapsed.TotalMilliseconds,
                            lockRejections);
                        break;
                    }

                    Thread.Sleep(
                        _publicationCommitOptions
                            .RetryDelayMilliseconds);
                    continue;
                }

                using (var scrape = conn.CreateCommand())
                {
                    scrape.Transaction = tx;
                    scrape.CommandText = """
                    UPDATE scrape_log
                    SET status = 'failed',
                        failed_at = COALESCE(failed_at, @now),
                        failure_phase = @phase,
                        failure_message = @message
                    WHERE id = @scrapeId
                      AND NOT EXISTS (
                          SELECT 1
                          FROM scrape_publication_state
                          WHERE id = TRUE
                            AND published_scrape_id = @scrapeId
                      )
                    """;
                    scrape.Parameters.AddWithValue("now", DateTime.UtcNow);
                    scrape.Parameters.AddWithValue("phase", phase);
                    scrape.Parameters.AddWithValue("message", message);
                    scrape.Parameters.AddWithValue(
                        "scrapeId",
                        checked((int)scrapeId));
                    scrape.ExecuteNonQuery();
                }

                using (var generation = conn.CreateCommand())
                {
                    generation.Transaction = tx;
                    generation.CommandText = """
                    UPDATE publication_generations
                    SET status = 'failed',
                        failed_at = COALESCE(failed_at, @now),
                        failure_phase = @phase,
                        failure_message = @message
                    WHERE scrape_id = @scrapeId
                      AND status NOT IN (
                          'current',
                          'retained',
                          'retired')
                    """;
                    generation.Parameters.AddWithValue(
                        "now",
                        DateTime.UtcNow);
                    generation.Parameters.AddWithValue("phase", phase);
                    generation.Parameters.AddWithValue("message", message);
                    generation.Parameters.AddWithValue(
                        "scrapeId",
                        scrapeId);
                    generation.ExecuteNonQuery();
                }

                using (var pointer = conn.CreateCommand())
                {
                    pointer.Transaction = tx;
                    pointer.CommandText = """
                    UPDATE scrape_publication_state publication
                    SET working_publication_id = NULL,
                        updated_at = @now
                    FROM publication_generations generation
                    WHERE publication.id = TRUE
                      AND generation.scrape_id = @scrapeId
                      AND publication.working_publication_id =
                            generation.publication_id
                    """;
                    pointer.Parameters.AddWithValue(
                        "now",
                        DateTime.UtcNow);
                    pointer.Parameters.AddWithValue(
                        "scrapeId",
                        scrapeId);
                    pointer.ExecuteNonQuery();
                }

                tx.Commit();
                drainStopwatch.Stop();
                _log.LogInformation(
                    "Recorded failed publication candidate for scrape {ScrapeId} after {DrainElapsedMs:N3} ms with {LockRejections} nonqueueing lock rejection(s).",
                    scrapeId,
                    drainStopwatch.Elapsed.TotalMilliseconds,
                    lockRejections);
                break;
            }

            try
            {
                CleanupFailedPublicationArtifacts(scrapeId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Failed scrape {ScrapeId} was durably isolated, but candidate artifact cleanup did not complete.",
                    scrapeId);
            }
            _ = SweepPublicationBandTableOrphans();
        }
        finally
        {
            if (commitIntent is not null
                && ownsCommitIntent)
            {
                if (durableFailureRecorded)
                {
                    var restoreState =
                        previousFreezeState.PublicationCommitDeferred
                        || previousFreezeState
                            .PublicationFailureIsolationPending
                            ? PublicReadFreezeState.NotFrozen
                            : previousFreezeState;
                    TryRestorePublicationCommitIntent(
                        commitIntent,
                        restoreState);
                }
                else
                {
                    try
                    {
                        TransitionPublicationCommitIntentToIsolationPending(
                            commitIntent);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(
                            ex,
                            "Failure isolation for scrape {ScrapeId} was not durable; retaining the owned publication commit latch for cross-process fail-closed recovery.",
                            scrapeId);
                    }
                }
            }
        }
    }

    public PublicationPreparationResult PrepareScrapePublication(
        long scrapeId,
        bool promoteCachedResponses = true,
        int? expectedPublishedScopeCount = null,
        bool queueImprovementNotifications = false,
        IReadOnlyCollection<SoloCurrentProjectionScopeKey>?
            improvementNotificationProjectionScopes = null)
    {
        ValidatePublicationCommitOptions();
        if (queueImprovementNotifications
            && improvementNotificationProjectionScopes is null)
        {
            throw new InvalidOperationException(
                "Improvement notification publication requires a persisted projection scope plan.");
        }

        var notificationProjectionScopes =
            (improvementNotificationProjectionScopes ?? [])
            .Where(static scope =>
                !string.IsNullOrWhiteSpace(scope.SongId)
                && !string.IsNullOrWhiteSpace(scope.Instrument))
            .Distinct()
            .OrderBy(static scope => scope.SongId, StringComparer.Ordinal)
            .ThenBy(static scope => scope.Instrument, StringComparer.Ordinal)
            .ToArray();
        var notificationProjectionScopesJson =
            JsonSerializer.Serialize(notificationProjectionScopes);
        _ = SweepPublicationBandTableOrphans();
        var prepareStopwatch = Stopwatch.StartNew();
        var preparedAt = DateTime.UtcNow;

        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var tx = conn.BeginTransaction();
        ApplyPublicationPreparationTimeouts(conn, tx);
        if (!TryAcquirePublicationAdvisoryLock(conn, tx, shared: true))
        {
            throw new PublicationCommitBusyException(
                $"Scrape {scrapeId} publication preparation was rejected because a publication writer is active.",
                prepareStopwatch.Elapsed,
                lockRejections: 1,
                relationLockRetries: 0);
        }

        var state = ReadPublicationStateForPreparation(conn, tx);
        if (state.PublishedScrapeId == scrapeId
            && state.CurrentPublicationId.HasValue)
        {
            var currentBandProjectionGeneration =
                ReadBandProjectionGeneration(conn, tx);
            tx.Commit();
            prepareStopwatch.Stop();
            return new PublicationPreparationResult(
                scrapeId,
                state.CurrentPublicationId.Value,
                state.CurrentPublicationId,
                state.PreviousPublicationId,
                promoteCachedResponses,
                expectedPublishedScopeCount,
                queueImprovementNotifications,
                notificationProjectionScopesJson,
                notificationProjectionScopes.Length,
                currentBandProjectionGeneration,
                preparedAt,
                prepareStopwatch.Elapsed,
                AlreadyPublished: true);
        }

        ValidateNotificationGate(
            scrapeId,
            state.PublishedScrapeId,
            state.NotificationScrapeId,
            state.NotificationStatus);
        VerifyCompletedScrape(conn, tx, scrapeId);

        var publicationId = EnsureWorkingPublicationGeneration(
            conn,
            tx,
            scrapeId);
        var workingPublicationId = state.WorkingPublicationId;
        if (!workingPublicationId.HasValue)
        {
            workingPublicationId = AdoptWorkingPublication(
                conn,
                tx,
                publicationId);
        }

        if (workingPublicationId != publicationId)
        {
            throw new InvalidOperationException(
                $"Publication generation {publicationId} does not own the working pointer " +
                $"({workingPublicationId?.ToString() ?? "null"}).");
        }

        VerifyPublicationCatalog(conn, tx, publicationId);
        if (promoteCachedResponses)
            VerifyPublicationCacheStaging(conn, tx, scrapeId, publicationId);
        if (expectedPublishedScopeCount.HasValue)
        {
            ValidatePublishedScopeSources(
                conn,
                tx,
                scrapeId,
                expectedPublishedScopeCount.Value);
        }

        var bandProjectionGeneration =
            ReadBandProjectionGeneration(conn, tx);
        var preparedBandTables = PreparePublishedBandTables(
            conn,
            tx,
            publicationId);
        PreparePublicationCache(
            conn,
            tx,
            publicationId,
            state.CurrentPublicationId,
            promoteCachedResponses);
        PreparePublicationSurfaceBindings(
            conn,
            tx,
            scrapeId,
            publicationId,
            state.CurrentPublicationId,
            promoteCachedResponses,
            expectedPublishedScopeCount,
            queueImprovementNotifications,
            notificationProjectionScopes.Length,
            bandProjectionGeneration,
            preparedBandTables);
        var preparationMetadata = JsonSerializer.Serialize(new
        {
            scrapeId,
            publicationId,
            currentPublicationId = state.CurrentPublicationId,
            previousPublicationId = state.PreviousPublicationId,
            promoteCachedResponses,
            expectedPublishedScopeCount,
            queueImprovementNotifications,
            improvementNotificationProjectionScopes =
                notificationProjectionScopes,
            improvementNotificationProjectionScopeCount =
                notificationProjectionScopes.Length,
            bandProjectionGeneration,
            preparedAtUtc = preparedAt,
        });

        using (var ready = conn.CreateCommand())
        {
            ready.Transaction = tx;
            ready.CommandText = """
                UPDATE publication_generations
                SET status = 'ready',
                    ready_at = @now,
                    metadata = metadata
                        || jsonb_build_object(
                            'publicationPreparation',
                            @preparationMetadata),
                    failed_at = NULL,
                    failure_phase = NULL,
                    failure_message = NULL
                WHERE publication_id = @publicationId
                  AND status IN ('building', 'ready')
                """;
            ready.Parameters.AddWithValue("now", DateTime.UtcNow);
            ready.Parameters.AddWithValue("publicationId", publicationId);
            ready.Parameters.Add(
                "preparationMetadata",
                NpgsqlDbType.Jsonb).Value =
                preparationMetadata;
            if (ready.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"Publication generation {publicationId} could not be marked ready.");
            }
        }

        tx.Commit();
        prepareStopwatch.Stop();
        _log.LogInformation(
            "Prepared publication {PublicationId} for scrape {ScrapeId} outside the exclusive publication lock in {PrepareElapsedMs:N3} ms. BandTables={BandTableCount}, CachePromoted={CachePromoted}.",
            publicationId,
            scrapeId,
            prepareStopwatch.Elapsed.TotalMilliseconds,
            preparedBandTables.Count,
            promoteCachedResponses);

        return new PublicationPreparationResult(
            scrapeId,
            publicationId,
            state.CurrentPublicationId,
            state.PreviousPublicationId,
            promoteCachedResponses,
            expectedPublishedScopeCount,
            queueImprovementNotifications,
            notificationProjectionScopesJson,
            notificationProjectionScopes.Length,
            bandProjectionGeneration,
            preparedAt,
            prepareStopwatch.Elapsed);
    }

    public PublicationPreparationResult?
        GetDeferredPublicationPreparation()
    {
        var injectedFailure =
            DeferredPreparationReadTestHook?.Invoke();
        if (injectedFailure is not null)
            throw injectedFailure;

        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT
                generation.scrape_id,
                generation.publication_id,
                publication.current_publication_id,
                publication.previous_publication_id,
                generation.metadata
                    -> 'publicationPreparation'
            FROM scrape_publication_state publication
            JOIN publication_generations generation
              ON generation.publication_id =
                    publication.working_publication_id
            WHERE publication.id = TRUE
              AND publication.public_reads_frozen_reason =
                    @deferredReason
              AND generation.status = 'ready'
            """;
        command.Parameters.AddWithValue(
            "deferredReason",
            PublicReadFreezeState.PublicationCommitDeferredReason);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var scrapeId = reader.GetInt64(0);
        var publicationId = reader.GetInt64(1);
        long? currentPublicationId = reader.IsDBNull(2)
            ? null
            : reader.GetInt64(2);
        long? previousPublicationId = reader.IsDBNull(3)
            ? null
            : reader.GetInt64(3);
        if (reader.IsDBNull(4))
        {
            throw new DeferredPublicationMetadataException(
                $"Deferred publication {publicationId} has no persisted preparation metadata.");
        }

        try
        {
            using var metadata = JsonDocument.Parse(
                reader.GetString(4));
            var root = metadata.RootElement;
            if (root.GetProperty("scrapeId").GetInt64() != scrapeId
                || root.GetProperty("publicationId").GetInt64()
                    != publicationId)
            {
                throw new DeferredPublicationMetadataException(
                    $"Deferred publication {publicationId} preparation metadata does not match its generation.");
            }

            var expectedPublishedScopeCount =
                root.TryGetProperty(
                        "expectedPublishedScopeCount",
                        out var expected)
                    && expected.ValueKind != JsonValueKind.Null
                    ? expected.GetInt32()
                    : (int?)null;
            var bandProjectionGeneration =
                root.TryGetProperty(
                        "bandProjectionGeneration",
                        out var bandGeneration)
                    && bandGeneration.ValueKind != JsonValueKind.Null
                    ? bandGeneration.GetInt64()
                    : (long?)null;
            var projectionScopes =
                root.GetProperty(
                        "improvementNotificationProjectionScopes")
                    .GetRawText();
            return new PublicationPreparationResult(
                scrapeId,
                publicationId,
                currentPublicationId,
                previousPublicationId,
                root.GetProperty(
                        "promoteCachedResponses")
                    .GetBoolean(),
                expectedPublishedScopeCount,
                root.GetProperty(
                        "queueImprovementNotifications")
                    .GetBoolean(),
                projectionScopes,
                root.GetProperty(
                        "improvementNotificationProjectionScopeCount")
                    .GetInt32(),
                bandProjectionGeneration,
                root.GetProperty("preparedAtUtc").GetDateTime(),
                PrepareDuration: TimeSpan.Zero);
        }
        catch (DeferredPublicationMetadataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is
            JsonException
            or KeyNotFoundException
            or InvalidOperationException
            or FormatException)
        {
            throw new DeferredPublicationMetadataException(
                $"Deferred publication {publicationId} preparation metadata is invalid.",
                ex);
        }
    }

    public PublicationCommitResult CommitPreparedScrapePublication(
        PublicationPreparationResult preparation,
        PublicationCommitIntentHandle? commitIntent = null)
    {
        ValidatePublicationCommitOptions();
        if (preparation.AlreadyPublished)
        {
            return new PublicationCommitResult(
                preparation.ScrapeId,
                preparation.PublicationId,
                preparation.CurrentPublicationId,
                TimeSpan.Zero,
                TimeSpan.Zero,
                0,
                0,
                AlreadyPublished: true);
        }

        var injectedFailure = PublicationCommitTestHook?.Invoke();
        if (injectedFailure is not null)
            throw injectedFailure;

        using (var schemaConnection = _ds.OpenConnection())
            EnsureScrapePublicationStateTable(schemaConnection);

        var drainStopwatch = Stopwatch.StartNew();
        var lockRejections = 0;
        var relationLockRetries = 0;
        Stopwatch? cutoverStopwatch = null;

        while (true)
        {
            if (commitIntent is not null)
                HeartbeatPublicationCommitIntent(commitIntent);
            EnsurePublicationCutoverBudget(
                preparation,
                cutoverStopwatch,
                relationLockRetries);
            using var conn = _ds.OpenConnection();
            using var tx = conn.BeginTransaction();
            ApplyPublicationCommitTimeouts(conn, tx);

            if (!TryAcquirePublicationAdvisoryLock(
                    conn,
                    tx,
                    shared: false))
            {
                tx.Rollback();
                lockRejections++;
                DelayPublicationCommitRetryOrThrow(
                    preparation,
                    drainStopwatch,
                    cutoverStopwatch,
                    lockRejections,
                    relationLockRetries);
                continue;
            }

            cutoverStopwatch ??= Stopwatch.StartNew();
            ApplyPublicationCutoverBudget(
                conn,
                tx,
                GetRemainingPublicationCutoverBudget(
                    preparation,
                    cutoverStopwatch,
                    relationLockRetries));
            var drainDuration = drainStopwatch.Elapsed;
            var attemptStopwatch = Stopwatch.StartNew();
            try
            {
                var alreadyPublished =
                    CommitPreparedPublicationTransaction(
                        conn,
                        tx,
                        preparation);
                tx.Commit();
                attemptStopwatch.Stop();
                cutoverStopwatch.Stop();
                drainStopwatch.Stop();

                if (cutoverStopwatch.Elapsed.TotalMilliseconds
                    > _publicationCommitOptions
                        .MaxExclusiveLockDurationMilliseconds)
                {
                    _log.LogCritical(
                        "Publication {PublicationId} committed after its enforced {TargetMs} ms cutover budget: {ExclusiveElapsedMs:N3} ms.",
                        preparation.PublicationId,
                        _publicationCommitOptions
                            .MaxExclusiveLockDurationMilliseconds,
                        cutoverStopwatch.Elapsed.TotalMilliseconds);
                }

                _log.LogInformation(
                    "Committed publication {PublicationId} for scrape {ScrapeId}. DrainElapsedMs={DrainElapsedMs:N3}, ExclusiveLockElapsedMs={ExclusiveElapsedMs:N3}, LockRejections={LockRejections}, RelationLockRetries={RelationLockRetries}, AlreadyPublished={AlreadyPublished}.",
                    preparation.PublicationId,
                    preparation.ScrapeId,
                    drainDuration.TotalMilliseconds,
                    cutoverStopwatch.Elapsed.TotalMilliseconds,
                    lockRejections,
                    relationLockRetries,
                    alreadyPublished);

                return new PublicationCommitResult(
                    preparation.ScrapeId,
                    preparation.PublicationId,
                    preparation.CurrentPublicationId,
                    drainDuration,
                    cutoverStopwatch.Elapsed,
                    lockRejections,
                    relationLockRetries,
                    alreadyPublished);
            }
            catch (PostgresException ex)
                when (IsRetryablePublicationRelationLockFailure(ex))
            {
                attemptStopwatch.Stop();
                TryRollback(tx);
                relationLockRetries++;
                _log.LogWarning(
                    ex,
                    "Publication {PublicationId} final swap hit a bounded relation-lock timeout after {ExclusiveElapsedMs:N3} ms; retrying from a fresh transaction.",
                    preparation.PublicationId,
                    attemptStopwatch.Elapsed.TotalMilliseconds);
                DelayPublicationCommitRetryOrThrow(
                    preparation,
                    drainStopwatch,
                    cutoverStopwatch,
                    lockRejections,
                    relationLockRetries);
            }
            catch (PostgresException ex)
                when (ex.SqlState == "25P04")
            {
                attemptStopwatch.Stop();
                TryRollback(tx);
                cutoverStopwatch.Stop();
                var budget = TimeSpan.FromMilliseconds(
                    _publicationCommitOptions
                        .MaxExclusiveLockDurationMilliseconds);
                throw new PublicationCommitDeadlineExceededException(
                    $"Publication {preparation.PublicationId} exceeded its cumulative {budget} final-cutover budget.",
                    cutoverStopwatch.Elapsed,
                    budget,
                    relationLockRetries,
                    ex);
            }
        }
    }

    public void CleanupPublishedScrapePublication(
        PublicationPreparationResult preparation,
        PublicationCommitResult commit)
    {
        if (commit.AlreadyPublished)
        {
            _ = SweepPublicationBandTableOrphans();
            return;
        }

        var cleanupStopwatch = Stopwatch.StartNew();
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        ApplyPublicationCleanupTimeouts(conn, tx);
        if (!TryAcquirePublicationAdvisoryLock(conn, tx, shared: true))
        {
            tx.Rollback();
            _log.LogWarning(
                "Skipped cleanup for publication {PublicationId} because another publication writer became active.",
                commit.PublicationId);
            _ = SweepPublicationBandTableOrphans();
            return;
        }

        using (var cacheBuildLock = conn.CreateCommand())
        {
            cacheBuildLock.Transaction = tx;
            cacheBuildLock.CommandText =
                "SELECT pg_try_advisory_xact_lock(@lockKey)";
            cacheBuildLock.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema
                    .CacheBuildAdvisoryLockBase
                + commit.PublicationId);
            if (cacheBuildLock.ExecuteScalar() is not true)
            {
                tx.Rollback();
                _log.LogWarning(
                    "Deferred cleanup for publication {PublicationId} because its cache build lease is active.",
                    commit.PublicationId);
                _ = SweepPublicationBandTableOrphans();
                return;
            }
        }

        using (var verify = conn.CreateCommand())
        {
            verify.Transaction = tx;
            verify.CommandText = """
                SELECT current_publication_id = @publicationId
                FROM scrape_publication_state
                WHERE id = TRUE
                """;
            verify.Parameters.AddWithValue(
                "publicationId",
                commit.PublicationId);
            if (verify.ExecuteScalar() is not bool isCurrent || !isCurrent)
            {
                tx.Rollback();
                _ = SweepPublicationBandTableOrphans();
                return;
            }
        }

        using (var legacyCache = conn.CreateCommand())
        {
            legacyCache.Transaction = tx;
            legacyCache.CommandText = """
                TRUNCATE api_response_cache;
                INSERT INTO api_response_cache (
                    cache_key, json_data, etag, cached_at)
                SELECT cache_key, json_data, etag, cached_at
                FROM publication_api_response_cache
                WHERE publication_id = @publicationId;
                TRUNCATE api_response_cache_staging;
                DELETE FROM publication_api_response_cache_staging
                WHERE publication_id = @publicationId;
                """;
            legacyCache.Parameters.AddWithValue(
                "publicationId",
                commit.PublicationId);
            legacyCache.ExecuteNonQuery();
        }

        RetainPublicationArtifacts(
            conn,
            tx,
            commit.PublicationId,
            commit.PreviousPublicationId);
        DropPreparedBandTables(conn, tx, commit.PublicationId);

        var staleRetainedPublicationId =
            preparation.PreviousPublicationId
            ?? (preparation.CurrentPublicationId.HasValue ? null : 0);
        if (staleRetainedPublicationId.HasValue)
        {
            DropRetainedBandTables(
                conn,
                tx,
                staleRetainedPublicationId.Value);
        }

        tx.Commit();
        cleanupStopwatch.Stop();
        _log.LogInformation(
            "Cleaned publication {PublicationId} compatibility cache and retired artifacts in {CleanupElapsedMs:N3} ms.",
            commit.PublicationId,
            cleanupStopwatch.Elapsed.TotalMilliseconds);
        _ = SweepPublicationBandTableOrphans();
    }

    public PublicationBandOrphanSweepResult
        SweepPublicationBandTableOrphans()
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var tx = conn.BeginTransaction();
        ApplyPublicationCleanupTimeouts(conn, tx);
        if (!TryAcquirePublicationAdvisoryLock(
                conn,
                tx,
                shared: true))
        {
            tx.Rollback();
            return new PublicationBandOrphanSweepResult(
                LockAcquired: false,
                Completed: false,
                ExaminedTableCount: 0,
                DroppedTables: []);
        }

        try
        {
            using (var clearFailedWorking = conn.CreateCommand())
            {
                clearFailedWorking.Transaction = tx;
                clearFailedWorking.CommandText = """
                    UPDATE scrape_publication_state publication
                    SET working_publication_id = NULL,
                        updated_at = now()
                    FROM publication_generations generation
                    WHERE publication.id = TRUE
                      AND generation.publication_id =
                            publication.working_publication_id
                      AND generation.status = 'failed'
                    """;
                clearFailedWorking.ExecuteNonQuery();
            }

            long? currentPublicationId;
            long? previousPublicationId;
            long? activeWorkingPublicationId;
            var hasPublicationState = false;
            using (var pointers = conn.CreateCommand())
            {
                pointers.Transaction = tx;
                pointers.CommandText = """
                    SELECT
                        publication.current_publication_id,
                        publication.previous_publication_id,
                        CASE
                            WHEN generation.status IN (
                                'building',
                                'ready')
                                THEN publication.working_publication_id
                            ELSE NULL
                        END
                    FROM scrape_publication_state publication
                    LEFT JOIN publication_generations generation
                      ON generation.publication_id =
                            publication.working_publication_id
                    WHERE publication.id = TRUE
                    """;
                using var reader = pointers.ExecuteReader();
                hasPublicationState = reader.Read();
                if (!hasPublicationState)
                {
                    currentPublicationId = null;
                    previousPublicationId = null;
                    activeWorkingPublicationId = null;
                }
                else
                {
                    currentPublicationId =
                        reader.IsDBNull(0)
                            ? null
                            : reader.GetInt64(0);
                    previousPublicationId =
                        reader.IsDBNull(1)
                            ? null
                            : reader.GetInt64(1);
                    activeWorkingPublicationId =
                        reader.IsDBNull(2)
                            ? null
                            : reader.GetInt64(2);
                }
            }

            if (!hasPublicationState)
            {
                using var fallback = conn.CreateCommand();
                fallback.Transaction = tx;
                fallback.CommandText = """
                    SELECT
                        (
                            SELECT publication_id
                            FROM publication_generations
                            WHERE status = 'current'
                            ORDER BY publication_id DESC
                            LIMIT 1
                        ),
                        (
                            SELECT previous_publication_id
                            FROM publication_generations
                            WHERE status = 'current'
                            ORDER BY publication_id DESC
                            LIMIT 1
                        ),
                        (
                            SELECT publication_id
                            FROM publication_generations
                            WHERE status IN ('building', 'ready')
                            ORDER BY publication_id DESC
                            LIMIT 1
                        )
                    """;
                using var reader = fallback.ExecuteReader();
                if (reader.Read())
                {
                    currentPublicationId =
                        reader.IsDBNull(0)
                            ? null
                            : reader.GetInt64(0);
                    previousPublicationId =
                        reader.IsDBNull(1)
                            ? null
                            : reader.GetInt64(1);
                    activeWorkingPublicationId =
                        reader.IsDBNull(2)
                            ? null
                            : reader.GetInt64(2);
                }
            }

            var artifactTables = new List<string>();
            using (var inventory = conn.CreateCommand())
            {
                inventory.Transaction = tx;
                inventory.CommandText = """
                    SELECT relation.relname
                    FROM pg_class relation
                    JOIN pg_namespace namespace
                      ON namespace.oid =
                            relation.relnamespace
                    WHERE namespace.nspname = 'public'
                      AND relation.relkind = 'r'
                      AND (
                          relation.relname LIKE
                              'btr_pubprep_%'
                          OR relation.relname LIKE
                              'btrs_pubprep_%'
                          OR relation.relname LIKE
                              'btr_retained_%'
                          OR relation.relname LIKE
                              'btrs_retained_%'
                      )
                    ORDER BY relation.relname
                    """;
                using var reader = inventory.ExecuteReader();
                while (reader.Read())
                    artifactTables.Add(reader.GetString(0));
            }

            var dropped = new List<string>();
            foreach (var tableName in artifactTables)
            {
                var match =
                    PublicationBandArtifactNamePattern.Match(
                        tableName);
                if (!match.Success
                    || !long.TryParse(
                        match.Groups["publicationId"].Value,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo
                            .InvariantCulture,
                        out var publicationId))
                {
                    continue;
                }

                var kind = match.Groups["kind"].Value;
                var keep = kind == "pubprep"
                    ? activeWorkingPublicationId == publicationId
                    : currentPublicationId == publicationId
                      || previousPublicationId == publicationId;
                if (keep)
                    continue;

                using var drop = conn.CreateCommand();
                drop.Transaction = tx;
                drop.CommandText =
                    $"DROP TABLE IF EXISTS {BandRankingStorageNames.QuoteIdentifier(tableName)}";
                drop.ExecuteNonQuery();
                dropped.Add(tableName);
            }

            tx.Commit();
            if (dropped.Count > 0)
            {
                _log.LogInformation(
                    "Dropped {Count} orphan publication band table(s): {Tables}.",
                    dropped.Count,
                    string.Join(", ", dropped));
            }
            return new PublicationBandOrphanSweepResult(
                LockAcquired: true,
                Completed: true,
                ExaminedTableCount: artifactTables.Count,
                DroppedTables: dropped);
        }
        catch (PostgresException ex)
            when (ex.SqlState is
                PostgresErrorCodes.LockNotAvailable
                or PostgresErrorCodes.QueryCanceled
                or PostgresErrorCodes.DeadlockDetected)
        {
            TryRollback(tx);
            _log.LogWarning(
                ex,
                "Publication band orphan sweep deferred because a candidate table remained locked.");
            return new PublicationBandOrphanSweepResult(
                LockAcquired: true,
                Completed: false,
                ExaminedTableCount: 0,
                DroppedTables: []);
        }
    }

    public PublicationCommitIntentHandle BeginPublicationCommitIntent(
        long scrapeId)
    {
        var now = DateTime.UtcNow;
        var commitIntent = new PublicationCommitIntentHandle(
            scrapeId,
            Guid.NewGuid().ToString("N"),
            now);
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var tx = conn.BeginTransaction();
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            WITH current_generation AS (
                SELECT
                    publication_id,
                    scrape_id,
                    previous_publication_id,
                    published_at
                FROM publication_generations
                WHERE status = 'current'
                ORDER BY publication_id DESC
                LIMIT 1
            ),
            target_generation AS (
                SELECT publication_id
                FROM publication_generations
                WHERE scrape_id = @scrapeId
                  AND status IN ('building', 'ready')
                LIMIT 1
            )
            INSERT INTO scrape_publication_state (
                id,
                published_scrape_id,
                published_at,
                public_reads_frozen,
                public_reads_frozen_at,
                public_reads_frozen_scrape_id,
                public_reads_frozen_reason,
                current_publication_id,
                previous_publication_id,
                working_publication_id,
                publication_commit_intent_started_at,
                publication_commit_intent_heartbeat_at,
                publication_commit_intent_owner,
                updated_at)
            VALUES (
                TRUE,
                (SELECT scrape_id::integer
                 FROM current_generation),
                (SELECT published_at
                 FROM current_generation),
                TRUE,
                @now,
                @scrapeId,
                @commitIntentReason,
                (SELECT publication_id
                 FROM current_generation),
                (SELECT previous_publication_id
                 FROM current_generation),
                (SELECT publication_id
                 FROM target_generation),
                @now,
                @now,
                @owner,
                @now)
            ON CONFLICT (id) DO UPDATE SET
                public_reads_frozen = TRUE,
                public_reads_frozen_at = EXCLUDED.public_reads_frozen_at,
                public_reads_frozen_scrape_id =
                    EXCLUDED.public_reads_frozen_scrape_id,
                public_reads_frozen_reason =
                    EXCLUDED.public_reads_frozen_reason,
                publication_commit_intent_started_at =
                    EXCLUDED.publication_commit_intent_started_at,
                publication_commit_intent_heartbeat_at =
                    EXCLUDED.publication_commit_intent_heartbeat_at,
                publication_commit_intent_owner =
                    EXCLUDED.publication_commit_intent_owner,
                updated_at = EXCLUDED.updated_at
            WHERE scrape_publication_state
                    .public_reads_frozen_reason
                    IS DISTINCT FROM @commitIntentReason
               OR scrape_publication_state
                    .publication_commit_intent_owner IS NULL
               OR scrape_publication_state
                    .publication_commit_intent_heartbeat_at
                    < @staleBefore
            """;
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue(
            "scrapeId",
            checked((int)scrapeId));
        command.Parameters.AddWithValue(
            "commitIntentReason",
            PublicReadFreezeState.PublicationCommitIntentReason);
        command.Parameters.AddWithValue(
            "owner",
            commitIntent.OwnerToken);
        command.Parameters.AddWithValue(
            "staleBefore",
            now.AddSeconds(
                -_publicationCommitOptions
                    .StaleCommitIntentSeconds));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new PublicationCommitBusyException(
                $"Scrape {scrapeId} cannot begin publication commit while another fresh commit-intent owner is active.",
                TimeSpan.Zero,
                lockRejections: 1,
                relationLockRetries: 0);
        }

        tx.Commit();
        return commitIntent;
    }

    public void HeartbeatPublicationCommitIntent(
        PublicationCommitIntentHandle commitIntent)
    {
        using var conn = _ds.OpenConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            UPDATE scrape_publication_state
            SET publication_commit_intent_heartbeat_at = @now,
                updated_at = @now
            WHERE id = TRUE
              AND public_reads_frozen
              AND public_reads_frozen_reason =
                    @commitIntentReason
              AND public_reads_frozen_scrape_id =
                    @scrapeId
              AND publication_commit_intent_owner = @owner
            """;
        command.Parameters.AddWithValue("now", DateTime.UtcNow);
        command.Parameters.AddWithValue(
            "commitIntentReason",
            PublicReadFreezeState.PublicationCommitIntentReason);
        command.Parameters.AddWithValue(
            "scrapeId",
            checked((int)commitIntent.ScrapeId));
        command.Parameters.AddWithValue(
            "owner",
            commitIntent.OwnerToken);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                $"Publication commit intent {commitIntent.OwnerToken} for scrape {commitIntent.ScrapeId} is no longer active.");
        }
    }

    public void TransitionPublicationCommitIntentToDeferred(
        PublicationCommitIntentHandle commitIntent)
    {
        DeferredTransitionTestHook?.Invoke();
        using var conn = _ds.OpenConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            UPDATE scrape_publication_state
            SET public_reads_frozen = TRUE,
                public_reads_frozen_at = @now,
                public_reads_frozen_scrape_id = @scrapeId,
                public_reads_frozen_reason = @deferredReason,
                publication_commit_intent_started_at = NULL,
                publication_commit_intent_heartbeat_at = NULL,
                publication_commit_intent_owner = NULL,
                updated_at = @now
            WHERE id = TRUE
              AND public_reads_frozen_reason =
                    @commitIntentReason
              AND public_reads_frozen_scrape_id = @scrapeId
              AND publication_commit_intent_owner = @owner
            """;
        command.Parameters.AddWithValue("now", DateTime.UtcNow);
        command.Parameters.AddWithValue(
            "scrapeId",
            checked((int)commitIntent.ScrapeId));
        command.Parameters.AddWithValue(
            "deferredReason",
            PublicReadFreezeState.PublicationCommitDeferredReason);
        command.Parameters.AddWithValue(
            "commitIntentReason",
            PublicReadFreezeState.PublicationCommitIntentReason);
        command.Parameters.AddWithValue(
            "owner",
            commitIntent.OwnerToken);
        if (command.ExecuteNonQuery() == 1)
            return;

        var state = GetPublicReadFreezeState();
        if (state.PublicationCommitDeferred
            && state.ScrapeId == commitIntent.ScrapeId)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Publication commit intent {commitIntent.OwnerToken} for scrape {commitIntent.ScrapeId} could not transition to deferred.");
    }

    public void TransitionPublicationCommitIntentToIsolationPending(
        PublicationCommitIntentHandle commitIntent)
    {
        IsolationPendingTransitionTestHook?.Invoke();
        using var conn = _ds.OpenConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            UPDATE scrape_publication_state
            SET public_reads_frozen = TRUE,
                public_reads_frozen_at = @now,
                public_reads_frozen_scrape_id = @scrapeId,
                public_reads_frozen_reason = @pendingReason,
                publication_commit_intent_started_at = NULL,
                publication_commit_intent_heartbeat_at = NULL,
                publication_commit_intent_owner = NULL,
                updated_at = @now
            WHERE id = TRUE
              AND public_reads_frozen_reason =
                    @commitIntentReason
              AND public_reads_frozen_scrape_id = @scrapeId
              AND publication_commit_intent_owner = @owner
            """;
        command.Parameters.AddWithValue("now", DateTime.UtcNow);
        command.Parameters.AddWithValue(
            "scrapeId",
            checked((int)commitIntent.ScrapeId));
        command.Parameters.AddWithValue(
            "pendingReason",
            PublicReadFreezeState
                .PublicationFailureIsolationPendingReason);
        command.Parameters.AddWithValue(
            "commitIntentReason",
            PublicReadFreezeState.PublicationCommitIntentReason);
        command.Parameters.AddWithValue(
            "owner",
            commitIntent.OwnerToken);
        if (command.ExecuteNonQuery() == 1)
            return;

        var state = GetPublicReadFreezeState();
        if (state.PublicationFailureIsolationPending
            && state.ScrapeId == commitIntent.ScrapeId)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Publication commit intent {commitIntent.OwnerToken} for scrape {commitIntent.ScrapeId} could not transition to pending isolation.");
    }

    public void ClearPublicationCommitIntentAfterIsolation(
        PublicationCommitIntentHandle commitIntent)
    {
        using var conn = _ds.OpenConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            UPDATE scrape_publication_state
            SET public_reads_frozen = FALSE,
                public_reads_frozen_at = NULL,
                public_reads_frozen_scrape_id = NULL,
                public_reads_frozen_reason = NULL,
                publication_commit_intent_started_at = NULL,
                publication_commit_intent_heartbeat_at = NULL,
                publication_commit_intent_owner = NULL,
                updated_at = now()
            WHERE id = TRUE
              AND public_reads_frozen_reason =
                    @commitIntentReason
              AND public_reads_frozen_scrape_id = @scrapeId
              AND publication_commit_intent_owner = @owner
            """;
        command.Parameters.AddWithValue(
            "commitIntentReason",
            PublicReadFreezeState.PublicationCommitIntentReason);
        command.Parameters.AddWithValue(
            "scrapeId",
            checked((int)commitIntent.ScrapeId));
        command.Parameters.AddWithValue(
            "owner",
            commitIntent.OwnerToken);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                $"Publication commit intent {commitIntent.OwnerToken} for scrape {commitIntent.ScrapeId} could not clear after durable isolation.");
        }
    }

    public void RestorePublicationCommitIntent(
        PublicationCommitIntentHandle commitIntent,
        PublicReadFreezeState previousState)
    {
        var deadline = Stopwatch.StartNew();
        PostgresException? lastLockException = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            using var conn = _ds.OpenConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                using (var timeout = conn.CreateCommand())
                {
                    timeout.Transaction = tx;
                    timeout.CommandText = """
                        SELECT set_config(
                            'lock_timeout',
                            '250ms',
                            true);
                        SELECT set_config(
                            'statement_timeout',
                            '2s',
                            true);
                        """;
                    timeout.ExecuteNonQuery();
                }

                using var restore = conn.CreateCommand();
                restore.Transaction = tx;
                restore.CommandText = """
                    UPDATE scrape_publication_state
                    SET public_reads_frozen = @frozen,
                        public_reads_frozen_at = CASE
                            WHEN @frozen
                                THEN COALESCE(
                                    @frozenAt,
                                    now())
                            ELSE NULL
                        END,
                        public_reads_frozen_scrape_id = CASE
                            WHEN @frozen
                                THEN @previousScrapeId
                            ELSE NULL
                        END,
                        public_reads_frozen_reason = CASE
                            WHEN @frozen
                                THEN @previousReason
                            ELSE NULL
                        END,
                        publication_commit_intent_started_at = NULL,
                        publication_commit_intent_heartbeat_at = NULL,
                        publication_commit_intent_owner = NULL,
                        updated_at = now()
                    WHERE id = TRUE
                      AND public_reads_frozen
                      AND public_reads_frozen_reason =
                            @commitIntentReason
                      AND public_reads_frozen_scrape_id =
                            @scrapeId
                      AND publication_commit_intent_owner =
                            @owner
                    """;
                restore.Parameters.AddWithValue(
                    "frozen",
                    previousState.IsFrozen);
                restore.Parameters.Add(
                    "frozenAt",
                    NpgsqlDbType.TimestampTz).Value =
                    previousState.FrozenAt.HasValue
                        ? previousState.FrozenAt.Value
                        : DBNull.Value;
                restore.Parameters.Add(
                    "previousScrapeId",
                    NpgsqlDbType.Integer).Value =
                    previousState.ScrapeId.HasValue
                        ? checked((int)previousState.ScrapeId.Value)
                        : DBNull.Value;
                restore.Parameters.Add(
                    "previousReason",
                    NpgsqlDbType.Text).Value =
                    string.IsNullOrWhiteSpace(previousState.Reason)
                        ? DBNull.Value
                        : previousState.Reason;
                restore.Parameters.AddWithValue(
                    "commitIntentReason",
                    PublicReadFreezeState
                        .PublicationCommitIntentReason);
                restore.Parameters.AddWithValue(
                    "scrapeId",
                    checked((int)commitIntent.ScrapeId));
                restore.Parameters.AddWithValue(
                    "owner",
                    commitIntent.OwnerToken);
                restore.ExecuteNonQuery();
                tx.Commit();
                return;
            }
            catch (PostgresException ex)
                when (ex.SqlState is
                    PostgresErrorCodes.LockNotAvailable
                    or PostgresErrorCodes.DeadlockDetected)
            {
                TryRollback(tx);
                lastLockException = ex;
                Thread.Sleep(25);
            }
        }

        throw new InvalidOperationException(
            $"Publication commit intent {commitIntent.OwnerToken} for scrape {commitIntent.ScrapeId} could not be restored within two seconds.",
            lastLockException);
    }

    private void TryRestorePublicationCommitIntent(
        PublicationCommitIntentHandle commitIntent,
        PublicReadFreezeState previousState)
    {
        try
        {
            RestorePublicationCommitIntent(
                commitIntent,
                previousState);
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Publication commit intent for scrape {ScrapeId} could not be restored immediately; stale-intent reconciliation remains armed.",
                commitIntent.ScrapeId);
        }
    }

    private void ClearAlreadyPublishedCommitIntent(long scrapeId)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var timeout = conn.CreateCommand())
        {
            timeout.Transaction = tx;
            timeout.CommandText = """
                SELECT set_config('lock_timeout', '250ms', true);
                SELECT set_config('statement_timeout', '2s', true);
                """;
            timeout.ExecuteNonQuery();
        }

        using var clear = conn.CreateCommand();
        clear.Transaction = tx;
        clear.CommandText = """
            UPDATE scrape_publication_state
            SET public_reads_frozen = FALSE,
                public_reads_frozen_at = NULL,
                public_reads_frozen_scrape_id = NULL,
                public_reads_frozen_reason = NULL,
                publication_commit_intent_started_at = NULL,
                publication_commit_intent_heartbeat_at = NULL,
                publication_commit_intent_owner = NULL,
                updated_at = now()
            WHERE id = TRUE
              AND published_scrape_id = @scrapeId
              AND working_publication_id IS NULL
              AND public_reads_frozen_reason =
                    @commitIntentReason
            """;
        clear.Parameters.AddWithValue(
            "scrapeId",
            checked((int)scrapeId));
        clear.Parameters.AddWithValue(
            "commitIntentReason",
            PublicReadFreezeState.PublicationCommitIntentReason);
        clear.ExecuteNonQuery();
        tx.Commit();
    }

    private PublicationCommitIntentObservation
        ReadPublicationCommitIntentObservation()
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT
                public_reads_frozen,
                public_reads_frozen_at,
                public_reads_frozen_scrape_id,
                public_reads_frozen_reason,
                publication_commit_intent_started_at,
                publication_commit_intent_heartbeat_at,
                publication_commit_intent_owner
            FROM scrape_publication_state
            WHERE id = TRUE
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new PublicationCommitIntentObservation(
                false,
                false,
                null,
                null,
                null,
                null,
                null);
        }

        var frozen = reader.GetBoolean(0);
        var reason =
            reader.IsDBNull(3)
                ? null
                : reader.GetString(3);
        var commitPending = string.Equals(
            reason,
            PublicReadFreezeState
                .PublicationCommitIntentReason,
            StringComparison.Ordinal);
        var failureIsolationPending = string.Equals(
            reason,
            PublicReadFreezeState
                .PublicationFailureIsolationPendingReason,
            StringComparison.Ordinal);
        return new PublicationCommitIntentObservation(
            frozen && (commitPending || failureIsolationPending),
            frozen && failureIsolationPending,
            reader.IsDBNull(2)
                ? null
                : Convert.ToInt64(reader.GetValue(2)),
            reader.IsDBNull(1)
                ? null
                : reader.GetDateTime(1),
            reader.IsDBNull(4)
                ? null
                : reader.GetDateTime(4),
            reader.IsDBNull(5)
                ? null
                : reader.GetDateTime(5),
            reader.IsDBNull(6)
                ? null
                : reader.GetString(6));
    }

    public PublicationCommitIntentReconciliationResult
        ReconcileStalePublicationCommitIntent(TimeSpan staleAfter)
    {
        if (staleAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(staleAfter));
        }

        var observed = ReadPublicationCommitIntentObservation();
        if (!observed.Pending)
        {
            return new PublicationCommitIntentReconciliationResult(
                PublicationCommitIntentReconciliationStatus
                    .NotPresent,
                observed.ScrapeId,
                null);
        }

        var observedLeaseAt =
            observed.HeartbeatAtUtc
            ?? observed.StartedAtUtc
            ?? observed.FrozenAtUtc;
        var age = observedLeaseAt.HasValue
            ? DateTime.UtcNow - observedLeaseAt.Value
            : TimeSpan.MaxValue;
        if (!observed.FailureIsolationPending
            && age < staleAfter)
        {
            return new PublicationCommitIntentReconciliationResult(
                PublicationCommitIntentReconciliationStatus.Fresh,
                observed.ScrapeId,
                age);
        }

        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var timeout = conn.CreateCommand())
        {
            timeout.Transaction = tx;
            timeout.CommandText = """
                SELECT set_config('lock_timeout', '250ms', true);
                SELECT set_config(
                    'statement_timeout',
                    '2s',
                    true);
                """;
            timeout.ExecuteNonQuery();
        }

        var exclusiveLockAcquired =
            TryAcquirePublicationAdvisoryLock(
                conn,
                tx,
                shared: false);
        var sharedRecoveryLockAcquired =
            !exclusiveLockAcquired
            && TryAcquirePublicationAdvisoryLock(
                conn,
                tx,
                shared: true);
        if (!exclusiveLockAcquired
            && !sharedRecoveryLockAcquired)
        {
            tx.Rollback();
            return new PublicationCommitIntentReconciliationResult(
                PublicationCommitIntentReconciliationStatus.Active,
                observed.ScrapeId,
                age);
        }

        try
        {
            long? currentPublicationId;
            long? workingPublicationId;
            long? workingScrapeId;
            long? frozenScrapeId;
            DateTime? frozenAt;
            DateTime? intentStartedAt;
            DateTime? intentHeartbeatAt;
            string? intentOwner;
            string? reason;
            using (var state = conn.CreateCommand())
            {
                state.Transaction = tx;
                state.CommandText = """
                    SELECT
                        publication.current_publication_id,
                        publication.working_publication_id,
                        generation.scrape_id,
                        publication.public_reads_frozen_scrape_id,
                        publication.public_reads_frozen_at,
                        publication.publication_commit_intent_started_at,
                        publication.publication_commit_intent_heartbeat_at,
                        publication.publication_commit_intent_owner,
                        publication.public_reads_frozen_reason
                    FROM scrape_publication_state publication
                    LEFT JOIN publication_generations generation
                      ON generation.publication_id =
                            publication.working_publication_id
                    WHERE publication.id = TRUE
                    FOR UPDATE OF publication NOWAIT
                    """;
                using var reader = state.ExecuteReader();
                if (!reader.Read())
                {
                    reader.Close();
                    tx.Rollback();
                    return new PublicationCommitIntentReconciliationResult(
                        PublicationCommitIntentReconciliationStatus
                            .NotPresent,
                        null,
                        null);
                }

                currentPublicationId =
                    reader.IsDBNull(0)
                        ? null
                        : reader.GetInt64(0);
                workingPublicationId =
                    reader.IsDBNull(1)
                        ? null
                        : reader.GetInt64(1);
                workingScrapeId =
                    reader.IsDBNull(2)
                        ? null
                        : reader.GetInt64(2);
                frozenScrapeId =
                    reader.IsDBNull(3)
                        ? null
                        : Convert.ToInt64(reader.GetValue(3));
                frozenAt =
                    reader.IsDBNull(4)
                        ? null
                        : reader.GetDateTime(4);
                intentStartedAt =
                    reader.IsDBNull(5)
                        ? null
                        : reader.GetDateTime(5);
                intentHeartbeatAt =
                    reader.IsDBNull(6)
                        ? null
                        : reader.GetDateTime(6);
                intentOwner =
                    reader.IsDBNull(7)
                        ? null
                        : reader.GetString(7);
                reason =
                    reader.IsDBNull(8)
                        ? null
                        : reader.GetString(8);
            }

            var currentLeaseAt =
                intentHeartbeatAt
                ?? intentStartedAt
                ?? frozenAt;
            var currentAge = currentLeaseAt.HasValue
                ? DateTime.UtcNow - currentLeaseAt.Value
                : TimeSpan.MaxValue;
            var currentCommitPending = string.Equals(
                reason,
                PublicReadFreezeState
                    .PublicationCommitIntentReason,
                StringComparison.Ordinal);
            var currentFailureIsolationPending =
                string.Equals(
                    reason,
                    PublicReadFreezeState
                        .PublicationFailureIsolationPendingReason,
                    StringComparison.Ordinal);
            if (!currentCommitPending
                && !currentFailureIsolationPending)
            {
                tx.Rollback();
                return new PublicationCommitIntentReconciliationResult(
                    PublicationCommitIntentReconciliationStatus
                        .NotPresent,
                    workingScrapeId,
                    currentAge);
            }
            if (!currentFailureIsolationPending
                && !string.IsNullOrWhiteSpace(intentOwner)
                && !string.Equals(
                    intentOwner,
                    observed.OwnerToken,
                    StringComparison.Ordinal)
                && currentAge < staleAfter)
            {
                tx.Rollback();
                return new PublicationCommitIntentReconciliationResult(
                    PublicationCommitIntentReconciliationStatus.Fresh,
                    workingScrapeId,
                    currentAge);
            }
            if (!currentFailureIsolationPending
                && currentAge < staleAfter)
            {
                tx.Rollback();
                return new PublicationCommitIntentReconciliationResult(
                    PublicationCommitIntentReconciliationStatus.Fresh,
                    workingScrapeId,
                    currentAge);
            }

            var targetScrapeId =
                frozenScrapeId ?? workingScrapeId;
            if (!targetScrapeId.HasValue)
            {
                tx.Rollback();
                return new PublicationCommitIntentReconciliationResult(
                    PublicationCommitIntentReconciliationStatus.Active,
                    null,
                    currentAge);
            }

            var workingMatchesTarget =
                workingPublicationId.HasValue
                && workingPublicationId != currentPublicationId
                && workingScrapeId == targetScrapeId;
            long? publishedScrapeId;
            long? currentGenerationScrapeId;
            string? targetGenerationStatus;
            using (var terminalState = conn.CreateCommand())
            {
                terminalState.Transaction = tx;
                terminalState.CommandText = """
                    SELECT
                        publication.published_scrape_id,
                        current_generation.scrape_id,
                        target_generation.status
                    FROM scrape_publication_state publication
                    LEFT JOIN publication_generations current_generation
                      ON current_generation.publication_id =
                            publication.current_publication_id
                    LEFT JOIN publication_generations target_generation
                      ON target_generation.scrape_id = @targetScrapeId
                    WHERE publication.id = TRUE
                    """;
                terminalState.Parameters.AddWithValue(
                    "targetScrapeId",
                    targetScrapeId.Value);
                using var reader = terminalState.ExecuteReader();
                if (!reader.Read())
                {
                    tx.Rollback();
                    return new PublicationCommitIntentReconciliationResult(
                        PublicationCommitIntentReconciliationStatus.Active,
                        targetScrapeId,
                        currentAge);
                }

                publishedScrapeId = reader.IsDBNull(0)
                    ? null
                    : Convert.ToInt64(reader.GetValue(0));
                currentGenerationScrapeId = reader.IsDBNull(1)
                    ? null
                    : reader.GetInt64(1);
                targetGenerationStatus = reader.IsDBNull(2)
                    ? null
                    : reader.GetString(2);
            }

            var targetAlreadyCurrent =
                publishedScrapeId == targetScrapeId
                && currentGenerationScrapeId == targetScrapeId
                && targetGenerationStatus ==
                    PublicationGenerationStatus.Current;
            var targetSafelyRetained =
                targetGenerationStatus ==
                    PublicationGenerationStatus.Retained
                && currentGenerationScrapeId.HasValue
                && currentGenerationScrapeId != targetScrapeId
                && workingScrapeId != targetScrapeId;
            if (targetAlreadyCurrent || targetSafelyRetained)
            {
                using var clearTerminal = conn.CreateCommand();
                clearTerminal.Transaction = tx;
                clearTerminal.CommandText = """
                    UPDATE scrape_publication_state
                    SET public_reads_frozen = FALSE,
                        public_reads_frozen_at = NULL,
                        public_reads_frozen_scrape_id = NULL,
                        public_reads_frozen_reason = NULL,
                        publication_commit_intent_started_at = NULL,
                        publication_commit_intent_heartbeat_at = NULL,
                        publication_commit_intent_owner = NULL,
                        updated_at = now()
                    WHERE id = TRUE
                      AND public_reads_frozen_scrape_id =
                            @targetScrapeId
                      AND public_reads_frozen_reason IN (
                            @commitIntentReason,
                            @failureIsolationPendingReason)
                    """;
                clearTerminal.Parameters.AddWithValue(
                    "targetScrapeId",
                    checked((int)targetScrapeId.Value));
                clearTerminal.Parameters.AddWithValue(
                    "commitIntentReason",
                    PublicReadFreezeState
                        .PublicationCommitIntentReason);
                clearTerminal.Parameters.AddWithValue(
                    "failureIsolationPendingReason",
                    PublicReadFreezeState
                        .PublicationFailureIsolationPendingReason);
                clearTerminal.ExecuteNonQuery();
                tx.Commit();
                _log.LogWarning(
                    "Cleared terminal publication isolation latch for already-published scrape {ScrapeId}; generation status={GenerationStatus}.",
                    targetScrapeId,
                    targetGenerationStatus);
                return new PublicationCommitIntentReconciliationResult(
                    PublicationCommitIntentReconciliationStatus.Cleared,
                    targetScrapeId,
                    currentAge);
            }

            RecordFailedScrapeState(
                conn,
                tx,
                targetScrapeId.Value,
                StalePublicationCommitIntentFailurePhase,
                "Recovered pending publication failure isolation.");

            bool failureConfirmed;
            using (var confirm = conn.CreateCommand())
            {
                confirm.Transaction = tx;
                confirm.CommandText = """
                    SELECT
                        EXISTS (
                            SELECT 1
                            FROM scrape_log
                            WHERE id = @scrapeId
                              AND status = 'failed'
                        )
                        AND NOT EXISTS (
                            SELECT 1
                            FROM publication_generations
                            WHERE scrape_id = @scrapeId
                              AND status IN (
                                  'building',
                                  'ready',
                                  'current',
                                  'retained')
                        )
                    """;
                confirm.Parameters.AddWithValue(
                    "scrapeId",
                    targetScrapeId.Value);
                failureConfirmed =
                    confirm.ExecuteScalar() is true;
            }

            if (!failureConfirmed)
            {
                tx.Commit();
                return new PublicationCommitIntentReconciliationResult(
                    PublicationCommitIntentReconciliationStatus.Active,
                    targetScrapeId,
                    currentAge);
            }

            using (var clear = conn.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = """
                    UPDATE scrape_publication_state
                    SET working_publication_id = CASE
                            WHEN @clearWorking
                                THEN NULL
                            ELSE working_publication_id
                        END,
                        public_reads_frozen = FALSE,
                        public_reads_frozen_at = NULL,
                        public_reads_frozen_scrape_id = NULL,
                        public_reads_frozen_reason = NULL,
                        publication_commit_intent_started_at = NULL,
                        publication_commit_intent_heartbeat_at = NULL,
                        publication_commit_intent_owner = NULL,
                        updated_at = now()
                    WHERE id = TRUE
                      AND public_reads_frozen_scrape_id =
                            @targetScrapeId
                      AND public_reads_frozen_reason IN (
                            @commitIntentReason,
                            @failureIsolationPendingReason)
                    """;
                clear.Parameters.AddWithValue(
                    "clearWorking",
                    workingMatchesTarget);
                clear.Parameters.AddWithValue(
                    "targetScrapeId",
                    checked((int)targetScrapeId.Value));
                clear.Parameters.AddWithValue(
                    "commitIntentReason",
                    PublicReadFreezeState
                        .PublicationCommitIntentReason);
                clear.Parameters.AddWithValue(
                    "failureIsolationPendingReason",
                    PublicReadFreezeState
                        .PublicationFailureIsolationPendingReason);
                clear.ExecuteNonQuery();
            }

            tx.Commit();
            var status =
                PublicationCommitIntentReconciliationStatus
                    .FailedCandidateIsolated;
            _log.LogWarning(
                "Reconciled stale publication commit intent. Status={Status}, ScrapeId={ScrapeId}, Age={Age}.",
                status,
                targetScrapeId,
                currentAge);
            try
            {
                CleanupFailedPublicationArtifacts(
                    targetScrapeId.Value);
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Stale publication candidate {ScrapeId} was isolated, but direct artifact cleanup deferred to the orphan sweeper.",
                    targetScrapeId.Value);
            }
            _ = SweepPublicationBandTableOrphans();
            return new PublicationCommitIntentReconciliationResult(
                status,
                targetScrapeId,
                currentAge);
        }
        catch (PostgresException ex)
            when (ex.SqlState is
                PostgresErrorCodes.LockNotAvailable
                or PostgresErrorCodes.DeadlockDetected)
        {
            TryRollback(tx);
            return new PublicationCommitIntentReconciliationResult(
                PublicationCommitIntentReconciliationStatus.Active,
                observed.ScrapeId,
                age);
        }
    }

    public PublicationCommitIntentReconciliationResult
        ReconcileAbandonedWorkingPublication(
            TimeSpan readyGrace,
            TimeSpan workerHeartbeatFreshness)
    {
        if (readyGrace <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(readyGrace));
        }
        if (workerHeartbeatFreshness <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workerHeartbeatFreshness));
        }

        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var tx = conn.BeginTransaction();
        using (var timeout = conn.CreateCommand())
        {
            timeout.Transaction = tx;
            timeout.CommandText = """
                SELECT set_config('lock_timeout', '250ms', true);
                SELECT set_config('statement_timeout', '5s', true);
                """;
            timeout.ExecuteNonQuery();
        }
        if (!TryAcquirePublicationAdvisoryLock(
                conn,
                tx,
                shared: true))
        {
            tx.Rollback();
            return new PublicationCommitIntentReconciliationResult(
                PublicationCommitIntentReconciliationStatus.Active,
                null,
                null);
        }

        try
        {
            long publicationId;
            long scrapeId;
            string generationStatus;
            string scrapeStatus;
            string? freezeReason;
            DateTime generationReferenceAt;
            bool workerActive;
            using (var state = conn.CreateCommand())
            {
                state.Transaction = tx;
                state.CommandText = """
                    SELECT
                        generation.publication_id,
                        generation.scrape_id,
                        generation.status,
                        scrape.status,
                        publication.public_reads_frozen_reason,
                        COALESCE(
                            generation.ready_at,
                            generation.created_at),
                        EXISTS (
                            SELECT 1
                            FROM service_worker_status worker
                            WHERE worker.worker_key = @workerKey
                              AND worker.status IN (
                                  'running',
                                  'starting')
                              AND worker.last_heartbeat_at
                                  >= @workerFreshAfter
                              AND COALESCE(
                                  worker.current_operation_json
                                      ->> 'OperationKey',
                                  worker.current_operation_json
                                      ->> 'operationKey',
                                  '') LIKE 'scrape.%'
                        )
                    FROM scrape_publication_state publication
                    JOIN publication_generations generation
                      ON generation.publication_id =
                            publication.working_publication_id
                    JOIN scrape_log scrape
                      ON scrape.id = generation.scrape_id
                    WHERE publication.id = TRUE
                      AND generation.status IN (
                          'building',
                          'ready')
                    FOR UPDATE OF publication NOWAIT
                    """;
                state.Parameters.AddWithValue(
                    "workerKey",
                    WorkerStatusPublisher.ScraperWorkerKey);
                state.Parameters.AddWithValue(
                    "workerFreshAfter",
                    DateTime.UtcNow - workerHeartbeatFreshness);
                using var reader = state.ExecuteReader();
                if (!reader.Read())
                {
                    reader.Close();
                    tx.Rollback();
                    return new PublicationCommitIntentReconciliationResult(
                        PublicationCommitIntentReconciliationStatus
                            .NotPresent,
                        null,
                        null);
                }

                publicationId = reader.GetInt64(0);
                scrapeId = reader.GetInt64(1);
                generationStatus = reader.GetString(2);
                scrapeStatus = reader.GetString(3);
                freezeReason = reader.IsDBNull(4)
                    ? null
                    : reader.GetString(4);
                generationReferenceAt = reader.GetDateTime(5);
                workerActive = reader.GetBoolean(6);
            }

            var age = DateTime.UtcNow - generationReferenceAt;
            if (freezeReason is
                    PublicReadFreezeState
                        .PublicationCommitIntentReason
                    or PublicReadFreezeState
                        .PublicationFailureIsolationPendingReason
                    or PublicReadFreezeState
                        .PublicationCommitDeferredReason
                || scrapeStatus == "running"
                || workerActive
                || generationStatus ==
                    PublicationGenerationStatus.Building
                   && age < readyGrace)
            {
                tx.Rollback();
                return new PublicationCommitIntentReconciliationResult(
                    PublicationCommitIntentReconciliationStatus.Active,
                    scrapeId,
                    age);
            }

            RecordFailedScrapeState(
                conn,
                tx,
                scrapeId,
                StalePublicationCommitIntentFailurePhase,
                "Recovered abandoned prepared publication generation.");
            tx.Commit();
            try
            {
                CleanupFailedPublicationArtifacts(scrapeId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Abandoned publication {PublicationId} was isolated, but direct cleanup deferred.",
                    publicationId);
            }
            _ = SweepPublicationBandTableOrphans();
            return new PublicationCommitIntentReconciliationResult(
                PublicationCommitIntentReconciliationStatus
                    .AbandonedWorkingIsolated,
                scrapeId,
                age);
        }
        catch (PostgresException ex)
            when (ex.SqlState is
                PostgresErrorCodes.LockNotAvailable
                or PostgresErrorCodes.DeadlockDetected)
        {
            TryRollback(tx);
            return new PublicationCommitIntentReconciliationResult(
                PublicationCommitIntentReconciliationStatus.Active,
                null,
                null);
        }
    }

    private PublicationStateSnapshot ReadPublicationStateForPreparation(
        NpgsqlConnection conn,
        NpgsqlTransaction tx)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT published_scrape_id,
                   improvement_notifications_scrape_id,
                   improvement_notifications_status,
                   current_publication_id,
                   previous_publication_id,
                   working_publication_id
            FROM scrape_publication_state
            WHERE id = TRUE
            FOR SHARE
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                "Publication state is unavailable.");
        }

        return new PublicationStateSnapshot(
            reader.IsDBNull(0)
                ? null
                : Convert.ToInt64(reader.GetValue(0)),
            reader.IsDBNull(1)
                ? null
                : Convert.ToInt64(reader.GetValue(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5));
    }

    private static void VerifyCompletedScrape(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long scrapeId)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT completed_at IS NOT NULL AND status = 'completed'
            FROM scrape_log
            WHERE id = @scrapeId
            """;
        command.Parameters.AddWithValue(
            "scrapeId",
            checked((int)scrapeId));
        if (command.ExecuteScalar() is not bool isCompleted || !isCompleted)
        {
            throw new InvalidOperationException(
                $"Scrape run {scrapeId} cannot be published before it is completed.");
        }
    }

    private static long EnsureWorkingPublicationGeneration(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long scrapeId)
    {
        using var generation = conn.CreateCommand();
        generation.Transaction = tx;
        generation.CommandText = """
            INSERT INTO publication_generations (
                scrape_id, status, created_at, source_cut_at)
            SELECT @scrapeId, 'building', scrape.started_at, scrape.completed_at
            FROM scrape_log scrape
            WHERE scrape.id = @scrapeId
            ON CONFLICT (scrape_id) DO UPDATE SET
                source_cut_at = COALESCE(
                    publication_generations.source_cut_at,
                    EXCLUDED.source_cut_at)
            RETURNING publication_id, status
            """;
        generation.Parameters.AddWithValue("scrapeId", scrapeId);
        using var reader = generation.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                $"Scrape run {scrapeId} has no publication generation.");
        }

        var publicationId = reader.GetInt64(0);
        var status = reader.GetString(1);
        if (status is not (
            PublicationGenerationStatus.Building
            or PublicationGenerationStatus.Ready))
        {
            throw new InvalidOperationException(
                $"Publication generation {publicationId} for scrape {scrapeId} is {status}.");
        }

        return publicationId;
    }

    private static long? AdoptWorkingPublication(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long publicationId)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO scrape_publication_state (
                id, working_publication_id, updated_at)
            VALUES (TRUE, @publicationId, @now)
            ON CONFLICT (id) DO UPDATE SET
                working_publication_id = COALESCE(
                    scrape_publication_state.working_publication_id,
                    EXCLUDED.working_publication_id),
                updated_at = EXCLUDED.updated_at
            RETURNING working_publication_id
            """;
        command.Parameters.AddWithValue("publicationId", publicationId);
        command.Parameters.AddWithValue("now", DateTime.UtcNow);
        return command.ExecuteScalar() is long adopted ? adopted : null;
    }

    private static void VerifyPublicationCatalog(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long publicationId)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM publication_song_catalog catalog
                JOIN publication_surface_bindings binding
                  ON binding.publication_id = catalog.publication_id
                 AND binding.surface_name = 'song_catalog'
                WHERE catalog.publication_id = @publicationId
                  AND binding.binding_kind =
                      'generation_catalog_snapshot'
                  AND catalog.is_exact
                  AND catalog.source_kind = 'provider_exact'
                  AND catalog.schema_version = @schemaVersion
                  AND binding.row_count = catalog.song_count
                  AND binding.content_hash = catalog.content_hash
                  AND binding.status = 'ready'
            )
            """;
        command.Parameters.AddWithValue("publicationId", publicationId);
        command.Parameters.AddWithValue(
            "schemaVersion",
            SongCatalogSnapshotBuilder.SchemaVersion);
        if (command.ExecuteScalar() is not bool hasCatalog || !hasCatalog)
        {
            throw new InvalidOperationException(
                $"Publication generation {publicationId} has no complete song catalog snapshot.");
        }
    }

    private static void VerifyPublicationCacheStaging(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long scrapeId,
        long publicationId)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT
                EXISTS (SELECT 1 FROM api_response_cache_staging)
                AND EXISTS (
                    SELECT 1
                    FROM publication_api_response_cache_staging
                    WHERE publication_id = @publicationId
                )
            """;
        command.Parameters.AddWithValue("publicationId", publicationId);
        if (command.ExecuteScalar() is not bool hasStagedResponses
            || !hasStagedResponses)
        {
            throw new InvalidOperationException(
                $"Scrape run {scrapeId} cannot be published because its API response cache staging table is empty.");
        }
    }

    private static void ValidatePublishedScopeSources(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long scrapeId,
        int expectedPublishedScopeCount)
    {
        if (expectedPublishedScopeCount <= 0)
        {
            throw new InvalidOperationException(
                "A published scope-source promotion requires at least one expected scope.");
        }

        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT
                COUNT(*)::int AS mapped_count,
                COUNT(*) FILTER (
                    WHERE NOT source.is_complete
                       OR source.source_scrape_id <= 0
                       OR source.source_scrape_id > source.published_scrape_id
                       OR fingerprint.song_id IS NULL
                       OR fingerprint.last_seen_scrape_id
                            <> source.published_scrape_id
                       OR NOT fingerprint.is_complete
                       OR fingerprint.entry_count::bigint <> source.row_count
                       OR fingerprint.content_fingerprint
                            IS DISTINCT FROM source.content_fingerprint
                       OR fingerprint.coverage_fingerprint
                            IS DISTINCT FROM source.coverage_fingerprint
                       OR source.reported_total_entries
                            < fingerprint.reported_total_entries
                       OR fingerprint.reported_total_pages
                            IS DISTINCT FROM source.reported_total_pages
                )::int AS invalid_count
            FROM leaderboard_published_scope_source source
            LEFT JOIN leaderboard_scope_fingerprints fingerprint
              ON fingerprint.song_id = source.song_id
             AND fingerprint.instrument = source.instrument
             AND fingerprint.scope_kind = source.scope_kind
            WHERE source.published_scrape_id = @scrapeId
            """;
        command.Parameters.AddWithValue("scrapeId", scrapeId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                $"Published scope-source validation for scrape {scrapeId} returned no result.");
        }

        var mappedCount = reader.GetInt32(0);
        var invalidCount = reader.GetInt32(1);
        if (mappedCount != expectedPublishedScopeCount
            || invalidCount != 0)
        {
            throw new InvalidOperationException(
                $"Scrape {scrapeId} cannot be published because its per-scope source mapping is invalid " +
                $"(expected={expectedPublishedScopeCount}, mapped={mappedCount}, invalid={invalidCount}).");
        }
    }

    private static long? ReadBandProjectionGeneration(
        NpgsqlConnection conn,
        NpgsqlTransaction? transaction)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT current_generation
            FROM band_current_projection_state
            WHERE id = TRUE
            """;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static IReadOnlyList<PreparedBandTableSet>
        PreparePublishedBandTables(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            long publicationId)
    {
        var options = BandTeamRankingRebuildOptions.Default;
        var prepared = new List<PreparedBandTableSet>();
        foreach (var bandType in BandRankingStorageNames.AllBandTypes)
        {
            var currentRankingTable =
                BandRankingStorageNames.GetCurrentRankingTable(bandType);
            var currentStatsTable =
                BandRankingStorageNames.GetCurrentStatsTable(bandType);
            var hasCurrentRankings =
                TableExists(conn, tx, currentRankingTable);
            var hasCurrentStats =
                TableExists(conn, tx, currentStatsTable);
            if (hasCurrentRankings != hasCurrentStats)
            {
                throw new InvalidOperationException(
                    $"Band publication source tables for {bandType} are incomplete.");
            }
            if (!hasCurrentRankings)
                continue;

            var preparedRankingTable =
                BandRankingStorageNames.GetPreparedPublishedRankingTable(
                    publicationId,
                    bandType);
            var preparedStatsTable =
                BandRankingStorageNames.GetPreparedPublishedStatsTable(
                    publicationId,
                    bandType);
            using (var drop = conn.CreateCommand())
            {
                ConfigureBandRebuildCommand(drop, tx, options);
                drop.CommandText =
                    $"DROP TABLE IF EXISTS {BandRankingStorageNames.QuoteIdentifier(preparedRankingTable)};" +
                    Environment.NewLine +
                    $"DROP TABLE IF EXISTS {BandRankingStorageNames.QuoteIdentifier(preparedStatsTable)};";
                drop.ExecuteNonQuery();
            }

            using (var create = conn.CreateCommand())
            {
                ConfigureBandRebuildCommand(create, tx, options);
                create.CommandText =
                    BandRankingStorageNames.GetCreateRankingTableSql(
                        preparedRankingTable,
                        includePrimaryKey: false)
                    + Environment.NewLine
                    + BandRankingStorageNames.GetCreateStatsTableSql(
                        preparedStatsTable,
                        includePrimaryKey: false);
                create.ExecuteNonQuery();
            }

            using (var copy = conn.CreateCommand())
            {
                ConfigureBandRebuildCommand(copy, tx, options);
                copy.CommandText =
                    $"INSERT INTO {BandRankingStorageNames.QuoteIdentifier(preparedRankingTable)} " +
                    $"SELECT * FROM {BandRankingStorageNames.QuoteIdentifier(currentRankingTable)};" +
                    Environment.NewLine +
                    $"INSERT INTO {BandRankingStorageNames.QuoteIdentifier(preparedStatsTable)} " +
                    $"SELECT * FROM {BandRankingStorageNames.QuoteIdentifier(currentStatsTable)};";
                copy.ExecuteNonQuery();
            }

            CreateBandRankingIndexes(
                conn,
                tx,
                options,
                preparedRankingTable,
                includeTeamLookup: false);
            CreateBandRankingStatsIndexes(
                conn,
                tx,
                options,
                preparedStatsTable);
            prepared.Add(new PreparedBandTableSet(
                bandType,
                preparedRankingTable,
                preparedStatsTable));
        }

        return prepared;
    }

    private static void PreparePublicationCache(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long publicationId,
        long? currentPublicationId,
        bool promoteCachedResponses)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = promoteCachedResponses
            ? """
              DELETE FROM publication_api_response_cache
              WHERE publication_id = @publicationId;

              INSERT INTO publication_api_response_cache (
                  publication_id, cache_key, json_data, etag, cached_at)
              SELECT publication_id, cache_key, json_data, etag, cached_at
              FROM publication_api_response_cache_staging
              WHERE publication_id = @publicationId;
              """
            : """
              DELETE FROM publication_api_response_cache
              WHERE publication_id = @publicationId;

              INSERT INTO publication_api_response_cache (
                  publication_id, cache_key, json_data, etag, cached_at)
              SELECT
                  @publicationId,
                  cache.cache_key,
                  cache.json_data,
                  cache.etag,
                  cache.cached_at
              FROM publication_api_response_cache cache
              WHERE cache.publication_id = @currentPublicationId
              UNION ALL
              SELECT
                  @publicationId,
                  legacy.cache_key,
                  legacy.json_data,
                  legacy.etag,
                  legacy.cached_at
              FROM api_response_cache legacy
              WHERE @currentPublicationId IS NULL;
              """;
        command.Parameters.AddWithValue("publicationId", publicationId);
        command.Parameters.Add(
            "currentPublicationId",
            NpgsqlDbType.Bigint).Value =
            currentPublicationId.HasValue
                ? currentPublicationId.Value
                : DBNull.Value;
        command.ExecuteNonQuery();

        if (!promoteCachedResponses
            && currentPublicationId.HasValue)
        {
            using var verifySource = conn.CreateCommand();
            verifySource.Transaction = tx;
            verifySource.CommandText = """
                SELECT
                    (
                        SELECT COUNT(*)
                        FROM publication_api_response_cache
                        WHERE publication_id =
                            @currentPublicationId
                    ),
                    (SELECT COUNT(*) FROM api_response_cache)
                """;
            verifySource.Parameters.AddWithValue(
                "currentPublicationId",
                currentPublicationId.Value);
            using var reader = verifySource.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException(
                    "Publication cache inheritance source validation returned no result.");
            }

            var generationRows = reader.GetInt64(0);
            var legacyRows = reader.GetInt64(1);
            if (generationRows == 0 && legacyRows > 0)
            {
                throw new InvalidOperationException(
                    $"Publication cache inheritance is unsafe because current generation {currentPublicationId.Value} is empty while the legacy compatibility cache contains {legacyRows} row(s). Reconcile the current generation before publication.");
            }
        }
    }

    private static void PreparePublicationSurfaceBindings(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long scrapeId,
        long publicationId,
        long? currentPublicationId,
        bool promoteCachedResponses,
        int? expectedPublishedScopeCount,
        bool queueImprovementNotifications,
        int notificationScopeCount,
        long? bandProjectionGeneration,
        IReadOnlyList<PreparedBandTableSet> preparedBandTables)
    {
        var bandBindingJson = JsonSerializer.Serialize(new
        {
            publicationId,
            scrapeId,
            generation = bandProjectionGeneration,
            tables = preparedBandTables.Select(static table => new
            {
                table.BandType,
                rankingTable = table.RankingTable,
                statsTable = table.StatsTable,
            }),
        });

        using var bindings = conn.CreateCommand();
        bindings.Transaction = tx;
        bindings.CommandText = """
            INSERT INTO publication_surface_bindings (
                publication_id, surface_name, binding_kind, binding_json,
                row_count, content_hash, status, built_at)
            VALUES
                (
                    @publicationId,
                    'api_response_cache',
                    'generation_cache_table',
                    jsonb_build_object(
                        'table', 'publication_api_response_cache',
                        'scrapeId', @scrapeId,
                        'publicationId', @publicationId,
                        'authoritative', true,
                        'inheritedFromPublicationId',
                        CASE
                            WHEN @promoteCachedResponses
                                THEN NULL
                            ELSE @currentPublicationId
                        END),
                    (
                        SELECT COUNT(*)
                        FROM publication_api_response_cache
                        WHERE publication_id = @publicationId
                    ),
                    (
                        SELECT md5(COALESCE(
                            string_agg(
                                cache_key || ':' || etag,
                                '|' ORDER BY cache_key),
                            ''))
                        FROM publication_api_response_cache
                        WHERE publication_id = @publicationId
                    ),
                    'ready',
                    @now
                ),
                (
                    @publicationId,
                    'solo_scope_sources',
                    'scrape_id',
                    jsonb_build_object(
                        'publicationId', @publicationId,
                        'table', 'leaderboard_published_scope_source',
                        'publishedScrapeId', @scrapeId),
                    (
                        SELECT COUNT(*)
                        FROM leaderboard_published_scope_source
                        WHERE published_scrape_id = @scrapeId
                    ),
                    NULL,
                    'ready',
                    @now
                ),
                (
                    @publicationId,
                    'band_rankings',
                    'prepared_published_tables',
                    @bandBinding,
                    NULL,
                    NULL,
                    'building',
                    @now
                ),
                (
                    @publicationId,
                    'improvement_notifications',
                    'publication_outbox',
                    jsonb_build_object(
                        'publicationId', @publicationId,
                        'scrapeId', @scrapeId,
                        'queued', @queueImprovementNotifications,
                        'scopeCount', @notificationScopeCount),
                    @notificationScopeCount,
                    NULL,
                    'ready',
                    @now
                ),
                (
                    @publicationId,
                    'item_shop',
                    'legacy_live_unversioned',
                    jsonb_build_object('table', 'item_shop_tracks'),
                    (SELECT COUNT(*) FROM item_shop_tracks),
                    NULL,
                    'building',
                    @now
                ),
                (
                    @publicationId,
                    'path_artifacts',
                    'legacy_live_unversioned',
                    jsonb_build_object('table', 'songs'),
                    (
                        SELECT COUNT(*)
                        FROM songs
                        WHERE paths_generated_at IS NOT NULL
                    ),
                    NULL,
                    'building',
                    @now
                )
            ON CONFLICT (publication_id, surface_name) DO UPDATE SET
                binding_kind = EXCLUDED.binding_kind,
                binding_json = EXCLUDED.binding_json,
                row_count = EXCLUDED.row_count,
                content_hash = EXCLUDED.content_hash,
                status = EXCLUDED.status,
                built_at = EXCLUDED.built_at
            """;
        bindings.Parameters.AddWithValue("publicationId", publicationId);
        bindings.Parameters.AddWithValue("scrapeId", scrapeId);
        bindings.Parameters.AddWithValue("now", DateTime.UtcNow);
        bindings.Parameters.AddWithValue(
            "promoteCachedResponses",
            promoteCachedResponses);
        bindings.Parameters.Add(
            "currentPublicationId",
            NpgsqlDbType.Bigint).Value =
            currentPublicationId.HasValue
                ? currentPublicationId.Value
                : DBNull.Value;
        bindings.Parameters.AddWithValue(
            "queueImprovementNotifications",
            queueImprovementNotifications);
        bindings.Parameters.AddWithValue(
            "notificationScopeCount",
            notificationScopeCount);
        bindings.Parameters.Add(
            "bandBinding",
            NpgsqlDbType.Jsonb).Value = bandBindingJson;
        bindings.ExecuteNonQuery();

        if (expectedPublishedScopeCount.HasValue)
        {
            using var verify = conn.CreateCommand();
            verify.Transaction = tx;
            verify.CommandText = """
                SELECT row_count = @expectedCount
                FROM publication_surface_bindings
                WHERE publication_id = @publicationId
                  AND surface_name = 'solo_scope_sources'
                """;
            verify.Parameters.AddWithValue(
                "expectedCount",
                expectedPublishedScopeCount.Value);
            verify.Parameters.AddWithValue("publicationId", publicationId);
            if (verify.ExecuteScalar() is not bool matches || !matches)
            {
                throw new InvalidOperationException(
                    $"Publication {publicationId} scope-source binding does not match the expected count.");
            }
        }
    }

    private bool CommitPreparedPublicationTransaction(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        PublicationPreparationResult preparation)
    {
        var state = ReadPublicationStateForCommit(conn, tx);
        if (state.PublishedScrapeId == preparation.ScrapeId
            && state.CurrentPublicationId == preparation.PublicationId)
        {
            return true;
        }

        ValidateNotificationGate(
            preparation.ScrapeId,
            state.PublishedScrapeId,
            state.NotificationScrapeId,
            state.NotificationStatus);
        if (state.CurrentPublicationId
                != preparation.CurrentPublicationId
            || state.PreviousPublicationId
                != preparation.PreviousPublicationId
            || state.WorkingPublicationId
                != preparation.PublicationId)
        {
            throw new InvalidOperationException(
                $"Publication {preparation.PublicationId} pointers changed after preparation " +
                $"(current={state.CurrentPublicationId?.ToString() ?? "null"}, " +
                $"previous={state.PreviousPublicationId?.ToString() ?? "null"}, " +
                $"working={state.WorkingPublicationId?.ToString() ?? "null"}).");
        }

        VerifyCompletedScrape(conn, tx, preparation.ScrapeId);
        VerifyPublicationCatalog(
            conn,
            tx,
            preparation.PublicationId);
        VerifyPreparedGeneration(conn, tx, preparation);
        if (preparation.ExpectedPublishedScopeCount.HasValue)
        {
            ValidatePublishedScopeSources(
                conn,
                tx,
                preparation.ScrapeId,
                preparation.ExpectedPublishedScopeCount.Value);
        }

        var currentBandGeneration =
            ReadBandProjectionGeneration(conn, tx);
        if (currentBandGeneration
            != preparation.BandProjectionGeneration)
        {
            throw new InvalidOperationException(
                $"Band projection generation changed after publication preparation " +
                $"(prepared={preparation.BandProjectionGeneration?.ToString() ?? "null"}, " +
                $"current={currentBandGeneration?.ToString() ?? "null"}).");
        }

        SwapPreparedPublishedBandTables(
            conn,
            tx,
            preparation.PublicationId,
            preparation.CurrentPublicationId);
        MarkPreviousBandBindingRetained(
            conn,
            tx,
            preparation);

        if (preparation.ExpectedPublishedScopeCount.HasValue)
        {
            using var fingerprints = conn.CreateCommand();
            fingerprints.Transaction = tx;
            fingerprints.CommandText = """
                UPDATE leaderboard_scope_fingerprints fingerprint
                SET published_scrape_id = @scrapeId
                FROM leaderboard_published_scope_source source
                WHERE source.published_scrape_id = @scrapeId
                  AND fingerprint.song_id = source.song_id
                  AND fingerprint.instrument = source.instrument
                  AND fingerprint.scope_kind = source.scope_kind
                  AND fingerprint.last_seen_scrape_id = @scrapeId
                  AND fingerprint.is_complete
                """;
            fingerprints.Parameters.AddWithValue(
                "scrapeId",
                preparation.ScrapeId);
            var publishedFingerprintCount =
                fingerprints.ExecuteNonQuery();
            if (publishedFingerprintCount
                != preparation.ExpectedPublishedScopeCount.Value)
            {
                throw new InvalidOperationException(
                    $"Scrape {preparation.ScrapeId} published {publishedFingerprintCount} fingerprint rows; " +
                    $"{preparation.ExpectedPublishedScopeCount.Value} were required.");
            }
        }

        MarkBandBindingPublished(conn, tx, preparation);
        if (preparation.CurrentPublicationId.HasValue
            && preparation.CurrentPublicationId
                != preparation.PublicationId)
        {
            using var retainPrevious = conn.CreateCommand();
            retainPrevious.Transaction = tx;
            retainPrevious.CommandText = """
                UPDATE publication_generations
                SET status = 'retained'
                WHERE publication_id = @publicationId
                  AND status = 'current'
                """;
            retainPrevious.Parameters.AddWithValue(
                "publicationId",
                preparation.CurrentPublicationId.Value);
            retainPrevious.ExecuteNonQuery();
        }

        using (var generation = conn.CreateCommand())
        {
            generation.Transaction = tx;
            generation.CommandText = """
                UPDATE publication_generations
                SET status = 'current',
                    previous_publication_id = @previousPublicationId,
                    ready_at = COALESCE(ready_at, @now),
                    published_at = @now,
                    failed_at = NULL,
                    failure_phase = NULL,
                    failure_message = NULL
                WHERE publication_id = @publicationId
                  AND status = 'ready'
                """;
            generation.Parameters.Add(
                "previousPublicationId",
                NpgsqlDbType.Bigint).Value =
                preparation.CurrentPublicationId.HasValue
                    ? preparation.CurrentPublicationId.Value
                    : DBNull.Value;
            generation.Parameters.AddWithValue("now", DateTime.UtcNow);
            generation.Parameters.AddWithValue(
                "publicationId",
                preparation.PublicationId);
            if (generation.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"Publication generation {preparation.PublicationId} could not be promoted.");
            }
        }

        using var publish = conn.CreateCommand();
        publish.Transaction = tx;
        publish.CommandText = """
            INSERT INTO scrape_publication_state (
                id, published_scrape_id, published_at,
                band_projection_generation,
                improvement_notifications_scrape_id,
                improvement_notifications_status,
                improvement_notifications_attempt_count,
                improvement_notifications_started_at,
                improvement_notifications_completed_at,
                improvement_notifications_error,
                improvement_notifications_projection_scopes,
                improvement_notifications_projection_ready,
                improvement_notifications_projection_scrape_id,
                current_publication_id,
                previous_publication_id,
                working_publication_id,
                updated_at)
            VALUES (
                TRUE, @scrapeId, @now,
                @bandProjectionGeneration,
                CASE
                    WHEN @queueImprovementNotifications
                        THEN @scrapeId
                    ELSE NULL
                END,
                CASE
                    WHEN @queueImprovementNotifications
                        THEN 'pending'
                    ELSE 'disabled'
                END,
                0,
                NULL,
                NULL,
                NULL,
                @notificationProjectionScopes,
                @queueImprovementNotifications,
                CASE
                    WHEN @queueImprovementNotifications
                        THEN @scrapeId
                    ELSE NULL
                END,
                @publicationId,
                @previousPublicationId,
                NULL,
                @now)
            ON CONFLICT (id) DO UPDATE SET
                published_scrape_id = EXCLUDED.published_scrape_id,
                published_at = EXCLUDED.published_at,
                band_projection_generation =
                    EXCLUDED.band_projection_generation,
                improvement_notifications_scrape_id =
                    EXCLUDED.improvement_notifications_scrape_id,
                improvement_notifications_status =
                    EXCLUDED.improvement_notifications_status,
                improvement_notifications_attempt_count = 0,
                improvement_notifications_started_at = NULL,
                improvement_notifications_completed_at = NULL,
                improvement_notifications_error = NULL,
                improvement_notifications_projection_scopes =
                    EXCLUDED.improvement_notifications_projection_scopes,
                improvement_notifications_projection_ready =
                    EXCLUDED.improvement_notifications_projection_ready,
                improvement_notifications_projection_scrape_id =
                    EXCLUDED.improvement_notifications_projection_scrape_id,
                current_publication_id =
                    EXCLUDED.current_publication_id,
                previous_publication_id =
                    EXCLUDED.previous_publication_id,
                working_publication_id = NULL,
                public_reads_frozen = FALSE,
                public_reads_frozen_at = NULL,
                public_reads_frozen_scrape_id = NULL,
                public_reads_frozen_reason = NULL,
                publication_commit_intent_started_at = NULL,
                publication_commit_intent_heartbeat_at = NULL,
                publication_commit_intent_owner = NULL,
                updated_at = EXCLUDED.updated_at
            """;
        publish.Parameters.AddWithValue(
            "scrapeId",
            checked((int)preparation.ScrapeId));
        publish.Parameters.AddWithValue(
            "publicationId",
            preparation.PublicationId);
        publish.Parameters.Add(
            "previousPublicationId",
            NpgsqlDbType.Bigint).Value =
            preparation.CurrentPublicationId.HasValue
                ? preparation.CurrentPublicationId.Value
                : DBNull.Value;
        publish.Parameters.Add(
            "bandProjectionGeneration",
            NpgsqlDbType.Bigint).Value =
            preparation.BandProjectionGeneration.HasValue
                ? preparation.BandProjectionGeneration.Value
                : DBNull.Value;
        publish.Parameters.AddWithValue("now", DateTime.UtcNow);
        publish.Parameters.AddWithValue(
            "queueImprovementNotifications",
            preparation.QueueImprovementNotifications);
        publish.Parameters.Add(
            "notificationProjectionScopes",
            NpgsqlDbType.Jsonb).Value =
            preparation.ImprovementNotificationProjectionScopesJson;
        publish.ExecuteNonQuery();
        return false;
    }

    private static PublicationStateSnapshot ReadPublicationStateForCommit(
        NpgsqlConnection conn,
        NpgsqlTransaction tx)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT published_scrape_id,
                   improvement_notifications_scrape_id,
                   improvement_notifications_status,
                   current_publication_id,
                   previous_publication_id,
                   working_publication_id
            FROM scrape_publication_state
            WHERE id = TRUE
            FOR UPDATE
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                "Publication state is unavailable.");
        }

        return new PublicationStateSnapshot(
            reader.IsDBNull(0)
                ? null
                : Convert.ToInt64(reader.GetValue(0)),
            reader.IsDBNull(1)
                ? null
                : Convert.ToInt64(reader.GetValue(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5));
    }

    private static void VerifyPreparedGeneration(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        PublicationPreparationResult preparation)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT
                generation.status = 'ready'
                AND EXISTS (
                    SELECT 1
                    FROM publication_surface_bindings cache
                    WHERE cache.publication_id = generation.publication_id
                      AND cache.surface_name = 'api_response_cache'
                      AND cache.binding_kind = 'generation_cache_table'
                      AND cache.status = 'ready'
                )
                AND EXISTS (
                    SELECT 1
                    FROM publication_surface_bindings band
                    WHERE band.publication_id = generation.publication_id
                      AND band.surface_name = 'band_rankings'
                      AND band.binding_kind =
                          'prepared_published_tables'
                      AND band.status = 'building'
                )
            FROM publication_generations generation
            WHERE generation.publication_id = @publicationId
              AND generation.scrape_id = @scrapeId
            """;
        command.Parameters.AddWithValue(
            "publicationId",
            preparation.PublicationId);
        command.Parameters.AddWithValue(
            "scrapeId",
            preparation.ScrapeId);
        if (command.ExecuteScalar() is not bool isReady || !isReady)
        {
            throw new InvalidOperationException(
                $"Publication generation {preparation.PublicationId} is not a complete prepared candidate.");
        }
    }

    private static void SwapPreparedPublishedBandTables(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long publicationId,
        long? currentPublicationId)
    {
        var options = BandTeamRankingRebuildOptions.Default;
        var retainedOwner = currentPublicationId ?? 0;
        foreach (var bandType in BandRankingStorageNames.AllBandTypes)
        {
            var preparedRanking =
                BandRankingStorageNames.GetPreparedPublishedRankingTable(
                    publicationId,
                    bandType);
            var preparedStats =
                BandRankingStorageNames.GetPreparedPublishedStatsTable(
                    publicationId,
                    bandType);
            if (!TableExists(conn, tx, preparedRanking)
                || !TableExists(conn, tx, preparedStats))
            {
                throw new InvalidOperationException(
                    $"Prepared band publication tables for {bandType} are missing.");
            }

            var publishedRanking =
                BandRankingStorageNames.GetPublishedRankingTable(bandType);
            var publishedStats =
                BandRankingStorageNames.GetPublishedStatsTable(bandType);
            var retainedRanking =
                BandRankingStorageNames.GetRetainedPublishedRankingTable(
                    retainedOwner,
                    bandType);
            var retainedStats =
                BandRankingStorageNames.GetRetainedPublishedStatsTable(
                    retainedOwner,
                    bandType);
            if (TableExists(conn, tx, retainedRanking)
                || TableExists(conn, tx, retainedStats))
            {
                throw new InvalidOperationException(
                    $"Retained band publication tables for generation {retainedOwner} already exist.");
            }

            var statements = new List<string>();
            if (TableExists(conn, tx, publishedRanking))
            {
                statements.Add(
                    $"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(publishedRanking)} " +
                    $"RENAME TO {BandRankingStorageNames.QuoteIdentifier(retainedRanking)}");
            }
            if (TableExists(conn, tx, publishedStats))
            {
                statements.Add(
                    $"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(publishedStats)} " +
                    $"RENAME TO {BandRankingStorageNames.QuoteIdentifier(retainedStats)}");
            }
            statements.Add(
                $"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(preparedRanking)} " +
                $"RENAME TO {BandRankingStorageNames.QuoteIdentifier(publishedRanking)}");
            statements.Add(
                $"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(preparedStats)} " +
                $"RENAME TO {BandRankingStorageNames.QuoteIdentifier(publishedStats)}");

            using var command = conn.CreateCommand();
            ConfigureBandRebuildCommand(command, tx, options);
            command.CommandText =
                string.Join(";" + Environment.NewLine, statements) + ";";
            command.ExecuteNonQuery();
        }
    }

    private static void MarkBandBindingPublished(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        PublicationPreparationResult preparation)
    {
        var tables = BandRankingStorageNames.AllBandTypes.Select(
            bandType => new
            {
                bandType,
                rankingTable =
                    BandRankingStorageNames.GetPublishedRankingTable(
                        bandType),
                statsTable =
                    BandRankingStorageNames.GetPublishedStatsTable(
                        bandType),
                retainedRankingTable =
                    BandRankingStorageNames
                        .GetRetainedPublishedRankingTable(
                            preparation.CurrentPublicationId ?? 0,
                            bandType),
                retainedStatsTable =
                    BandRankingStorageNames
                        .GetRetainedPublishedStatsTable(
                            preparation.CurrentPublicationId ?? 0,
                            bandType),
            });
        var bindingJson = JsonSerializer.Serialize(new
        {
            publicationId = preparation.PublicationId,
            scrapeId = preparation.ScrapeId,
            generation = preparation.BandProjectionGeneration,
            tables,
        });

        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE publication_surface_bindings
            SET binding_kind = 'published_tables',
                binding_json = @binding,
                status = 'ready',
                built_at = @now
            WHERE publication_id = @publicationId
              AND surface_name = 'band_rankings'
              AND binding_kind = 'prepared_published_tables'
            """;
        command.Parameters.Add(
            "binding",
            NpgsqlDbType.Jsonb).Value = bindingJson;
        command.Parameters.AddWithValue("now", DateTime.UtcNow);
        command.Parameters.AddWithValue(
            "publicationId",
            preparation.PublicationId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                $"Publication {preparation.PublicationId} band binding could not be promoted.");
        }
    }

    private static void MarkPreviousBandBindingRetained(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        PublicationPreparationResult preparation)
    {
        if (!preparation.CurrentPublicationId.HasValue)
            return;

        var tables = BandRankingStorageNames.AllBandTypes.Select(
            bandType => new
            {
                bandType,
                rankingTable =
                    BandRankingStorageNames
                        .GetRetainedPublishedRankingTable(
                            preparation.CurrentPublicationId.Value,
                            bandType),
                statsTable =
                    BandRankingStorageNames
                        .GetRetainedPublishedStatsTable(
                            preparation.CurrentPublicationId.Value,
                            bandType),
            });
        var bindingJson = JsonSerializer.Serialize(new
        {
            publicationId = preparation.CurrentPublicationId.Value,
            retainedByPublicationId = preparation.PublicationId,
            tables,
        });

        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE publication_surface_bindings
            SET binding_kind = 'retained_published_tables',
                binding_json = @binding,
                status = 'ready',
                built_at = @now
            WHERE publication_id = @publicationId
              AND surface_name = 'band_rankings'
            """;
        command.Parameters.Add(
            "binding",
            NpgsqlDbType.Jsonb).Value = bindingJson;
        command.Parameters.AddWithValue("now", DateTime.UtcNow);
        command.Parameters.AddWithValue(
            "publicationId",
            preparation.CurrentPublicationId.Value);
        command.ExecuteNonQuery();
    }

    private static void RetainPublicationArtifacts(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long currentPublicationId,
        long? previousPublicationId)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            DELETE FROM publication_api_response_cache
            WHERE publication_id <> @currentPublicationId
              AND (
                  @previousPublicationId IS NULL
                  OR publication_id <> @previousPublicationId
              );

            DELETE FROM publication_api_response_cache_staging
            WHERE publication_id <> @currentPublicationId
              AND (
                  @previousPublicationId IS NULL
                  OR publication_id <> @previousPublicationId
              );

            DELETE FROM publication_song_catalog
            WHERE publication_id <> @currentPublicationId
              AND (
                  @previousPublicationId IS NULL
                  OR publication_id <> @previousPublicationId
              );

            UPDATE publication_surface_bindings
            SET binding_kind = 'retired_generation_catalog',
                binding_json = jsonb_build_object(
                    'table', 'publication_song_catalog',
                    'retired', true),
                row_count = 0,
                content_hash = NULL,
                status = 'retired',
                built_at = @now
            WHERE surface_name = 'song_catalog'
              AND publication_id <> @currentPublicationId
              AND (
                  @previousPublicationId IS NULL
                  OR publication_id <> @previousPublicationId
              )
              AND status <> 'retired';

            UPDATE publication_surface_bindings
            SET binding_kind = 'retired_generation_cache',
                binding_json = jsonb_build_object(
                    'table', 'publication_api_response_cache',
                    'retired', true),
                row_count = 0,
                content_hash = NULL,
                status = 'retired',
                built_at = @now
            WHERE surface_name = 'api_response_cache'
              AND publication_id <> @currentPublicationId
              AND (
                  @previousPublicationId IS NULL
                  OR publication_id <> @previousPublicationId
              )
              AND status <> 'retired';

            UPDATE publication_surface_bindings
            SET binding_kind = 'retired_band_tables',
                binding_json = jsonb_build_object(
                    'retired', true),
                row_count = 0,
                content_hash = NULL,
                status = 'retired',
                built_at = @now
            WHERE surface_name = 'band_rankings'
              AND publication_id <> @currentPublicationId
              AND (
                  @previousPublicationId IS NULL
                  OR publication_id <> @previousPublicationId
              )
              AND status <> 'retired';
            """;
        command.Parameters.AddWithValue(
            "currentPublicationId",
            currentPublicationId);
        command.Parameters.Add(
            "previousPublicationId",
            NpgsqlDbType.Bigint).Value =
            previousPublicationId.HasValue
                ? previousPublicationId.Value
                : DBNull.Value;
        command.Parameters.AddWithValue("now", DateTime.UtcNow);
        command.ExecuteNonQuery();
    }

    private void RecordFailedScrapeWithDegradedIsolation(
        long scrapeId,
        string phase,
        string message,
        PublicationCommitIntentHandle commitIntent)
    {
        var deadline = Stopwatch.StartNew();
        var recoveryBudget = TimeSpan.FromSeconds(15);
        while (deadline.Elapsed < recoveryBudget)
        {
            HeartbeatPublicationCommitIntent(commitIntent);
            using var conn = _ds.OpenConnection();
            using var tx = conn.BeginTransaction();
            using (var timeout = conn.CreateCommand())
            {
                timeout.Transaction = tx;
                timeout.CommandText = """
                    SELECT set_config('lock_timeout', '2s', true);
                    SELECT set_config('statement_timeout', '10s', true);
                    """;
                timeout.ExecuteNonQuery();
            }

            var exclusive =
                TryAcquirePublicationAdvisoryLock(
                    conn,
                    tx,
                    shared: false);
            var shared = !exclusive
                && TryAcquirePublicationAdvisoryLock(
                    conn,
                    tx,
                    shared: true);
            if (!exclusive && !shared)
            {
                tx.Rollback();
                Thread.Sleep(50);
                continue;
            }

            try
            {
                RecordFailedScrapeState(
                    conn,
                    tx,
                    scrapeId,
                    phase,
                    message);
                tx.Commit();
                return;
            }
            catch (PostgresException ex)
                when (ex.SqlState is
                    PostgresErrorCodes.LockNotAvailable
                    or PostgresErrorCodes.DeadlockDetected
                    or PostgresErrorCodes.QueryCanceled)
            {
                TryRollback(tx);
                Thread.Sleep(50);
            }
        }

        throw new InvalidOperationException(
            $"Failed scrape {scrapeId} could not be durably isolated within {recoveryBudget}.");
    }

    private void MarkFailedScrapeCandidateDurably(
        long scrapeId,
        string phase,
        string message)
    {
        var deadline = Stopwatch.StartNew();
        var recoveryBudget = TimeSpan.FromSeconds(15);
        while (deadline.Elapsed < recoveryBudget)
        {
            using var conn = _ds.OpenConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                using (var timeout = conn.CreateCommand())
                {
                    timeout.Transaction = tx;
                    timeout.CommandText = """
                        SELECT set_config('lock_timeout', '2s', true);
                        SELECT set_config('statement_timeout', '10s', true);
                        """;
                    timeout.ExecuteNonQuery();
                }

                using (var scrape = conn.CreateCommand())
                {
                    scrape.Transaction = tx;
                    scrape.CommandText = """
                        UPDATE scrape_log
                        SET status = 'failed',
                            failed_at = COALESCE(failed_at, @now),
                            failure_phase = @phase,
                            failure_message = @message
                        WHERE id = @scrapeId
                          AND NOT EXISTS (
                              SELECT 1
                              FROM scrape_publication_state
                              WHERE id = TRUE
                                AND published_scrape_id = @scrapeId
                          )
                        """;
                    scrape.Parameters.AddWithValue(
                        "now",
                        DateTime.UtcNow);
                    scrape.Parameters.AddWithValue("phase", phase);
                    scrape.Parameters.AddWithValue("message", message);
                    scrape.Parameters.AddWithValue(
                        "scrapeId",
                        checked((int)scrapeId));
                    scrape.ExecuteNonQuery();
                }

                using var generation = conn.CreateCommand();
                generation.Transaction = tx;
                generation.CommandText = """
                    UPDATE publication_generations
                    SET status = 'failed',
                        failed_at = COALESCE(failed_at, @now),
                        failure_phase = @phase,
                        failure_message = @message
                    WHERE scrape_id = @scrapeId
                      AND status NOT IN (
                          'current',
                          'retained',
                          'retired')
                    """;
                generation.Parameters.AddWithValue(
                    "now",
                    DateTime.UtcNow);
                generation.Parameters.AddWithValue("phase", phase);
                generation.Parameters.AddWithValue("message", message);
                generation.Parameters.AddWithValue(
                    "scrapeId",
                    scrapeId);
                generation.ExecuteNonQuery();
                tx.Commit();
                return;
            }
            catch (PostgresException ex)
                when (ex.SqlState is
                    PostgresErrorCodes.LockNotAvailable
                    or PostgresErrorCodes.DeadlockDetected
                    or PostgresErrorCodes.QueryCanceled)
            {
                TryRollback(tx);
                Thread.Sleep(50);
            }
        }

        throw new InvalidOperationException(
            $"Failed scrape {scrapeId} could not be durably marked failed within {recoveryBudget}.");
    }

    private static void RecordFailedScrapeState(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long scrapeId,
        string phase,
        string message)
    {
        using (var scrape = conn.CreateCommand())
        {
            scrape.Transaction = tx;
            scrape.CommandText = """
                UPDATE scrape_log
                SET status = 'failed',
                    failed_at = COALESCE(failed_at, @now),
                    failure_phase = @phase,
                    failure_message = @message
                WHERE id = @scrapeId
                  AND NOT EXISTS (
                      SELECT 1
                      FROM scrape_publication_state
                      WHERE id = TRUE
                        AND published_scrape_id = @scrapeId
                  )
                """;
            scrape.Parameters.AddWithValue("now", DateTime.UtcNow);
            scrape.Parameters.AddWithValue("phase", phase);
            scrape.Parameters.AddWithValue("message", message);
            scrape.Parameters.AddWithValue(
                "scrapeId",
                checked((int)scrapeId));
            scrape.ExecuteNonQuery();
        }

        using (var generation = conn.CreateCommand())
        {
            generation.Transaction = tx;
            generation.CommandText = """
                UPDATE publication_generations
                SET status = 'failed',
                    failed_at = COALESCE(failed_at, @now),
                    failure_phase = @phase,
                    failure_message = @message
                WHERE scrape_id = @scrapeId
                  AND status NOT IN (
                      'current',
                      'retained',
                      'retired')
                """;
            generation.Parameters.AddWithValue(
                "now",
                DateTime.UtcNow);
            generation.Parameters.AddWithValue("phase", phase);
            generation.Parameters.AddWithValue("message", message);
            generation.Parameters.AddWithValue(
                "scrapeId",
                scrapeId);
            generation.ExecuteNonQuery();
        }

        using var pointer = conn.CreateCommand();
        pointer.Transaction = tx;
        pointer.CommandText = """
            UPDATE scrape_publication_state publication
            SET working_publication_id = NULL,
                updated_at = @now
            FROM publication_generations generation
            WHERE publication.id = TRUE
              AND generation.scrape_id = @scrapeId
              AND publication.working_publication_id =
                    generation.publication_id
            """;
        pointer.Parameters.AddWithValue("now", DateTime.UtcNow);
        pointer.Parameters.AddWithValue("scrapeId", scrapeId);
        pointer.ExecuteNonQuery();
    }

    private void CleanupFailedPublicationArtifacts(long scrapeId)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        ApplyPublicationCleanupTimeouts(conn, tx);
        if (!TryAcquirePublicationAdvisoryLock(conn, tx, shared: true))
        {
            tx.Rollback();
            return;
        }

        long? publicationId;
        using (var generation = conn.CreateCommand())
        {
            generation.Transaction = tx;
            generation.CommandText = """
                SELECT publication_id
                FROM publication_generations
                WHERE scrape_id = @scrapeId
                  AND status = 'failed'
                """;
            generation.Parameters.AddWithValue("scrapeId", scrapeId);
            var value = generation.ExecuteScalar();
            publicationId =
                value is null or DBNull
                    ? null
                    : Convert.ToInt64(value);
        }

        if (!publicationId.HasValue)
        {
            tx.Commit();
            return;
        }

        using (var cleanup = conn.CreateCommand())
        {
            cleanup.Transaction = tx;
            cleanup.CommandText = """
                DELETE FROM publication_api_response_cache_staging
                WHERE publication_id = @publicationId;
                DELETE FROM publication_api_response_cache
                WHERE publication_id = @publicationId;
                DELETE FROM publication_song_catalog
                WHERE publication_id = @publicationId;

                UPDATE publication_surface_bindings
                SET binding_kind = CASE
                        WHEN surface_name = 'song_catalog'
                            THEN 'failed_generation_catalog'
                        WHEN surface_name = 'api_response_cache'
                            THEN 'failed_generation_cache'
                        ELSE binding_kind
                    END,
                    binding_json = binding_json
                        || jsonb_build_object(
                            'failed', true,
                            'retained', false),
                    row_count = CASE
                        WHEN surface_name IN (
                            'song_catalog',
                            'api_response_cache')
                            THEN 0
                        ELSE row_count
                    END,
                    content_hash = CASE
                        WHEN surface_name IN (
                            'song_catalog',
                            'api_response_cache')
                            THEN NULL
                        ELSE content_hash
                    END,
                    status = 'failed',
                    built_at = @now
                WHERE publication_id = @publicationId;
                """;
            cleanup.Parameters.AddWithValue(
                "publicationId",
                publicationId.Value);
            cleanup.Parameters.AddWithValue("now", DateTime.UtcNow);
            cleanup.ExecuteNonQuery();
        }

        DropPreparedBandTables(
            conn,
            tx,
            publicationId.Value);
        tx.Commit();
    }

    private static void DropPreparedBandTables(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long publicationId)
    {
        var statements = BandRankingStorageNames.AllBandTypes
            .SelectMany(bandType => new[]
            {
                BandRankingStorageNames.GetPreparedPublishedRankingTable(
                    publicationId,
                    bandType),
                BandRankingStorageNames.GetPreparedPublishedStatsTable(
                    publicationId,
                    bandType),
            })
            .Select(table =>
                $"DROP TABLE IF EXISTS {BandRankingStorageNames.QuoteIdentifier(table)}");
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            string.Join(";" + Environment.NewLine, statements) + ";";
        command.ExecuteNonQuery();
    }

    private static void DropRetainedBandTables(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long publicationId)
    {
        var statements = BandRankingStorageNames.AllBandTypes
            .SelectMany(bandType => new[]
            {
                BandRankingStorageNames.GetRetainedPublishedRankingTable(
                    publicationId,
                    bandType),
                BandRankingStorageNames.GetRetainedPublishedStatsTable(
                    publicationId,
                    bandType),
            })
            .Select(table =>
                $"DROP TABLE IF EXISTS {BandRankingStorageNames.QuoteIdentifier(table)}");
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            string.Join(";" + Environment.NewLine, statements) + ";";
        command.ExecuteNonQuery();
    }

    private void DelayPublicationCommitRetryOrThrow(
        PublicationPreparationResult preparation,
        Stopwatch drainStopwatch,
        Stopwatch? cutoverStopwatch,
        int lockRejections,
        int relationLockRetries)
    {
        if (cutoverStopwatch is not null)
        {
            EnsurePublicationCutoverBudget(
                preparation,
                cutoverStopwatch,
                relationLockRetries);
            Thread.Sleep(
                Math.Min(
                    _publicationCommitOptions
                        .RetryDelayMilliseconds,
                    Math.Max(
                        1,
                        (int)GetRemainingPublicationCutoverBudget(
                                preparation,
                                cutoverStopwatch,
                                relationLockRetries)
                            .TotalMilliseconds)));
            return;
        }

        var drainTimeout = TimeSpan.FromMilliseconds(
            _publicationCommitOptions.DrainTimeoutMilliseconds);
        if (drainStopwatch.Elapsed >= drainTimeout)
        {
            drainStopwatch.Stop();
            _log.LogError(
                "Publication {PublicationId} failed to obtain a bounded final commit window. DrainElapsedMs={DrainElapsedMs:N3}, LockRejections={LockRejections}, RelationLockRetries={RelationLockRetries}.",
                preparation.PublicationId,
                drainStopwatch.Elapsed.TotalMilliseconds,
                lockRejections,
                relationLockRetries);
            throw new PublicationCommitBusyException(
                $"Publication {preparation.PublicationId} could not obtain a bounded final commit window within {drainTimeout}.",
                drainStopwatch.Elapsed,
                lockRejections,
                relationLockRetries);
        }

        Thread.Sleep(_publicationCommitOptions.RetryDelayMilliseconds);
    }

    private TimeSpan GetRemainingPublicationCutoverBudget(
        PublicationPreparationResult preparation,
        Stopwatch cutoverStopwatch,
        int relationLockRetries)
    {
        var budget = TimeSpan.FromMilliseconds(
            _publicationCommitOptions
                .MaxExclusiveLockDurationMilliseconds);
        var remaining = budget - cutoverStopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            throw new PublicationCommitDeadlineExceededException(
                $"Publication {preparation.PublicationId} exhausted its cumulative {budget} final-cutover budget.",
                cutoverStopwatch.Elapsed,
                budget,
                relationLockRetries);
        }

        return remaining;
    }

    private void EnsurePublicationCutoverBudget(
        PublicationPreparationResult preparation,
        Stopwatch? cutoverStopwatch,
        int relationLockRetries)
    {
        if (cutoverStopwatch is null)
            return;

        _ = GetRemainingPublicationCutoverBudget(
            preparation,
            cutoverStopwatch,
            relationLockRetries);
    }

    private static bool TryAcquirePublicationAdvisoryLock(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        bool shared)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = shared
            ? "SELECT pg_try_advisory_xact_lock_shared(@lockKey)"
            : "SELECT pg_try_advisory_xact_lock(@lockKey)";
        command.Parameters.AddWithValue(
            "lockKey",
            PublicationGenerationSchema.AdvisoryLockKey);
        return command.ExecuteScalar() is true;
    }

    private void ApplyPublicationCommitTimeouts(
        NpgsqlConnection conn,
        NpgsqlTransaction tx)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT set_config('lock_timeout', @lockTimeout, true);
            SELECT set_config('statement_timeout', @statementTimeout, true);
            """;
        command.Parameters.AddWithValue(
            "lockTimeout",
            $"{_publicationCommitOptions.RelationLockTimeoutMilliseconds}ms");
        command.Parameters.AddWithValue(
            "statementTimeout",
            $"{_publicationCommitOptions.StatementTimeoutMilliseconds}ms");
        command.ExecuteNonQuery();
    }

    private void ApplyPublicationPreparationTimeouts(
        NpgsqlConnection conn,
        NpgsqlTransaction tx)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT set_config('lock_timeout', @lockTimeout, true);
            SELECT set_config(
                'statement_timeout',
                @statementTimeout,
                true);
            SELECT set_config(
                'transaction_timeout',
                @transactionTimeout,
                true);
            """;
        command.Parameters.AddWithValue(
            "lockTimeout",
            $"{_publicationCommitOptions.PreparationLockTimeoutMilliseconds}ms");
        command.Parameters.AddWithValue(
            "statementTimeout",
            $"{_publicationCommitOptions.PreparationStatementTimeoutMilliseconds}ms");
        command.Parameters.AddWithValue(
            "transactionTimeout",
            $"{_publicationCommitOptions.PreparationTransactionTimeoutMilliseconds}ms");
        command.ExecuteNonQuery();
    }

    private void ApplyPublicationCutoverBudget(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        TimeSpan remaining)
    {
        var remainingMilliseconds = Math.Max(
            1,
            (int)Math.Floor(remaining.TotalMilliseconds));
        var lockTimeoutMilliseconds = Math.Max(
            1,
            Math.Min(
                _publicationCommitOptions
                    .RelationLockTimeoutMilliseconds,
                remainingMilliseconds));
        var statementTimeoutMilliseconds = Math.Max(
            1,
            Math.Min(
                _publicationCommitOptions
                    .StatementTimeoutMilliseconds,
                remainingMilliseconds));

        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT set_config(
                'lock_timeout',
                @lockTimeout,
                true);
            SELECT set_config(
                'statement_timeout',
                @statementTimeout,
                true);
            SELECT set_config(
                'transaction_timeout',
                @transactionTimeout,
                true);
            """;
        command.Parameters.AddWithValue(
            "lockTimeout",
            $"{lockTimeoutMilliseconds}ms");
        command.Parameters.AddWithValue(
            "statementTimeout",
            $"{statementTimeoutMilliseconds}ms");
        command.Parameters.AddWithValue(
            "transactionTimeout",
            $"{remainingMilliseconds}ms");
        command.ExecuteNonQuery();
    }

    private static void ApplyPublicationCleanupTimeouts(
        NpgsqlConnection conn,
        NpgsqlTransaction tx)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT set_config('lock_timeout', '1s', true);
            SELECT set_config('statement_timeout', '30s', true);
            """;
        command.ExecuteNonQuery();
    }

    private static bool IsRetryablePublicationRelationLockFailure(
        PostgresException exception) =>
        exception.SqlState is
            PostgresErrorCodes.LockNotAvailable
            or PostgresErrorCodes.DeadlockDetected;

    private static void TryRollback(NpgsqlTransaction transaction)
    {
        try
        {
            transaction.Rollback();
        }
        catch
        {
        }
    }

    private void ValidatePublicationCommitOptions()
    {
        if (_publicationCommitOptions.DrainTimeoutMilliseconds <= 0
            || _publicationCommitOptions.RetryDelayMilliseconds <= 0
            || _publicationCommitOptions.RelationLockTimeoutMilliseconds <= 0
            || _publicationCommitOptions.StatementTimeoutMilliseconds <= 0
            || _publicationCommitOptions
                .MaxExclusiveLockDurationMilliseconds <= 0
            || _publicationCommitOptions
                .StaleCommitIntentSeconds <= 0
            || _publicationCommitOptions.ContentionRetryAttempts <= 0
            || _publicationCommitOptions
                .ContentionRetryDelayMilliseconds <= 0
            || _publicationCommitOptions.DefaultReadLeaseSeconds <= 0
            || _publicationCommitOptions.ExportReadLeaseSeconds <= 0
            || _publicationCommitOptions.AbandonedReadyGraceSeconds <= 0
            || _publicationCommitOptions.WorkerHeartbeatFreshSeconds <= 0
            || _publicationCommitOptions
                .PreparationLockTimeoutMilliseconds <= 0
            || _publicationCommitOptions
                .PreparationStatementTimeoutMilliseconds <= 0
            || _publicationCommitOptions
                .PreparationTransactionTimeoutMilliseconds <= 0
            || _publicationCommitOptions
                .NotificationRecoveryRetrySeconds <= 0)
        {
            throw new InvalidOperationException(
                "Publication commit timeout options must all be positive.");
        }
    }

    private static void ValidateNotificationGate(
        long scrapeId,
        long? publishedScrapeId,
        long? notificationScrapeId,
        string? notificationStatus)
    {
        if (!publishedScrapeId.HasValue)
            return;

        var notificationComplete =
            notificationScrapeId is null
            && notificationStatus is null or "disabled"
            || notificationScrapeId == publishedScrapeId
            && notificationStatus is "completed" or "disabled";
        if (!notificationComplete)
        {
            throw new InvalidOperationException(
                $"Scrape {scrapeId} cannot be published while improvement notifications " +
                $"for published scrape {publishedScrapeId} are incomplete " +
                $"(marker={notificationScrapeId?.ToString() ?? "null"}, " +
                $"status={notificationStatus ?? "null"}).");
        }
    }

    private sealed record PublicationStateSnapshot(
        long? PublishedScrapeId,
        long? NotificationScrapeId,
        string? NotificationStatus,
        long? CurrentPublicationId,
        long? PreviousPublicationId,
        long? WorkingPublicationId);

    private sealed record PreparedBandTableSet(
        string BandType,
        string RankingTable,
        string StatsTable);

    private sealed record PublicationCommitIntentObservation(
        bool Pending,
        bool FailureIsolationPending,
        long? ScrapeId,
        DateTime? FrozenAtUtc,
        DateTime? StartedAtUtc,
        DateTime? HeartbeatAtUtc,
        string? OwnerToken);
}
