using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Tests.Unit;

public sealed class MetaDatabaseTests : IDisposable
{
    private readonly InMemoryMetaDatabase _fixture = new();
    private Persistence.MetaDatabase Db => _fixture.Db;
    private NpgsqlDataSource DataSource => _fixture.DataSource;

    public void Dispose() => _fixture.Dispose();

    // ═══ ScrapeLog ══════════════════════════════════════════════

    [Fact]
    public void StartScrapeRun_returns_positive_id()
    {
        var id = Db.StartScrapeRun();
        Assert.True(id > 0);
    }

    [Fact]
    public void StartScrapeRun_allocates_working_publication_generation()
    {
        var scrapeId = Db.StartScrapeRun();

        var pointer = Db.GetPublicationPointerState();
        var generation = Db.GetPublicationGenerationForScrape(scrapeId);

        Assert.NotNull(generation);
        Assert.Equal(PublicationGenerationStatus.Building, generation!.Status);
        Assert.Equal(scrapeId, generation.ScrapeId);
        Assert.Equal(generation.PublicationId, pointer.WorkingPublicationId);
        Assert.Null(pointer.CurrentPublicationId);
    }

    [Fact]
    public async Task StartScrapeRun_captures_immutable_working_song_catalog()
    {
        var persistence = new FestivalPersistence(DataSource);
        var token = await persistence.SaveSongsVersionedAsync(
        [
            CreateCatalogSong("song-a", "Alpha"),
        ]);
        var liveBefore = ReadLiveSongCatalog();

        var scrapeId = Db.StartScrapeRun(token);
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        var captured = ReadPublicationSongCatalog(publicationId);

        Assert.Equal(liveBefore.ContentHash, captured.ContentHash);
        Assert.Equal(liveBefore.SongCount, captured.SongCount);
        Assert.Equal(liveBefore.CatalogVersion, captured.CatalogVersion);
        Assert.Equal(token.CatalogVersion, captured.CatalogVersion);
        Assert.True(captured.IsExact);
        Assert.Equal("provider_exact", captured.SourceKind);
        var binding = Db.GetPublicationSurfaceBindings(publicationId)
            .Single(static item => item.SurfaceName == "song_catalog");
        Assert.Equal("generation_catalog_snapshot", binding.BindingKind);
        Assert.Equal("ready", binding.Status);
        Assert.Equal(captured.ContentHash, binding.ContentHash);
        Assert.Equal(captured.SongCount, binding.RowCount);

        await persistence.SaveSongsAsync(
        [
            CreateCatalogSong("song-a", "Alpha changed"),
            CreateCatalogSong("song-b", "Beta"),
        ]);
        var liveAfter = ReadLiveSongCatalog();
        var stillCaptured = ReadPublicationSongCatalog(publicationId);

        Assert.NotEqual(liveBefore.ContentHash, liveAfter.ContentHash);
        Assert.Equal(captured, stillCaptured);

        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);

        var publishedBinding = Db.GetPublicationSurfaceBindings(publicationId)
            .Single(static item => item.SurfaceName == "song_catalog");
        Assert.Equal("generation_catalog_snapshot", publishedBinding.BindingKind);
        Assert.Equal("ready", publishedBinding.Status);
        Assert.Equal(captured.ContentHash, publishedBinding.ContentHash);
        Assert.Equal(captured, ReadPublicationSongCatalog(publicationId));
    }

    [Fact]
    public void Publication_surface_source_evidence_matches_generation_sources()
    {
        var scrapeId = Db.StartScrapeRun();
        Db.BulkSetCachedResponses(
        [
            (
                Key: "public-route:/api/example",
                Json: new byte[] { 1, 2, 3 },
                ETag: "\"example\""),
        ]);
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false);
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        var bindings = Db.GetPublicationSurfaceBindings(publicationId)
            .ToDictionary(static binding => binding.SurfaceName);

        foreach (var surfaceName in new[]
                 {
                     PublicationSurfaceNames.ApiResponseCache,
                     PublicationSurfaceNames.SongCatalog,
                 })
        {
            var evidence = Db.GetPublicationSurfaceSourceEvidence(
                publicationId,
                surfaceName);
            Assert.NotNull(evidence);
            Assert.True(evidence!.Exists);
            Assert.Equal(publicationId, evidence.PublicationId);
            Assert.Equal(scrapeId, evidence.ScrapeId);
            Assert.Equal(bindings[surfaceName].RowCount, evidence.RowCount);
            Assert.Equal(
                bindings[surfaceName].ContentHash,
                evidence.ContentHash);
        }

        var soloEvidence = Db.GetPublicationSurfaceSourceEvidence(
            publicationId,
            PublicationSurfaceNames.SoloScopeSources);
        Assert.NotNull(soloEvidence);
        Assert.False(soloEvidence!.Exists);
        Assert.Equal(0, soloEvidence.RowCount);
    }

    [Fact]
    public void Solo_scope_source_evidence_ignores_later_mutable_fingerprints()
    {
        using (var conn = DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO scrape_log (id, started_at, completed_at, status)
                VALUES
                    (700, now() - interval '5 minutes', now(), 'completed'),
                    (701, now() - interval '1 minute', NULL, 'running')
                ON CONFLICT (id) DO NOTHING;

                INSERT INTO leaderboard_scope_fingerprints (
                    song_id,
                    instrument,
                    scope_kind,
                    fingerprint_version,
                    source_scrape_id,
                    published_scrape_id,
                    first_seen_scrape_id,
                    last_changed_scrape_id,
                    last_seen_scrape_id,
                    is_complete,
                    entry_count,
                    reported_total_entries,
                    reported_total_pages,
                    content_fingerprint,
                    coverage_fingerprint,
                    changed_at,
                    seen_at)
                VALUES (
                    'readiness-song',
                    'Solo_Guitar',
                    'alltime',
                    2,
                    701,
                    NULL,
                    701,
                    701,
                    701,
                    TRUE,
                    2,
                    2,
                    1,
                    'candidate-content',
                    'candidate-coverage',
                    now(),
                    now())
                ON CONFLICT (song_id, instrument, scope_kind)
                DO UPDATE SET
                    last_seen_scrape_id = EXCLUDED.last_seen_scrape_id,
                    is_complete = EXCLUDED.is_complete,
                    entry_count = EXCLUDED.entry_count,
                    reported_total_entries =
                        EXCLUDED.reported_total_entries,
                    reported_total_pages =
                        EXCLUDED.reported_total_pages,
                    content_fingerprint =
                        EXCLUDED.content_fingerprint,
                    coverage_fingerprint =
                        EXCLUDED.coverage_fingerprint,
                    changed_at = EXCLUDED.changed_at,
                    seen_at = EXCLUDED.seen_at;

                INSERT INTO leaderboard_published_scope_source (
                    published_scrape_id,
                    song_id,
                    instrument,
                    scope_kind,
                    source_kind,
                    source_snapshot_id,
                    source_scrape_id,
                    is_complete,
                    row_count,
                    reported_total_entries,
                    reported_total_pages,
                    content_fingerprint,
                    coverage_fingerprint,
                    created_at,
                    validated_at)
                VALUES (
                    700,
                    'readiness-song',
                    'Solo_Guitar',
                    'alltime',
                    'snapshot',
                    699,
                    699,
                    TRUE,
                    1,
                    1,
                    1,
                    'published-content',
                    'published-coverage',
                    now(),
                    now())
                ON CONFLICT (
                    published_scrape_id,
                    song_id,
                    instrument,
                    scope_kind)
                DO UPDATE SET
                    source_scrape_id = EXCLUDED.source_scrape_id,
                    is_complete = EXCLUDED.is_complete,
                    row_count = EXCLUDED.row_count,
                    reported_total_entries =
                        EXCLUDED.reported_total_entries,
                    content_fingerprint =
                        EXCLUDED.content_fingerprint,
                    coverage_fingerprint =
                        EXCLUDED.coverage_fingerprint,
                    created_at = EXCLUDED.created_at,
                    validated_at = EXCLUDED.validated_at;

                INSERT INTO publication_generations (
                    publication_id,
                    scrape_id,
                    status,
                    created_at,
                    source_cut_at,
                    ready_at,
                    published_at)
                VALUES (
                    700,
                    700,
                    'current',
                    now() - interval '5 minutes',
                    now() - interval '2 minutes',
                    now() - interval '1 minute',
                    now())
                ON CONFLICT (publication_id) DO UPDATE SET
                    scrape_id = EXCLUDED.scrape_id,
                    status = EXCLUDED.status,
                    source_cut_at = EXCLUDED.source_cut_at,
                    ready_at = EXCLUDED.ready_at,
                    published_at = EXCLUDED.published_at;
                """;
            cmd.ExecuteNonQuery();
        }

        var evidence = Db.GetPublicationSurfaceSourceEvidence(
            700,
            PublicationSurfaceNames.SoloScopeSources);

        Assert.NotNull(evidence);
        Assert.True(evidence!.Exists);
        Assert.Equal(1, evidence.RowCount);
        Assert.Equal(700, evidence.ScrapeId);
    }

    [Fact]
    public async Task StartScrapeRun_rejects_cross_process_catalog_race()
    {
        var workerPersistence = new FestivalPersistence(DataSource);
        var servicePersistence = new FestivalPersistence(DataSource);
        var workerSongs =
            new[] { CreateCatalogSong("song-a", "Worker catalog") };
        var serviceSongs =
            new[] { CreateCatalogSong("song-a", "Service catalog") };
        var workerToken =
            await workerPersistence.SaveSongsVersionedAsync(workerSongs);
        var serviceToken =
            await servicePersistence.SaveSongsVersionedAsync(serviceSongs);
        var scrapeCountBefore = CountScrapeRuns();

        var exception = Assert.Throws<InvalidOperationException>(
            () => Db.StartScrapeRun(workerToken));

        Assert.Contains(
            "persisted song catalog changed before scrape allocation",
            exception.Message);
        Assert.Equal(scrapeCountBefore, CountScrapeRuns());
        Assert.Null(Db.GetPublicationPointerState().WorkingPublicationId);
        Assert.Throws<InvalidOperationException>(() =>
            SongCatalogSnapshotBuilder.ValidateToken(
                SongCatalogSnapshotBuilder.Create(serviceSongs),
                workerToken));

        var scrapeId = Db.StartScrapeRun(serviceToken);
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        var captured = ReadPublicationSongCatalog(publicationId);
        Assert.Equal(serviceToken.CatalogVersion, captured.CatalogVersion);
        Assert.Equal(serviceToken.ContentHash, captured.ContentHash);
        Assert.True(captured.IsExact);
    }

    [Fact]
    public async Task Catalog_writer_and_publication_allocation_share_lock()
    {
        var persistence = new FestivalPersistence(DataSource);
        using var lockConn = DataSource.OpenConnection();
        using var lockTx = lockConn.BeginTransaction();
        using (var acquire = lockConn.CreateCommand())
        {
            acquire.Transaction = lockTx;
            acquire.CommandText =
                "SELECT pg_advisory_xact_lock(@lockKey)";
            acquire.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            acquire.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<SongCatalogPersistenceBusyException>(
            () => persistence.SaveSongsVersionedAsync(
            [
                CreateCatalogSong("song-a", "Blocked writer"),
            ]).WaitAsync(TimeSpan.FromSeconds(5)));

        lockTx.Commit();
        var token = await persistence.SaveSongsVersionedAsync(
        [
            CreateCatalogSong("song-a", "Retried writer"),
        ]).WaitAsync(TimeSpan.FromSeconds(5));
        var scrapeId = Db.StartScrapeRun(token);

        Assert.True(scrapeId > 0);
    }

    [Fact]
    public async Task FailScrapeRun_waits_for_publication_read_leases()
    {
        var publishedId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(publishedId, 1, 1, 1, 1);
        Db.PublishScrapeRun(publishedId, promoteCachedResponses: false);
        var candidateId = Db.StartScrapeRun();

        using var readConn = DataSource.OpenConnection();
        using var readTx = readConn.BeginTransaction();
        using (var acquire = readConn.CreateCommand())
        {
            acquire.Transaction = readTx;
            acquire.CommandText =
                "SELECT pg_advisory_xact_lock_shared(@lockKey)";
            acquire.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            acquire.ExecuteNonQuery();
        }

        var failTask = Task.Run(() => Db.FailScrapeRun(
            candidateId,
            MetaDatabase.PostProcessReadIsolationFailurePhase,
            "test failure"));
        await Task.Delay(100);
        Assert.False(failTask.IsCompleted);

        readTx.Commit();
        await failTask.WaitAsync(TimeSpan.FromSeconds(5));

        var isolation = Db.GetFailedCandidateReadIsolationState();
        Assert.True(isolation.IsFrozen);
        Assert.Equal(candidateId, isolation.ScrapeId);
    }

    [Fact]
    public async Task Legacy_rollback_writer_invalidates_exact_catalog_token()
    {
        var persistence = new FestivalPersistence(DataSource);
        var token = await persistence.SaveSongsVersionedAsync(
        [
            CreateCatalogSong("song-a", "Exact catalog"),
        ]);

        using (var conn = DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO live_song_catalog (
                    id, catalog_json, content_hash, song_count, captured_at)
                VALUES (
                    TRUE,
                    '{"schemaVersion":1,"songs":[]}'::jsonb,
                    repeat('a', 64),
                    0,
                    now())
                ON CONFLICT (id) DO UPDATE SET
                    catalog_json = EXCLUDED.catalog_json,
                    content_hash = EXCLUDED.content_hash,
                    song_count = EXCLUDED.song_count,
                    captured_at = EXCLUDED.captured_at
                """;
            cmd.ExecuteNonQuery();
        }

        var downgraded = ReadLiveSongCatalog();
        Assert.NotEqual(token.CatalogVersion, downgraded.CatalogVersion);
        Assert.False(downgraded.IsExact);
        Assert.Equal(1, downgraded.SchemaVersion);
        Assert.Equal(
            "legacy_columns_reconstructed",
            downgraded.SourceKind);
        var exception = Assert.Throws<InvalidOperationException>(
            () => Db.StartScrapeRun(token));
        Assert.Contains(
            "reconstructed or obsolete song catalog",
            exception.Message);
    }

    [Fact]
    public void CompleteScrapeRun_updates_record()
    {
        var id = Db.StartScrapeRun();
        Db.CompleteScrapeRun(id, 100, 50_000, 200, 1_000_000);

        var last = Db.GetLastCompletedScrapeRun();
        Assert.NotNull(last);
        Assert.Equal(id, last.Id);
        Assert.Equal(100, last.SongsScraped);
        Assert.Equal(50_000, last.TotalEntries);
        Assert.NotNull(last.CompletedAt);
        Assert.False(last.EpicReportedOver100Pages);
        Assert.Equal("completed", last.Status);
    }

    [Fact]
    public async Task PublishScrapeRun_AtomicallyStampsBandProjectionGeneration()
    {
        var projection = new BandCurrentProjectionBuilder(
            DataSource,
            Substitute.For<ILogger<BandCurrentProjectionBuilder>>());
        await projection.EnsureSchemaAsync();
        using (var conn = DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO band_current_projection_state (
                    id, current_generation, row_count, scope_count,
                    failed_scope_count, updated_at)
                VALUES (TRUE, 42, 0, 0, 0, now())
                ON CONFLICT (id) DO UPDATE SET
                    current_generation = EXCLUDED.current_generation,
                    updated_at = EXCLUDED.updated_at
                """;
            cmd.ExecuteNonQuery();
        }

        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);

        Assert.True(Db.IsBandCurrentProjectionGloballyPublished());

        using (var conn = DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE band_current_projection_state
                SET current_generation = 43,
                    updated_at = now()
                WHERE id = TRUE
                """;
            cmd.ExecuteNonQuery();
        }

        Assert.False(Db.IsBandCurrentProjectionGloballyPublished());
    }

    [Fact]
    public void PublishScrapeRun_AtomicallyQueuesImprovementNotifications()
    {
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);

        Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true,
            improvementNotificationProjectionScopes:
            [
                new SoloCurrentProjectionScopeKey("song-1", "Solo_Guitar"),
            ]);

        using var conn = DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT published_scrape_id,
                   improvement_notifications_scrape_id,
                   improvement_notifications_status,
                   improvement_notifications_attempt_count,
                   improvement_notifications_projection_ready,
                   improvement_notifications_projection_scopes::text
            FROM scrape_publication_state
            WHERE id = TRUE;
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal((int)scrapeId, reader.GetInt32(0));
        Assert.Equal((int)scrapeId, reader.GetInt32(1));
        Assert.Equal("pending", reader.GetString(2));
        Assert.Equal(0, reader.GetInt32(3));
        Assert.True(reader.GetBoolean(4));
        Assert.Contains("\"SongId\": \"song-1\"", reader.GetString(5));
        Assert.Contains("\"Instrument\": \"Solo_Guitar\"", reader.GetString(5));
    }

    [Fact]
    public void PublishScrapeRun_RequiresProjectionPlanWhenNotificationsAreQueued()
    {
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Db.PublishScrapeRun(
                scrapeId,
                promoteCachedResponses: false,
                queueImprovementNotifications: true));

        Assert.Contains("projection scope plan", exception.Message);
        using var conn = DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT published_scrape_id
            FROM scrape_publication_state
            WHERE id = TRUE;
            """;
        Assert.True(cmd.ExecuteScalar() is null or DBNull);
    }

    [Fact]
    public void PublishScrapeRun_BlocksNextPublicationUntilImprovementNotificationsComplete()
    {
        var publishedId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(publishedId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            publishedId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true,
            improvementNotificationProjectionScopes: []);

        var candidateId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateId, 1, 10, 1, 100);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Db.PublishScrapeRun(candidateId, promoteCachedResponses: false));

        Assert.Contains("improvement notifications", exception.Message);
        Assert.Equal(publishedId, Db.GetPublishedScrapeRun()?.Id);

        var notifications = new ImprovementNotificationService(
            DataSource,
            Substitute.For<ILogger<ImprovementNotificationService>>());
        notifications.MarkPublicationCompleted(publishedId);

        Db.PublishScrapeRun(candidateId, promoteCachedResponses: false);

        Assert.Equal(candidateId, Db.GetPublishedScrapeRun()?.Id);
    }

    [Fact]
    public void PublicationConstraint_RejectsPreContractMarkerOverwrite()
    {
        var publishedId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(publishedId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            publishedId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true,
            improvementNotificationProjectionScopes:
            [
                new SoloCurrentProjectionScopeKey("song-1", "Solo_Guitar"),
            ]);

        var candidateId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateId, 1, 10, 1, 100);

        using var conn = DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE scrape_publication_state
            SET published_scrape_id = @candidateId,
                improvement_notifications_scrape_id = @candidateId,
                improvement_notifications_status = 'pending',
                updated_at = now()
            WHERE id = TRUE;
            """;
        cmd.Parameters.AddWithValue("candidateId", (int)candidateId);

        var exception = Assert.Throws<PostgresException>(() => cmd.ExecuteNonQuery());

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(publishedId, Db.GetPublishedScrapeRun()?.Id);
    }

    [Fact]
    public void PublicationConstraint_RejectsNullProjectionOwnerForActiveMarker()
    {
        var publishedId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(publishedId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            publishedId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true,
            improvementNotificationProjectionScopes: []);

        using var conn = DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE scrape_publication_state
            SET improvement_notifications_projection_scrape_id = NULL
            WHERE id = TRUE;
            """;

        var exception = Assert.Throws<PostgresException>(() => cmd.ExecuteNonQuery());

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public void PublishScrapeRun_AllowsNextPublicationAfterNotificationsAreExplicitlyDisabled()
    {
        var publishedId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(publishedId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            publishedId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true,
            improvementNotificationProjectionScopes: []);

        var notifications = new ImprovementNotificationService(
            DataSource,
            Substitute.For<ILogger<ImprovementNotificationService>>());
        notifications.MarkPublicationDisabled(
            publishedId,
            "Disabled by test configuration.");

        var candidateId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateId, 1, 10, 1, 100);
        Db.PublishScrapeRun(candidateId, promoteCachedResponses: false);

        Assert.Equal(candidateId, Db.GetPublishedScrapeRun()?.Id);
    }

    [Fact]
    public void FailedCandidateRemainsVisibleAndCannotReplacePublishedScrape()
    {
        var publishedId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(publishedId, 1, 10, 1, 100);
        Db.PublishScrapeRun(publishedId, promoteCachedResponses: false);

        foreach (var phase in PostScrapePhasePolicy.All
                     .Where(static pair =>
                         pair.Value == PostScrapePhaseCriticality.PublicationCritical)
                     .Select(static pair => pair.Key))
        {
            var candidateId = Db.StartScrapeRun();
            var ledger = new PostScrapeExecutionLedger();
            ledger.Record(new PostScrapePhaseOutcome(
                phase,
                PostScrapePhaseCriticality.PublicationCritical,
                false,
                "injected"));

            Assert.Throws<InvalidOperationException>(() =>
                ScrapePublicationGuard.EnsureCanPublish(
                    candidateId,
                    ledger,
                    enforcePublicationCriticalPhases: true));
            Db.FailScrapeRun(candidateId, phase, "injected");

            var runtime = Db.GetServiceRuntimeState(WorkerStatusPublisher.ScraperWorkerKey);
            Assert.Equal(publishedId, runtime.PublishedScrape?.Id);
            Assert.Equal(candidateId, runtime.LatestScrape?.Id);
            Assert.Equal("failed", runtime.LatestScrape?.Status);
            Assert.Equal(phase, runtime.LatestScrape?.FailurePhase);
        }

    }

    [Fact]
    public void FailScrapeRun_marks_working_publication_generation_failed()
    {
        var scrapeId = Db.StartScrapeRun();
        var generation = Db.GetPublicationGenerationForScrape(scrapeId)!;
        Db.BulkSetCachedResponsesStaging(
            [(Key: "failed-cache", Json: new byte[] { 1 }, ETag: "\"failed\"")],
            generation.PublicationId);

        Db.FailScrapeRun(scrapeId, "post_process", "injected");

        var failed = Db.GetPublicationGeneration(generation.PublicationId);
        var pointer = Db.GetPublicationPointerState();
        Assert.Equal(PublicationGenerationStatus.Failed, failed!.Status);
        Assert.Equal("post_process", failed.FailurePhase);
        Assert.Equal("injected", failed.FailureMessage);
        Assert.Null(pointer.WorkingPublicationId);
        using var conn = DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                (
                    SELECT COUNT(*)
                    FROM publication_api_response_cache_staging
                    WHERE publication_id = @publicationId
                ),
                (
                    SELECT COUNT(*)
                    FROM publication_song_catalog
                    WHERE publication_id = @publicationId
                ),
                (
                    SELECT status
                    FROM publication_surface_bindings
                    WHERE publication_id = @publicationId
                      AND surface_name = 'song_catalog'
                )
            """;
        cmd.Parameters.AddWithValue(
            "publicationId",
            generation.PublicationId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal("failed", reader.GetString(2));
    }

    [Fact]
    public void FailScrapeRun_does_not_remove_published_song_catalog()
    {
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;

        Db.FailScrapeRun(scrapeId, "late_failure", "ignored");

        Assert.True(HasPublicationSongCatalog(publicationId));
        Assert.Equal(
            PublicationGenerationStatus.Current,
            Db.GetPublicationGeneration(publicationId)!.Status);
        Assert.Equal(
            "ready",
            Db.GetPublicationSurfaceBindings(publicationId)
                .Single(static binding =>
                    binding.SurfaceName == "song_catalog")
                .Status);
    }

    [Fact]
    public void BestEffortFailuresRemainVisibleAndAllowCorrectPublication()
    {
        var publishedId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(publishedId, 1, 10, 1, 100);
        Db.PublishScrapeRun(publishedId, promoteCachedResponses: false);

        foreach (var phase in PostScrapePhasePolicy.All
                     .Where(static pair =>
                         pair.Value == PostScrapePhaseCriticality.BestEffort)
                     .Select(static pair => pair.Key))
        {
            var candidateId = Db.StartScrapeRun();
            var now = DateTime.UtcNow;
            Db.RecordScrapePhaseOutcome(new ScrapePhaseOutcomeRecord(
                candidateId,
                phase,
                "best_effort",
                "failed",
                now,
                now.AddMilliseconds(1),
                1,
                "injected"));
            var ledger = new PostScrapeExecutionLedger();
            ledger.Record(new PostScrapePhaseOutcome(
                phase,
                PostScrapePhaseCriticality.BestEffort,
                false,
                "injected"));

            ScrapePublicationGuard.EnsureCanPublish(
                candidateId,
                ledger,
                enforcePublicationCriticalPhases: true);
            Db.CompleteScrapeRun(candidateId, 1, 10, 1, 100);
            Db.PublishScrapeRun(candidateId, promoteCachedResponses: false);

            var runtime = Db.GetServiceRuntimeState(WorkerStatusPublisher.ScraperWorkerKey);
            Assert.Equal(candidateId, runtime.PublishedScrape?.Id);
            Assert.Equal(1, runtime.PublishedScrape?.BestEffortFailureCount);
            Assert.Equal([phase], runtime.PublishedScrape?.BestEffortFailedPhases);
        }
    }

    [Fact]
    public void WriterFailuresPersistExactScopesAndFailedCandidateState()
    {
        var scrapeId = Db.StartScrapeRun();
        var occurredAt = DateTime.UtcNow;
        var failure = new WriterBatchFailure(
            "solo-online",
            "Solo_Guitar",
            [
                new WriterFailedScope("song_1", 2, 150),
                new WriterFailedScope("song_2", 1, 25),
            ],
            typeof(InvalidOperationException).FullName!,
            "injected",
            "/same-drive/replay",
            occurredAt);
        var drain = new WriterDrainResult(
            "solo-online",
            3,
            175,
            0,
            0,
            [failure],
            "/same-drive/replay");

        Db.RecordScrapeWriterFailures(scrapeId, [drain]);
        Db.FailScrapeRun(scrapeId, "writer", "175 rows retained");

        using var conn = DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT song_id, page_count, row_count, artifact_path
            FROM scrape_writer_failures
            WHERE scrape_id = @scrapeId
            ORDER BY song_id
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("song_1", reader.GetString(0));
        Assert.Equal(2, reader.GetInt32(1));
        Assert.Equal(150, reader.GetInt64(2));
        Assert.Equal("/same-drive/replay", reader.GetString(3));
        Assert.True(reader.Read());
        Assert.Equal("song_2", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(25, reader.GetInt64(2));
        Assert.False(reader.Read());

        var runtime = Db.GetServiceRuntimeState(WorkerStatusPublisher.ScraperWorkerKey);
        Assert.Equal("failed", runtime.LatestScrape?.Status);
        Assert.Equal("writer", runtime.LatestScrape?.FailurePhase);
        Assert.Throws<InvalidOperationException>(() =>
            Db.CompleteScrapeRun(scrapeId, 1, 175, 1, 100));
    }

    [Fact]
    public void CompletedButUnpublishedCandidateCanBeMarkedFailedAndCannotPublish()
    {
        var publishedId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(publishedId, 1, 10, 1, 100);
        Db.PublishScrapeRun(publishedId, promoteCachedResponses: false);

        var candidateId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateId, 1, 11, 1, 101);
        Db.FailScrapeRun(candidateId, "publication", "injected publication failure");

        var runtime = Db.GetServiceRuntimeState(WorkerStatusPublisher.ScraperWorkerKey);
        Assert.Equal(candidateId, runtime.LatestScrape?.Id);
        Assert.Equal("failed", runtime.LatestScrape?.Status);
        Assert.Equal(publishedId, runtime.PublishedScrape?.Id);
        Assert.Throws<InvalidOperationException>(() =>
            Db.PublishScrapeRun(candidateId, promoteCachedResponses: false));
    }

    [Fact]
    public void CompleteScrapeRun_persists_epic_page_count_visibility_signal()
    {
        var cappedId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(cappedId, 100, 50_000, 200, 1_000_000, epicReportedOver100Pages: false);
        Assert.False(Db.ShouldShowLeaderboardEntryTotals());

        var uncappedId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(uncappedId, 100, 50_000, 200, 1_000_000, epicReportedOver100Pages: true);

        var last = Db.GetLastCompletedScrapeRun();
        Assert.NotNull(last);
        Assert.Equal(uncappedId, last.Id);
        Assert.True(last.EpicReportedOver100Pages);
        Assert.True(Db.ShouldShowLeaderboardEntryTotals());
    }

    [Fact]
    public void Leaderboard_entry_total_visibility_remains_on_published_scrape()
    {
        var publishedId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(publishedId, 100, 50_000, 200, 1_000_000, epicReportedOver100Pages: false);
        Db.PublishScrapeRun(publishedId, promoteCachedResponses: false);

        var candidateId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateId, 100, 50_000, 200, 1_000_000, epicReportedOver100Pages: true);

        Assert.False(Db.ShouldShowLeaderboardEntryTotals());

        Db.PublishScrapeRun(candidateId, promoteCachedResponses: false);

        Assert.True(Db.ShouldShowLeaderboardEntryTotals());
    }

    [Fact]
    public void ReplaceComboLeaderboard_replaces_rows_via_bulk_staging()
    {
        const string comboId = "test-combo";
        Db.ReplaceComboLeaderboard(comboId,
        [
            (AccountId: "player-1", AdjustedRating: 0.10, WeightedRating: 0.20, FcRate: 0.80, TotalScore: 1_000L, MaxScorePercent: 0.95, SongsPlayed: 2, FullComboCount: 1),
            (AccountId: "player-2", AdjustedRating: 0.30, WeightedRating: 0.40, FcRate: 0.60, TotalScore: 900L, MaxScorePercent: 0.85, SongsPlayed: 2, FullComboCount: 0),
        ], totalAccounts: 2);

        var (initialEntries, initialTotal) = Db.GetComboLeaderboard(comboId);
        Assert.Equal(2, initialTotal);
        Assert.Equal(["player-1", "player-2"], initialEntries.Select(entry => entry.AccountId).ToArray());

        Db.ReplaceComboLeaderboard(comboId,
        [
            (AccountId: "player-3", AdjustedRating: 0.05, WeightedRating: 0.10, FcRate: 1.00, TotalScore: 1_500L, MaxScorePercent: 1.00, SongsPlayed: 3, FullComboCount: 3),
        ], totalAccounts: 1);

        var (replacementEntries, replacementTotal) = Db.GetComboLeaderboard(comboId);
        var replacement = Assert.Single(replacementEntries);
        Assert.Equal(1, replacementTotal);
        Assert.Equal("player-3", replacement.AccountId);
        Assert.Equal(1, replacement.Rank);
    }

    [Fact]
    public void GetLastCompletedScrapeRun_returns_null_when_empty()
    {
        var last = Db.GetLastCompletedScrapeRun();
        Assert.Null(last);
    }

    [Fact]
    public void WorkerStatus_roundtrips_heartbeat_and_activity()
    {
        var startedAt = DateTime.UtcNow.AddMinutes(-5);
        var heartbeatAt = DateTime.UtcNow.AddSeconds(-10);
        var operationStartedAt = DateTime.UtcNow.AddMinutes(-2);
        var operationUpdatedAt = DateTime.UtcNow.AddMinutes(-1);

        Db.UpsertWorkerHeartbeat(
            WorkerStatusPublisher.ScraperWorkerKey,
            "running",
            "scraper",
            "instance-1",
            startedAt,
            heartbeatAt,
            "ready");

        Db.UpdateWorkerActivity(
            WorkerStatusPublisher.ScraperWorkerKey,
            new WorkerOperationInfo
            {
                OperationKey = "rankings.instrument.Solo_Guitar",
                OperationLabel = "Computing Lead Rankings",
                Status = "running",
                Phase = "ComputingRankings",
                SubOperation = "per_instrument_rankings",
                Detail = "Solo_Guitar",
                StartedAtUtc = operationStartedAt,
                UpdatedAtUtc = operationUpdatedAt,
                ProgressPercent = 50,
            },
            updatedAtUtc: operationUpdatedAt);

        var status = Db.GetWorkerStatus(WorkerStatusPublisher.ScraperWorkerKey);

        Assert.NotNull(status);
        Assert.Equal("running", status.Status);
        Assert.Equal("scraper", status.Mode);
        Assert.Equal("instance-1", status.InstanceId);
        Assert.NotNull(status.StartedAtUtc);
        Assert.NotNull(status.LastHeartbeatAtUtc);
        Assert.Equal("ready", status.Message);
        Assert.NotNull(status.CurrentOperation);
        Assert.Equal("Computing Lead Rankings", status.CurrentOperation.OperationLabel);
        Assert.Equal("ComputingRankings", status.CurrentOperation.Phase);
        Assert.Equal(50, status.CurrentOperation.ProgressPercent);
    }

    [Fact]
    public void WorkerStatus_moves_completed_operation_to_last_operation()
    {
        var now = DateTime.UtcNow;
        var current = new WorkerOperationInfo
        {
            OperationKey = "rankings.band.Band_Trios",
            OperationLabel = "Computing Band Trios Rankings",
            Status = "running",
            Phase = "ComputingRankings",
            SubOperation = "band_rankings",
            StartedAtUtc = now.AddMinutes(-3),
            UpdatedAtUtc = now.AddMinutes(-1),
        };
        var completed = new WorkerOperationInfo
        {
            OperationKey = current.OperationKey,
            OperationLabel = current.OperationLabel,
            Status = "completed",
            Phase = current.Phase,
            SubOperation = current.SubOperation,
            StartedAtUtc = current.StartedAtUtc,
            UpdatedAtUtc = now,
            EndedAtUtc = now,
            ElapsedSeconds = 180,
        };

        Db.UpdateWorkerActivity(WorkerStatusPublisher.ScraperWorkerKey, current, updatedAtUtc: current.UpdatedAtUtc);
        Db.UpdateWorkerActivity(WorkerStatusPublisher.ScraperWorkerKey, null, completed, updatedAtUtc: now);

        var status = Db.GetWorkerStatus(WorkerStatusPublisher.ScraperWorkerKey);

        Assert.NotNull(status);
        Assert.Null(status.CurrentOperation);
        Assert.NotNull(status.LastOperation);
        Assert.Equal("Computing Band Trios Rankings", status.LastOperation.OperationLabel);
        Assert.Equal("completed", status.LastOperation.Status);
        Assert.NotNull(status.LastOperation.EndedAtUtc);
    }

    [Fact]
    public void WorkerHeartbeat_refreshes_current_operation_timestamp()
    {
        var startedAt = DateTime.UtcNow.AddMinutes(-5);
        var originalUpdate = startedAt.AddMinutes(1);
        var heartbeatAt = DateTime.UtcNow;
        var current = new WorkerOperationInfo
        {
            OperationKey = "scrape.post_process",
            OperationLabel = "Post-processing leaderboard update",
            Status = "running",
            Phase = "PostScrapeEnrichment",
            StartedAtUtc = startedAt,
            UpdatedAtUtc = originalUpdate,
        };

        Db.UpsertWorkerHeartbeat(
            WorkerStatusPublisher.ScraperWorkerKey,
            "running",
            "scraper",
            "instance-refresh",
            startedAt,
            heartbeatAt,
            currentOperation: new WorkerOperationInfo
            {
                OperationKey = current.OperationKey,
                OperationLabel = current.OperationLabel,
                Status = current.Status,
                Phase = current.Phase,
                StartedAtUtc = current.StartedAtUtc,
                UpdatedAtUtc = heartbeatAt,
                ElapsedSeconds = (heartbeatAt - startedAt).TotalSeconds,
            });

        var status = Db.GetWorkerStatus(WorkerStatusPublisher.ScraperWorkerKey);

        Assert.NotNull(status?.CurrentOperation);
        Assert.Equal(heartbeatAt, status.CurrentOperation.UpdatedAtUtc, TimeSpan.FromMilliseconds(10));
        Assert.True(status.CurrentOperation.ElapsedSeconds >= 299);
    }

    [Fact]
    public void ServiceRuntimeState_returns_published_active_freeze_and_worker_atomically()
    {
        var publishedId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(publishedId, 1, 10, 1, 100);
        Db.PublishScrapeRun(publishedId, promoteCachedResponses: false);
        var activeId = Db.StartScrapeRun();
        Db.SetPublicReadFreeze(true, reason: "scrape");

        var now = DateTime.UtcNow;
        Db.UpsertWorkerHeartbeat(
            WorkerStatusPublisher.ScraperWorkerKey,
            "running",
            "scraper",
            "runtime-state-instance",
            now.AddMinutes(-1),
            now,
            currentOperation: new WorkerOperationInfo
            {
                OperationKey = "scrape.leaderboards",
                OperationLabel = "Scraping leaderboard scores",
                Status = "running",
                Phase = "Scraping",
                SubOperation = "fetching_leaderboards",
                StartedAtUtc = now.AddMinutes(-1),
                UpdatedAtUtc = now,
            });

        var runtime = Db.GetServiceRuntimeState(WorkerStatusPublisher.ScraperWorkerKey);

        Assert.Equal(activeId, runtime.LatestScrape?.Id);
        Assert.Equal(publishedId, runtime.PublishedScrape?.Id);
        Assert.True(runtime.PublicReadFreeze.IsFrozen);
        Assert.Equal(publishedId, runtime.PublicReadFreeze.ScrapeId);
        Assert.Equal("scrape", runtime.PublicReadFreeze.Reason);
        Assert.Equal("scrape.leaderboards", runtime.WorkerStatus?.CurrentOperation?.OperationKey);
    }

    [Fact]
    public void PublishScrapeRun_requires_completed_scrape()
    {
        var id = Db.StartScrapeRun();

        Assert.Throws<InvalidOperationException>(() => Db.PublishScrapeRun(id));
        Assert.Null(Db.GetPublishedScrapeRun());
    }

    [Fact]
    public void PublishScrapeRun_promotes_completed_scrape_and_staged_cache_atomically()
    {
        var oldId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(oldId, 1, 10, 1, 100);
        Db.BulkSetCachedResponses([(Key: "player:acct_1:::", Json: new byte[] { 1 }, ETag: "\"old\"")]);
        Db.PublishScrapeRun(oldId, promoteCachedResponses: false);
        var oldPublicationId =
            Db.GetPublicationGenerationForScrape(oldId)!.PublicationId;

        var nextId = Db.StartScrapeRun();
        Db.BulkSetCachedResponsesStaging([(Key: "player:acct_1:::", Json: new byte[] { 2 }, ETag: "\"new\"")]);

        var cachedBeforePublish = Db.GetCachedResponse("player:acct_1:::");
        Assert.NotNull(cachedBeforePublish);
        Assert.Equal(new byte[] { 1 }, cachedBeforePublish.Value.Json);
        Assert.Equal(oldId, Db.GetPublishedScrapeRun()?.Id);

        Db.CompleteScrapeRun(nextId, 2, 20, 2, 200);
        Db.PublishScrapeRun(nextId);

        var published = Db.GetPublishedScrapeRun();
        Assert.NotNull(published);
        Assert.Equal(nextId, published.Id);

        var cachedAfterPublish = Db.GetCachedResponse("player:acct_1:::");
        Assert.NotNull(cachedAfterPublish);
        Assert.Equal(new byte[] { 2 }, cachedAfterPublish.Value.Json);
        Assert.Equal("\"new\"", cachedAfterPublish.Value.ETag);

        var nextPublicationId =
            Db.GetPublicationGenerationForScrape(nextId)!.PublicationId;
        Assert.Equal(
            new byte[] { 1 },
            Db.GetCachedResponse(
                oldPublicationId,
                "player:acct_1:::")?.Json);
        Assert.Equal(
            new byte[] { 2 },
            Db.GetCachedResponse(
                nextPublicationId,
                "player:acct_1:::")?.Json);
        var cacheBinding = Db.GetPublicationSurfaceBindings(nextPublicationId)
            .Single(binding =>
                binding.SurfaceName == "api_response_cache");
        Assert.Equal("generation_cache_table", cacheBinding.BindingKind);
        Assert.Equal(1, cacheBinding.RowCount);
        Assert.False(string.IsNullOrWhiteSpace(cacheBinding.ContentHash));
    }

    [Fact]
    public void PublishScrapeRun_rotates_publication_generations_and_records_surface_bindings()
    {
        var firstScrapeId = Db.StartScrapeRun();
        Db.BulkSetCachedResponses(
        [
            (Key: "player:acct_1:::", Json: new byte[] { 1 }, ETag: "\"old\""),
        ]);
        Db.CompleteScrapeRun(firstScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(firstScrapeId, promoteCachedResponses: false);

        var firstGeneration = Db.GetPublicationGenerationForScrape(firstScrapeId)!;
        var firstPointer = Db.GetPublicationPointerState();
        Assert.Equal(firstGeneration.PublicationId, firstPointer.CurrentPublicationId);
        Assert.Null(firstPointer.PreviousPublicationId);
        Assert.Null(firstPointer.WorkingPublicationId);
        Assert.Equal(PublicationGenerationStatus.Current, firstGeneration.Status);
        Assert.Equal(
            [
                "api_response_cache",
                "band_rankings",
                "improvement_notifications",
                "item_shop",
                "path_artifacts",
                "solo_scope_sources",
                "song_catalog",
            ],
            Db.GetPublicationSurfaceBindings(firstGeneration.PublicationId)
                .Select(static binding => binding.SurfaceName)
                .ToArray());
        var firstCatalogBinding =
            Db.GetPublicationSurfaceBindings(firstGeneration.PublicationId)
                .Single(static binding =>
                    binding.SurfaceName == "song_catalog");
        Assert.Equal(
            "generation_catalog_snapshot",
            firstCatalogBinding.BindingKind);
        Assert.Equal("ready", firstCatalogBinding.Status);
        Assert.False(string.IsNullOrWhiteSpace(
            firstCatalogBinding.ContentHash));
        Assert.True(HasPublicationSongCatalog(
            firstGeneration.PublicationId));

        var secondScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(secondScrapeId, 1, 20, 2, 200);
        Db.PublishScrapeRun(secondScrapeId, promoteCachedResponses: false);

        var secondGeneration = Db.GetPublicationGenerationForScrape(secondScrapeId)!;
        var secondPointer = Db.GetPublicationPointerState();
        Assert.Equal(secondGeneration.PublicationId, secondPointer.CurrentPublicationId);
        Assert.Equal(firstGeneration.PublicationId, secondPointer.PreviousPublicationId);
        Assert.Null(secondPointer.WorkingPublicationId);
        Assert.Equal(
            PublicationGenerationStatus.Retained,
            Db.GetPublicationGeneration(firstGeneration.PublicationId)!.Status);
        Assert.Equal(PublicationGenerationStatus.Current, secondGeneration.Status);
        Assert.Equal(
            firstGeneration.PublicationId,
            secondGeneration.PreviousPublicationId);
        Assert.Equal(
            Db.GetCachedResponse(
                firstGeneration.PublicationId,
                "player:acct_1:::")?.Json,
            Db.GetCachedResponse(
                secondGeneration.PublicationId,
                "player:acct_1:::")?.Json);
        Assert.Equal(
            new byte[] { 1 },
            Db.GetCachedResponse(
                secondGeneration.PublicationId,
                "player:acct_1:::")?.Json);
        Assert.True(RelationExists(
            BandRankingStorageNames.GetRetainedPublishedRankingTable(
                firstGeneration.PublicationId,
                "Band_Duets")));

        var thirdScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(thirdScrapeId, 1, 30, 3, 300);
        Db.PublishScrapeRun(
            thirdScrapeId,
            promoteCachedResponses: false);
        var thirdGeneration =
            Db.GetPublicationGenerationForScrape(thirdScrapeId)!;

        Assert.Null(Db.GetCachedResponse(
            firstGeneration.PublicationId,
            "player:acct_1:::"));
        Assert.NotNull(Db.GetCachedResponse(
            secondGeneration.PublicationId,
            "player:acct_1:::"));
        Assert.NotNull(Db.GetCachedResponse(
            thirdGeneration.PublicationId,
            "player:acct_1:::"));
        Assert.False(RelationExists(
            BandRankingStorageNames.GetRetainedPublishedRankingTable(
                firstGeneration.PublicationId,
                "Band_Duets")));
        Assert.True(RelationExists(
            BandRankingStorageNames.GetRetainedPublishedRankingTable(
                secondGeneration.PublicationId,
                "Band_Duets")));
        Assert.False(HasPublicationSongCatalog(
            firstGeneration.PublicationId));
        Assert.True(HasPublicationSongCatalog(
            secondGeneration.PublicationId));
        Assert.True(HasPublicationSongCatalog(
            thirdGeneration.PublicationId));
        Assert.Equal(
            "retired",
            Db.GetPublicationSurfaceBindings(firstGeneration.PublicationId)
                .Single(binding =>
                    binding.SurfaceName == "api_response_cache")
                .Status);
        Assert.Equal(
            "retired",
            Db.GetPublicationSurfaceBindings(firstGeneration.PublicationId)
                .Single(binding =>
                    binding.SurfaceName == "song_catalog")
                .Status);
    }

    [Fact]
    public void PublishScrapeRun_rejects_generation_that_does_not_own_working_pointer()
    {
        var firstScrapeId = Db.StartScrapeRun();
        var firstPublicationId =
            Db.GetPublicationGenerationForScrape(firstScrapeId)!.PublicationId;
        var secondScrapeId = Db.StartScrapeRun();
        var secondPublicationId =
            Db.GetPublicationGenerationForScrape(secondScrapeId)!.PublicationId;
        Db.CompleteScrapeRun(firstScrapeId, 1, 10, 1, 100);

        var exception = Assert.Throws<InvalidOperationException>(
            () => Db.PublishScrapeRun(
                firstScrapeId,
                promoteCachedResponses: false));

        Assert.Contains("does not own the working pointer", exception.Message);
        Assert.Equal(
            secondPublicationId,
            Db.GetPublicationPointerState().WorkingPublicationId);
        Assert.False(HasPublicationSongCatalog(firstPublicationId));
        Assert.True(HasPublicationSongCatalog(secondPublicationId));
        Assert.Equal(
            "retired",
            Db.GetPublicationSurfaceBindings(firstPublicationId)
                .Single(static binding =>
                    binding.SurfaceName == "song_catalog")
                .Status);
    }

    [Fact]
    public void PublishScrapeRun_current_generation_retry_is_idempotent()
    {
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);
        var before = Db.GetPublicationPointerState();

        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);

        var after = Db.GetPublicationPointerState();
        Assert.Equal(before, after);
        Assert.Null(
            Db.GetPublicationGeneration(after.CurrentPublicationId!.Value)!
                .PreviousPublicationId);
    }

    [Fact]
    public void Current_cache_target_is_rejected_after_new_working_generation_starts()
    {
        var publishedScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(publishedScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var currentPublicationId =
            Db.GetPublicationGenerationForScrape(publishedScrapeId)!
                .PublicationId;

        var candidateScrapeId = Db.StartScrapeRun();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Db.BulkSetCachedResponsesStaging(
                [(Key: "stale", Json: new byte[] { 1 }, ETag: "\"stale\"")],
                currentPublicationId));

        Assert.Contains(
            "cannot mutate the cache while working publication",
            exception.Message);
    }

    [Fact]
    public void Current_cache_build_is_blocked_by_failed_candidate_isolation()
    {
        var publishedScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(publishedScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var currentPublicationId =
            Db.GetPublicationGenerationForScrape(publishedScrapeId)!
                .PublicationId;
        var failedScrapeId = Db.StartScrapeRun();
        Db.FailScrapeRun(
            failedScrapeId,
            MetaDatabase.PostProcessReadIsolationFailurePhase,
            "injected");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Db.AcquirePublicationCacheBuildLease(
                currentPublicationId,
                requireCurrentPublication: true));

        Assert.Contains("failed-candidate read isolation", exception.Message);

        var swapException = Assert.Throws<InvalidOperationException>(() =>
            Db.SwapCachedResponsesFromStaging(currentPublicationId));
        Assert.Contains(
            "failed-candidate read isolation",
            swapException.Message);
    }

    [Fact]
    public async Task Current_cache_build_lease_blocks_new_scrape_allocation()
    {
        var publishedScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(publishedScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var currentPublicationId =
            Db.GetPublicationGenerationForScrape(publishedScrapeId)!
                .PublicationId;

        using var lease = Db.AcquirePublicationCacheBuildLease(
            currentPublicationId,
            requireCurrentPublication: true);
        var startTask = Task.Run(Db.StartScrapeRun);
        await Task.Delay(100);
        Assert.False(startTask.IsCompleted);
        Db.BulkSetCachedResponsesStaging(
            [(Key: "lease-key", Json: new byte[] { 9 }, ETag: "\"lease\"")],
            currentPublicationId);

        lease.Dispose();
        var newScrapeId = await startTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(newScrapeId > publishedScrapeId);
    }

    [Fact]
    public async Task Publication_cache_build_lease_is_exclusive_per_generation()
    {
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;

        using var first = Db.AcquirePublicationCacheBuildLease(
            publicationId,
            requireCurrentPublication: true);
        var secondTask = Task.Run(() =>
            Db.AcquirePublicationCacheBuildLease(
                publicationId,
                requireCurrentPublication: true));
        await Task.Delay(100);
        Assert.False(secondTask.IsCompleted);

        first.Dispose();
        using var second =
            await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Rejected_cache_build_lease_releases_advisory_locks()
    {
        var publishedScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(publishedScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var currentPublicationId =
            Db.GetPublicationGenerationForScrape(publishedScrapeId)!
                .PublicationId;
        var candidateScrapeId = Db.StartScrapeRun();

        Assert.Throws<InvalidOperationException>(() =>
            Db.AcquirePublicationCacheBuildLease(
                currentPublicationId,
                requireCurrentPublication: true));

        Db.FailScrapeRun(candidateScrapeId, "test", "cleanup");
        var startTask = Task.Run(Db.StartScrapeRun);
        var nextScrapeId =
            await startTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(nextScrapeId > candidateScrapeId);
    }

    [Fact]
    public async Task SchemaUpgrade_reconciles_legacy_cache_after_rollback_writer()
    {
        var scrapeId = Db.StartScrapeRun();
        Db.BulkSetCachedResponsesStaging(
            [(Key: "old-key", Json: new byte[] { 1 }, ETag: "\"old\"")]);
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId);
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;

        using (var conn = DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                TRUNCATE api_response_cache;
                INSERT INTO api_response_cache (
                    cache_key, json_data, etag, cached_at)
                VALUES (
                    'new-key', '\x02'::bytea, '"new"', now() + interval '1 minute');
                """;
            cmd.ExecuteNonQuery();
        }

        await DatabaseInitializer.EnsureSchemaAsync(DataSource);

        Assert.Null(Db.GetCachedResponse(publicationId, "old-key"));
        Assert.Equal(
            new byte[] { 2 },
            Db.GetCachedResponse(publicationId, "new-key")?.Json);
        Assert.Equal(
            "legacy_current_table_reconciled",
            Db.GetPublicationSurfaceBindings(publicationId)
                .Single(binding =>
                    binding.SurfaceName == "api_response_cache")
                .BindingKind);
    }

    [Fact]
    public async Task SchemaUpgrade_PreservesAuthoritativeGenerationBeforeCompatibilityCleanup()
    {
        var oldScrapeId = Db.StartScrapeRun();
        Db.BulkSetCachedResponses(
        [
                (Key: "cutover-key", Json: new byte[] { 1 }, ETag: "\"old\""),
        ]);
        Db.CompleteScrapeRun(oldScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
                oldScrapeId,
                promoteCachedResponses: false);

        var candidateScrapeId = Db.StartScrapeRun();
        Db.BulkSetCachedResponsesStaging(
        [
                (Key: "cutover-key", Json: new byte[] { 2 }, ETag: "\"new\""),
        ]);
        Db.CompleteScrapeRun(candidateScrapeId, 1, 20, 2, 200);
        var preparation =
                Db.PrepareScrapePublication(candidateScrapeId);
        Db.SetPublicReadFreeze(
                true,
                candidateScrapeId,
                PublicReadFreezeState.PublicationCommitIntentReason);
        var commit =
                Db.CommitPreparedScrapePublication(preparation);

        using (var conn = DataSource.OpenConnection())
        using (var legacy = conn.CreateCommand())
        {
                legacy.CommandText = """
                    SELECT json_data
                    FROM api_response_cache
                    WHERE cache_key = 'cutover-key'
                    """;
                Assert.Equal(
                    new byte[] { 1 },
                    (byte[]?)legacy.ExecuteScalar());
        }
        Assert.Equal(
                new byte[] { 2 },
                Db.GetCachedResponse("cutover-key")?.Json);

        await DatabaseInitializer.EnsureSchemaAsync(DataSource);

        Assert.Equal(
                new byte[] { 2 },
                Db.GetCachedResponse("cutover-key")?.Json);
        Db.CleanupPublishedScrapePublication(
                preparation,
                commit);
    }

    [Fact]
    public async Task SchemaUpgrade_bootstraps_current_song_catalog_safely()
    {
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(scrapeId, promoteCachedResponses: false);
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;

        using (var conn = DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM publication_song_catalog
                WHERE publication_id = @publicationId;
                DELETE FROM live_song_catalog
                WHERE id = TRUE;

                UPDATE publication_surface_bindings
                SET binding_kind = 'legacy_live_unversioned',
                    binding_json = jsonb_build_object('table', 'songs'),
                    row_count = NULL,
                    content_hash = NULL,
                    status = 'building',
                    built_at = now()
                WHERE publication_id = @publicationId
                  AND surface_name = 'song_catalog';

                INSERT INTO songs (
                    song_id, title, artist, active_date, last_modified,
                    lead_diff, bass_diff, vocals_diff, drums_diff,
                    pro_lead_diff, pro_bass_diff, release_year, tempo,
                    plastic_guitar_diff, plastic_bass_diff,
                    plastic_drums_diff, pro_vocals_diff)
                VALUES (
                    'bootstrap-song', 'Bootstrap Song', 'Bootstrap Artist',
                    '2026-07-30T00:00:00.0000000Z',
                    '2026-07-31T00:00:00.0000000Z',
                    1, 2, 3, 4, 5, 6, 2026, 120, 5, 6, 7, 8)
                ON CONFLICT (song_id) DO UPDATE SET
                    title = EXCLUDED.title,
                    artist = EXCLUDED.artist
                """;
            cmd.Parameters.AddWithValue(
                "publicationId",
                publicationId);
            cmd.ExecuteNonQuery();
        }

        await DatabaseInitializer.EnsureSchemaAsync(DataSource);
        var first = ReadPublicationSongCatalog(publicationId);
        await DatabaseInitializer.EnsureSchemaAsync(DataSource);
        var second = ReadPublicationSongCatalog(publicationId);

        Assert.Equal(first, second);
        Assert.Equal(1, first.SongCount);
        Assert.Equal(64, first.ContentHash.Length);
        Assert.False(first.IsExact);
        Assert.Equal(
            "legacy_publication_reconstructed",
            first.SourceKind);
        Assert.Equal(1, first.SchemaVersion);
        var liveLegacy = ReadLiveSongCatalog();
        Assert.False(liveLegacy.IsExact);
        Assert.Equal(
            "legacy_columns_reconstructed",
            liveLegacy.SourceKind);
        Assert.Equal(1, liveLegacy.SchemaVersion);
        var binding = Db.GetPublicationSurfaceBindings(publicationId)
            .Single(static item => item.SurfaceName == "song_catalog");
        Assert.Equal("legacy_reconstructed_catalog", binding.BindingKind);
        Assert.Equal("building", binding.Status);
        Assert.Equal(first.ContentHash, binding.ContentHash);
        Assert.Equal(first.SongCount, binding.RowCount);
        Assert.Throws<InvalidOperationException>(() => Db.StartScrapeRun());

        var persistence = new FestivalPersistence(DataSource);
        var exactToken = await persistence.SaveSongsVersionedAsync(
        [
            CreateCatalogSong("bootstrap-song", "Bootstrap Song"),
        ]);
        await DatabaseInitializer.EnsureSchemaAsync(DataSource);

        var unchangedCurrentBinding =
            Db.GetPublicationSurfaceBindings(publicationId)
                .Single(static item =>
                    item.SurfaceName == "song_catalog");
        Assert.Equal(
            "legacy_reconstructed_catalog",
            unchangedCurrentBinding.BindingKind);
        Assert.Equal("building", unchangedCurrentBinding.Status);
        Assert.False(
            ReadPublicationSongCatalog(publicationId).IsExact);

        var nextScrapeId = Db.StartScrapeRun(exactToken);
        var nextPublicationId =
            Db.GetPublicationGenerationForScrape(nextScrapeId)!.PublicationId;
        var exactSnapshot =
            ReadPublicationSongCatalog(nextPublicationId);
        Assert.True(exactSnapshot.IsExact);
        Assert.Equal("provider_exact", exactSnapshot.SourceKind);
        Assert.Equal(
            SongCatalogSnapshotBuilder.SchemaVersion,
            exactSnapshot.SchemaVersion);
        Assert.Equal(exactToken.CatalogVersion, exactSnapshot.CatalogVersion);
        Assert.Equal(
            "ready",
            Db.GetPublicationSurfaceBindings(nextPublicationId)
                .Single(static item =>
                    item.SurfaceName == "song_catalog")
                .Status);
    }

    [Fact]
    public async Task SchemaUpgrade_downgrades_unproven_working_catalog()
    {
        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        using (var conn = DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE publication_song_catalog
                SET schema_version = 1,
                    source_kind = 'legacy_publication_reconstructed',
                    is_exact = FALSE
                WHERE publication_id = @publicationId;

                UPDATE publication_surface_bindings
                SET binding_kind = 'generation_catalog_snapshot',
                    status = 'ready'
                WHERE publication_id = @publicationId
                  AND surface_name = 'song_catalog';
                """;
            cmd.Parameters.AddWithValue(
                "publicationId",
                publicationId);
            cmd.ExecuteNonQuery();
        }

        await DatabaseInitializer.EnsureSchemaAsync(DataSource);

        var snapshot = ReadPublicationSongCatalog(publicationId);
        var binding = Db.GetPublicationSurfaceBindings(publicationId)
            .Single(static item => item.SurfaceName == "song_catalog");
        Assert.False(snapshot.IsExact);
        Assert.Equal(
            "legacy_publication_reconstructed",
            snapshot.SourceKind);
        Assert.Equal("legacy_reconstructed_catalog", binding.BindingKind);
        Assert.Equal("building", binding.Status);

        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        var exception = Assert.Throws<InvalidOperationException>(
            () => Db.PublishScrapeRun(
                scrapeId,
                promoteCachedResponses: false));
        Assert.Contains(
            "no complete song catalog snapshot",
            exception.Message);
    }

    [Fact]
    public async Task SchemaUpgrade_from_original_catalog_schema_marks_rows_inexact()
    {
        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        using (var conn = DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                ALTER TABLE live_song_catalog
                    DROP CONSTRAINT ck_live_song_catalog_source_kind;
                ALTER TABLE publication_song_catalog
                    DROP CONSTRAINT ck_publication_song_catalog_source_kind;

                ALTER TABLE live_song_catalog
                    DROP COLUMN catalog_version,
                    DROP COLUMN schema_version,
                    DROP COLUMN source_kind,
                    DROP COLUMN is_exact;
                ALTER TABLE publication_song_catalog
                    DROP COLUMN catalog_version,
                    DROP COLUMN schema_version,
                    DROP COLUMN source_kind,
                    DROP COLUMN is_exact;
                """;
            cmd.ExecuteNonQuery();
        }

        await DatabaseInitializer.EnsureSchemaAsync(DataSource);

        var live = ReadLiveSongCatalog();
        var publication =
            ReadPublicationSongCatalog(publicationId);
        var binding = Db.GetPublicationSurfaceBindings(publicationId)
            .Single(static item => item.SurfaceName == "song_catalog");
        Assert.False(live.IsExact);
        Assert.Equal("legacy_columns_reconstructed", live.SourceKind);
        Assert.Equal(1, live.SchemaVersion);
        Assert.False(publication.IsExact);
        Assert.Equal(
            "legacy_publication_reconstructed",
            publication.SourceKind);
        Assert.Equal(1, publication.SchemaVersion);
        Assert.Equal("legacy_reconstructed_catalog", binding.BindingKind);
        Assert.Equal("building", binding.Status);
        Assert.Throws<InvalidOperationException>(() => Db.StartScrapeRun());
    }

    [Fact]
    public async Task SchemaUpgrade_preserves_active_working_cache_staging()
    {
        var scrapeId = Db.StartScrapeRun();
        var publicationId =
            Db.GetPublicationGenerationForScrape(scrapeId)!.PublicationId;
        Db.BulkSetCachedResponsesStaging(
            [(Key: "working-key", Json: new byte[] { 4 }, ETag: "\"working\"")],
            publicationId);

        await DatabaseInitializer.EnsureSchemaAsync(DataSource);

        using var conn = DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM publication_api_response_cache_staging
            WHERE publication_id = @publicationId
            """;
        cmd.Parameters.AddWithValue("publicationId", publicationId);
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Retaining_newer_publication_does_not_block_old_scrape_deletion()
    {
        var firstScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(firstScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(firstScrapeId, promoteCachedResponses: false);
        var firstPublicationId =
            Db.GetPublicationGenerationForScrape(firstScrapeId)!.PublicationId;

        var secondScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(secondScrapeId, 1, 20, 2, 200);
        Db.PublishScrapeRun(secondScrapeId, promoteCachedResponses: false);
        var secondPublicationId =
            Db.GetPublicationGenerationForScrape(secondScrapeId)!.PublicationId;

        using (var conn = DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM scrape_log WHERE id = @scrapeId";
            cmd.Parameters.AddWithValue("scrapeId", firstScrapeId);
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }

        Assert.Null(Db.GetPublicationGeneration(firstPublicationId));
        Assert.Null(
            Db.GetPublicationGeneration(secondPublicationId)!
                .PreviousPublicationId);
        Assert.Null(Db.GetPublicationPointerState().PreviousPublicationId);
    }

    [Fact]
    public void PublishScrapeRun_rejects_empty_cache_staging_and_preserves_published_state()
    {
        var oldId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(oldId, 1, 10, 1, 100);
        Db.BulkSetCachedResponses(
            [(Key: "player:acct_1:::", Json: new byte[] { 1 }, ETag: "\"old\"")]);
        Db.PublishScrapeRun(oldId, promoteCachedResponses: false);

        var nextId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(nextId, 2, 20, 2, 200);

        var exception = Assert.Throws<InvalidOperationException>(
            () => Db.PublishScrapeRun(nextId));

        Assert.Contains("cache staging table is empty", exception.Message);
        Assert.Equal(oldId, Db.GetPublishedScrapeRun()?.Id);
        Assert.Equal(
            new byte[] { 1 },
            Db.GetCachedResponse("player:acct_1:::")?.Json);
    }

    [Fact]
    public async Task PublishScrapeRun_KeepsPublicCacheReadableWhileBandSnapshotsAreBuilt()
    {
        var oldId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(oldId, 1, 10, 1, 100);
        Db.BulkSetCachedResponses([(Key: "player:acct_1:::", Json: new byte[] { 1 }, ETag: "\"old\"")]);
        Db.PublishScrapeRun(oldId, promoteCachedResponses: false);

        var nextId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(nextId, 2, 20, 2, 200);
        Db.BulkSetCachedResponsesStaging([(Key: "player:acct_1:::", Json: new byte[] { 2 }, ETag: "\"new\"")]);

        var bandRankingTable =
            BandRankingStorageNames.GetCurrentRankingTable("Band_Duets");
        using var blocker = DataSource.OpenConnection();
        using var blockerTx = await blocker.BeginTransactionAsync();
        using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = blockerTx;
            lockCommand.CommandText =
                $"LOCK TABLE {BandRankingStorageNames.QuoteIdentifier(bandRankingTable)} " +
                "IN ACCESS EXCLUSIVE MODE";
            await lockCommand.ExecuteNonQueryAsync();
        }

        var publishTask = Task.Run(() => Db.PublishScrapeRun(nextId));
        await WaitForBlockedRelationLockAsync(bandRankingTable);

        try
        {
            using var readerConnection = DataSource.OpenConnection();
            using (var timeout = readerConnection.CreateCommand())
            {
                timeout.CommandText = "SET statement_timeout = '1s'";
                await timeout.ExecuteNonQueryAsync();
            }

            using var readCache = readerConnection.CreateCommand();
            readCache.CommandText = """
                SELECT json_data
                FROM api_response_cache
                WHERE cache_key = @cacheKey
                """;
            readCache.Parameters.AddWithValue("cacheKey", "player:acct_1:::");
            Assert.Equal(new byte[] { 1 }, (byte[]?)await readCache.ExecuteScalarAsync());
        }
        finally
        {
            await blockerTx.RollbackAsync();
            await publishTask.WaitAsync(TimeSpan.FromSeconds(30));
        }

        var cachedAfterPublish = Db.GetCachedResponse("player:acct_1:::");
        Assert.NotNull(cachedAfterPublish);
        Assert.Equal(new byte[] { 2 }, cachedAfterPublish.Value.Json);
    }

    [Fact]
    public async Task PrepareScrapePublication_KeepsOldPublicationReadableWithoutExclusiveLock()
    {
        var oldScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(oldScrapeId, 1, 10, 1, 100);
        Db.BulkSetCachedResponses(
        [
            (Key: "publication-split", Json: new byte[] { 1 }, ETag: "\"old\""),
        ]);
        Db.PublishScrapeRun(
            oldScrapeId,
            promoteCachedResponses: false);
        var oldPublicationId =
            Db.GetPublicationPointerState().CurrentPublicationId;

        var candidateScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateScrapeId, 2, 20, 2, 200);
        Db.BulkSetCachedResponsesStaging(
        [
            (Key: "publication-split", Json: new byte[] { 2 }, ETag: "\"new\""),
        ]);

        var bandRankingTable =
            BandRankingStorageNames.GetCurrentRankingTable("Band_Duets");
        using var blocker = DataSource.OpenConnection();
        using var blockerTx = await blocker.BeginTransactionAsync();
        using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = blockerTx;
            lockCommand.CommandText =
                $"LOCK TABLE {BandRankingStorageNames.QuoteIdentifier(bandRankingTable)} " +
                "IN ACCESS EXCLUSIVE MODE";
            await lockCommand.ExecuteNonQueryAsync();
        }

        var prepareTask = Task.Run(() =>
            Db.PrepareScrapePublication(candidateScrapeId));
        await WaitForBlockedRelationLockAsync(bandRankingTable);

        try
        {
            using var readConnection = DataSource.OpenConnection();
            using var readTx = readConnection.BeginTransaction();
            using var sharedLock = readConnection.CreateCommand();
            sharedLock.Transaction = readTx;
            sharedLock.CommandText =
                "SELECT pg_try_advisory_xact_lock_shared(@lockKey)";
            sharedLock.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            Assert.True(sharedLock.ExecuteScalar() is true);
            Assert.Equal(
                oldPublicationId,
                Db.GetPublicationPointerState().CurrentPublicationId);
            Assert.Equal(
                new byte[] { 1 },
                Db.GetCachedResponse("publication-split")?.Json);
            readTx.Commit();
        }
        finally
        {
            await blockerTx.RollbackAsync();
        }

        var preparation =
            await prepareTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(
            PublicationGenerationStatus.Ready,
            Db.GetPublicationGeneration(preparation.PublicationId)?.Status);
        Assert.Equal(
            oldPublicationId,
            Db.GetPublicationPointerState().CurrentPublicationId);
        Assert.True(RelationExists(
            BandRankingStorageNames.GetPreparedPublishedRankingTable(
                preparation.PublicationId,
                "Band_Duets")));

        Db.SetPublicReadFreeze(
            true,
            candidateScrapeId,
            PublicReadFreezeState.PublicationCommitIntentReason);
        var commit = Db.CommitPreparedScrapePublication(preparation);
        Db.CleanupPublishedScrapePublication(preparation, commit);
        Assert.Equal(
            preparation.PublicationId,
            Db.GetPublicationPointerState().CurrentPublicationId);
        Assert.Equal(
            new byte[] { 2 },
            Db.GetCachedResponse("publication-split")?.Json);
    }

    [Fact]
    public void PrepareScrapePublication_UsesBoundedServerLockTimeout()
    {
        var publishedScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(
            publishedScrapeId,
            1,
            10,
            1,
            100);
        Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var candidateScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(
            candidateScrapeId,
            1,
            20,
            2,
            200);
        var currentBandTable =
            BandRankingStorageNames.GetCurrentRankingTable(
                "Band_Duets");
        using var blocker = DataSource.OpenConnection();
        using var blockerTx = blocker.BeginTransaction();
        using (var lockTable = blocker.CreateCommand())
        {
            lockTable.Transaction = blockerTx;
            lockTable.CommandText =
                $"LOCK TABLE {BandRankingStorageNames.QuoteIdentifier(currentBandTable)} " +
                "IN ACCESS EXCLUSIVE MODE";
            lockTable.ExecuteNonQuery();
        }
        using var boundedDb = new MetaDatabase(
            DataSource,
            Substitute.For<ILogger<MetaDatabase>>(),
            publicationCommitOptions:
                new PublicationCommitOptions
                {
                    PreparationLockTimeoutMilliseconds = 50,
                    PreparationStatementTimeoutMilliseconds = 1_000,
                    PreparationTransactionTimeoutMilliseconds = 1_000,
                });

        var exception = Assert.Throws<PostgresException>(() =>
            boundedDb.PrepareScrapePublication(
                candidateScrapeId,
                promoteCachedResponses: false));

        Assert.Equal(
            PostgresErrorCodes.LockNotAvailable,
            exception.SqlState);
        Assert.Equal(
            PublicationGenerationStatus.Building,
            Db.GetPublicationGenerationForScrape(
                candidateScrapeId)?.Status);
        blockerTx.Commit();
        Db.FailScrapeRun(
            candidateScrapeId,
            "test",
            "cleanup");
    }

    [Fact]
    public async Task CommitPreparedScrapePublication_DoesNotQueueExclusiveWaiter()
    {
        var oldScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(oldScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            oldScrapeId,
            promoteCachedResponses: false);
        var oldPublicationId =
            Db.GetPublicationPointerState().CurrentPublicationId;

        var candidateScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateScrapeId, 1, 20, 2, 200);
        var preparation = Db.PrepareScrapePublication(
            candidateScrapeId,
            promoteCachedResponses: false);
        Db.SetPublicReadFreeze(
            true,
            candidateScrapeId,
            PublicReadFreezeState.PublicationCommitIntentReason);

        using var readConnection = DataSource.OpenConnection();
        using var readTx = readConnection.BeginTransaction();
        using (var sharedLock = readConnection.CreateCommand())
        {
            sharedLock.Transaction = readTx;
            sharedLock.CommandText =
                "SELECT pg_advisory_xact_lock_shared(@lockKey)";
            sharedLock.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            sharedLock.ExecuteNonQuery();
        }

        using var boundedDb = new MetaDatabase(
            DataSource,
            Substitute.For<ILogger<MetaDatabase>>(),
            publicationCommitOptions: new PublicationCommitOptions
            {
                DrainTimeoutMilliseconds = 250,
                RetryDelayMilliseconds = 20,
                RelationLockTimeoutMilliseconds = 50,
                StatementTimeoutMilliseconds = 500,
                MaxExclusiveLockDurationMilliseconds = 500,
            });
        var commitTask = Task.Run(() =>
            boundedDb.CommitPreparedScrapePublication(preparation));
        await Task.Delay(100);
        Assert.Equal(0L, CountUngrantedPublicationAdvisoryLocks());
        var exception = await Assert.ThrowsAsync<PublicationCommitBusyException>(
            async () => await commitTask);
        Assert.True(exception.LockRejections > 0);
        Assert.Equal(
            oldPublicationId,
            Db.GetPublicationPointerState().CurrentPublicationId);
        Assert.Equal(
            PublicationGenerationStatus.Ready,
            Db.GetPublicationGeneration(preparation.PublicationId)?.Status);

        readTx.Commit();
        var commit = Db.CommitPreparedScrapePublication(preparation);
        Assert.True(
            commit.ExclusiveLockDuration
            <= TimeSpan.FromMilliseconds(5_000));
        Db.CleanupPublishedScrapePublication(preparation, commit);
    }

    [Fact]
    public async Task CommitPreparedScrapePublication_RollsBackBoundedRelationLockFailure()
    {
        var oldScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(oldScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            oldScrapeId,
            promoteCachedResponses: false);
        var oldPublicationId =
            Db.GetPublicationPointerState().CurrentPublicationId;

        var candidateScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateScrapeId, 1, 20, 2, 200);
        var preparation = Db.PrepareScrapePublication(
            candidateScrapeId,
            promoteCachedResponses: false);
        Db.SetPublicReadFreeze(
            true,
            candidateScrapeId,
            PublicReadFreezeState.PublicationCommitIntentReason);

        var publishedBandTable =
            BandRankingStorageNames.GetPublishedRankingTable(
                "Band_Duets");
        using var blocker = DataSource.OpenConnection();
        using var blockerTx = blocker.BeginTransaction();
        using (var relationLock = blocker.CreateCommand())
        {
            relationLock.Transaction = blockerTx;
            relationLock.CommandText =
                $"LOCK TABLE {BandRankingStorageNames.QuoteIdentifier(publishedBandTable)} " +
                "IN ACCESS SHARE MODE";
            relationLock.ExecuteNonQuery();
        }

        using var boundedDb = new MetaDatabase(
            DataSource,
            Substitute.For<ILogger<MetaDatabase>>(),
            publicationCommitOptions: new PublicationCommitOptions
            {
                DrainTimeoutMilliseconds = 1_000,
                RetryDelayMilliseconds = 10,
                RelationLockTimeoutMilliseconds = 70,
                StatementTimeoutMilliseconds = 1_000,
                MaxExclusiveLockDurationMilliseconds = 150,
            });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var exception =
            Assert.Throws<PublicationCommitDeadlineExceededException>(
            () => boundedDb.CommitPreparedScrapePublication(
                preparation));
        stopwatch.Stop();
        Assert.True(exception.RelationLockRetries > 0);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
        Assert.Equal(
            oldPublicationId,
            Db.GetPublicationPointerState().CurrentPublicationId);
        Assert.True(RelationExists(publishedBandTable));
        Assert.True(RelationExists(
            BandRankingStorageNames.GetPreparedPublishedRankingTable(
                preparation.PublicationId,
                "Band_Duets")));

        blockerTx.Commit();
        var commit = Db.CommitPreparedScrapePublication(preparation);
        Db.CleanupPublishedScrapePublication(preparation, commit);
    }

    [Fact]
    public void PublishScrapeRun_DrainTimeoutRestoresPreviousFreeze()
    {
        var oldScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(oldScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            oldScrapeId,
            promoteCachedResponses: false);

        var candidateScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateScrapeId, 1, 20, 2, 200);
        Db.SetPublicReadFreeze(
            true,
            candidateScrapeId,
            "publish");

        using var readConnection = DataSource.OpenConnection();
        using var readTx = readConnection.BeginTransaction();
        using (var sharedLock = readConnection.CreateCommand())
        {
            sharedLock.Transaction = readTx;
            sharedLock.CommandText =
                "SELECT pg_advisory_xact_lock_shared(@lockKey)";
            sharedLock.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            sharedLock.ExecuteNonQuery();
        }

        using var boundedDb = new MetaDatabase(
            DataSource,
            Substitute.For<ILogger<MetaDatabase>>(),
            publicationCommitOptions: new PublicationCommitOptions
            {
                DrainTimeoutMilliseconds = 150,
                RetryDelayMilliseconds = 20,
                RelationLockTimeoutMilliseconds = 50,
                StatementTimeoutMilliseconds = 500,
                MaxExclusiveLockDurationMilliseconds = 500,
            });

        Assert.Throws<PublicationCommitBusyException>(() =>
            boundedDb.PublishScrapeRun(
                candidateScrapeId,
                promoteCachedResponses: false));

        var freeze = Db.GetPublicReadFreezeState();
        Assert.True(freeze.IsFrozen);
        Assert.Equal("publish", freeze.Reason);
        Assert.False(freeze.PublicationCommitPending);

        readTx.Commit();
        Db.FailScrapeRun(
            candidateScrapeId,
            "test",
            "cleanup");
    }

    [Fact]
    public void CommitBusyThenDegradedFailureIsolationAllowsNextPublication()
    {
        var oldScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(oldScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            oldScrapeId,
            promoteCachedResponses: false);
        var candidateScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(
            candidateScrapeId,
            1,
            20,
            2,
            200);
        Db.SetPublicReadFreeze(
            true,
            candidateScrapeId,
            "publish");

        using var readConnection = DataSource.OpenConnection();
        using var readTx = readConnection.BeginTransaction();
        using (var sharedLock = readConnection.CreateCommand())
        {
            sharedLock.Transaction = readTx;
            sharedLock.CommandText =
                "SELECT pg_advisory_xact_lock_shared(@lockKey)";
            sharedLock.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            sharedLock.ExecuteNonQuery();
        }

        using var boundedDb = new MetaDatabase(
            DataSource,
            Substitute.For<ILogger<MetaDatabase>>(),
            publicationCommitOptions: new PublicationCommitOptions
            {
                DrainTimeoutMilliseconds = 150,
                RetryDelayMilliseconds = 20,
                RelationLockTimeoutMilliseconds = 50,
                StatementTimeoutMilliseconds = 500,
                MaxExclusiveLockDurationMilliseconds = 500,
            });
        Assert.Throws<PublicationCommitBusyException>(() =>
            boundedDb.PublishScrapeRun(
                candidateScrapeId,
                promoteCachedResponses: false));

        boundedDb.FailScrapeRun(
                candidateScrapeId,
                "test",
                "injected");

        var freeze = Db.GetPublicReadFreezeState();
        Assert.True(freeze.IsFrozen);
        Assert.Equal("publish", freeze.Reason);
        Assert.False(freeze.PublicationCommitPending);
        Assert.Null(
            Db.GetPublicationPointerState()
                .WorkingPublicationId);
        Assert.Equal(
            PublicationGenerationStatus.Failed,
            Db.GetPublicationGenerationForScrape(
                candidateScrapeId)?.Status);

        readTx.Commit();
        var nextScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(
            nextScrapeId,
            1,
            30,
            3,
            300);
        Db.PublishScrapeRun(
            nextScrapeId,
            promoteCachedResponses: false);
        Assert.Equal(
            nextScrapeId,
            Db.GetPublicationPointerState()
                .PublishedScrapeId);
    }

    [Fact]
    public void PublishScrapeRun_LockNotAvailableRestoresPreviousFreeze()
    {
        var oldScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(oldScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            oldScrapeId,
            promoteCachedResponses: false);

        var candidateScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateScrapeId, 1, 20, 2, 200);
        Db.SetPublicReadFreeze(
            true,
            candidateScrapeId,
            "publish");
        CreatePublicationStateFailureTrigger(
            "publication_lock_not_available",
            PostgresErrorCodes.LockNotAvailable);
        try
        {
            using var boundedDb = new MetaDatabase(
                DataSource,
                Substitute.For<ILogger<MetaDatabase>>(),
                publicationCommitOptions: new PublicationCommitOptions
                {
                    DrainTimeoutMilliseconds = 1_000,
                    RetryDelayMilliseconds = 10,
                    RelationLockTimeoutMilliseconds = 50,
                    StatementTimeoutMilliseconds = 1_000,
                    MaxExclusiveLockDurationMilliseconds = 150,
                });

            Assert.Throws<PublicationCommitDeadlineExceededException>(
                () => boundedDb.PublishScrapeRun(
                    candidateScrapeId,
                    promoteCachedResponses: false));
        }
        finally
        {
            DropPublicationStateFailureTrigger(
                "publication_lock_not_available");
        }

        var freeze = Db.GetPublicReadFreezeState();
        Assert.True(freeze.IsFrozen);
        Assert.Equal("publish", freeze.Reason);
        Assert.False(freeze.PublicationCommitPending);
        Db.FailScrapeRun(
            candidateScrapeId,
            "test",
            "cleanup");
    }

    [Fact]
    public void PublishScrapeRun_NonLockExceptionRestoresPreviousFreeze()
    {
        var oldScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(oldScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            oldScrapeId,
            promoteCachedResponses: false);

        var candidateScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateScrapeId, 1, 20, 2, 200);
        Db.SetPublicReadFreeze(
            true,
            candidateScrapeId,
            "publish");
        CreatePublicationStateFailureTrigger(
            "publication_non_lock_failure",
            PostgresErrorCodes.RaiseException);
        try
        {
            var exception = Assert.Throws<PostgresException>(() =>
                Db.PublishScrapeRun(
                    candidateScrapeId,
                    promoteCachedResponses: false));
            Assert.Equal(
                PostgresErrorCodes.RaiseException,
                exception.SqlState);
        }
        finally
        {
            DropPublicationStateFailureTrigger(
                "publication_non_lock_failure");
        }

        var freeze = Db.GetPublicReadFreezeState();
        Assert.True(freeze.IsFrozen);
        Assert.Equal("publish", freeze.Reason);
        Assert.False(freeze.PublicationCommitPending);
        Db.FailScrapeRun(
            candidateScrapeId,
            "test",
            "cleanup");
    }

    [Fact]
    public void PublishScrapeRun_AlreadyPublishedClearsCommitIntent()
    {
        var scrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false);
        Db.SetPublicReadFreeze(
            true,
            scrapeId,
            PublicReadFreezeState.PublicationCommitIntentReason);

        Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false);

        Assert.False(Db.GetPublicReadFreezeState().IsFrozen);
    }

    [Fact]
    public async Task CommitIntent_RefreshesOldFreezeTimestampAndStaysActiveBetweenRetries()
    {
        var oldScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(oldScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            oldScrapeId,
            promoteCachedResponses: false);
        var candidateScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateScrapeId, 1, 20, 2, 200);
        var preparation = Db.PrepareScrapePublication(
            candidateScrapeId,
            promoteCachedResponses: false);
        Db.SetPublicReadFreeze(
            true,
            candidateScrapeId,
            "scrape");
        using (var conn = DataSource.OpenConnection())
        using (var ageFreeze = conn.CreateCommand())
        {
            ageFreeze.CommandText = """
                UPDATE scrape_publication_state
                SET public_reads_frozen_at =
                    now() - interval '2 hours'
                WHERE id = TRUE
                """;
            ageFreeze.ExecuteNonQuery();
        }

        var commitIntent =
            Db.BeginPublicationCommitIntent(candidateScrapeId);
        Assert.Throws<PublicationCommitBusyException>(() =>
            Db.BeginPublicationCommitIntent(
                candidateScrapeId));
        var initialLease = ReadPublicationCommitIntentLease();
        Assert.Equal(
            commitIntent.OwnerToken,
            initialLease.OwnerToken);
        Assert.True(
            initialLease.FrozenAtUtc
            > DateTime.UtcNow.AddSeconds(-5));
        Assert.True(
            initialLease.StartedAtUtc
            > DateTime.UtcNow.AddSeconds(-5));
        Assert.True(
            initialLease.HeartbeatAtUtc
            > DateTime.UtcNow.AddSeconds(-5));
        Assert.Equal(
            PublicationCommitIntentReconciliationStatus.Fresh,
            Db.ReconcileStalePublicationCommitIntent(
                    TimeSpan.FromSeconds(30))
                .Status);

        using var readConnection = DataSource.OpenConnection();
        using var readTx = readConnection.BeginTransaction();
        using (var sharedLock = readConnection.CreateCommand())
        {
            sharedLock.Transaction = readTx;
            sharedLock.CommandText =
                "SELECT pg_advisory_xact_lock_shared(@lockKey)";
            sharedLock.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            sharedLock.ExecuteNonQuery();
        }

        using var boundedDb = new MetaDatabase(
            DataSource,
            Substitute.For<ILogger<MetaDatabase>>(),
            publicationCommitOptions: new PublicationCommitOptions
            {
                DrainTimeoutMilliseconds = 2_000,
                RetryDelayMilliseconds = 20,
                RelationLockTimeoutMilliseconds = 100,
                StatementTimeoutMilliseconds = 1_000,
                MaxExclusiveLockDurationMilliseconds = 1_000,
                StaleCommitIntentSeconds = 30,
            });
        var commitTask = Task.Run(() =>
            boundedDb.CommitPreparedScrapePublication(
                preparation,
                commitIntent));
        await WaitForPublicationCommitHeartbeatAfterAsync(
            initialLease.HeartbeatAtUtc);

        for (var sample = 0; sample < 3; sample++)
        {
            var reconciliation =
                Db.ReconcileStalePublicationCommitIntent(
                    TimeSpan.FromSeconds(30));
            Assert.Equal(
                PublicationCommitIntentReconciliationStatus.Fresh,
                reconciliation.Status);
            Assert.Equal(
                PublicationGenerationStatus.Ready,
                Db.GetPublicationGeneration(
                    preparation.PublicationId)?.Status);
            Assert.Equal(
                preparation.PublicationId,
                Db.GetPublicationPointerState()
                    .WorkingPublicationId);
            await Task.Delay(30);
        }

        readTx.Commit();
        var commit =
            await commitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Db.CleanupPublishedScrapePublication(
            preparation,
            commit);
        Assert.Equal(
            preparation.PublicationId,
            Db.GetPublicationPointerState()
                .CurrentPublicationId);
        Assert.False(
            Db.GetPublicReadFreezeState().IsFrozen);
    }

    [Fact]
    public void BeginPublicationCommitIntent_UpsertsMissingSingletonAndReconstructsPointers()
    {
        var publishedScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(
            publishedScrapeId,
            1,
            10,
            1,
            100);
        Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var publishedPublicationId =
            Db.GetPublicationPointerState()
                .CurrentPublicationId;
        var candidateScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(
            candidateScrapeId,
            1,
            20,
            2,
            200);
        var preparation = Db.PrepareScrapePublication(
            candidateScrapeId,
            promoteCachedResponses: false);
        var candidatePublicationId =
            preparation.PublicationId;
        var activePreparedTable =
            BandRankingStorageNames
                .GetPreparedPublishedRankingTable(
                    candidatePublicationId,
                    "Band_Duets");
        using (var conn = DataSource.OpenConnection())
        using (var deleteState = conn.CreateCommand())
        {
            deleteState.CommandText =
                "DELETE FROM scrape_publication_state WHERE id = TRUE";
            deleteState.ExecuteNonQuery();
        }
        var sweep = Db.SweepPublicationBandTableOrphans();
        Assert.True(sweep.Completed);
        Assert.True(RelationExists(activePreparedTable));

        var commitIntent =
            Db.BeginPublicationCommitIntent(candidateScrapeId);

        var pointers = Db.GetPublicationPointerState();
        Assert.Equal(
            publishedPublicationId,
            pointers.CurrentPublicationId);
        Assert.Equal(
            candidatePublicationId,
            pointers.WorkingPublicationId);
        Assert.Equal(
            publishedScrapeId,
            pointers.PublishedScrapeId);
        Assert.True(
            Db.GetPublicReadFreezeState()
                .PublicationCommitPending);

        Db.RestorePublicationCommitIntent(
            commitIntent,
            PublicReadFreezeState.NotFrozen);
        Db.FailScrapeRun(
            candidateScrapeId,
            "test",
            "cleanup");
    }

    [Fact]
    public async Task PublishScrapeRun_ConcurrentSameScrapeWinnerRestoresLoserIntent()
    {
        var oldScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(oldScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            oldScrapeId,
            promoteCachedResponses: false);
        var candidateScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateScrapeId, 1, 20, 2, 200);

        using var preparedSignal = new ManualResetEventSlim();
        using var releaseLoser = new ManualResetEventSlim();
        PublicationPreparationResult? loserPreparation = null;
        using var losingPublisher = new MetaDatabase(
            DataSource,
            Substitute.For<ILogger<MetaDatabase>>());
        losingPublisher.PublicationPreparedTestHook =
            preparation =>
            {
                loserPreparation = preparation;
                preparedSignal.Set();
                if (!releaseLoser.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException(
                        "Timed out waiting to release losing publisher.");
                }
            };

        var losingTask = Task.Run(() =>
            losingPublisher.PublishScrapeRun(
                candidateScrapeId,
                promoteCachedResponses: false));
        Assert.True(
            preparedSignal.Wait(TimeSpan.FromSeconds(10)));
        Assert.NotNull(loserPreparation);

        var winningIntent =
            Db.BeginPublicationCommitIntent(candidateScrapeId);
        var winningCommit =
            Db.CommitPreparedScrapePublication(
                loserPreparation!,
                winningIntent);
        Assert.False(winningCommit.AlreadyPublished);
        Db.CleanupPublishedScrapePublication(
            loserPreparation!,
            winningCommit);

        releaseLoser.Set();
        await losingTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            loserPreparation!.PublicationId,
            Db.GetPublicationPointerState()
                .CurrentPublicationId);
        Assert.False(
            Db.GetPublicReadFreezeState().IsFrozen);
        var finalLease = ReadPublicationCommitIntentLease();
        Assert.Null(finalLease.OwnerToken);
        Assert.Null(finalLease.StartedAtUtc);
        Assert.Null(finalLease.HeartbeatAtUtc);
    }

    [Fact]
    public void ReconcileStalePublicationCommitIntent_FailsWorkingCandidateAndClearsLatch()
    {
        var oldScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(oldScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            oldScrapeId,
            promoteCachedResponses: false);
        var oldPublicationId =
            Db.GetPublicationPointerState().CurrentPublicationId;

        var candidateScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateScrapeId, 1, 20, 2, 200);
        var preparation = Db.PrepareScrapePublication(
            candidateScrapeId,
            promoteCachedResponses: false);
        var candidatePublicationId =
            preparation.PublicationId;
        var preparedBandTable =
            BandRankingStorageNames
                .GetPreparedPublishedRankingTable(
                    candidatePublicationId,
                    "Band_Duets");
        Assert.True(RelationExists(preparedBandTable));
        SetStalePublicationCommitIntent(candidateScrapeId);

        var result =
            Db.ReconcileStalePublicationCommitIntent(
                TimeSpan.FromSeconds(1));

        Assert.Equal(
            PublicationCommitIntentReconciliationStatus
                .FailedCandidateIsolated,
            result.Status);
        Assert.False(Db.GetPublicReadFreezeState().IsFrozen);
        Assert.Equal(
            oldPublicationId,
            Db.GetPublicationPointerState().CurrentPublicationId);
        Assert.Null(
            Db.GetPublicationPointerState().WorkingPublicationId);
        Assert.Equal(
            PublicationGenerationStatus.Failed,
            Db.GetPublicationGeneration(
                candidatePublicationId)?.Status);
        Assert.True(
            Db.GetFailedCandidateReadIsolationState().IsFrozen);
        Assert.False(RelationExists(preparedBandTable));
    }

    [Fact]
    public void ReconcileAbandonedReadyGeneration_AllowsNextPublicationAfterRestart()
    {
        var publishedScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(
            publishedScrapeId,
            1,
            10,
            1,
            100);
        Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var abandonedScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(
            abandonedScrapeId,
            1,
            20,
            2,
            200);
        var abandoned = Db.PrepareScrapePublication(
            abandonedScrapeId,
            promoteCachedResponses: false);
        var preparedBandTable =
            BandRankingStorageNames
                .GetPreparedPublishedRankingTable(
                    abandoned.PublicationId,
                    "Band_Duets");
        Assert.True(RelationExists(preparedBandTable));

        var reconciliation =
            Db.ReconcileAbandonedWorkingPublication(
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30));

        Assert.Equal(
            PublicationCommitIntentReconciliationStatus
                .AbandonedWorkingIsolated,
            reconciliation.Status);
        Assert.Null(
            Db.GetPublicationPointerState()
                .WorkingPublicationId);
        Assert.Equal(
            PublicationGenerationStatus.Failed,
            Db.GetPublicationGeneration(
                abandoned.PublicationId)?.Status);
        Assert.False(RelationExists(preparedBandTable));

        var nextScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(
            nextScrapeId,
            1,
            30,
            3,
            300);
        Db.PublishScrapeRun(
            nextScrapeId,
            promoteCachedResponses: false);
        Assert.Equal(
            nextScrapeId,
            Db.GetPublicationPointerState()
                .PublishedScrapeId);
    }

    [Fact]
    public void ReconcileStalePublicationCommitIntent_DoesNotMaskActiveCommit()
    {
        var scrapeId = Db.StartScrapeRun();
        SetStalePublicationCommitIntent(scrapeId);

        using var lockConnection = DataSource.OpenConnection();
        using var lockTransaction =
            lockConnection.BeginTransaction();
        using (var publicationLock =
               lockConnection.CreateCommand())
        {
            publicationLock.Transaction = lockTransaction;
            publicationLock.CommandText =
                "SELECT pg_advisory_xact_lock(@lockKey)";
            publicationLock.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            publicationLock.ExecuteNonQuery();
        }

        var result =
            Db.ReconcileStalePublicationCommitIntent(
                TimeSpan.FromSeconds(1));

        Assert.Equal(
            PublicationCommitIntentReconciliationStatus.Active,
            result.Status);
        Assert.True(
            Db.GetPublicReadFreezeState()
                .PublicationCommitPending);
        lockTransaction.Commit();

        _ = Db.ReconcileStalePublicationCommitIntent(
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void PendingIsolationWithoutWorkingPointerFailsFrozenScrapeBeforeUnfreeze()
    {
        var publishedScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(
            publishedScrapeId,
            1,
            10,
            1,
            100);
        Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var failedScrapeId = Db.StartScrapeRun();
        using (var conn = DataSource.OpenConnection())
        using (var state = conn.CreateCommand())
        {
            state.CommandText = """
                UPDATE scrape_publication_state
                SET working_publication_id = NULL,
                    public_reads_frozen = TRUE,
                    public_reads_frozen_at = now(),
                    public_reads_frozen_scrape_id = @scrapeId,
                    public_reads_frozen_reason = @reason,
                    updated_at = now()
                WHERE id = TRUE
                """;
            state.Parameters.AddWithValue(
                "scrapeId",
                checked((int)failedScrapeId));
            state.Parameters.AddWithValue(
                "reason",
                PublicReadFreezeState
                    .PublicationFailureIsolationPendingReason);
            state.ExecuteNonQuery();
        }

        var result =
            Db.ReconcileStalePublicationCommitIntent(
                TimeSpan.FromSeconds(30));

        Assert.Equal(
            PublicationCommitIntentReconciliationStatus
                .FailedCandidateIsolated,
            result.Status);
        Assert.Equal(
            "failed",
            Db.GetScrapeResumeState(failedScrapeId)?.Status);
        Assert.False(
            Db.GetPublicReadFreezeState().IsFrozen);
    }

    [Fact]
    public void PendingIsolationForCurrentPublishedScrapeClearsWithoutFailingPublication()
    {
        var publishedScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(
            publishedScrapeId,
            1,
            10,
            1,
            100);
        Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var publicationId =
            Db.GetPublicationPointerState()
                .CurrentPublicationId!.Value;
        using (var conn = DataSource.OpenConnection())
        using (var state = conn.CreateCommand())
        {
            state.CommandText = """
                UPDATE scrape_publication_state
                SET public_reads_frozen = TRUE,
                    public_reads_frozen_at = now(),
                    public_reads_frozen_scrape_id = @scrapeId,
                    public_reads_frozen_reason = @reason,
                    updated_at = now()
                WHERE id = TRUE
                """;
            state.Parameters.AddWithValue(
                "scrapeId",
                checked((int)publishedScrapeId));
            state.Parameters.AddWithValue(
                "reason",
                PublicReadFreezeState
                    .PublicationFailureIsolationPendingReason);
            state.ExecuteNonQuery();
        }

        var reconciliation =
            Db.ReconcileStalePublicationCommitIntent(
                TimeSpan.FromSeconds(30));

        Assert.Equal(
            PublicationCommitIntentReconciliationStatus.Cleared,
            reconciliation.Status);
        Assert.False(
            Db.GetPublicReadFreezeState().IsFrozen);
        Assert.Equal(
            publicationId,
            Db.GetPublicationPointerState()
                .CurrentPublicationId);
        Assert.Equal(
            PublicationGenerationStatus.Current,
            Db.GetPublicationGeneration(publicationId)?.Status);
        Assert.Equal(
            "completed",
            Db.GetScrapeResumeState(publishedScrapeId)?.Status);
        Assert.False(
            Db.GetFailedCandidateReadIsolationState().IsFrozen);
    }

    [Fact]
    public void PendingIsolationUpdateFailureRemainsFailClosedUntilRetry()
    {
        var publishedScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(
            publishedScrapeId,
            1,
            10,
            1,
            100);
        Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var failedScrapeId = Db.StartScrapeRun();
        using (var conn = DataSource.OpenConnection())
        using (var state = conn.CreateCommand())
        {
            state.CommandText = """
                UPDATE scrape_publication_state
                SET working_publication_id = NULL,
                    public_reads_frozen = TRUE,
                    public_reads_frozen_at = now(),
                    public_reads_frozen_scrape_id = @scrapeId,
                    public_reads_frozen_reason = @reason,
                    updated_at = now()
                WHERE id = TRUE
                """;
            state.Parameters.AddWithValue(
                "scrapeId",
                checked((int)failedScrapeId));
            state.Parameters.AddWithValue(
                "reason",
                PublicReadFreezeState
                    .PublicationFailureIsolationPendingReason);
            state.ExecuteNonQuery();
        }
        CreateScrapeFailureTrigger(
            "pending_isolation_failure");
        try
        {
            Assert.Throws<PostgresException>(() =>
                Db.ReconcileStalePublicationCommitIntent(
                    TimeSpan.FromSeconds(30)));
        }
        finally
        {
            DropScrapeFailureTrigger(
                "pending_isolation_failure");
        }

        Assert.True(
            Db.GetPublicReadFreezeState()
                .PublicationFailureIsolationPending);
        Assert.NotEqual(
            "failed",
            Db.GetScrapeResumeState(failedScrapeId)?.Status);

        _ = Db.ReconcileStalePublicationCommitIntent(
            TimeSpan.FromSeconds(30));
        Assert.False(
            Db.GetPublicReadFreezeState().IsFrozen);
        Assert.Equal(
            "failed",
            Db.GetScrapeResumeState(failedScrapeId)?.Status);
    }

    [Fact]
    public void PendingIsolationDoesNotFailNewMismatchedWorkingGeneration()
    {
        var publishedScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(
            publishedScrapeId,
            1,
            10,
            1,
            100);
        Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var failedScrapeId = Db.StartScrapeRun();
        var newerScrapeId = Db.StartScrapeRun();
        var newerPublicationId =
            Db.GetPublicationPointerState()
                .WorkingPublicationId;
        using (var conn = DataSource.OpenConnection())
        using (var state = conn.CreateCommand())
        {
            state.CommandText = """
                UPDATE scrape_publication_state
                SET public_reads_frozen = TRUE,
                    public_reads_frozen_at = now(),
                    public_reads_frozen_scrape_id = @scrapeId,
                    public_reads_frozen_reason = @reason,
                    updated_at = now()
                WHERE id = TRUE
                """;
            state.Parameters.AddWithValue(
                "scrapeId",
                checked((int)failedScrapeId));
            state.Parameters.AddWithValue(
                "reason",
                PublicReadFreezeState
                    .PublicationFailureIsolationPendingReason);
            state.ExecuteNonQuery();
        }

        _ = Db.ReconcileStalePublicationCommitIntent(
            TimeSpan.FromSeconds(30));

        Assert.Equal(
            "failed",
            Db.GetScrapeResumeState(failedScrapeId)?.Status);
        Assert.Equal(
            "running",
            Db.GetScrapeResumeState(newerScrapeId)?.Status);
        Assert.Equal(
            newerPublicationId,
            Db.GetPublicationPointerState()
                .WorkingPublicationId);
        Assert.Equal(
            PublicationGenerationStatus.Building,
            Db.GetPublicationGeneration(
                newerPublicationId!.Value)?.Status);
        Db.FailScrapeRun(
            newerScrapeId,
            "test",
            "cleanup");
    }

    [Fact]
    public void PublicationBandOrphanSweep_DropsOnlyUnreferencedExactArtifacts()
    {
        var firstScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(firstScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            firstScrapeId,
            promoteCachedResponses: false);
        var firstPublicationId =
            Db.GetPublicationPointerState()
                .CurrentPublicationId!.Value;

        var secondScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(secondScrapeId, 1, 20, 2, 200);
        Db.PublishScrapeRun(
            secondScrapeId,
            promoteCachedResponses: false);
        var retainedTable =
            BandRankingStorageNames
                .GetRetainedPublishedRankingTable(
                    firstPublicationId,
                    "Band_Duets");
        Assert.True(RelationExists(retainedTable));

        var workingScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(workingScrapeId, 1, 30, 3, 300);
        var preparation = Db.PrepareScrapePublication(
            workingScrapeId,
            promoteCachedResponses: false);
        var activePreparedTable =
            BandRankingStorageNames
                .GetPreparedPublishedRankingTable(
                    preparation.PublicationId,
                    "Band_Duets");
        var orphanPreparedTable =
            BandRankingStorageNames
                .GetPreparedPublishedRankingTable(
                    999_991,
                    "Band_Duets");
        var orphanRetainedTable =
            BandRankingStorageNames
                .GetRetainedPublishedRankingTable(
                    999_992,
                    "Band_Duets");
        CreateSimpleTable(orphanPreparedTable);
        CreateSimpleTable(orphanRetainedTable);

        var result = Db.SweepPublicationBandTableOrphans();

        Assert.True(result.LockAcquired);
        Assert.True(result.Completed);
        Assert.True(RelationExists(activePreparedTable));
        Assert.True(RelationExists(retainedTable));
        Assert.False(RelationExists(orphanPreparedTable));
        Assert.False(RelationExists(orphanRetainedTable));
        Assert.Contains(
            orphanPreparedTable,
            result.DroppedTables);
        Assert.Contains(
            orphanRetainedTable,
            result.DroppedTables);
        Db.FailScrapeRun(
            workingScrapeId,
            "test",
            "cleanup");
    }

    [Fact]
    public void PublicationBandOrphanSweep_DefersLockedTableAndNextPrepareRetries()
    {
        var publishedScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(
            publishedScrapeId,
            1,
            10,
            1,
            100);
        Db.PublishScrapeRun(
            publishedScrapeId,
            promoteCachedResponses: false);
        var orphanTable =
            BandRankingStorageNames
                .GetPreparedPublishedRankingTable(
                    999_993,
                    "Band_Duets");
        CreateSimpleTable(orphanTable);

        using var blocker = DataSource.OpenConnection();
        using var blockerTx = blocker.BeginTransaction();
        using (var lockTable = blocker.CreateCommand())
        {
            lockTable.Transaction = blockerTx;
            lockTable.CommandText =
                $"LOCK TABLE {BandRankingStorageNames.QuoteIdentifier(orphanTable)} " +
                "IN ACCESS SHARE MODE";
            lockTable.ExecuteNonQuery();
        }

        var deferred = Db.SweepPublicationBandTableOrphans();
        Assert.True(deferred.LockAcquired);
        Assert.False(deferred.Completed);
        Assert.True(RelationExists(orphanTable));
        blockerTx.Commit();

        var nextScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(nextScrapeId, 1, 20, 2, 200);
        _ = Db.PrepareScrapePublication(
            nextScrapeId,
            promoteCachedResponses: false);

        Assert.False(RelationExists(orphanTable));
        Db.FailScrapeRun(
            nextScrapeId,
            "test",
            "cleanup");
    }

    [Fact]
    public async Task PostCommitCleanup_DefersWhileCurrentCacheRebuildLeaseIsActive()
    {
        var oldScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(oldScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            oldScrapeId,
            promoteCachedResponses: false);

        var candidateScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateScrapeId, 1, 20, 2, 200);
        var preparation = Db.PrepareScrapePublication(
            candidateScrapeId,
            promoteCachedResponses: false);
        var commitIntent =
            Db.BeginPublicationCommitIntent(candidateScrapeId);
        var commit = Db.CommitPreparedScrapePublication(
            preparation,
            commitIntent);
        var publicationId = commit.PublicationId;
        var rebuiltJson = new byte[] { 7, 8, 9 };

        using (Db.AcquirePublicationCacheBuildLease(
                   publicationId,
                   requireCurrentPublication: true))
        {
            Db.BulkSetCachedResponsesStaging(
            [
                (
                    Key: "concurrent-rebuild",
                    Json: rebuiltJson,
                    ETag: "\"rebuilt\""),
            ],
            publicationId);

            await Task.Run(() =>
                Db.CleanupPublishedScrapePublication(
                    preparation,
                    commit));

            using (var conn = DataSource.OpenConnection())
            using (var countStaging = conn.CreateCommand())
            {
                countStaging.CommandText = """
                    SELECT COUNT(*)
                    FROM publication_api_response_cache_staging
                    WHERE publication_id = @publicationId
                    """;
                countStaging.Parameters.AddWithValue(
                    "publicationId",
                    publicationId);
                Assert.Equal(
                    1L,
                    (long)countStaging.ExecuteScalar()!);
            }

            Db.SwapCachedResponsesFromStaging(publicationId);
        }

        Assert.Equal(
            rebuiltJson,
            Db.GetCachedResponse(
                publicationId,
                "concurrent-rebuild")?.Json);
        Db.CleanupPublishedScrapePublication(
            preparation,
            commit);
        Assert.Equal(
            rebuiltJson,
            Db.GetCachedResponse(
                publicationId,
                "concurrent-rebuild")?.Json);
    }

    [Fact]
    public void PrepareScrapePublication_BlocksUnsafeEmptyGenerationCacheInheritance()
    {
        var oldScrapeId = Db.StartScrapeRun();
        Db.BulkSetCachedResponses(
        [
            (
                Key: "legacy-only-cache",
                Json: new byte[] { 1 },
                ETag: "\"legacy\""),
        ]);
        Db.CompleteScrapeRun(oldScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            oldScrapeId,
            promoteCachedResponses: false);
        var oldPublicationId =
            Db.GetPublicationPointerState()
                .CurrentPublicationId!.Value;
        using (var conn = DataSource.OpenConnection())
        using (var removeGeneration = conn.CreateCommand())
        {
            removeGeneration.CommandText = """
                DELETE FROM publication_api_response_cache
                WHERE publication_id = @publicationId
                """;
            removeGeneration.Parameters.AddWithValue(
                "publicationId",
                oldPublicationId);
            removeGeneration.ExecuteNonQuery();
        }

        var candidateScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateScrapeId, 1, 20, 2, 200);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Db.PrepareScrapePublication(
                candidateScrapeId,
                promoteCachedResponses: false));

        Assert.Contains(
            "current generation",
            exception.Message);
        Assert.Contains(
            "legacy compatibility cache",
            exception.Message);
        Assert.Equal(
            oldPublicationId,
            Db.GetPublicationPointerState()
                .CurrentPublicationId);
        using (var conn = DataSource.OpenConnection())
        using (var countLegacy = conn.CreateCommand())
        {
            countLegacy.CommandText =
                "SELECT COUNT(*) FROM api_response_cache";
            Assert.Equal(
                1L,
                (long)countLegacy.ExecuteScalar()!);
        }
        Db.FailScrapeRun(
            candidateScrapeId,
            "test",
            "cleanup");
    }

    [Fact]
    public void PublishScrapeRun_QueryCanceledIsNotRetriedAndRestoresFreeze()
    {
        var oldScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(oldScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            oldScrapeId,
            promoteCachedResponses: false);

        var candidateScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateScrapeId, 1, 20, 2, 200);
        Db.SetPublicReadFreeze(
            true,
            candidateScrapeId,
            "publish");
        CreateSlowPublicationStateTrigger(
            "publication_slow_non_lock",
            delaySeconds: 0.2);
        try
        {
            using var boundedDb = new MetaDatabase(
                DataSource,
                Substitute.For<ILogger<MetaDatabase>>(),
                publicationCommitOptions: new PublicationCommitOptions
                {
                    DrainTimeoutMilliseconds = 1_000,
                    RetryDelayMilliseconds = 10,
                    RelationLockTimeoutMilliseconds = 200,
                    StatementTimeoutMilliseconds = 50,
                    MaxExclusiveLockDurationMilliseconds = 500,
                });
            var stopwatch =
                System.Diagnostics.Stopwatch.StartNew();
            var exception = Assert.Throws<PostgresException>(() =>
                boundedDb.PublishScrapeRun(
                    candidateScrapeId,
                    promoteCachedResponses: false));
            stopwatch.Stop();

            Assert.Equal(
                PostgresErrorCodes.QueryCanceled,
                exception.SqlState);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromMilliseconds(400));
        }
        finally
        {
            DropPublicationStateFailureTrigger(
                "publication_slow_non_lock");
        }

        var freeze = Db.GetPublicReadFreezeState();
        Assert.True(freeze.IsFrozen);
        Assert.Equal("publish", freeze.Reason);
        Assert.False(freeze.PublicationCommitPending);
        Db.FailScrapeRun(
            candidateScrapeId,
            "test",
            "cleanup");
    }

    [Fact]
    public void FailScrapeRun_CleansPreparedCandidateAndKeepsPreviousPublication()
    {
        var oldScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(oldScrapeId, 1, 10, 1, 100);
        Db.PublishScrapeRun(
            oldScrapeId,
            promoteCachedResponses: false);
        var oldPublicationId =
            Db.GetPublicationPointerState().CurrentPublicationId;

        var candidateScrapeId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(candidateScrapeId, 1, 20, 2, 200);
        var preparation = Db.PrepareScrapePublication(
            candidateScrapeId,
            promoteCachedResponses: false);
        var preparedBandTable =
            BandRankingStorageNames.GetPreparedPublishedRankingTable(
                preparation.PublicationId,
                "Band_Duets");
        Assert.True(RelationExists(preparedBandTable));

        Db.FailScrapeRun(
            candidateScrapeId,
            MetaDatabase.PublicationReadIsolationFailurePhase,
            "injected");

        Assert.Equal(
            oldPublicationId,
            Db.GetPublicationPointerState().CurrentPublicationId);
        Assert.Null(
            Db.GetPublicationPointerState().WorkingPublicationId);
        Assert.Equal(
            PublicationGenerationStatus.Failed,
            Db.GetPublicationGeneration(preparation.PublicationId)?.Status);
        Assert.False(RelationExists(preparedBandTable));
    }

    [Fact]
    public void PublishScrapeRun_rejects_missing_scope_mapping_and_retains_previous_publication()
    {
        var oldId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(oldId, 1, 10, 1, 100);
        Db.PublishScrapeRun(oldId, promoteCachedResponses: false);

        var nextId = Db.StartScrapeRun();
        Db.CompleteScrapeRun(nextId, 1, 10, 1, 100);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Db.PublishScrapeRun(
                nextId,
                promoteCachedResponses: false,
                expectedPublishedScopeCount: 1));

        Assert.Contains("per-scope source mapping is invalid", exception.Message);
        Assert.Equal(oldId, Db.GetPublishedScrapeRun()?.Id);
    }

    private async Task WaitForBlockedRelationLockAsync(string relationName)
    {
        using var conn = DataSource.OpenConnection();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_locks
                    WHERE relation = to_regclass(@relationName)
                      AND NOT granted
                )
                """;
            cmd.Parameters.AddWithValue("relationName", relationName);
            if (cmd.ExecuteScalar() is true)
                return;

            await Task.Delay(100);
        }
        throw new TimeoutException(
            $"Publication did not block on relation {relationName}.");
    }

    private bool RelationExists(string relationName)
    {
        using var conn = DataSource.OpenConnection();
        using var command = conn.CreateCommand();
        command.CommandText =
            "SELECT to_regclass(@relationName) IS NOT NULL";
        command.Parameters.AddWithValue("relationName", relationName);
        return command.ExecuteScalar() is true;
    }

    private void CreateSimpleTable(string relationName)
    {
        using var conn = DataSource.OpenConnection();
        using var command = conn.CreateCommand();
        command.CommandText =
            $"CREATE TABLE {BandRankingStorageNames.QuoteIdentifier(relationName)} (id INTEGER)";
        command.ExecuteNonQuery();
    }

    private long CountUngrantedPublicationAdvisoryLocks()
    {
        using var conn = DataSource.OpenConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND NOT granted
              AND (
                  (classid::bigint << 32)
                  | objid::bigint
              ) = @lockKey
            """;
        command.Parameters.AddWithValue(
            "lockKey",
            PublicationGenerationSchema.AdvisoryLockKey);
        return (long)command.ExecuteScalar()!;
    }

    private void SetStalePublicationCommitIntent(long scrapeId)
    {
        using var conn = DataSource.OpenConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            UPDATE scrape_publication_state
            SET public_reads_frozen = TRUE,
                public_reads_frozen_at =
                    now() - interval '5 minutes',
                public_reads_frozen_scrape_id = @scrapeId,
                public_reads_frozen_reason =
                    @commitIntentReason,
                updated_at = now()
            WHERE id = TRUE
            """;
        command.Parameters.AddWithValue(
            "scrapeId",
            checked((int)scrapeId));
        command.Parameters.AddWithValue(
            "commitIntentReason",
            PublicReadFreezeState.PublicationCommitIntentReason);
        command.ExecuteNonQuery();
    }

    private (
        DateTime? FrozenAtUtc,
        DateTime? StartedAtUtc,
        DateTime? HeartbeatAtUtc,
        string? OwnerToken)
        ReadPublicationCommitIntentLease()
    {
        using var conn = DataSource.OpenConnection();
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT
                public_reads_frozen_at,
                publication_commit_intent_started_at,
                publication_commit_intent_heartbeat_at,
                publication_commit_intent_owner
            FROM scrape_publication_state
            WHERE id = TRUE
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (
            reader.IsDBNull(0)
                ? null
                : reader.GetDateTime(0),
            reader.IsDBNull(1)
                ? null
                : reader.GetDateTime(1),
            reader.IsDBNull(2)
                ? null
                : reader.GetDateTime(2),
            reader.IsDBNull(3)
                ? null
                : reader.GetString(3));
    }

    private async Task WaitForPublicationCommitHeartbeatAfterAsync(
        DateTime? previousHeartbeat)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var current =
                ReadPublicationCommitIntentLease()
                    .HeartbeatAtUtc;
            if (current.HasValue
                && (!previousHeartbeat.HasValue
                    || current.Value
                        > previousHeartbeat.Value))
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(
            "Publication commit heartbeat did not advance.");
    }

    private void CreatePublicationStateFailureTrigger(
        string name,
        string sqlState)
    {
        using var conn = DataSource.OpenConnection();
        using var command = conn.CreateCommand();
        command.CommandText = $"""
            CREATE OR REPLACE FUNCTION "{name}_fn"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF OLD.public_reads_frozen_reason =
                        '{PublicReadFreezeState.PublicationCommitIntentReason}'
                   AND NOT NEW.public_reads_frozen
                THEN
                    RAISE EXCEPTION 'injected publication failure'
                        USING ERRCODE = '{sqlState}';
                END IF;
                RETURN NEW;
            END
            $$;

            CREATE TRIGGER "{name}"
            BEFORE UPDATE ON scrape_publication_state
            FOR EACH ROW
            EXECUTE FUNCTION "{name}_fn"();
            """;
        command.ExecuteNonQuery();
    }

    private void CreateSlowPublicationStateTrigger(
        string name,
        double delaySeconds)
    {
        using var conn = DataSource.OpenConnection();
        using var command = conn.CreateCommand();
        command.CommandText = $"""
            CREATE OR REPLACE FUNCTION "{name}_fn"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF OLD.public_reads_frozen_reason =
                        '{PublicReadFreezeState.PublicationCommitIntentReason}'
                   AND NOT NEW.public_reads_frozen
                THEN
                    PERFORM pg_sleep({delaySeconds.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)});
                END IF;
                RETURN NEW;
            END
            $$;

            CREATE TRIGGER "{name}"
            BEFORE UPDATE ON scrape_publication_state
            FOR EACH ROW
            EXECUTE FUNCTION "{name}_fn"();
            """;
        command.ExecuteNonQuery();
    }

    private void DropPublicationStateFailureTrigger(string name)
    {
        using var conn = DataSource.OpenConnection();
        using var command = conn.CreateCommand();
        command.CommandText = $"""
            DROP TRIGGER IF EXISTS "{name}"
                ON scrape_publication_state;
            DROP FUNCTION IF EXISTS "{name}_fn"();
            """;
        command.ExecuteNonQuery();
    }

    private void CreateScrapeFailureTrigger(string name)
    {
        using var conn = DataSource.OpenConnection();
        using var command = conn.CreateCommand();
        command.CommandText = $"""
            CREATE OR REPLACE FUNCTION "{name}_fn"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF NEW.status = 'failed' THEN
                    RAISE EXCEPTION 'injected scrape failure update'
                        USING ERRCODE = 'P0001';
                END IF;
                RETURN NEW;
            END
            $$;

            CREATE TRIGGER "{name}"
            BEFORE UPDATE ON scrape_log
            FOR EACH ROW
            EXECUTE FUNCTION "{name}_fn"();
            """;
        command.ExecuteNonQuery();
    }

    private void DropScrapeFailureTrigger(string name)
    {
        using var conn = DataSource.OpenConnection();
        using var command = conn.CreateCommand();
        command.CommandText = $"""
            DROP TRIGGER IF EXISTS "{name}" ON scrape_log;
            DROP FUNCTION IF EXISTS "{name}_fn"();
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task SetPublicReadFreeze_skips_ddl_lock_when_publication_schema_is_complete()
    {
        await using var blockerConnection = await DataSource.OpenConnectionAsync();
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await using (var blocker = blockerConnection.CreateCommand())
        {
            blocker.Transaction = blockerTransaction;
            blocker.CommandText = "SELECT COUNT(*) FROM scrape_publication_state";
            await blocker.ExecuteScalarAsync();
        }

        var freezeTask = Task.Run(() => Db.SetPublicReadFreeze(true, reason: "lock-safety-test"));
        await freezeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(Db.GetPublicReadFreezeState().IsFrozen);
        await blockerTransaction.RollbackAsync();
    }

    // ═══ AccountNames ═══════════════════════════════════════════

    [Fact]
    public void InsertAccountIds_creates_unresolved_entries()
    {
        var inserted = Db.InsertAccountIds(["acct_1", "acct_2"]);
        Assert.Equal(2, inserted);

        var unresolved = Db.GetUnresolvedAccountIds();
        Assert.Contains("acct_1", unresolved);
        Assert.Contains("acct_2", unresolved);
    }

    [Fact]
    public void InsertAccountIds_ignores_duplicates()
    {
        Db.InsertAccountIds(["acct_1"]);
        var inserted = Db.InsertAccountIds(["acct_1"]);
        Assert.Equal(0, inserted);
    }

    [Fact]
    public void InsertAccountNames_resolves_display_names()
    {
        Db.InsertAccountIds(["acct_1"]);
        Db.InsertAccountNames([("acct_1", "PlayerOne")]);

        var name = Db.GetDisplayName("acct_1");
        Assert.Equal("PlayerOne", name);

        Assert.Equal(0, Db.GetUnresolvedAccountCount());
    }

    [Fact]
    public void GetAccountIdForUsername_finds_by_display_name()
    {
        Db.InsertAccountNames([("acct_1", "PlayerOne")]);
        var id = Db.GetAccountIdForUsername("PlayerOne");
        Assert.Equal("acct_1", id);
    }

    [Fact]
    public void GetAccountIdForUsername_is_case_insensitive()
    {
        Db.InsertAccountNames([("acct_1", "PlayerOne")]);
        var id = Db.GetAccountIdForUsername("playerone");
        Assert.Equal("acct_1", id);
    }

    [Fact]
    public void GetAccountIdForUsername_returns_null_for_unknown()
    {
        var id = Db.GetAccountIdForUsername("nobody");
        Assert.Null(id);
    }

    [Fact]
    public void SearchAccountNames_is_case_insensitive_for_substring_matches()
    {
        Db.InsertAccountNames([
            ("acct_1", "AlphaSearchBeta"),
            ("acct_2", "CompletelyDifferent"),
        ]);

        var results = Db.SearchAccountNames("search");

        Assert.Single(results);
        Assert.Equal("acct_1", results[0].AccountId);
        Assert.Equal("AlphaSearchBeta", results[0].DisplayName);
    }

    [Fact]
    public void SearchAccountNames_prioritizes_prefix_matches_then_shorter_names()
    {
        Db.InsertAccountNames([
            ("acct_1", "Search"),
            ("acct_2", "SearchLonger"),
            ("acct_3", "AlphaSearch"),
        ]);

        var results = Db.SearchAccountNames("Search", limit: 3);

        Assert.Equal(3, results.Count);
        Assert.Equal("acct_1", results[0].AccountId);
        Assert.Equal("acct_2", results[1].AccountId);
        Assert.Equal("acct_3", results[2].AccountId);
    }

    // ═══ RegisteredUsers ════════════════════════════════════════

    [Fact]
    public void GetRegisteredAccountIds_returns_distinct_accounts()
    {
        Db.RegisterUser("dev_1", "acct_1");
        Db.RegisterUser("dev_2", "acct_1");
        Db.RegisterUser("dev_3", "acct_2");

        var ids = Db.GetRegisteredAccountIds();
        Assert.Equal(2, ids.Count);
        Assert.Contains("acct_1", ids);
        Assert.Contains("acct_2", ids);
    }

    [Fact]
    public void IsAccountRegistered_uses_account_point_lookup()
    {
        Db.RegisterUser("dev_1", "acct_1");

        Assert.True(Db.IsAccountRegistered("acct_1"));
        Assert.False(Db.IsAccountRegistered("acct_missing"));
    }

    // ═══ ScoreHistory ═══════════════════════════════════════════

    [Fact]
    public void InsertScoreChange_and_GetScoreHistory_roundtrip()
    {
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 100_000, null, 42,
            accuracy: 95, isFullCombo: false, stars: 5, percentile: 99.5, season: 3,
            scoreAchievedAt: "2025-01-15T12:00:00Z");

        var history = Db.GetScoreHistory("acct_1");
        Assert.Single(history);

        var entry = history[0];
        Assert.Equal("song_1", entry.SongId);
        Assert.Equal("Solo_Guitar", entry.Instrument);
        Assert.Null(entry.OldScore);
        Assert.Equal(100_000, entry.NewScore);
        Assert.Equal(95, entry.Accuracy);
        Assert.False(entry.IsFullCombo);
        Assert.Equal(5, entry.Stars);
        Assert.Equal(3, entry.Season);
        Assert.Null(entry.SeasonRank);
        Assert.Null(entry.AllTimeRank);
    }

    [Fact]
    public void GetScoreHistory_respects_limit()
    {
        for (int i = 0; i < 10; i++)
            Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, i * 1000, null, i);

        var history = Db.GetScoreHistory("acct_1", limit: 3);
        Assert.Equal(3, history.Count);
    }

    [Fact]
    public void GetScoreHistory_filters_by_songId()
    {
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 100_000, null, 1);
        Db.InsertScoreChange("song_2", "Solo_Guitar", "acct_1", null, 90_000, null, 2);
        Db.InsertScoreChange("song_1", "Solo_Bass", "acct_1", null, 80_000, null, 3);

        var history = Db.GetScoreHistory("acct_1", songId: "song_1");
        Assert.Equal(2, history.Count);
        Assert.All(history, h => Assert.Equal("song_1", h.SongId));
    }

    [Fact]
    public void GetScoreHistory_songId_filter_returns_empty_when_no_match()
    {
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 100_000, null, 1);

        var history = Db.GetScoreHistory("acct_1", songId: "song_nonexistent");
        Assert.Empty(history);
    }

    [Fact]
    public void InsertScoreChange_roundtrips_SeasonRank_and_AllTimeRank()
    {
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 200_000, null, 50,
            accuracy: 90, isFullCombo: true, stars: 5, percentile: 98.0, season: 10,
            scoreAchievedAt: "2025-06-01T00:00:00Z",
            seasonRank: 742, allTimeRank: 9989);

        var history = Db.GetScoreHistory("acct_1");
        Assert.Single(history);

        var entry = history[0];
        Assert.Equal(742, entry.SeasonRank);
        Assert.Equal(9989, entry.AllTimeRank);
    }

    // ═══ InsertScoreChanges (batch) ═════════════════════════════

    [Fact]
    public void InsertScoreChanges_batch_inserts_multiple()
    {
        var changes = new List<ScoreChangeRecord>
        {
            new()
            {
                SongId = "song_1", Instrument = "Solo_Guitar", AccountId = "acct_1",
                OldScore = null, NewScore = 100_000, OldRank = null, NewRank = 1,
                Accuracy = 95, IsFullCombo = true, Stars = 5, Percentile = 99.0,
                Season = 10, ScoreAchievedAt = "2025-01-01T00:00:00Z", AllTimeRank = 1,
            },
            new()
            {
                SongId = "song_2", Instrument = "Solo_Bass", AccountId = "acct_2",
                OldScore = 50_000, NewScore = 80_000, OldRank = 100, NewRank = 50,
                Accuracy = 88, IsFullCombo = false, Stars = 4, Percentile = 85.0,
                Season = 10, ScoreAchievedAt = "2025-01-02T00:00:00Z", AllTimeRank = 50,
            },
        };

        var inserted = Db.InsertScoreChanges(changes);
        Assert.Equal(2, inserted);

        var history1 = Db.GetScoreHistory("acct_1");
        Assert.Single(history1);
        Assert.Equal(100_000, history1[0].NewScore);

        var history2 = Db.GetScoreHistory("acct_2");
        Assert.Single(history2);
        Assert.Equal(80_000, history2[0].NewScore);
        Assert.Equal(50_000, history2[0].OldScore);
    }

    [Fact]
    public void InsertScoreChanges_batch_empty_returns_zero()
    {
        var inserted = Db.InsertScoreChanges([]);
        Assert.Equal(0, inserted);
    }

    [Fact]
    public void InsertScoreChanges_batch_deduplicates_with_conflict()
    {
        // Insert initial record
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 100_000, null, 1,
            scoreAchievedAt: "2025-01-01T00:00:00Z", seasonRank: 5);

        // Batch-insert same key with allTimeRank — should merge via COALESCE
        var changes = new List<ScoreChangeRecord>
        {
            new()
            {
                SongId = "song_1", Instrument = "Solo_Guitar", AccountId = "acct_1",
                OldScore = null, NewScore = 100_000, OldRank = null, NewRank = 1,
                ScoreAchievedAt = "2025-01-01T00:00:00Z", AllTimeRank = 42,
            },
        };

        Db.InsertScoreChanges(changes);

        var history = Db.GetScoreHistory("acct_1");
        Assert.Single(history);
        Assert.Equal(5, history[0].SeasonRank);   // preserved from first insert
        Assert.Equal(42, history[0].AllTimeRank);  // merged from batch
    }

    [Theory]
    [InlineData(1)]
    [InlineData(21)]
    public void ScoreHistory_conflicts_only_enrich_null_season_and_difficulty(
        int batchSize)
    {
        const string timestamp = "2025-01-01T00:00:00Z";
        Db.InsertScoreChange(
            "song_target",
            "Solo_Guitar",
            "acct_target",
            null,
            100_000,
            null,
            1,
            scoreAchievedAt: timestamp);

        Db.InsertScoreChanges(BuildBatch(batchSize, season: 7, difficulty: 3));
        var enriched = Assert.Single(Db.GetScoreHistory("acct_target"));
        Assert.Equal(7, enriched.Season);
        Assert.Equal(3, enriched.Difficulty);

        Db.InsertScoreChanges(BuildBatch(batchSize, season: 8, difficulty: 4));
        var preserved = Assert.Single(Db.GetScoreHistory("acct_target"));
        Assert.Equal(7, preserved.Season);
        Assert.Equal(3, preserved.Difficulty);

        List<ScoreChangeRecord> BuildBatch(
            int count,
            int season,
            int difficulty)
        {
            var rows = new List<ScoreChangeRecord>
            {
                new()
                {
                    SongId = "song_target",
                    Instrument = "Solo_Guitar",
                    AccountId = "acct_target",
                    NewScore = 100_000,
                    NewRank = 1,
                    ScoreAchievedAt = timestamp,
                    Season = season,
                    Difficulty = difficulty,
                },
            };
            for (var index = 1; index < count; index++)
            {
                rows.Add(new ScoreChangeRecord
                {
                    SongId = $"song_filler_{season}_{index}",
                    Instrument = "Solo_Guitar",
                    AccountId = $"acct_filler_{season}_{index}",
                    NewScore = 100_000 + index,
                    NewRank = index + 1,
                    ScoreAchievedAt = timestamp,
                    Season = season,
                    Difficulty = difficulty,
                });
            }

            return rows;
        }
    }

    [Fact]
    public void InsertScoreChanges_large_batch_collapses_duplicate_source_keys()
    {
        var changes = new List<ScoreChangeRecord>();
        for (var i = 0; i < 21; i++)
        {
            changes.Add(new ScoreChangeRecord
            {
                SongId = $"song_{i}", Instrument = "Solo_Guitar", AccountId = "acct_seed",
                OldScore = null, NewScore = 100_000 + i, OldRank = null, NewRank = i + 1,
                ScoreAchievedAt = $"2025-01-{(i % 9) + 1:00}T00:00:00Z", AllTimeRank = i + 1,
            });
        }

        changes.Add(new ScoreChangeRecord
        {
            SongId = "song_dupe", Instrument = "Solo_Guitar", AccountId = "acct_1",
            OldScore = null, NewScore = 123_456, OldRank = null, NewRank = 77,
            ScoreAchievedAt = "2025-02-01T00:00:00Z", AllTimeRank = 77,
        });
        changes.Add(new ScoreChangeRecord
        {
            SongId = "song_dupe", Instrument = "Solo_Guitar", AccountId = "acct_1",
            OldScore = null, NewScore = 123_456, OldRank = null, NewRank = 402,
            ScoreAchievedAt = "2025-02-01T00:00:00Z", Season = 10, SeasonRank = 402,
        });

        Db.InsertScoreChanges(changes);

        var history = Db.GetScoreHistory("acct_1");
        Assert.Single(history);
        Assert.Equal(77, history[0].AllTimeRank);
        Assert.Equal(402, history[0].SeasonRank);
    }

    // ═══ BackfillStatus ═════════════════════════════════════════

    [Fact]
    public void Backfill_lifecycle_pending_to_complete()
    {
        Db.EnqueueBackfill("acct_1", 1000);
        var status = Db.GetBackfillStatus("acct_1");
        Assert.NotNull(status);
        Assert.Equal("pending", status.Status);
        Assert.Equal(1000, status.TotalSongsToCheck);

        Db.StartBackfill("acct_1");
        status = Db.GetBackfillStatus("acct_1");
        Assert.Equal("in_progress", status!.Status);
        Assert.NotNull(status.StartedAt);

        Db.UpdateBackfillProgress("acct_1", 500, 25);
        status = Db.GetBackfillStatus("acct_1");
        Assert.Equal(500, status!.SongsChecked);
        Assert.Equal(25, status.EntriesFound);

        Db.CompleteBackfill("acct_1");
        status = Db.GetBackfillStatus("acct_1");
        Assert.Equal("complete", status!.Status);
        Assert.NotNull(status.CompletedAt);
    }

    [Fact]
    public void Backfill_error_state()
    {
        Db.EnqueueBackfill("acct_1", 100);
        Db.StartBackfill("acct_1");
        Db.FailBackfill("acct_1", "API timeout");

        var status = Db.GetBackfillStatus("acct_1");
        Assert.Equal("error", status!.Status);
        Assert.Equal("API timeout", status.ErrorMessage);
    }

    [Fact]
    public void GetPendingBackfills_returns_pending_and_in_progress()
    {
        Db.EnqueueBackfill("acct_1", 100);
        Db.EnqueueBackfill("acct_2", 200);
        Db.StartBackfill("acct_1");
        Db.EnqueueBackfill("acct_3", 300);
        Db.StartBackfill("acct_3");
        Db.CompleteBackfill("acct_3");

        var pending = Db.GetPendingBackfills();
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, p => p.AccountId == "acct_1");
        Assert.Contains(pending, p => p.AccountId == "acct_2");
    }

    [Fact]
    public void DeferredBackfill_is_excluded_from_pending_until_started()
    {
        Db.EnqueueBackfill("acct_pending", 100);
        Db.DeferBackfill("acct_deferred", 200, "server_update_in_progress");

        var pending = Db.GetPendingBackfills();
        var deferred = Db.GetDeferredBackfills();

        Assert.Contains(pending, p => p.AccountId == "acct_pending");
        Assert.DoesNotContain(pending, p => p.AccountId == "acct_deferred");
        Assert.Contains(deferred, p => p.AccountId == "acct_deferred" && p.DeferredReason == "server_update_in_progress");

        Db.StartBackfill("acct_deferred");
        pending = Db.GetPendingBackfills();

        Assert.Contains(pending, p => p.AccountId == "acct_deferred");
    }

    [Fact]
    public void DeferredBackfills_include_interrupted_in_progress_for_resume()
    {
        Db.DeferBackfill("acct_resume", 200, "worker_backfill_queue");
        Db.StartBackfill("acct_resume");

        var deferred = Db.GetDeferredBackfills();

        Assert.Contains(deferred, p => p.AccountId == "acct_resume" && p.Status == "in_progress");
    }

    [Fact]
    public void CompleteBackfill_tracks_and_clears_rankings_pending()
    {
        Db.EnqueueBackfill("acct_1", 100);
        Db.StartBackfill("acct_1");
        Db.CompleteBackfill("acct_1", rankingsPending: true);
        Db.CompleteBackfill("acct_1");

        var status = Db.GetBackfillStatus("acct_1");
        Assert.True(status!.RankingsPending);

        Db.ClearBackfillRankingsPending(["acct_1"], DateTime.UtcNow.AddMinutes(1));

        status = Db.GetBackfillStatus("acct_1");
        Assert.False(status!.RankingsPending);
    }

    [Fact]
    public void RequeueBackfill_preserves_rankings_pending_until_publication_clears_it()
    {
        Db.EnqueueBackfill("acct_requeue", 100);
        Db.StartBackfill("acct_requeue");
        Db.CompleteBackfill("acct_requeue", rankingsPending: true);

        Db.EnqueueBackfill("acct_requeue", 200);
        Db.DeferBackfill("acct_requeue", 200, "test");

        Assert.True(Db.GetBackfillStatus("acct_requeue")!.RankingsPending);
    }

    [Fact]
    public void GetBackfillProjectionScopesCompletedBefore_returns_only_published_cut_accounts()
    {
        Db.EnqueueBackfill("acct_cut", 100);
        Db.StartBackfill("acct_cut");
        Db.CompleteBackfill("acct_cut", rankingsPending: true);

        using (var conn = DataSource.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO leaderboard_entries_overlay
                (song_id, instrument, account_id, score, source, first_seen_at,
                 last_updated_at, source_priority, overlay_reason)
                VALUES
                ('song_cut', 'Solo_Guitar', 'acct_cut', 123456, 'registered',
                 @now, @now, 100, 'test')
                """;
            cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
            cmd.ExecuteNonQuery();
        }

        var scopes = Db.GetBackfillProjectionScopesCompletedBefore(
            ["acct_cut"],
            DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(
            [new SoloCurrentProjectionScopeKey("song_cut", "Solo_Guitar")],
            scopes);
        Assert.Empty(Db.GetBackfillProjectionScopesCompletedBefore(
            ["acct_cut"],
            DateTime.UtcNow.AddMinutes(-1)));
        Assert.Empty(Db.GetBackfillProjectionScopesCompletedBefore(
            ["other_account"],
            DateTime.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public void ClearBackfillRankingsPending_preserves_work_completed_after_cutoff()
    {
        Db.EnqueueBackfill("acct_1", 100);
        Db.StartBackfill("acct_1");
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        Db.CompleteBackfill("acct_1", rankingsPending: true);

        Db.ClearBackfillRankingsPending(["acct_1"], cutoff);

        Assert.True(Db.GetBackfillStatus("acct_1")!.RankingsPending);
    }

    [Fact]
    public void BackfillProgress_tracks_checked_pairs()
    {
        Db.MarkBackfillSongChecked("acct_1", "song_1", "Solo_Guitar", true);
        Db.MarkBackfillSongChecked("acct_1", "song_2", "Solo_Guitar", false);

        var checkedPairs = Db.GetCheckedBackfillPairs("acct_1");
        Assert.Equal(2, checkedPairs.Count);
        Assert.Contains(("song_1", "Solo_Guitar"), checkedPairs);
        Assert.Contains(("song_2", "Solo_Guitar"), checkedPairs);
    }

    [Fact]
    public void BackfillSongProgress_reports_song_level_display_counts()
    {
        var instruments = GlobalLeaderboardScraper.AllInstruments;
        var totalPairs = instruments.Count * 3;
        Db.EnqueueBackfill("acct_1", totalPairs);
        Db.UpdateBackfillProgress("acct_1", instruments.Count + 2, 3);

        foreach (var instrument in instruments)
            Db.MarkBackfillSongChecked("acct_1", "song_1", instrument, entryFound: true);
        Db.MarkBackfillSongChecked("acct_1", "song_2", instruments[0], entryFound: false);

        var progress = Db.GetBackfillSongProgress("acct_1", instruments.Count + 2, totalPairs);

        Assert.NotNull(progress);
        Assert.Equal(1, progress.SongsChecked);
        Assert.Equal(3, progress.TotalSongs);
    }

    [Fact]
    public void EnqueueBackfill_does_not_reset_completed_for_same_catalog_size()
    {
        Db.EnqueueBackfill("acct_1", 100);
        Db.StartBackfill("acct_1");
        Db.CompleteBackfill("acct_1");

        // Re-enqueue should not overwrite 'complete' status unless the catalog grew.
        Db.EnqueueBackfill("acct_1", 100);
        var status = Db.GetBackfillStatus("acct_1");
        Assert.Equal("complete", status!.Status);
    }

    [Fact]
    public void EnqueueBackfill_reopens_completed_when_catalog_size_grows()
    {
        Db.EnqueueBackfill("acct_1", 100);
        Db.StartBackfill("acct_1");
        Db.UpdateBackfillProgress("acct_1", 100, 5);
        Db.CompleteBackfill("acct_1");

        Db.EnqueueBackfill("acct_1", 200);
        var status = Db.GetBackfillStatus("acct_1");

        Assert.Equal("pending", status!.Status);
        Assert.Equal(200, status.TotalSongsToCheck);
        Assert.Equal(0, status.SongsChecked);
        Assert.Equal(0, status.EntriesFound);
        Assert.Null(status.StartedAt);
        Assert.Null(status.CompletedAt);
        Assert.Null(status.LastResumedAt);
    }

    [Fact]
    public void DeferBackfill_reopens_completed_when_catalog_size_grows()
    {
        Db.EnqueueBackfill("acct_1", 100);
        Db.StartBackfill("acct_1");
        Db.UpdateBackfillProgress("acct_1", 100, 5);
        Db.CompleteBackfill("acct_1");

        Db.DeferBackfill("acct_1", 200, "worker_backfill_queue");
        var status = Db.GetBackfillStatus("acct_1");

        Assert.Equal("deferred", status!.Status);
        Assert.Equal(200, status.TotalSongsToCheck);
        Assert.Equal(0, status.SongsChecked);
        Assert.Equal(0, status.EntriesFound);
        Assert.Equal("worker_backfill_queue", status.DeferredReason);
        Assert.Null(status.StartedAt);
        Assert.Null(status.CompletedAt);
        Assert.Null(status.LastResumedAt);
    }

    // ═══ HistoryReconStatus ═════════════════════════════════════

    [Fact]
    public void HistoryRecon_lifecycle_pending_to_complete()
    {
        Db.EnqueueBackfill("acct_1", 500);
        Db.StartBackfill("acct_1");
        Db.CompleteBackfill("acct_1");
        Db.EnqueueHistoryRecon("acct_1", 500);
        var status = Db.GetHistoryReconStatus("acct_1");
        Assert.NotNull(status);
        Assert.Equal("pending", status.Status);
        Assert.Equal(500, status.TotalSongsToProcess);

        Db.StartHistoryRecon("acct_1");
        status = Db.GetHistoryReconStatus("acct_1");
        Assert.Equal("in_progress", status!.Status);

        Db.UpdateHistoryReconProgress("acct_1", 250, 800, 50);
        status = Db.GetHistoryReconStatus("acct_1");
        Assert.Equal(250, status!.SongsProcessed);
        Assert.Equal(800, status.SeasonsQueried);
        Assert.Equal(50, status.HistoryEntriesFound);

        Db.CompleteHistoryRecon("acct_1");
        status = Db.GetHistoryReconStatus("acct_1");
        Assert.Equal("complete", status!.Status);
        Assert.True(
            Db.GetBackfillStatus("acct_1")
                ?.RankingsPending);
    }

    [Fact]
    public void HistoryRecon_error_state()
    {
        Db.EnqueueHistoryRecon("acct_1", 100);
        Db.StartHistoryRecon("acct_1");
        Db.FailHistoryRecon("acct_1", "Network error");

        var status = Db.GetHistoryReconStatus("acct_1");
        Assert.Equal("error", status!.Status);
        Assert.Equal("Network error", status.ErrorMessage);
    }

    [Fact]
    public void HistoryReconProgress_tracks_processed_pairs()
    {
        Db.EnqueueHistoryRecon("acct_1", 2);
        Db.MarkHistoryReconSongProcessed("acct_1", "song_1", "Solo_Guitar");
        Db.MarkHistoryReconSongProcessed("acct_1", "song_2", "Solo_Bass");

        var processed = Db.GetProcessedHistoryReconPairs("acct_1");
        Assert.Equal(2, processed.Count);
        Assert.Contains(("song_1", "Solo_Guitar"), processed);
        Assert.Contains(("song_2", "Solo_Bass"), processed);
    }

    [Fact]
    public void GetPendingHistoryRecons_returns_pending_and_in_progress()
    {
        Db.EnqueueHistoryRecon("acct_1", 100);
        Db.EnqueueHistoryRecon("acct_2", 200);
        Db.EnqueueHistoryRecon("acct_3", 300);
        Db.StartHistoryRecon("acct_3");
        Db.CompleteHistoryRecon("acct_3");

        var pending = Db.GetPendingHistoryRecons();
        Assert.Equal(2, pending.Count);
    }

    [Fact]
    public void HistoryRecon_window_fingerprint_change_resets_status_and_progress()
    {
        Db.EnqueueHistoryRecon("acct-versioned", 1, 1, "fingerprint-a");
        Db.StartHistoryRecon("acct-versioned");
        Db.MarkHistoryReconSongProcessed(
            "acct-versioned",
            "song-a",
            "Solo_Guitar",
            1,
            "fingerprint-a");
        Db.CompleteHistoryRecon("acct-versioned", 1, "fingerprint-a");

        Db.EnqueueHistoryRecon("acct-versioned", 1, 1, "fingerprint-b");

        var status = Db.GetHistoryReconStatus("acct-versioned");
        Assert.Equal("pending", status?.Status);
        Assert.Equal(1, status?.ReconstructionVersion);
        Assert.Equal("fingerprint-b", status?.WindowFingerprint);
        Assert.Empty(Db.GetProcessedHistoryReconPairs(
            "acct-versioned",
            1,
            "fingerprint-b"));
    }

    [Fact]
    public async Task HistoryRecon_stale_identity_writes_cannot_overwrite_active_fingerprint()
    {
        var staleRevision = Db.AdmitHistoryRecon(
            "acct-stale",
            1,
            1,
            "fingerprint-1");
        Db.StartHistoryRecon(
            "acct-stale",
            1,
            "fingerprint-1",
            staleRevision);
        var activeRevision = Db.AdmitHistoryRecon(
            "acct-stale",
            1,
            1,
            "fingerprint-2");
        Assert.True(activeRevision > staleRevision);

        await Task.WhenAll(
            Task.Run(() => Db.MarkHistoryReconSongProcessed(
                "acct-stale",
                "song-stale",
                "Solo_Guitar",
                1,
                "fingerprint-1",
                staleRevision)),
            Task.Run(() => Db.UpdateHistoryReconProgress(
                "acct-stale",
                99,
                99,
                99,
                1,
                "fingerprint-1",
                staleRevision)),
            Task.Run(() => Db.CompleteHistoryRecon(
                "acct-stale",
                1,
                "fingerprint-1",
                staleRevision)),
            Task.Run(() => Db.FailHistoryRecon(
                "acct-stale",
                "stale failure",
                1,
                "fingerprint-1",
                staleRevision)));

        var status = Db.GetHistoryReconStatus("acct-stale");
        Assert.Equal("pending", status?.Status);
        Assert.Equal("fingerprint-2", status?.WindowFingerprint);
        Assert.Equal(activeRevision, status?.AdmissionRevision);
        Assert.Equal(0, status?.SongsProcessed);
        Assert.Equal(0, status?.SeasonsQueried);
        Assert.Equal(0, status?.HistoryEntriesFound);
        Assert.Empty(Db.GetProcessedHistoryReconPairs(
            "acct-stale",
            1,
            "fingerprint-1",
            staleRevision));
        Assert.Empty(Db.GetProcessedHistoryReconPairs(
            "acct-stale",
            1,
            "fingerprint-2",
            activeRevision));
    }

    [Fact]
    public void HistoryRecon_same_fingerprint_readmission_advances_fence_and_preserves_progress()
    {
        var firstRevision = Db.AdmitHistoryRecon(
            "acct-readmit",
            1,
            1,
            "fingerprint");
        Db.MarkHistoryReconSongProcessed(
            "acct-readmit",
            "song-a",
            "Solo_Guitar",
            1,
            "fingerprint",
            firstRevision);

        var secondRevision = Db.AdmitHistoryRecon(
            "acct-readmit",
            1,
            1,
            "fingerprint");

        Assert.True(secondRevision > firstRevision);
        Assert.Contains(
            ("song-a", "Solo_Guitar"),
            Db.GetProcessedHistoryReconPairs(
                "acct-readmit",
                1,
                "fingerprint",
                secondRevision));
        Assert.Empty(Db.GetProcessedHistoryReconPairs(
            "acct-readmit",
            1,
            "fingerprint",
            firstRevision));
    }

    // ═══ SeasonWindows ══════════════════════════════════════════

    [Fact]
    public void UpsertSeasonWindow_and_GetSeasonWindows_roundtrip()
    {
        Db.UpsertSeasonWindow(1, "evt_1", "season_1");
        Db.UpsertSeasonWindow(2, "evt_2", "season_2");

        var windows = Db.GetSeasonWindows();
        Assert.Equal(2, windows.Count);
        Assert.Equal(1, windows[0].SeasonNumber);
        Assert.Equal("season_1", windows[0].WindowId);
        Assert.Equal(2, windows[1].SeasonNumber);
    }

    [Fact]
    public void UpsertSeasonWindow_updates_existing()
    {
        Db.UpsertSeasonWindow(1, "evt_1", "season_1");
        Db.UpsertSeasonWindow(1, "evt_1_updated", "season_1_new");

        var windows = Db.GetSeasonWindows();
        var window = windows.First(w => w.SeasonNumber == 1);
        Assert.Equal("evt_1_updated", window.EventId);
        Assert.Equal("season_1_new", window.WindowId);
    }

    [Fact]
    public void UpsertSeasonWindow_does_not_let_probe_override_event_api()
    {
        Db.UpsertSeasonWindow(
            15,
            "festival-season-15",
            "season_15_competitive",
            "event_api");
        Db.UpsertSeasonWindow(
            15,
            "season015_probe_song",
            "season015",
            "probe");

        var window = Db.GetSeasonWindow(15);

        Assert.NotNull(window);
        Assert.Equal("season_15_competitive", window!.WindowId);
        Assert.Equal("event_api", window.SourceKind);
    }

    // ═══ SongFirstSeenSeason ════════════════════════════════════

    [Fact]
    public void UpsertFirstSeenSeason_roundtrip()
    {
        Db.UpsertFirstSeenSeason("song_1", 5, 4, 5, "found_at_season_5", 2);
        var dict = Db.GetAllFirstSeenSeasons();
        Assert.Equal(5, dict["song_1"].FirstSeenSeason);
        Assert.Equal(2, dict["song_1"].CalculationVersion);
    }

    [Fact]
    public void GetSongIdsWithFirstSeenVersion_returns_matching_set()
    {
        Db.UpsertFirstSeenSeason("song_1", 5, 4, 5, null, 2);
        Db.UpsertFirstSeenSeason("song_2", null, 3, 3, "not_found", 1);

        var v2Set = Db.GetSongIdsWithFirstSeenVersion(2);
        Assert.Single(v2Set);
        Assert.Contains("song_1", v2Set);

        var v1Set = Db.GetSongIdsWithFirstSeenVersion(1);
        Assert.Single(v1Set);
        Assert.Contains("song_2", v1Set);
    }

    [Fact]
    public void GetAllFirstSeenSeasons_returns_dictionary()
    {
        Db.UpsertFirstSeenSeason("song_1", 5, 4, 5, null, 2);
        Db.UpsertFirstSeenSeason("song_2", null, 3, 3, null, 1);

        var dict = Db.GetAllFirstSeenSeasons();
        Assert.Equal(2, dict.Count);
        Assert.Equal(5, dict["song_1"].FirstSeenSeason);
        Assert.Equal(5, dict["song_1"].EstimatedSeason);
        Assert.Equal(2, dict["song_1"].CalculationVersion);
        Assert.Null(dict["song_2"].FirstSeenSeason);
        Assert.Equal(3, dict["song_2"].EstimatedSeason);
        Assert.Equal(1, dict["song_2"].CalculationVersion);
    }

    [Fact]
    public void UpsertFirstSeenSeason_updates_existing()
    {
        Db.UpsertFirstSeenSeason("song_1", 5, 4, 5, "initial", 1);
        Db.UpsertFirstSeenSeason("song_1", 3, 2, 3, "updated", 2);

        var dict = Db.GetAllFirstSeenSeasons();
        Assert.Equal(3, dict["song_1"].FirstSeenSeason);
        Assert.Equal(3, dict["song_1"].EstimatedSeason);
        Assert.Equal(2, dict["song_1"].CalculationVersion);
    }

    [Fact]
    public void UpsertFirstSeenSeason_nullable_firstSeen()
    {
        Db.UpsertFirstSeenSeason("song_1", null, 3, 3, null, 2);

        var dict = Db.GetAllFirstSeenSeasons();
        Assert.Null(dict["song_1"].FirstSeenSeason);
        Assert.Equal(3, dict["song_1"].EstimatedSeason);
    }

    // ═══ RegisterUser / UnregisterUser ══════════════════════════

    [Fact]
    public void RegisterUser_returns_true_for_new()
    {
        var isNew = Db.RegisterUser("dev1", "acct1");
        Assert.True(isNew);
    }

    [Fact]
    public void RegisterUser_returns_false_for_duplicate()
    {
        Db.RegisterUser("dev1", "acct1");
        var isNew = Db.RegisterUser("dev1", "acct1");
        Assert.False(isNew);
    }

    [Fact]
    public void UnregisterUser_returns_true_when_removed()
    {
        Db.RegisterUser("dev1", "acct1");
        var removed = Db.UnregisterUser("dev1", "acct1");
        Assert.True(removed);
    }

    [Fact]
    public void UnregisterUser_returns_false_when_not_found()
    {
        var removed = Db.UnregisterUser("dev1", "acct1");
        Assert.False(removed);
    }

    [Fact]
    public void UnregisterUser_last_device_cascades_to_full_cleanup()
    {
        Db.RegisterUser("dev1", "acct1");
        Db.UpsertPlayerStats(new PlayerStatsDto
        {
            AccountId = "acct1", Instrument = "Solo_Guitar", SongsPlayed = 10,
        });
        Db.EnqueueBackfill("acct1", 50);
        Db.EnqueueHistoryRecon("acct1", 50);

        var removed = Db.UnregisterUser("dev1", "acct1");
        Assert.True(removed);

        // All per-account data should be cleaned up
        Assert.Empty(Db.GetPlayerStats("acct1"));
        Assert.Null(Db.GetBackfillStatus("acct1"));
        Assert.Null(Db.GetHistoryReconStatus("acct1"));
    }

    [Fact]
    public void UnregisterUser_not_last_device_does_not_cascade()
    {
        Db.RegisterUser("dev1", "acct1");
        Db.RegisterUser("dev2", "acct1");
        Db.UpsertPlayerStats(new PlayerStatsDto
        {
            AccountId = "acct1", Instrument = "Solo_Guitar", SongsPlayed = 10,
        });
        Db.EnqueueBackfill("acct1", 50);

        var removed = Db.UnregisterUser("dev1", "acct1");
        Assert.True(removed);

        // Per-account data should still exist (dev2 still registered)
        Assert.Single(Db.GetPlayerStats("acct1"));
        Assert.NotNull(Db.GetBackfillStatus("acct1"));
        Assert.Contains("acct1", Db.GetRegisteredAccountIds());
    }

    [Fact]
    public void TouchWebRegistrationActivity_refreshes_stale_web_registration()
    {
        Db.RegisterUser("web-tracker", "acct1");
        SetWebRegistrationActivity("acct1", DateTime.UtcNow.AddHours(-8));

        Db.TouchWebRegistrationActivity("acct1");

        var pruned = Db.PruneStaleWebRegistrations(DateTime.UtcNow.AddHours(-4));
        Assert.Equal(0, pruned);
        Assert.Contains("acct1", Db.GetRegisteredAccountIds());
    }

    [Fact]
    public void PruneStaleWebRegistrations_removes_only_web_tracker_rows_non_destructively()
    {
        Db.RegisterUser("web-tracker", "acct1");
        Db.UpsertPlayerStats(new PlayerStatsDto
        {
            AccountId = "acct1", Instrument = "Solo_Guitar", SongsPlayed = 10,
        });
        SetWebRegistrationActivity("acct1", DateTime.UtcNow.AddHours(-8));

        var pruned = Db.PruneStaleWebRegistrations(DateTime.UtcNow.AddHours(-4));

        Assert.Equal(1, pruned);
        Assert.DoesNotContain("acct1", Db.GetRegisteredAccountIds());
        Assert.Single(Db.GetPlayerStats("acct1"));
    }

    [Fact]
    public void PruneStaleWebRegistrations_preserves_non_web_registrations_for_same_account()
    {
        Db.RegisterUser("web-tracker", "acct1");
        Db.RegisterUser("mobile-device", "acct1");
        SetWebRegistrationActivity("acct1", DateTime.UtcNow.AddHours(-8));

        var pruned = Db.PruneStaleWebRegistrations(DateTime.UtcNow.AddHours(-4));

        Assert.Equal(1, pruned);
        Assert.Contains("acct1", Db.GetRegisteredAccountIds());
    }

    [Fact]
    public void RegisterSelectedBandActivity_registers_band_and_member_accounts()
    {
        InsertBandProjection("Band_Duets", "acct1:acct2", ["acct1", "acct2"]);

        var result = Db.RegisterSelectedBandActivity("Band_Duets", "acct1:acct2");

        Assert.True(result.Registered);
        Assert.Equal(["acct1", "acct2"], result.MemberAccountIds);
        Assert.Contains("acct1", Db.GetRegisteredAccountIds());
        Assert.Contains("acct2", Db.GetRegisteredAccountIds());
        var registeredBand = Assert.Single(Db.GetRegisteredBands());
        Assert.Equal("web-band-tracker", registeredBand.SourceId);
        Assert.Equal("Band_Duets", registeredBand.BandType);
        Assert.Equal("acct1:acct2", registeredBand.TeamKey);
        Assert.Equal(result.BandId, registeredBand.BandId);
        Assert.NotNull(GetRegistrationActivity("acct1", "web-tracker"));
        Assert.NotNull(GetRegistrationActivity("acct2", "web-tracker"));
        Assert.NotNull(GetRegistrationActivity("acct1", "web-band-tracker"));
        Assert.NotNull(GetRegistrationActivity("acct2", "web-band-tracker"));

        var acct1Backfill = Db.GetBackfillStatus("acct1");
        var acct2Backfill = Db.GetBackfillStatus("acct2");
        Assert.Equal("pending", acct1Backfill?.Status);
        Assert.Equal(0, acct1Backfill?.TotalSongsToCheck);
        Assert.Equal("pending", acct2Backfill?.Status);
        Assert.Equal(0, acct2Backfill?.TotalSongsToCheck);

        var processingStatus = Db.GetRegisteredBandProcessingStatus("web-band-tracker", "Band_Duets", "acct1:acct2");
        Assert.Equal("pending", processingStatus?.Status);
    }

    [Fact]
    public void RegisterSelectedBandActivity_refreshes_independent_member_activity_on_reselect()
    {
        InsertBandProjection("Band_Duets", "acct1:acct2", ["acct1", "acct2"]);
        Db.RegisterSelectedBandActivity("Band_Duets", "acct1:acct2");
        var staleAt = DateTime.UtcNow.AddHours(-8);
        SetRegistrationActivity("acct1", "web-tracker", staleAt);
        SetRegistrationActivity("acct2", "web-tracker", staleAt);
        SetRegistrationActivity("acct1", "web-band-tracker", staleAt);
        SetRegistrationActivity("acct2", "web-band-tracker", staleAt);

        var result = Db.RegisterSelectedBandActivity("Band_Duets", "acct1:acct2");

        Assert.True(result.Registered);
        Assert.True(GetRegistrationActivity("acct1", "web-tracker").GetValueOrDefault() > staleAt);
        Assert.True(GetRegistrationActivity("acct2", "web-tracker").GetValueOrDefault() > staleAt);
        Assert.True(GetRegistrationActivity("acct1", "web-band-tracker").GetValueOrDefault() > staleAt);
        Assert.True(GetRegistrationActivity("acct2", "web-band-tracker").GetValueOrDefault() > staleAt);
    }

    [Fact]
    public void RegisterKnownBandsForAccountActivity_registers_known_player_bands_without_member_backfills()
    {
        InsertBandProjection("Band_Duets", "acct1:acct2", ["acct1", "acct2"]);
        Db.RegisterUser("web-tracker", "acct1");

        var registered = Db.RegisterKnownBandsForAccountActivity("acct1");

        Assert.Equal(1, registered);
        var registeredBand = Assert.Single(Db.GetRegisteredBands());
        Assert.Equal("web-band-tracker", registeredBand.SourceId);
        Assert.Equal("Band_Duets", registeredBand.BandType);
        Assert.Equal("acct1:acct2", registeredBand.TeamKey);
        Assert.Contains("acct1", Db.GetRegisteredAccountIds());
        Assert.DoesNotContain("acct2", Db.GetRegisteredAccountIds());

        var processingStatus = Db.GetRegisteredBandProcessingStatus("web-band-tracker", "Band_Duets", "acct1:acct2");
        Assert.Equal("pending", processingStatus?.Status);
        Assert.Null(Db.GetBackfillStatus("acct2"));
    }

    [Fact]
    public void RegisterKnownBandsForAccountActivities_registers_distinct_bands_for_all_requested_accounts()
    {
        InsertBandProjection("Band_Duets", "acct1:acct2", ["acct1", "acct2"]);
        InsertBandProjection("Band_Trios", "acct2:acct3:acct4", ["acct2", "acct3", "acct4"]);

        var registered = Db.RegisterKnownBandsForAccountActivities(
            [" acct1 ", "acct3", "ACCT1", ""]);

        Assert.Equal(2, registered);
        var registeredBands = Db.GetRegisteredBands();
        Assert.Equal(2, registeredBands.Count);
        Assert.Contains(
            registeredBands,
            band => band.BandType == "Band_Duets"
                    && band.TeamKey == "acct1:acct2");
        Assert.Contains(
            registeredBands,
            band => band.BandType == "Band_Trios"
                    && band.TeamKey == "acct2:acct3:acct4");
        Assert.Null(Db.GetBackfillStatus("acct2"));
        Assert.Null(Db.GetBackfillStatus("acct4"));
    }

    [Fact]
    public void RegisterDiscoveredBandActivity_registers_exact_band_without_member_backfills()
    {
        Db.RegisterUser("web-tracker", "acct1");

        Db.RegisterDiscoveredBandActivity("Band_Quad", "acct1:acct2:acct3:acct4", ["acct1", "acct2", "acct3", "acct4"]);

        var registeredBand = Assert.Single(Db.GetRegisteredBands());
        Assert.Equal("web-band-tracker", registeredBand.SourceId);
        Assert.Equal("Band_Quad", registeredBand.BandType);
        Assert.Equal("acct1:acct2:acct3:acct4", registeredBand.TeamKey);
        Assert.Contains("acct1", Db.GetRegisteredAccountIds());
        Assert.DoesNotContain("acct2", Db.GetRegisteredAccountIds());

        var processingStatus = Db.GetRegisteredBandProcessingStatus("web-band-tracker", "Band_Quad", "acct1:acct2:acct3:acct4");
        Assert.Equal("pending", processingStatus?.Status);
        Assert.Null(Db.GetBackfillStatus("acct2"));
    }

    [Fact]
    public void RegisteredPlayerBandDiscoveryProgress_tracks_checked_lookup()
    {
        Db.MarkRegisteredPlayerBandDiscoveryChecked("acct1", "song-a", "Band_Duets", "alltime", 0, true);
        Db.MarkRegisteredPlayerBandDiscoveryChecked("acct1", "song-a", "Band_Trios", "season", 14, false);

        var progress = Db.GetCheckedRegisteredPlayerBandDiscoveryLookups("acct1");

        Assert.Equal(2, progress.Count);
        Assert.Contains(progress, row => row.SongId == "song-a" && row.BandType == "Band_Duets" && row.Scope == "alltime" && row.Season == 0 && row.WindowId == "alltime" && row.EntryFound);
        Assert.Contains(progress, row => row.SongId == "song-a" && row.BandType == "Band_Trios" && row.Scope == "season" && row.Season == 14 && row.WindowId == "season014" && !row.EntryFound);
        Assert.Empty(Db.GetCheckedRegisteredPlayerBandDiscoveryLookups("acct2"));
    }

    [Fact]
    public void RegisteredBandProcessingStatus_tracks_progress_and_completion()
    {
        InsertBandProjection("Band_Duets", "acct1:acct2", ["acct1", "acct2"]);
        Db.RegisterSelectedBandActivity("Band_Duets", "acct1:acct2");

        Db.EnsureRegisteredBandProcessingStatus("web-band-tracker", "Band_Duets", "acct1:acct2", 2);
        Db.StartRegisteredBandProcessing("web-band-tracker", "Band_Duets", "acct1:acct2");
        Db.MarkRegisteredBandLookupChecked("web-band-tracker", "Band_Duets", "acct1:acct2", "song-a", "alltime", 0, true);
        Db.UpdateRegisteredBandProcessingProgress("web-band-tracker", "Band_Duets", "acct1:acct2", 1, 1, 2);

        var progress = Db.GetCheckedRegisteredBandLookups("web-band-tracker", "Band_Duets", "acct1:acct2");
        Assert.Single(progress);
        Assert.True(progress[0].EntryFound);
        Assert.Equal("alltime", progress[0].WindowId);

        var inProgress = Db.GetRegisteredBandProcessingStatus("web-band-tracker", "Band_Duets", "acct1:acct2");
        Assert.Equal("in_progress", inProgress?.Status);
        Assert.Equal(1, inProgress?.LookupsChecked);
        Assert.Equal(2, inProgress?.TotalLookupsToCheck);

        Db.MarkRegisteredBandLookupChecked("web-band-tracker", "Band_Duets", "acct1:acct2", "song-a", "season", 14, false);
        Db.CompleteRegisteredBandProcessing("web-band-tracker", "Band_Duets", "acct1:acct2", 2, 1);

        var complete = Db.GetRegisteredBandProcessingStatus("web-band-tracker", "Band_Duets", "acct1:acct2");
        Assert.Equal("complete", complete?.Status);
        Assert.Equal(2, complete?.LookupsChecked);
        Assert.Equal(1, complete?.EntriesFound);
        Assert.NotNull(complete?.CompletedAt);
    }

    [Fact]
    public void RegisterSelectedBandActivity_does_not_reset_completed_member_backfill()
    {
        InsertBandProjection("Band_Duets", "acct1:acct2", ["acct1", "acct2"]);
        Db.EnqueueBackfill("acct1", 50);
        Db.StartBackfill("acct1");
        Db.CompleteBackfill("acct1");

        var result = Db.RegisterSelectedBandActivity("Band_Duets", "acct1:acct2");

        Assert.True(result.Registered);
        Assert.Equal("complete", Db.GetBackfillStatus("acct1")?.Status);
        Assert.Equal("pending", Db.GetBackfillStatus("acct2")?.Status);
    }

    [Fact]
    public void RegisterSelectedBandActivity_rejects_mismatched_band_id()
    {
        InsertBandProjection("Band_Duets", "acct1:acct2", ["acct1", "acct2"]);

        var result = Db.RegisterSelectedBandActivity("Band_Duets", "acct1:acct2", "not-the-band-id");

        Assert.False(result.Registered);
        Assert.Empty(result.MemberAccountIds);
        Assert.Empty(Db.GetRegisteredBands());
        Assert.Empty(Db.GetRegisteredAccountIds());
        Assert.Null(Db.GetRegisteredBandProcessingStatus("web-band-tracker", "Band_Duets", "acct1:acct2"));
    }

    [Fact]
    public void PruneStaleWebRegistrations_removes_stale_band_profile_rows()
    {
        InsertBandProjection("Band_Duets", "acct1:acct2", ["acct1", "acct2"]);
        Db.RegisterSelectedBandActivity("Band_Duets", "acct1:acct2");
        var staleAt = DateTime.UtcNow.AddHours(-8);
        SetRegistrationActivity("acct1", "web-band-tracker", staleAt);
        SetRegistrationActivity("acct2", "web-band-tracker", staleAt);
        SetRegisteredBandActivity("Band_Duets", "acct1:acct2", staleAt);

        var pruned = Db.PruneStaleWebRegistrations(DateTime.UtcNow.AddHours(-4));

        Assert.Equal(3, pruned);
        Assert.Empty(Db.GetRegisteredBands());
        Assert.Contains("acct1", Db.GetRegisteredAccountIds());
        Assert.Contains("acct2", Db.GetRegisteredAccountIds());
    }

    private void SetWebRegistrationActivity(string accountId, DateTime lastActivityAt)
        => SetRegistrationActivity(accountId, "web-tracker", lastActivityAt);

    private void SetRegistrationActivity(string accountId, string deviceId, DateTime lastActivityAt)
    {
        using var conn = DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE registered_users SET last_activity_at = @lastActivityAt, registered_at = @registeredAt WHERE device_id = @deviceId AND account_id = @accountId";
        cmd.Parameters.AddWithValue("deviceId", deviceId);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("lastActivityAt", lastActivityAt);
        cmd.Parameters.AddWithValue("registeredAt", lastActivityAt);
        cmd.ExecuteNonQuery();
    }

    private DateTime? GetRegistrationActivity(string accountId, string deviceId)
    {
        using var conn = DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_activity_at FROM registered_users WHERE device_id = @deviceId AND account_id = @accountId";
        cmd.Parameters.AddWithValue("deviceId", deviceId);
        cmd.Parameters.AddWithValue("accountId", accountId);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : (DateTime)result;
    }

    private void SetRegisteredBandActivity(string bandType, string teamKey, DateTime lastActivityAt)
    {
        using var conn = DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE registered_bands SET last_activity_at = @lastActivityAt, registered_at = @registeredAt WHERE source_id = @sourceId AND band_type = @bandType AND team_key = @teamKey";
        cmd.Parameters.AddWithValue("sourceId", "web-band-tracker");
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("lastActivityAt", lastActivityAt);
        cmd.Parameters.AddWithValue("registeredAt", lastActivityAt);
        cmd.ExecuteNonQuery();
    }

    private void InsertBandProjection(string bandType, string teamKey, string[] memberAccountIds)
    {
        using var conn = DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO band_search_team_projection (band_type, team_key, band_id, appearance_count, member_account_ids, updated_at)
            VALUES (@bandType, @teamKey, @bandId, @appearanceCount, @memberAccountIds, @updatedAt);

            INSERT INTO band_search_member_projection (
                account_id, band_type, team_key, band_id, appearance_count,
                team_appearance_count, updated_at)
            SELECT member_account_id, @bandType, @teamKey, @bandId,
                   @appearanceCount, @appearanceCount, @updatedAt
            FROM unnest(@memberAccountIds::text[]) AS member(member_account_id);
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("bandId", $"test-{bandType}-{teamKey}");
        cmd.Parameters.AddWithValue("appearanceCount", 1);
        cmd.Parameters.Add("memberAccountIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = memberAccountIds;
        cmd.Parameters.AddWithValue("updatedAt", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    // ═══ GetAllFirstSeenSeasons ═════════════════════════════════

    [Fact]
    public void GetAllFirstSeenSeasons_returns_all_entries()
    {
        Db.UpsertFirstSeenSeason("song_a", 3, 2, 3, "found", 2);
        Db.UpsertFirstSeenSeason("song_b", null, null, 5, "estimated", 2);

        var all = Db.GetAllFirstSeenSeasons();
        Assert.Equal(2, all.Count);
        Assert.Equal(3, all["song_a"].FirstSeenSeason);
        Assert.Null(all["song_b"].FirstSeenSeason);
        Assert.Equal(5, all["song_b"].EstimatedSeason);
    }

    // ═══ InsertScoreChange with full params ═════════════════════

    [Fact]
    public void InsertScoreChange_with_all_optional_params()
    {
        Db.InsertScoreChange("song1", "Solo_Guitar", "acct1",
            oldScore: 50000, newScore: 100000, oldRank: 100, newRank: 50,
            accuracy: 95, isFullCombo: true, stars: 5, percentile: 99.5,
            season: 3, scoreAchievedAt: "2025-01-15T12:00:00Z",
            seasonRank: 10, allTimeRank: 25);

        var history = Db.GetScoreHistory("acct1", limit: 10);
        Assert.Single(history);
        var entry = history[0];
        Assert.Equal(50000, entry.OldScore);
        Assert.Equal(100000, entry.NewScore);
        Assert.Equal(95, entry.Accuracy);
        Assert.True(entry.IsFullCombo);
        Assert.Equal(5, entry.Stars);
        Assert.Equal(99.5, entry.Percentile);
        Assert.Equal(3, entry.Season);
        Assert.Equal(10, entry.SeasonRank);
        Assert.Equal(25, entry.AllTimeRank);
    }

    // ═══ GetDisplayName ═════════════════════════════════════════

    [Fact]
    public void GetDisplayName_returns_resolved_name()
    {
        Db.InsertAccountNames([("acct_dn", "DisplayUser")]);
        Assert.Equal("DisplayUser", Db.GetDisplayName("acct_dn"));
    }

    [Fact]
    public void GetDisplayName_returns_null_for_unknown()
    {
        Assert.Null(Db.GetDisplayName("nobody"));
    }

    // ═══ LeaderboardPopulation ══════════════════════════════════

    [Fact]
    public void UpsertLeaderboardPopulation_inserts_and_queries()
    {
        var items = new List<(string, string, long)>
        {
            ("song1", "Solo_Guitar", 100_000),
            ("song2", "Solo_Drums", 50_000),
        };

        Db.UpsertLeaderboardPopulation(items);

        Assert.Equal(100_000, Db.GetLeaderboardPopulation("song1", "Solo_Guitar"));
        Assert.Equal(50_000, Db.GetLeaderboardPopulation("song2", "Solo_Drums"));
    }

    [Fact]
    public void GetLeaderboardPopulation_returns_minus1_when_not_found()
    {
        Assert.Equal(-1, Db.GetLeaderboardPopulation("missing", "Solo_Guitar"));
    }

    [Fact]
    public void UpsertLeaderboardPopulation_updates_existing()
    {
        Db.UpsertLeaderboardPopulation([("song1", "Solo_Guitar", 100_000)]);
        Db.UpsertLeaderboardPopulation([("song1", "Solo_Guitar", 200_000)]);

        Assert.Equal(200_000, Db.GetLeaderboardPopulation("song1", "Solo_Guitar"));
    }

    [Fact]
    public void GetAllLeaderboardPopulation_returns_all_entries()
    {
        var items = new List<(string, string, long)>
        {
            ("songA", "Solo_Guitar", 10),
            ("songA", "Solo_Bass", 20),
            ("songB", "Solo_Guitar", 30),
        };

        Db.UpsertLeaderboardPopulation(items);
        var all = Db.GetAllLeaderboardPopulation();

        Assert.Equal(3, all.Count);
        Assert.Equal(10, all[("songA", "Solo_Guitar")]);
        Assert.Equal(20, all[("songA", "Solo_Bass")]);
        Assert.Equal(30, all[("songB", "Solo_Guitar")]);
    }

    [Fact]
    public void UpsertLeaderboardPopulation_empty_list_no_op()
    {
        Db.UpsertLeaderboardPopulation([]); // should not throw
        var all = Db.GetAllLeaderboardPopulation();
        Assert.Empty(all);
    }

    // ═══ RaiseLeaderboardPopulationFloor ════════════════════════

    [Fact]
    public void RaisePopulationFloor_inserts_when_no_existing_data()
    {
        Db.RaiseLeaderboardPopulationFloor("song1", "Solo_Guitar", 150_000);
        Assert.Equal(150_000, Db.GetLeaderboardPopulation("song1", "Solo_Guitar"));
    }

    [Fact]
    public void RaisePopulationFloor_raises_when_higher()
    {
        Db.UpsertLeaderboardPopulation([("song1", "Solo_Guitar", 100_000)]);
        Db.RaiseLeaderboardPopulationFloor("song1", "Solo_Guitar", 200_000);
        Assert.Equal(200_000, Db.GetLeaderboardPopulation("song1", "Solo_Guitar"));
    }

    [Fact]
    public void RaisePopulationFloor_does_not_lower_existing()
    {
        Db.UpsertLeaderboardPopulation([("song1", "Solo_Guitar", 300_000)]);
        Db.RaiseLeaderboardPopulationFloor("song1", "Solo_Guitar", 100_000);
        Assert.Equal(300_000, Db.GetLeaderboardPopulation("song1", "Solo_Guitar"));
    }

    [Fact]
    public void RaisePopulationFloor_ignores_zero_and_negative()
    {
        Db.RaiseLeaderboardPopulationFloor("song1", "Solo_Guitar", 0);
        Assert.Equal(-1, Db.GetLeaderboardPopulation("song1", "Solo_Guitar"));

        Db.RaiseLeaderboardPopulationFloor("song1", "Solo_Guitar", -5);
        Assert.Equal(-1, Db.GetLeaderboardPopulation("song1", "Solo_Guitar"));
    }

    // ═══ PlayerStats ════════════════════════════════════════════

    [Fact]
    public void UpsertPlayerStats_inserts_new_row()
    {
        Db.UpsertPlayerStats(new Persistence.PlayerStatsDto
        {
            AccountId = "acct_1",
            Instrument = "Solo_Guitar",
            SongsPlayed = 50,
            FullComboCount = 10,
            GoldStarCount = 5,
            AvgAccuracy = 95.5,
            BestRank = 3,
            BestRankSongId = "song_best",
            TotalScore = 5_000_000,
            PercentileDist = "{\"1\":2,\"5\":10}",
            AvgPercentile = "Top 3%",
            OverallPercentile = "Top 10%",
        });

        var stats = Db.GetPlayerStats("acct_1");
        Assert.Single(stats);
        var s = stats[0];
        Assert.Equal("Solo_Guitar", s.Instrument);
        Assert.Equal(50, s.SongsPlayed);
        Assert.Equal(10, s.FullComboCount);
        Assert.Equal(5, s.GoldStarCount);
        Assert.Equal(95.5, s.AvgAccuracy, 0.01);
        Assert.Equal(3, s.BestRank);
        Assert.Equal("song_best", s.BestRankSongId);
        Assert.Equal(5_000_000, s.TotalScore);
        Assert.Equal("{\"1\":2,\"5\":10}", s.PercentileDist);
        Assert.Equal("Top 3%", s.AvgPercentile);
        Assert.Equal("Top 10%", s.OverallPercentile);
    }

    [Fact]
    public void UpsertPlayerStats_updates_existing_row()
    {
        Db.UpsertPlayerStats(new Persistence.PlayerStatsDto
        {
            AccountId = "acct_1",
            Instrument = "Solo_Guitar",
            SongsPlayed = 50,
            FullComboCount = 10,
        });
        Db.UpsertPlayerStats(new Persistence.PlayerStatsDto
        {
            AccountId = "acct_1",
            Instrument = "Solo_Guitar",
            SongsPlayed = 60,
            FullComboCount = 20,
        });

        var stats = Db.GetPlayerStats("acct_1");
        Assert.Single(stats);
        Assert.Equal(60, stats[0].SongsPlayed);
        Assert.Equal(20, stats[0].FullComboCount);
    }

    [Fact]
    public void GetPlayerStats_returns_multiple_instruments()
    {
        Db.UpsertPlayerStats(new Persistence.PlayerStatsDto
        {
            AccountId = "acct_1",
            Instrument = "Solo_Guitar",
            SongsPlayed = 50,
        });
        Db.UpsertPlayerStats(new Persistence.PlayerStatsDto
        {
            AccountId = "acct_1",
            Instrument = "Solo_Bass",
            SongsPlayed = 30,
        });

        var stats = Db.GetPlayerStats("acct_1");
        Assert.Equal(2, stats.Count);
    }

    [Fact]
    public void GetPlayerStats_returns_empty_for_unknown_account()
    {
        var stats = Db.GetPlayerStats("nobody");
        Assert.Empty(stats);
    }

    [Fact]
    public void UpsertPlayerStats_handles_null_optional_fields()
    {
        Db.UpsertPlayerStats(new Persistence.PlayerStatsDto
        {
            AccountId = "acct_1",
            Instrument = "Overall",
            SongsPlayed = 10,
            BestRankSongId = null,
            PercentileDist = null,
            AvgPercentile = null,
            OverallPercentile = null,
        });

        var stats = Db.GetPlayerStats("acct_1");
        Assert.Single(stats);
        Assert.Null(stats[0].BestRankSongId);
        Assert.Null(stats[0].PercentileDist);
    }

    // ═══ Checkpoint ═════════════════════════════════════════════

    [Fact]
    public void Checkpoint_succeeds_after_writes()
    {
        Db.StartScrapeRun();

        // Should not throw
        Db.Checkpoint();
    }

    [Fact]
    public void Checkpoint_succeeds_on_empty_database()
    {
        // Should not throw even when there's nothing to checkpoint
        Db.Checkpoint();
    }

    // ═══ GetCompositeRankingNeighborhood ═════════════════════

    private void SeedCompositeRankings(params (string AccountId, double Rating, int Rank)[] accounts)
    {
        Db.ReplaceCompositeRankings(accounts.Select(a => new CompositeRankingDto
        {
            AccountId = a.AccountId,
            InstrumentsPlayed = 2,
            TotalSongsPlayed = 50,
            CompositeRating = a.Rating,
            CompositeRank = a.Rank,
            ComputedAt = "2025-01-01T00:00:00Z",
        }).ToList());
    }

    [Fact]
    public void GetCompositeRankingNeighborhood_returns_above_self_below()
    {
        SeedCompositeRankings(
            ("a1", 0.1, 1), ("a2", 0.2, 2), ("a3", 0.3, 3),
            ("a4", 0.4, 4), ("a5", 0.5, 5));

        var (above, self, below) = Db.GetCompositeRankingNeighborhood("a3", radius: 2);

        Assert.NotNull(self);
        Assert.Equal("a3", self.AccountId);
        Assert.Equal(3, self.CompositeRank);
        Assert.Equal(2, above.Count);
        Assert.Equal("a1", above[0].AccountId);
        Assert.Equal("a2", above[1].AccountId);
        Assert.Equal(2, below.Count);
        Assert.Equal("a4", below[0].AccountId);
        Assert.Equal("a5", below[1].AccountId);
    }

    [Fact]
    public void GetCompositeRankingNeighborhood_rank1_has_no_above()
    {
        SeedCompositeRankings(
            ("a1", 0.1, 1), ("a2", 0.2, 2), ("a3", 0.3, 3));

        var (above, self, below) = Db.GetCompositeRankingNeighborhood("a1", radius: 2);

        Assert.NotNull(self);
        Assert.Equal("a1", self.AccountId);
        Assert.Empty(above);
        Assert.Equal(2, below.Count);
    }

    [Fact]
    public void GetCompositeRankingNeighborhood_last_rank_has_no_below()
    {
        SeedCompositeRankings(
            ("a1", 0.1, 1), ("a2", 0.2, 2), ("a3", 0.3, 3));

        var (above, self, below) = Db.GetCompositeRankingNeighborhood("a3", radius: 2);

        Assert.NotNull(self);
        Assert.Equal("a3", self.AccountId);
        Assert.Equal(2, above.Count);
        Assert.Empty(below);
    }

    [Fact]
    public void GetCompositeRankingNeighborhood_unknown_account_returns_nulls()
    {
        SeedCompositeRankings(("a1", 0.1, 1));

        var (above, self, below) = Db.GetCompositeRankingNeighborhood("unknown");

        Assert.Null(self);
        Assert.Empty(above);
        Assert.Empty(below);
    }

    [Fact]
    public void GetCompositeRankingNeighborhood_default_radius_is_5()
    {
        var accounts = Enumerable.Range(1, 11)
            .Select(i => ($"a{i}", (double)i * 0.1, i))
            .ToArray();
        SeedCompositeRankings(accounts);

        var (above, self, below) = Db.GetCompositeRankingNeighborhood("a6");

        Assert.NotNull(self);
        Assert.Equal(5, above.Count);
        Assert.Equal(5, below.Count);
    }

    // ═══ GetBestValidScores ═════════════════════════════════════

    [Fact]
    public void GetBestValidScores_returns_highest_valid_score()
    {
        // Insert multiple score history entries: 80k, 90k, and 110k (invalid)
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 80_000, null, 3,
            accuracy: 90, isFullCombo: false, stars: 5, scoreAchievedAt: "2025-01-01T00:00:00Z");
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", 80_000, 90_000, 3, 2,
            accuracy: 95, isFullCombo: true, stars: 6, scoreAchievedAt: "2025-02-01T00:00:00Z");
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", 90_000, 110_000, 2, 1,
            accuracy: 98, isFullCombo: true, stars: 6, scoreAchievedAt: "2025-03-01T00:00:00Z");

        var thresholds = new Dictionary<(string, string), int>
        {
            [("song_1", "Solo_Guitar")] = 100_000, // 110k is invalid
        };
        var result = Db.GetBestValidScores("acct_1", thresholds);

        Assert.Single(result);
        var fallback = result[("song_1", "Solo_Guitar")];
        Assert.Equal(90_000, fallback.Score);
        Assert.Equal(95, fallback.Accuracy);
        Assert.True(fallback.IsFullCombo);
        Assert.Equal(6, fallback.Stars);
    }

    [Fact]
    public void GetBestValidScores_returns_empty_when_no_valid_scores()
    {
        // Only one score, and it's invalid
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 110_000, null, 1,
            accuracy: 98, scoreAchievedAt: "2025-01-01T00:00:00Z");

        var thresholds = new Dictionary<(string, string), int>
        {
            [("song_1", "Solo_Guitar")] = 100_000,
        };
        var result = Db.GetBestValidScores("acct_1", thresholds);

        Assert.Empty(result);
    }

    [Fact]
    public void GetBestValidScores_returns_empty_for_empty_thresholds()
    {
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 90_000, null, 1,
            scoreAchievedAt: "2025-01-01T00:00:00Z");

        var result = Db.GetBestValidScores("acct_1", new Dictionary<(string, string), int>());
        Assert.Empty(result);
    }

    [Fact]
    public void GetBestValidScores_handles_multiple_instruments()
    {
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 90_000, null, 1,
            accuracy: 95, isFullCombo: true, stars: 6, scoreAchievedAt: "2025-01-01T00:00:00Z");
        Db.InsertScoreChange("song_1", "Solo_Bass", "acct_1", null, 85_000, null, 2,
            accuracy: 90, isFullCombo: false, stars: 5, scoreAchievedAt: "2025-01-01T00:00:00Z");

        var thresholds = new Dictionary<(string, string), int>
        {
            [("song_1", "Solo_Guitar")] = 100_000,
            [("song_1", "Solo_Bass")] = 100_000,
        };
        var result = Db.GetBestValidScores("acct_1", thresholds);

        Assert.Equal(2, result.Count);
        Assert.Equal(90_000, result[("song_1", "Solo_Guitar")].Score);
        Assert.Equal(85_000, result[("song_1", "Solo_Bass")].Score);
    }

    // ═══ GetBulkBestValidScores ═════════════════════════════════

    [Fact]
    public void GetBulkBestValidScores_ReturnsHighestValidPerEntry()
    {
        // acct_1 on song_1: 80k, 90k (valid), 110k (invalid)
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 80_000, null, 3,
            accuracy: 90, isFullCombo: false, stars: 5, scoreAchievedAt: "2025-01-01T00:00:00Z");
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", 80_000, 90_000, 3, 2,
            accuracy: 95, isFullCombo: true, stars: 6, scoreAchievedAt: "2025-02-01T00:00:00Z");
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", 90_000, 110_000, 2, 1,
            accuracy: 98, isFullCombo: true, stars: 6, scoreAchievedAt: "2025-03-01T00:00:00Z");

        // acct_2 on song_2: 50k (valid)
        Db.InsertScoreChange("song_2", "Solo_Guitar", "acct_2", null, 50_000, null, 5,
            accuracy: 85, isFullCombo: false, stars: 4, scoreAchievedAt: "2025-01-01T00:00:00Z");

        var entries = new Dictionary<(string, string), int>
        {
            [("acct_1", "song_1")] = 100_000, // 110k exceeds, 90k is best valid
            [("acct_2", "song_2")] = 100_000,
        };
        var result = Db.GetBulkBestValidScores("Solo_Guitar", entries);

        Assert.Equal(2, result.Count);
        Assert.Equal(90_000, result[("acct_1", "song_1")].Score);
        Assert.Equal(95, result[("acct_1", "song_1")].Accuracy);
        Assert.True(result[("acct_1", "song_1")].IsFullCombo);
        Assert.Equal(50_000, result[("acct_2", "song_2")].Score);
    }

    [Fact]
    public void GetBulkBestValidScores_SkipsEntriesAboveThreshold()
    {
        // Only score is above threshold
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 110_000, null, 1,
            accuracy: 98, scoreAchievedAt: "2025-01-01T00:00:00Z");

        var entries = new Dictionary<(string, string), int>
        {
            [("acct_1", "song_1")] = 100_000,
        };
        var result = Db.GetBulkBestValidScores("Solo_Guitar", entries);
        Assert.Empty(result);
    }

    [Fact]
    public void GetBulkBestValidScores_EmptyInput_ReturnsEmpty()
    {
        var result = Db.GetBulkBestValidScores("Solo_Guitar", new Dictionary<(string, string), int>());
        Assert.Empty(result);
    }

    [Fact]
    public void GetBulkBestValidScores_FiltersbyInstrument()
    {
        // Same account/song but different instruments
        Db.InsertScoreChange("song_1", "Solo_Guitar", "acct_1", null, 90_000, null, 1,
            accuracy: 95, scoreAchievedAt: "2025-01-01T00:00:00Z");
        Db.InsertScoreChange("song_1", "Solo_Bass", "acct_1", null, 70_000, null, 1,
            accuracy: 85, scoreAchievedAt: "2025-01-01T00:00:00Z");

        var entries = new Dictionary<(string, string), int>
        {
            [("acct_1", "song_1")] = 100_000,
        };

        var guitarResult = Db.GetBulkBestValidScores("Solo_Guitar", entries);
        Assert.Single(guitarResult);
        Assert.Equal(90_000, guitarResult[("acct_1", "song_1")].Score);

        var bassResult = Db.GetBulkBestValidScores("Solo_Bass", entries);
        Assert.Single(bassResult);
        Assert.Equal(70_000, bassResult[("acct_1", "song_1")].Score);
    }

    // ═══ Leaderboard Rivals ═════════════════════════════════════

    [Fact]
    public void ReplaceLeaderboardRivalsData_persists_and_reads_back()
    {
        var rivals = new List<Persistence.LeaderboardRivalRow>
        {
            new()
            {
                UserId = "u1", RivalAccountId = "r1", Instrument = "Solo_Guitar",
                RankMethod = "totalscore", Direction = "above",
                UserRank = 10, RivalRank = 8, SharedSongCount = 5,
                AheadCount = 3, BehindCount = 2, AvgSignedDelta = -1.5,
                ComputedAt = "2026-01-01T00:00:00Z",
            },
        };
        var samples = new List<Persistence.LeaderboardRivalSongSampleRow>
        {
            new()
            {
                UserId = "u1", RivalAccountId = "r1", Instrument = "Solo_Guitar",
                RankMethod = "totalscore", SongId = "s1",
                UserRank = 10, RivalRank = 8, RankDelta = -2,
                UserScore = 1000, RivalScore = 1100,
            },
        };

        Db.ReplaceLeaderboardRivalsData("u1", "Solo_Guitar", rivals, samples);

        var readRivals = Db.GetLeaderboardRivals("u1", "Solo_Guitar", "totalscore");
        Assert.Single(readRivals);
        Assert.Equal("r1", readRivals[0].RivalAccountId);
        Assert.Equal(5, readRivals[0].SharedSongCount);

        var readSamples = Db.GetLeaderboardRivalSongSamples("u1", "r1", "Solo_Guitar", "totalscore");
        Assert.Single(readSamples);
        Assert.Equal("s1", readSamples[0].SongId);
        Assert.Equal(-2, readSamples[0].RankDelta);
    }

    [Fact]
    public void ReplaceLeaderboardRivalsData_replaces_existing_data()
    {
        var initial = new List<Persistence.LeaderboardRivalRow>
        {
            new()
            {
                UserId = "u1", RivalAccountId = "old_rival", Instrument = "Solo_Guitar",
                RankMethod = "totalscore", Direction = "below",
                UserRank = 5, RivalRank = 7, SharedSongCount = 3,
                AheadCount = 2, BehindCount = 1, AvgSignedDelta = 2.0,
                ComputedAt = "2026-01-01T00:00:00Z",
            },
        };

        Db.ReplaceLeaderboardRivalsData("u1", "Solo_Guitar", initial, []);

        var updated = new List<Persistence.LeaderboardRivalRow>
        {
            new()
            {
                UserId = "u1", RivalAccountId = "new_rival", Instrument = "Solo_Guitar",
                RankMethod = "totalscore", Direction = "above",
                UserRank = 5, RivalRank = 3, SharedSongCount = 10,
                AheadCount = 4, BehindCount = 6, AvgSignedDelta = -3.0,
                ComputedAt = "2026-01-02T00:00:00Z",
            },
        };

        Db.ReplaceLeaderboardRivalsData("u1", "Solo_Guitar", updated, []);

        var readRivals = Db.GetLeaderboardRivals("u1", "Solo_Guitar");
        Assert.Single(readRivals);
        Assert.Equal("new_rival", readRivals[0].RivalAccountId);
    }

    private (
        long CatalogVersion,
        int SchemaVersion,
        string CatalogJson,
        string ContentHash,
        int SongCount,
        string SourceKind,
        bool IsExact)
        ReadLiveSongCatalog()
    {
        using var conn = DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT catalog_version, schema_version, catalog_json::text,
                   content_hash, song_count, source_kind, is_exact
            FROM live_song_catalog
            WHERE id = TRUE
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return (
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.GetBoolean(6));
    }

    private (
        long CatalogVersion,
        int SchemaVersion,
        string CatalogJson,
        string ContentHash,
        int SongCount,
        string SourceKind,
        bool IsExact)
        ReadPublicationSongCatalog(long publicationId)
    {
        using var conn = DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT catalog_version, schema_version, catalog_json::text,
                   content_hash, song_count, source_kind, is_exact
            FROM publication_song_catalog
            WHERE publication_id = @publicationId
            """;
        cmd.Parameters.AddWithValue("publicationId", publicationId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return (
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.GetBoolean(6));
    }

    private bool HasPublicationSongCatalog(long publicationId)
    {
        using var conn = DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM publication_song_catalog
                WHERE publication_id = @publicationId
            )
            """;
        cmd.Parameters.AddWithValue("publicationId", publicationId);
        return (bool)cmd.ExecuteScalar()!;
    }

    private long CountScrapeRuns()
    {
        using var conn = DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM scrape_log";
        return (long)cmd.ExecuteScalar()!;
    }

    private static Song CreateCatalogSong(string songId, string title) =>
        new()
        {
            _title = title,
            lastModified = new DateTime(
                2026, 7, 31, 12, 0, 0, DateTimeKind.Utc),
            track = new Track
            {
                su = songId,
                tt = title,
                an = "Artist",
                ab = "Album",
                au = $"https://example.test/{songId}.jpg",
                mu = $"https://example.test/{songId}.dat",
                sig = "4/4",
                ge = ["rock"],
                ry = 2026,
                mt = 120,
                dn = 200,
                @in = new In
                {
                    gr = 1,
                    ba = 2,
                    vl = 3,
                    ds = 4,
                    pg = 5,
                    pb = 6,
                    pd = 7,
                    bd = 8,
                },
            },
        };
}
