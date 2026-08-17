using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FortniteFestival.Core.Persistence;
using FSTService.Scraping;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

internal sealed record MaxScoreMaintenanceCommitTestContext(
    string Operation,
    int BackendProcessId);

internal sealed record MaxScoreMaintenanceServerTimeoutTestContext(
    string Stage,
    int StatementTimeoutSeconds,
    int LockTimeoutSeconds,
    string TransactionIsolation);

/// <summary>
/// Central metadata database (<see cref="IMetaDatabase"/> implementation).
/// Uses NpgsqlDataSource (connection pooling) — MVCC handles concurrent reads/writes natively.
/// </summary>
public sealed partial class MetaDatabase : IMetaDatabase
{
    private static readonly JsonSerializerOptions WorkerOperationJsonOptions =
        new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    private static readonly AsyncLocal<long?> PublicationCacheBuildTarget = new();
    private static readonly AsyncLocal<long?>
        CurrentPublicationMaintenanceTarget = new();
    private readonly NpgsqlDataSource _ds;
    private readonly PostgresUnpooledConnectionFactory
        _unpooledConnections;
    private readonly SemaphoreSlim _boundedRegistrationAdmissions;
    private readonly ILogger<MetaDatabase> _log;
    private readonly BandRankHistoryOptions _bandRankHistoryOptions;
    private readonly PublicationCommitOptions _publicationCommitOptions;
    private readonly int
        _maxScoreMaintenanceCommandTimeoutSeconds;
    private readonly object _bandRankHistoryPollingSchemaLock = new();
    private bool _bandRankHistoryPollingSchemaEnsured;
    private int _bandRankHistoryCompactV3DuetsReady;
    private int _bandRankHistoryCompactV3TriosReady;
    private int _bandRankHistoryCompactV3QuadReady;
    internal Func<Exception?>?
        PublicReadFreezeReadTestHook
    { get; set; }
    internal Action? PublicReadFreezeWriteTestHook { get; set; }
    internal Action<MaxScoreMaintenanceCommitTestContext>?
        MaxScoreMaintenanceBeforeCommitTestHook
    { get; set; }
    internal Action<MaxScoreMaintenanceCommitTestContext>?
        MaxScoreMaintenanceAfterLocksReleasedTestHook
    { get; set; }
    internal Action<MaxScoreMaintenanceServerTimeoutTestContext>?
        MaxScoreMaintenanceServerTimeoutTestHook
    { get; set; }

    internal const int DataCollectionVersion = 3;
    internal const string WebTrackerDeviceId = "web-tracker";
    internal const string WebBandTrackerDeviceId = "web-band-tracker";
    internal const string LegacyLeaderboardStagingTable = "leaderboard_staging";
    internal const string LeaderboardStagingTable = "leaderboard_staging_v2";
    internal const string FailedCandidateReadIsolationFailurePhase = "capacity_watchdog_abandoned";
    internal const string NoProgressReadIsolationFailurePhase = "post_process_no_progress_abandoned";
    internal const string MaxScoreMaintenanceSourceLockSql = """
        LOCK TABLE leaderboard_entries_overlay IN SHARE MODE;
        LOCK TABLE leaderboard_entries IN SHARE MODE;
        LOCK TABLE score_history IN SHARE MODE;
        LOCK TABLE band_member_stats IN SHARE MODE;
        LOCK TABLE leaderboard_population IN SHARE MODE;
        """;
    internal const string PostProcessReadIsolationFailurePhase = "post_process";
    internal const string PublicationReadIsolationFailurePhase = "publication";
    internal const string StalePublicationCommitIntentFailurePhase =
        "stale_publication_commit_intent";
    internal const string FailedCandidateReadIsolationReason = "failed-candidate";
    private const string BandRankHistoryCompactV3StateTable = "band_rank_history_compact_v3_state";
    private const string BandRankHistoryCompactV3DuetsTable = "band_team_rank_history_points_v3_duets";
    private const string BandRankHistoryCompactV3DuetsTeamTable = "band_rank_history_team_v3_duets";
    private const string BandRankHistoryCompactV3DuetsComboTable = "band_rank_history_combo_v3_duets";
    private const string BandRankHistoryCompactV3TriosTable = "band_team_rank_history_points_v3_trios";
    private const string BandRankHistoryCompactV3TriosTeamTable = "band_rank_history_team_v3_trios";
    private const string BandRankHistoryCompactV3TriosComboTable = "band_rank_history_combo_v3_trios";
    private const string BandRankHistoryCompactV3QuadTable = "band_team_rank_history_points_v3_quad";
    private const string BandRankHistoryCompactV3QuadTeamTable = "band_rank_history_team_v3_quad";
    private const string BandRankHistoryCompactV3QuadComboTable = "band_rank_history_combo_v3_quad";
    private static readonly string[] FailedCandidateReadIsolationFailurePhases =
    [
        FailedCandidateReadIsolationFailurePhase,
        NoProgressReadIsolationFailurePhase,
        PostProcessReadIsolationFailurePhase,
        PublicationReadIsolationFailurePhase,
        StalePublicationCommitIntentFailurePhase,
    ];
    private const string LeaderboardStagingReadColumns = "scrape_id, song_id, instrument, page_num, account_id, score, accuracy, is_full_combo, stars, season, difficulty, percentile, rank, end_time, api_rank, source, staged_at";
    private const int
        MaxScoreMaintenanceFinalMutationStatementTimeoutSeconds = 120;
    private const string SongInstrumentThresholdsCte = """
        requested_thresholds AS MATERIALIZED (
            SELECT threshold.song_id,
                   threshold.instrument,
                   threshold.max_score
            FROM unnest(
                @thresholdSongIds::TEXT[],
                @thresholdInstruments::TEXT[],
                @thresholdMaxScores::INTEGER[])
                AS threshold(song_id, instrument, max_score)
        )
        """;

    public MetaDatabase(
        NpgsqlDataSource dataSource,
        ILogger<MetaDatabase> log,
        BandRankHistoryOptions? bandRankHistoryOptions = null,
        PublicationCommitOptions? publicationCommitOptions = null,
        PostgresUnpooledConnectionFactory?
            unpooledConnections = null,
        ScraperOptions? scraperOptions = null)
    {
        _ds = dataSource;
        _unpooledConnections =
            unpooledConnections
            ?? new PostgresUnpooledConnectionFactory(
                dataSource.ConnectionString);
        var connectionSettings =
            new NpgsqlConnectionStringBuilder(
                dataSource.ConnectionString);
        _boundedRegistrationAdmissions =
            new SemaphoreSlim(
                Math.Max(1, connectionSettings.MaxPoolSize),
                Math.Max(1, connectionSettings.MaxPoolSize));
        _log = log;
        _bandRankHistoryOptions = bandRankHistoryOptions ?? new BandRankHistoryOptions();
        _publicationCommitOptions =
            publicationCommitOptions ?? new PublicationCommitOptions();
        _maxScoreMaintenanceCommandTimeoutSeconds =
            scraperOptions?
                .MaxScoreMaintenanceCommandTimeoutSeconds
            ?? ScraperOptions
                .DefaultMaxScoreMaintenanceCommandTimeoutSeconds;
    }

    public MetaDatabase(
        NpgsqlDataSource dataSource,
        ILogger<MetaDatabase> log,
        IOptions<BandRankHistoryOptions> bandRankHistoryOptions,
        IOptions<PublicationCommitOptions> publicationCommitOptions,
        PostgresUnpooledConnectionFactory?
            unpooledConnections = null,
        IOptions<ScraperOptions>? scraperOptions = null)
        : this(
            dataSource,
            log,
            bandRankHistoryOptions.Value,
            publicationCommitOptions.Value,
            unpooledConnections,
            scraperOptions?.Value)
    {
    }

    public void EnsureSchema() { } // Created by DatabaseInitializer

    internal static string GetLeaderboardStagingReadSource(string alias) =>
        $"(SELECT {LeaderboardStagingReadColumns} FROM {LeaderboardStagingTable} " +
        $"UNION ALL SELECT {LeaderboardStagingReadColumns} FROM {LegacyLeaderboardStagingTable}) AS {alias}";

    private static void AddSongInstrumentThresholdParameters(
        NpgsqlCommand command,
        IReadOnlyDictionary<(string SongId, string Instrument), int> thresholds)
    {
        var ordered = thresholds
            .OrderBy(static pair => pair.Key.SongId, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Key.Instrument, StringComparer.Ordinal)
            .ToArray();
        command.Parameters.Add(
                "thresholdSongIds",
                NpgsqlDbType.Array | NpgsqlDbType.Text)
            .Value = ordered.Select(static pair => pair.Key.SongId).ToArray();
        command.Parameters.Add(
                "thresholdInstruments",
                NpgsqlDbType.Array | NpgsqlDbType.Text)
            .Value = ordered.Select(static pair => pair.Key.Instrument).ToArray();
        command.Parameters.Add(
                "thresholdMaxScores",
                NpgsqlDbType.Array | NpgsqlDbType.Integer)
            .Value = ordered.Select(static pair => pair.Value).ToArray();
    }

    // ── Scrape log ───────────────────────────────────────────────────

    public long StartScrapeRun() => StartScrapeRun(expectedCatalog: null);

    public long StartScrapeRun(
        SongCatalogPersistenceToken? expectedCatalog)
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow;

        using (var publicationLock = conn.CreateCommand())
        {
            publicationLock.Transaction = tx;
            publicationLock.CommandText =
                "SELECT pg_advisory_xact_lock(@lockKey)";
            publicationLock.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            publicationLock.ExecuteNonQuery();
        }

        using (var deferredPublication = conn.CreateCommand())
        {
            deferredPublication.Transaction = tx;
            deferredPublication.CommandText = """
                SELECT EXISTS (
                    SELECT 1
                    FROM scrape_publication_state publication
                    JOIN publication_generations generation
                      ON generation.publication_id =
                            publication.working_publication_id
                    WHERE publication.id = TRUE
                      AND publication.public_reads_frozen_reason =
                            @deferredReason
                      AND generation.status = 'ready'
                )
                """;
            deferredPublication.Parameters.AddWithValue(
                "deferredReason",
                PublicReadFreezeState.PublicationCommitDeferredReason);
            if (deferredPublication.ExecuteScalar() is true)
            {
                throw new PublicationCommitBusyException(
                    "A ready publication is deferred and must be retried before allocating another scrape.",
                    TimeSpan.Zero,
                    lockRejections: 1,
                    relationLockRetries: 0);
            }
        }

        using (var maxScoreMaintenance = conn.CreateCommand())
        {
            maxScoreMaintenance.Transaction = tx;
            maxScoreMaintenance.CommandText = """
                SELECT EXISTS (
                    SELECT 1
                    FROM scrape_publication_state publication
                    WHERE publication.id = TRUE
                      AND (
                          publication.max_score_mutation_gate_token
                              IS NOT NULL
                          OR (
                              publication.public_reads_frozen
                              AND publication.public_reads_frozen_reason
                                  LIKE @maxScoreReasonPrefix
                          )
                      )
                )
                """;
            maxScoreMaintenance.Parameters.AddWithValue(
                "maxScoreReasonPrefix",
                PublicReadFreezeState
                    .MaxScoreMaintenanceReasonPrefix
                + "%");
            if (maxScoreMaintenance.ExecuteScalar() is true)
            {
                throw new PublicationCommitBusyException(
                    "Max-score maintenance must finish or reconcile before allocating another scrape.",
                    TimeSpan.Zero,
                    lockRejections: 1,
                    relationLockRetries: 0);
            }
        }

        SongCatalogPersistenceToken persistedCatalog;
        using (var catalogToken = conn.CreateCommand())
        {
            catalogToken.Transaction = tx;
            catalogToken.CommandText = """
                SELECT catalog_version, schema_version, content_hash,
                       song_count, is_exact, source_kind
                FROM live_song_catalog
                WHERE id = TRUE
                FOR SHARE
                """;
            using var reader = catalogToken.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException(
                    "A scrape publication cannot be allocated without a persisted song catalog.");
            }

            persistedCatalog = new SongCatalogPersistenceToken(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetInt32(3));
            var isExact = reader.GetBoolean(4);
            var sourceKind = reader.GetString(5);
            if (!isExact
                || sourceKind != "provider_exact"
                || persistedCatalog.SchemaVersion !=
                    SongCatalogSnapshotBuilder.SchemaVersion)
            {
                throw new InvalidOperationException(
                    "A scrape publication cannot be allocated from a reconstructed or obsolete song catalog.");
            }
        }

        if (expectedCatalog is not null
            && !CatalogTokensMatch(expectedCatalog, persistedCatalog))
        {
            throw new InvalidOperationException(
                $"The persisted song catalog changed before scrape allocation " +
                $"(expected version {expectedCatalog.CatalogVersion}/{expectedCatalog.ContentHash}, " +
                $"found {persistedCatalog.CatalogVersion}/{persistedCatalog.ContentHash}).");
        }

        long scrapeId;
        using (var scrape = conn.CreateCommand())
        {
            scrape.Transaction = tx;
            scrape.CommandText =
                "INSERT INTO scrape_log (started_at, status) VALUES (@now, 'running') RETURNING id";
            scrape.Parameters.AddWithValue("now", now);
            scrapeId = (long)(int)scrape.ExecuteScalar()!;
        }

        long publicationId;
        using (var generation = conn.CreateCommand())
        {
            generation.Transaction = tx;
            generation.CommandText = """
                INSERT INTO publication_generations (
                    scrape_id, status, created_at, source_cut_at)
                VALUES (@scrapeId, 'building', @now, @now)
                RETURNING publication_id
                """;
            generation.Parameters.AddWithValue("scrapeId", scrapeId);
            generation.Parameters.AddWithValue("now", now);
            publicationId = (long)generation.ExecuteScalar()!;
        }

        using (var catalog = conn.CreateCommand())
        {
            catalog.Transaction = tx;
            catalog.CommandText = """
                INSERT INTO publication_song_catalog (
                    publication_id, catalog_version, schema_version,
                    catalog_json, content_hash, song_count, source_kind,
                    is_exact, source_captured_at, captured_at)
                SELECT
                    @publicationId,
                    catalog_version,
                    schema_version,
                    catalog_json,
                    content_hash,
                    song_count,
                    source_kind,
                    is_exact,
                    captured_at,
                    @now
                FROM live_song_catalog
                WHERE id = TRUE
                  AND catalog_version = @catalogVersion
                  AND schema_version = @schemaVersion
                  AND content_hash = @contentHash
                  AND song_count = @songCount
                  AND is_exact
                ON CONFLICT (publication_id) DO NOTHING
                """;
            catalog.Parameters.AddWithValue("publicationId", publicationId);
            catalog.Parameters.AddWithValue(
                "catalogVersion",
                persistedCatalog.CatalogVersion);
            catalog.Parameters.AddWithValue(
                "schemaVersion",
                persistedCatalog.SchemaVersion);
            catalog.Parameters.AddWithValue(
                "contentHash",
                persistedCatalog.ContentHash);
            catalog.Parameters.AddWithValue(
                "songCount",
                persistedCatalog.SongCount);
            catalog.Parameters.AddWithValue("now", now);
            if (catalog.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    "A scrape publication cannot be allocated without a live song catalog snapshot.");
            }
        }

        using (var catalogBinding = conn.CreateCommand())
        {
            catalogBinding.Transaction = tx;
            catalogBinding.CommandText = """
                INSERT INTO publication_surface_bindings (
                    publication_id, surface_name, binding_kind, binding_json,
                    row_count, content_hash, status, built_at)
                SELECT
                    publication_id,
                    'song_catalog',
                    'generation_catalog_snapshot',
                    jsonb_build_object(
                        'table', 'publication_song_catalog',
                        'publicationId', publication_id,
                        'catalogVersion', catalog_version,
                        'schemaVersion', schema_version,
                        'sourceKind', source_kind,
                        'isExact', is_exact,
                        'sourceCapturedAt', source_captured_at),
                    song_count,
                    content_hash,
                    'ready',
                    captured_at
                FROM publication_song_catalog
                WHERE publication_id = @publicationId
                """;
            catalogBinding.Parameters.AddWithValue(
                "publicationId",
                publicationId);
            if (catalogBinding.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"Publication generation {publicationId} has no ready song catalog binding.");
            }
        }

        using (var pointer = conn.CreateCommand())
        {
            pointer.Transaction = tx;
            pointer.CommandText = """
                INSERT INTO scrape_publication_state (
                    id, working_publication_id, updated_at)
                VALUES (TRUE, @publicationId, @now)
                ON CONFLICT (id) DO UPDATE SET
                    working_publication_id = EXCLUDED.working_publication_id,
                    updated_at = EXCLUDED.updated_at
                """;
            pointer.Parameters.AddWithValue("publicationId", publicationId);
            pointer.Parameters.AddWithValue("now", now);
            pointer.ExecuteNonQuery();
        }

        using (var retainCatalogs = conn.CreateCommand())
        {
            retainCatalogs.Transaction = tx;
            retainCatalogs.CommandText = """
                DELETE FROM publication_song_catalog catalog
                USING scrape_publication_state publication
                WHERE publication.id = TRUE
                  AND catalog.publication_id IS DISTINCT FROM
                      publication.current_publication_id
                  AND catalog.publication_id IS DISTINCT FROM
                      publication.previous_publication_id
                  AND catalog.publication_id IS DISTINCT FROM
                      publication.working_publication_id;

                UPDATE publication_surface_bindings binding
                SET binding_kind = 'retired_generation_catalog',
                    binding_json = jsonb_build_object(
                        'table', 'publication_song_catalog',
                        'retired', true),
                    row_count = 0,
                    content_hash = NULL,
                    status = 'retired',
                    built_at = @now
                FROM scrape_publication_state publication
                WHERE publication.id = TRUE
                  AND binding.surface_name = 'song_catalog'
                  AND binding.publication_id IS DISTINCT FROM
                      publication.current_publication_id
                  AND binding.publication_id IS DISTINCT FROM
                      publication.previous_publication_id
                  AND binding.publication_id IS DISTINCT FROM
                      publication.working_publication_id
                  AND binding.status <> 'retired';
                """;
            retainCatalogs.Parameters.AddWithValue("now", now);
            retainCatalogs.ExecuteNonQuery();
        }

        tx.Commit();
        return scrapeId;
    }

    public void CompleteScrapeRun(long scrapeId, int songsScraped, long totalEntries, int totalRequests, long totalBytes, bool epicReportedOver100Pages = false)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE scrape_log
            SET completed_at = @now,
                status = 'completed',
                failed_at = NULL,
                failure_phase = NULL,
                failure_message = NULL,
                songs_scraped = @songs,
                total_entries = @entries,
                total_requests = @requests,
                total_bytes = @bytes,
                epic_reported_over_100_pages = @epicReportedOver100Pages
            WHERE id = @id
              AND status <> 'failed'
            """;
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("songs", songsScraped);
        cmd.Parameters.AddWithValue("entries", (int)totalEntries);
        cmd.Parameters.AddWithValue("requests", totalRequests);
        cmd.Parameters.AddWithValue("bytes", totalBytes);
        cmd.Parameters.AddWithValue("epicReportedOver100Pages", epicReportedOver100Pages);
        cmd.Parameters.AddWithValue("id", (int)scrapeId);
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException(
                $"Scrape run {scrapeId} cannot be completed after it has failed.");
    }


    public void RecordScrapeWriterFailures(
        long scrapeId,
        IReadOnlyList<WriterDrainResult> results)
    {
        if (scrapeId <= 0)
            return;

        var failures = results.SelectMany(static result => result.Failures).ToArray();
        if (failures.Length == 0)
            return;

        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        foreach (var failure in failures)
        {
            foreach (var scope in failure.Scopes)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO scrape_writer_failures (
                        scrape_id, writer_kind, instrument, song_id, page_count,
                        row_count, artifact_path, exception_type, error_message, occurred_at)
                    VALUES (
                        @scrapeId, @writerKind, @instrument, @songId, @pageCount,
                        @rowCount, @artifactPath, @exceptionType, @errorMessage, @occurredAt)
                    """;
                cmd.Parameters.AddWithValue("scrapeId", scrapeId);
                cmd.Parameters.AddWithValue("writerKind", failure.WriterKind);
                cmd.Parameters.AddWithValue("instrument", failure.Instrument);
                cmd.Parameters.AddWithValue("songId", scope.SongId);
                cmd.Parameters.AddWithValue("pageCount", scope.PageCount);
                cmd.Parameters.AddWithValue("rowCount", scope.RowCount);
                cmd.Parameters.AddWithValue(
                    "artifactPath",
                    (object?)failure.ArtifactPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("exceptionType", failure.ExceptionType);
                cmd.Parameters.AddWithValue("errorMessage", failure.ErrorMessage);
                cmd.Parameters.AddWithValue("occurredAt", failure.OccurredAtUtc);
                cmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    public void RecordScrapePhaseOutcome(ScrapePhaseOutcomeRecord outcome)
    {
        if (outcome.ScrapeId <= 0 || string.IsNullOrWhiteSpace(outcome.Phase))
            return;

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scrape_phase_outcomes (
                scrape_id, phase, criticality, status, started_at,
                completed_at, duration_ms, error_message)
            VALUES (
                @scrapeId, @phase, @criticality, @status, @startedAt,
                @completedAt, @durationMs, @errorMessage)
            ON CONFLICT (scrape_id, phase) DO UPDATE SET
                criticality = EXCLUDED.criticality,
                status = EXCLUDED.status,
                started_at = EXCLUDED.started_at,
                completed_at = EXCLUDED.completed_at,
                duration_ms = EXCLUDED.duration_ms,
                error_message = EXCLUDED.error_message;

            UPDATE scrape_log
            SET best_effort_failure_count = (
                    SELECT COUNT(*)::int
                    FROM scrape_phase_outcomes
                    WHERE scrape_id = @scrapeId
                      AND criticality = 'best_effort'
                      AND status = 'failed'
                ),
                best_effort_failed_phases = ARRAY(
                    SELECT phase
                    FROM scrape_phase_outcomes
                    WHERE scrape_id = @scrapeId
                      AND criticality = 'best_effort'
                      AND status = 'failed'
                    ORDER BY phase
                )
            WHERE id = @scrapeId
            """;
        cmd.Parameters.AddWithValue("scrapeId", outcome.ScrapeId);
        cmd.Parameters.AddWithValue("phase", outcome.Phase);
        cmd.Parameters.AddWithValue("criticality", outcome.Criticality);
        cmd.Parameters.AddWithValue("status", outcome.Status);
        cmd.Parameters.AddWithValue("startedAt", outcome.StartedAtUtc);
        cmd.Parameters.AddWithValue("completedAt", outcome.CompletedAtUtc);
        cmd.Parameters.AddWithValue("durationMs", outcome.DurationMs);
        cmd.Parameters.AddWithValue("errorMessage", (object?)outcome.ErrorMessage ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public ScrapeResumeState? GetScrapeResumeState(long scrapeId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                scrape.id,
                scrape.started_at,
                scrape.status,
                publication.published_scrape_id,
                (SELECT COUNT(*)::int FROM leaderboard_scope_manifests WHERE scrape_id = scrape.id),
                (SELECT COUNT(*)::int FROM leaderboard_scope_manifests WHERE scrape_id = scrape.id AND is_complete),
                (SELECT COUNT(*)::int FROM scrape_writer_failures WHERE scrape_id = scrape.id),
                (
                    SELECT COUNT(*)::int
                    FROM scrape_phase_outcomes
                    WHERE scrape_id = scrape.id
                      AND criticality = 'publication_critical'
                      AND status <> 'completed'
                )
            FROM scrape_log scrape
            LEFT JOIN scrape_publication_state publication ON publication.id = TRUE
            WHERE scrape.id = @scrapeId
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);

        long id;
        DateTime startedAtUtc;
        string status;
        long? publishedScrapeId;
        int manifestCount;
        int completeManifestCount;
        int writerFailureCount;
        int criticalPhaseFailureCount;
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read())
                return null;

            id = Convert.ToInt64(reader.GetValue(0));
            startedAtUtc = reader.GetDateTime(1);
            status = reader.GetString(2);
            publishedScrapeId = reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3));
            manifestCount = reader.GetInt32(4);
            completeManifestCount = reader.GetInt32(5);
            writerFailureCount = reader.GetInt32(6);
            criticalPhaseFailureCount = reader.GetInt32(7);
        }

        using var outcomesCmd = conn.CreateCommand();
        outcomesCmd.CommandText = """
            SELECT scrape_id, phase, criticality, status, started_at,
                   completed_at, duration_ms, error_message
            FROM scrape_phase_outcomes
            WHERE scrape_id = @scrapeId
            ORDER BY started_at, phase
            """;
        outcomesCmd.Parameters.AddWithValue("scrapeId", scrapeId);
        var outcomes = new List<ScrapePhaseOutcomeRecord>();
        using (var reader = outcomesCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                outcomes.Add(new ScrapePhaseOutcomeRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetDateTime(4),
                    reader.GetDateTime(5),
                    reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        return new ScrapeResumeState(
            id,
            startedAtUtc,
            status,
            publishedScrapeId,
            manifestCount,
            completeManifestCount,
            writerFailureCount,
            criticalPhaseFailureCount,
            outcomes);
    }

    public ScrapeRunInfo? GetLastCompletedScrapeRun()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, started_at, completed_at, songs_scraped, total_entries,
                   total_requests, total_bytes, epic_reported_over_100_pages,
                   status, failed_at, failure_phase, failure_message,
                   best_effort_failure_count, best_effort_failed_phases
            FROM scrape_log
            WHERE completed_at IS NOT NULL
              AND status = 'completed'
            ORDER BY id DESC
            LIMIT 1
            """;
        using var r = cmd.ExecuteReader();
        return ReadScrapeRunInfo(r);
    }

    public ScrapeRunInfo? GetPublishedScrapeRun()
    {
        try
        {
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                                SELECT scrape.id, scrape.started_at, scrape.completed_at, scrape.songs_scraped,
                                             scrape.total_entries, scrape.total_requests, scrape.total_bytes,
                                             scrape.epic_reported_over_100_pages, scrape.status,
                                             scrape.failed_at, scrape.failure_phase, scrape.failure_message,
                                             scrape.best_effort_failure_count, scrape.best_effort_failed_phases
                                FROM scrape_publication_state publication
                                JOIN scrape_log scrape ON scrape.id = publication.published_scrape_id
                                WHERE publication.id = TRUE
                                    AND scrape.completed_at IS NOT NULL
                                    AND scrape.status = 'completed'
                                UNION ALL
                                SELECT id, started_at, completed_at, songs_scraped, total_entries, total_requests,
                                             total_bytes, epic_reported_over_100_pages, status,
                                             failed_at, failure_phase, failure_message,
                                             best_effort_failure_count, best_effort_failed_phases
                                FROM scrape_log
                                WHERE completed_at IS NOT NULL
                                    AND status = 'completed'
                                    AND NOT EXISTS (SELECT 1 FROM scrape_publication_state WHERE id = TRUE AND published_scrape_id IS NOT NULL)
                                ORDER BY id DESC
                                LIMIT 1
                                """;
            using var r = cmd.ExecuteReader();
            return ReadScrapeRunInfo(r);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return GetLastCompletedScrapeRun();
        }
    }

    public PublicationPointerState GetPublicationPointerState()
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT current_publication_id,
                   previous_publication_id,
                   working_publication_id,
                   published_scrape_id,
                   published_at
            FROM scrape_publication_state
            WHERE id = TRUE
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return new PublicationPointerState(null, null, null, null, null);

        return new PublicationPointerState(
            reader.IsDBNull(0) ? null : reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4));
    }

    public PublicationGenerationInfo? GetPublicationGeneration(long publicationId)
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT publication_id, scrape_id, status, previous_publication_id,
                   created_at, source_cut_at, ready_at, published_at,
                   failed_at, failure_phase, failure_message
            FROM publication_generations
            WHERE publication_id = @publicationId
            """;
        cmd.Parameters.AddWithValue("publicationId", publicationId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadPublicationGeneration(reader) : null;
    }

    public PublicationGenerationInfo? GetPublicationGenerationForScrape(long scrapeId)
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT publication_id, scrape_id, status, previous_publication_id,
                   created_at, source_cut_at, ready_at, published_at,
                   failed_at, failure_phase, failure_message
            FROM publication_generations
            WHERE scrape_id = @scrapeId
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadPublicationGeneration(reader) : null;
    }

    public PublicationSongCatalogInfo? GetPublicationSongCatalogForScrape(
        long scrapeId)
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                catalog.publication_id,
                generation.scrape_id,
                catalog.catalog_version,
                catalog.schema_version,
                catalog.catalog_json::text,
                catalog.content_hash,
                catalog.song_count,
                catalog.source_captured_at
            FROM publication_generations generation
            JOIN publication_song_catalog catalog
              ON catalog.publication_id = generation.publication_id
            JOIN publication_surface_bindings binding
              ON binding.publication_id = generation.publication_id
             AND binding.surface_name = 'song_catalog'
            WHERE generation.scrape_id = @scrapeId
              AND catalog.is_exact
              AND catalog.source_kind = 'provider_exact'
              AND catalog.schema_version = @schemaVersion
              AND binding.binding_kind = 'generation_catalog_snapshot'
              AND binding.status = 'ready'
              AND binding.row_count = catalog.song_count
              AND binding.content_hash = catalog.content_hash
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        cmd.Parameters.AddWithValue(
            "schemaVersion",
            SongCatalogSnapshotBuilder.SchemaVersion);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new PublicationSongCatalogInfo(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetDateTime(7));
    }

    public PublicationSongCatalogInfo? GetCurrentPublicationSongCatalogFallback(
        long publicationId)
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                catalog.publication_id,
                generation.scrape_id,
                catalog.catalog_version,
                catalog.schema_version,
                catalog.catalog_json::text,
                catalog.content_hash,
                catalog.song_count,
                catalog.source_captured_at
            FROM publication_generations generation
            JOIN publication_song_catalog catalog
              ON catalog.publication_id = generation.publication_id
            JOIN publication_surface_bindings binding
              ON binding.publication_id = generation.publication_id
             AND binding.surface_name = 'song_catalog'
            WHERE generation.publication_id = @publicationId
              AND generation.status = 'current'
              AND binding.row_count = catalog.song_count
              AND binding.content_hash = catalog.content_hash
              AND (
                    (
                        catalog.is_exact
                        AND catalog.source_kind = 'provider_exact'
                        AND catalog.schema_version = @schemaVersion
                        AND binding.binding_kind =
                            'generation_catalog_snapshot'
                        AND binding.status = 'ready'
                    )
                    OR
                    (
                        NOT catalog.is_exact
                        AND catalog.source_kind =
                            'legacy_publication_reconstructed'
                        AND binding.binding_kind =
                            'legacy_reconstructed_catalog'
                        AND binding.status = 'building'
                    )
                  )
            """;
        cmd.Parameters.AddWithValue("publicationId", publicationId);
        cmd.Parameters.AddWithValue(
            "schemaVersion",
            SongCatalogSnapshotBuilder.SchemaVersion);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new PublicationSongCatalogInfo(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetDateTime(7));
    }

    internal (int SchemaVersion, string CatalogJson, int SongCount)?
        GetLiveExactSongCatalogFallback()
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT schema_version, catalog_json::text, song_count
            FROM live_song_catalog
            WHERE id = TRUE
              AND is_exact
              AND source_kind = 'provider_exact'
              AND schema_version = @schemaVersion
            """;
        cmd.Parameters.AddWithValue(
            "schemaVersion",
            SongCatalogSnapshotBuilder.SchemaVersion);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return (
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetInt32(2));
    }

    public IReadOnlyList<PublicationSurfaceBinding> GetPublicationSurfaceBindings(
        long publicationId)
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT publication_id, surface_name, binding_kind,
                   binding_json::text, row_count, content_hash, status, built_at
            FROM publication_surface_bindings
            WHERE publication_id = @publicationId
            ORDER BY surface_name
            """;
        cmd.Parameters.AddWithValue("publicationId", publicationId);

        var bindings = new List<PublicationSurfaceBinding>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            bindings.Add(new PublicationSurfaceBinding(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetDateTime(7)));
        }

        return bindings;
    }

    public PublicationSurfaceSourceEvidence? GetPublicationSurfaceSourceEvidence(
        long publicationId,
        string surfaceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceName);

        return surfaceName switch
        {
            PublicationSurfaceNames.ApiResponseCache =>
                GetPublicationApiResponseCacheEvidence(publicationId),
            PublicationSurfaceNames.BandRankings =>
                GetPublicationBandRankingsEvidence(publicationId),
            PublicationSurfaceNames.SoloScopeSources =>
                GetPublicationSoloScopeSourceEvidence(publicationId),
            PublicationSurfaceNames.SongCatalog =>
                GetPublicationSongCatalogEvidence(publicationId),
            _ => null,
        };
    }

    private PublicationSurfaceSourceEvidence?
        GetPublicationApiResponseCacheEvidence(long publicationId)
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH target AS (
                SELECT publication_id, scrape_id
                FROM publication_generations
                WHERE publication_id = @publicationId
            )
            SELECT
                target.publication_id,
                target.scrape_id,
                COUNT(cache.cache_key),
                md5(COALESCE(
                    string_agg(
                        cache.cache_key || ':' || cache.etag,
                        '|' ORDER BY cache.cache_key),
                    ''))
            FROM target
            LEFT JOIN publication_api_response_cache cache
              ON cache.publication_id = target.publication_id
            GROUP BY target.publication_id, target.scrape_id
            """;
        cmd.Parameters.AddWithValue("publicationId", publicationId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new PublicationSurfaceSourceEvidence(
            PublicationSurfaceNames.ApiResponseCache,
            Exists: true,
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetString(3));
    }

    private PublicationSurfaceSourceEvidence?
        GetPublicationBandRankingsEvidence(long publicationId)
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                generation.publication_id,
                generation.scrape_id,
                publication.current_publication_id =
                    generation.publication_id
                AND publication.band_projection_generation IS NOT NULL
                AND publication.band_projection_generation =
                    projection.current_generation,
                projection.current_generation
            FROM publication_generations generation
            LEFT JOIN scrape_publication_state publication
              ON publication.id = TRUE
            LEFT JOIN band_current_projection_state projection
              ON projection.id = TRUE
            WHERE generation.publication_id = @publicationId
            """;
        cmd.Parameters.AddWithValue("publicationId", publicationId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new PublicationSurfaceSourceEvidence(
            PublicationSurfaceNames.BandRankings,
            Exists: !reader.IsDBNull(2) && reader.GetBoolean(2),
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            RowCount: null,
            ContentHash: null,
            SourceGeneration:
                reader.IsDBNull(3) ? null : reader.GetInt64(3));
    }

    private PublicationSurfaceSourceEvidence?
        GetPublicationSoloScopeSourceEvidence(long publicationId)
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH target AS (
                SELECT publication_id, scrape_id
                FROM publication_generations
                WHERE publication_id = @publicationId
            )
            SELECT
                target.publication_id,
                target.scrape_id,
                COUNT(source.song_id)
            FROM target
            LEFT JOIN leaderboard_published_scope_source source
              ON source.published_scrape_id = target.scrape_id
            GROUP BY target.publication_id, target.scrape_id
            """;
        cmd.Parameters.AddWithValue("publicationId", publicationId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        var rowCount = reader.GetInt64(2);
        return new PublicationSurfaceSourceEvidence(
            PublicationSurfaceNames.SoloScopeSources,
            Exists: rowCount > 0,
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            rowCount,
            ContentHash: null);
    }

    private PublicationSurfaceSourceEvidence?
        GetPublicationSongCatalogEvidence(long publicationId)
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                generation.publication_id,
                generation.scrape_id,
                catalog.song_count,
                catalog.content_hash,
                catalog.is_exact
                    AND catalog.source_kind = 'provider_exact'
                    AND catalog.schema_version = @schemaVersion
            FROM publication_generations generation
            LEFT JOIN publication_song_catalog catalog
              ON catalog.publication_id = generation.publication_id
            WHERE generation.publication_id = @publicationId
            """;
        cmd.Parameters.AddWithValue("publicationId", publicationId);
        cmd.Parameters.AddWithValue(
            "schemaVersion",
            SongCatalogSnapshotBuilder.SchemaVersion);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new PublicationSurfaceSourceEvidence(
            PublicationSurfaceNames.SongCatalog,
            Exists: !reader.IsDBNull(4) && reader.GetBoolean(4),
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }


    public void SetPublicReadFreeze(bool frozen, long? scrapeId = null, string? reason = null)
    {
        if (!frozen
            && reason?.Trim().StartsWith(
                PublicReadFreezeState
                    .MaxScoreMaintenanceReasonPrefix,
                StringComparison.Ordinal) == true)
        {
            throw new InvalidOperationException(
                "Max-score maintenance can release its freeze only through the live maintenance lease's atomic cache publication.");
        }

        PublicReadFreezeWriteTestHook?.Invoke();
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO scrape_publication_state (id, public_reads_frozen, public_reads_frozen_at,
                    public_reads_frozen_scrape_id, public_reads_frozen_reason, updated_at)
                VALUES (TRUE, @frozen, CASE WHEN @frozen THEN @now ELSE NULL END,
                    @scrapeId, @reason, @now)
                ON CONFLICT (id) DO UPDATE SET
                    public_reads_frozen = EXCLUDED.public_reads_frozen,
                    public_reads_frozen_at = CASE
                        WHEN EXCLUDED.public_reads_frozen_reason =
                                @commitIntentReason
                            THEN EXCLUDED.public_reads_frozen_at
                        WHEN scrape_publication_state.public_reads_frozen
                         AND EXCLUDED.public_reads_frozen
                            THEN scrape_publication_state.public_reads_frozen_at
                        ELSE EXCLUDED.public_reads_frozen_at
                    END,
                    public_reads_frozen_scrape_id = CASE
                        WHEN EXCLUDED.public_reads_frozen
                            THEN COALESCE(
                                EXCLUDED.public_reads_frozen_scrape_id,
                                scrape_publication_state.published_scrape_id)
                        ELSE NULL
                    END,
                    public_reads_frozen_reason = EXCLUDED.public_reads_frozen_reason,
                    publication_commit_intent_started_at = NULL,
                    publication_commit_intent_heartbeat_at = NULL,
                    publication_commit_intent_owner = NULL,
                    updated_at = EXCLUDED.updated_at
                WHERE (
                    (
                        COALESCE(
                            scrape_publication_state
                                .public_reads_frozen_reason,
                            '')
                            NOT IN (
                                @commitIntentReason,
                                @failureIsolationPendingReason,
                                @commitDeferredReason)
                        AND COALESCE(
                            scrape_publication_state
                                .public_reads_frozen_reason,
                            '')
                            NOT LIKE @maxScoreMaintenancePattern
                    )
                    OR scrape_publication_state
                        .public_reads_frozen_reason
                        IS NOT DISTINCT FROM
                        EXCLUDED.public_reads_frozen_reason
                    OR (
                        scrape_publication_state
                            .public_reads_frozen_reason =
                            @commitIntentReason
                        AND scrape_publication_state
                            .publication_commit_intent_owner IS NULL
                    )
                )
                """;
            cmd.Parameters.AddWithValue("frozen", frozen);
            cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("scrapeId", scrapeId is null ? DBNull.Value : (int)scrapeId.Value);
            cmd.Parameters.AddWithValue("reason", string.IsNullOrWhiteSpace(reason) ? DBNull.Value : reason.Trim());
            cmd.Parameters.AddWithValue(
                "commitIntentReason",
                PublicReadFreezeState.PublicationCommitIntentReason);
            cmd.Parameters.AddWithValue(
                "failureIsolationPendingReason",
                PublicReadFreezeState
                    .PublicationFailureIsolationPendingReason);
            cmd.Parameters.AddWithValue(
                "commitDeferredReason",
                PublicReadFreezeState.PublicationCommitDeferredReason);
            cmd.Parameters.AddWithValue(
                "maxScoreMaintenancePattern",
                PublicReadFreezeState
                    .MaxScoreMaintenanceReasonPrefix
                + "%");
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public PublicReadFreezeState GetPublicReadFreezeState()
    {
        var injectedFailure =
            PublicReadFreezeReadTestHook?.Invoke();
        if (injectedFailure is not null)
            throw injectedFailure;

        try
        {
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT public_reads_frozen, public_reads_frozen_at, public_reads_frozen_scrape_id,
                    public_reads_frozen_reason
                FROM scrape_publication_state
                WHERE id = TRUE
                """;

            using var r = cmd.ExecuteReader();
            if (!r.Read())
                return PublicReadFreezeState.NotFrozen;

            var frozen = r.GetBoolean(0);
            if (!frozen)
                return PublicReadFreezeState.NotFrozen;

            return new PublicReadFreezeState(
                true,
                r.IsDBNull(1) ? null : r.GetDateTime(1),
                r.IsDBNull(2) ? null : r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetString(3));
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        {
            return PublicReadFreezeState.NotFrozen;
        }
    }

    public PublicReadFreezeState GetFailedCandidateReadIsolationState()
    {
        try
        {
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                WITH publication AS (
                    SELECT published_scrape_id
                    FROM scrape_publication_state
                    WHERE id = TRUE
                )
                SELECT scrape.failed_at, scrape.id
                FROM scrape_log scrape
                CROSS JOIN publication
                WHERE scrape.id > COALESCE(publication.published_scrape_id, 0)
                  AND scrape.status = 'failed'
                  AND scrape.failure_phase = ANY(@failurePhases)
                ORDER BY scrape.id DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue(
                "failurePhases",
                NpgsqlDbType.Array | NpgsqlDbType.Text,
                FailedCandidateReadIsolationFailurePhases);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return PublicReadFreezeState.NotFrozen;

            return new PublicReadFreezeState(
                true,
                reader.IsDBNull(0) ? null : reader.GetDateTime(0),
                reader.GetInt64(1),
                FailedCandidateReadIsolationReason);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        {
            return PublicReadFreezeState.NotFrozen;
        }
    }

    public bool IsBandCurrentProjectionGloballyPublished()
    {
        try
        {
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT publication.band_projection_generation IS NOT NULL
                   AND publication.band_projection_generation = projection.current_generation
                FROM scrape_publication_state publication
                CROSS JOIN band_current_projection_state projection
                WHERE publication.id = TRUE
                  AND projection.id = TRUE
                """;
            return cmd.ExecuteScalar() is bool isPublished && isPublished;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        {
            return false;
        }
    }

    private static void EnsureScrapePublicationStateTable(NpgsqlConnection conn)
    {
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = """
                SELECT to_regclass('public.scrape_publication_state') IS NOT NULL
                   AND to_regclass('public.publication_generations') IS NOT NULL
                   AND to_regclass('public.publication_surface_bindings') IS NOT NULL
                   AND to_regclass('public.live_song_catalog') IS NOT NULL
                   AND to_regclass('public.publication_song_catalog') IS NOT NULL
                   AND to_regclass('public.publication_api_response_cache') IS NOT NULL
                   AND to_regclass('public.publication_api_response_cache_staging') IS NOT NULL
                   AND (
                       SELECT COUNT(*) = 4
                       FROM information_schema.columns
                       WHERE table_schema = 'public'
                         AND table_name = 'live_song_catalog'
                         AND column_name IN (
                             'catalog_version',
                             'schema_version',
                             'source_kind',
                             'is_exact')
                   )
                   AND (
                       SELECT COUNT(*) = 4
                       FROM information_schema.columns
                       WHERE table_schema = 'public'
                         AND table_name = 'publication_song_catalog'
                         AND column_name IN (
                             'catalog_version',
                             'schema_version',
                             'source_kind',
                             'is_exact')
                   )
                   AND (
                       SELECT COUNT(*) = 20
                       FROM information_schema.columns
                       WHERE table_schema = 'public'
                         AND table_name = 'scrape_publication_state'
                         AND column_name IN (
                             'public_reads_frozen',
                             'public_reads_frozen_at',
                             'public_reads_frozen_scrape_id',
                             'public_reads_frozen_reason',
                             'publication_commit_intent_started_at',
                             'publication_commit_intent_heartbeat_at',
                             'publication_commit_intent_owner',
                             'band_projection_generation',
                             'improvement_notifications_scrape_id',
                             'improvement_notifications_status',
                             'improvement_notifications_attempt_count',
                             'improvement_notifications_started_at',
                             'improvement_notifications_completed_at',
                             'improvement_notifications_error',
                             'improvement_notifications_projection_scopes',
                             'improvement_notifications_projection_ready',
                             'improvement_notifications_projection_scrape_id',
                             'current_publication_id',
                             'previous_publication_id',
                             'working_publication_id')
                   )
                """;
            if (Convert.ToBoolean(probe.ExecuteScalar()))
                return;
        }

        using var tx = conn.BeginTransaction();
        using (var timeout = conn.CreateCommand())
        {
            timeout.Transaction = tx;
            timeout.CommandText = "SET LOCAL lock_timeout = '5s'";
            timeout.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS scrape_publication_state (
                id                  BOOLEAN     PRIMARY KEY DEFAULT TRUE CHECK (id),
                published_scrape_id INTEGER     REFERENCES scrape_log(id),
                published_at        TIMESTAMPTZ,
                public_reads_frozen BOOLEAN     NOT NULL DEFAULT FALSE,
                public_reads_frozen_at TIMESTAMPTZ,
                public_reads_frozen_scrape_id INTEGER REFERENCES scrape_log(id),
                public_reads_frozen_reason TEXT,
                publication_commit_intent_started_at TIMESTAMPTZ,
                publication_commit_intent_heartbeat_at TIMESTAMPTZ,
                publication_commit_intent_owner TEXT,
                band_projection_generation BIGINT,
                max_score_mutation_gate_token TEXT,
                max_score_mutation_gate_publication_id BIGINT,
                max_score_mutation_gate_backend_pid INTEGER,
                max_score_mutation_gate_backend_start TIMESTAMPTZ,
                max_score_mutation_gate_acquired_at TIMESTAMPTZ,
                improvement_notifications_scrape_id INTEGER REFERENCES scrape_log(id),
                improvement_notifications_status TEXT,
                improvement_notifications_attempt_count INTEGER NOT NULL DEFAULT 0,
                improvement_notifications_started_at TIMESTAMPTZ,
                improvement_notifications_completed_at TIMESTAMPTZ,
                improvement_notifications_error TEXT,
                improvement_notifications_projection_scopes JSONB NOT NULL DEFAULT '[]'::jsonb,
                improvement_notifications_projection_ready BOOLEAN NOT NULL DEFAULT FALSE,
                improvement_notifications_projection_scrape_id INTEGER REFERENCES scrape_log(id),
                updated_at          TIMESTAMPTZ NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();

        using var alter = conn.CreateCommand();
        alter.Transaction = tx;
        alter.CommandText = """
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS public_reads_frozen BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS public_reads_frozen_at TIMESTAMPTZ;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS public_reads_frozen_scrape_id INTEGER REFERENCES scrape_log(id);
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS public_reads_frozen_reason TEXT;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS publication_commit_intent_started_at TIMESTAMPTZ;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS publication_commit_intent_heartbeat_at TIMESTAMPTZ;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS publication_commit_intent_owner TEXT;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS band_projection_generation BIGINT;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS max_score_mutation_gate_token TEXT;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS max_score_mutation_gate_publication_id BIGINT;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS max_score_mutation_gate_backend_pid INTEGER;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS max_score_mutation_gate_backend_start TIMESTAMPTZ;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS max_score_mutation_gate_acquired_at TIMESTAMPTZ;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS improvement_notifications_scrape_id INTEGER REFERENCES scrape_log(id);
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS improvement_notifications_status TEXT;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS improvement_notifications_attempt_count INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS improvement_notifications_started_at TIMESTAMPTZ;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS improvement_notifications_completed_at TIMESTAMPTZ;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS improvement_notifications_error TEXT;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS improvement_notifications_projection_scopes JSONB NOT NULL DEFAULT '[]'::jsonb;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS improvement_notifications_projection_ready BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE scrape_publication_state ADD COLUMN IF NOT EXISTS improvement_notifications_projection_scrape_id INTEGER REFERENCES scrape_log(id);

            UPDATE scrape_publication_state
            SET improvement_notifications_projection_scopes = '[]'::jsonb,
                improvement_notifications_projection_ready = true,
                improvement_notifications_projection_scrape_id = published_scrape_id
            WHERE improvement_notifications_status = 'completed'
              AND improvement_notifications_scrape_id = published_scrape_id
              AND (
                  NOT improvement_notifications_projection_ready
                  OR improvement_notifications_projection_scrape_id IS DISTINCT FROM published_scrape_id
              );

            UPDATE scrape_publication_state
            SET improvement_notifications_scrape_id = NULL,
                improvement_notifications_projection_scopes = '[]'::jsonb,
                improvement_notifications_projection_ready = false,
                improvement_notifications_projection_scrape_id = NULL
            WHERE improvement_notifications_status = 'disabled';

            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conrelid = 'scrape_publication_state'::regclass
                      AND conname = 'ck_scrape_publication_notification_plan'
                ) THEN
                    ALTER TABLE scrape_publication_state
                    ADD CONSTRAINT ck_scrape_publication_notification_plan
                    CHECK (
                        improvement_notifications_status IS NULL
                        OR (
                            improvement_notifications_status = 'disabled'
                            AND improvement_notifications_scrape_id IS NULL
                            AND NOT improvement_notifications_projection_ready
                            AND improvement_notifications_projection_scrape_id IS NULL
                        )
                        OR (
                            improvement_notifications_status IN ('pending', 'running', 'failed', 'completed')
                            AND published_scrape_id IS NOT NULL
                            AND improvement_notifications_scrape_id IS NOT NULL
                            AND improvement_notifications_projection_scrape_id IS NOT NULL
                            AND improvement_notifications_scrape_id = published_scrape_id
                            AND improvement_notifications_projection_ready
                            AND improvement_notifications_projection_scrape_id = published_scrape_id
                        )
                    ) NOT VALID;
                END IF;
            END $$;
            """;
        alter.ExecuteNonQuery();

        using var publicationGenerations = conn.CreateCommand();
        publicationGenerations.Transaction = tx;
        publicationGenerations.CommandText = PublicationGenerationSchema.Sql;
        publicationGenerations.ExecuteNonQuery();
        tx.Commit();
    }

    private static ScrapeRunInfo? ReadScrapeRunInfo(NpgsqlDataReader r)
    {
        if (!r.Read()) return null;
        return ReadScrapeRunInfo(r, 0);
    }

    private static PublicationGenerationInfo ReadPublicationGeneration(
        NpgsqlDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.GetDateTime(4),
            reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            reader.IsDBNull(7) ? null : reader.GetDateTime(7),
            reader.IsDBNull(8) ? null : reader.GetDateTime(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));

    private static bool CatalogTokensMatch(
        SongCatalogPersistenceToken expected,
        SongCatalogPersistenceToken actual) =>
        expected.CatalogVersion == actual.CatalogVersion
        && expected.SchemaVersion == actual.SchemaVersion
        && expected.SongCount == actual.SongCount
        && string.Equals(
            expected.ContentHash,
            actual.ContentHash,
            StringComparison.Ordinal);

    private static ScrapeRunInfo? ReadScrapeRunInfo(NpgsqlDataReader r, int startOrdinal)
    {
        if (r.IsDBNull(startOrdinal))
            return null;

        return new ScrapeRunInfo
        {
            Id = Convert.ToInt64(r.GetValue(startOrdinal)),
            StartedAt = r.GetDateTime(startOrdinal + 1).ToString("o"),
            CompletedAt = r.IsDBNull(startOrdinal + 2) ? null : r.GetDateTime(startOrdinal + 2).ToString("o"),
            SongsScraped = r.IsDBNull(startOrdinal + 3) ? 0 : r.GetInt32(startOrdinal + 3),
            TotalEntries = r.IsDBNull(startOrdinal + 4) ? 0 : r.GetInt32(startOrdinal + 4),
            TotalRequests = r.IsDBNull(startOrdinal + 5) ? 0 : r.GetInt32(startOrdinal + 5),
            TotalBytes = r.IsDBNull(startOrdinal + 6) ? 0 : r.GetInt64(startOrdinal + 6),
            EpicReportedOver100Pages = !r.IsDBNull(startOrdinal + 7) && r.GetBoolean(startOrdinal + 7),
            Status = r.IsDBNull(startOrdinal + 8) ? "running" : r.GetString(startOrdinal + 8),
            FailedAt = r.IsDBNull(startOrdinal + 9) ? null : r.GetDateTime(startOrdinal + 9).ToString("o"),
            FailurePhase = r.IsDBNull(startOrdinal + 10) ? null : r.GetString(startOrdinal + 10),
            FailureMessage = r.IsDBNull(startOrdinal + 11) ? null : r.GetString(startOrdinal + 11),
            BestEffortFailureCount = r.IsDBNull(startOrdinal + 12) ? 0 : r.GetInt32(startOrdinal + 12),
            BestEffortFailedPhases = r.IsDBNull(startOrdinal + 13)
                ? []
                : r.GetFieldValue<string[]>(startOrdinal + 13),
        };
    }

    public bool ShouldShowLeaderboardEntryTotals()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(
                (
                    SELECT scrape.epic_reported_over_100_pages
                    FROM scrape_publication_state publication
                    JOIN scrape_log scrape ON scrape.id = publication.published_scrape_id
                    WHERE publication.id = TRUE
                      AND scrape.completed_at IS NOT NULL
                ),
                (
                    SELECT scrape.epic_reported_over_100_pages
                    FROM scrape_log scrape
                    WHERE scrape.completed_at IS NOT NULL
                      AND scrape.status = 'completed'
                    ORDER BY scrape.id DESC
                    LIMIT 1
                )
            )
            """;
        var result = cmd.ExecuteScalar();
        return result is bool value && value;
    }

    public void RecordScrapePhaseTiming(ScrapePhaseTimingRecord timing)
    {
        if (timing.ScrapeId <= 0 || string.IsNullOrWhiteSpace(timing.Phase))
            return;

        try
        {
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO scrape_phase_timings (
                    scrape_id, phase, subphase, item_key, started_at, completed_at, duration_ms,
                    rows_read, rows_written, rows_deleted, scope_count, success, error_message)
                VALUES (
                    @scrapeId, @phase, @subphase, @itemKey, @startedAt, @completedAt, @durationMs,
                    @rowsRead, @rowsWritten, @rowsDeleted, @scopeCount, @success, @errorMessage)
                """;
            cmd.Parameters.AddWithValue("scrapeId", timing.ScrapeId);
            cmd.Parameters.AddWithValue("phase", timing.Phase);
            cmd.Parameters.AddWithValue("subphase", (object?)timing.Subphase ?? DBNull.Value);
            cmd.Parameters.AddWithValue("itemKey", (object?)timing.ItemKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("startedAt", timing.StartedAtUtc);
            cmd.Parameters.AddWithValue("completedAt", timing.CompletedAtUtc);
            cmd.Parameters.AddWithValue("durationMs", timing.DurationMs);
            cmd.Parameters.AddWithValue("rowsRead", (object?)timing.RowsRead ?? DBNull.Value);
            cmd.Parameters.AddWithValue("rowsWritten", (object?)timing.RowsWritten ?? DBNull.Value);
            cmd.Parameters.AddWithValue("rowsDeleted", (object?)timing.RowsDeleted ?? DBNull.Value);
            cmd.Parameters.AddWithValue("scopeCount", (object?)timing.ScopeCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("success", timing.Success);
            cmd.Parameters.AddWithValue("errorMessage", (object?)timing.ErrorMessage ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to record scrape phase timing for {Phase}. Continuing.", timing.Phase);
        }
    }

    // ── Worker status ────────────────────────────────────────────────

    public void UpsertWorkerHeartbeat(string workerKey, string status, string mode, string instanceId,
        DateTime startedAtUtc, DateTime heartbeatAtUtc, string? message = null,
        WorkerOperationInfo? currentOperation = null)
    {
        if (string.IsNullOrWhiteSpace(workerKey))
            throw new ArgumentException("Worker key is required.", nameof(workerKey));

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO service_worker_status (
                worker_key, status, mode, instance_id, started_at, last_heartbeat_at,
                last_status_change_at, message, current_operation_json, updated_at)
            VALUES (@workerKey, @status, @mode, @instanceId, @startedAt, @heartbeatAt,
                @changedAt, @message, @currentOperation, @updatedAt)
            ON CONFLICT (worker_key) DO UPDATE SET
                status = EXCLUDED.status,
                mode = EXCLUDED.mode,
                instance_id = EXCLUDED.instance_id,
                started_at = CASE
                    WHEN service_worker_status.instance_id IS DISTINCT FROM EXCLUDED.instance_id THEN EXCLUDED.started_at
                    ELSE COALESCE(service_worker_status.started_at, EXCLUDED.started_at)
                END,
                last_heartbeat_at = EXCLUDED.last_heartbeat_at,
                last_status_change_at = CASE
                    WHEN service_worker_status.status IS DISTINCT FROM EXCLUDED.status THEN EXCLUDED.last_status_change_at
                    ELSE service_worker_status.last_status_change_at
                END,
                message = COALESCE(EXCLUDED.message, service_worker_status.message),
                current_operation_json = CASE
                    WHEN service_worker_status.instance_id IS DISTINCT FROM EXCLUDED.instance_id
                        THEN EXCLUDED.current_operation_json
                    ELSE COALESCE(EXCLUDED.current_operation_json, service_worker_status.current_operation_json)
                END,
                updated_at = EXCLUDED.updated_at
            """;
        cmd.Parameters.AddWithValue("workerKey", workerKey);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("mode", mode);
        cmd.Parameters.AddWithValue("instanceId", instanceId);
        cmd.Parameters.AddWithValue("startedAt", NormalizeUtc(startedAtUtc));
        cmd.Parameters.AddWithValue("heartbeatAt", NormalizeUtc(heartbeatAtUtc));
        cmd.Parameters.AddWithValue("changedAt", NormalizeUtc(heartbeatAtUtc));
        cmd.Parameters.AddWithValue("message", (object?)message ?? DBNull.Value);
        AddJsonbParameter(cmd, "currentOperation", currentOperation);
        cmd.Parameters.AddWithValue("updatedAt", NormalizeUtc(heartbeatAtUtc));
        cmd.ExecuteNonQuery();
    }

    public void UpdateWorkerActivity(string workerKey, WorkerOperationInfo? currentOperation,
        WorkerOperationInfo? lastOperation = null, string? status = null, string? message = null,
        DateTime? updatedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(workerKey))
            throw new ArgumentException("Worker key is required.", nameof(workerKey));

        var now = NormalizeUtc(updatedAtUtc ?? DateTime.UtcNow);

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO service_worker_status (
                worker_key, status, last_status_change_at, message,
                current_operation_json, last_operation_json, updated_at)
            VALUES (@workerKey, COALESCE(@status, 'running'), @changedAt, @message,
                @currentOperation, @lastOperation, @updatedAt)
            ON CONFLICT (worker_key) DO UPDATE SET
                status = COALESCE(EXCLUDED.status, service_worker_status.status),
                last_status_change_at = CASE
                    WHEN EXCLUDED.status IS NOT NULL
                     AND service_worker_status.status IS DISTINCT FROM EXCLUDED.status THEN EXCLUDED.last_status_change_at
                    ELSE service_worker_status.last_status_change_at
                END,
                message = COALESCE(EXCLUDED.message, service_worker_status.message),
                current_operation_json = EXCLUDED.current_operation_json,
                last_operation_json = COALESCE(EXCLUDED.last_operation_json, service_worker_status.last_operation_json),
                updated_at = EXCLUDED.updated_at
            """;
        cmd.Parameters.AddWithValue("workerKey", workerKey);
        cmd.Parameters.AddWithValue("status", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("changedAt", now);
        cmd.Parameters.AddWithValue("message", (object?)message ?? DBNull.Value);
        AddJsonbParameter(cmd, "currentOperation", currentOperation);
        AddJsonbParameter(cmd, "lastOperation", lastOperation);
        cmd.Parameters.AddWithValue("updatedAt", now);
        cmd.ExecuteNonQuery();
    }

    public WorkerStatusInfo? GetWorkerStatus(string workerKey)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT worker_key, status, mode, instance_id, started_at, last_heartbeat_at,
                   last_status_change_at, message, current_operation_json, last_operation_json
            FROM service_worker_status
            WHERE worker_key = @workerKey
            """;
        cmd.Parameters.AddWithValue("workerKey", workerKey);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new WorkerStatusInfo
        {
            WorkerKey = reader.GetString(0),
            Status = reader.GetString(1),
            Mode = reader.IsDBNull(2) ? null : reader.GetString(2),
            InstanceId = reader.IsDBNull(3) ? null : reader.GetString(3),
            StartedAtUtc = GetNullableUtc(reader, 4),
            LastHeartbeatAtUtc = GetNullableUtc(reader, 5),
            LastStatusChangeAtUtc = GetUtc(reader, 6),
            Message = reader.IsDBNull(7) ? null : reader.GetString(7),
            CurrentOperation = DeserializeOperation(reader, 8),
            LastOperation = DeserializeOperation(reader, 9),
        };
    }

    public ServiceRuntimeState GetServiceRuntimeState(
        string workerKey,
        int commandTimeoutSeconds = 0)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        if (commandTimeoutSeconds > 0)
            cmd.CommandTimeout = commandTimeoutSeconds;
        cmd.CommandText = """
            WITH latest_scrape AS (
                SELECT id, started_at, completed_at, songs_scraped, total_entries,
                       total_requests, total_bytes, epic_reported_over_100_pages,
                       status, failed_at, failure_phase, failure_message,
                       best_effort_failure_count, best_effort_failed_phases
                FROM scrape_log
                ORDER BY id DESC
                LIMIT 1
            ),
            publication AS (
                SELECT published_scrape_id, published_at, public_reads_frozen,
                       public_reads_frozen_at, public_reads_frozen_scrape_id,
                       public_reads_frozen_reason
                FROM scrape_publication_state
                WHERE id = TRUE
            ),
            published_scrape AS (
                SELECT scrape.id, scrape.started_at, scrape.completed_at, scrape.songs_scraped,
                       scrape.total_entries, scrape.total_requests, scrape.total_bytes,
                       scrape.epic_reported_over_100_pages, scrape.status,
                       scrape.failed_at, scrape.failure_phase, scrape.failure_message,
                       scrape.best_effort_failure_count, scrape.best_effort_failed_phases
                FROM publication
                JOIN scrape_log scrape ON scrape.id = publication.published_scrape_id
                WHERE scrape.completed_at IS NOT NULL
                  AND scrape.status = 'completed'
                UNION ALL
                SELECT scrape.id, scrape.started_at, scrape.completed_at, scrape.songs_scraped,
                       scrape.total_entries, scrape.total_requests, scrape.total_bytes,
                       scrape.epic_reported_over_100_pages, scrape.status,
                       scrape.failed_at, scrape.failure_phase, scrape.failure_message,
                       scrape.best_effort_failure_count, scrape.best_effort_failed_phases
                FROM scrape_log scrape
                WHERE scrape.completed_at IS NOT NULL
                  AND scrape.status = 'completed'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM publication
                      WHERE published_scrape_id IS NOT NULL
                  )
                ORDER BY id DESC
                LIMIT 1
            ),
            active_attempt AS (
                SELECT attempt.*
                FROM scrape_phase_attempts attempt
                JOIN latest_scrape latest
                  ON latest.id = attempt.scrape_id
                WHERE attempt.status = 'running'
                ORDER BY attempt.last_progress_at DESC,
                         attempt.phase_ordinal DESC,
                         attempt.attempt DESC
                LIMIT 1
            )
            SELECT
                latest.id, latest.started_at, latest.completed_at, latest.songs_scraped,
                latest.total_entries, latest.total_requests, latest.total_bytes,
                latest.epic_reported_over_100_pages, latest.status, latest.failed_at,
                latest.failure_phase, latest.failure_message,
                latest.best_effort_failure_count, latest.best_effort_failed_phases,
                published.id, published.started_at, published.completed_at, published.songs_scraped,
                published.total_entries, published.total_requests, published.total_bytes,
                published.epic_reported_over_100_pages, published.status, published.failed_at,
                published.failure_phase, published.failure_message,
                published.best_effort_failure_count, published.best_effort_failed_phases,
                publication.published_at,
                COALESCE(publication.public_reads_frozen, FALSE),
                publication.public_reads_frozen_at,
                CASE
                    WHEN COALESCE(publication.public_reads_frozen, FALSE)
                    THEN COALESCE(publication.public_reads_frozen_scrape_id, published.id)
                    ELSE NULL
                END,
                publication.public_reads_frozen_reason,
                worker.worker_key, worker.status, worker.mode, worker.instance_id, worker.started_at,
                worker.last_heartbeat_at, worker.last_status_change_at, worker.message,
                worker.current_operation_json, worker.last_operation_json,
                attempt.scrape_id, attempt.phase_id, attempt.attempt,
                attempt.operation_id, attempt.phase_ordinal, attempt.plan_version,
                attempt.worker_instance_id, attempt.current_subphase_id,
                attempt.status, attempt.units_kind, attempt.units_completed,
                attempt.units_total, attempt.units_total_final,
                attempt.phase_percent, attempt.overall_percent_kind,
                attempt.overall_percent, attempt.overall_model_version,
                attempt.eta_lower_seconds, attempt.eta_upper_seconds,
                attempt.eta_confidence, attempt.eta_sample_count,
                attempt.started_at, attempt.last_progress_at,
                attempt.heartbeat_at, attempt.completed_at,
                attempt.build_id, attempt.config_id,
                attempt.warning_message, attempt.error_message
            FROM (SELECT TRUE) singleton
            LEFT JOIN latest_scrape latest ON TRUE
            LEFT JOIN publication ON TRUE
            LEFT JOIN published_scrape published ON TRUE
            LEFT JOIN service_worker_status worker ON worker.worker_key = @workerKey
            LEFT JOIN active_attempt attempt ON TRUE
            """;
        cmd.Parameters.AddWithValue("workerKey", workerKey);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return new ServiceRuntimeState();

        var frozen = reader.GetBoolean(29);
        WorkerStatusInfo? workerStatus = null;
        if (!reader.IsDBNull(33))
        {
            workerStatus = new WorkerStatusInfo
            {
                WorkerKey = reader.GetString(33),
                Status = reader.GetString(34),
                Mode = reader.IsDBNull(35) ? null : reader.GetString(35),
                InstanceId = reader.IsDBNull(36) ? null : reader.GetString(36),
                StartedAtUtc = GetNullableUtc(reader, 37),
                LastHeartbeatAtUtc = GetNullableUtc(reader, 38),
                LastStatusChangeAtUtc = GetUtc(reader, 39),
                Message = reader.IsDBNull(40) ? null : reader.GetString(40),
                CurrentOperation = DeserializeOperation(reader, 41),
                LastOperation = DeserializeOperation(reader, 42),
            };
        }

        return new ServiceRuntimeState
        {
            LatestScrape = ReadScrapeRunInfo(reader, 0),
            PublishedScrape = ReadScrapeRunInfo(reader, 14),
            PublishedAtUtc = GetNullableUtc(reader, 28),
            PublicReadFreeze = frozen
                ? new PublicReadFreezeState(
                    true,
                    GetNullableUtc(reader, 30),
                    reader.IsDBNull(31) ? null : Convert.ToInt64(reader.GetValue(31)),
                    reader.IsDBNull(32) ? null : reader.GetString(32))
                : PublicReadFreezeState.NotFrozen,
            WorkerStatus = workerStatus,
            CurrentPhaseAttempt = ReadScrapePhaseAttempt(reader, 43),
        };
    }

    private static void AddJsonbParameter(NpgsqlCommand cmd, string name, WorkerOperationInfo? operation)
    {
        var parameter = cmd.Parameters.Add(name, NpgsqlDbType.Jsonb);
        parameter.Value = operation is null
            ? DBNull.Value
            : JsonSerializer.Serialize(
                operation,
                WorkerOperationJsonOptions);
    }

    private static WorkerOperationInfo? DeserializeOperation(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        return JsonSerializer.Deserialize<WorkerOperationInfo>(reader.GetString(ordinal));
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    private static DateTime GetUtc(NpgsqlDataReader reader, int ordinal)
        => NormalizeUtc(reader.GetDateTime(ordinal));

    private static DateTime? GetNullableUtc(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : GetUtc(reader, ordinal);

    // ── Score history ────────────────────────────────────────────────

    public void InsertScoreChange(string songId, string instrument, string accountId,
        int? oldScore, int newScore, int? oldRank, int newRank,
        int? accuracy = null, bool? isFullCombo = null, int? stars = null,
        double? percentile = null, int? season = null, string? scoreAchievedAt = null,
        int? seasonRank = null, int? allTimeRank = null, int? difficulty = null)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        var parsedScoreAchievedAt = scoreAchievedAt is not null ? ParseUtc(scoreAchievedAt) : (DateTime?)null;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO score_history (song_id, instrument, account_id, old_score, new_score, old_rank, new_rank, accuracy, is_full_combo, stars, percentile, season, score_achieved_at, season_rank, all_time_rank, difficulty, changed_at) " +
            "VALUES (@songId, @instrument, @accountId, @oldScore, @newScore, @oldRank, @newRank, @accuracy, @isFullCombo, @stars, @percentile, @season, @scoreAchievedAt, @seasonRank, @allTimeRank, @difficulty, @now) " +
            "ON CONFLICT (account_id, song_id, instrument, new_score, score_achieved_at) DO UPDATE SET " +
            "season_rank = COALESCE(EXCLUDED.season_rank, score_history.season_rank), all_time_rank = COALESCE(EXCLUDED.all_time_rank, score_history.all_time_rank), " +
            "season = COALESCE(score_history.season, EXCLUDED.season), difficulty = COALESCE(score_history.difficulty, EXCLUDED.difficulty)";
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("oldScore", (object?)oldScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("newScore", newScore);
        cmd.Parameters.AddWithValue("oldRank", (object?)oldRank ?? DBNull.Value);
        cmd.Parameters.AddWithValue("newRank", newRank);
        cmd.Parameters.AddWithValue("accuracy", (object?)accuracy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("isFullCombo", (object?)isFullCombo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("stars", (object?)stars ?? DBNull.Value);
        cmd.Parameters.AddWithValue("percentile", (object?)percentile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("season", (object?)season ?? DBNull.Value);
        cmd.Parameters.AddWithValue("scoreAchievedAt", parsedScoreAchievedAt.HasValue ? parsedScoreAchievedAt.Value : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("seasonRank", (object?)seasonRank ?? DBNull.Value);
        cmd.Parameters.AddWithValue("allTimeRank", (object?)allTimeRank ?? DBNull.Value);
        cmd.Parameters.AddWithValue("difficulty", (object?)difficulty ?? DBNull.Value);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();

        tx.Commit();
    }

    public void BackfillScoreHistoryDifficulty(string accountId, string songId, string instrument, int score, int difficulty)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE score_history SET difficulty = @difficulty WHERE account_id = @accountId AND song_id = @songId AND instrument = @instrument AND new_score = @score AND difficulty IS NULL";
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("difficulty", difficulty);
        cmd.ExecuteNonQuery();
    }

    public int InsertScoreChanges(IReadOnlyList<ScoreChangeRecord> changes)
    {
        if (changes.Count == 0) return 0;
        var now = DateTime.UtcNow;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        // Use COPY + merge for larger batches
        if (changes.Count > 20)
        {
            using (var c = conn.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText =
                    "CREATE TEMP TABLE _sh_staging (" +
                    "song_id TEXT, instrument TEXT, account_id TEXT, old_score INTEGER, new_score INTEGER, " +
                    "old_rank INTEGER, new_rank INTEGER, accuracy INTEGER, is_full_combo BOOLEAN, " +
                    "stars INTEGER, percentile DOUBLE PRECISION, season INTEGER, " +
                    "score_achieved_at TIMESTAMPTZ, season_rank INTEGER, all_time_rank INTEGER, " +
                    "difficulty INTEGER, changed_at TIMESTAMPTZ" +
                    ") ON COMMIT DROP";
                c.ExecuteNonQuery();
            }

            using (var writer = conn.BeginBinaryImport(
                "COPY _sh_staging (song_id, instrument, account_id, old_score, new_score, old_rank, new_rank, " +
                "accuracy, is_full_combo, stars, percentile, season, score_achieved_at, season_rank, " +
                "all_time_rank, difficulty, changed_at) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var c in changes)
                {
                    writer.StartRow();
                    writer.Write(c.SongId, NpgsqlDbType.Text);
                    writer.Write(c.Instrument, NpgsqlDbType.Text);
                    writer.Write(c.AccountId, NpgsqlDbType.Text);
                    WriteNullableInt(writer, c.OldScore);
                    writer.Write(c.NewScore, NpgsqlDbType.Integer);
                    WriteNullableInt(writer, c.OldRank);
                    writer.Write(c.NewRank, NpgsqlDbType.Integer);
                    WriteNullableInt(writer, c.Accuracy);
                    if (c.IsFullCombo.HasValue) writer.Write(c.IsFullCombo.Value, NpgsqlDbType.Boolean);
                    else writer.WriteNull();
                    WriteNullableInt(writer, c.Stars);
                    if (c.Percentile.HasValue) writer.Write(c.Percentile.Value, NpgsqlDbType.Double);
                    else writer.WriteNull();
                    WriteNullableInt(writer, c.Season);
                    if (c.ScoreAchievedAt is not null) writer.Write(ParseUtc(c.ScoreAchievedAt), NpgsqlDbType.TimestampTz);
                    else writer.WriteNull();
                    WriteNullableInt(writer, c.SeasonRank);
                    WriteNullableInt(writer, c.AllTimeRank);
                    WriteNullableInt(writer, c.Difficulty);
                    writer.Write(now, NpgsqlDbType.TimestampTz);
                }
                writer.Complete();
            }

            int inserted;
            using (var c = conn.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandTimeout = 0;
                c.CommandText = """
                    WITH source_rows AS (
                        SELECT
                            song_id,
                            instrument,
                            account_id,
                            (ARRAY_AGG(old_score ORDER BY (old_score IS NULL), changed_at DESC))[1] AS old_score,
                            new_score,
                            (ARRAY_AGG(old_rank ORDER BY (old_rank IS NULL), changed_at DESC))[1] AS old_rank,
                            COALESCE(
                                MIN(all_time_rank) FILTER (WHERE all_time_rank IS NOT NULL),
                                MIN(season_rank) FILTER (WHERE season_rank IS NOT NULL),
                                MIN(new_rank)
                            ) AS new_rank,
                            MAX(accuracy) AS accuracy,
                            BOOL_OR(is_full_combo) FILTER (WHERE is_full_combo IS NOT NULL) AS is_full_combo,
                            MAX(stars) AS stars,
                            MAX(percentile) AS percentile,
                            MIN(season) FILTER (WHERE season IS NOT NULL) AS season,
                            score_achieved_at,
                            MIN(season_rank) FILTER (WHERE season_rank IS NOT NULL) AS season_rank,
                            MIN(all_time_rank) FILTER (WHERE all_time_rank IS NOT NULL) AS all_time_rank,
                            MAX(difficulty) AS difficulty,
                            MAX(changed_at) AS changed_at
                        FROM _sh_staging
                        GROUP BY song_id, instrument, account_id, new_score, score_achieved_at
                    )
                    INSERT INTO score_history (song_id, instrument, account_id, old_score, new_score, old_rank, new_rank, accuracy, is_full_combo, stars, percentile, season, score_achieved_at, season_rank, all_time_rank, difficulty, changed_at)
                    SELECT song_id, instrument, account_id, old_score, new_score, old_rank, new_rank, accuracy, is_full_combo, stars, percentile, season, score_achieved_at, season_rank, all_time_rank, difficulty, changed_at FROM source_rows
                    ON CONFLICT(account_id, song_id, instrument, new_score, score_achieved_at) DO UPDATE SET
                    season_rank = COALESCE(EXCLUDED.season_rank, score_history.season_rank), all_time_rank = COALESCE(EXCLUDED.all_time_rank, score_history.all_time_rank),
                    old_score = COALESCE(EXCLUDED.old_score, score_history.old_score), old_rank = COALESCE(EXCLUDED.old_rank, score_history.old_rank),
                    season = COALESCE(score_history.season, EXCLUDED.season),
                    difficulty = COALESCE(score_history.difficulty, EXCLUDED.difficulty), changed_at = EXCLUDED.changed_at
                    """;
                inserted = c.ExecuteNonQuery();
            }
            tx.Commit();
            return inserted;
        }

        // Small batch: prepared-statement loop
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO score_history (song_id, instrument, account_id, old_score, new_score, old_rank, new_rank, accuracy, is_full_combo, stars, percentile, season, score_achieved_at, season_rank, all_time_rank, difficulty, changed_at) " +
            "VALUES (@songId, @instrument, @accountId, @oldScore, @newScore, @oldRank, @newRank, @accuracy, @fc, @stars, @percentile, @season, @scoreAchievedAt, @seasonRank, @allTimeRank, @difficulty, @now) " +
            "ON CONFLICT(account_id, song_id, instrument, new_score, score_achieved_at) DO UPDATE SET " +
            "season_rank = COALESCE(EXCLUDED.season_rank, score_history.season_rank), all_time_rank = COALESCE(EXCLUDED.all_time_rank, score_history.all_time_rank), " +
            "old_score = COALESCE(EXCLUDED.old_score, score_history.old_score), old_rank = COALESCE(EXCLUDED.old_rank, score_history.old_rank), " +
            "season = COALESCE(score_history.season, EXCLUDED.season), " +
            "difficulty = COALESCE(score_history.difficulty, EXCLUDED.difficulty), changed_at = EXCLUDED.changed_at";
        var pSongId = cmd.Parameters.Add("songId", NpgsqlDbType.Text);
        var pInstrument = cmd.Parameters.Add("instrument", NpgsqlDbType.Text);
        var pAccountId = cmd.Parameters.Add("accountId", NpgsqlDbType.Text);
        var pOldScore = cmd.Parameters.Add("oldScore", NpgsqlDbType.Integer);
        var pNewScore = cmd.Parameters.Add("newScore", NpgsqlDbType.Integer);
        var pOldRank = cmd.Parameters.Add("oldRank", NpgsqlDbType.Integer);
        var pNewRank = cmd.Parameters.Add("newRank", NpgsqlDbType.Integer);
        var pAccuracy = cmd.Parameters.Add("accuracy", NpgsqlDbType.Integer);
        var pFc = cmd.Parameters.Add("fc", NpgsqlDbType.Boolean);
        var pStars = cmd.Parameters.Add("stars", NpgsqlDbType.Integer);
        var pPercentile = cmd.Parameters.Add("percentile", NpgsqlDbType.Double);
        var pSeason = cmd.Parameters.Add("season", NpgsqlDbType.Integer);
        var pScoreAchievedAt = cmd.Parameters.Add("scoreAchievedAt", NpgsqlDbType.TimestampTz);
        var pSeasonRank = cmd.Parameters.Add("seasonRank", NpgsqlDbType.Integer);
        var pAllTimeRank = cmd.Parameters.Add("allTimeRank", NpgsqlDbType.Integer);
        var pDifficulty = cmd.Parameters.Add("difficulty", NpgsqlDbType.Integer);
        var pNow = cmd.Parameters.Add("now", NpgsqlDbType.TimestampTz);
        cmd.Prepare();
        int loopInserted = 0;
        foreach (var c in changes)
        {
            pSongId.Value = c.SongId; pInstrument.Value = c.Instrument; pAccountId.Value = c.AccountId;
            pOldScore.Value = c.OldScore.HasValue ? c.OldScore.Value : DBNull.Value;
            pNewScore.Value = c.NewScore;
            pOldRank.Value = c.OldRank.HasValue ? c.OldRank.Value : DBNull.Value;
            pNewRank.Value = c.NewRank;
            pAccuracy.Value = c.Accuracy.HasValue ? c.Accuracy.Value : DBNull.Value;
            pFc.Value = c.IsFullCombo.HasValue ? c.IsFullCombo.Value : DBNull.Value;
            pStars.Value = c.Stars.HasValue ? c.Stars.Value : DBNull.Value;
            pPercentile.Value = c.Percentile.HasValue ? c.Percentile.Value : DBNull.Value;
            pSeason.Value = c.Season.HasValue ? c.Season.Value : DBNull.Value;
            pScoreAchievedAt.Value = c.ScoreAchievedAt is not null ? ParseUtc(c.ScoreAchievedAt) : DBNull.Value;
            pSeasonRank.Value = c.SeasonRank.HasValue ? c.SeasonRank.Value : DBNull.Value;
            pAllTimeRank.Value = c.AllTimeRank.HasValue ? c.AllTimeRank.Value : DBNull.Value;
            pDifficulty.Value = c.Difficulty.HasValue ? c.Difficulty.Value : DBNull.Value;
            pNow.Value = now;
            loopInserted += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return loopInserted;
    }

    public List<ScoreHistoryEntry> GetScoreHistory(string accountId, int limit = 100, string? songId = null, string? instrument = null)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        var where = "WHERE account_id = @accountId";
        cmd.Parameters.AddWithValue("accountId", accountId);
        if (songId is not null) { where += " AND song_id = @songId"; cmd.Parameters.AddWithValue("songId", songId); }
        if (instrument is not null) { where += " AND instrument = @instrument"; cmd.Parameters.AddWithValue("instrument", instrument); }
        cmd.CommandText = $"SELECT song_id, instrument, old_score, new_score, old_rank, new_rank, accuracy, is_full_combo, stars, percentile, season, score_achieved_at, changed_at, season_rank, all_time_rank, difficulty FROM score_history {where} ORDER BY id DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("limit", limit);
        var list = new List<ScoreHistoryEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ScoreHistoryEntry
            {
                SongId = r.GetString(0),
                Instrument = r.GetString(1),
                OldScore = r.IsDBNull(2) ? null : r.GetInt32(2),
                NewScore = r.GetInt32(3),
                OldRank = r.IsDBNull(4) ? null : r.GetInt32(4),
                NewRank = r.GetInt32(5),
                Accuracy = r.IsDBNull(6) ? null : r.GetInt32(6),
                IsFullCombo = r.IsDBNull(7) ? null : r.GetBoolean(7),
                Stars = r.IsDBNull(8) ? null : r.GetInt32(8),
                Percentile = r.IsDBNull(9) ? null : r.GetDouble(9),
                Season = r.IsDBNull(10) ? null : r.GetInt32(10),
                ScoreAchievedAt = r.IsDBNull(11) ? null : r.GetDateTime(11).ToString("o"),
                ChangedAt = r.GetDateTime(12).ToString("o"),
                SeasonRank = r.IsDBNull(13) ? null : r.GetInt32(13),
                AllTimeRank = r.IsDBNull(14) ? null : r.GetInt32(14),
                Difficulty = r.IsDBNull(15) ? null : r.GetInt32(15),
            });
        }
        return list;
    }

    public Dictionary<(string SongId, string Instrument), ValidScoreFallback> GetBestValidScores(string accountId, Dictionary<(string SongId, string Instrument), int> thresholds)
    {
        if (thresholds.Count == 0) return new();
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH {SongInstrumentThresholdsCte}
            SELECT sh.song_id,
                   sh.instrument,
                   sh.new_score,
                   sh.accuracy,
                   sh.is_full_combo,
                   sh.stars
            FROM score_history sh
            JOIN requested_thresholds threshold
              ON threshold.song_id = sh.song_id
             AND threshold.instrument = sh.instrument
            WHERE sh.account_id = @accountId
              AND sh.new_score <= threshold.max_score
              AND sh.new_score = (
                  SELECT MAX(sh2.new_score)
                  FROM score_history sh2
                  WHERE sh2.account_id = @accountId
                    AND sh2.song_id = sh.song_id
                    AND sh2.instrument = sh.instrument
                    AND sh2.new_score <= threshold.max_score
              )
            GROUP BY sh.song_id,
                     sh.instrument,
                     sh.new_score,
                     sh.accuracy,
                     sh.is_full_combo,
                     sh.stars
            """;
        AddSongInstrumentThresholdParameters(cmd, thresholds);
        cmd.Parameters.AddWithValue("accountId", accountId);
        var result = new Dictionary<(string, string), ValidScoreFallback>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) result[(r.GetString(0), r.GetString(1))] = new ValidScoreFallback { Score = r.GetInt32(2), Accuracy = r.IsDBNull(3) ? null : r.GetInt32(3), IsFullCombo = r.IsDBNull(4) ? null : r.GetBoolean(4), Stars = r.IsDBNull(5) ? null : r.GetInt32(5) };
        }
        return result;
    }

    public Dictionary<(string AccountId, string SongId), ValidScoreFallback> GetBulkBestValidScores(string instrument, Dictionary<(string AccountId, string SongId), int> entries)
    {
        if (entries.Count == 0) return new();
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        var result = GetBulkBestValidScores(
            instrument,
            entries,
            conn,
            tx);
        tx.Commit();
        return result;
    }

    public Dictionary<(string AccountId, string SongId), ValidScoreFallback>
        GetBulkBestValidScores(
            string instrument,
            Dictionary<(string AccountId, string SongId), int> entries,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction)
    {
        if (entries.Count == 0)
            return new();
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The valid-score fallback transaction must belong to the supplied connection.",
                nameof(transaction));
        }

        using (var c = connection.CreateCommand()) { c.Transaction = transaction; c.CommandText = "CREATE TEMP TABLE _bulk_thresholds (account_id TEXT, song_id TEXT, max_score INTEGER, PRIMARY KEY (account_id, song_id)) ON COMMIT DROP"; c.ExecuteNonQuery(); }
        using (var writer = connection.BeginBinaryImport("COPY _bulk_thresholds (account_id, song_id, max_score) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var ((accountId, songId), maxScore) in entries)
            {
                writer.StartRow();
                writer.Write(accountId, NpgsqlDbType.Text);
                writer.Write(songId, NpgsqlDbType.Text);
                writer.Write(maxScore, NpgsqlDbType.Integer);
            }

            writer.Complete();
        }
        using (var c = connection.CreateCommand()) { c.Transaction = transaction; c.CommandText = "ANALYZE _bulk_thresholds"; c.ExecuteNonQuery(); }
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT sh.account_id, sh.song_id, sh.new_score, sh.accuracy, sh.is_full_combo, sh.stars FROM score_history sh JOIN _bulk_thresholds bt ON bt.account_id = sh.account_id AND bt.song_id = sh.song_id WHERE sh.instrument = @instrument AND sh.new_score <= bt.max_score AND sh.new_score = (SELECT MAX(sh2.new_score) FROM score_history sh2 WHERE sh2.account_id = sh.account_id AND sh2.song_id = sh.song_id AND sh2.instrument = @instrument AND sh2.new_score <= bt.max_score) GROUP BY sh.account_id, sh.song_id, sh.new_score, sh.accuracy, sh.is_full_combo, sh.stars";
        cmd.Parameters.AddWithValue("instrument", instrument);
        var result = new Dictionary<(string, string), ValidScoreFallback>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) result[(r.GetString(0), r.GetString(1))] = new ValidScoreFallback { Score = r.GetInt32(2), Accuracy = r.IsDBNull(3) ? null : r.GetInt32(3), IsFullCombo = r.IsDBNull(4) ? null : r.GetBoolean(4), Stars = r.IsDBNull(5) ? null : r.GetInt32(5) };
        }
        return result;
    }

    public Dictionary<(string SongId, string Instrument), List<ValidScoreFallback>> GetAllValidScoreTiers(
        string accountId, Dictionary<(string SongId, string Instrument), int> maxThresholds)
    {
        if (maxThresholds.Count == 0) return new();
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "CREATE TEMP TABLE _tier_thresholds (song_id TEXT, instrument TEXT, max_score INTEGER, PRIMARY KEY (song_id, instrument)) ON COMMIT DROP"; c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "INSERT INTO _tier_thresholds VALUES (@s, @i, @m)";
            var ps = c.Parameters.Add("s", NpgsqlTypes.NpgsqlDbType.Text);
            var pi = c.Parameters.Add("i", NpgsqlTypes.NpgsqlDbType.Text);
            var pm = c.Parameters.Add("m", NpgsqlTypes.NpgsqlDbType.Integer);
            c.Prepare();
            foreach (var ((s, i), m) in maxThresholds) { ps.Value = s; pi.Value = i; pm.Value = m; c.ExecuteNonQuery(); }
        }
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT sh.song_id,
                   sh.instrument,
                   sh.new_score,
                   MAX(sh.accuracy),
                   MAX(CASE WHEN sh.is_full_combo THEN 1 ELSE 0 END)::BOOLEAN,
                   MAX(sh.stars),
                   MIN(COALESCE(NULLIF(sh.all_time_rank, 0), NULLIF(sh.season_rank, 0), NULLIF(sh.new_rank, 0))) AS fallback_rank
            FROM score_history sh
            JOIN _tier_thresholds tt ON tt.song_id = sh.song_id AND tt.instrument = sh.instrument
            WHERE sh.account_id = @accountId
              AND sh.new_score <= tt.max_score
            GROUP BY sh.song_id, sh.instrument, sh.new_score
            ORDER BY sh.song_id, sh.instrument, sh.new_score DESC
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        var result = new Dictionary<(string, string), List<ValidScoreFallback>>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var key = (r.GetString(0), r.GetString(1));
                if (!result.TryGetValue(key, out var list)) { list = new List<ValidScoreFallback>(); result[key] = list; }
                list.Add(new ValidScoreFallback
                {
                    Score = r.GetInt32(2),
                    Accuracy = r.IsDBNull(3) ? null : r.GetInt32(3),
                    IsFullCombo = r.IsDBNull(4) ? null : r.GetBoolean(4),
                    Stars = r.IsDBNull(5) ? null : r.GetInt32(5),
                    Rank = r.IsDBNull(6) ? null : r.GetInt32(6),
                });
            }
        }
        tx.Commit();
        return result;
    }

    public Dictionary<(string SongId, string Instrument), string> GetLastPlayedDates(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT song_id, instrument, MAX(score_achieved_at) FROM score_history WHERE account_id = @accountId AND score_achieved_at IS NOT NULL GROUP BY song_id, instrument";
        cmd.Parameters.AddWithValue("accountId", accountId);
        var result = new Dictionary<(string, string), string>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var ts = r.GetDateTime(2).ToString("O");
            result[(r.GetString(0), r.GetString(1))] = ts;
        }
        return result;
    }

    public Dictionary<(string SongId, string Instrument), string> GetLastPlayedDates(
        string accountId, Dictionary<(string SongId, string Instrument), int> maxThresholds)
    {
        if (maxThresholds.Count == 0) return new();
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH {SongInstrumentThresholdsCte}
            SELECT sh.song_id,
                   sh.instrument,
                   MAX(sh.score_achieved_at)
            FROM score_history sh
            JOIN requested_thresholds threshold
              ON threshold.song_id = sh.song_id
             AND threshold.instrument = sh.instrument
            WHERE sh.account_id = @accountId
              AND sh.new_score <= threshold.max_score
              AND sh.score_achieved_at IS NOT NULL
            GROUP BY sh.song_id, sh.instrument
            """;
        AddSongInstrumentThresholdParameters(cmd, maxThresholds);
        cmd.Parameters.AddWithValue("accountId", accountId);
        var result = new Dictionary<(string, string), string>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var ts = r.GetDateTime(2).ToString("O");
                result[(r.GetString(0), r.GetString(1))] = ts;
            }
        }
        return result;
    }

    // ── Account names ────────────────────────────────────────────────

    public int InsertAccountIds(IEnumerable<string> accountIds)
    {
        var idList = accountIds as IList<string> ?? accountIds.ToList();
        if (idList.Count == 0) return 0;

        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        // COPY + merge for larger batches
        if (idList.Count > 50)
        {
            using (var c = conn.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = "CREATE TEMP TABLE _acct_staging (account_id TEXT) ON COMMIT DROP";
                c.ExecuteNonQuery();
            }

            using (var writer = conn.BeginBinaryImport("COPY _acct_staging (account_id) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var id in idList)
                {
                    writer.StartRow();
                    writer.Write(id, NpgsqlDbType.Text);
                }
                writer.Complete();
            }

            int inserted;
            using (var c = conn.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandTimeout = 0;
                c.CommandText = "INSERT INTO account_names (account_id) SELECT account_id FROM _acct_staging ON CONFLICT DO NOTHING";
                inserted = c.ExecuteNonQuery();
            }
            tx.Commit();
            return inserted;
        }

        // Small batch: prepared-statement loop
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO account_names (account_id) VALUES (@id) ON CONFLICT DO NOTHING";
        var pId = cmd.Parameters.Add("id", NpgsqlDbType.Text); cmd.Prepare();
        int loopInserted = 0;
        foreach (var id in idList) { pId.Value = id; loopInserted += cmd.ExecuteNonQuery(); }
        tx.Commit();
        return loopInserted;
    }

    public List<string> GetUnresolvedAccountIds() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id FROM account_names WHERE last_resolved IS NULL"; var ids = new List<string>(); using var r = cmd.ExecuteReader(); while (r.Read()) ids.Add(r.GetString(0)); return ids; }
    public int GetUnresolvedAccountCount() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM account_names WHERE last_resolved IS NULL"; return Convert.ToInt32(cmd.ExecuteScalar()); }

    public int InsertAccountNames(IReadOnlyList<(string AccountId, string? DisplayName)> accounts)
    {
        if (accounts.Count == 0) return 0;
        var now = DateTime.UtcNow;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO account_names (account_id, display_name, last_resolved) VALUES (@id, @name, @now) ON CONFLICT(account_id) DO UPDATE SET display_name = EXCLUDED.display_name, last_resolved = EXCLUDED.last_resolved";
        var pId = cmd.Parameters.Add("id", NpgsqlTypes.NpgsqlDbType.Text); var pName = cmd.Parameters.Add("name", NpgsqlTypes.NpgsqlDbType.Text); var pNow = cmd.Parameters.Add("now", NpgsqlTypes.NpgsqlDbType.TimestampTz); cmd.Prepare();
        int inserted = 0;
        foreach (var (accountId, displayName) in accounts) { pId.Value = accountId; pName.Value = displayName is not null ? displayName : DBNull.Value; pNow.Value = now; inserted += cmd.ExecuteNonQuery(); }
        tx.Commit();
        return inserted;
    }

    public string? GetDisplayName(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT display_name FROM account_names WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); var result = cmd.ExecuteScalar(); return result is DBNull or null ? null : (string)result; }
    public List<(string AccountId, string DisplayName)> SearchAccountNames(string query, int limit = 10)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        var normalizedQuery = query.Trim().ToLowerInvariant();
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return list;

        if (normalizedQuery.Length <= 2 && TryGetExclusiveUpperBound(normalizedQuery, out var upperBound))
        {
            cmd.CommandTimeout = 2;
            cmd.CommandText = @"
                SELECT account_id, display_name
                FROM account_names
                WHERE display_name IS NOT NULL
                  AND LOWER(display_name) >= @prefix
                  AND LOWER(display_name) < @upperBound
                ORDER BY LOWER(display_name), display_name
                LIMIT @limit";
            cmd.Parameters.AddWithValue("prefix", normalizedQuery);
            cmd.Parameters.AddWithValue("upperBound", upperBound);
            cmd.Parameters.AddWithValue("limit", limit);

            try
            {
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add((r.GetString(0), r.GetString(1)));
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                _log.LogWarning(ex, "Account prefix search timed out for query length {Length}; returning an empty fast-fail result.", normalizedQuery.Length);
            }

            return list;
        }

        var escapedQuery = EscapeLikePattern(normalizedQuery);
        cmd.CommandTimeout = 2;
        cmd.CommandText = "SELECT account_id, display_name FROM account_names WHERE display_name IS NOT NULL AND LOWER(display_name) LIKE @pattern ESCAPE '!' ORDER BY CASE WHEN LOWER(display_name) LIKE @prefix ESCAPE '!' THEN 0 ELSE 1 END, LENGTH(display_name), display_name LIMIT @limit";
        cmd.Parameters.AddWithValue("pattern", $"%{escapedQuery}%");
        cmd.Parameters.AddWithValue("prefix", $"{escapedQuery}%");
        cmd.Parameters.AddWithValue("limit", limit);
        try
        {
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetString(0), r.GetString(1)));
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _log.LogWarning(ex, "Account search timed out for query length {Length}; returning an empty fast-fail result.", normalizedQuery.Length);
        }
        return list;
    }

    private static bool TryGetExclusiveUpperBound(string prefix, out string upperBound)
    {
        var chars = prefix.ToCharArray();
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            if (chars[i] == char.MaxValue)
                continue;

            chars[i]++;
            upperBound = new string(chars, 0, i + 1);
            return true;
        }

        upperBound = string.Empty;
        return false;
    }

    private static string EscapeLikePattern(string value) => value
        .Replace("!", "!!", StringComparison.Ordinal)
        .Replace("%", "!%", StringComparison.Ordinal)
        .Replace("_", "!_", StringComparison.Ordinal);

    public Dictionary<string, string> GetDisplayNames(IEnumerable<string> accountIds)
    {
        var idList = accountIds as IList<string> ?? accountIds.ToList();
        if (idList.Count == 0) return new();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in idList.Chunk(500))
        {
            using var conn = _ds.OpenConnection();
            using var cmd = conn.CreateCommand();
            var paramNames = new string[batch.Length];
            for (int i = 0; i < batch.Length; i++) { paramNames[i] = $"@id{i}"; cmd.Parameters.AddWithValue($"id{i}", batch[i]); }
            cmd.CommandText = $"SELECT account_id, display_name FROM account_names WHERE display_name IS NOT NULL AND account_id IN ({string.Join(',', paramNames)})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result[r.GetString(0)] = r.GetString(1);
        }
        return result;
    }

    // ── Registered users ─────────────────────────────────────────────

    public HashSet<string> GetRegisteredAccountIds() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT DISTINCT account_id FROM registered_users"; var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) ids.Add(r.GetString(0)); return ids; }
    public IReadOnlyList<string> GetRegisteredUserRefreshSongOrder(
        IReadOnlyCollection<string> songIds,
        IReadOnlyCollection<string> instruments)
    {
        var requestedSongs = songIds
            .Where(static songId => !string.IsNullOrWhiteSpace(songId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requestedSongs.Length == 0)
            return [];

        var requestedInstruments = instruments
            .Where(static instrument => !string.IsNullOrWhiteSpace(instrument))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requestedInstruments.Length == 0)
            return requestedSongs.OrderBy(static songId => songId, StringComparer.Ordinal).ToArray();

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH requested_songs AS (
                SELECT DISTINCT song_id
                FROM unnest(@songIds::text[]) AS requested(song_id)
            ),
            coverage AS (
                SELECT
                    requested.song_id,
                    COUNT(progress.instrument)::INTEGER AS checked_scopes,
                    MIN(progress.checked_at) AS oldest_checked_at
                FROM requested_songs requested
                LEFT JOIN registered_user_refresh_scope_progress progress
                  ON progress.song_id = requested.song_id
                 AND progress.instrument = ANY(@instruments)
                 AND progress.status = 'complete'
                GROUP BY requested.song_id
            )
            SELECT song_id
            FROM coverage
            ORDER BY
                (@instrumentCount - checked_scopes) DESC,
                oldest_checked_at ASC NULLS FIRST,
                song_id
            """;
        cmd.Parameters.Add("songIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = requestedSongs;
        cmd.Parameters.Add("instruments", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = requestedInstruments;
        cmd.Parameters.AddWithValue("instrumentCount", requestedInstruments.Length);

        var ordered = new List<string>(requestedSongs.Length);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ordered.Add(reader.GetString(0));
        return ordered;
    }

    public int UpsertRegisteredUserRefreshScopes(
        long scrapeId,
        IReadOnlyCollection<SoloCurrentProjectionScopeKey> scopes,
        DateTime checkedAtUtc)
    {
        if (scrapeId < 0)
            throw new ArgumentOutOfRangeException(nameof(scrapeId), scrapeId, "Scrape ID cannot be negative.");

        var normalizedScopes = scopes
            .Where(static scope =>
                !string.IsNullOrWhiteSpace(scope.SongId) &&
                !string.IsNullOrWhiteSpace(scope.Instrument))
            .Distinct()
            .OrderBy(static scope => scope.SongId, StringComparer.Ordinal)
            .ThenBy(static scope => scope.Instrument, StringComparer.Ordinal)
            .ToArray();
        if (normalizedScopes.Length == 0)
            return 0;

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO registered_user_refresh_scope_progress (
                song_id,
                instrument,
                status,
                checked_at,
                scrape_id,
                provenance)
            SELECT
                scope.song_id,
                scope.instrument,
                'complete',
                @checkedAt,
                @scrapeId,
                @provenance
            FROM unnest(
                @songIds::text[],
                @instruments::text[]) AS scope(song_id, instrument)
            ON CONFLICT (song_id, instrument) DO UPDATE SET
                status = EXCLUDED.status,
                checked_at = EXCLUDED.checked_at,
                scrape_id = EXCLUDED.scrape_id,
                provenance = EXCLUDED.provenance
            WHERE registered_user_refresh_scope_progress.checked_at < EXCLUDED.checked_at
               OR (
                    registered_user_refresh_scope_progress.checked_at = EXCLUDED.checked_at
                AND COALESCE(registered_user_refresh_scope_progress.scrape_id, 0)
                    <= COALESCE(EXCLUDED.scrape_id, 0))
            """;
        cmd.Parameters.Add("songIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            normalizedScopes.Select(static scope => scope.SongId).ToArray();
        cmd.Parameters.Add("instruments", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            normalizedScopes.Select(static scope => scope.Instrument).ToArray();
        cmd.Parameters.AddWithValue("checkedAt", NormalizeUtc(checkedAtUtc));
        cmd.Parameters.Add("scrapeId", NpgsqlDbType.Bigint).Value =
            scrapeId > 0 ? scrapeId : DBNull.Value;
        cmd.Parameters.AddWithValue(
            "provenance",
            scrapeId > 0 ? "scrape" : "phase_only");
        return cmd.ExecuteNonQuery();
    }

    public RegisteredUserRefreshCoverageInfo GetRegisteredUserRefreshCoverage(
        IReadOnlyCollection<string> songIds,
        IReadOnlyCollection<string> instruments,
        long currentScrapeId,
        DateTime observedAtUtc)
    {
        var requestedSongs = songIds
            .Where(static songId => !string.IsNullOrWhiteSpace(songId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var requestedInstruments = instruments
            .Where(static instrument => !string.IsNullOrWhiteSpace(instrument))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var expectedScopes = checked(requestedSongs.Length * requestedInstruments.Length);
        if (expectedScopes == 0)
        {
            return new RegisteredUserRefreshCoverageInfo(
                0,
                0,
                0,
                null,
                null,
                0);
        }

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH requested_songs AS (
                SELECT DISTINCT song_id
                FROM unnest(@songIds::text[]) AS requested(song_id)
            ),
            requested_instruments AS (
                SELECT DISTINCT instrument
                FROM unnest(@instruments::text[]) AS requested(instrument)
            )
            SELECT
                COUNT(progress.instrument)::INTEGER AS checked_scopes,
                MIN(progress.checked_at) AS oldest_checked_at,
                COUNT(progress.instrument) FILTER (
                    WHERE progress.scrape_id = @currentScrapeId)::INTEGER
                    AS current_scrape_completions
            FROM requested_songs song
            CROSS JOIN requested_instruments instrument
            LEFT JOIN registered_user_refresh_scope_progress progress
              ON progress.song_id = song.song_id
             AND progress.instrument = instrument.instrument
             AND progress.status = 'complete'
            """;
        cmd.Parameters.Add("songIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = requestedSongs;
        cmd.Parameters.Add("instruments", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = requestedInstruments;
        cmd.Parameters.AddWithValue("currentScrapeId", currentScrapeId);

        using var reader = cmd.ExecuteReader();
        reader.Read();
        var checkedScopes = reader.GetInt32(0);
        var oldestCheckedAtUtc = reader.IsDBNull(1)
            ? (DateTime?)null
            : NormalizeUtc(reader.GetDateTime(1));
        var currentScrapeCompletions = reader.GetInt32(2);
        var observedUtc = NormalizeUtc(observedAtUtc);
        var oldestCheckedAge = oldestCheckedAtUtc is DateTime oldest
            ? observedUtc - oldest
            : (TimeSpan?)null;
        if (oldestCheckedAge is TimeSpan age && age < TimeSpan.Zero)
            oldestCheckedAge = TimeSpan.Zero;

        return new RegisteredUserRefreshCoverageInfo(
            expectedScopes,
            checkedScopes,
            expectedScopes - checkedScopes,
            oldestCheckedAtUtc,
            oldestCheckedAge,
            currentScrapeCompletions);
    }

    public List<string> GetRegisteredAccountIdsForBandDiscovery()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT registered.account_id
            FROM (SELECT DISTINCT account_id FROM registered_users) registered
            LEFT JOIN (
                SELECT account_id, MAX(checked_at) AS last_checked_at
                FROM registered_player_band_discovery_progress
                GROUP BY account_id
            ) progress ON progress.account_id = registered.account_id
            ORDER BY progress.last_checked_at NULLS FIRST, registered.account_id
            """;
        var ids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    public bool AreRegistrationMutationsBlocked()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT public_reads_frozen,
                   public_reads_frozen_reason,
                   max_score_mutation_gate_token
            FROM scrape_publication_state
            WHERE id = TRUE
            """;
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
            return IsRegistrationMutationBlocked(reader);
        reader.Close();

        using (var ensure = conn.CreateCommand())
        {
            ensure.CommandText = """
                INSERT INTO scrape_publication_state (
                    id,
                    updated_at)
                VALUES (
                    TRUE,
                    now())
                ON CONFLICT (id) DO NOTHING
                """;
            ensure.ExecuteNonQuery();
        }
        using var verify = conn.CreateCommand();
        verify.CommandText = """
            SELECT public_reads_frozen,
                   public_reads_frozen_reason,
                   max_score_mutation_gate_token
            FROM scrape_publication_state
            WHERE id = TRUE
            """;
        using var verifyReader = verify.ExecuteReader();
        return !verifyReader.Read()
               || IsRegistrationMutationBlocked(verifyReader);
    }

    public IRegistrationMutationLease AcquireRegistrationMutationLease()
        => AcquireRegistrationMutationLeaseAsync(
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    public async Task<IRegistrationMutationLease>
        AcquireRegistrationMutationLeaseAsync(
            CancellationToken ct = default)
        => await AcquireRegistrationMutationLeaseCoreAsync(
            waitForExclusiveMaintenance: true,
            boundedAdmission: false,
            ct);

    public async Task<IRegistrationMutationLease>
        TryAcquireRegistrationMutationLeaseAsync(
            CancellationToken ct = default)
        => await AcquireRegistrationMutationLeaseCoreAsync(
            waitForExclusiveMaintenance: false,
            boundedAdmission: true,
            ct);

    private async Task<IRegistrationMutationLease>
        AcquireRegistrationMutationLeaseCoreAsync(
            bool waitForExclusiveMaintenance,
            bool boundedAdmission,
            CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var boundedAdmissionAcquired = false;
        NpgsqlConnection? conn = null;
        var mutationGateLockAcquired = false;
        var leaseToken = CreateLeaseToken();
        var backendProcessId = 0;
        try
        {
            if (boundedAdmission)
            {
                boundedAdmissionAcquired =
                    await _boundedRegistrationAdmissions
                        .WaitAsync(0, ct);
                if (!boundedAdmissionAcquired)
                    throw new RegistrationMutationBlockedException();
            }

            conn = _unpooledConnections.CreateConnection();
            await conn.OpenAsync(ct);
            await using (var identity = conn.CreateCommand())
            {
                identity.CommandTimeout = 5;
                identity.CommandText = """
                    SELECT
                        set_config(
                            'application_name',
                            'fst-registration-mutation',
                            FALSE),
                        set_config(
                            'fst.registration_mutation_lease_token',
                            @leaseToken,
                            FALSE),
                        pg_backend_pid()
                    """;
                identity.Parameters.AddWithValue(
                    "leaseToken",
                    leaseToken);
                await using var identityReader =
                    await identity.ExecuteReaderAsync(ct);
                if (!await identityReader.ReadAsync(ct))
                    throw new RegistrationMutationBlockedException();
                backendProcessId = identityReader.GetInt32(2);
            }

            await using (var mutationGate =
                         conn.CreateCommand())
            {
                mutationGate.CommandTimeout =
                    waitForExclusiveMaintenance ? 0 : 5;
                mutationGate.CommandText =
                    waitForExclusiveMaintenance
                        ? "SELECT pg_advisory_lock_shared(@lockKey)"
                        : "SELECT pg_try_advisory_lock_shared(@lockKey)";
                mutationGate.Parameters.AddWithValue(
                    "lockKey",
                    RegistrationMutationGate.AdvisoryLockKey);
                var result =
                    await mutationGate.ExecuteScalarAsync(ct);
                if (!waitForExclusiveMaintenance
                    && result is not true)
                {
                    throw new RegistrationMutationBlockedException();
                }
                mutationGateLockAcquired = true;
            }
            await using (var ensure = conn.CreateCommand())
            {
                ensure.CommandText = """
                    INSERT INTO scrape_publication_state (
                        id,
                        updated_at)
                    VALUES (
                        TRUE,
                        now())
                    ON CONFLICT (id) DO NOTHING
                    """;
                await ensure.ExecuteNonQueryAsync(ct);
            }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT public_reads_frozen,
                       public_reads_frozen_reason,
                       max_score_mutation_gate_token
                FROM scrape_publication_state
                WHERE id = TRUE
                """;
            await using var reader =
                await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new RegistrationMutationBlockedException();
            var blocked = IsRegistrationMutationBlocked(reader);
            await reader.CloseAsync();
            if (blocked)
                throw new RegistrationMutationBlockedException();

            return new PostgresRegistrationMutationLease(
                conn,
                leaseToken,
                backendProcessId,
                boundedAdmission
                    ? _boundedRegistrationAdmissions
                    : null);
        }
        catch
        {
            if (conn is not null)
            {
                try
                {
                    if (mutationGateLockAcquired)
                    {
                        await using var unlock =
                            conn.CreateCommand();
                        unlock.CommandTimeout = 5;
                        unlock.CommandText =
                            "SELECT pg_advisory_unlock_shared(@lockKey)";
                        unlock.Parameters.AddWithValue(
                            "lockKey",
                            RegistrationMutationGate.AdvisoryLockKey);
                        await unlock.ExecuteScalarAsync(
                            CancellationToken.None);
                    }
                }
                catch
                {
                }
                finally
                {
                    await conn.DisposeAsync();
                }
            }
            if (boundedAdmissionAcquired)
                _boundedRegistrationAdmissions.Release();
            throw;
        }
    }

    private static bool IsRegistrationMutationBlocked(
        NpgsqlDataReader reader)
        => !reader.IsDBNull(2)
           || reader.GetBoolean(0)
              && !reader.IsDBNull(1)
              && reader.GetString(1).StartsWith(
                  PublicReadFreezeState
                      .MaxScoreMaintenanceReasonPrefix,
                  StringComparison.Ordinal);

    private static string CreateLeaseToken()
        => Convert.ToHexString(
                RandomNumberGenerator.GetBytes(32))
            .ToLowerInvariant();

    public bool IsAccountRegistered(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM registered_users WHERE account_id = @accountId)"; cmd.Parameters.AddWithValue("accountId", accountId); return Convert.ToBoolean(cmd.ExecuteScalar() ?? false); }
    public bool RegisterUser(string deviceId, string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO registered_users (device_id, account_id, registered_at, last_activity_at) VALUES (@deviceId, @accountId, @now, @now) ON CONFLICT DO NOTHING";
        cmd.Parameters.AddWithValue("deviceId", deviceId);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        return cmd.ExecuteNonQuery() > 0;
    }
    public bool UnregisterUser(string deviceId, string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var delCmd = conn.CreateCommand();
        delCmd.Transaction = tx;
        delCmd.CommandText = "DELETE FROM registered_users WHERE device_id = @deviceId AND account_id = @accountId";
        delCmd.Parameters.AddWithValue("deviceId", deviceId);
        delCmd.Parameters.AddWithValue("accountId", accountId);
        bool removed = delCmd.ExecuteNonQuery() > 0;
        if (removed)
        {
            using var chk = conn.CreateCommand();
            chk.Transaction = tx;
            chk.CommandText = "SELECT COUNT(*) FROM registered_users WHERE account_id = @accountId";
            chk.Parameters.AddWithValue("accountId", accountId);
            int remaining = Convert.ToInt32(chk.ExecuteScalar());
            if (remaining == 0)
            {
                // Cascade-delete all per-account data (account_id column)
                foreach (var t in new[] { "player_stats", "player_stats_tiers", "backfill_status", "backfill_progress", "history_recon_status", "history_recon_progress", "rivals_status", "rivals_dirty_songs", "rival_song_fingerprints", "rival_instrument_state" })
                { using var c = conn.CreateCommand(); c.Transaction = tx; c.CommandText = $"DELETE FROM {t} WHERE account_id = @id"; c.Parameters.AddWithValue("id", accountId); c.ExecuteNonQuery(); }
                // Rivals tables use user_id column
                foreach (var t in new[] { "user_rivals", "rival_song_samples" })
                { using var c = conn.CreateCommand(); c.Transaction = tx; c.CommandText = $"DELETE FROM {t} WHERE user_id = @id"; c.Parameters.AddWithValue("id", accountId); c.ExecuteNonQuery(); }
            }
        }
        tx.Commit();
        return removed;
    }

    public void TouchWebRegistrationActivity(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE registered_users SET last_activity_at = @now WHERE device_id = @deviceId AND account_id = @accountId";
        cmd.Parameters.AddWithValue("deviceId", WebTrackerDeviceId);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    public SelectedBandRegistrationResult RegisterSelectedBandActivity(string bandType, string teamKey, string? bandId = null)
    {
        var normalizedBandType = bandType.Trim();
        var normalizedTeamKey = teamKey.Trim();
        if (string.IsNullOrWhiteSpace(normalizedBandType) || string.IsNullOrWhiteSpace(normalizedTeamKey))
            return new SelectedBandRegistrationResult(false, string.Empty, []);

        var canonicalBandId = BandIdentity.CreateBandId(normalizedBandType, normalizedTeamKey);
        if (!string.IsNullOrWhiteSpace(bandId)
            && !string.Equals(bandId.Trim(), canonicalBandId, StringComparison.OrdinalIgnoreCase))
        {
            return new SelectedBandRegistrationResult(false, canonicalBandId, []);
        }

        using var conn = _ds.OpenConnection();
        var memberAccountIds = GetBandMemberAccountIds(conn, normalizedBandType, normalizedTeamKey);
        if (memberAccountIds.Count == 0)
            return new SelectedBandRegistrationResult(false, canonicalBandId, []);

        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow;

        using (var bandCmd = conn.CreateCommand())
        {
            bandCmd.Transaction = tx;
            bandCmd.CommandText = """
                INSERT INTO registered_bands (source_id, band_type, team_key, band_id, registered_at, last_activity_at, last_member_sync_at)
                VALUES (@sourceId, @bandType, @teamKey, @bandId, @now, @now, @now)
                ON CONFLICT (source_id, band_type, team_key)
                DO UPDATE SET band_id = EXCLUDED.band_id,
                              last_activity_at = EXCLUDED.last_activity_at,
                              last_member_sync_at = EXCLUDED.last_member_sync_at
                """;
            bandCmd.Parameters.AddWithValue("sourceId", WebBandTrackerDeviceId);
            bandCmd.Parameters.AddWithValue("bandType", normalizedBandType);
            bandCmd.Parameters.AddWithValue("teamKey", normalizedTeamKey);
            bandCmd.Parameters.AddWithValue("bandId", canonicalBandId);
            bandCmd.Parameters.AddWithValue("now", now);
            bandCmd.ExecuteNonQuery();
        }

        if (TableExists(conn, tx, BandIdentityPersistence.TableName))
        {
            using var identityCmd = conn.CreateCommand();
            identityCmd.Transaction = tx;
            identityCmd.CommandText = """
                INSERT INTO band_identity (band_id, band_type, team_key, member_account_ids, appearance_count, first_seen_at, last_seen_at, updated_at, source)
                VALUES (@bandId, @bandType, @teamKey, @memberAccountIds, 0, @now, @now, @now, 'registered_bands')
                ON CONFLICT (band_id) DO UPDATE SET
                    band_type = EXCLUDED.band_type,
                    team_key = EXCLUDED.team_key,
                    member_account_ids = EXCLUDED.member_account_ids,
                    last_seen_at = COALESCE(GREATEST(band_identity.last_seen_at, EXCLUDED.last_seen_at), band_identity.last_seen_at, EXCLUDED.last_seen_at),
                    updated_at = EXCLUDED.updated_at,
                    source = EXCLUDED.source
                """;
            identityCmd.Parameters.AddWithValue("bandId", canonicalBandId);
            identityCmd.Parameters.AddWithValue("bandType", normalizedBandType);
            identityCmd.Parameters.AddWithValue("teamKey", normalizedTeamKey);
            identityCmd.Parameters.Add("memberAccountIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = memberAccountIds.ToArray();
            identityCmd.Parameters.AddWithValue("now", now);
            identityCmd.ExecuteNonQuery();
        }

        using (var membersCmd = conn.CreateCommand())
        {
            membersCmd.Transaction = tx;
            membersCmd.CommandText = """
                INSERT INTO registered_users (device_id, account_id, registered_at, last_activity_at)
                SELECT device_id, account_id, @now, @now
                FROM unnest(@memberAccountIds::text[]) AS selected_member(account_id)
                CROSS JOIN unnest(@deviceIds::text[]) AS selected_device(device_id)
                ON CONFLICT (device_id, account_id)
                DO UPDATE SET last_activity_at = EXCLUDED.last_activity_at
                """;
            membersCmd.Parameters.Add("deviceIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = new[]
            {
                WebBandTrackerDeviceId,
                WebTrackerDeviceId,
            };
            membersCmd.Parameters.AddWithValue("now", now);
            membersCmd.Parameters.Add("memberAccountIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = memberAccountIds.ToArray();
            membersCmd.ExecuteNonQuery();
        }

        using (var backfillCmd = conn.CreateCommand())
        {
            backfillCmd.Transaction = tx;
            backfillCmd.CommandText = """
                INSERT INTO backfill_status (account_id, status, total_songs_to_check)
                SELECT account_id, 'pending', 0
                FROM unnest(@memberAccountIds::text[]) AS selected_member(account_id)
                ON CONFLICT (account_id) DO UPDATE SET
                    status = CASE
                        WHEN backfill_status.status = 'complete' THEN backfill_status.status
                        ELSE 'pending'
                    END,
                    total_songs_to_check = CASE
                        WHEN backfill_status.status = 'complete' THEN backfill_status.total_songs_to_check
                        ELSE GREATEST(backfill_status.total_songs_to_check, EXCLUDED.total_songs_to_check)
                    END
                WHERE backfill_status.status != 'complete'
                """;
            backfillCmd.Parameters.Add("memberAccountIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = memberAccountIds.ToArray();
            backfillCmd.ExecuteNonQuery();
        }

        using (var processingCmd = conn.CreateCommand())
        {
            processingCmd.Transaction = tx;
            processingCmd.CommandText = """
                INSERT INTO registered_band_processing_status (source_id, band_type, team_key, status, total_lookups_to_check)
                VALUES (@sourceId, @bandType, @teamKey, 'pending', 0)
                ON CONFLICT (source_id, band_type, team_key) DO NOTHING
                """;
            processingCmd.Parameters.AddWithValue("sourceId", WebBandTrackerDeviceId);
            processingCmd.Parameters.AddWithValue("bandType", normalizedBandType);
            processingCmd.Parameters.AddWithValue("teamKey", normalizedTeamKey);
            processingCmd.ExecuteNonQuery();
        }

        tx.Commit();
        return new SelectedBandRegistrationResult(true, canonicalBandId, memberAccountIds);
    }

    public int RegisterKnownBandsForAccountActivity(string accountId)
        => RegisterKnownBandsForAccountActivities([accountId]);

    public int RegisterKnownBandsForAccountActivities(IEnumerable<string> accountIds)
    {
        ArgumentNullException.ThrowIfNull(accountIds);

        var normalizedAccountIds = accountIds
            .Where(static accountId => !string.IsNullOrWhiteSpace(accountId))
            .Select(static accountId => accountId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedAccountIds.Length == 0)
            return 0;

        var requestedAccountIds = normalizedAccountIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        using var conn = _ds.OpenConnection();

        var knownBands = new List<(string BandType, string TeamKey)>();
        using (var lookupCmd = conn.CreateCommand())
        {
            lookupCmd.CommandText = """
                SELECT DISTINCT band_type, team_key
                FROM (
                    SELECT band_type, team_key
                    FROM band_team_membership
                    WHERE account_id = ANY(@accountIds)
                    UNION
                    SELECT band_type, team_key
                    FROM band_members
                    WHERE account_id = ANY(@accountIds)
                    UNION
                    SELECT band_type, team_key
                    FROM band_search_member_projection
                    WHERE account_id = ANY(@accountIds)
                ) AS known_band
                ORDER BY band_type, team_key
                """;
            lookupCmd.Parameters.Add(
                "accountIds",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                normalizedAccountIds;

            using var reader = lookupCmd.ExecuteReader();
            while (reader.Read())
            {
                var bandType = reader.GetString(0);
                var teamKey = reader.GetString(1);
                var memberAccountIds = teamKey.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!memberAccountIds.Any(requestedAccountIds.Contains))
                    continue;

                knownBands.Add((bandType, teamKey));
            }
        }

        if (knownBands.Count == 0)
            return 0;

        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow;
        var registered = 0;

        foreach (var (bandType, teamKey) in knownBands)
        {
            var memberAccountIds = teamKey.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var bandId = BandIdentity.CreateBandId(bandType, teamKey);

            using (var bandCmd = conn.CreateCommand())
            {
                bandCmd.Transaction = tx;
                bandCmd.CommandText = """
                    INSERT INTO registered_bands (source_id, band_type, team_key, band_id, registered_at, last_activity_at, last_member_sync_at)
                    VALUES (@sourceId, @bandType, @teamKey, @bandId, @now, @now, @now)
                    ON CONFLICT (source_id, band_type, team_key)
                    DO UPDATE SET band_id = EXCLUDED.band_id,
                                  last_activity_at = EXCLUDED.last_activity_at,
                                  last_member_sync_at = EXCLUDED.last_member_sync_at
                    """;
                bandCmd.Parameters.AddWithValue("sourceId", WebBandTrackerDeviceId);
                bandCmd.Parameters.AddWithValue("bandType", bandType);
                bandCmd.Parameters.AddWithValue("teamKey", teamKey);
                bandCmd.Parameters.AddWithValue("bandId", bandId);
                bandCmd.Parameters.AddWithValue("now", now);
                bandCmd.ExecuteNonQuery();
            }

            if (TableExists(conn, tx, BandIdentityPersistence.TableName))
            {
                using var identityCmd = conn.CreateCommand();
                identityCmd.Transaction = tx;
                identityCmd.CommandText = """
                    INSERT INTO band_identity (band_id, band_type, team_key, member_account_ids, appearance_count, first_seen_at, last_seen_at, updated_at, source)
                    VALUES (@bandId, @bandType, @teamKey, @memberAccountIds, 0, @now, @now, @now, 'registered_player_bands')
                    ON CONFLICT (band_id) DO UPDATE SET
                        band_type = EXCLUDED.band_type,
                        team_key = EXCLUDED.team_key,
                        member_account_ids = EXCLUDED.member_account_ids,
                        last_seen_at = COALESCE(GREATEST(band_identity.last_seen_at, EXCLUDED.last_seen_at), band_identity.last_seen_at, EXCLUDED.last_seen_at),
                        updated_at = EXCLUDED.updated_at,
                        source = EXCLUDED.source
                    """;
                identityCmd.Parameters.AddWithValue("bandId", bandId);
                identityCmd.Parameters.AddWithValue("bandType", bandType);
                identityCmd.Parameters.AddWithValue("teamKey", teamKey);
                identityCmd.Parameters.Add("memberAccountIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = memberAccountIds;
                identityCmd.Parameters.AddWithValue("now", now);
                identityCmd.ExecuteNonQuery();
            }

            using (var processingCmd = conn.CreateCommand())
            {
                processingCmd.Transaction = tx;
                processingCmd.CommandText = """
                    INSERT INTO registered_band_processing_status (source_id, band_type, team_key, status, total_lookups_to_check)
                    VALUES (@sourceId, @bandType, @teamKey, 'pending', 0)
                    ON CONFLICT (source_id, band_type, team_key) DO NOTHING
                    """;
                processingCmd.Parameters.AddWithValue("sourceId", WebBandTrackerDeviceId);
                processingCmd.Parameters.AddWithValue("bandType", bandType);
                processingCmd.Parameters.AddWithValue("teamKey", teamKey);
                processingCmd.ExecuteNonQuery();
            }

            registered++;
        }

        tx.Commit();
        return registered;
    }

    public void RegisterDiscoveredBandActivity(string bandType, string teamKey, IReadOnlyList<string> memberAccountIds)
    {
        var normalizedBandType = bandType.Trim();
        var normalizedTeamKey = teamKey.Trim();
        if (string.IsNullOrWhiteSpace(normalizedBandType) || string.IsNullOrWhiteSpace(normalizedTeamKey) || memberAccountIds.Count == 0)
            return;

        var members = memberAccountIds
            .Where(static accountId => !string.IsNullOrWhiteSpace(accountId))
            .Select(static accountId => accountId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (members.Length == 0)
            return;

        var bandId = BandIdentity.CreateBandId(normalizedBandType, normalizedTeamKey);
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow;

        using (var bandCmd = conn.CreateCommand())
        {
            bandCmd.Transaction = tx;
            bandCmd.CommandText = """
                INSERT INTO registered_bands (source_id, band_type, team_key, band_id, registered_at, last_activity_at, last_member_sync_at)
                VALUES (@sourceId, @bandType, @teamKey, @bandId, @now, @now, @now)
                ON CONFLICT (source_id, band_type, team_key)
                DO UPDATE SET band_id = EXCLUDED.band_id,
                              last_activity_at = EXCLUDED.last_activity_at,
                              last_member_sync_at = EXCLUDED.last_member_sync_at
                """;
            bandCmd.Parameters.AddWithValue("sourceId", WebBandTrackerDeviceId);
            bandCmd.Parameters.AddWithValue("bandType", normalizedBandType);
            bandCmd.Parameters.AddWithValue("teamKey", normalizedTeamKey);
            bandCmd.Parameters.AddWithValue("bandId", bandId);
            bandCmd.Parameters.AddWithValue("now", now);
            bandCmd.ExecuteNonQuery();
        }

        if (TableExists(conn, tx, BandIdentityPersistence.TableName))
        {
            using var identityCmd = conn.CreateCommand();
            identityCmd.Transaction = tx;
            identityCmd.CommandText = """
                INSERT INTO band_identity (band_id, band_type, team_key, member_account_ids, appearance_count, first_seen_at, last_seen_at, updated_at, source)
                VALUES (@bandId, @bandType, @teamKey, @memberAccountIds, 0, @now, @now, @now, 'registered_player_band_discovery')
                ON CONFLICT (band_id) DO UPDATE SET
                    band_type = EXCLUDED.band_type,
                    team_key = EXCLUDED.team_key,
                    member_account_ids = EXCLUDED.member_account_ids,
                    last_seen_at = COALESCE(GREATEST(band_identity.last_seen_at, EXCLUDED.last_seen_at), band_identity.last_seen_at, EXCLUDED.last_seen_at),
                    updated_at = EXCLUDED.updated_at,
                    source = EXCLUDED.source
                """;
            identityCmd.Parameters.AddWithValue("bandId", bandId);
            identityCmd.Parameters.AddWithValue("bandType", normalizedBandType);
            identityCmd.Parameters.AddWithValue("teamKey", normalizedTeamKey);
            identityCmd.Parameters.Add("memberAccountIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = members;
            identityCmd.Parameters.AddWithValue("now", now);
            identityCmd.ExecuteNonQuery();
        }

        using (var processingCmd = conn.CreateCommand())
        {
            processingCmd.Transaction = tx;
            processingCmd.CommandText = """
                INSERT INTO registered_band_processing_status (source_id, band_type, team_key, status, total_lookups_to_check)
                VALUES (@sourceId, @bandType, @teamKey, 'pending', 0)
                ON CONFLICT (source_id, band_type, team_key) DO NOTHING
                """;
            processingCmd.Parameters.AddWithValue("sourceId", WebBandTrackerDeviceId);
            processingCmd.Parameters.AddWithValue("bandType", normalizedBandType);
            processingCmd.Parameters.AddWithValue("teamKey", normalizedTeamKey);
            processingCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public List<RegisteredBandInfo> GetRegisteredBands()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT band.source_id,
                   band.band_type,
                   band.team_key,
                   band.band_id,
                   band.registered_at,
                   band.last_activity_at,
                   band.last_member_sync_at
            FROM registered_bands band
            LEFT JOIN registered_band_processing_status status
              ON status.source_id = band.source_id
             AND status.band_type = band.band_type
             AND status.team_key = band.team_key
            ORDER BY CASE status.status
                         WHEN 'pending' THEN 0
                         WHEN 'failed' THEN 1
                         WHEN 'in_progress' THEN 2
                         WHEN 'complete' THEN 3
                         ELSE 0
                     END,
                     status.last_resumed_at NULLS FIRST,
                     band.registered_at,
                     band.source_id,
                     band.band_type,
                     band.team_key
            """;
        var bands = new List<RegisteredBandInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            bands.Add(new RegisteredBandInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6)));
        }
        return bands;
    }

    public void EnsureRegisteredBandProcessingStatus(string sourceId, string bandType, string teamKey, int totalLookupsToCheck)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO registered_band_processing_status (source_id, band_type, team_key, status, total_lookups_to_check)
            VALUES (@sourceId, @bandType, @teamKey, 'pending', @total)
            ON CONFLICT (source_id, band_type, team_key) DO UPDATE SET
                total_lookups_to_check = GREATEST(registered_band_processing_status.total_lookups_to_check, EXCLUDED.total_lookups_to_check),
                status = CASE
                    WHEN registered_band_processing_status.status = 'complete'
                         AND registered_band_processing_status.total_lookups_to_check >= EXCLUDED.total_lookups_to_check
                    THEN registered_band_processing_status.status
                    ELSE 'pending'
                END,
                completed_at = CASE
                    WHEN registered_band_processing_status.status = 'complete'
                         AND registered_band_processing_status.total_lookups_to_check >= EXCLUDED.total_lookups_to_check
                    THEN registered_band_processing_status.completed_at
                    ELSE NULL
                END,
                error_message = CASE
                    WHEN registered_band_processing_status.status = 'complete'
                         AND registered_band_processing_status.total_lookups_to_check >= EXCLUDED.total_lookups_to_check
                    THEN registered_band_processing_status.error_message
                    ELSE NULL
                END
            """;
        cmd.Parameters.AddWithValue("sourceId", sourceId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("total", totalLookupsToCheck);
        cmd.ExecuteNonQuery();
    }

    public RegisteredBandProcessingStatusInfo? GetRegisteredBandProcessingStatus(string sourceId, string bandType, string teamKey)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT source_id, band_type, team_key, status, lookups_checked, entries_found,
                   total_lookups_to_check, started_at, completed_at, last_resumed_at, error_message
            FROM registered_band_processing_status
            WHERE source_id = @sourceId AND band_type = @bandType AND team_key = @teamKey
            """;
        cmd.Parameters.AddWithValue("sourceId", sourceId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRegisteredBandProcessingStatus(reader) : null;
    }

    public void StartRegisteredBandProcessing(string sourceId, string bandType, string teamKey)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE registered_band_processing_status
            SET status = 'in_progress',
                started_at = COALESCE(started_at, @now),
                last_resumed_at = @now,
                error_message = NULL
            WHERE source_id = @sourceId AND band_type = @bandType AND team_key = @teamKey
            """;
        AddRegisteredBandKeyParameters(cmd, sourceId, bandType, teamKey);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    public void CompleteRegisteredBandProcessing(string sourceId, string bandType, string teamKey, int lookupsChecked, int entriesFound)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE registered_band_processing_status
            SET status = 'complete', lookups_checked = @checked, entries_found = @found,
                completed_at = @now, error_message = NULL
            WHERE source_id = @sourceId AND band_type = @bandType AND team_key = @teamKey
            """;
        AddRegisteredBandKeyParameters(cmd, sourceId, bandType, teamKey);
        cmd.Parameters.AddWithValue("checked", lookupsChecked);
        cmd.Parameters.AddWithValue("found", entriesFound);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    public void FailRegisteredBandProcessing(string sourceId, string bandType, string teamKey, string errorMessage)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE registered_band_processing_status
            SET status = 'error', error_message = @err, completed_at = @now
            WHERE source_id = @sourceId AND band_type = @bandType AND team_key = @teamKey
            """;
        AddRegisteredBandKeyParameters(cmd, sourceId, bandType, teamKey);
        cmd.Parameters.AddWithValue("err", errorMessage);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    public void UpdateRegisteredBandProcessingProgress(string sourceId, string bandType, string teamKey, int lookupsChecked, int entriesFound, int totalLookupsToCheck)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE registered_band_processing_status
            SET lookups_checked = @checked,
                entries_found = @found,
                total_lookups_to_check = GREATEST(total_lookups_to_check, @total)
            WHERE source_id = @sourceId AND band_type = @bandType AND team_key = @teamKey
            """;
        AddRegisteredBandKeyParameters(cmd, sourceId, bandType, teamKey);
        cmd.Parameters.AddWithValue("checked", lookupsChecked);
        cmd.Parameters.AddWithValue("found", entriesFound);
        cmd.Parameters.AddWithValue("total", totalLookupsToCheck);
        cmd.ExecuteNonQuery();
    }

    public void MarkRegisteredBandLookupChecked(string sourceId, string bandType, string teamKey, string songId, string scope, int season, bool entryFound, string? windowId = null)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO registered_band_processing_progress
                (source_id, band_type, team_key, song_id, scope, season, checked, entry_found, checked_at, window_id)
            VALUES (@sourceId, @bandType, @teamKey, @songId, @scope, @season, 1, @found, @now, @windowId)
            ON CONFLICT (source_id, band_type, team_key, song_id, scope, season) DO UPDATE SET
                checked = 1,
                entry_found = EXCLUDED.entry_found,
                checked_at = EXCLUDED.checked_at,
                window_id = EXCLUDED.window_id
            """;
        AddRegisteredBandKeyParameters(cmd, sourceId, bandType, teamKey);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("scope", scope);
        cmd.Parameters.AddWithValue("season", season);
        cmd.Parameters.AddWithValue("found", entryFound ? 1 : 0);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue(
            "windowId",
            RegisteredBandLookupIdentity.ResolveWindowId(scope, season, windowId));
        cmd.ExecuteNonQuery();
    }

    public List<RegisteredBandLookupProgressInfo> GetCheckedRegisteredBandLookups(string sourceId, string bandType, string teamKey)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT song_id, scope, season, entry_found, window_id
            FROM registered_band_processing_progress
            WHERE source_id = @sourceId AND band_type = @bandType AND team_key = @teamKey AND checked = 1
            """;
        AddRegisteredBandKeyParameters(cmd, sourceId, bandType, teamKey);
        var rows = new List<RegisteredBandLookupProgressInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new RegisteredBandLookupProgressInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3) != 0,
                reader.GetString(4)));
        return rows;
    }

    public void MarkRegisteredPlayerBandDiscoveryChecked(string accountId, string songId, string bandType, string scope, int season, bool entryFound, string? windowId = null)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO registered_player_band_discovery_progress
                (account_id, song_id, band_type, scope, season, checked, entry_found, checked_at, window_id)
            VALUES (@accountId, @songId, @bandType, @scope, @season, 1, @found, @now, @windowId)
            ON CONFLICT (account_id, song_id, band_type, scope, season) DO UPDATE SET
                checked = 1,
                entry_found = EXCLUDED.entry_found,
                checked_at = EXCLUDED.checked_at,
                window_id = EXCLUDED.window_id
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", scope);
        cmd.Parameters.AddWithValue("season", season);
        cmd.Parameters.AddWithValue("found", entryFound ? 1 : 0);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue(
            "windowId",
            RegisteredBandLookupIdentity.ResolveWindowId(scope, season, windowId));
        cmd.ExecuteNonQuery();
    }

    public List<RegisteredPlayerBandDiscoveryProgressInfo> GetCheckedRegisteredPlayerBandDiscoveryLookups(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT song_id, band_type, scope, season, entry_found, window_id
            FROM registered_player_band_discovery_progress
            WHERE account_id = @accountId AND checked = 1
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        var rows = new List<RegisteredPlayerBandDiscoveryProgressInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new RegisteredPlayerBandDiscoveryProgressInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4) != 0,
                reader.GetString(5)));
        }
        return rows;
    }

    public int PruneStaleWebRegistrations(DateTime staleBeforeUtc)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        int prunedUsers;
        using (var usersCmd = conn.CreateCommand())
        {
            usersCmd.Transaction = tx;
            usersCmd.CommandText = """
                DELETE FROM registered_users
                WHERE device_id IN (@webTrackerDeviceId, @webBandTrackerDeviceId)
                  AND COALESCE(last_activity_at, registered_at) < @staleBeforeUtc
                """;
            usersCmd.Parameters.AddWithValue("webTrackerDeviceId", WebTrackerDeviceId);
            usersCmd.Parameters.AddWithValue("webBandTrackerDeviceId", WebBandTrackerDeviceId);
            usersCmd.Parameters.AddWithValue("staleBeforeUtc", staleBeforeUtc);
            prunedUsers = usersCmd.ExecuteNonQuery();
        }

        int prunedBands;
        using (var bandsCmd = conn.CreateCommand())
        {
            bandsCmd.Transaction = tx;
            bandsCmd.CommandText = """
                DELETE FROM registered_bands
                WHERE source_id = @sourceId
                  AND COALESCE(last_activity_at, registered_at) < @staleBeforeUtc
                """;
            bandsCmd.Parameters.AddWithValue("sourceId", WebBandTrackerDeviceId);
            bandsCmd.Parameters.AddWithValue("staleBeforeUtc", staleBeforeUtc);
            prunedBands = bandsCmd.ExecuteNonQuery();
        }

        using (var progressCleanupCmd = conn.CreateCommand())
        {
            progressCleanupCmd.Transaction = tx;
            progressCleanupCmd.CommandText = """
                DELETE FROM registered_band_processing_progress p
                WHERE NOT EXISTS (
                    SELECT 1 FROM registered_bands b
                    WHERE b.source_id = p.source_id AND b.band_type = p.band_type AND b.team_key = p.team_key
                )
                """;
            progressCleanupCmd.ExecuteNonQuery();
        }

        using (var statusCleanupCmd = conn.CreateCommand())
        {
            statusCleanupCmd.Transaction = tx;
            statusCleanupCmd.CommandText = """
                DELETE FROM registered_band_processing_status s
                WHERE NOT EXISTS (
                    SELECT 1 FROM registered_bands b
                    WHERE b.source_id = s.source_id AND b.band_type = s.band_type AND b.team_key = s.team_key
                )
                """;
            statusCleanupCmd.ExecuteNonQuery();
        }

        tx.Commit();
        return prunedUsers + prunedBands;
    }

    private static void AddRegisteredBandKeyParameters(NpgsqlCommand cmd, string sourceId, string bandType, string teamKey)
    {
        cmd.Parameters.AddWithValue("sourceId", sourceId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
    }

    private static RegisteredBandProcessingStatusInfo ReadRegisteredBandProcessingStatus(NpgsqlDataReader reader) => new()
    {
        SourceId = reader.GetString(0),
        BandType = reader.GetString(1),
        TeamKey = reader.GetString(2),
        Status = reader.GetString(3),
        LookupsChecked = reader.GetInt32(4),
        EntriesFound = reader.GetInt32(5),
        TotalLookupsToCheck = reader.GetInt32(6),
        StartedAt = reader.IsDBNull(7) ? null : reader.GetDateTime(7).ToString("o"),
        CompletedAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8).ToString("o"),
        LastResumedAt = reader.IsDBNull(9) ? null : reader.GetDateTime(9).ToString("o"),
        ErrorMessage = reader.IsDBNull(10) ? null : reader.GetString(10),
    };

    private static List<string> GetBandMemberAccountIds(NpgsqlConnection conn, string bandType, string teamKey)
    {
        using (var projectionCmd = conn.CreateCommand())
        {
            projectionCmd.CommandText = """
                SELECT member_account_ids
                FROM band_search_team_projection
                WHERE band_type = @bandType AND team_key = @teamKey
                LIMIT 1
                """;
            projectionCmd.Parameters.AddWithValue("bandType", bandType);
            projectionCmd.Parameters.AddWithValue("teamKey", teamKey);
            using var projectionReader = projectionCmd.ExecuteReader();
            if (projectionReader.Read())
            {
                var memberAccountIds = projectionReader.GetFieldValue<string[]>(0)
                    .Where(static accountId => !string.IsNullOrWhiteSpace(accountId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (memberAccountIds.Count > 0) return memberAccountIds;
            }
        }

        using var membersCmd = conn.CreateCommand();
        membersCmd.CommandText = """
            SELECT DISTINCT account_id
            FROM (
                SELECT account_id FROM band_team_membership WHERE band_type = @bandType AND team_key = @teamKey
                UNION
                SELECT account_id FROM band_members WHERE band_type = @bandType AND team_key = @teamKey
            ) AS members
            ORDER BY account_id
            """;
        membersCmd.Parameters.AddWithValue("bandType", bandType);
        membersCmd.Parameters.AddWithValue("teamKey", teamKey);
        var memberIds = new List<string>();
        using var membersReader = membersCmd.ExecuteReader();
        while (membersReader.Read())
            memberIds.Add(membersReader.GetString(0));
        return memberIds;
    }

    public string? GetAccountIdForUsername(string username) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id FROM account_names WHERE LOWER(display_name) = LOWER(@username) LIMIT 1"; cmd.Parameters.AddWithValue("username", username); var result = cmd.ExecuteScalar(); return result is DBNull or null ? null : (string)result; }

    // ── Backfill ─────────────────────────────────────────────────────

    private const string BackfillStatusColumns = "account_id, status, songs_checked, entries_found, total_songs_to_check, started_at, completed_at, last_resumed_at, error_message, rankings_pending, deferred_reason";

    public void EnqueueBackfill(string accountId, int totalSongsToCheck) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO backfill_status (account_id, status, total_songs_to_check, rankings_pending, deferred_reason) VALUES (@id, 'pending', @total, FALSE, NULL) ON CONFLICT(account_id) DO UPDATE SET status = 'pending', songs_checked = CASE WHEN backfill_status.status = 'complete' THEN 0 ELSE backfill_status.songs_checked END, entries_found = CASE WHEN backfill_status.status = 'complete' THEN 0 ELSE backfill_status.entries_found END, started_at = CASE WHEN backfill_status.status = 'complete' THEN NULL ELSE backfill_status.started_at END, completed_at = CASE WHEN backfill_status.status = 'complete' THEN NULL ELSE backfill_status.completed_at END, last_resumed_at = CASE WHEN backfill_status.status = 'complete' THEN NULL ELSE backfill_status.last_resumed_at END, error_message = NULL, total_songs_to_check = EXCLUDED.total_songs_to_check, rankings_pending = backfill_status.rankings_pending, deferred_reason = CASE WHEN backfill_status.deferred_reason = @catalogRefreshReason THEN backfill_status.deferred_reason ELSE NULL END WHERE backfill_status.status != 'complete' OR EXCLUDED.total_songs_to_check > backfill_status.total_songs_to_check"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("total", totalSongsToCheck); cmd.Parameters.AddWithValue("catalogRefreshReason", BackfillDeferredReasons.CatalogRefreshQueue); cmd.ExecuteNonQuery(); }
    public void DeferBackfill(string accountId, int totalSongsToCheck, string reason) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO backfill_status (account_id, status, total_songs_to_check, rankings_pending, deferred_reason) VALUES (@id, 'deferred', @total, FALSE, @reason) ON CONFLICT(account_id) DO UPDATE SET status = 'deferred', songs_checked = CASE WHEN backfill_status.status = 'complete' THEN 0 ELSE backfill_status.songs_checked END, entries_found = CASE WHEN backfill_status.status = 'complete' THEN 0 ELSE backfill_status.entries_found END, started_at = CASE WHEN backfill_status.status = 'complete' THEN NULL ELSE backfill_status.started_at END, completed_at = CASE WHEN backfill_status.status = 'complete' THEN NULL ELSE backfill_status.completed_at END, last_resumed_at = CASE WHEN backfill_status.status = 'complete' THEN NULL ELSE backfill_status.last_resumed_at END, error_message = NULL, total_songs_to_check = EXCLUDED.total_songs_to_check, rankings_pending = backfill_status.rankings_pending, deferred_reason = CASE WHEN backfill_status.deferred_reason = @catalogRefreshReason THEN backfill_status.deferred_reason ELSE EXCLUDED.deferred_reason END WHERE backfill_status.status != 'complete' OR EXCLUDED.total_songs_to_check > backfill_status.total_songs_to_check"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("total", totalSongsToCheck); cmd.Parameters.AddWithValue("reason", reason); cmd.Parameters.AddWithValue("catalogRefreshReason", BackfillDeferredReasons.CatalogRefreshQueue); cmd.ExecuteNonQuery(); }
    public List<BackfillStatusInfo> GetPendingBackfills() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = $"SELECT {BackfillStatusColumns} FROM backfill_status WHERE status IN ('pending', 'in_progress')"; var list = new List<BackfillStatusInfo>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(ReadBackfillStatus(r)); return list; }
    public List<BackfillStatusInfo> GetDeferredBackfills() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = $"SELECT {BackfillStatusColumns} FROM backfill_status WHERE status IN ('deferred', 'in_progress')"; var list = new List<BackfillStatusInfo>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(ReadBackfillStatus(r)); return list; }
    public BackfillStatusInfo? GetBackfillStatus(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = $"SELECT {BackfillStatusColumns} FROM backfill_status WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); using var r = cmd.ExecuteReader(); return r.Read() ? ReadBackfillStatus(r) : null; }
    public void StartBackfill(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE backfill_status
            SET status = 'in_progress',
                started_at = COALESCE(started_at, @now),
                last_resumed_at = @now,
                deferred_reason = CASE
                    WHEN deferred_reason = @catalogRefreshReason THEN deferred_reason
                    ELSE NULL
                END
            WHERE account_id = @id
            """;
        cmd.Parameters.AddWithValue("id", accountId);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue(
            "catalogRefreshReason",
            BackfillDeferredReasons.CatalogRefreshQueue);
        cmd.ExecuteNonQuery();
    }
    public void CompleteBackfill(string accountId, bool rankingsPending = false) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE backfill_status SET status = 'complete', completed_at = @now, rankings_pending = rankings_pending OR @rankingsPending, deferred_reason = CASE WHEN deferred_reason = @catalogRefreshReason THEN deferred_reason ELSE NULL END WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.Parameters.AddWithValue("rankingsPending", rankingsPending); cmd.Parameters.AddWithValue("catalogRefreshReason", BackfillDeferredReasons.CatalogRefreshQueue); cmd.ExecuteNonQuery(); }
    public IReadOnlyList<SoloCurrentProjectionScopeKey> GetBackfillProjectionScopesCompletedBefore(
        IEnumerable<string> accountIds,
        DateTime completedBeforeUtc)
    {
        var ids = accountIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0)
            return [];

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT overlay.song_id, overlay.instrument
            FROM backfill_status backfill
            JOIN leaderboard_entries_overlay overlay
              ON overlay.account_id = backfill.account_id
            WHERE backfill.account_id = ANY(@ids)
              AND backfill.status = 'complete'
              AND backfill.rankings_pending = TRUE
              AND backfill.completed_at IS NOT NULL
              AND backfill.completed_at <= @completedBefore
            ORDER BY overlay.instrument, overlay.song_id
            """;
        cmd.Parameters.AddWithValue("ids", ids);
        cmd.Parameters.AddWithValue("completedBefore", completedBeforeUtc);

        var scopes = new List<SoloCurrentProjectionScopeKey>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            scopes.Add(new SoloCurrentProjectionScopeKey(
                reader.GetString(0),
                reader.GetString(1)));
        }

        return scopes;
    }
    public void ClearBackfillRankingsPending(IEnumerable<string> accountIds, DateTime completedBeforeUtc) { var ids = accountIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if (ids.Length == 0) return; using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE backfill_status SET rankings_pending = FALSE WHERE rankings_pending = TRUE AND account_id = ANY(@ids) AND completed_at IS NOT NULL AND completed_at <= @completedBefore"; cmd.Parameters.AddWithValue("ids", ids); cmd.Parameters.AddWithValue("completedBefore", completedBeforeUtc); cmd.ExecuteNonQuery(); }
    public void FailBackfill(string accountId, string errorMessage) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE backfill_status SET status = 'error', error_message = @err, deferred_reason = CASE WHEN deferred_reason = @catalogRefreshReason THEN deferred_reason ELSE NULL END WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("err", errorMessage); cmd.Parameters.AddWithValue("catalogRefreshReason", BackfillDeferredReasons.CatalogRefreshQueue); cmd.ExecuteNonQuery(); }
    public void UpdateBackfillProgress(string accountId, int songsChecked, int entriesFound) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE backfill_status SET songs_checked = @checked, entries_found = @found WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("checked", songsChecked); cmd.Parameters.AddWithValue("found", entriesFound); cmd.ExecuteNonQuery(); }
    public void MarkBackfillSongChecked(string accountId, string songId, string instrument, bool entryFound) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO backfill_progress (account_id, song_id, instrument, checked, entry_found, checked_at) VALUES (@acct, @song, @inst, 1, @found, @now) ON CONFLICT(account_id, song_id, instrument) DO UPDATE SET checked = 1, entry_found = EXCLUDED.entry_found, checked_at = EXCLUDED.checked_at"; cmd.Parameters.AddWithValue("acct", accountId); cmd.Parameters.AddWithValue("song", songId); cmd.Parameters.AddWithValue("inst", instrument); cmd.Parameters.AddWithValue("found", entryFound ? 1 : 0); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    public void MarkBackfillSongsChecked(string accountId, IReadOnlyCollection<(string SongId, string Instrument, bool EntryFound)> checks)
    {
        if (checks.Count == 0) return;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "CREATE TEMP TABLE _backfill_progress_stage (song_id TEXT, instrument TEXT, entry_found INTEGER) ON COMMIT DROP";
            c.ExecuteNonQuery();
        }
        using (var writer = conn.BeginBinaryImport("COPY _backfill_progress_stage (song_id, instrument, entry_found) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var (songId, instrument, entryFound) in checks)
            {
                writer.StartRow();
                writer.Write(songId, NpgsqlDbType.Text);
                writer.Write(instrument, NpgsqlDbType.Text);
                writer.Write(entryFound ? 1 : 0, NpgsqlDbType.Integer);
            }
            writer.Complete();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO backfill_progress (account_id, song_id, instrument, checked, entry_found, checked_at)
                SELECT @acct, song_id, instrument, 1, entry_found, @now
                FROM _backfill_progress_stage
                ON CONFLICT(account_id, song_id, instrument) DO UPDATE SET
                    checked = 1,
                    entry_found = EXCLUDED.entry_found,
                    checked_at = EXCLUDED.checked_at
                """;
            cmd.Parameters.AddWithValue("acct", accountId);
            cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public HashSet<(string SongId, string Instrument)> GetCheckedBackfillPairs(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id, instrument FROM backfill_progress WHERE account_id = @acct AND checked = 1"; cmd.Parameters.AddWithValue("acct", accountId); var set = new HashSet<(string, string)>(); using var r = cmd.ExecuteReader(); while (r.Read()) set.Add((r.GetString(0), r.GetString(1))); return set; }

    public RegistrationAdmissionResetResult
        ResetRegistrationProgressForAdmittedPairs(
            IReadOnlyCollection<SoloCurrentProjectionScopeKey> pairs,
            string requiredMaxScoreFreezeReason,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The registration admission reset transaction must belong to the supplied connection.",
                nameof(transaction));
        }
        if (string.IsNullOrWhiteSpace(
                requiredMaxScoreFreezeReason)
            || !requiredMaxScoreFreezeReason.StartsWith(
                PublicReadFreezeState
                    .MaxScoreMaintenanceReasonPrefix,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A max-score maintenance freeze reason is required.",
                nameof(requiredMaxScoreFreezeReason));
        }

        var normalizedPairs = pairs
            .Where(pair =>
                !string.IsNullOrWhiteSpace(pair.SongId)
                && !string.IsNullOrWhiteSpace(pair.Instrument))
            .Select(pair => new SoloCurrentProjectionScopeKey(
                pair.SongId.Trim(),
                pair.Instrument.Trim()))
            .Distinct()
            .OrderBy(pair => pair.SongId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Instrument, StringComparer.Ordinal)
            .ToArray();
        if (normalizedPairs.Length == 0)
            return new RegistrationAdmissionResetResult(0, 0, 0, 0);

        using (var guard = connection.CreateCommand())
        {
            guard.Transaction = transaction;
            guard.CommandText = """
                SELECT public_reads_frozen,
                       public_reads_frozen_reason,
                       max_score_mutation_gate_token,
                       current_setting(
                           'fst.max_score_maintenance_lease_token',
                           TRUE)
                FROM scrape_publication_state
                WHERE id = TRUE
                FOR SHARE
                """;
            using var reader = guard.ExecuteReader();
            if (!reader.Read()
                || !reader.GetBoolean(0)
                || reader.IsDBNull(1)
                || !string.Equals(
                    reader.GetString(1),
                    requiredMaxScoreFreezeReason,
                    StringComparison.Ordinal)
                || reader.IsDBNull(2)
                || reader.IsDBNull(3)
                || !string.Equals(
                    reader.GetString(2),
                    reader.GetString(3),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Registration admission reset requires the exact owned max-score maintenance freeze.");
            }
        }

        var affectedAccounts = new List<string>();
        using (var reset = connection.CreateCommand())
        {
            reset.Transaction = transaction;
            reset.CommandText = """
                DELETE FROM backfill_progress progress
                USING unnest(
                    @songIds::text[],
                    @instruments::text[])
                    AS admitted(song_id, instrument)
                WHERE progress.song_id = admitted.song_id
                  AND progress.instrument =
                      admitted.instrument
                  AND progress.checked = 1
                  AND progress.entry_found = 0
                RETURNING progress.account_id
                """;
            reset.Parameters.Add(
                "songIds",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                normalizedPairs
                    .Select(pair => pair.SongId)
                    .ToArray();
            reset.Parameters.Add(
                "instruments",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                normalizedPairs
                    .Select(pair => pair.Instrument)
                    .ToArray();
            using var reader = reset.ExecuteReader();
            while (reader.Read())
                affectedAccounts.Add(reader.GetString(0));
        }

        var accountIds = affectedAccounts
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requeued = 0;
        if (accountIds.Length > 0)
        {
            using var queue = connection.CreateCommand();
            queue.Transaction = transaction;
            queue.CommandText = """
                INSERT INTO backfill_status (
                    account_id,
                    status,
                    songs_checked,
                    entries_found,
                    total_songs_to_check,
                    started_at,
                    completed_at,
                    last_resumed_at,
                    error_message,
                    rankings_pending,
                    deferred_reason)
                SELECT affected.account_id,
                       'deferred',
                       (
                           SELECT COUNT(*)::INTEGER
                           FROM backfill_progress progress
                           WHERE progress.account_id =
                                   affected.account_id
                             AND progress.checked = 1
                       ),
                       (
                           SELECT COUNT(*)::INTEGER
                           FROM backfill_progress progress
                           WHERE progress.account_id =
                                   affected.account_id
                             AND progress.checked = 1
                             AND progress.entry_found = 1
                       ),
                       0,
                       NULL,
                       NULL,
                       NULL,
                       NULL,
                       FALSE,
                       @deferredReason
                FROM unnest(@accountIds::text[])
                    AS affected(account_id)
                ON CONFLICT (account_id) DO UPDATE SET
                    status = 'deferred',
                    songs_checked =
                        EXCLUDED.songs_checked,
                    entries_found =
                        EXCLUDED.entries_found,
                    started_at = NULL,
                    completed_at = NULL,
                    last_resumed_at = NULL,
                    error_message = NULL,
                    deferred_reason =
                        EXCLUDED.deferred_reason
                RETURNING 1
                """;
            queue.Parameters.Add(
                "accountIds",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                accountIds;
            queue.Parameters.AddWithValue(
                "deferredReason",
                BackfillDeferredReasons.PathAdmissionRefresh);
            using var reader = queue.ExecuteReader();
            while (reader.Read())
                requeued++;
        }

        var affectedHistoryAccounts = new List<string>();
        using (var resetHistory = connection.CreateCommand())
        {
            resetHistory.Transaction = transaction;
            resetHistory.CommandText = """
                DELETE FROM history_recon_progress progress
                USING unnest(
                    @songIds::text[],
                    @instruments::text[])
                    AS admitted(song_id, instrument)
                WHERE progress.song_id = admitted.song_id
                  AND progress.instrument =
                      admitted.instrument
                  AND progress.processed = 1
                RETURNING
                    progress.account_id
                """;
            resetHistory.Parameters.Add(
                "songIds",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                normalizedPairs
                    .Select(pair => pair.SongId)
                    .ToArray();
            resetHistory.Parameters.Add(
                "instruments",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                normalizedPairs
                    .Select(pair => pair.Instrument)
                    .ToArray();
            using var reader = resetHistory.ExecuteReader();
            while (reader.Read())
                affectedHistoryAccounts.Add(reader.GetString(0));
        }

        var historyAccountIds = affectedHistoryAccounts
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requeuedHistoryStates =
            new List<(
                string AccountId,
                int ReconstructionVersion,
                string WindowFingerprint,
                long PreviousAdmissionRevision,
                long AdmissionRevision)>();
        if (historyAccountIds.Length > 0)
        {
            using var queueHistory = connection.CreateCommand();
            queueHistory.Transaction = transaction;
            queueHistory.CommandText = """
                UPDATE history_recon_status status
                SET status = 'pending',
                    songs_processed = (
                        SELECT COUNT(*)::INTEGER
                        FROM history_recon_progress remaining
                        WHERE remaining.account_id =
                                status.account_id
                          AND remaining.processed = 1
                          AND remaining.reconstruction_version =
                                status.reconstruction_version
                          AND remaining.window_fingerprint =
                                status.window_fingerprint
                          AND remaining.admission_revision =
                                status.admission_revision
                    ),
                    seasons_queried = 0,
                    history_entries_found = 0,
                    started_at = NULL,
                    completed_at = NULL,
                    error_message = NULL,
                    admission_revision =
                        status.admission_revision + 1
                FROM unnest(
                    @accountIds::text[])
                    AS affected(account_id)
                WHERE status.account_id =
                        affected.account_id
                RETURNING
                    status.account_id,
                    status.reconstruction_version,
                    status.window_fingerprint,
                    status.admission_revision - 1,
                    status.admission_revision
                """;
            queueHistory.Parameters.Add(
                "accountIds",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                historyAccountIds;
            using var reader = queueHistory.ExecuteReader();
            while (reader.Read())
            {
                requeuedHistoryStates.Add((
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4)));
            }
        }

        if (requeuedHistoryStates.Count > 0)
        {
            using var fenceHistory = connection.CreateCommand();
            fenceHistory.Transaction = transaction;
            fenceHistory.CommandText = """
                UPDATE history_recon_progress progress
                SET admission_revision =
                        affected.admission_revision
                FROM unnest(
                    @accountIds::text[],
                    @versions::integer[],
                    @fingerprints::text[],
                    @previousRevisions::bigint[],
                    @revisions::bigint[])
                    AS affected(
                        account_id,
                        reconstruction_version,
                        window_fingerprint,
                        previous_admission_revision,
                        admission_revision)
                WHERE progress.account_id =
                        affected.account_id
                  AND progress.reconstruction_version =
                        affected.reconstruction_version
                  AND progress.window_fingerprint =
                        affected.window_fingerprint
                  AND progress.admission_revision =
                        affected.previous_admission_revision
                """;
            fenceHistory.Parameters.Add(
                "accountIds",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                requeuedHistoryStates
                    .Select(state => state.AccountId)
                    .ToArray();
            fenceHistory.Parameters.Add(
                "versions",
                NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                requeuedHistoryStates
                    .Select(state => state.ReconstructionVersion)
                    .ToArray();
            fenceHistory.Parameters.Add(
                "fingerprints",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                requeuedHistoryStates
                    .Select(state => state.WindowFingerprint)
                    .ToArray();
            fenceHistory.Parameters.Add(
                "previousRevisions",
                NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value =
                requeuedHistoryStates
                    .Select(state => state.PreviousAdmissionRevision)
                    .ToArray();
            fenceHistory.Parameters.Add(
                "revisions",
                NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value =
                requeuedHistoryStates
                    .Select(state => state.AdmissionRevision)
                    .ToArray();
            fenceHistory.ExecuteNonQuery();
        }

        return new RegistrationAdmissionResetResult(
            affectedAccounts.Count,
            requeued,
            affectedHistoryAccounts.Count,
            requeuedHistoryStates.Count);
    }

    public BackfillSongProgressInfo? GetBackfillSongProgress(string accountId, int checkedPairs, int totalPairs)
    {
        var instrumentCount = Math.Max(1, GlobalLeaderboardScraper.AllInstruments.Count);
        var totalSongs = EstimateBackfillSongCount(totalPairs, instrumentCount, roundUp: true);
        if (totalSongs <= 0 && checkedPairs <= 0) return null;

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM (
                SELECT song_id
                FROM backfill_progress
                WHERE account_id = @acct AND checked = 1
                GROUP BY song_id
                HAVING COUNT(DISTINCT instrument) >= @instrumentCount
            ) completed_songs
            """;
        cmd.Parameters.AddWithValue("acct", accountId);
        cmd.Parameters.AddWithValue("instrumentCount", instrumentCount);
        var completedSongs = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        var estimatedCheckedSongs = EstimateBackfillSongCount(checkedPairs, instrumentCount, roundUp: false);
        var songsChecked = Math.Max(completedSongs, estimatedCheckedSongs);
        if (totalSongs <= 0) totalSongs = songsChecked;

        return new BackfillSongProgressInfo
        {
            SongsChecked = Math.Min(songsChecked, totalSongs),
            TotalSongs = totalSongs,
        };
    }

    // ── History reconstruction ───────────────────────────────────────

    public void EnqueueHistoryRecon(string accountId, int totalSongsToProcess)
        => EnqueueHistoryRecon(accountId, totalSongsToProcess, 0, "");
    public void EnqueueHistoryRecon(string accountId, int totalSongsToProcess, int reconstructionVersion, string windowFingerprint)
        => _ = AdmitHistoryRecon(
            accountId,
            totalSongsToProcess,
            reconstructionVersion,
            windowFingerprint);
    public long AdmitHistoryRecon(string accountId, int totalSongsToProcess, int reconstructionVersion, string windowFingerprint)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        long admissionRevision;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO history_recon_status (
                    account_id,
                    status,
                    total_songs_to_process,
                    reconstruction_version,
                    window_fingerprint,
                    admission_revision)
                VALUES (
                    @id,
                    'pending',
                    @total,
                    @version,
                    @fingerprint,
                    1)
                ON CONFLICT(account_id) DO UPDATE SET
                    status = CASE
                        WHEN history_recon_status.reconstruction_version = EXCLUDED.reconstruction_version
                         AND history_recon_status.window_fingerprint = EXCLUDED.window_fingerprint
                         AND history_recon_status.status = 'complete'
                        THEN 'complete'
                        ELSE 'pending'
                    END,
                    songs_processed = CASE
                        WHEN history_recon_status.reconstruction_version = EXCLUDED.reconstruction_version
                         AND history_recon_status.window_fingerprint = EXCLUDED.window_fingerprint
                        THEN history_recon_status.songs_processed
                        ELSE 0
                    END,
                    seasons_queried = CASE
                        WHEN history_recon_status.reconstruction_version = EXCLUDED.reconstruction_version
                         AND history_recon_status.window_fingerprint = EXCLUDED.window_fingerprint
                        THEN history_recon_status.seasons_queried
                        ELSE 0
                    END,
                    history_entries_found = CASE
                        WHEN history_recon_status.reconstruction_version = EXCLUDED.reconstruction_version
                         AND history_recon_status.window_fingerprint = EXCLUDED.window_fingerprint
                        THEN history_recon_status.history_entries_found
                        ELSE 0
                    END,
                    total_songs_to_process = EXCLUDED.total_songs_to_process,
                    started_at = CASE
                        WHEN history_recon_status.reconstruction_version = EXCLUDED.reconstruction_version
                         AND history_recon_status.window_fingerprint = EXCLUDED.window_fingerprint
                        THEN history_recon_status.started_at
                        ELSE NULL
                    END,
                    completed_at = CASE
                        WHEN history_recon_status.reconstruction_version = EXCLUDED.reconstruction_version
                         AND history_recon_status.window_fingerprint = EXCLUDED.window_fingerprint
                         AND history_recon_status.status = 'complete'
                        THEN history_recon_status.completed_at
                        ELSE NULL
                    END,
                    error_message = NULL,
                    reconstruction_version = EXCLUDED.reconstruction_version,
                    window_fingerprint = EXCLUDED.window_fingerprint,
                    admission_revision =
                        history_recon_status.admission_revision + 1
                RETURNING admission_revision
                """;
            cmd.Parameters.AddWithValue("id", accountId);
            cmd.Parameters.AddWithValue("total", totalSongsToProcess);
            cmd.Parameters.AddWithValue("version", reconstructionVersion);
            cmd.Parameters.AddWithValue("fingerprint", windowFingerprint);
            admissionRevision = Convert.ToInt64(cmd.ExecuteScalar());
        }

        using (var cleanup = conn.CreateCommand())
        {
            cleanup.Transaction = tx;
            cleanup.CommandText = """
                DELETE FROM history_recon_progress
                WHERE account_id = @id
                  AND (
                      reconstruction_version <> @version
                      OR window_fingerprint <> @fingerprint)
                """;
            cleanup.Parameters.AddWithValue("id", accountId);
            cleanup.Parameters.AddWithValue("version", reconstructionVersion);
            cleanup.Parameters.AddWithValue("fingerprint", windowFingerprint);
            cleanup.Parameters.AddWithValue("revision", admissionRevision);
            cleanup.ExecuteNonQuery();
        }

        using (var fence = conn.CreateCommand())
        {
            fence.Transaction = tx;
            fence.CommandText = """
                UPDATE history_recon_progress
                SET admission_revision = @revision
                WHERE account_id = @id
                  AND reconstruction_version = @version
                  AND window_fingerprint = @fingerprint
                """;
            fence.Parameters.AddWithValue("id", accountId);
            fence.Parameters.AddWithValue("version", reconstructionVersion);
            fence.Parameters.AddWithValue("fingerprint", windowFingerprint);
            fence.Parameters.AddWithValue("revision", admissionRevision);
            fence.ExecuteNonQuery();
        }

        tx.Commit();
        return admissionRevision;
    }
    public List<HistoryReconStatusInfo> GetPendingHistoryRecons() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, status, songs_processed, total_songs_to_process, seasons_queried, history_entries_found, started_at, completed_at, error_message, reconstruction_version, window_fingerprint, admission_revision FROM history_recon_status WHERE status IN ('pending', 'in_progress')"; var list = new List<HistoryReconStatusInfo>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(ReadHistoryReconStatus(r)); return list; }
    public HistoryReconStatusInfo? GetHistoryReconStatus(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, status, songs_processed, total_songs_to_process, seasons_queried, history_entries_found, started_at, completed_at, error_message, reconstruction_version, window_fingerprint, admission_revision FROM history_recon_status WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); using var r = cmd.ExecuteReader(); return r.Read() ? ReadHistoryReconStatus(r) : null; }
    public void StartHistoryRecon(string accountId) { SimpleUpdate("UPDATE history_recon_status SET status = 'in_progress', started_at = COALESCE(started_at, @now) WHERE account_id = @id", accountId); }
    public void StartHistoryRecon(string accountId, int reconstructionVersion, string windowFingerprint)
        => StartHistoryRecon(accountId, reconstructionVersion, windowFingerprint, admissionRevision: 0);
    public void StartHistoryRecon(string accountId, int reconstructionVersion, string windowFingerprint, long admissionRevision)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE history_recon_status
            SET status = 'in_progress',
                started_at = COALESCE(started_at, @now),
                error_message = NULL
            WHERE account_id = @id
              AND reconstruction_version = @version
              AND window_fingerprint = @fingerprint
              AND (@revision = 0 OR admission_revision = @revision)
            """;
        cmd.Parameters.AddWithValue("id", accountId);
        cmd.Parameters.AddWithValue("version", reconstructionVersion);
        cmd.Parameters.AddWithValue("fingerprint", windowFingerprint);
        cmd.Parameters.AddWithValue("revision", admissionRevision);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }
    public void CompleteHistoryRecon(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE history_recon_status
            SET status = 'complete',
                completed_at = @now
            WHERE account_id = @id
            """;
        cmd.Parameters.AddWithValue("id", accountId);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        if (cmd.ExecuteNonQuery() == 1)
            MarkBackfillPublicationPending(conn, tx, accountId);
        tx.Commit();
    }
    public void CompleteHistoryRecon(string accountId, int reconstructionVersion, string windowFingerprint)
        => CompleteHistoryRecon(accountId, reconstructionVersion, windowFingerprint, admissionRevision: 0);
    public void CompleteHistoryRecon(string accountId, int reconstructionVersion, string windowFingerprint, long admissionRevision)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE history_recon_status
            SET status = 'complete',
                completed_at = @now,
                error_message = NULL
            WHERE account_id = @id
              AND reconstruction_version = @version
              AND window_fingerprint = @fingerprint
              AND (@revision = 0 OR admission_revision = @revision)
            """;
        cmd.Parameters.AddWithValue("id", accountId);
        cmd.Parameters.AddWithValue("version", reconstructionVersion);
        cmd.Parameters.AddWithValue("fingerprint", windowFingerprint);
        cmd.Parameters.AddWithValue("revision", admissionRevision);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        if (cmd.ExecuteNonQuery() == 1)
            MarkBackfillPublicationPending(conn, tx, accountId);
        tx.Commit();
    }
    private static void MarkBackfillPublicationPending(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string accountId)
    {
        using var pending = conn.CreateCommand();
        pending.Transaction = tx;
        pending.CommandText = """
            UPDATE backfill_status
            SET rankings_pending = TRUE,
                completed_at = now()
            WHERE account_id = @id
              AND status = 'complete'
            """;
        pending.Parameters.AddWithValue("id", accountId);
        pending.ExecuteNonQuery();
    }
    public void FailHistoryRecon(string accountId, string errorMessage) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE history_recon_status SET status = 'error', error_message = @err WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("err", errorMessage); cmd.ExecuteNonQuery(); }
    public void FailHistoryRecon(string accountId, string errorMessage, int reconstructionVersion, string windowFingerprint)
        => FailHistoryRecon(accountId, errorMessage, reconstructionVersion, windowFingerprint, admissionRevision: 0);
    public void FailHistoryRecon(string accountId, string errorMessage, int reconstructionVersion, string windowFingerprint, long admissionRevision)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE history_recon_status
            SET status = 'error',
                error_message = @err
            WHERE account_id = @id
              AND reconstruction_version = @version
              AND window_fingerprint = @fingerprint
              AND (@revision = 0 OR admission_revision = @revision)
            """;
        cmd.Parameters.AddWithValue("id", accountId);
        cmd.Parameters.AddWithValue("err", errorMessage);
        cmd.Parameters.AddWithValue("version", reconstructionVersion);
        cmd.Parameters.AddWithValue("fingerprint", windowFingerprint);
        cmd.Parameters.AddWithValue("revision", admissionRevision);
        cmd.ExecuteNonQuery();
    }
    public void UpdateHistoryReconProgress(string accountId, int songsProcessed, int seasonsQueried, int historyEntriesFound) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE history_recon_status SET songs_processed = @songs, seasons_queried = @seasons, history_entries_found = @entries WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("songs", songsProcessed); cmd.Parameters.AddWithValue("seasons", seasonsQueried); cmd.Parameters.AddWithValue("entries", historyEntriesFound); cmd.ExecuteNonQuery(); }
    public void UpdateHistoryReconProgress(string accountId, int songsProcessed, int seasonsQueried, int historyEntriesFound, int reconstructionVersion, string windowFingerprint)
        => UpdateHistoryReconProgress(accountId, songsProcessed, seasonsQueried, historyEntriesFound, reconstructionVersion, windowFingerprint, admissionRevision: 0);
    public void UpdateHistoryReconProgress(string accountId, int songsProcessed, int seasonsQueried, int historyEntriesFound, int reconstructionVersion, string windowFingerprint, long admissionRevision)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE history_recon_status
            SET songs_processed = @songs,
                seasons_queried = @seasons,
                history_entries_found = @entries
            WHERE account_id = @id
              AND reconstruction_version = @version
              AND window_fingerprint = @fingerprint
              AND (@revision = 0 OR admission_revision = @revision)
            """;
        cmd.Parameters.AddWithValue("id", accountId);
        cmd.Parameters.AddWithValue("songs", songsProcessed);
        cmd.Parameters.AddWithValue("seasons", seasonsQueried);
        cmd.Parameters.AddWithValue("entries", historyEntriesFound);
        cmd.Parameters.AddWithValue("version", reconstructionVersion);
        cmd.Parameters.AddWithValue("fingerprint", windowFingerprint);
        cmd.Parameters.AddWithValue("revision", admissionRevision);
        cmd.ExecuteNonQuery();
    }
    public void MarkHistoryReconSongProcessed(string accountId, string songId, string instrument)
        => MarkHistoryReconSongProcessed(accountId, songId, instrument, 0, "");
    public void MarkHistoryReconSongProcessed(string accountId, string songId, string instrument, int reconstructionVersion, string windowFingerprint)
        => MarkHistoryReconSongProcessed(accountId, songId, instrument, reconstructionVersion, windowFingerprint, admissionRevision: 0);
    public void MarkHistoryReconSongProcessed(string accountId, string songId, string instrument, int reconstructionVersion, string windowFingerprint, long admissionRevision)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history_recon_progress (
                account_id,
                song_id,
                instrument,
                processed,
                processed_at,
                reconstruction_version,
                window_fingerprint,
                admission_revision)
            SELECT
                @acct,
                @song,
                @inst,
                1,
                @now,
                @version,
                @fingerprint,
                @revision
            FROM history_recon_status
            WHERE account_id = @acct
              AND reconstruction_version = @version
              AND window_fingerprint = @fingerprint
              AND (@revision = 0 OR admission_revision = @revision)
            FOR UPDATE
            ON CONFLICT(account_id, song_id, instrument) DO UPDATE SET
                processed = 1,
                processed_at = EXCLUDED.processed_at,
                reconstruction_version = EXCLUDED.reconstruction_version,
                window_fingerprint = EXCLUDED.window_fingerprint,
                admission_revision = EXCLUDED.admission_revision
            WHERE history_recon_progress.reconstruction_version = EXCLUDED.reconstruction_version
              AND history_recon_progress.window_fingerprint = EXCLUDED.window_fingerprint
              AND history_recon_progress.admission_revision = EXCLUDED.admission_revision
            """;
        cmd.Parameters.AddWithValue("acct", accountId);
        cmd.Parameters.AddWithValue("song", songId);
        cmd.Parameters.AddWithValue("inst", instrument);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("version", reconstructionVersion);
        cmd.Parameters.AddWithValue("fingerprint", windowFingerprint);
        cmd.Parameters.AddWithValue("revision", admissionRevision);
        cmd.ExecuteNonQuery();
    }
    public HashSet<(string SongId, string Instrument)> GetProcessedHistoryReconPairs(string accountId)
        => GetProcessedHistoryReconPairs(accountId, 0, "");
    public HashSet<(string SongId, string Instrument)> GetProcessedHistoryReconPairs(string accountId, int reconstructionVersion, string windowFingerprint)
        => GetProcessedHistoryReconPairs(accountId, reconstructionVersion, windowFingerprint, admissionRevision: 0);
    public HashSet<(string SongId, string Instrument)> GetProcessedHistoryReconPairs(string accountId, int reconstructionVersion, string windowFingerprint, long admissionRevision) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id, instrument FROM history_recon_progress WHERE account_id = @acct AND processed = 1 AND reconstruction_version = @version AND window_fingerprint = @fingerprint AND (@revision = 0 OR admission_revision = @revision)"; cmd.Parameters.AddWithValue("acct", accountId); cmd.Parameters.AddWithValue("version", reconstructionVersion); cmd.Parameters.AddWithValue("fingerprint", windowFingerprint); cmd.Parameters.AddWithValue("revision", admissionRevision); var set = new HashSet<(string, string)>(); using var r = cmd.ExecuteReader(); while (r.Read()) set.Add((r.GetString(0), r.GetString(1))); return set; }
    public bool CommitStagedHistoryData(
        string accountId,
        IReadOnlyList<ScoreChangeRecord> scoreChanges,
        IReadOnlyList<HistoryReconProgressWrite> historyProgress,
        int reconstructionVersion,
        string windowFingerprint,
        long admissionRevision)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var identity = conn.CreateCommand())
        {
            identity.Transaction = tx;
            identity.CommandText = """
                SELECT 1
                FROM history_recon_status
                WHERE account_id = @accountId
                  AND reconstruction_version = @version
                  AND window_fingerprint = @fingerprint
                  AND admission_revision = @revision
                FOR UPDATE
                """;
            identity.Parameters.AddWithValue("accountId", accountId);
            identity.Parameters.AddWithValue("version", reconstructionVersion);
            identity.Parameters.AddWithValue("fingerprint", windowFingerprint);
            identity.Parameters.AddWithValue("revision", admissionRevision);
            if (identity.ExecuteScalar() is null)
            {
                tx.Rollback();
                return false;
            }
        }

        var now = DateTime.UtcNow;
        if (scoreChanges.Count > 0)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO score_history (song_id, instrument, account_id, old_score, new_score, old_rank, new_rank, accuracy, is_full_combo, stars, percentile, season, score_achieved_at, season_rank, all_time_rank, difficulty, changed_at) " +
                "VALUES (@songId, @instrument, @accountId, @oldScore, @newScore, @oldRank, @newRank, @accuracy, @fc, @stars, @percentile, @season, @scoreAchievedAt, @seasonRank, @allTimeRank, @difficulty, @now) " +
                "ON CONFLICT(account_id, song_id, instrument, new_score, score_achieved_at) DO UPDATE SET " +
                "season_rank = COALESCE(EXCLUDED.season_rank, score_history.season_rank), all_time_rank = COALESCE(EXCLUDED.all_time_rank, score_history.all_time_rank), " +
                "old_score = COALESCE(EXCLUDED.old_score, score_history.old_score), old_rank = COALESCE(EXCLUDED.old_rank, score_history.old_rank), " +
                "season = COALESCE(score_history.season, EXCLUDED.season), " +
                "difficulty = COALESCE(score_history.difficulty, EXCLUDED.difficulty), changed_at = EXCLUDED.changed_at";
            var pSongId = cmd.Parameters.Add("songId", NpgsqlDbType.Text);
            var pInstrument = cmd.Parameters.Add("instrument", NpgsqlDbType.Text);
            var pAccountId = cmd.Parameters.Add("accountId", NpgsqlDbType.Text);
            var pOldScore = cmd.Parameters.Add("oldScore", NpgsqlDbType.Integer);
            var pNewScore = cmd.Parameters.Add("newScore", NpgsqlDbType.Integer);
            var pOldRank = cmd.Parameters.Add("oldRank", NpgsqlDbType.Integer);
            var pNewRank = cmd.Parameters.Add("newRank", NpgsqlDbType.Integer);
            var pAccuracy = cmd.Parameters.Add("accuracy", NpgsqlDbType.Integer);
            var pFc = cmd.Parameters.Add("fc", NpgsqlDbType.Boolean);
            var pStars = cmd.Parameters.Add("stars", NpgsqlDbType.Integer);
            var pPercentile = cmd.Parameters.Add("percentile", NpgsqlDbType.Double);
            var pSeason = cmd.Parameters.Add("season", NpgsqlDbType.Integer);
            var pScoreAchievedAt = cmd.Parameters.Add("scoreAchievedAt", NpgsqlDbType.TimestampTz);
            var pSeasonRank = cmd.Parameters.Add("seasonRank", NpgsqlDbType.Integer);
            var pAllTimeRank = cmd.Parameters.Add("allTimeRank", NpgsqlDbType.Integer);
            var pDifficulty = cmd.Parameters.Add("difficulty", NpgsqlDbType.Integer);
            var pNow = cmd.Parameters.Add("now", NpgsqlDbType.TimestampTz);
            cmd.Prepare();
            foreach (var change in scoreChanges)
            {
                pSongId.Value = change.SongId;
                pInstrument.Value = change.Instrument;
                pAccountId.Value = change.AccountId;
                pOldScore.Value = change.OldScore.HasValue ? change.OldScore.Value : DBNull.Value;
                pNewScore.Value = change.NewScore;
                pOldRank.Value = change.OldRank.HasValue ? change.OldRank.Value : DBNull.Value;
                pNewRank.Value = change.NewRank;
                pAccuracy.Value = change.Accuracy.HasValue ? change.Accuracy.Value : DBNull.Value;
                pFc.Value = change.IsFullCombo.HasValue ? change.IsFullCombo.Value : DBNull.Value;
                pStars.Value = change.Stars.HasValue ? change.Stars.Value : DBNull.Value;
                pPercentile.Value = change.Percentile.HasValue ? change.Percentile.Value : DBNull.Value;
                pSeason.Value = change.Season.HasValue ? change.Season.Value : DBNull.Value;
                pScoreAchievedAt.Value = change.ScoreAchievedAt is not null ? ParseUtc(change.ScoreAchievedAt) : DBNull.Value;
                pSeasonRank.Value = change.SeasonRank.HasValue ? change.SeasonRank.Value : DBNull.Value;
                pAllTimeRank.Value = change.AllTimeRank.HasValue ? change.AllTimeRank.Value : DBNull.Value;
                pDifficulty.Value = change.Difficulty.HasValue ? change.Difficulty.Value : DBNull.Value;
                pNow.Value = now;
                cmd.ExecuteNonQuery();
            }

        }

        var distinctProgress = historyProgress
            .Distinct()
            .ToArray();
        if (distinctProgress.Length > 0)
        {
            using var progress = conn.CreateCommand();
            progress.Transaction = tx;
            progress.CommandText = """
                INSERT INTO history_recon_progress (
                    account_id,
                    song_id,
                    instrument,
                    processed,
                    processed_at,
                    reconstruction_version,
                    window_fingerprint,
                    admission_revision)
                SELECT
                    @accountId,
                    item.song_id,
                    item.instrument,
                    1,
                    @now,
                    @version,
                    @fingerprint,
                    @revision
                FROM unnest(
                    @songIds::text[],
                    @instruments::text[]) AS item(song_id, instrument)
                ON CONFLICT(account_id, song_id, instrument) DO UPDATE SET
                    processed = 1,
                    processed_at = EXCLUDED.processed_at,
                    reconstruction_version = EXCLUDED.reconstruction_version,
                    window_fingerprint = EXCLUDED.window_fingerprint,
                    admission_revision = EXCLUDED.admission_revision
                WHERE history_recon_progress.reconstruction_version = EXCLUDED.reconstruction_version
                  AND history_recon_progress.window_fingerprint = EXCLUDED.window_fingerprint
                  AND history_recon_progress.admission_revision = EXCLUDED.admission_revision
                """;
            progress.Parameters.AddWithValue("accountId", accountId);
            progress.Parameters.AddWithValue("now", now);
            progress.Parameters.AddWithValue("version", reconstructionVersion);
            progress.Parameters.AddWithValue("fingerprint", windowFingerprint);
            progress.Parameters.AddWithValue("revision", admissionRevision);
            progress.Parameters.Add("songIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                distinctProgress.Select(static item => item.SongId).ToArray();
            progress.Parameters.Add("instruments", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                distinctProgress.Select(static item => item.Instrument).ToArray();
            progress.ExecuteNonQuery();
        }

        tx.Commit();
        return true;
    }

    // ── Season windows ───────────────────────────────────────────────

    public void UpsertSeasonWindow(int seasonNumber, string eventId, string windowId, string sourceKind = "legacy")
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO season_windows (
                season_number,
                event_id,
                window_id,
                source_kind,
                discovered_at)
            VALUES (
                @season,
                @eventId,
                @windowId,
                @sourceKind,
                @now)
            ON CONFLICT(season_number) DO UPDATE SET
                event_id = EXCLUDED.event_id,
                window_id = EXCLUDED.window_id,
                source_kind = EXCLUDED.source_kind,
                discovered_at = EXCLUDED.discovered_at
            WHERE (
                CASE EXCLUDED.source_kind
                    WHEN 'event_api' THEN 3
                    WHEN 'legacy' THEN 2
                    WHEN 'probe' THEN 1
                    ELSE 0
                END)
                >= (
                CASE season_windows.source_kind
                    WHEN 'event_api' THEN 3
                    WHEN 'legacy' THEN 2
                    WHEN 'probe' THEN 1
                    ELSE 0
                END)
            """;
        cmd.Parameters.AddWithValue("season", seasonNumber);
        cmd.Parameters.AddWithValue("eventId", eventId);
        cmd.Parameters.AddWithValue("windowId", windowId);
        cmd.Parameters.AddWithValue(
            "sourceKind",
            string.IsNullOrWhiteSpace(sourceKind) ? "legacy" : sourceKind);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }
    public List<SeasonWindowInfo> GetSeasonWindows() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT season_number, event_id, window_id, discovered_at, source_kind FROM season_windows ORDER BY season_number"; var list = new List<SeasonWindowInfo>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(new SeasonWindowInfo { SeasonNumber = r.GetInt32(0), EventId = r.GetString(1), WindowId = r.GetString(2), DiscoveredAt = r.GetDateTime(3).ToString("o"), SourceKind = r.GetString(4) }); return list; }
    public SeasonWindowInfo? GetSeasonWindow(int seasonNumber) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT season_number, event_id, window_id, discovered_at, source_kind FROM season_windows WHERE season_number = @season"; cmd.Parameters.AddWithValue("season", seasonNumber); using var r = cmd.ExecuteReader(); if (!r.Read()) return null; return new SeasonWindowInfo { SeasonNumber = r.GetInt32(0), EventId = r.GetString(1), WindowId = r.GetString(2), DiscoveredAt = r.GetDateTime(3).ToString("o"), SourceKind = r.GetString(4) }; }
    public int GetCurrentSeason() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT COALESCE(MAX(season_number), 0) FROM season_windows"; return Convert.ToInt32(cmd.ExecuteScalar()); }

    // ── Player stats ─────────────────────────────────────────────────

    public void UpsertPlayerStats(PlayerStatsDto stats) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO player_stats (account_id, instrument, songs_played, full_combo_count, gold_star_count, avg_accuracy, best_rank, best_rank_song_id, total_score, percentile_dist, avg_percentile, overall_percentile, updated_at) VALUES (@accountId, @instrument, @songsPlayed, @fcCount, @goldStars, @avgAcc, @bestRank, @bestRankSongId, @totalScore, @pctDist, @avgPct, @overallPct, @now) ON CONFLICT(account_id, instrument) DO UPDATE SET songs_played = EXCLUDED.songs_played, full_combo_count = EXCLUDED.full_combo_count, gold_star_count = EXCLUDED.gold_star_count, avg_accuracy = EXCLUDED.avg_accuracy, best_rank = EXCLUDED.best_rank, best_rank_song_id = EXCLUDED.best_rank_song_id, total_score = EXCLUDED.total_score, percentile_dist = EXCLUDED.percentile_dist, avg_percentile = EXCLUDED.avg_percentile, overall_percentile = EXCLUDED.overall_percentile, updated_at = EXCLUDED.updated_at"; cmd.Parameters.AddWithValue("accountId", stats.AccountId); cmd.Parameters.AddWithValue("instrument", stats.Instrument); cmd.Parameters.AddWithValue("songsPlayed", stats.SongsPlayed); cmd.Parameters.AddWithValue("fcCount", stats.FullComboCount); cmd.Parameters.AddWithValue("goldStars", stats.GoldStarCount); cmd.Parameters.AddWithValue("avgAcc", stats.AvgAccuracy); cmd.Parameters.AddWithValue("bestRank", stats.BestRank); cmd.Parameters.AddWithValue("bestRankSongId", (object?)stats.BestRankSongId ?? DBNull.Value); cmd.Parameters.AddWithValue("totalScore", stats.TotalScore); cmd.Parameters.AddWithValue("pctDist", (object?)stats.PercentileDist ?? DBNull.Value); cmd.Parameters.AddWithValue("avgPct", (object?)stats.AvgPercentile ?? DBNull.Value); cmd.Parameters.AddWithValue("overallPct", (object?)stats.OverallPercentile ?? DBNull.Value); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    public List<PlayerStatsDto> GetPlayerStats(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT instrument, songs_played, full_combo_count, gold_star_count, avg_accuracy, best_rank, best_rank_song_id, total_score, percentile_dist, avg_percentile, overall_percentile FROM player_stats WHERE account_id = @accountId"; cmd.Parameters.AddWithValue("accountId", accountId); var list = new List<PlayerStatsDto>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(new PlayerStatsDto { AccountId = accountId, Instrument = r.GetString(0), SongsPlayed = r.GetInt32(1), FullComboCount = r.GetInt32(2), GoldStarCount = r.GetInt32(3), AvgAccuracy = r.GetDouble(4), BestRank = r.GetInt32(5), BestRankSongId = r.IsDBNull(6) ? null : r.GetString(6), TotalScore = r.GetInt64(7), PercentileDist = r.IsDBNull(8) ? null : r.GetString(8), AvgPercentile = r.IsDBNull(9) ? null : r.GetString(9), OverallPercentile = r.IsDBNull(10) ? null : r.GetString(10) }); return list; }

    // ── Player stats tiers ───────────────────────────────────────────

    private const int PlayerStatsTiersCopyThreshold = 32;

    public void UpsertPlayerStatsTiers(string accountId, string instrument, string tiersJson)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO player_stats_tiers (account_id, instrument, tiers_json, updated_at) VALUES (@accountId, @instrument, @tiers::jsonb, @now) ON CONFLICT(account_id, instrument) DO UPDATE SET tiers_json = EXCLUDED.tiers_json, updated_at = EXCLUDED.updated_at";
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("tiers", tiersJson);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    public void UpsertPlayerStatsTiersBatch(IReadOnlyList<PlayerStatsTiersRow> rows)
    {
        if (rows.Count == 0) return;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        UpsertPlayerStatsTiersBatch(rows, conn, tx);
        tx.Commit();
    }

    public void UpsertPlayerStatsTiersBatch(
        IReadOnlyList<PlayerStatsTiersRow> rows,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (rows.Count == 0)
            return;
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The player-stats transaction must belong to the supplied connection.",
                nameof(transaction));
        }
        if (rows.Count >= PlayerStatsTiersCopyThreshold)
        {
            using (var createCmd = connection.CreateCommand())
            {
                createCmd.Transaction = transaction;
                createCmd.CommandText = "CREATE TEMP TABLE _player_stats_tiers_staging (account_id TEXT NOT NULL, instrument TEXT NOT NULL, tiers_json TEXT NOT NULL, updated_at TIMESTAMPTZ NOT NULL) ON COMMIT DROP";
                createCmd.ExecuteNonQuery();
            }

            var copyNow = DateTime.UtcNow;
            using (var writer = connection.BeginBinaryImport("COPY _player_stats_tiers_staging (account_id, instrument, tiers_json, updated_at) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var row in rows)
                {
                    writer.StartRow();
                    writer.Write(row.AccountId, NpgsqlDbType.Text);
                    writer.Write(row.Instrument, NpgsqlDbType.Text);
                    writer.Write(row.TiersJson, NpgsqlDbType.Text);
                    writer.Write(copyNow, NpgsqlDbType.TimestampTz);
                }
                writer.Complete();
            }

            using (var mergeCmd = connection.CreateCommand())
            {
                mergeCmd.Transaction = transaction;
                mergeCmd.CommandTimeout = 0;
                mergeCmd.CommandText = """
                    INSERT INTO player_stats_tiers (account_id, instrument, tiers_json, updated_at)
                    SELECT DISTINCT ON (account_id, instrument)
                           account_id,
                           instrument,
                           tiers_json::jsonb,
                           updated_at
                    FROM _player_stats_tiers_staging
                    ORDER BY account_id, instrument, updated_at DESC
                    ON CONFLICT(account_id, instrument) DO UPDATE SET
                        tiers_json = EXCLUDED.tiers_json,
                        updated_at = EXCLUDED.updated_at
                    """;
                mergeCmd.ExecuteNonQuery();
            }

            return;
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = 0;
        cmd.CommandText = "INSERT INTO player_stats_tiers (account_id, instrument, tiers_json, updated_at) VALUES (@accountId, @instrument, @tiers::jsonb, @now) ON CONFLICT(account_id, instrument) DO UPDATE SET tiers_json = EXCLUDED.tiers_json, updated_at = EXCLUDED.updated_at";
        var pAcct = cmd.Parameters.Add("accountId", NpgsqlTypes.NpgsqlDbType.Text);
        var pInst = cmd.Parameters.Add("instrument", NpgsqlTypes.NpgsqlDbType.Text);
        var pTiers = cmd.Parameters.Add("tiers", NpgsqlTypes.NpgsqlDbType.Text);
        var pNow = cmd.Parameters.Add("now", NpgsqlTypes.NpgsqlDbType.TimestampTz);
        cmd.Prepare();
        var now = DateTime.UtcNow;
        foreach (var r in rows)
        {
            pAcct.Value = r.AccountId;
            pInst.Value = r.Instrument;
            pTiers.Value = r.TiersJson;
            pNow.Value = now;
            cmd.ExecuteNonQuery();
        }
    }

    public void ReplacePlayerStatsTiersForMaxScoreMaintenance(
        IReadOnlyCollection<string> accountIds,
        IReadOnlyCollection<string> publishedInstruments,
        IReadOnlyList<PlayerStatsTiersRow> rows,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        ArgumentNullException.ThrowIfNull(publishedInstruments);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The maintenance player-tier transaction must belong to the supplied connection.",
                nameof(transaction));
        }

        var normalizedAccountIds = accountIds
            .Where(accountId =>
                !string.IsNullOrWhiteSpace(accountId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(
                accountId => accountId,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedAccountIds.Length != accountIds.Count)
        {
            throw new ArgumentException(
                "Maintenance player-tier accounts must be nonblank and unique.",
                nameof(accountIds));
        }
        var allowedInstruments = publishedInstruments
            .Where(instrument =>
                !string.IsNullOrWhiteSpace(instrument))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        if (allowedInstruments.Count
            != publishedInstruments.Count)
        {
            throw new ArgumentException(
                "Maintenance player-tier instruments must be nonblank and unique.",
                nameof(publishedInstruments));
        }
        var accountSet = normalizedAccountIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var rowKeys = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!accountSet.Contains(row.AccountId)
                || row.Instrument != "Overall"
                   && !allowedInstruments.Contains(
                       row.Instrument)
                || !rowKeys.Add(
                    row.AccountId
                    + "\u001f"
                    + row.Instrument))
            {
                throw new ArgumentException(
                    "Maintenance player-tier rows must be unique and owned by the exact account/publication instrument scope.",
                    nameof(rows));
            }
        }
        if (normalizedAccountIds.Length == 0)
        {
            if (rows.Count != 0)
            {
                throw new ArgumentException(
                    "Maintenance player-tier rows require affected accounts.",
                    nameof(rows));
            }
            return;
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM player_stats_tiers
                WHERE account_id = ANY(@accountIds)
                """;
            delete.Parameters.Add(
                "accountIds",
                NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                normalizedAccountIds;
            delete.ExecuteNonQuery();
        }
        UpsertPlayerStatsTiersBatch(
            rows,
            connection,
            transaction);
    }

    public List<PlayerStatsTiersRow> GetPlayerStatsTiers(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT instrument, tiers_json, updated_at FROM player_stats_tiers WHERE account_id = @accountId";
        cmd.Parameters.AddWithValue("accountId", accountId);
        var list = new List<PlayerStatsTiersRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PlayerStatsTiersRow
            {
                AccountId = accountId,
                Instrument = r.GetString(0),
                TiersJson = r.GetString(1),
                UpdatedAt = r.GetDateTime(2).ToString("o"),
            });
        }
        return list;
    }

    // ── First seen season ────────────────────────────────────────────

    public HashSet<string> GetSongIdsWithFirstSeenVersion(int currentVersion) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id FROM song_first_seen_season WHERE calculation_version = @ver"; cmd.Parameters.AddWithValue("ver", currentVersion); var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) set.Add(r.GetString(0)); return set; }
    public HashSet<string> GetSongIdsWithFirstSeenVersion(int currentVersion, string windowFingerprint, int maxSeason) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id FROM song_first_seen_season WHERE calculation_version = @ver AND window_fingerprint = @fingerprint AND max_season = @maxSeason AND first_seen_season IS NOT NULL"; cmd.Parameters.AddWithValue("ver", currentVersion); cmd.Parameters.AddWithValue("fingerprint", windowFingerprint); cmd.Parameters.AddWithValue("maxSeason", maxSeason); var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) set.Add(r.GetString(0)); return set; }
    public void UpsertFirstSeenSeason(string songId, int? firstSeenSeason, int? minObservedSeason, int estimatedSeason, string? probeResult, int calculationVersion, string windowFingerprint = "", int maxSeason = 0) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO song_first_seen_season (song_id, first_seen_season, min_observed_season, estimated_season, probe_result, calculated_at, calculation_version, window_fingerprint, max_season) VALUES (@songId, @firstSeen, @minObserved, @estimated, @probeResult, @now, @ver, @fingerprint, @maxSeason) ON CONFLICT(song_id) DO UPDATE SET first_seen_season = EXCLUDED.first_seen_season, min_observed_season = EXCLUDED.min_observed_season, estimated_season = EXCLUDED.estimated_season, probe_result = EXCLUDED.probe_result, calculated_at = EXCLUDED.calculated_at, calculation_version = EXCLUDED.calculation_version, window_fingerprint = EXCLUDED.window_fingerprint, max_season = EXCLUDED.max_season"; cmd.Parameters.AddWithValue("songId", songId); cmd.Parameters.AddWithValue("firstSeen", (object?)firstSeenSeason ?? DBNull.Value); cmd.Parameters.AddWithValue("minObserved", (object?)minObservedSeason ?? DBNull.Value); cmd.Parameters.AddWithValue("estimated", estimatedSeason); cmd.Parameters.AddWithValue("probeResult", (object?)probeResult ?? DBNull.Value); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.Parameters.AddWithValue("ver", calculationVersion); cmd.Parameters.AddWithValue("fingerprint", windowFingerprint); cmd.Parameters.AddWithValue("maxSeason", maxSeason); cmd.ExecuteNonQuery(); }
    public Dictionary<string, FirstSeenSeasonInfo> GetAllFirstSeenSeasons() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id, first_seen_season, estimated_season, calculation_version, window_fingerprint, max_season FROM song_first_seen_season"; var dict = new Dictionary<string, FirstSeenSeasonInfo>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) dict[r.GetString(0)] = new FirstSeenSeasonInfo(r.IsDBNull(1) ? null : r.GetInt32(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.GetString(4), r.GetInt32(5)); return dict; }

    // ── Leaderboard population ───────────────────────────────────────

    public void RaiseLeaderboardPopulationFloor(string songId, string instrument, long floor) { if (floor <= 0) return; using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO leaderboard_population (song_id, instrument, total_entries, updated_at) VALUES (@songId, @instrument, @floor, @now) ON CONFLICT (song_id, instrument) DO UPDATE SET total_entries = GREATEST(leaderboard_population.total_entries, EXCLUDED.total_entries), updated_at = CASE WHEN EXCLUDED.total_entries > leaderboard_population.total_entries THEN EXCLUDED.updated_at ELSE leaderboard_population.updated_at END"; cmd.Parameters.AddWithValue("songId", songId); cmd.Parameters.AddWithValue("instrument", instrument); cmd.Parameters.AddWithValue("floor", (int)floor); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }

    public void UpsertLeaderboardPopulation(IReadOnlyList<(string SongId, string Instrument, long TotalEntries)> items)
    {
        if (items.Count == 0) return;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO leaderboard_population (song_id, instrument, total_entries, updated_at) VALUES (@songId, @instrument, @totalEntries, @now) ON CONFLICT (song_id, instrument) DO UPDATE SET total_entries = EXCLUDED.total_entries, updated_at = EXCLUDED.updated_at";
        var pSong = cmd.Parameters.Add("songId", NpgsqlTypes.NpgsqlDbType.Text); var pInst = cmd.Parameters.Add("instrument", NpgsqlTypes.NpgsqlDbType.Text); var pTotal = cmd.Parameters.Add("totalEntries", NpgsqlTypes.NpgsqlDbType.Integer); var pNow = cmd.Parameters.Add("now", NpgsqlTypes.NpgsqlDbType.TimestampTz); cmd.Prepare();
        var now = DateTime.UtcNow;
        foreach (var (songId, instrument, totalEntries) in items) { pSong.Value = songId; pInst.Value = instrument; pTotal.Value = (int)totalEntries; pNow.Value = now; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    public long GetLeaderboardPopulation(string songId, string instrument) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT total_entries FROM leaderboard_population WHERE song_id = @s AND instrument = @i"; cmd.Parameters.AddWithValue("s", songId); cmd.Parameters.AddWithValue("i", instrument); var result = cmd.ExecuteScalar(); return result is DBNull or null ? -1 : Convert.ToInt64(result); }
    public Dictionary<(string SongId, string Instrument), long> GetAllLeaderboardPopulation() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id, instrument, total_entries FROM leaderboard_population"; var dict = new Dictionary<(string, string), long>(); using var r = cmd.ExecuteReader(); while (r.Read()) dict[(r.GetString(0), r.GetString(1))] = r.GetInt32(2); return dict; }

    // ── Rivals ───────────────────────────────────────────────────────

    public void EnsureRivalsStatus(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO rivals_status (account_id, status) VALUES (@id, 'pending') ON CONFLICT DO NOTHING"; cmd.Parameters.AddWithValue("id", accountId); cmd.ExecuteNonQuery(); }
    public void QueueRivalsRecompute(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "INSERT INTO rivals_status (account_id, status) VALUES (@id, 'pending') ON CONFLICT (account_id) DO UPDATE SET status = 'pending', combos_computed = 0, total_combos_to_compute = 0, rivals_found = 0, started_at = NULL, completed_at = NULL, error_message = NULL"; cmd.Parameters.AddWithValue("id", accountId); cmd.ExecuteNonQuery(); }
    public void StartRivals(string accountId, int totalCombosToCompute = 0) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE rivals_status SET status = 'in_progress', started_at = @now, total_combos_to_compute = @total, combos_computed = 0, rivals_found = 0, error_message = NULL WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.Parameters.AddWithValue("total", totalCombosToCompute); cmd.ExecuteNonQuery(); }
    public bool CompleteRivals(string accountId, int combosComputed, int rivalsFound) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE rivals_status SET status = 'complete', combos_computed = @combos, rivals_found = @rivals, algorithm_version = @version, completed_at = @now, error_message = NULL WHERE account_id = @id AND status = 'in_progress'"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("combos", combosComputed); cmd.Parameters.AddWithValue("rivals", rivalsFound); cmd.Parameters.AddWithValue("version", RivalsAlgorithmVersion.SongRivals); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); return cmd.ExecuteNonQuery() == 1; }
    public void FailRivals(string accountId, string errorMessage) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE rivals_status SET status = 'error', error_message = @err, completed_at = @now WHERE account_id = @id AND status = 'in_progress'"; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("err", errorMessage); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    public RivalsStatusInfo? GetRivalsStatus(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, status, combos_computed, total_combos_to_compute, rivals_found, algorithm_version, started_at, completed_at, error_message FROM rivals_status WHERE account_id = @id"; cmd.Parameters.AddWithValue("id", accountId); using var r = cmd.ExecuteReader(); if (!r.Read()) return null; return new RivalsStatusInfo { AccountId = r.GetString(0), Status = r.GetString(1), CombosComputed = r.GetInt32(2), TotalCombosToCompute = r.GetInt32(3), RivalsFound = r.GetInt32(4), AlgorithmVersion = r.GetInt32(5), StartedAt = r.IsDBNull(6) ? null : r.GetDateTime(6).ToString("o"), CompletedAt = r.IsDBNull(7) ? null : r.GetDateTime(7).ToString("o"), ErrorMessage = r.IsDBNull(8) ? null : r.GetString(8) }; }
    public List<string> GetPendingRivalsAccounts() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id FROM rivals_status WHERE status IN ('pending', 'in_progress')"; var list = new List<string>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(r.GetString(0)); return list; }
    public int ResetStaleRivals() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE rivals_status SET status = 'pending', combos_computed = 0, rivals_found = 0, error_message = NULL WHERE status = 'complete' AND (rivals_found = 0 OR algorithm_version < @version)"; cmd.Parameters.AddWithValue("version", RivalsAlgorithmVersion.SongRivals); return cmd.ExecuteNonQuery(); }

    public void UpsertDirtyRivalSongs(IReadOnlyList<RivalDirtySongRow> dirtySongs)
    {
        if (dirtySongs.Count == 0)
            return;

        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO rivals_dirty_songs (account_id, instrument, song_id, dirty_reason, detected_at) VALUES (@accountId, @instrument, @songId, @reason, @detectedAt) ON CONFLICT (account_id, instrument, song_id) DO UPDATE SET dirty_reason = EXCLUDED.dirty_reason, detected_at = EXCLUDED.detected_at";
        var pAccountId = cmd.Parameters.Add("accountId", NpgsqlDbType.Text);
        var pInstrument = cmd.Parameters.Add("instrument", NpgsqlDbType.Text);
        var pSongId = cmd.Parameters.Add("songId", NpgsqlDbType.Text);
        var pReason = cmd.Parameters.Add("reason", NpgsqlDbType.Text);
        var pDetectedAt = cmd.Parameters.Add("detectedAt", NpgsqlDbType.TimestampTz);
        cmd.Prepare();

        foreach (var dirtySong in dirtySongs)
        {
            pAccountId.Value = dirtySong.AccountId;
            pInstrument.Value = dirtySong.Instrument;
            pSongId.Value = dirtySong.SongId;
            pReason.Value = dirtySong.DirtyReason;
            pDetectedAt.Value = ParseUtc(dirtySong.DetectedAt);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public List<string> GetDirtyRivalAccounts()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT account_id FROM rivals_dirty_songs ORDER BY account_id";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(r.GetString(0));
        return list;
    }

    public List<RivalDirtySongRow> GetDirtyRivalSongs(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id, instrument, song_id, dirty_reason, detected_at FROM rivals_dirty_songs WHERE account_id = @id ORDER BY instrument, song_id";
        cmd.Parameters.AddWithValue("id", accountId);
        var list = new List<RivalDirtySongRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new RivalDirtySongRow
            {
                AccountId = r.GetString(0),
                Instrument = r.GetString(1),
                SongId = r.GetString(2),
                DirtyReason = r.GetString(3),
                DetectedAt = r.GetDateTime(4).ToString("o"),
            });
        }

        return list;
    }

    public void ClearDirtyRivalSongs(string accountId, string instrument, IReadOnlyCollection<string> songIds)
    {
        if (songIds.Count == 0)
            return;

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM rivals_dirty_songs WHERE account_id = @id AND instrument = @instrument AND song_id = ANY(@songIds)";
        cmd.Parameters.AddWithValue("id", accountId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("songIds", songIds.ToArray());
        cmd.ExecuteNonQuery();
    }

    public void ClearAllDirtyRivalSongs(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM rivals_dirty_songs WHERE account_id = @id";
        cmd.Parameters.AddWithValue("id", accountId);
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, RivalSongFingerprintRow> GetRivalSongFingerprints(string accountId, string instrument, IReadOnlyCollection<string> songIds)
    {
        var dict = new Dictionary<string, RivalSongFingerprintRow>(StringComparer.OrdinalIgnoreCase);
        if (songIds.Count == 0)
            return dict;

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id, instrument, song_id, user_rank, neighborhood_signature, computed_at FROM rival_song_fingerprints WHERE account_id = @id AND instrument = @instrument AND song_id = ANY(@songIds)";
        cmd.Parameters.AddWithValue("id", accountId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("songIds", songIds.ToArray());
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            dict[r.GetString(2)] = new RivalSongFingerprintRow
            {
                AccountId = r.GetString(0),
                Instrument = r.GetString(1),
                SongId = r.GetString(2),
                UserRank = r.GetInt32(3),
                NeighborhoodSignature = r.GetString(4),
                ComputedAt = r.GetDateTime(5).ToString("o"),
            };
        }

        return dict;
    }

    public Dictionary<string, RivalInstrumentStateRow> GetRivalInstrumentStates(string accountId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id, instrument, song_count, is_eligible, computed_at FROM rival_instrument_state WHERE account_id = @id";
        cmd.Parameters.AddWithValue("id", accountId);
        var dict = new Dictionary<string, RivalInstrumentStateRow>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            dict[r.GetString(1)] = new RivalInstrumentStateRow
            {
                AccountId = r.GetString(0),
                Instrument = r.GetString(1),
                SongCount = r.GetInt32(2),
                IsEligible = r.GetBoolean(3),
                ComputedAt = r.GetDateTime(4).ToString("o"),
            };
        }

        return dict;
    }

    public void ReplaceRivalSelectionState(string accountId, IReadOnlyList<RivalSongFingerprintRow> fingerprints, IReadOnlyList<RivalInstrumentStateRow> instrumentStates)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "SET LOCAL synchronous_commit = off"; c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM rival_song_fingerprints WHERE account_id = @id"; c.Parameters.AddWithValue("id", accountId); c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM rival_instrument_state WHERE account_id = @id"; c.Parameters.AddWithValue("id", accountId); c.ExecuteNonQuery(); }

        if (fingerprints.Count > 0)
        {
            using var writer = conn.BeginBinaryImport(
                "COPY rival_song_fingerprints (account_id, instrument, song_id, user_rank, neighborhood_signature, computed_at) FROM STDIN (FORMAT BINARY)");
            foreach (var row in fingerprints)
            {
                writer.StartRow();
                writer.Write(row.AccountId, NpgsqlDbType.Text);
                writer.Write(row.Instrument, NpgsqlDbType.Text);
                writer.Write(row.SongId, NpgsqlDbType.Text);
                writer.Write(row.UserRank, NpgsqlDbType.Integer);
                writer.Write(row.NeighborhoodSignature, NpgsqlDbType.Text);
                writer.Write(ParseUtc(row.ComputedAt), NpgsqlDbType.TimestampTz);
            }

            writer.Complete();
        }

        if (instrumentStates.Count > 0)
        {
            using var writer = conn.BeginBinaryImport(
                "COPY rival_instrument_state (account_id, instrument, song_count, is_eligible, computed_at) FROM STDIN (FORMAT BINARY)");
            foreach (var row in instrumentStates)
            {
                writer.StartRow();
                writer.Write(row.AccountId, NpgsqlDbType.Text);
                writer.Write(row.Instrument, NpgsqlDbType.Text);
                writer.Write(row.SongCount, NpgsqlDbType.Integer);
                writer.Write(row.IsEligible, NpgsqlDbType.Boolean);
                writer.Write(ParseUtc(row.ComputedAt), NpgsqlDbType.TimestampTz);
            }

            writer.Complete();
        }

        tx.Commit();
    }

    public void ReplaceRivalsData(string userId, IReadOnlyList<UserRivalRow> rivals, IReadOnlyList<RivalSongSampleRow> samples)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "SET LOCAL synchronous_commit = off"; c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM rival_song_samples WHERE user_id = @uid"; c.Parameters.AddWithValue("uid", userId); c.ExecuteNonQuery(); }
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM user_rivals WHERE user_id = @uid"; c.Parameters.AddWithValue("uid", userId); c.ExecuteNonQuery(); }
        if (rivals.Count > 0)
        {
            using var writer = conn.BeginBinaryImport(
                "COPY user_rivals (user_id, rival_account_id, instrument_combo, direction, rival_score, avg_signed_delta, shared_song_count, ahead_count, behind_count, computed_at) FROM STDIN (FORMAT BINARY)");
            foreach (var rv in rivals)
            {
                writer.StartRow();
                writer.Write(rv.UserId, NpgsqlDbType.Text);
                writer.Write(rv.RivalAccountId, NpgsqlDbType.Text);
                writer.Write(rv.InstrumentCombo, NpgsqlDbType.Text);
                writer.Write(rv.Direction, NpgsqlDbType.Text);
                writer.Write((float)rv.RivalScore, NpgsqlDbType.Real);
                writer.Write((float)rv.AvgSignedDelta, NpgsqlDbType.Real);
                writer.Write(rv.SharedSongCount, NpgsqlDbType.Integer);
                writer.Write(rv.AheadCount, NpgsqlDbType.Integer);
                writer.Write(rv.BehindCount, NpgsqlDbType.Integer);
                writer.Write(ParseUtc(rv.ComputedAt), NpgsqlDbType.TimestampTz);
            }

            writer.Complete();
        }
        if (samples.Count > 0)
        {
            using var writer = conn.BeginBinaryImport(
                "COPY rival_song_samples (user_id, rival_account_id, instrument, song_id, user_rank, rival_rank, rank_delta, user_score, rival_score) FROM STDIN (FORMAT BINARY)");
            foreach (var s in samples)
            {
                writer.StartRow();
                writer.Write(s.UserId, NpgsqlDbType.Text);
                writer.Write(s.RivalAccountId, NpgsqlDbType.Text);
                writer.Write(s.Instrument, NpgsqlDbType.Text);
                writer.Write(s.SongId, NpgsqlDbType.Text);
                writer.Write(s.UserRank, NpgsqlDbType.Integer);
                writer.Write(s.RivalRank, NpgsqlDbType.Integer);
                writer.Write(s.RankDelta, NpgsqlDbType.Integer);
                WriteNullableInt(writer, s.UserScore);
                WriteNullableInt(writer, s.RivalScore);
            }

            writer.Complete();
        }
        tx.Commit();
    }

    public List<UserRivalRow> GetUserRivals(string userId, string? instrumentCombo = null, string? direction = null)
    {
        var normalizedRequestedCombo = instrumentCombo is null
            ? null
            : ComboIds.NormalizeSupportedRivalComboParam(instrumentCombo);
        if (instrumentCombo is not null && normalizedRequestedCombo is null)
            return new List<UserRivalRow>();

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        var where = "WHERE user_id = @uid";
        cmd.Parameters.AddWithValue("uid", userId);
        if (direction is not null)
        {
            where += " AND direction = @dir";
            cmd.Parameters.AddWithValue("dir", direction);
        }

        cmd.CommandText = $"SELECT user_id, rival_account_id, instrument_combo, direction, rival_score, avg_signed_delta, shared_song_count, ahead_count, behind_count, computed_at FROM user_rivals {where} ORDER BY rival_score DESC";

        var list = new List<UserRivalRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var rawCombo = r.GetString(2);
            var normalizedStoredCombo = ComboIds.NormalizeSupportedRivalComboParam(rawCombo);
            if (normalizedStoredCombo is null)
                continue;
            if (normalizedRequestedCombo is not null && !normalizedStoredCombo.Equals(normalizedRequestedCombo, StringComparison.OrdinalIgnoreCase))
                continue;

            list.Add(new UserRivalRow
            {
                UserId = r.GetString(0),
                RivalAccountId = r.GetString(1),
                InstrumentCombo = rawCombo,
                Direction = r.GetString(3),
                RivalScore = r.GetDouble(4),
                AvgSignedDelta = r.GetDouble(5),
                SharedSongCount = r.GetInt32(6),
                AheadCount = r.GetInt32(7),
                BehindCount = r.GetInt32(8),
                ComputedAt = r.GetDateTime(9).ToString("o"),
            });
        }

        return list;
    }

    public List<RivalComboSummary> GetRivalCombos(string userId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT instrument_combo, SUM(CASE WHEN direction = 'above' THEN 1 ELSE 0 END), SUM(CASE WHEN direction = 'below' THEN 1 ELSE 0 END) FROM user_rivals WHERE user_id = @uid GROUP BY instrument_combo ORDER BY instrument_combo";
        cmd.Parameters.AddWithValue("uid", userId);

        var list = new List<RivalComboSummary>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var combo = r.GetString(0);
            if (ComboIds.NormalizeSupportedRivalComboParam(combo) is null)
                continue;

            list.Add(new RivalComboSummary
            {
                InstrumentCombo = combo,
                AboveCount = (int)r.GetInt64(1),
                BelowCount = (int)r.GetInt64(2),
            });
        }

        return list;
    }
    public List<RivalSongSampleRow> GetRivalSongSamples(string userId, string rivalAccountId, string? instrument = null) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); var where = "WHERE user_id = @uid AND rival_account_id = @rid"; cmd.Parameters.AddWithValue("uid", userId); cmd.Parameters.AddWithValue("rid", rivalAccountId); if (instrument is not null) { where += " AND instrument = @inst"; cmd.Parameters.AddWithValue("inst", instrument); } cmd.CommandText = $"SELECT user_id, rival_account_id, instrument, song_id, user_rank, rival_rank, rank_delta, user_score, rival_score FROM rival_song_samples {where} ORDER BY ABS(rank_delta) ASC"; return ReadRivalSamples(cmd); }
    public Dictionary<string, List<RivalSongSampleRow>> GetAllRivalSongSamplesForUser(string userId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT user_id, rival_account_id, instrument, song_id, user_rank, rival_rank, rank_delta, user_score, rival_score FROM rival_song_samples WHERE user_id = @uid ORDER BY rival_account_id, ABS(rank_delta) ASC"; cmd.Parameters.AddWithValue("uid", userId); var dict = new Dictionary<string, List<RivalSongSampleRow>>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) { var sample = ReadRivalSample(r); if (!dict.TryGetValue(sample.RivalAccountId, out var list)) { list = new(); dict[sample.RivalAccountId] = list; } list.Add(sample); } return dict; }

    // ── Leaderboard Rivals ───────────────────────────────────────────

    public void ReplaceLeaderboardRivalsData(string userId, string instrument,
        IReadOnlyList<LeaderboardRivalRow> rivals,
        IReadOnlyList<LeaderboardRivalSongSampleRow> samples,
        IReadOnlyCollection<string>? completedRankMethods = null,
        IReadOnlyDictionary<string, int>? userRanks = null)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        ReplaceLeaderboardRivalsData(
            userId,
            instrument,
            rivals,
            samples,
            completedRankMethods,
            userRanks,
            conn,
            tx);
        tx.Commit();
    }

    public void ReplaceLeaderboardRivalsData(
        string userId,
        string instrument,
        IReadOnlyList<LeaderboardRivalRow> rivals,
        IReadOnlyList<LeaderboardRivalSongSampleRow> samples,
        IReadOnlyCollection<string>? completedRankMethods,
        IReadOnlyDictionary<string, int>? userRanks,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);
        ArgumentNullException.ThrowIfNull(rivals);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The leaderboard-rivals transaction must belong to the supplied connection.",
                nameof(transaction));
        }
        using (var c = connection.CreateCommand()) { c.Transaction = transaction; c.CommandText = "SET LOCAL synchronous_commit = off"; c.ExecuteNonQuery(); }

        // Delete existing rivals + samples for this user/instrument
        using (var d = connection.CreateCommand()) { d.Transaction = transaction; d.CommandText = "DELETE FROM leaderboard_rival_song_samples WHERE user_id = @uid AND instrument = @inst"; d.Parameters.AddWithValue("uid", userId); d.Parameters.AddWithValue("inst", instrument); d.ExecuteNonQuery(); }
        using (var d = connection.CreateCommand()) { d.Transaction = transaction; d.CommandText = "DELETE FROM leaderboard_rivals WHERE user_id = @uid AND instrument = @inst"; d.Parameters.AddWithValue("uid", userId); d.Parameters.AddWithValue("inst", instrument); d.ExecuteNonQuery(); }
        using (var d = connection.CreateCommand()) { d.Transaction = transaction; d.CommandText = "DELETE FROM leaderboard_rivals_state WHERE user_id = @uid AND instrument = @inst"; d.Parameters.AddWithValue("uid", userId); d.Parameters.AddWithValue("inst", instrument); d.ExecuteNonQuery(); }

        if (completedRankMethods is { Count: > 0 })
        {
            using var state = connection.CreateCommand();
            state.Transaction = transaction;
            state.CommandText = """
                INSERT INTO leaderboard_rivals_state (
                    user_id, instrument, rank_method, user_rank, computed_at)
                VALUES (@uid, @instrument, @rankMethod, @userRank, now())
                """;
            state.Parameters.AddWithValue("uid", userId);
            state.Parameters.AddWithValue("instrument", instrument);
            state.Parameters.Add(new NpgsqlParameter("rankMethod", NpgsqlDbType.Text));
            state.Parameters.Add(new NpgsqlParameter("userRank", NpgsqlDbType.Integer));
            state.Prepare();
            foreach (var rankMethod in completedRankMethods.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                state.Parameters["rankMethod"].Value = rankMethod;
                state.Parameters["userRank"].Value =
                    userRanks is not null && userRanks.TryGetValue(rankMethod, out var userRank)
                        ? userRank
                        : DBNull.Value;
                state.ExecuteNonQuery();
            }
        }

        // Insert rivals
        if (rivals.Count > 0)
        {
            using var writer = connection.BeginBinaryImport(
                "COPY leaderboard_rivals (user_id, rival_account_id, instrument, rank_method, direction, user_rank, rival_rank, shared_song_count, ahead_count, behind_count, avg_signed_delta, computed_at) FROM STDIN (FORMAT BINARY)");
            foreach (var r in rivals)
            {
                writer.StartRow();
                writer.Write(r.UserId, NpgsqlDbType.Text);
                writer.Write(r.RivalAccountId, NpgsqlDbType.Text);
                writer.Write(r.Instrument, NpgsqlDbType.Text);
                writer.Write(r.RankMethod, NpgsqlDbType.Text);
                writer.Write(r.Direction, NpgsqlDbType.Text);
                writer.Write(r.UserRank, NpgsqlDbType.Integer);
                writer.Write(r.RivalRank, NpgsqlDbType.Integer);
                writer.Write(r.SharedSongCount, NpgsqlDbType.Integer);
                writer.Write(r.AheadCount, NpgsqlDbType.Integer);
                writer.Write(r.BehindCount, NpgsqlDbType.Integer);
                writer.Write((float)r.AvgSignedDelta, NpgsqlDbType.Real);
                writer.Write(ParseUtc(r.ComputedAt), NpgsqlDbType.TimestampTz);
            }

            writer.Complete();
        }

        // Insert samples
        if (samples.Count > 0)
        {
            using var writer = connection.BeginBinaryImport(
                "COPY leaderboard_rival_song_samples (user_id, rival_account_id, instrument, rank_method, song_id, user_rank, rival_rank, rank_delta, user_score, rival_score) FROM STDIN (FORMAT BINARY)");
            foreach (var s in samples)
            {
                writer.StartRow();
                writer.Write(s.UserId, NpgsqlDbType.Text);
                writer.Write(s.RivalAccountId, NpgsqlDbType.Text);
                writer.Write(s.Instrument, NpgsqlDbType.Text);
                writer.Write(s.RankMethod, NpgsqlDbType.Text);
                writer.Write(s.SongId, NpgsqlDbType.Text);
                writer.Write(s.UserRank, NpgsqlDbType.Integer);
                writer.Write(s.RivalRank, NpgsqlDbType.Integer);
                writer.Write(s.RankDelta, NpgsqlDbType.Integer);
                WriteNullableInt(writer, s.UserScore);
                WriteNullableInt(writer, s.RivalScore);
            }

            writer.Complete();
        }
    }

    public List<LeaderboardRivalRow> GetLeaderboardRivals(string userId, string? instrument = null, string? rankMethod = null, string? direction = null)
    {
        using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand();
        var where = "WHERE user_id = @uid"; cmd.Parameters.AddWithValue("uid", userId);
        if (instrument is not null) { where += " AND instrument = @inst"; cmd.Parameters.AddWithValue("inst", instrument); }
        if (rankMethod is not null) { where += " AND rank_method = @rm"; cmd.Parameters.AddWithValue("rm", rankMethod); }
        if (direction is not null) { where += " AND direction = @dir"; cmd.Parameters.AddWithValue("dir", direction); }
        cmd.CommandText = $"SELECT user_id, rival_account_id, instrument, rank_method, direction, user_rank, rival_rank, shared_song_count, ahead_count, behind_count, avg_signed_delta, computed_at FROM leaderboard_rivals {where} ORDER BY rank_method, direction";
        var list = new List<LeaderboardRivalRow>(); using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new LeaderboardRivalRow { UserId = r.GetString(0), RivalAccountId = r.GetString(1), Instrument = r.GetString(2), RankMethod = r.GetString(3), Direction = r.GetString(4), UserRank = r.GetInt32(5), RivalRank = r.GetInt32(6), SharedSongCount = r.GetInt32(7), AheadCount = r.GetInt32(8), BehindCount = r.GetInt32(9), AvgSignedDelta = r.GetDouble(10), ComputedAt = r.GetDateTime(11).ToString("o") });
        return list;
    }

    public Dictionary<string, int?> GetLeaderboardRivalUserRanks(
        string userId,
        string instrument)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT rank_method, user_rank
            FROM leaderboard_rivals_state
            WHERE user_id = @userId
              AND instrument = @instrument
            """;
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        var result = new Dictionary<string, int?>(
            StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] =
                reader.IsDBNull(1) ? null : reader.GetInt32(1);
        }
        return result;
    }

    public List<LeaderboardRivalSongSampleRow> GetLeaderboardRivalSongSamples(string userId, string rivalAccountId, string instrument, string rankMethod)
    {
        using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT user_id, rival_account_id, instrument, rank_method, song_id, user_rank, rival_rank, rank_delta, user_score, rival_score FROM leaderboard_rival_song_samples WHERE user_id = @uid AND rival_account_id = @rid AND instrument = @inst AND rank_method = @rm ORDER BY ABS(rank_delta) ASC";
        cmd.Parameters.AddWithValue("uid", userId); cmd.Parameters.AddWithValue("rid", rivalAccountId); cmd.Parameters.AddWithValue("inst", instrument); cmd.Parameters.AddWithValue("rm", rankMethod);
        var list = new List<LeaderboardRivalSongSampleRow>(); using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new LeaderboardRivalSongSampleRow { UserId = r.GetString(0), RivalAccountId = r.GetString(1), Instrument = r.GetString(2), RankMethod = r.GetString(3), SongId = r.GetString(4), UserRank = r.GetInt32(5), RivalRank = r.GetInt32(6), RankDelta = r.GetInt32(7), UserScore = r.IsDBNull(8) ? null : r.GetInt32(8), RivalScore = r.IsDBNull(9) ? null : r.GetInt32(9) });
        return list;
    }

    // ── Item shop ────────────────────────────────────────────────────

    public void SaveItemShopTracks(IReadOnlySet<string> songIds, IReadOnlySet<string> leavingTomorrow, IReadOnlySet<string> newSongIds, DateTime scrapedAt)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var c = conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM item_shop_tracks"; c.ExecuteNonQuery(); }
        if (songIds.Count > 0) { using var c = conn.CreateCommand(); c.Transaction = tx; c.CommandText = "INSERT INTO item_shop_tracks (song_id, scraped_at, leaving_tomorrow, is_new) VALUES (@songId, @ts, @leaving, @isNew)"; var pSong = c.Parameters.Add("songId", NpgsqlTypes.NpgsqlDbType.Text); var pTs = c.Parameters.Add("ts", NpgsqlTypes.NpgsqlDbType.TimestampTz); var pLeaving = c.Parameters.Add("leaving", NpgsqlTypes.NpgsqlDbType.Boolean); var pIsNew = c.Parameters.Add("isNew", NpgsqlTypes.NpgsqlDbType.Boolean); c.Prepare(); foreach (var songId in songIds) { pSong.Value = songId; pTs.Value = scrapedAt; pLeaving.Value = leavingTomorrow.Contains(songId); pIsNew.Value = newSongIds.Contains(songId); c.ExecuteNonQuery(); } }
        tx.Commit();
    }

    public (HashSet<string> InShop, HashSet<string> LeavingTomorrow, HashSet<string> NewSongIds) LoadItemShopTracks() { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT song_id, leaving_tomorrow, is_new FROM item_shop_tracks"; var inShop = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var leaving = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var newSongIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase); using var r = cmd.ExecuteReader(); while (r.Read()) { var songId = r.GetString(0); inShop.Add(songId); if (r.GetBoolean(1)) leaving.Add(songId); if (r.GetBoolean(2)) newSongIds.Add(songId); } return (inShop, leaving, newSongIds); }

    // ── Composite rankings ───────────────────────────────────────────

    public void ReplaceCompositeRankings(IReadOnlyList<CompositeRankingDto> rankings)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        ReplaceCompositeRankings(rankings, conn, tx);
        tx.Commit();
    }

    public void ReplaceCompositeRankings(
        IReadOnlyList<CompositeRankingDto> rankings,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(rankings);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The composite-ranking transaction must belong to the supplied connection.",
                nameof(transaction));
        }
        using (var c = connection.CreateCommand()) { c.Transaction = transaction; c.CommandText = "TRUNCATE composite_rankings"; c.ExecuteNonQuery(); }
        using (var c = connection.CreateCommand()) { c.Transaction = transaction; c.CommandText = "SET LOCAL synchronous_commit = off"; c.ExecuteNonQuery(); }
        if (rankings.Count > 0)
        {
            var now = DateTime.UtcNow;
            using var writer = connection.BeginBinaryImport(
                "COPY composite_rankings (account_id, instruments_played, total_songs_played, composite_rating, composite_rank, guitar_adjusted_skill, guitar_skill_rank, bass_adjusted_skill, bass_skill_rank, drums_adjusted_skill, drums_skill_rank, vocals_adjusted_skill, vocals_skill_rank, pro_guitar_adjusted_skill, pro_guitar_skill_rank, pro_bass_adjusted_skill, pro_bass_skill_rank, pro_vocals_adjusted_skill, pro_vocals_skill_rank, pro_cymbals_adjusted_skill, pro_cymbals_skill_rank, pro_drums_adjusted_skill, pro_drums_skill_rank, composite_rating_weighted, composite_rank_weighted, composite_rating_fcrate, composite_rank_fcrate, composite_rating_totalscore, composite_rank_totalscore, composite_rating_maxscore, composite_rank_maxscore, computed_at) FROM STDIN (FORMAT BINARY)");
            foreach (var rv in rankings)
            {
                writer.StartRow();
                writer.Write(rv.AccountId, NpgsqlDbType.Text);
                writer.Write(rv.InstrumentsPlayed, NpgsqlDbType.Integer);
                writer.Write(rv.TotalSongsPlayed, NpgsqlDbType.Integer);
                writer.Write((float)rv.CompositeRating, NpgsqlDbType.Real);
                writer.Write(rv.CompositeRank, NpgsqlDbType.Integer);
                WriteNullableReal(writer, rv.GuitarAdjustedSkill);
                WriteNullableInt(writer, rv.GuitarSkillRank);
                WriteNullableReal(writer, rv.BassAdjustedSkill);
                WriteNullableInt(writer, rv.BassSkillRank);
                WriteNullableReal(writer, rv.DrumsAdjustedSkill);
                WriteNullableInt(writer, rv.DrumsSkillRank);
                WriteNullableReal(writer, rv.VocalsAdjustedSkill);
                WriteNullableInt(writer, rv.VocalsSkillRank);
                WriteNullableReal(writer, rv.ProGuitarAdjustedSkill);
                WriteNullableInt(writer, rv.ProGuitarSkillRank);
                WriteNullableReal(writer, rv.ProBassAdjustedSkill);
                WriteNullableInt(writer, rv.ProBassSkillRank);
                WriteNullableReal(writer, rv.ProVocalsAdjustedSkill);
                WriteNullableInt(writer, rv.ProVocalsSkillRank);
                WriteNullableReal(writer, rv.ProCymbalsAdjustedSkill);
                WriteNullableInt(writer, rv.ProCymbalsSkillRank);
                WriteNullableReal(writer, rv.ProDrumsAdjustedSkill);
                WriteNullableInt(writer, rv.ProDrumsSkillRank);
                WriteNullableReal(writer, rv.CompositeRatingWeighted);
                WriteNullableInt(writer, rv.CompositeRankWeighted);
                WriteNullableReal(writer, rv.CompositeRatingFcRate);
                WriteNullableInt(writer, rv.CompositeRankFcRate);
                WriteNullableReal(writer, rv.CompositeRatingTotalScore);
                WriteNullableInt(writer, rv.CompositeRankTotalScore);
                WriteNullableReal(writer, rv.CompositeRatingMaxScore);
                WriteNullableInt(writer, rv.CompositeRankMaxScore);
                writer.Write(now, NpgsqlDbType.TimestampTz);
            }
            writer.Complete();
        }
    }

    public (List<CompositeRankingDto> Entries, int TotalCount) GetCompositeRankings(int page = 1, int pageSize = 50) { using var conn = _ds.OpenConnection(); int total; using (var c = conn.CreateCommand()) { c.CommandText = "SELECT COUNT(*) FROM composite_rankings"; total = Convert.ToInt32(c.ExecuteScalar()); } using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, instruments_played, total_songs_played, composite_rating, composite_rank, guitar_adjusted_skill, guitar_skill_rank, bass_adjusted_skill, bass_skill_rank, drums_adjusted_skill, drums_skill_rank, vocals_adjusted_skill, vocals_skill_rank, pro_guitar_adjusted_skill, pro_guitar_skill_rank, pro_bass_adjusted_skill, pro_bass_skill_rank, pro_vocals_adjusted_skill, pro_vocals_skill_rank, pro_cymbals_adjusted_skill, pro_cymbals_skill_rank, pro_drums_adjusted_skill, pro_drums_skill_rank, composite_rating_weighted, composite_rank_weighted, composite_rating_fcrate, composite_rank_fcrate, composite_rating_totalscore, composite_rank_totalscore, composite_rating_maxscore, composite_rank_maxscore, computed_at FROM composite_rankings ORDER BY composite_rank ASC LIMIT @limit OFFSET @offset"; cmd.Parameters.AddWithValue("limit", pageSize); cmd.Parameters.AddWithValue("offset", (page - 1) * pageSize); var list = new List<CompositeRankingDto>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(ReadCompositeRanking(r)); return (list, total); }
    public CompositeRankingDto? GetCompositeRanking(string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT account_id, instruments_played, total_songs_played, composite_rating, composite_rank, guitar_adjusted_skill, guitar_skill_rank, bass_adjusted_skill, bass_skill_rank, drums_adjusted_skill, drums_skill_rank, vocals_adjusted_skill, vocals_skill_rank, pro_guitar_adjusted_skill, pro_guitar_skill_rank, pro_bass_adjusted_skill, pro_bass_skill_rank, pro_vocals_adjusted_skill, pro_vocals_skill_rank, pro_cymbals_adjusted_skill, pro_cymbals_skill_rank, pro_drums_adjusted_skill, pro_drums_skill_rank, composite_rating_weighted, composite_rank_weighted, composite_rating_fcrate, composite_rank_fcrate, composite_rating_totalscore, composite_rank_totalscore, composite_rating_maxscore, composite_rank_maxscore, computed_at FROM composite_rankings WHERE account_id = @accountId"; cmd.Parameters.AddWithValue("accountId", accountId); using var r = cmd.ExecuteReader(); return r.Read() ? ReadCompositeRanking(r) : null; }

    // ── Solo family rankings ────────────────────────────────────────

    public void ReplaceSoloFamilyRankings(
        IReadOnlyList<SoloFamilyRankingDto> rankings,
        int lockTimeoutSeconds = 0)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        if (lockTimeoutSeconds > 0)
        {
            using var timeout = conn.CreateCommand();
            timeout.Transaction = tx;
            timeout.CommandText =
                "SELECT set_config('lock_timeout', @lockTimeout, TRUE)";
            timeout.Parameters.AddWithValue(
                "lockTimeout",
                $"{lockTimeoutSeconds}s");
            timeout.ExecuteNonQuery();
        }

        ReplaceSoloFamilyRankings(rankings, conn, tx);
        tx.Commit();
    }

    public void ReplaceSoloFamilyRankings(
        IReadOnlyList<SoloFamilyRankingDto> rankings,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(rankings);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The solo-family replacement transaction must belong to " +
                "the supplied connection.",
                nameof(transaction));
        }

        var candidateScopes = rankings
            .Select(static ranking => ranking.ScopeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingScopes = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        using (var scopeCheck = connection.CreateCommand())
        {
            scopeCheck.Transaction = transaction;
            scopeCheck.CommandText =
                "SELECT DISTINCT scope_id FROM solo_family_rankings";
            using var reader = scopeCheck.ExecuteReader();
            while (reader.Read())
                existingScopes.Add(reader.GetString(0));
        }

        if (rankings.Count == 0)
        {
            if (existingScopes.Count == 0)
                return;

            throw new InvalidOperationException(
                "Solo-family replacement refused an empty candidate.");
        }

        foreach (var existingScope in existingScopes)
        {
            if (!candidateScopes.Contains(existingScope))
            {
                throw new InvalidOperationException(
                    $"Solo-family replacement omitted existing scope {existingScope}.");
            }
        }

        using (var c = connection.CreateCommand()) { c.Transaction = transaction; c.CommandText = "TRUNCATE solo_family_rankings"; c.ExecuteNonQuery(); }
        using (var c = connection.CreateCommand()) { c.Transaction = transaction; c.CommandText = "SET LOCAL synchronous_commit = off"; c.ExecuteNonQuery(); }

        if (rankings.Count > 0)
        {
            var now = DateTime.UtcNow;
            using var writer = connection.BeginBinaryImport(
                "COPY solo_family_rankings (scope_id, account_id, songs_played, total_charted_songs, coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score, total_score_rank, max_score_percent, max_score_percent_rank, full_combo_count, raw_max_score_percent, raw_weighted_rating, computed_at) FROM STDIN (FORMAT BINARY)");
            foreach (var ranking in rankings)
            {
                writer.StartRow();
                writer.Write(ranking.ScopeId, NpgsqlDbType.Text);
                writer.Write(ranking.AccountId, NpgsqlDbType.Text);
                writer.Write(ranking.SongsPlayed, NpgsqlDbType.Integer);
                writer.Write(ranking.TotalChartedSongs, NpgsqlDbType.Integer);
                writer.Write((float)ranking.Coverage, NpgsqlDbType.Real);
                writer.Write((float)ranking.RawSkillRating, NpgsqlDbType.Real);
                writer.Write((float)ranking.AdjustedSkillRating, NpgsqlDbType.Real);
                writer.Write(ranking.AdjustedSkillRank, NpgsqlDbType.Integer);
                writer.Write((float)ranking.WeightedRating, NpgsqlDbType.Real);
                writer.Write(ranking.WeightedRank, NpgsqlDbType.Integer);
                writer.Write((float)ranking.FcRate, NpgsqlDbType.Real);
                writer.Write(ranking.FcRateRank, NpgsqlDbType.Integer);
                writer.Write(ranking.TotalScore, NpgsqlDbType.Bigint);
                writer.Write(ranking.TotalScoreRank, NpgsqlDbType.Integer);
                writer.Write((float)ranking.MaxScorePercent, NpgsqlDbType.Real);
                writer.Write(ranking.MaxScorePercentRank, NpgsqlDbType.Integer);
                writer.Write(ranking.FullComboCount, NpgsqlDbType.Integer);
                WriteNullableReal(writer, ranking.RawMaxScorePercent);
                WriteNullableReal(writer, ranking.RawWeightedRating);
                writer.Write(now, NpgsqlDbType.TimestampTz);
            }

            writer.Complete();
        }
    }

    public (List<SoloFamilyRankingDto> Entries, int TotalCount) GetSoloFamilyRankings(string scopeId, string rankBy = "adjusted", int page = 1, int pageSize = 50)
    {
        var rankColumn = SoloFamilyRankColumn(rankBy);
        using var conn = _ds.OpenConnection();
        int total;
        using (var count = conn.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM solo_family_rankings WHERE scope_id = @scopeId";
            count.Parameters.AddWithValue("scopeId", scopeId);
            total = Convert.ToInt32(count.ExecuteScalar());
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT scope_id, account_id, songs_played, total_charted_songs, coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score, total_score_rank, max_score_percent, max_score_percent_rank, full_combo_count, raw_max_score_percent, raw_weighted_rating, computed_at FROM solo_family_rankings WHERE scope_id = @scopeId ORDER BY {rankColumn} ASC LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("scopeId", scopeId);
        cmd.Parameters.AddWithValue("limit", pageSize);
        cmd.Parameters.AddWithValue("offset", (page - 1) * pageSize);
        var entries = new List<SoloFamilyRankingDto>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) entries.Add(ReadSoloFamilyRanking(r, total));
        return (entries, total);
    }

    public SoloFamilyRankingDto? GetSoloFamilyRanking(string scopeId, string accountId)
    {
        using var conn = _ds.OpenConnection();
        var total = GetSoloFamilyTotalAccounts(conn, scopeId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT scope_id, account_id, songs_played, total_charted_songs, coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score, total_score_rank, max_score_percent, max_score_percent_rank, full_combo_count, raw_max_score_percent, raw_weighted_rating, computed_at FROM solo_family_rankings WHERE scope_id = @scopeId AND account_id = @accountId";
        cmd.Parameters.AddWithValue("scopeId", scopeId);
        cmd.Parameters.AddWithValue("accountId", accountId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSoloFamilyRanking(r, total) : null;
    }

    public Dictionary<string, SoloFamilyRankingDto> GetSoloFamilyRankingsForAccount(string accountId)
    {
        using var conn = _ds.OpenConnection();
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var count = conn.CreateCommand())
        {
            count.CommandText = "SELECT scope_id, COUNT(*) FROM solo_family_rankings GROUP BY scope_id";
            using var reader = count.ExecuteReader();
            while (reader.Read()) totals[reader.GetString(0)] = Convert.ToInt32(reader.GetValue(1));
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT scope_id, account_id, songs_played, total_charted_songs, coverage, raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank, fc_rate, fc_rate_rank, total_score, total_score_rank, max_score_percent, max_score_percent_rank, full_combo_count, raw_max_score_percent, raw_weighted_rating, computed_at FROM solo_family_rankings WHERE account_id = @accountId";
        cmd.Parameters.AddWithValue("accountId", accountId);
        var result = new Dictionary<string, SoloFamilyRankingDto>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var scopeId = r.GetString(0);
            result[scopeId] = ReadSoloFamilyRanking(r, totals.GetValueOrDefault(scopeId));
        }

        return result;
    }

    public (List<CompositeRankingDto> Above, CompositeRankingDto? Self, List<CompositeRankingDto> Below) GetCompositeRankingNeighborhood(string accountId, int radius = 5)
    {
        var self = GetCompositeRanking(accountId);
        if (self is null) return (new(), null, new());
        using var conn = _ds.OpenConnection();
        var above = new List<CompositeRankingDto>();
        using (var cmd = conn.CreateCommand()) { cmd.CommandText = "SELECT account_id, instruments_played, total_songs_played, composite_rating, composite_rank, guitar_adjusted_skill, guitar_skill_rank, bass_adjusted_skill, bass_skill_rank, drums_adjusted_skill, drums_skill_rank, vocals_adjusted_skill, vocals_skill_rank, pro_guitar_adjusted_skill, pro_guitar_skill_rank, pro_bass_adjusted_skill, pro_bass_skill_rank, pro_vocals_adjusted_skill, pro_vocals_skill_rank, pro_cymbals_adjusted_skill, pro_cymbals_skill_rank, pro_drums_adjusted_skill, pro_drums_skill_rank, composite_rating_weighted, composite_rank_weighted, composite_rating_fcrate, composite_rank_fcrate, composite_rating_totalscore, composite_rank_totalscore, composite_rating_maxscore, composite_rank_maxscore, computed_at FROM composite_rankings WHERE composite_rank < @selfRank ORDER BY composite_rank DESC LIMIT @radius"; cmd.Parameters.AddWithValue("selfRank", self.CompositeRank); cmd.Parameters.AddWithValue("radius", radius); using var r = cmd.ExecuteReader(); while (r.Read()) above.Add(ReadCompositeRanking(r)); }
        above.Reverse();
        var below = new List<CompositeRankingDto>();
        using (var cmd = conn.CreateCommand()) { cmd.CommandText = "SELECT account_id, instruments_played, total_songs_played, composite_rating, composite_rank, guitar_adjusted_skill, guitar_skill_rank, bass_adjusted_skill, bass_skill_rank, drums_adjusted_skill, drums_skill_rank, vocals_adjusted_skill, vocals_skill_rank, pro_guitar_adjusted_skill, pro_guitar_skill_rank, pro_bass_adjusted_skill, pro_bass_skill_rank, pro_vocals_adjusted_skill, pro_vocals_skill_rank, pro_cymbals_adjusted_skill, pro_cymbals_skill_rank, pro_drums_adjusted_skill, pro_drums_skill_rank, composite_rating_weighted, composite_rank_weighted, composite_rating_fcrate, composite_rank_fcrate, composite_rating_totalscore, composite_rank_totalscore, composite_rating_maxscore, composite_rank_maxscore, computed_at FROM composite_rankings WHERE composite_rank > @selfRank ORDER BY composite_rank ASC LIMIT @radius"; cmd.Parameters.AddWithValue("selfRank", self.CompositeRank); cmd.Parameters.AddWithValue("radius", radius); using var r = cmd.ExecuteReader(); while (r.Read()) below.Add(ReadCompositeRanking(r)); }
        return (above, self, below);
    }

    public void SnapshotCompositeRankHistory(int retentionDays = 365, bool cleanupRetention = true)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();

        // Step A: Build temp table of each account's latest composite snapshot
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = @"
                CREATE TEMP TABLE _latest_composite ON COMMIT DROP AS
                SELECT DISTINCT ON (account_id)
                    account_id, composite_rank, composite_rating, instruments_played, total_songs_played
                FROM composite_rank_history
                ORDER BY account_id, snapshot_date DESC";
            c.ExecuteNonQuery();
        }

        // Step B: Insert only changed or new accounts
        using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = @"
                INSERT INTO composite_rank_history (account_id, snapshot_date, composite_rank,
                    composite_rating, instruments_played, total_songs_played)
                SELECT cr.account_id, @today, cr.composite_rank,
                    cr.composite_rating, cr.instruments_played, cr.total_songs_played
                FROM composite_rankings cr
                LEFT JOIN _latest_composite lc ON lc.account_id = cr.account_id
                WHERE lc.account_id IS NULL
                  OR lc.composite_rank IS DISTINCT FROM cr.composite_rank
                  OR lc.composite_rating IS DISTINCT FROM cr.composite_rating
                  OR lc.instruments_played IS DISTINCT FROM cr.instruments_played
                  OR lc.total_songs_played IS DISTINCT FROM cr.total_songs_played
                ON CONFLICT (account_id, snapshot_date) DO UPDATE SET
                    composite_rank = EXCLUDED.composite_rank,
                    composite_rating = EXCLUDED.composite_rating,
                    instruments_played = EXCLUDED.instruments_played,
                    total_songs_played = EXCLUDED.total_songs_played";
            c.Parameters.AddWithValue("today", today);
            c.ExecuteNonQuery();
        }

        if (cleanupRetention)
            CleanupCompositeRankHistoryRetention(conn, tx, retentionDays);

        tx.Commit();
    }

    public int CleanupCompositeRankHistoryRetention(
        int retentionDays = 365,
        int batchSize = 5000,
        int maxBatches = 1,
        int commandTimeoutSeconds = 0,
        CancellationToken ct = default)
    {
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (maxBatches <= 0) throw new ArgumentOutOfRangeException(nameof(maxBatches));

        using var conn = _ds.OpenConnection();
        var totalDeleted = 0;

        for (var batch = 0; batch < maxBatches; batch++)
        {
            ct.ThrowIfCancellationRequested();
            using var tx = conn.BeginTransaction();
            var deleted = CleanupCompositeRankHistoryRetentionBatch(
                conn,
                tx,
                retentionDays,
                batchSize,
                commandTimeoutSeconds,
                ct);
            tx.Commit();
            totalDeleted += deleted;

            if (deleted < batchSize)
                break;
        }

        return totalDeleted;
    }

    private static int CleanupCompositeRankHistoryRetention(NpgsqlConnection conn, NpgsqlTransaction tx, int retentionDays)
        => CleanupCompositeRankHistoryRetentionBatch(
            conn,
            tx,
            retentionDays,
            FSTService.DatabaseMaintenanceOptions.DefaultCleanupBatchSize,
            commandTimeoutSeconds: 0,
            ct: default);

    private static int CleanupCompositeRankHistoryRetentionBatch(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        int retentionDays,
        int batchSize,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        if (retentionDays <= 0)
            return 0;

        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-retentionDays);
        using var c = conn.CreateCommand();
        c.Transaction = tx;
        ConfigureCommandTimeout(c, commandTimeoutSeconds);
        c.CommandText = @"
            WITH doomed AS (
                SELECT crh.ctid
                FROM composite_rank_history crh
                WHERE crh.snapshot_date < @cutoff
                  AND EXISTS (
                    SELECT 1 FROM composite_rank_history crh2
                    WHERE crh2.account_id = crh.account_id
                      AND crh2.snapshot_date > crh.snapshot_date
                      AND crh2.snapshot_date <= @cutoff
                  )
                -- Keep the batch unordered so the cutoff BRIN can reject
                -- empty ranges and LIMIT can stop after one bounded batch.
                LIMIT @batchSize
            )
            DELETE FROM composite_rank_history crh
            USING doomed
            WHERE crh.ctid = doomed.ctid";
        c.Parameters.AddWithValue("cutoff", cutoff);
        c.Parameters.AddWithValue("batchSize", batchSize);
        return ExecuteNonQueryWithCancellation(c, ct);
    }

    // ── Combo leaderboard ────────────────────────────────────────────

    public void ReplaceComboLeaderboard(string comboId, IReadOnlyList<(string AccountId, double AdjustedRating, double WeightedRating, double FcRate, long TotalScore, double MaxScorePercent, int SongsPlayed, int FullComboCount)> entries, int totalAccounts)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        ReplaceComboLeaderboard(
            comboId,
            entries,
            totalAccounts,
            conn,
            tx);
        tx.Commit();
    }

    public void ReplaceComboLeaderboard(
        string comboId,
        IReadOnlyList<(string AccountId, double AdjustedRating, double WeightedRating, double FcRate, long TotalScore, double MaxScorePercent, int SongsPlayed, int FullComboCount)> entries,
        int totalAccounts,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comboId);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The combo-ranking transaction must belong to the supplied connection.",
                nameof(transaction));
        }
        using (var c = connection.CreateCommand()) { c.Transaction = transaction; c.CommandText = "SET LOCAL synchronous_commit = off"; c.ExecuteNonQuery(); }
        using (var c = connection.CreateCommand()) { c.Transaction = transaction; c.CommandText = "DELETE FROM combo_leaderboard WHERE combo_id = @id"; c.Parameters.AddWithValue("id", comboId); c.ExecuteNonQuery(); }
        var now = DateTime.UtcNow;
        if (entries.Count > 0)
        {
            using (var c = connection.CreateCommand())
            {
                c.Transaction = transaction;
                c.CommandText = """
                    CREATE TEMP TABLE _combo_leaderboard_staging (
                        combo_id TEXT,
                        account_id TEXT,
                        adjusted_rating DOUBLE PRECISION,
                        weighted_rating DOUBLE PRECISION,
                        fc_rate DOUBLE PRECISION,
                        total_score INTEGER,
                        max_score_percent DOUBLE PRECISION,
                        songs_played INTEGER,
                        full_combo_count INTEGER,
                        computed_at TIMESTAMPTZ
                    ) ON COMMIT DROP
                    """;
                c.ExecuteNonQuery();
            }

            using (var writer = connection.BeginBinaryImport(
                "COPY _combo_leaderboard_staging (combo_id, account_id, adjusted_rating, weighted_rating, fc_rate, total_score, max_score_percent, songs_played, full_combo_count, computed_at) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var e in entries)
                {
                    writer.StartRow();
                    writer.Write(comboId, NpgsqlDbType.Text);
                    writer.Write(e.AccountId, NpgsqlDbType.Text);
                    writer.Write(e.AdjustedRating, NpgsqlDbType.Double);
                    writer.Write(e.WeightedRating, NpgsqlDbType.Double);
                    writer.Write(e.FcRate, NpgsqlDbType.Double);
                    writer.Write((int)e.TotalScore, NpgsqlDbType.Integer);
                    writer.Write(e.MaxScorePercent, NpgsqlDbType.Double);
                    writer.Write(e.SongsPlayed, NpgsqlDbType.Integer);
                    writer.Write(e.FullComboCount, NpgsqlDbType.Integer);
                    writer.Write(now, NpgsqlDbType.TimestampTz);
                }

                writer.Complete();
            }

            using (var c = connection.CreateCommand())
            {
                c.Transaction = transaction;
                c.CommandText = """
                    INSERT INTO combo_leaderboard (
                        combo_id, account_id, adjusted_rating, weighted_rating, fc_rate,
                        total_score, max_score_percent, songs_played, full_combo_count, computed_at)
                    SELECT
                        combo_id, account_id, adjusted_rating, weighted_rating, fc_rate,
                        total_score, max_score_percent, songs_played, full_combo_count, computed_at
                    FROM _combo_leaderboard_staging
                    """;
                c.ExecuteNonQuery();
            }
        }
        using (var c = connection.CreateCommand()) { c.Transaction = transaction; c.CommandText = "INSERT INTO combo_stats (combo_id, total_accounts, computed_at) VALUES (@id, @total, @now) ON CONFLICT(combo_id) DO UPDATE SET total_accounts = EXCLUDED.total_accounts, computed_at = EXCLUDED.computed_at"; c.Parameters.AddWithValue("id", comboId); c.Parameters.AddWithValue("total", totalAccounts); c.Parameters.AddWithValue("now", now); c.ExecuteNonQuery(); }
    }

    public (List<ComboLeaderboardEntry> Entries, int TotalAccounts) GetComboLeaderboard(string comboId, string rankBy = "adjusted", int page = 1, int pageSize = 50) { using var conn = _ds.OpenConnection(); int total; using (var c = conn.CreateCommand()) { c.CommandText = "SELECT total_accounts FROM combo_stats WHERE combo_id = @id"; c.Parameters.AddWithValue("id", comboId); var r2 = c.ExecuteScalar(); total = r2 is DBNull or null ? 0 : Convert.ToInt32(r2); } var orderBy = ComboRankOrderBy(rankBy); using var cmd = conn.CreateCommand(); cmd.CommandText = $"SELECT ROW_NUMBER() OVER (ORDER BY {orderBy}) AS rank, account_id, adjusted_rating, weighted_rating, fc_rate, total_score, max_score_percent, songs_played, full_combo_count, computed_at FROM combo_leaderboard WHERE combo_id = @id ORDER BY {orderBy} LIMIT @limit OFFSET @offset"; cmd.Parameters.AddWithValue("id", comboId); cmd.Parameters.AddWithValue("limit", pageSize); cmd.Parameters.AddWithValue("offset", (page - 1) * pageSize); var list = new List<ComboLeaderboardEntry>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(ReadComboEntry(r)); return (list, total); }
    public ComboLeaderboardEntry? GetComboRank(string comboId, string accountId, string rankBy = "adjusted")
    {
        var rankPredicate = ComboRankPrecedesPredicate(rankBy);
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            WITH target AS (
                SELECT account_id, adjusted_rating, weighted_rating, fc_rate, total_score,
                       max_score_percent, songs_played, full_combo_count, computed_at
                FROM combo_leaderboard
                WHERE combo_id = @id AND account_id = @aid
            )
            SELECT
                (
                    SELECT COUNT(*) + 1
                    FROM combo_leaderboard other, target
                    WHERE other.combo_id = @id
                      AND ({rankPredicate})
                ) AS rank,
                target.account_id,
                target.adjusted_rating,
                target.weighted_rating,
                target.fc_rate,
                target.total_score,
                target.max_score_percent,
                target.songs_played,
                target.full_combo_count,
                target.computed_at
            FROM target";
        cmd.Parameters.AddWithValue("id", comboId);
        cmd.Parameters.AddWithValue("aid", accountId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadComboEntry(r) : null;
    }
    public int GetComboTotalAccounts(string comboId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT total_accounts FROM combo_stats WHERE combo_id = @id"; cmd.Parameters.AddWithValue("id", comboId); var result = cmd.ExecuteScalar(); return result is DBNull or null ? 0 : Convert.ToInt32(result); }

    // ── Band team rankings ──────────────────────────────────────────

    public void RebuildBandTeamRankings(string bandType, int totalChartedSongs, int credibilityThreshold = 50, double populationMedian = 0.5, BandTeamRankingRebuildOptions? options = null)
    {
        RebuildBandTeamRankingsMeasured(bandType, totalChartedSongs, credibilityThreshold, populationMedian, options);
    }

    public void PublishCurrentBandTeamRankings()
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        PublishCurrentBandTeamRankings(conn, tx);
        tx.Commit();
    }

    private static void PublishCurrentBandTeamRankings(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        var options = BandTeamRankingRebuildOptions.Default;
        foreach (var bandType in BandRankingStorageNames.AllBandTypes)
            PublishCurrentBandTeamRankingTables(conn, tx, options, bandType);
    }

    private static void PublishCurrentBandTeamRankingTables(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string bandType)
    {
        var currentRankingTable = BandRankingStorageNames.GetCurrentRankingTable(bandType);
        var currentStatsTable = BandRankingStorageNames.GetCurrentStatsTable(bandType);
        if (!TableExists(conn, tx, currentRankingTable) || !TableExists(conn, tx, currentStatsTable))
            return;

        var buildSuffix = Guid.NewGuid().ToString("N")[..8];
        var buildRankingTable = CreateBandRankingBuildTable(conn, tx, options, bandType, buildSuffix);
        var buildStatsTable = CreateBandRankingStatsBuildTable(conn, tx, options, bandType, buildSuffix);

        using (var copyRankings = conn.CreateCommand())
        {
            ConfigureBandRebuildCommand(copyRankings, tx, options);
            copyRankings.CommandText = $"INSERT INTO {BandRankingStorageNames.QuoteIdentifier(buildRankingTable)} SELECT * FROM {BandRankingStorageNames.QuoteIdentifier(currentRankingTable)}";
            copyRankings.ExecuteNonQuery();
        }

        using (var copyStats = conn.CreateCommand())
        {
            ConfigureBandRebuildCommand(copyStats, tx, options);
            copyStats.CommandText = $"INSERT INTO {BandRankingStorageNames.QuoteIdentifier(buildStatsTable)} SELECT * FROM {BandRankingStorageNames.QuoteIdentifier(currentStatsTable)}";
            copyStats.ExecuteNonQuery();
        }

        CreateBandRankingIndexes(conn, tx, options, buildRankingTable, includeTeamLookup: false);
        CreateBandRankingStatsIndexes(conn, tx, options, buildStatsTable);
        SwapBandPublishedTables(conn, tx, options, bandType, buildRankingTable, buildStatsTable, buildSuffix);
    }

    public BandTeamRankingRebuildMetrics RebuildBandTeamRankingsMeasured(string bandType, int totalChartedSongs, int credibilityThreshold = 50, double populationMedian = 0.5, BandTeamRankingRebuildOptions? options = null)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        var metrics = RebuildBandTeamRankingsMeasured(
            bandType,
            totalChartedSongs,
            credibilityThreshold,
            populationMedian,
            options,
            conn,
            tx);
        tx.Commit();
        return metrics;
    }

    public BandTeamRankingRebuildMetrics RebuildBandTeamRankingsMeasured(
        string bandType,
        int totalChartedSongs,
        int credibilityThreshold,
        double populationMedian,
        BandTeamRankingRebuildOptions? options,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The band-ranking transaction must belong to the supplied connection.",
                nameof(transaction));
        }
        var resolvedOptions = ResolveBandTeamRankingRebuildOptions(options);
        var expectedMembers = BandInstrumentMapping.ExpectedMemberCount(bandType);
        var totalSw = Stopwatch.StartNew();
        var lastCompletedStage = "open_connection";
        var currentStage = resolvedOptions.DisableSynchronousCommit
            ? "disable_synchronous_commit"
            : "materialize_results";
        var syncCommitMs = 0d;
        var materializeMs = 0d;
        var analyzeMs = 0d;
        var distinctComboCount = 0;
        var deleteExistingMs = 0d;
        var insertRankingsMs = 0d;
        var insertStatsMs = 0d;
        var resultRowCount = 0;
        var statsRowCount = 0;
        var rankingGeneration = 0L;
        var conn = connection;
        var tx = transaction;

        try
        {
            currentStage = "ensure_vnext_schema";
            EnsureBandRankHistoryTables(conn, tx);
            lastCompletedStage = "ensure_vnext_schema";

            if (resolvedOptions.DisableSynchronousCommit)
            {
                var syncCommitSw = Stopwatch.StartNew();
                using var cmd = conn.CreateCommand();
                ConfigureBandRebuildCommand(cmd, tx, resolvedOptions);
                cmd.CommandText = "SET LOCAL synchronous_commit = off";
                cmd.ExecuteNonQuery();
                syncCommitSw.Stop();
                syncCommitMs = RoundElapsed(syncCommitSw);
                LogBandRebuildStage(bandType, resolvedOptions, "disable_synchronous_commit", syncCommitMs);
                lastCompletedStage = "disable_synchronous_commit";
            }

            currentStage = "materialize_results";
            var materializeSw = Stopwatch.StartNew();
            var computedAt = DateTime.UtcNow;
            rankingGeneration = CreateBandRankingGeneration(conn, tx, resolvedOptions, bandType, computedAt);
            switch (resolvedOptions.WriteMode)
            {
                case BandTeamRankingWriteMode.Monolithic:
                case BandTeamRankingWriteMode.ComboBatched:
                    MaterializeBandTeamRankingResultsMonolithic(
                        conn,
                        tx,
                        resolvedOptions,
                        bandType,
                        totalChartedSongs,
                        expectedMembers,
                        credibilityThreshold,
                        populationMedian,
                        computedAt);
                    currentStage = "materialize_results";
                    break;
                case BandTeamRankingWriteMode.Phased:
                    MaterializeBandTeamRankingResultsPhased(
                        conn,
                        tx,
                        resolvedOptions,
                        bandType,
                        totalChartedSongs,
                        expectedMembers,
                        credibilityThreshold,
                        populationMedian,
                        computedAt,
                        ref currentStage);
                    currentStage = "materialize_results";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(resolvedOptions.WriteMode), resolvedOptions.WriteMode, "Unsupported band ranking materialization mode.");
            }
            materializeSw.Stop();
            materializeMs = RoundElapsed(materializeSw);
            LogBandRebuildStage(bandType, resolvedOptions, "materialize_results", materializeMs);
            lastCompletedStage = "materialize_results";

            if (resolvedOptions.AnalyzeStagingTable)
            {
                currentStage = "analyze_results";
                var analyzeSw = Stopwatch.StartNew();
                using var cmd = conn.CreateCommand();
                ConfigureBandRebuildCommand(cmd, tx, resolvedOptions);
                cmd.CommandText = "ANALYZE _band_rank_results";
                cmd.ExecuteNonQuery();
                analyzeSw.Stop();
                analyzeMs = RoundElapsed(analyzeSw);
                LogBandRebuildStage(bandType, resolvedOptions, "analyze_results", analyzeMs);
                lastCompletedStage = "analyze_results";
            }

            currentStage = "count_distinct_combos";
            using (var cmd = conn.CreateCommand())
            {
                ConfigureBandRebuildCommand(cmd, tx, resolvedOptions);
                cmd.CommandText = @"
                SELECT COUNT(*)::INT
                FROM (
                    SELECT combo_id
                    FROM _band_rank_results
                    WHERE ranking_scope = 'combo'
                    GROUP BY combo_id
                ) combos;";
                distinctComboCount = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
            LogBandRebuildStage(bandType, resolvedOptions, "count_distinct_combos", 0d, distinctComboCount);
            lastCompletedStage = "count_distinct_combos";

            currentStage = "insert_rankings";
            var insertRankingsSw = Stopwatch.StartNew();
            var buildSuffix = Guid.NewGuid().ToString("N")[..8];

            currentStage = "create_ranking_build_table";
            var createRankingTableSw = Stopwatch.StartNew();
            var buildRankingTable = CreateBandRankingBuildTable(conn, tx, resolvedOptions, bandType, buildSuffix);
            createRankingTableSw.Stop();
            LogBandRebuildStage(bandType, resolvedOptions, "create_ranking_build_table", RoundElapsed(createRankingTableSw));

            currentStage = "insert_ranking_rows";
            var insertRankingRowsSw = Stopwatch.StartNew();
            resultRowCount = resolvedOptions.WriteMode switch
            {
                BandTeamRankingWriteMode.Monolithic => InsertBandTeamRankingRowsMonolithic(conn, tx, resolvedOptions, buildRankingTable, rankingGeneration),
                BandTeamRankingWriteMode.ComboBatched => InsertBandTeamRankingRowsComboBatched(conn, tx, resolvedOptions, buildRankingTable, rankingGeneration),
                BandTeamRankingWriteMode.Phased => InsertBandTeamRankingRowsMonolithic(conn, tx, resolvedOptions, buildRankingTable, rankingGeneration),
                _ => throw new ArgumentOutOfRangeException(nameof(resolvedOptions.WriteMode), resolvedOptions.WriteMode, "Unsupported band ranking write mode."),
            };
            insertRankingRowsSw.Stop();
            LogBandRebuildStage(bandType, resolvedOptions, "insert_ranking_rows", RoundElapsed(insertRankingRowsSw), rowCount: resultRowCount);

            currentStage = "create_ranking_indexes";
            var createRankingIndexesSw = Stopwatch.StartNew();
            CreateBandRankingIndexes(conn, tx, resolvedOptions, buildRankingTable, includeTeamLookup: true);
            createRankingIndexesSw.Stop();
            LogBandRebuildStage(bandType, resolvedOptions, "create_ranking_indexes", RoundElapsed(createRankingIndexesSw));

            insertRankingsSw.Stop();
            insertRankingsMs = RoundElapsed(insertRankingsSw);
            LogBandRebuildStage(bandType, resolvedOptions, "insert_rankings", insertRankingsMs, rowCount: resultRowCount);
            lastCompletedStage = "insert_rankings";

            currentStage = "insert_stats";
            var insertStatsSw = Stopwatch.StartNew();

            currentStage = "create_stats_build_table";
            var createStatsTableSw = Stopwatch.StartNew();
            var buildStatsTable = CreateBandRankingStatsBuildTable(conn, tx, resolvedOptions, bandType, buildSuffix);
            createStatsTableSw.Stop();
            LogBandRebuildStage(bandType, resolvedOptions, "create_stats_build_table", RoundElapsed(createStatsTableSw));

            currentStage = "insert_stats_rows";
            var insertStatsRowsSw = Stopwatch.StartNew();
            statsRowCount = InsertBandTeamRankingStatsRows(conn, tx, resolvedOptions, buildStatsTable);
            insertStatsRowsSw.Stop();
            LogBandRebuildStage(bandType, resolvedOptions, "insert_stats_rows", RoundElapsed(insertStatsRowsSw), rowCount: statsRowCount);

            currentStage = "create_stats_indexes";
            var createStatsIndexesSw = Stopwatch.StartNew();
            CreateBandRankingStatsIndexes(conn, tx, resolvedOptions, buildStatsTable);
            createStatsIndexesSw.Stop();
            LogBandRebuildStage(bandType, resolvedOptions, "create_stats_indexes", RoundElapsed(createStatsIndexesSw));

            insertStatsSw.Stop();
            insertStatsMs = RoundElapsed(insertStatsSw);
            LogBandRebuildStage(bandType, resolvedOptions, "insert_stats", insertStatsMs, rowCount: statsRowCount);
            lastCompletedStage = "insert_stats";

            currentStage = "swap_current";
            var swapSw = Stopwatch.StartNew();
            SwapBandCurrentTables(conn, tx, resolvedOptions, bandType, buildRankingTable, buildStatsTable, buildSuffix);
            CompleteBandRankingGeneration(conn, tx, resolvedOptions, rankingGeneration, bandType, resultRowCount, statsRowCount);
            swapSw.Stop();
            deleteExistingMs = RoundElapsed(swapSw);
            LogBandRebuildStage(bandType, resolvedOptions, "swap_current", deleteExistingMs);
            lastCompletedStage = "swap_current";

            totalSw.Stop();
            var metrics = new BandTeamRankingRebuildMetrics(
                bandType,
                resolvedOptions.WriteMode,
                resultRowCount,
                statsRowCount,
                distinctComboCount,
                materializeMs,
                analyzeMs,
                deleteExistingMs,
                insertRankingsMs,
                insertStatsMs,
                RoundElapsed(totalSw));

            _log.LogInformation(
                "Rebuilt band team rankings for {BandType} using {WriteMode}: rows={ResultRowCount}, stats={StatsRowCount}, combos={DistinctComboCount}, materializeMs={MaterializeResultsMs}, analyzeMs={AnalyzeResultsMs}, deleteMs={DeleteExistingMs}, insertMs={InsertRankingsMs}, statsMs={InsertStatsMs}, totalMs={TotalElapsedMs}",
                metrics.BandType,
                metrics.WriteMode,
                metrics.ResultRowCount,
                metrics.StatsRowCount,
                metrics.DistinctComboCount,
                metrics.MaterializeResultsMs,
                metrics.AnalyzeResultsMs,
                metrics.DeleteExistingMs,
                metrics.InsertRankingsMs,
                metrics.InsertStatsMs,
                metrics.TotalElapsedMs);

            return metrics;
        }
        catch
        {
            totalSw.Stop();
            _log.LogWarning(
                "Band team ranking rebuild failed for {BandType} using {WriteMode}: timeoutSeconds={CommandTimeoutSeconds}, lastCompletedStage={LastCompletedStage}, failingStage={FailingStage}, syncCommitMs={SyncCommitMs}, materializeMs={MaterializeResultsMs}, analyzeMs={AnalyzeResultsMs}, deleteMs={DeleteExistingMs}, insertMs={InsertRankingsMs}, statsMs={InsertStatsMs}, distinctCombos={DistinctComboCount}, resultRows={ResultRowCount}, statsRows={StatsRowCount}, totalMs={TotalElapsedMs}",
                bandType,
                resolvedOptions.WriteMode,
                resolvedOptions.CommandTimeoutSeconds,
                lastCompletedStage,
                currentStage,
                syncCommitMs,
                materializeMs,
                analyzeMs,
                deleteExistingMs,
                insertRankingsMs,
                insertStatsMs,
                distinctComboCount,
                resultRowCount,
                statsRowCount,
                RoundElapsed(totalSw));
            throw;
        }
    }

    private void LogBandRebuildStage(string bandType, BandTeamRankingRebuildOptions options, string stage, double elapsedMs, int? rowCount = null, string? comboId = null)
    {
        _log.LogInformation(
            "[BandRankings.Stage] band_type={BandType} write_mode={WriteMode} timeout_seconds={CommandTimeoutSeconds} stage={Stage} combo_id={ComboId} elapsed_ms={ElapsedMs} row_count={RowCount}",
            bandType,
            options.WriteMode,
            options.CommandTimeoutSeconds,
            stage,
            comboId ?? "-",
            elapsedMs,
            rowCount?.ToString() ?? "-");
    }

    private static BandTeamRankingRebuildOptions ResolveBandTeamRankingRebuildOptions(BandTeamRankingRebuildOptions? options)
    {
        var resolved = options ?? BandTeamRankingRebuildOptions.Default;
        if (resolved.CommandTimeoutSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "CommandTimeoutSeconds must be zero or greater.");

        return resolved;
    }

    private static void ConfigureBandRebuildCommand(NpgsqlCommand cmd, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options)
    {
        cmd.Transaction = tx;
        cmd.CommandTimeout = options.CommandTimeoutSeconds;
    }

    private static long CreateBandRankingGeneration(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        BandTeamRankingRebuildOptions options,
        string bandType,
        DateTime computedAt)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = @"
            INSERT INTO band_team_ranking_generation (band_type, status, computed_at)
            VALUES (@bandType, 'building', @computedAt)
            RETURNING generation_id;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("computedAt", computedAt);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void CompleteBandRankingGeneration(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        BandTeamRankingRebuildOptions options,
        long generationId,
        string bandType,
        int rowCount,
        int scopeCount)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = @"
            UPDATE band_team_ranking_generation
            SET status = 'published',
                published_at = now(),
                ranking_table = @rankingTable,
                stats_table = @statsTable,
                row_count = @rowCount,
                scope_count = @scopeCount,
                updated_at = now()
            WHERE generation_id = @generationId;";
        cmd.Parameters.AddWithValue("generationId", generationId);
        cmd.Parameters.AddWithValue("rankingTable", BandRankingStorageNames.GetCurrentRankingTable(bandType));
        cmd.Parameters.AddWithValue("statsTable", BandRankingStorageNames.GetCurrentStatsTable(bandType));
        cmd.Parameters.AddWithValue("rowCount", rowCount);
        cmd.Parameters.AddWithValue("scopeCount", scopeCount);
        cmd.ExecuteNonQuery();
    }

    private static void MaterializeBandTeamRankingResultsMonolithic(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        BandTeamRankingRebuildOptions options,
        string bandType,
        int totalChartedSongs,
        int expectedMembers,
        int credibilityThreshold,
        double populationMedian,
        DateTime computedAt)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = @"
                CREATE TEMP TABLE _band_rank_results ON COMMIT DROP AS
                WITH NormalizedEntries AS (
                    SELECT
                        be.song_id,
                        be.team_key,
                        be.score,
                        COALESCE(be.accuracy, 0) AS accuracy,
                        COALESCE(be.is_full_combo, FALSE) AS is_full_combo,
                        COALESCE(be.stars, 0) AS stars,
                        COALESCE(be.end_time, '') AS end_time,
                        COALESCE((
                            SELECT string_agg(mapped.instrument, '+' ORDER BY mapped.sort_order, mapped.instrument)
                            FROM (
                                SELECT
                                    CASE part::INT
                                        WHEN 0 THEN 'Solo_Guitar'
                                        WHEN 1 THEN 'Solo_Bass'
                                        WHEN 3 THEN 'Solo_Drums'
                                        WHEN 2 THEN 'Solo_Vocals'
                                        WHEN 4 THEN 'Solo_PeripheralGuitar'
                                        WHEN 5 THEN 'Solo_PeripheralBass'
                                        WHEN 7 THEN 'Solo_PeripheralVocals'
                                        WHEN 8 THEN 'Solo_PeripheralCymbals'
                                        WHEN 6 THEN 'Solo_PeripheralDrums'
                                        ELSE NULL
                                    END AS instrument,
                                    CASE part::INT
                                        WHEN 0 THEN 0
                                        WHEN 1 THEN 1
                                        WHEN 3 THEN 2
                                        WHEN 2 THEN 3
                                        WHEN 4 THEN 4
                                        WHEN 5 THEN 5
                                        WHEN 7 THEN 6
                                        WHEN 8 THEN 7
                                        WHEN 6 THEN 8
                                        ELSE 999
                                    END AS sort_order
                                FROM unnest(string_to_array(be.instrument_combo, ':')) AS parts(part)
                            ) mapped
                            WHERE mapped.instrument IS NOT NULL
                        ), '') AS combo_id
                    FROM band_entries be
                    WHERE be.band_type = @bandType
                      AND NOT be.is_over_threshold
                ),
                OverallChoice AS (
                    SELECT *
                    FROM (
                        SELECT
                            ne.*,
                            ROW_NUMBER() OVER (
                                PARTITION BY ne.song_id, ne.team_key
                                ORDER BY ne.score DESC, ne.end_time ASC, ne.combo_id ASC, ne.team_key ASC
                            ) AS choice_rank
                        FROM NormalizedEntries ne
                    ) ranked
                    WHERE choice_rank = 1
                ),
                OverallValidEntries AS (
                    SELECT
                        oc.song_id,
                        oc.team_key,
                        oc.score,
                        oc.accuracy,
                        oc.is_full_combo,
                        oc.stars,
                        COUNT(*) OVER (PARTITION BY oc.song_id) AS entry_count,
                        CASE
                            WHEN COUNT(*) OVER (PARTITION BY oc.song_id) > 0
                                THEN LN(COUNT(*) OVER (PARTITION BY oc.song_id)::DOUBLE PRECISION) / LN(2.0)
                            ELSE 0.0
                        END AS log_weight,
                        ROW_NUMBER() OVER (
                            PARTITION BY oc.song_id
                            ORDER BY oc.score DESC, oc.end_time ASC, oc.team_key ASC
                        ) AS effective_rank
                    FROM OverallChoice oc
                ),
                OverallAggregated AS (
                    SELECT
                        'overall'::TEXT AS ranking_scope,
                        ''::TEXT AS combo_id,
                        team_key,
                        COUNT(*) AS songs_played,
                        @totalCharted AS total_charted_songs,
                        COUNT(*)::DOUBLE PRECISION / @totalCharted AS coverage,
                        AVG(effective_rank::DOUBLE PRECISION / entry_count) AS raw_skill_rating,
                        SUM((effective_rank::DOUBLE PRECISION / entry_count) * log_weight) / NULLIF(SUM(log_weight), 0) AS raw_weighted_rating,
                        SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END)::DOUBLE PRECISION / @totalCharted AS fc_rate,
                        SUM(score)::BIGINT AS total_score,
                        COALESCE(AVG(accuracy::DOUBLE PRECISION), 0.0) AS avg_accuracy,
                        SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END) AS full_combo_count,
                        COALESCE(AVG(stars::DOUBLE PRECISION), 0.0) AS avg_stars,
                        MIN(effective_rank) AS best_rank,
                        AVG(effective_rank::DOUBLE PRECISION) AS avg_rank
                    FROM OverallValidEntries
                    GROUP BY team_key
                ),
                OverallWithBayesian AS (
                    SELECT *,
                        (songs_played * raw_skill_rating + @m * @c) / (songs_played + @m) AS adjusted_skill_rating,
                        (songs_played * COALESCE(raw_weighted_rating, 1.0) + @m * @c) / (songs_played + @m) AS adjusted_weighted_rating
                    FROM OverallAggregated
                ),
                OverallRanked AS (
                    SELECT *,
                        ROW_NUMBER() OVER (ORDER BY adjusted_skill_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, team_key ASC) AS adjusted_skill_rank,
                        ROW_NUMBER() OVER (ORDER BY adjusted_weighted_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, team_key ASC) AS weighted_rank,
                        ROW_NUMBER() OVER (ORDER BY fc_rate DESC, total_score DESC, songs_played DESC, adjusted_skill_rating ASC, team_key ASC) AS fc_rate_rank,
                        ROW_NUMBER() OVER (ORDER BY total_score DESC, songs_played DESC, adjusted_skill_rating ASC, team_key ASC) AS total_score_rank
                    FROM OverallWithBayesian
                ),
                ComboValidEntries AS (
                    SELECT
                        ne.combo_id,
                        ne.song_id,
                        ne.team_key,
                        ne.score,
                        ne.accuracy,
                        ne.is_full_combo,
                        ne.stars,
                        COUNT(*) OVER (PARTITION BY ne.combo_id, ne.song_id) AS entry_count,
                        CASE
                            WHEN COUNT(*) OVER (PARTITION BY ne.combo_id, ne.song_id) > 0
                                THEN LN(COUNT(*) OVER (PARTITION BY ne.combo_id, ne.song_id)::DOUBLE PRECISION) / LN(2.0)
                            ELSE 0.0
                        END AS log_weight,
                        ROW_NUMBER() OVER (
                            PARTITION BY ne.combo_id, ne.song_id
                            ORDER BY ne.score DESC, ne.end_time ASC, ne.team_key ASC
                        ) AS effective_rank
                    FROM NormalizedEntries ne
                    WHERE ne.combo_id <> ''
                      AND array_length(string_to_array(ne.combo_id, '+'), 1) = @expectedMembers
                ),
                ComboAggregated AS (
                    SELECT
                        'combo'::TEXT AS ranking_scope,
                        combo_id,
                        team_key,
                        COUNT(*) AS songs_played,
                        @totalCharted AS total_charted_songs,
                        COUNT(*)::DOUBLE PRECISION / @totalCharted AS coverage,
                        AVG(effective_rank::DOUBLE PRECISION / entry_count) AS raw_skill_rating,
                        SUM((effective_rank::DOUBLE PRECISION / entry_count) * log_weight) / NULLIF(SUM(log_weight), 0) AS raw_weighted_rating,
                        SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END)::DOUBLE PRECISION / @totalCharted AS fc_rate,
                        SUM(score)::BIGINT AS total_score,
                        COALESCE(AVG(accuracy::DOUBLE PRECISION), 0.0) AS avg_accuracy,
                        SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END) AS full_combo_count,
                        COALESCE(AVG(stars::DOUBLE PRECISION), 0.0) AS avg_stars,
                        MIN(effective_rank) AS best_rank,
                        AVG(effective_rank::DOUBLE PRECISION) AS avg_rank
                    FROM ComboValidEntries
                    GROUP BY combo_id, team_key
                ),
                ComboWithBayesian AS (
                    SELECT *,
                        (songs_played * raw_skill_rating + @m * @c) / (songs_played + @m) AS adjusted_skill_rating,
                        (songs_played * COALESCE(raw_weighted_rating, 1.0) + @m * @c) / (songs_played + @m) AS adjusted_weighted_rating
                    FROM ComboAggregated
                ),
                ComboRanked AS (
                    SELECT *,
                        ROW_NUMBER() OVER (PARTITION BY combo_id ORDER BY adjusted_skill_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, team_key ASC) AS adjusted_skill_rank,
                        ROW_NUMBER() OVER (PARTITION BY combo_id ORDER BY adjusted_weighted_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, team_key ASC) AS weighted_rank,
                        ROW_NUMBER() OVER (PARTITION BY combo_id ORDER BY fc_rate DESC, total_score DESC, songs_played DESC, adjusted_skill_rating ASC, team_key ASC) AS fc_rate_rank,
                        ROW_NUMBER() OVER (PARTITION BY combo_id ORDER BY total_score DESC, songs_played DESC, adjusted_skill_rating ASC, team_key ASC) AS total_score_rank
                    FROM ComboWithBayesian
                )
                SELECT
                    @bandType AS band_type,
                    ranking_scope,
                    combo_id,
                    team_key,
                    string_to_array(team_key, ':') AS team_members,
                    songs_played,
                    total_charted_songs,
                    coverage,
                    raw_skill_rating,
                    adjusted_skill_rating,
                    adjusted_skill_rank,
                    adjusted_weighted_rating AS weighted_rating,
                    weighted_rank,
                    fc_rate,
                    fc_rate_rank,
                    total_score,
                    total_score_rank,
                    avg_accuracy,
                    full_combo_count,
                    avg_stars,
                    best_rank,
                    avg_rank,
                    raw_weighted_rating,
                    @now AS computed_at
                FROM OverallRanked
                UNION ALL
                SELECT
                    @bandType AS band_type,
                    ranking_scope,
                    combo_id,
                    team_key,
                    string_to_array(team_key, ':') AS team_members,
                    songs_played,
                    total_charted_songs,
                    coverage,
                    raw_skill_rating,
                    adjusted_skill_rating,
                    adjusted_skill_rank,
                    adjusted_weighted_rating AS weighted_rating,
                    weighted_rank,
                    fc_rate,
                    fc_rate_rank,
                    total_score,
                    total_score_rank,
                    avg_accuracy,
                    full_combo_count,
                    avg_stars,
                    best_rank,
                    avg_rank,
                    raw_weighted_rating,
                    @now AS computed_at
                FROM ComboRanked;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("totalCharted", totalChartedSongs);
        cmd.Parameters.AddWithValue("expectedMembers", expectedMembers);
        cmd.Parameters.AddWithValue("m", credibilityThreshold);
        cmd.Parameters.AddWithValue("c", populationMedian);
        cmd.Parameters.AddWithValue("now", computedAt);
        cmd.ExecuteNonQuery();
    }

    private void MaterializeBandTeamRankingResultsPhased(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        BandTeamRankingRebuildOptions options,
        string bandType,
        int totalChartedSongs,
        int expectedMembers,
        int credibilityThreshold,
        double populationMedian,
        DateTime computedAt,
        ref string currentStage)
    {
        currentStage = "materialize_source";
        var sourceSw = Stopwatch.StartNew();
        using (var cmd = conn.CreateCommand())
        {
            ConfigureBandRebuildCommand(cmd, tx, options);
            cmd.CommandText = @"
                CREATE TEMP TABLE _band_rank_source ON COMMIT DROP AS
                SELECT
                    be.song_id,
                    be.team_key,
                    be.score,
                    COALESCE(be.accuracy, 0) AS accuracy,
                    COALESCE(be.is_full_combo, FALSE) AS is_full_combo,
                    COALESCE(be.stars, 0) AS stars,
                    COALESCE(be.end_time, '') AS end_time,
                    COALESCE((
                        SELECT string_agg(mapped.instrument, '+' ORDER BY mapped.sort_order, mapped.instrument)
                        FROM (
                            SELECT
                                CASE part::INT
                                    WHEN 0 THEN 'Solo_Guitar'
                                    WHEN 1 THEN 'Solo_Bass'
                                    WHEN 3 THEN 'Solo_Drums'
                                    WHEN 2 THEN 'Solo_Vocals'
                                    WHEN 4 THEN 'Solo_PeripheralGuitar'
                                    WHEN 5 THEN 'Solo_PeripheralBass'
                                    WHEN 7 THEN 'Solo_PeripheralVocals'
                                    WHEN 8 THEN 'Solo_PeripheralCymbals'
                                    WHEN 6 THEN 'Solo_PeripheralDrums'
                                    ELSE NULL
                                END AS instrument,
                                CASE part::INT
                                    WHEN 0 THEN 0
                                    WHEN 1 THEN 1
                                    WHEN 3 THEN 2
                                    WHEN 2 THEN 3
                                    WHEN 4 THEN 4
                                    WHEN 5 THEN 5
                                    WHEN 7 THEN 6
                                    WHEN 8 THEN 7
                                    WHEN 6 THEN 8
                                    ELSE 999
                                END AS sort_order
                            FROM unnest(string_to_array(be.instrument_combo, ':')) AS parts(part)
                        ) mapped
                        WHERE mapped.instrument IS NOT NULL
                    ), '') AS combo_id
                FROM band_entries be
                WHERE be.band_type = @bandType
                  AND NOT be.is_over_threshold;";
            cmd.Parameters.AddWithValue("bandType", bandType);
            cmd.ExecuteNonQuery();
        }
        sourceSw.Stop();
        LogBandRebuildStage(bandType, options, "materialize_source", RoundElapsed(sourceSw));

        currentStage = "index_source";
        var indexSw = Stopwatch.StartNew();
        using (var cmd = conn.CreateCommand())
        {
            ConfigureBandRebuildCommand(cmd, tx, options);
            cmd.CommandText = @"
                CREATE INDEX _band_rank_source_overall_idx ON _band_rank_source (song_id, team_key, score DESC, end_time ASC, combo_id ASC);
                CREATE INDEX _band_rank_source_combo_idx ON _band_rank_source (combo_id, song_id, score DESC, end_time ASC, team_key ASC);
                ANALYZE _band_rank_source;";
            cmd.ExecuteNonQuery();
        }
        indexSw.Stop();
        LogBandRebuildStage(bandType, options, "index_source", RoundElapsed(indexSw));

        currentStage = "create_results_stage";
        var createResultsSw = Stopwatch.StartNew();
        CreateEmptyBandRankResultsTable(conn, tx, options);
        createResultsSw.Stop();
        LogBandRebuildStage(bandType, options, "create_results_stage", RoundElapsed(createResultsSw));

        currentStage = "materialize_overall_phase";
        var overallSw = Stopwatch.StartNew();
        var overallRows = InsertBandRankOverallPhase(conn, tx, options, bandType, totalChartedSongs, credibilityThreshold, populationMedian, computedAt);
        overallSw.Stop();
        LogBandRebuildStage(bandType, options, "materialize_overall_phase", RoundElapsed(overallSw), overallRows);

        currentStage = "load_combo_catalog";
        var comboCatalogSw = Stopwatch.StartNew();
        var comboIds = LoadBandRankSourceComboIds(conn, tx, options, expectedMembers);
        comboCatalogSw.Stop();
        LogBandRebuildStage(bandType, options, "load_combo_catalog", RoundElapsed(comboCatalogSw), comboIds.Count);

        currentStage = "materialize_combo_phases";
        var comboSw = Stopwatch.StartNew();
        var comboRows = 0;
        foreach (var comboId in comboIds)
        {
            currentStage = $"materialize_combo_phase:{comboId}";
            var comboPhaseSw = Stopwatch.StartNew();
            var comboPhaseRows = InsertBandRankComboPhase(conn, tx, options, bandType, comboId, totalChartedSongs, credibilityThreshold, populationMedian, computedAt);
            comboPhaseSw.Stop();
            comboRows += comboPhaseRows;
            LogBandRebuildStage(bandType, options, "materialize_combo_phase", RoundElapsed(comboPhaseSw), comboPhaseRows, comboId);
        }
        comboSw.Stop();
        currentStage = "materialize_combo_phases";
        LogBandRebuildStage(bandType, options, "materialize_combo_phases", RoundElapsed(comboSw), comboRows);
    }

    private static void CreateEmptyBandRankResultsTable(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = BandRankingStorageNames.GetCreateRankingTableSql(
            "_band_rank_results",
            includePrimaryKey: false,
            temporary: true,
            onCommitDrop: true);
        cmd.ExecuteNonQuery();
    }

    private static int InsertBandRankOverallPhase(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        BandTeamRankingRebuildOptions options,
        string bandType,
        int totalChartedSongs,
        int credibilityThreshold,
        double populationMedian,
        DateTime computedAt)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = @"
            INSERT INTO _band_rank_results (
                band_type, ranking_scope, combo_id, team_key, team_members,
                songs_played, total_charted_songs, coverage, raw_skill_rating,
                adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating, computed_at)
            WITH OverallChoice AS (
                SELECT *
                FROM (
                    SELECT
                        src.*,
                        ROW_NUMBER() OVER (
                            PARTITION BY src.song_id, src.team_key
                            ORDER BY src.score DESC, src.end_time ASC, src.combo_id ASC, src.team_key ASC
                        ) AS choice_rank
                    FROM _band_rank_source src
                ) ranked
                WHERE choice_rank = 1
            ),
            OverallValidEntries AS (
                SELECT
                    oc.song_id,
                    oc.team_key,
                    oc.score,
                    oc.accuracy,
                    oc.is_full_combo,
                    oc.stars,
                    COUNT(*) OVER (PARTITION BY oc.song_id) AS entry_count,
                    CASE
                        WHEN COUNT(*) OVER (PARTITION BY oc.song_id) > 0
                            THEN LN(COUNT(*) OVER (PARTITION BY oc.song_id)::DOUBLE PRECISION) / LN(2.0)
                        ELSE 0.0
                    END AS log_weight,
                    ROW_NUMBER() OVER (
                        PARTITION BY oc.song_id
                        ORDER BY oc.score DESC, oc.end_time ASC, oc.team_key ASC
                    ) AS effective_rank
                FROM OverallChoice oc
            ),
            OverallAggregated AS (
                SELECT
                    team_key,
                    COUNT(*) AS songs_played,
                    @totalCharted AS total_charted_songs,
                    COUNT(*)::DOUBLE PRECISION / @totalCharted AS coverage,
                    AVG(effective_rank::DOUBLE PRECISION / entry_count) AS raw_skill_rating,
                    SUM((effective_rank::DOUBLE PRECISION / entry_count) * log_weight) / NULLIF(SUM(log_weight), 0) AS raw_weighted_rating,
                    SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END)::DOUBLE PRECISION / @totalCharted AS fc_rate,
                    SUM(score)::BIGINT AS total_score,
                    COALESCE(AVG(accuracy::DOUBLE PRECISION), 0.0) AS avg_accuracy,
                    SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END) AS full_combo_count,
                    COALESCE(AVG(stars::DOUBLE PRECISION), 0.0) AS avg_stars,
                    MIN(effective_rank) AS best_rank,
                    AVG(effective_rank::DOUBLE PRECISION) AS avg_rank
                FROM OverallValidEntries
                GROUP BY team_key
            ),
            OverallWithBayesian AS (
                SELECT *,
                    (songs_played * raw_skill_rating + @m * @c) / (songs_played + @m) AS adjusted_skill_rating,
                    (songs_played * COALESCE(raw_weighted_rating, 1.0) + @m * @c) / (songs_played + @m) AS adjusted_weighted_rating
                FROM OverallAggregated
            ),
            OverallRanked AS (
                SELECT *,
                    ROW_NUMBER() OVER (ORDER BY adjusted_skill_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, team_key ASC) AS adjusted_skill_rank,
                    ROW_NUMBER() OVER (ORDER BY adjusted_weighted_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, team_key ASC) AS weighted_rank,
                    ROW_NUMBER() OVER (ORDER BY fc_rate DESC, total_score DESC, songs_played DESC, adjusted_skill_rating ASC, team_key ASC) AS fc_rate_rank,
                    ROW_NUMBER() OVER (ORDER BY total_score DESC, songs_played DESC, adjusted_skill_rating ASC, team_key ASC) AS total_score_rank
                FROM OverallWithBayesian
            )
            SELECT
                @bandType AS band_type,
                'overall'::TEXT AS ranking_scope,
                ''::TEXT AS combo_id,
                team_key,
                string_to_array(team_key, ':') AS team_members,
                songs_played,
                total_charted_songs,
                coverage,
                raw_skill_rating,
                adjusted_skill_rating,
                adjusted_skill_rank,
                adjusted_weighted_rating AS weighted_rating,
                weighted_rank,
                fc_rate,
                fc_rate_rank,
                total_score,
                total_score_rank,
                avg_accuracy,
                full_combo_count,
                avg_stars,
                best_rank,
                avg_rank,
                raw_weighted_rating,
                @now AS computed_at
            FROM OverallRanked;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("totalCharted", totalChartedSongs);
        cmd.Parameters.AddWithValue("m", credibilityThreshold);
        cmd.Parameters.AddWithValue("c", populationMedian);
        cmd.Parameters.AddWithValue("now", computedAt);
        return cmd.ExecuteNonQuery();
    }

    private static List<string> LoadBandRankSourceComboIds(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, int expectedMembers)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = @"
            SELECT combo_id
            FROM _band_rank_source
            WHERE combo_id <> ''
              AND array_length(string_to_array(combo_id, '+'), 1) = @expectedMembers
            GROUP BY combo_id
            ORDER BY combo_id;";
        cmd.Parameters.AddWithValue("expectedMembers", expectedMembers);

        var comboIds = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            comboIds.Add(reader.GetString(0));

        return comboIds;
    }

    private static int InsertBandRankComboPhase(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        BandTeamRankingRebuildOptions options,
        string bandType,
        string comboId,
        int totalChartedSongs,
        int credibilityThreshold,
        double populationMedian,
        DateTime computedAt)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = @"
            INSERT INTO _band_rank_results (
                band_type, ranking_scope, combo_id, team_key, team_members,
                songs_played, total_charted_songs, coverage, raw_skill_rating,
                adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating, computed_at)
            WITH ComboValidEntries AS (
                SELECT
                    src.song_id,
                    src.team_key,
                    src.score,
                    src.accuracy,
                    src.is_full_combo,
                    src.stars,
                    COUNT(*) OVER (PARTITION BY src.song_id) AS entry_count,
                    CASE
                        WHEN COUNT(*) OVER (PARTITION BY src.song_id) > 0
                            THEN LN(COUNT(*) OVER (PARTITION BY src.song_id)::DOUBLE PRECISION) / LN(2.0)
                        ELSE 0.0
                    END AS log_weight,
                    ROW_NUMBER() OVER (
                        PARTITION BY src.song_id
                        ORDER BY src.score DESC, src.end_time ASC, src.team_key ASC
                    ) AS effective_rank
                FROM _band_rank_source src
                WHERE src.combo_id = @comboId
            ),
            ComboAggregated AS (
                SELECT
                    team_key,
                    COUNT(*) AS songs_played,
                    @totalCharted AS total_charted_songs,
                    COUNT(*)::DOUBLE PRECISION / @totalCharted AS coverage,
                    AVG(effective_rank::DOUBLE PRECISION / entry_count) AS raw_skill_rating,
                    SUM((effective_rank::DOUBLE PRECISION / entry_count) * log_weight) / NULLIF(SUM(log_weight), 0) AS raw_weighted_rating,
                    SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END)::DOUBLE PRECISION / @totalCharted AS fc_rate,
                    SUM(score)::BIGINT AS total_score,
                    COALESCE(AVG(accuracy::DOUBLE PRECISION), 0.0) AS avg_accuracy,
                    SUM(CASE WHEN is_full_combo THEN 1 ELSE 0 END) AS full_combo_count,
                    COALESCE(AVG(stars::DOUBLE PRECISION), 0.0) AS avg_stars,
                    MIN(effective_rank) AS best_rank,
                    AVG(effective_rank::DOUBLE PRECISION) AS avg_rank
                FROM ComboValidEntries
                GROUP BY team_key
            ),
            ComboWithBayesian AS (
                SELECT *,
                    (songs_played * raw_skill_rating + @m * @c) / (songs_played + @m) AS adjusted_skill_rating,
                    (songs_played * COALESCE(raw_weighted_rating, 1.0) + @m * @c) / (songs_played + @m) AS adjusted_weighted_rating
                FROM ComboAggregated
            ),
            ComboRanked AS (
                SELECT *,
                    ROW_NUMBER() OVER (ORDER BY adjusted_skill_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, team_key ASC) AS adjusted_skill_rank,
                    ROW_NUMBER() OVER (ORDER BY adjusted_weighted_rating ASC, songs_played DESC, total_score DESC, full_combo_count DESC, team_key ASC) AS weighted_rank,
                    ROW_NUMBER() OVER (ORDER BY fc_rate DESC, total_score DESC, songs_played DESC, adjusted_skill_rating ASC, team_key ASC) AS fc_rate_rank,
                    ROW_NUMBER() OVER (ORDER BY total_score DESC, songs_played DESC, adjusted_skill_rating ASC, team_key ASC) AS total_score_rank
                FROM ComboWithBayesian
            )
            SELECT
                @bandType AS band_type,
                'combo'::TEXT AS ranking_scope,
                @comboId AS combo_id,
                team_key,
                string_to_array(team_key, ':') AS team_members,
                songs_played,
                total_charted_songs,
                coverage,
                raw_skill_rating,
                adjusted_skill_rating,
                adjusted_skill_rank,
                adjusted_weighted_rating AS weighted_rating,
                weighted_rank,
                fc_rate,
                fc_rate_rank,
                total_score,
                total_score_rank,
                avg_accuracy,
                full_combo_count,
                avg_stars,
                best_rank,
                avg_rank,
                raw_weighted_rating,
                @now AS computed_at
            FROM ComboRanked;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("comboId", comboId);
        cmd.Parameters.AddWithValue("totalCharted", totalChartedSongs);
        cmd.Parameters.AddWithValue("m", credibilityThreshold);
        cmd.Parameters.AddWithValue("c", populationMedian);
        cmd.Parameters.AddWithValue("now", computedAt);
        return cmd.ExecuteNonQuery();
    }

    private static string BuildBandTeamRankingInsertSql(string targetTable, string whereClause, string orderByClause) => $@"
                INSERT INTO {BandRankingStorageNames.QuoteIdentifier(targetTable)} (
                    band_type, ranking_scope, combo_id, team_key, team_members,
                    songs_played, total_charted_songs, coverage, raw_skill_rating,
                    adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                    fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                    full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating, computed_at,
                    ranking_generation, row_fingerprint)
                SELECT
                    source.band_type, source.ranking_scope, source.combo_id, source.team_key, source.team_members,
                    source.songs_played, source.total_charted_songs, source.coverage, source.raw_skill_rating,
                    source.adjusted_skill_rating, source.adjusted_skill_rank, source.weighted_rating, source.weighted_rank,
                    source.fc_rate, source.fc_rate_rank, source.total_score, source.total_score_rank, source.avg_accuracy,
                    source.full_combo_count, source.avg_stars, source.best_rank, source.avg_rank, source.raw_weighted_rating, source.computed_at,
                    @rankingGeneration, {BandRankHistoryFingerprintExpression("source")}
                FROM _band_rank_results source
                {whereClause}
                {orderByClause};";

    private static int InsertBandTeamRankingRowsMonolithic(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string targetTable, long rankingGeneration)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = BuildBandTeamRankingInsertSql(targetTable, string.Empty, "ORDER BY ranking_scope, combo_id, team_key");
        cmd.Parameters.AddWithValue("rankingGeneration", rankingGeneration);
        return cmd.ExecuteNonQuery();
    }

    private static int InsertBandTeamRankingRowsComboBatched(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string targetTable, long rankingGeneration)
    {
        var insertedRows = 0;

        using (var cmd = conn.CreateCommand())
        {
            ConfigureBandRebuildCommand(cmd, tx, options);
            cmd.CommandText = BuildBandTeamRankingInsertSql(targetTable, "WHERE ranking_scope = 'overall'", "ORDER BY team_key");
            cmd.Parameters.AddWithValue("rankingGeneration", rankingGeneration);
            insertedRows += cmd.ExecuteNonQuery();
        }

        var comboIds = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            ConfigureBandRebuildCommand(cmd, tx, options);
            cmd.CommandText = @"
                SELECT combo_id
                FROM _band_rank_results
                WHERE ranking_scope = 'combo'
                GROUP BY combo_id
                ORDER BY combo_id;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                comboIds.Add(reader.GetString(0));
        }

        if (comboIds.Count == 0)
            return insertedRows;

        using var insertCmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(insertCmd, tx, options);
        insertCmd.CommandText = BuildBandTeamRankingInsertSql(targetTable, "WHERE ranking_scope = 'combo' AND combo_id = @comboId", "ORDER BY team_key");
        insertCmd.Parameters.AddWithValue("rankingGeneration", rankingGeneration);
        var comboIdParam = insertCmd.Parameters.Add("comboId", NpgsqlDbType.Text);

        foreach (var comboId in comboIds)
        {
            comboIdParam.Value = comboId;
            insertedRows += insertCmd.ExecuteNonQuery();
        }

        return insertedRows;
    }

    private static int InsertBandTeamRankingStatsRows(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string targetTable)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = $@"
                INSERT INTO {BandRankingStorageNames.QuoteIdentifier(targetTable)} (band_type, ranking_scope, combo_id, total_teams, computed_at)
                SELECT band_type, ranking_scope, combo_id, COUNT(*), MAX(computed_at)
                FROM _band_rank_results
                GROUP BY band_type, ranking_scope, combo_id;";
        return cmd.ExecuteNonQuery();
    }

    private static string CreateBandRankingBuildTable(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string bandType, string buildSuffix)
    {
        var tableName = $"band_team_rankings_build_{bandType.ToLowerInvariant()}_{buildSuffix}".Replace('-', '_');
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = BandRankingStorageNames.GetCreateRankingTableSql(tableName, includePrimaryKey: false);
        cmd.ExecuteNonQuery();
        return tableName;
    }

    private static string CreateBandRankingStatsBuildTable(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string bandType, string buildSuffix)
    {
        var tableName = $"band_team_ranking_stats_build_{bandType.ToLowerInvariant()}_{buildSuffix}".Replace('-', '_');
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = BandRankingStorageNames.GetCreateStatsTableSql(tableName, includePrimaryKey: false);
        cmd.ExecuteNonQuery();
        return tableName;
    }

    private static void CreateBandRankingIndexes(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        BandTeamRankingRebuildOptions options,
        string tableName,
        bool includeTeamLookup)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = BandRankingStorageNames.GetCreateRankingBuildIndexesSql(tableName, includeTeamLookup);
        cmd.ExecuteNonQuery();
    }

    private static void CreateBandRankingStatsIndexes(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string tableName)
    {
        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = $"CREATE UNIQUE INDEX {BandRankingStorageNames.QuoteIdentifier(tableName + "_pkey")} ON {BandRankingStorageNames.QuoteIdentifier(tableName)} (band_type, ranking_scope, combo_id);";
        cmd.ExecuteNonQuery();
    }

    private static void SwapBandCurrentTables(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string bandType, string buildRankingTable, string buildStatsTable, string buildSuffix)
    {
        var currentRankingTable = BandRankingStorageNames.GetCurrentRankingTable(bandType);
        var currentStatsTable = BandRankingStorageNames.GetCurrentStatsTable(bandType);
        var backupRankingTable = $"{currentRankingTable}_old_{buildSuffix}";
        var backupStatsTable = $"{currentStatsTable}_old_{buildSuffix}";
        var statements = new List<string>();

        if (TableExists(conn, tx, currentRankingTable))
            statements.Add($"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(currentRankingTable)} RENAME TO {BandRankingStorageNames.QuoteIdentifier(backupRankingTable)}");

        if (TableExists(conn, tx, currentStatsTable))
            statements.Add($"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(currentStatsTable)} RENAME TO {BandRankingStorageNames.QuoteIdentifier(backupStatsTable)}");

        statements.Add($"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(buildRankingTable)} RENAME TO {BandRankingStorageNames.QuoteIdentifier(currentRankingTable)}");
        statements.Add($"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(buildStatsTable)} RENAME TO {BandRankingStorageNames.QuoteIdentifier(currentStatsTable)}");

        // The backup tables were just created by the RENAMEs above in this same
        // batch, so TableExists() executed before the batch cannot see them.
        // Use IF EXISTS so Postgres evaluates existence at statement time and
        // drops the backup regardless of whether the first RENAME ran (no-op on
        // first-ever build when currentRankingTable did not exist).
        statements.Add($"DROP TABLE IF EXISTS {BandRankingStorageNames.QuoteIdentifier(backupRankingTable)}");
        statements.Add($"DROP TABLE IF EXISTS {BandRankingStorageNames.QuoteIdentifier(backupStatsTable)}");

        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = string.Join(";\n", statements) + ";";
        cmd.ExecuteNonQuery();
    }

    private static void SwapBandPublishedTables(NpgsqlConnection conn, NpgsqlTransaction tx, BandTeamRankingRebuildOptions options, string bandType, string buildRankingTable, string buildStatsTable, string buildSuffix)
    {
        var publishedRankingTable = BandRankingStorageNames.GetPublishedRankingTable(bandType);
        var publishedStatsTable = BandRankingStorageNames.GetPublishedStatsTable(bandType);
        var backupRankingTable = $"{publishedRankingTable}_old_{buildSuffix}";
        var backupStatsTable = $"{publishedStatsTable}_old_{buildSuffix}";
        var statements = new List<string>();

        if (TableExists(conn, tx, publishedRankingTable))
            statements.Add($"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(publishedRankingTable)} RENAME TO {BandRankingStorageNames.QuoteIdentifier(backupRankingTable)}");

        if (TableExists(conn, tx, publishedStatsTable))
            statements.Add($"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(publishedStatsTable)} RENAME TO {BandRankingStorageNames.QuoteIdentifier(backupStatsTable)}");

        statements.Add($"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(buildRankingTable)} RENAME TO {BandRankingStorageNames.QuoteIdentifier(publishedRankingTable)}");
        statements.Add($"ALTER TABLE {BandRankingStorageNames.QuoteIdentifier(buildStatsTable)} RENAME TO {BandRankingStorageNames.QuoteIdentifier(publishedStatsTable)}");
        statements.Add($"DROP TABLE IF EXISTS {BandRankingStorageNames.QuoteIdentifier(backupRankingTable)}");
        statements.Add($"DROP TABLE IF EXISTS {BandRankingStorageNames.QuoteIdentifier(backupStatsTable)}");

        using var cmd = conn.CreateCommand();
        ConfigureBandRebuildCommand(cmd, tx, options);
        cmd.CommandText = string.Join(";\n", statements) + ";";
        cmd.ExecuteNonQuery();
    }

    private static double RoundElapsed(Stopwatch sw) => Math.Round(sw.Elapsed.TotalMilliseconds, 3);

    public void SnapshotBandRankHistory(string bandType, int retentionDays = 365)
    {
        SnapshotBandRankHistoryChunked(bandType, new BandRankHistorySnapshotOptions
        {
            UseLatestState = true,
            UseNarrowHistory = true,
            UseWideHistoryCompatibilityWrite = true,
            RetentionDays = retentionDays,
        });
    }

    public BandRankHistorySnapshotResult SnapshotBandRankHistoryChunked(
        string bandType,
        BandRankHistorySnapshotOptions options,
        long? jobId = null,
        CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        using var conn = _ds.OpenConnection();
        EnsureBandRankHistoryPollingSchema(conn);

        var rankingsTable = ResolveBandRankingReadTable(
            conn,
            bandType,
            options.UsePublishedRankings);
        var statsTable = ResolveBandRankingStatsReadTable(
            conn,
            bandType,
            options.UsePublishedRankings);

        if (options.UseLatestState)
            SeedBandRankHistoryLatestState(conn, bandType, options.CommandTimeoutSeconds, ct);

        var chunks = jobId.HasValue
            ? EnsureAndGetBandRankHistoryJobChunks(conn, jobId.Value, bandType, rankingsTable, statsTable, options, options.CommandTimeoutSeconds)
            : GetBandRankHistoryChunks(conn, bandType, rankingsTable, statsTable, options, options.CommandTimeoutSeconds)
                .Select(chunk => new BandRankHistoryChunkInfo
                {
                    JobId = 0,
                    BandType = bandType,
                    RankingScope = chunk.RankingScope,
                    ComboId = chunk.ComboId,
                    ChunkOrdinal = chunk.ChunkOrdinal,
                    TeamKeyStart = chunk.TeamKeyStart,
                    TeamKeyEnd = chunk.TeamKeyEnd,
                    EstimatedRows = chunk.EstimatedRows,
                    SourceGeneration = chunk.SourceGeneration,
                    Status = "queued",
                })
                .ToList();

        long scanned = 0;
        long inserted = 0;
        int completed = 0;
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            if (jobId.HasValue)
                MarkBandRankHistoryChunkRunning(conn, jobId.Value, chunk, options.CommandTimeoutSeconds);

            var chunkResult = SnapshotBandRankHistoryChunk(
                conn,
                rankingsTable,
                statsTable,
                bandType,
                chunk.RankingScope,
                chunk.ComboId,
                chunk.TeamKeyStart,
                chunk.TeamKeyEnd,
                chunk.SourceGeneration,
                today,
                options,
                ct);

            scanned += chunkResult.RowsScanned;
            inserted += chunkResult.RowsInserted;
            completed++;

            if (jobId.HasValue)
                CompleteBandRankHistoryChunk(
                    conn,
                    jobId.Value,
                    chunk,
                    chunkResult.RowsScanned,
                    chunkResult.RowsInserted,
                    Math.Max(0, chunkResult.RowsScanned - chunkResult.RowsInserted),
                    options.CommandTimeoutSeconds);
        }

        if (options.CleanupRetention)
            CleanupBandRankHistoryRetention(conn, bandType, options.RetentionDays, options.CommandTimeoutSeconds, ct);

        if (jobId.HasValue)
            return ReadBandRankHistoryJobSnapshotResult(conn, jobId.Value, options.CommandTimeoutSeconds);

        return new BandRankHistorySnapshotResult
        {
            RowsScanned = scanned,
            RowsInserted = inserted,
            RowsSkipped = Math.Max(0, scanned - inserted),
            ChunksCompleted = completed,
            ChunksTotal = chunks.Count,
        };
    }

    public BandRankHistoryV2BackfillResult BackfillBandRankHistoryV2FromLegacy(
        string bandType,
        BandRankHistoryV2BackfillOptions options,
        CancellationToken ct = default)
    {
        using var conn = _ds.OpenConnection();
        using (var tx = conn.BeginTransaction())
        {
            EnsureBandRankHistoryTables(conn, tx);
            tx.Commit();
        }

        var slices = ReadBandRankHistoryV2BackfillSlices(conn, bandType, options, ct);
        var resultSlices = new List<BandRankHistoryV2BackfillSlice>(slices.Count);

        foreach (var slice in slices)
        {
            ct.ThrowIfCancellationRequested();
            if (!options.Execute || (slice.MissingV2Rows <= 0 && slice.CompleteSnapshots > 0))
            {
                resultSlices.Add(slice.ToDto());
                continue;
            }

            resultSlices.Add(BackfillBandRankHistoryV2Slice(conn, slice, options, ct));
        }

        return new BandRankHistoryV2BackfillResult
        {
            BandType = bandType,
            StartDate = options.StartDate?.ToString("yyyy-MM-dd"),
            EndDate = options.EndDate?.ToString("yyyy-MM-dd"),
            Execute = options.Execute,
            LegacyRows = resultSlices.Sum(static slice => slice.LegacyRows),
            ExistingV2Rows = resultSlices.Sum(static slice => slice.ExistingV2Rows),
            MissingV2Rows = resultSlices.Sum(static slice => slice.MissingV2Rows),
            SnapshotRowsUpserted = resultSlices.Sum(static slice => slice.SnapshotRowsUpserted),
            PointRowsInserted = resultSlices.Sum(static slice => slice.PointRowsInserted),
            LatestRowsUpserted = resultSlices.Sum(static slice => slice.LatestRowsUpserted),
            SlicesTotal = resultSlices.Count,
            SlicesBackfilled = resultSlices.Count(static slice => slice.SnapshotRowsUpserted > 0 || slice.PointRowsInserted > 0 || slice.LatestRowsUpserted > 0),
            Slices = resultSlices,
        };
    }

    public BandRankHistoryWideNarrowParitySummary GetBandRankHistoryWideNarrowParity(
        string bandType,
        DateOnly snapshotDate,
        string? rankingScope = null,
        string? comboId = null,
        int sampleLimit = 10,
        bool ensureSchema = true)
    {
        using var conn = _ds.OpenConnection();
        if (ensureSchema)
        {
            using var tx = conn.BeginTransaction();
            EnsureBandRankHistoryTables(conn, tx);
            tx.Commit();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
                        WITH wide_rows AS (
                                SELECT count(*)::bigint AS row_count
                                FROM band_team_rank_history w
                                WHERE w.band_type = @bandType
                                    AND w.snapshot_date = @snapshotDate
                                    AND (@scope IS NULL OR w.ranking_scope = @scope)
                                    AND (@comboId IS NULL OR w.combo_id = @comboId)
                        ), narrow_rows AS (
                                SELECT count(*)::bigint AS row_count
                                FROM band_team_rank_history_points n
                                WHERE n.band_type = @bandType
                                    AND n.snapshot_date = @snapshotDate
                                    AND (@scope IS NULL OR n.ranking_scope = @scope)
                                    AND (@comboId IS NULL OR n.combo_id = @comboId)
                        ), matched AS (
                                SELECT
                                        count(*)::bigint AS row_count,
                                        count(*) FILTER (WHERE
                                                w.computed_at IS DISTINCT FROM n.snapshot_taken_at OR
                                                w.adjusted_skill_rank IS DISTINCT FROM n.adjusted_skill_rank OR
                                                w.weighted_rank IS DISTINCT FROM n.weighted_rank OR
                                                w.fc_rate_rank IS DISTINCT FROM n.fc_rate_rank OR
                                                w.total_score_rank IS DISTINCT FROM n.total_score_rank OR
                                                w.adjusted_skill_rating IS DISTINCT FROM n.adjusted_skill_rating OR
                                                w.weighted_rating IS DISTINCT FROM n.weighted_rating OR
                                                w.fc_rate IS DISTINCT FROM n.fc_rate OR
                                                w.total_score IS DISTINCT FROM n.total_score OR
                                                w.songs_played IS DISTINCT FROM n.songs_played OR
                                                w.coverage IS DISTINCT FROM n.coverage OR
                                                w.full_combo_count IS DISTINCT FROM n.full_combo_count OR
                                                w.total_charted_songs IS DISTINCT FROM n.total_charted_songs OR
                                                w.raw_weighted_rating IS DISTINCT FROM n.raw_weighted_rating OR
                                                w.raw_skill_rating IS DISTINCT FROM n.raw_skill_rating OR
                                                n.total_ranked_teams IS DISTINCT FROM stats.total_teams)::bigint AS value_mismatches
                                FROM band_team_rank_history w
                                INNER JOIN band_team_rank_history_points n
                                        ON n.band_type = @bandType
                                     AND n.snapshot_date = @snapshotDate
                                     AND n.ranking_scope = w.ranking_scope
                                     AND n.combo_id = w.combo_id
                                     AND n.team_key = w.team_key
                                LEFT JOIN band_team_ranking_stats_history stats
                                        ON stats.band_type = @bandType
                                     AND stats.snapshot_date = @snapshotDate
                                     AND stats.ranking_scope = w.ranking_scope
                                     AND stats.combo_id = w.combo_id
                                WHERE w.band_type = @bandType
                                    AND w.snapshot_date = @snapshotDate
                                    AND (@scope IS NULL OR w.ranking_scope = @scope)
                                    AND (@comboId IS NULL OR w.combo_id = @comboId)
                        ), missing_from_narrow AS (
                                SELECT count(*)::bigint AS row_count
                                FROM band_team_rank_history w
                                WHERE w.band_type = @bandType
                                    AND w.snapshot_date = @snapshotDate
                                    AND (@scope IS NULL OR w.ranking_scope = @scope)
                                    AND (@comboId IS NULL OR w.combo_id = @comboId)
                                    AND NOT EXISTS (
                                        SELECT 1
                                        FROM band_team_rank_history_points n
                                        WHERE n.band_type = @bandType
                                            AND n.snapshot_date = @snapshotDate
                                            AND n.ranking_scope = w.ranking_scope
                                            AND n.combo_id = w.combo_id
                                            AND n.team_key = w.team_key)
                        ), missing_from_wide AS (
                                SELECT count(*)::bigint AS row_count
                                FROM band_team_rank_history_points n
                                WHERE n.band_type = @bandType
                                    AND n.snapshot_date = @snapshotDate
                                    AND (@scope IS NULL OR n.ranking_scope = @scope)
                                    AND (@comboId IS NULL OR n.combo_id = @comboId)
                                    AND NOT EXISTS (
                                        SELECT 1
                                        FROM band_team_rank_history w
                                        WHERE w.band_type = @bandType
                                            AND w.snapshot_date = @snapshotDate
                                            AND w.ranking_scope = n.ranking_scope
                                            AND w.combo_id = n.combo_id
                                            AND w.team_key = n.team_key)
                        )
                        SELECT wide_rows.row_count,
                                     narrow_rows.row_count,
                                     matched.row_count,
                                     missing_from_narrow.row_count,
                                     missing_from_wide.row_count,
                                     matched.value_mismatches
                        FROM wide_rows
                        CROSS JOIN narrow_rows
                        CROSS JOIN matched
                        CROSS JOIN missing_from_narrow
                        CROSS JOIN missing_from_wide;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.Parameters.Add("scope", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(rankingScope) ? DBNull.Value : rankingScope;
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = comboId is null ? DBNull.Value : comboId;

        long wideRows;
        long narrowRows;
        long matchingRows;
        long missingFromNarrow;
        long missingFromWide;
        long valueMismatches;
        using (var reader = cmd.ExecuteReader())
        {
            reader.Read();
            wideRows = reader.GetInt64(0);
            narrowRows = reader.GetInt64(1);
            matchingRows = reader.GetInt64(2);
            missingFromNarrow = reader.GetInt64(3);
            missingFromWide = reader.GetInt64(4);
            valueMismatches = reader.GetInt64(5);
        }

        var effectiveSampleLimit = Math.Max(0, sampleLimit);
        var samples = effectiveSampleLimit > 0 && (missingFromNarrow > 0 || missingFromWide > 0 || valueMismatches > 0)
            ? ReadBandRankHistoryWideNarrowParitySamples(conn, bandType, snapshotDate, rankingScope, comboId, effectiveSampleLimit)
            : [];
        return new BandRankHistoryWideNarrowParitySummary
        {
            BandType = bandType,
            RankingScope = rankingScope,
            ComboId = comboId,
            SnapshotDate = snapshotDate.ToString("yyyy-MM-dd"),
            WideRows = wideRows,
            NarrowRows = narrowRows,
            MatchingRows = matchingRows,
            MissingFromNarrow = missingFromNarrow,
            MissingFromWide = missingFromWide,
            ValueMismatches = valueMismatches,
            Samples = samples,
        };
    }

    public BandRankHistoryV2ParitySummary GetBandRankHistoryV2Parity(
        string bandType,
        DateOnly snapshotDate,
        string? rankingScope = null,
        string? comboId = null,
        int sampleLimit = 10,
        bool ensureSchema = true)
    {
        using var conn = _ds.OpenConnection();
        if (ensureSchema)
        {
            using var tx = conn.BeginTransaction();
            EnsureBandRankHistoryTables(conn, tx);
            tx.Commit();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH legacy AS (
                SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date,
                       snapshot_taken_at, adjusted_skill_rank, weighted_rank, fc_rate_rank,
                       total_score_rank, adjusted_skill_rating, weighted_rating, fc_rate,
                       total_score, songs_played, coverage, full_combo_count,
                       total_charted_songs, total_ranked_teams, raw_weighted_rating,
                       raw_skill_rating
                FROM band_team_rank_history_points
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            ), v2 AS (
                SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date,
                       snapshot_taken_at, adjusted_skill_rank, weighted_rank, fc_rate_rank,
                       total_score_rank, adjusted_skill_rating, weighted_rating, fc_rate,
                       total_score, songs_played, coverage, full_combo_count,
                       total_charted_songs, total_ranked_teams, raw_weighted_rating,
                       raw_skill_rating
                FROM band_team_rank_history_points_v2
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            ), matching AS (
                SELECT legacy.*,
                       v2.snapshot_taken_at AS v2_snapshot_taken_at,
                       v2.adjusted_skill_rank AS v2_adjusted_skill_rank,
                       v2.weighted_rank AS v2_weighted_rank,
                       v2.fc_rate_rank AS v2_fc_rate_rank,
                       v2.total_score_rank AS v2_total_score_rank,
                       v2.adjusted_skill_rating AS v2_adjusted_skill_rating,
                       v2.weighted_rating AS v2_weighted_rating,
                       v2.fc_rate AS v2_fc_rate,
                       v2.total_score AS v2_total_score,
                       v2.songs_played AS v2_songs_played,
                       v2.coverage AS v2_coverage,
                       v2.full_combo_count AS v2_full_combo_count,
                       v2.total_charted_songs AS v2_total_charted_songs,
                       v2.total_ranked_teams AS v2_total_ranked_teams,
                       v2.raw_weighted_rating AS v2_raw_weighted_rating,
                       v2.raw_skill_rating AS v2_raw_skill_rating
                FROM legacy
                INNER JOIN v2
                    ON v2.band_type = legacy.band_type
                   AND v2.ranking_scope = legacy.ranking_scope
                   AND v2.combo_id = legacy.combo_id
                   AND v2.team_key = legacy.team_key
                   AND v2.snapshot_date = legacy.snapshot_date
            ), value_mismatches AS (
                SELECT count(*)::bigint AS row_count
                FROM matching
                WHERE snapshot_taken_at IS DISTINCT FROM v2_snapshot_taken_at
                   OR adjusted_skill_rank IS DISTINCT FROM v2_adjusted_skill_rank
                   OR weighted_rank IS DISTINCT FROM v2_weighted_rank
                   OR fc_rate_rank IS DISTINCT FROM v2_fc_rate_rank
                   OR total_score_rank IS DISTINCT FROM v2_total_score_rank
                   OR adjusted_skill_rating IS DISTINCT FROM v2_adjusted_skill_rating
                   OR weighted_rating IS DISTINCT FROM v2_weighted_rating
                   OR fc_rate IS DISTINCT FROM v2_fc_rate
                   OR total_score IS DISTINCT FROM v2_total_score
                   OR songs_played IS DISTINCT FROM v2_songs_played
                   OR coverage IS DISTINCT FROM v2_coverage
                   OR full_combo_count IS DISTINCT FROM v2_full_combo_count
                   OR total_charted_songs IS DISTINCT FROM v2_total_charted_songs
                   OR total_ranked_teams IS DISTINCT FROM v2_total_ranked_teams
                   OR raw_weighted_rating IS DISTINCT FROM v2_raw_weighted_rating
                   OR raw_skill_rating IS DISTINCT FROM v2_raw_skill_rating
            ), snapshots AS (
                SELECT
                    count(*) FILTER (WHERE status = 'complete')::bigint AS complete_snapshots,
                    count(*) FILTER (WHERE status <> 'complete')::bigint AS incomplete_snapshots,
                    COALESCE(sum(source_row_count), 0)::bigint AS source_rows
                FROM band_team_rank_history_snapshot_v2
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            ), stats AS (
                SELECT COALESCE(sum(total_teams), 0)::bigint AS legacy_stats_rows
                FROM band_team_ranking_stats_history
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            )
            SELECT
                (SELECT count(*) FROM legacy),
                (SELECT count(*) FROM v2),
                (SELECT count(*) FROM matching),
                                (SELECT count(*) FROM legacy l WHERE NOT EXISTS (
                                        SELECT 1 FROM v2
                                        WHERE v2.band_type = l.band_type
                                            AND v2.ranking_scope = l.ranking_scope
                                            AND v2.combo_id = l.combo_id
                                            AND v2.team_key = l.team_key
                                            AND v2.snapshot_date = l.snapshot_date)),
                                (SELECT count(*) FROM v2 WHERE NOT EXISTS (
                                        SELECT 1 FROM legacy l
                                        WHERE l.band_type = v2.band_type
                                            AND l.ranking_scope = v2.ranking_scope
                                            AND l.combo_id = v2.combo_id
                                            AND l.team_key = v2.team_key
                                            AND l.snapshot_date = v2.snapshot_date)),
                (SELECT row_count FROM value_mismatches),
                snapshots.complete_snapshots,
                snapshots.incomplete_snapshots,
                snapshots.source_rows,
                stats.legacy_stats_rows
            FROM snapshots
            CROSS JOIN stats;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.Parameters.Add("scope", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(rankingScope) ? DBNull.Value : rankingScope;
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = comboId is null ? DBNull.Value : comboId;

        long legacyRows;
        long v2Rows;
        long matchingRows;
        long missingFromV2;
        long missingFromLegacy;
        long valueMismatches;
        long completeSnapshots;
        long incompleteSnapshots;
        long v2SnapshotSourceRows;
        long legacyStatsRows;
        using (var reader = cmd.ExecuteReader())
        {
            reader.Read();
            legacyRows = reader.GetInt64(0);
            v2Rows = reader.GetInt64(1);
            matchingRows = reader.GetInt64(2);
            missingFromV2 = reader.GetInt64(3);
            missingFromLegacy = reader.GetInt64(4);
            valueMismatches = reader.GetInt64(5);
            completeSnapshots = reader.GetInt64(6);
            incompleteSnapshots = reader.GetInt64(7);
            v2SnapshotSourceRows = reader.GetInt64(8);
            legacyStatsRows = reader.GetInt64(9);
        }

        var effectiveSampleLimit = Math.Max(0, sampleLimit);
        var samples = effectiveSampleLimit > 0 && (missingFromV2 > 0 || missingFromLegacy > 0 || valueMismatches > 0)
            ? ReadBandRankHistoryV2ParitySamples(conn, bandType, snapshotDate, rankingScope, comboId, effectiveSampleLimit)
            : [];

        return new BandRankHistoryV2ParitySummary
        {
            BandType = bandType,
            RankingScope = rankingScope,
            ComboId = comboId,
            SnapshotDate = snapshotDate.ToString("yyyy-MM-dd"),
            LegacyRows = legacyRows,
            V2Rows = v2Rows,
            MatchingRows = matchingRows,
            MissingFromV2 = missingFromV2,
            MissingFromLegacy = missingFromLegacy,
            ValueMismatches = valueMismatches,
            CompleteSnapshots = completeSnapshots,
            IncompleteSnapshots = incompleteSnapshots,
            V2SnapshotSourceRows = v2SnapshotSourceRows,
            LegacyStatsRows = legacyStatsRows,
            Samples = samples,
        };
    }

    public BandRankHistoryV2LatestParitySummary GetBandRankHistoryV2LatestParity(
        string bandType,
        DateOnly snapshotDate,
        string? rankingScope = null,
        string? comboId = null,
        int sampleLimit = 10,
        bool ensureSchema = true)
    {
        using var conn = _ds.OpenConnection();
        if (ensureSchema)
        {
            using var tx = conn.BeginTransaction();
            EnsureBandRankHistoryTables(conn, tx);
            tx.Commit();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH points AS (
                SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date,
                       snapshot_id, generation_id, row_fingerprint
                FROM band_team_rank_history_points_v2
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            ), latest AS (
                SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date,
                       snapshot_id, generation_id, row_fingerprint
                FROM band_team_rank_history_latest_v2
                WHERE band_type = @bandType
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            ), latest_for_snapshot AS (
                SELECT *
                FROM latest
                WHERE snapshot_date = @snapshotDate
            ), matching AS (
                SELECT points.*
                FROM points
                INNER JOIN latest
                    ON latest.band_type = points.band_type
                   AND latest.ranking_scope = points.ranking_scope
                   AND latest.combo_id = points.combo_id
                   AND latest.team_key = points.team_key
                   AND latest.snapshot_date = points.snapshot_date
                   AND latest.snapshot_id = points.snapshot_id
                   AND latest.generation_id = points.generation_id
                   AND latest.row_fingerprint = points.row_fingerprint
            ), mismatched AS (
                SELECT points.*
                FROM points
                INNER JOIN latest
                    ON latest.band_type = points.band_type
                   AND latest.ranking_scope = points.ranking_scope
                   AND latest.combo_id = points.combo_id
                   AND latest.team_key = points.team_key
                WHERE latest.snapshot_date < points.snapshot_date
                   OR (latest.snapshot_date = points.snapshot_date AND (
                        latest.snapshot_id IS DISTINCT FROM points.snapshot_id
                        OR latest.generation_id IS DISTINCT FROM points.generation_id
                        OR latest.row_fingerprint IS DISTINCT FROM points.row_fingerprint))
            )
            SELECT
                (SELECT count(*) FROM points),
                (SELECT count(*) FROM latest_for_snapshot),
                (SELECT count(*) FROM matching),
                (SELECT count(*) FROM points p WHERE NOT EXISTS (
                    SELECT 1 FROM latest l
                    WHERE l.band_type = p.band_type
                      AND l.ranking_scope = p.ranking_scope
                      AND l.combo_id = p.combo_id
                      AND l.team_key = p.team_key)),
                (SELECT count(*) FROM mismatched),
                (SELECT count(*) FROM latest_for_snapshot l WHERE NOT EXISTS (
                    SELECT 1 FROM points p
                    WHERE p.band_type = l.band_type
                      AND p.ranking_scope = l.ranking_scope
                      AND p.combo_id = l.combo_id
                      AND p.team_key = l.team_key
                      AND p.snapshot_date = l.snapshot_date));";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.Parameters.Add("scope", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(rankingScope) ? DBNull.Value : rankingScope;
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = comboId is null ? DBNull.Value : comboId;

        long pointRows;
        long latestRowsForSnapshot;
        long matchingLatestRows;
        long missingFromLatest;
        long latestMismatches;
        long extraLatestRows;
        using (var reader = cmd.ExecuteReader())
        {
            reader.Read();
            pointRows = reader.GetInt64(0);
            latestRowsForSnapshot = reader.GetInt64(1);
            matchingLatestRows = reader.GetInt64(2);
            missingFromLatest = reader.GetInt64(3);
            latestMismatches = reader.GetInt64(4);
            extraLatestRows = reader.GetInt64(5);
        }

        var effectiveSampleLimit = Math.Max(0, sampleLimit);
        var samples = effectiveSampleLimit > 0 && (missingFromLatest > 0 || latestMismatches > 0 || extraLatestRows > 0)
            ? ReadBandRankHistoryV2LatestParitySamples(conn, bandType, snapshotDate, rankingScope, comboId, effectiveSampleLimit)
            : [];

        return new BandRankHistoryV2LatestParitySummary
        {
            BandType = bandType,
            RankingScope = rankingScope,
            ComboId = comboId,
            SnapshotDate = snapshotDate.ToString("yyyy-MM-dd"),
            V2PointRows = pointRows,
            LatestRowsForSnapshot = latestRowsForSnapshot,
            MatchingLatestRows = matchingLatestRows,
            MissingFromLatest = missingFromLatest,
            LatestMismatches = latestMismatches,
            ExtraLatestRowsForSnapshot = extraLatestRows,
            Samples = samples,
        };
    }

    public BandRankHistoryV2ReadPreview GetBandRankHistoryV2ReadPreview(
        string bandType,
        string teamKey,
        string? comboId = null,
        int days = 30,
        bool ensureSchema = true)
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-Math.Max(days, 1)));

        using var conn = _ds.OpenConnection();
        if (ensureSchema)
        {
            using var tx = conn.BeginTransaction();
            EnsureBandRankHistoryTables(conn, tx);
            tx.Commit();
        }

        var narrow = TableExists(conn, null, "band_team_rank_history_points")
            ? GetBandRankHistoryFromPoints(conn, bandType, teamKey, rankingScope, normalizedComboId, cutoff)
            : [];
        var wide = TableExists(conn, null, "band_team_rank_history") && TableExists(conn, null, "band_team_ranking_stats_history")
            ? GetBandRankHistoryFromWide(conn, bandType, teamKey, rankingScope, normalizedComboId, cutoff)
            : [];
        var legacy = narrow.Count > 0 ? narrow : wide;
        var v2 = TableExists(conn, null, "band_team_rank_history_points_v2")
            ? GetBandRankHistoryFromV2Points(conn, bandType, teamKey, rankingScope, normalizedComboId, cutoff)
            : [];
        var currentFallback = v2.Count > 0 ? v2 : legacy;
        var merged = MergeBandRankHistoryByDate(v2, legacy);

        var legacyDates = ReadSnapshotDates(legacy);
        var v2Dates = ReadSnapshotDates(v2);
        var currentFallbackDates = ReadSnapshotDates(currentFallback);
        var mergedDates = ReadSnapshotDates(merged);
        var hiddenByCurrentFallback = v2.Count > 0 ? MissingDates(legacyDates, currentFallbackDates) : [];

        return new BandRankHistoryV2ReadPreview
        {
            BandType = bandType,
            RankingScope = rankingScope,
            ComboId = normalizedComboId,
            TeamKey = teamKey,
            Days = days,
            LegacyRows = legacy.Count,
            V2OnlyRows = v2.Count,
            CurrentV2FallbackRows = currentFallback.Count,
            MergedRows = merged.Count,
            CurrentV2FallbackWouldHideLegacyDates = hiddenByCurrentFallback.Count > 0,
            LegacyDates = legacyDates,
            V2Dates = v2Dates,
            CurrentV2FallbackDates = currentFallbackDates,
            MergedDates = mergedDates,
            LegacyDatesHiddenByCurrentV2Fallback = hiddenByCurrentFallback,
            LegacyDatesMissingFromV2 = MissingDates(legacyDates, v2Dates),
        };
    }

    private static List<BandRankHistoryParityMismatchSample> ReadBandRankHistoryV2ParitySamples(
        NpgsqlConnection conn,
        string bandType,
        DateOnly snapshotDate,
        string? rankingScope,
        string? comboId,
        int sampleLimit)
    {
        if (sampleLimit <= 0)
            return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH value_mismatches AS (
                SELECT legacy.band_type,
                       legacy.ranking_scope,
                       legacy.combo_id,
                       legacy.team_key,
                       legacy.snapshot_date,
                       diff.mismatched_columns
                FROM band_team_rank_history_points legacy
                INNER JOIN band_team_rank_history_points_v2 v2
                    ON v2.band_type = legacy.band_type
                   AND v2.ranking_scope = legacy.ranking_scope
                   AND v2.combo_id = legacy.combo_id
                   AND v2.team_key = legacy.team_key
                   AND v2.snapshot_date = legacy.snapshot_date
                CROSS JOIN LATERAL (
                    SELECT array_remove(ARRAY[
                        CASE WHEN legacy.snapshot_taken_at IS DISTINCT FROM v2.snapshot_taken_at THEN 'snapshot_taken_at' END,
                        CASE WHEN legacy.adjusted_skill_rank IS DISTINCT FROM v2.adjusted_skill_rank THEN 'adjusted_skill_rank' END,
                        CASE WHEN legacy.weighted_rank IS DISTINCT FROM v2.weighted_rank THEN 'weighted_rank' END,
                        CASE WHEN legacy.fc_rate_rank IS DISTINCT FROM v2.fc_rate_rank THEN 'fc_rate_rank' END,
                        CASE WHEN legacy.total_score_rank IS DISTINCT FROM v2.total_score_rank THEN 'total_score_rank' END,
                        CASE WHEN legacy.adjusted_skill_rating IS DISTINCT FROM v2.adjusted_skill_rating THEN 'adjusted_skill_rating' END,
                        CASE WHEN legacy.weighted_rating IS DISTINCT FROM v2.weighted_rating THEN 'weighted_rating' END,
                        CASE WHEN legacy.fc_rate IS DISTINCT FROM v2.fc_rate THEN 'fc_rate' END,
                        CASE WHEN legacy.total_score IS DISTINCT FROM v2.total_score THEN 'total_score' END,
                        CASE WHEN legacy.songs_played IS DISTINCT FROM v2.songs_played THEN 'songs_played' END,
                        CASE WHEN legacy.coverage IS DISTINCT FROM v2.coverage THEN 'coverage' END,
                        CASE WHEN legacy.full_combo_count IS DISTINCT FROM v2.full_combo_count THEN 'full_combo_count' END,
                        CASE WHEN legacy.total_charted_songs IS DISTINCT FROM v2.total_charted_songs THEN 'total_charted_songs' END,
                        CASE WHEN legacy.total_ranked_teams IS DISTINCT FROM v2.total_ranked_teams THEN 'total_ranked_teams' END,
                        CASE WHEN legacy.raw_weighted_rating IS DISTINCT FROM v2.raw_weighted_rating THEN 'raw_weighted_rating' END,
                        CASE WHEN legacy.raw_skill_rating IS DISTINCT FROM v2.raw_skill_rating THEN 'raw_skill_rating' END
                    ], NULL)::text[] AS mismatched_columns
                ) diff
                WHERE legacy.band_type = @bandType
                  AND legacy.snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR legacy.ranking_scope = @scope)
                  AND (@comboId IS NULL OR legacy.combo_id = @comboId)
                  AND cardinality(diff.mismatched_columns) > 0
            ), samples AS (
                SELECT legacy.band_type, legacy.ranking_scope, legacy.combo_id, legacy.team_key, legacy.snapshot_date,
                       'missing_from_v2' AS mismatch_kind, ARRAY[]::text[] AS mismatched_columns
                FROM band_team_rank_history_points legacy
                WHERE legacy.band_type = @bandType
                  AND legacy.snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR legacy.ranking_scope = @scope)
                  AND (@comboId IS NULL OR legacy.combo_id = @comboId)
                  AND NOT EXISTS (
                    SELECT 1
                    FROM band_team_rank_history_points_v2 v2
                    WHERE v2.band_type = legacy.band_type
                      AND v2.snapshot_date = legacy.snapshot_date
                      AND v2.ranking_scope = legacy.ranking_scope
                      AND v2.combo_id = legacy.combo_id
                      AND v2.team_key = legacy.team_key)
                UNION ALL
                SELECT v2.band_type, v2.ranking_scope, v2.combo_id, v2.team_key, v2.snapshot_date,
                       'missing_from_legacy' AS mismatch_kind, ARRAY[]::text[] AS mismatched_columns
                FROM band_team_rank_history_points_v2 v2
                WHERE v2.band_type = @bandType
                  AND v2.snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR v2.ranking_scope = @scope)
                  AND (@comboId IS NULL OR v2.combo_id = @comboId)
                  AND NOT EXISTS (
                    SELECT 1
                    FROM band_team_rank_history_points legacy
                    WHERE legacy.band_type = v2.band_type
                      AND legacy.snapshot_date = v2.snapshot_date
                      AND legacy.ranking_scope = v2.ranking_scope
                      AND legacy.combo_id = v2.combo_id
                      AND legacy.team_key = v2.team_key)
                UNION ALL
                SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date,
                       'value_mismatch' AS mismatch_kind, mismatched_columns
                FROM value_mismatches
            )
            SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date, mismatch_kind, mismatched_columns
            FROM samples
            ORDER BY mismatch_kind, ranking_scope, combo_id, team_key
            LIMIT @sampleLimit;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.Parameters.Add("scope", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(rankingScope) ? DBNull.Value : rankingScope;
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = comboId is null ? DBNull.Value : comboId;
        cmd.Parameters.AddWithValue("sampleLimit", sampleLimit);

        return ReadBandRankHistoryParitySamples(cmd);
    }

    private static List<BandRankHistoryParityMismatchSample> ReadBandRankHistoryV2LatestParitySamples(
        NpgsqlConnection conn,
        string bandType,
        DateOnly snapshotDate,
        string? rankingScope,
        string? comboId,
        int sampleLimit)
    {
        if (sampleLimit <= 0)
            return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH points AS (
                SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date,
                       snapshot_id, generation_id, row_fingerprint
                FROM band_team_rank_history_points_v2
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            ), latest AS (
                SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date,
                       snapshot_id, generation_id, row_fingerprint
                FROM band_team_rank_history_latest_v2
                WHERE band_type = @bandType
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            ), samples AS (
                SELECT points.band_type, points.ranking_scope, points.combo_id, points.team_key, points.snapshot_date,
                       'missing_from_latest' AS mismatch_kind, ARRAY[]::text[] AS mismatched_columns
                FROM points
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM latest
                    WHERE latest.band_type = points.band_type
                      AND latest.ranking_scope = points.ranking_scope
                      AND latest.combo_id = points.combo_id
                      AND latest.team_key = points.team_key)
                UNION ALL
                SELECT points.band_type, points.ranking_scope, points.combo_id, points.team_key, points.snapshot_date,
                       'latest_mismatch' AS mismatch_kind,
                       array_remove(ARRAY[
                           CASE WHEN latest.snapshot_date < points.snapshot_date THEN 'snapshot_date' END,
                           CASE WHEN latest.snapshot_date = points.snapshot_date AND latest.snapshot_id IS DISTINCT FROM points.snapshot_id THEN 'snapshot_id' END,
                           CASE WHEN latest.snapshot_date = points.snapshot_date AND latest.generation_id IS DISTINCT FROM points.generation_id THEN 'generation_id' END,
                           CASE WHEN latest.snapshot_date = points.snapshot_date AND latest.row_fingerprint IS DISTINCT FROM points.row_fingerprint THEN 'row_fingerprint' END
                       ], NULL)::text[] AS mismatched_columns
                FROM points
                INNER JOIN latest
                    ON latest.band_type = points.band_type
                   AND latest.ranking_scope = points.ranking_scope
                   AND latest.combo_id = points.combo_id
                   AND latest.team_key = points.team_key
                WHERE latest.snapshot_date < points.snapshot_date
                   OR (latest.snapshot_date = points.snapshot_date AND (
                        latest.snapshot_id IS DISTINCT FROM points.snapshot_id
                        OR latest.generation_id IS DISTINCT FROM points.generation_id
                        OR latest.row_fingerprint IS DISTINCT FROM points.row_fingerprint))
                UNION ALL
                SELECT latest.band_type, latest.ranking_scope, latest.combo_id, latest.team_key, latest.snapshot_date,
                       'extra_latest_for_snapshot' AS mismatch_kind, ARRAY[]::text[] AS mismatched_columns
                FROM latest
                WHERE latest.snapshot_date = @snapshotDate
                  AND NOT EXISTS (
                    SELECT 1
                    FROM points
                    WHERE points.band_type = latest.band_type
                      AND points.ranking_scope = latest.ranking_scope
                      AND points.combo_id = latest.combo_id
                      AND points.team_key = latest.team_key
                      AND points.snapshot_date = latest.snapshot_date)
            )
            SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date, mismatch_kind, mismatched_columns
            FROM samples
            ORDER BY mismatch_kind, ranking_scope, combo_id, team_key
            LIMIT @sampleLimit;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.Parameters.Add("scope", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(rankingScope) ? DBNull.Value : rankingScope;
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = comboId is null ? DBNull.Value : comboId;
        cmd.Parameters.AddWithValue("sampleLimit", sampleLimit);

        return ReadBandRankHistoryParitySamples(cmd);
    }

    private static List<BandRankHistoryParityMismatchSample> ReadBandRankHistoryParitySamples(NpgsqlCommand cmd)
    {
        var samples = new List<BandRankHistoryParityMismatchSample>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            samples.Add(new BandRankHistoryParityMismatchSample
            {
                BandType = reader.GetString(0),
                RankingScope = reader.GetString(1),
                ComboId = reader.GetString(2),
                TeamKey = reader.GetString(3),
                SnapshotDate = DateOnly.FromDateTime(reader.GetDateTime(4)).ToString("yyyy-MM-dd"),
                MismatchKind = reader.GetString(5),
                MismatchedColumns = reader.GetFieldValue<string[]>(6),
            });
        }

        return samples;
    }

    private static List<BandRankHistoryDto> MergeBandRankHistoryByDate(IReadOnlyList<BandRankHistoryDto> v2, IReadOnlyList<BandRankHistoryDto> legacy)
    {
        var byDate = legacy
            .Concat(v2)
            .GroupBy(static row => row.SnapshotDate, StringComparer.Ordinal)
            .Select(static group => group.Last())
            .OrderBy(static row => row.SnapshotDate, StringComparer.Ordinal)
            .ToList();
        return byDate;
    }

    private static IReadOnlyList<string> ReadSnapshotDates(IReadOnlyList<BandRankHistoryDto> rows) =>
        rows.Select(static row => row.SnapshotDate)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static date => date, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> MissingDates(IReadOnlyList<string> expectedDates, IReadOnlyList<string> actualDates)
    {
        var actual = actualDates.ToHashSet(StringComparer.Ordinal);
        return expectedDates.Where(date => !actual.Contains(date)).ToArray();
    }

    private static List<BandRankHistoryParityMismatchSample> ReadBandRankHistoryWideNarrowParitySamples(
        NpgsqlConnection conn,
        string bandType,
        DateOnly snapshotDate,
        string? rankingScope,
        string? comboId,
        int sampleLimit)
    {
        if (sampleLimit <= 0)
            return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH value_mismatches AS (
                SELECT w.band_type,
                       w.ranking_scope,
                       w.combo_id,
                       w.team_key,
                       w.snapshot_date,
                       diff.mismatched_columns
                FROM band_team_rank_history w
                INNER JOIN band_team_rank_history_points n
                    ON n.band_type = @bandType
                   AND n.snapshot_date = @snapshotDate
                   AND n.ranking_scope = w.ranking_scope
                   AND n.combo_id = w.combo_id
                   AND n.team_key = w.team_key
                LEFT JOIN band_team_ranking_stats_history stats
                    ON stats.band_type = @bandType
                   AND stats.snapshot_date = @snapshotDate
                   AND stats.ranking_scope = w.ranking_scope
                   AND stats.combo_id = w.combo_id
                CROSS JOIN LATERAL (
                    SELECT array_remove(ARRAY[
                        CASE WHEN w.computed_at IS DISTINCT FROM n.snapshot_taken_at THEN 'snapshot_taken_at' END,
                        CASE WHEN w.adjusted_skill_rank IS DISTINCT FROM n.adjusted_skill_rank THEN 'adjusted_skill_rank' END,
                        CASE WHEN w.weighted_rank IS DISTINCT FROM n.weighted_rank THEN 'weighted_rank' END,
                        CASE WHEN w.fc_rate_rank IS DISTINCT FROM n.fc_rate_rank THEN 'fc_rate_rank' END,
                        CASE WHEN w.total_score_rank IS DISTINCT FROM n.total_score_rank THEN 'total_score_rank' END,
                        CASE WHEN w.adjusted_skill_rating IS DISTINCT FROM n.adjusted_skill_rating THEN 'adjusted_skill_rating' END,
                        CASE WHEN w.weighted_rating IS DISTINCT FROM n.weighted_rating THEN 'weighted_rating' END,
                        CASE WHEN w.fc_rate IS DISTINCT FROM n.fc_rate THEN 'fc_rate' END,
                        CASE WHEN w.total_score IS DISTINCT FROM n.total_score THEN 'total_score' END,
                        CASE WHEN w.songs_played IS DISTINCT FROM n.songs_played THEN 'songs_played' END,
                        CASE WHEN w.coverage IS DISTINCT FROM n.coverage THEN 'coverage' END,
                        CASE WHEN w.full_combo_count IS DISTINCT FROM n.full_combo_count THEN 'full_combo_count' END,
                        CASE WHEN w.total_charted_songs IS DISTINCT FROM n.total_charted_songs THEN 'total_charted_songs' END,
                        CASE WHEN w.raw_weighted_rating IS DISTINCT FROM n.raw_weighted_rating THEN 'raw_weighted_rating' END,
                        CASE WHEN w.raw_skill_rating IS DISTINCT FROM n.raw_skill_rating THEN 'raw_skill_rating' END,
                        CASE WHEN n.total_ranked_teams IS DISTINCT FROM stats.total_teams THEN 'total_ranked_teams' END
                    ], NULL)::text[] AS mismatched_columns
                ) diff
                WHERE w.band_type = @bandType
                  AND w.snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR w.ranking_scope = @scope)
                  AND (@comboId IS NULL OR w.combo_id = @comboId)
                  AND cardinality(diff.mismatched_columns) > 0
            ), samples AS (
                SELECT w.band_type, w.ranking_scope, w.combo_id, w.team_key, w.snapshot_date,
                       'missing_from_narrow' AS mismatch_kind, ARRAY[]::text[] AS mismatched_columns
                FROM band_team_rank_history w
                WHERE w.band_type = @bandType
                  AND w.snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR w.ranking_scope = @scope)
                  AND (@comboId IS NULL OR w.combo_id = @comboId)
                  AND NOT EXISTS (
                    SELECT 1
                    FROM band_team_rank_history_points n
                    WHERE n.band_type = @bandType
                      AND n.snapshot_date = @snapshotDate
                      AND n.ranking_scope = w.ranking_scope
                      AND n.combo_id = w.combo_id
                      AND n.team_key = w.team_key)
                UNION ALL
                SELECT n.band_type, n.ranking_scope, n.combo_id, n.team_key, n.snapshot_date,
                       'missing_from_wide' AS mismatch_kind, ARRAY[]::text[] AS mismatched_columns
                FROM band_team_rank_history_points n
                WHERE n.band_type = @bandType
                  AND n.snapshot_date = @snapshotDate
                  AND (@scope IS NULL OR n.ranking_scope = @scope)
                  AND (@comboId IS NULL OR n.combo_id = @comboId)
                  AND NOT EXISTS (
                    SELECT 1
                    FROM band_team_rank_history w
                    WHERE w.band_type = @bandType
                      AND w.snapshot_date = @snapshotDate
                      AND w.ranking_scope = n.ranking_scope
                      AND w.combo_id = n.combo_id
                      AND w.team_key = n.team_key)
                UNION ALL
                SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date,
                       'value_mismatch' AS mismatch_kind, mismatched_columns
                FROM value_mismatches
            )
            SELECT band_type, ranking_scope, combo_id, team_key, snapshot_date, mismatch_kind, mismatched_columns
            FROM samples
            ORDER BY mismatch_kind, ranking_scope, combo_id, team_key
            LIMIT @sampleLimit;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.Parameters.Add("scope", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(rankingScope) ? DBNull.Value : rankingScope;
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = comboId is null ? DBNull.Value : comboId;
        cmd.Parameters.AddWithValue("sampleLimit", sampleLimit);

        var samples = new List<BandRankHistoryParityMismatchSample>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            samples.Add(new BandRankHistoryParityMismatchSample
            {
                BandType = reader.GetString(0),
                RankingScope = reader.GetString(1),
                ComboId = reader.GetString(2),
                TeamKey = reader.GetString(3),
                SnapshotDate = DateOnly.FromDateTime(reader.GetDateTime(4)).ToString("yyyy-MM-dd"),
                MismatchKind = reader.GetString(5),
                MismatchedColumns = reader.GetFieldValue<string[]>(6),
            });
        }

        return samples;
    }

    private static BandRankHistorySnapshotResult ReadBandRankHistoryJobSnapshotResult(
        NpgsqlConnection conn,
        long jobId,
        int commandTimeoutSeconds)
    {
        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, commandTimeoutSeconds);
        cmd.CommandText = @"
            SELECT count(*)::int AS chunks_total,
                   count(*) FILTER (WHERE status = 'complete')::int AS chunks_completed,
                   COALESCE(sum(rows_scanned), 0)::bigint AS rows_scanned,
                   COALESCE(sum(rows_inserted), 0)::bigint AS rows_inserted,
                   COALESCE(sum(rows_skipped), 0)::bigint AS rows_skipped
            FROM band_rank_history_job_chunks
            WHERE job_id = @jobId";
        cmd.Parameters.AddWithValue("jobId", jobId);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new BandRankHistorySnapshotResult
        {
            ChunksTotal = reader.GetInt32(0),
            ChunksCompleted = reader.GetInt32(1),
            RowsScanned = reader.GetInt64(2),
            RowsInserted = reader.GetInt64(3),
            RowsSkipped = reader.GetInt64(4),
        };
    }

    private sealed record BandRankHistoryChunkKey(
        string RankingScope,
        string ComboId,
        int ChunkOrdinal,
        string? TeamKeyStart,
        string? TeamKeyEnd,
        long EstimatedRows,
        long SourceGeneration);

    private sealed record BandRankHistoryChunkResult(long RowsScanned, long RowsInserted);

    private sealed record BandRankHistoryV2BackfillSliceInfo(
        string BandType,
        DateOnly SnapshotDate,
        string RankingScope,
        string ComboId,
        DateTime ComputedAt,
        long LegacyRows,
        long ExistingV2Rows,
        long MissingV2Rows,
        long CompleteSnapshots)
    {
        public BandRankHistoryV2BackfillSlice ToDto() => new()
        {
            BandType = BandType,
            SnapshotDate = SnapshotDate.ToString("yyyy-MM-dd"),
            RankingScope = RankingScope,
            ComboId = ComboId,
            LegacyRows = LegacyRows,
            ExistingV2Rows = ExistingV2Rows,
            MissingV2Rows = MissingV2Rows,
            CompleteSnapshots = CompleteSnapshots,
        };
    }

    private static string BandRankHistoryFingerprintExpression(string alias) => $@"md5(concat_ws('|',
                    {alias}.team_members::text,
                    {alias}.songs_played::text,
                    {alias}.total_charted_songs::text,
                    {alias}.coverage::text,
                    {alias}.raw_skill_rating::text,
                    {alias}.adjusted_skill_rating::text,
                    {alias}.adjusted_skill_rank::text,
                    {alias}.weighted_rating::text,
                    {alias}.weighted_rank::text,
                    {alias}.fc_rate::text,
                    {alias}.fc_rate_rank::text,
                    {alias}.total_score::text,
                    {alias}.total_score_rank::text,
                    {alias}.avg_accuracy::text,
                    {alias}.full_combo_count::text,
                    {alias}.avg_stars::text,
                    {alias}.best_rank::text,
                    {alias}.avg_rank::text,
                    COALESCE({alias}.raw_weighted_rating::text, '')))";

    private static string BandRankHistoryPointFingerprintExpression(string alias) => $@"md5(concat_ws('|',
                    {alias}.snapshot_taken_at::text,
                    {alias}.adjusted_skill_rank::text,
                    {alias}.weighted_rank::text,
                    {alias}.fc_rate_rank::text,
                    {alias}.total_score_rank::text,
                    COALESCE({alias}.adjusted_skill_rating::text, ''),
                    COALESCE({alias}.weighted_rating::text, ''),
                    COALESCE({alias}.fc_rate::text, ''),
                    COALESCE({alias}.total_score::text, ''),
                    COALESCE({alias}.songs_played::text, ''),
                    COALESCE({alias}.coverage::text, ''),
                    COALESCE({alias}.full_combo_count::text, ''),
                    COALESCE({alias}.total_charted_songs::text, ''),
                    COALESCE({alias}.total_ranked_teams::text, ''),
                    COALESCE({alias}.raw_weighted_rating::text, ''),
                    COALESCE({alias}.raw_skill_rating::text, '')))";

    private static List<BandRankHistoryV2BackfillSliceInfo> ReadBandRankHistoryV2BackfillSlices(
        NpgsqlConnection conn,
        string bandType,
        BandRankHistoryV2BackfillOptions options,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, options.CommandTimeoutSeconds);
        cmd.CommandText = @"
            WITH legacy AS (
                SELECT
                    band_type,
                    snapshot_date,
                    ranking_scope,
                    combo_id,
                    count(*)::bigint AS legacy_rows,
                    max(snapshot_taken_at) AS computed_at
                FROM band_team_rank_history_points
                WHERE band_type = @bandType
                  AND (@startDate IS NULL OR snapshot_date >= @startDate)
                  AND (@endDate IS NULL OR snapshot_date <= @endDate)
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
                GROUP BY band_type, snapshot_date, ranking_scope, combo_id
            ), v2 AS (
                SELECT
                    band_type,
                    snapshot_date,
                    ranking_scope,
                    combo_id,
                    count(*)::bigint AS v2_rows
                FROM band_team_rank_history_points_v2
                WHERE band_type = @bandType
                  AND (@startDate IS NULL OR snapshot_date >= @startDate)
                  AND (@endDate IS NULL OR snapshot_date <= @endDate)
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
                GROUP BY band_type, snapshot_date, ranking_scope, combo_id
            ), snapshots AS (
                SELECT
                    band_type,
                    snapshot_date,
                    ranking_scope,
                    combo_id,
                    count(*) FILTER (WHERE status = 'complete')::bigint AS complete_snapshots
                FROM band_team_rank_history_snapshot_v2
                WHERE band_type = @bandType
                  AND (@startDate IS NULL OR snapshot_date >= @startDate)
                  AND (@endDate IS NULL OR snapshot_date <= @endDate)
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
                GROUP BY band_type, snapshot_date, ranking_scope, combo_id
            ), stats AS (
                SELECT band_type, snapshot_date, ranking_scope, combo_id, computed_at
                FROM band_team_ranking_stats_history
                WHERE band_type = @bandType
                  AND (@startDate IS NULL OR snapshot_date >= @startDate)
                  AND (@endDate IS NULL OR snapshot_date <= @endDate)
                  AND (@scope IS NULL OR ranking_scope = @scope)
                  AND (@comboId IS NULL OR combo_id = @comboId)
            )
            SELECT
                legacy.band_type,
                legacy.snapshot_date,
                legacy.ranking_scope,
                legacy.combo_id,
                COALESCE(stats.computed_at, legacy.computed_at) AS computed_at,
                legacy.legacy_rows,
                COALESCE(v2.v2_rows, 0) AS existing_v2_rows,
                GREATEST(legacy.legacy_rows - COALESCE(v2.v2_rows, 0), 0) AS missing_v2_rows,
                COALESCE(snapshots.complete_snapshots, 0) AS complete_snapshots
            FROM legacy
            LEFT JOIN v2
              ON v2.band_type = legacy.band_type
             AND v2.snapshot_date = legacy.snapshot_date
             AND v2.ranking_scope = legacy.ranking_scope
             AND v2.combo_id = legacy.combo_id
            LEFT JOIN snapshots
              ON snapshots.band_type = legacy.band_type
             AND snapshots.snapshot_date = legacy.snapshot_date
             AND snapshots.ranking_scope = legacy.ranking_scope
             AND snapshots.combo_id = legacy.combo_id
            LEFT JOIN stats
              ON stats.band_type = legacy.band_type
             AND stats.snapshot_date = legacy.snapshot_date
             AND stats.ranking_scope = legacy.ranking_scope
             AND stats.combo_id = legacy.combo_id
            WHERE legacy.legacy_rows <> COALESCE(v2.v2_rows, 0)
               OR COALESCE(snapshots.complete_snapshots, 0) = 0
            ORDER BY legacy.snapshot_date, legacy.ranking_scope, legacy.combo_id;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.Add("startDate", NpgsqlDbType.Date).Value = options.StartDate.HasValue ? options.StartDate.Value : (object)DBNull.Value;
        cmd.Parameters.Add("endDate", NpgsqlDbType.Date).Value = options.EndDate.HasValue ? options.EndDate.Value : (object)DBNull.Value;
        cmd.Parameters.Add("scope", NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(options.RankingScope) ? DBNull.Value : options.RankingScope;
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = options.ComboId is null ? DBNull.Value : options.ComboId;

        ct.ThrowIfCancellationRequested();
        using var registration = ct.Register(static state => ((NpgsqlCommand)state!).Cancel(), cmd);
        var slices = new List<BandRankHistoryV2BackfillSliceInfo>();
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                slices.Add(new BandRankHistoryV2BackfillSliceInfo(
                    reader.GetString(0),
                    DateOnly.FromDateTime(reader.GetDateTime(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetDateTime(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8)));
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        return slices;
    }

    private static BandRankHistoryV2BackfillSlice BackfillBandRankHistoryV2Slice(
        NpgsqlConnection conn,
        BandRankHistoryV2BackfillSliceInfo slice,
        BandRankHistoryV2BackfillOptions options,
        CancellationToken ct)
    {
        using var tx = conn.BeginTransaction();
        if (options.SynchronousCommitOff)
        {
            using var syncCmd = conn.CreateCommand();
            syncCmd.Transaction = tx;
            syncCmd.CommandText = "SET LOCAL synchronous_commit = off";
            syncCmd.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, options.CommandTimeoutSeconds);
        cmd.Transaction = tx;
        cmd.CommandText = $@"
            WITH target_snapshot AS (
                INSERT INTO band_team_rank_history_snapshot_v2 (
                    generation_id, band_type, ranking_scope, combo_id, snapshot_date,
                    computed_at, source_row_count, changed_row_count, status, completed_at, updated_at)
                VALUES (0, @bandType, @scope, @comboId, @snapshotDate,
                    @computedAt, @legacyRows, @missingRows, 'complete', now(), now())
                ON CONFLICT (band_type, ranking_scope, combo_id, snapshot_date) DO UPDATE SET
                    computed_at = EXCLUDED.computed_at,
                    source_row_count = EXCLUDED.source_row_count,
                    changed_row_count = EXCLUDED.changed_row_count,
                    status = EXCLUDED.status,
                    completed_at = EXCLUDED.completed_at,
                    updated_at = now()
                RETURNING snapshot_id, generation_id
            ), legacy AS (
                SELECT
                    points.*,
                    {BandRankHistoryPointFingerprintExpression("points")} AS row_fingerprint
                FROM band_team_rank_history_points points
                WHERE points.band_type = @bandType
                  AND points.snapshot_date = @snapshotDate
                  AND points.ranking_scope = @scope
                  AND points.combo_id = @comboId
            ), missing AS (
                SELECT legacy.*
                FROM legacy
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM band_team_rank_history_points_v2 existing
                    WHERE existing.band_type = legacy.band_type
                      AND existing.snapshot_date = legacy.snapshot_date
                      AND existing.ranking_scope = legacy.ranking_scope
                      AND existing.combo_id = legacy.combo_id
                      AND existing.team_key = legacy.team_key)
            ), inserted_points AS (
                INSERT INTO band_team_rank_history_points_v2 (
                    band_type, ranking_scope, combo_id, team_key, snapshot_date, snapshot_id, generation_id,
                    snapshot_taken_at, row_fingerprint, adjusted_skill_rank, weighted_rank, fc_rate_rank,
                    total_score_rank, adjusted_skill_rating, weighted_rating, fc_rate, total_score,
                    songs_played, coverage, full_combo_count, total_charted_songs, total_ranked_teams,
                    raw_weighted_rating, raw_skill_rating)
                SELECT
                    band_type,
                    ranking_scope,
                    combo_id,
                    team_key,
                    snapshot_date,
                    (SELECT snapshot_id FROM target_snapshot),
                    (SELECT generation_id FROM target_snapshot),
                    snapshot_taken_at,
                    row_fingerprint,
                    adjusted_skill_rank,
                    weighted_rank,
                    fc_rate_rank,
                    total_score_rank,
                    adjusted_skill_rating,
                    weighted_rating,
                    fc_rate,
                    total_score,
                    songs_played,
                    coverage,
                    full_combo_count,
                    total_charted_songs,
                    total_ranked_teams,
                    raw_weighted_rating,
                    raw_skill_rating
                FROM missing
                ON CONFLICT (band_type, ranking_scope, combo_id, team_key, snapshot_date) DO NOTHING
                RETURNING band_type, ranking_scope, combo_id, team_key, snapshot_date, snapshot_id, generation_id, row_fingerprint
            ), latest_v2 AS (
                INSERT INTO band_team_rank_history_latest_v2 (
                    band_type, ranking_scope, combo_id, team_key, generation_id,
                    snapshot_id, snapshot_date, row_fingerprint, updated_at)
                SELECT
                    band_type,
                    ranking_scope,
                    combo_id,
                    team_key,
                    generation_id,
                    snapshot_id,
                    snapshot_date,
                    row_fingerprint,
                    now()
                FROM inserted_points
                ON CONFLICT (band_type, ranking_scope, combo_id, team_key) DO UPDATE SET
                    generation_id = EXCLUDED.generation_id,
                    snapshot_id = EXCLUDED.snapshot_id,
                    snapshot_date = EXCLUDED.snapshot_date,
                    row_fingerprint = EXCLUDED.row_fingerprint,
                    updated_at = now()
                WHERE band_team_rank_history_latest_v2.snapshot_date <= EXCLUDED.snapshot_date
                RETURNING 1
            )
            SELECT
                (SELECT count(*) FROM target_snapshot),
                (SELECT count(*) FROM inserted_points),
                (SELECT count(*) FROM latest_v2);";
        cmd.Parameters.AddWithValue("bandType", slice.BandType);
        cmd.Parameters.AddWithValue("snapshotDate", slice.SnapshotDate);
        cmd.Parameters.AddWithValue("scope", slice.RankingScope);
        cmd.Parameters.AddWithValue("comboId", slice.ComboId);
        cmd.Parameters.AddWithValue("computedAt", slice.ComputedAt);
        cmd.Parameters.AddWithValue("legacyRows", slice.LegacyRows);
        cmd.Parameters.AddWithValue("missingRows", slice.MissingV2Rows);

        ct.ThrowIfCancellationRequested();
        using var registration = ct.Register(static state => ((NpgsqlCommand)state!).Cancel(), cmd);
        try
        {
            BandRankHistoryV2BackfillSlice result;
            using (var reader = cmd.ExecuteReader())
            {
                reader.Read();
                result = new BandRankHistoryV2BackfillSlice
                {
                    BandType = slice.BandType,
                    SnapshotDate = slice.SnapshotDate.ToString("yyyy-MM-dd"),
                    RankingScope = slice.RankingScope,
                    ComboId = slice.ComboId,
                    LegacyRows = slice.LegacyRows,
                    ExistingV2Rows = slice.ExistingV2Rows,
                    MissingV2Rows = slice.MissingV2Rows,
                    CompleteSnapshots = slice.CompleteSnapshots,
                    SnapshotRowsUpserted = reader.GetInt64(0),
                    PointRowsInserted = reader.GetInt64(1),
                    LatestRowsUpserted = reader.GetInt64(2),
                };
            }
            tx.Commit();
            return result;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    private static void ConfigureCommandTimeout(NpgsqlCommand cmd, int commandTimeoutSeconds)
    {
        if (commandTimeoutSeconds > 0)
            cmd.CommandTimeout = commandTimeoutSeconds;
    }

    private static List<BandRankHistoryChunkKey> GetBandRankHistoryChunks(
        NpgsqlConnection conn,
        string bandType,
        string rankingsTable,
        string statsTable,
        BandRankHistorySnapshotOptions options,
        int commandTimeoutSeconds)
    {
        var effectiveChunkSize = Math.Max(1, options.ChunkSize);
        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, commandTimeoutSeconds);
        if (!options.RangeChunkingEnabled)
        {
            cmd.CommandText = $@"
                SELECT ranking_scope, combo_id, 0 AS chunk_ordinal, NULL::text AS team_key_start,
                       NULL::text AS team_key_end, total_teams::bigint AS estimated_rows, 0::bigint AS source_generation
                FROM {BandRankingStorageNames.QuoteIdentifier(statsTable)}
                WHERE band_type = @bandType
                ORDER BY ranking_scope, combo_id";
        }
        else
        {
            cmd.CommandText = $@"
                WITH scoped_stats AS (
                    SELECT ranking_scope, combo_id, GREATEST(total_teams, 0)::bigint AS estimated_rows
                    FROM {BandRankingStorageNames.QuoteIdentifier(statsTable)}
                    WHERE band_type = @bandType
                ), numbered AS (
                    SELECT
                        src.ranking_scope,
                        src.combo_id,
                        ((row_number() OVER (PARTITION BY src.ranking_scope, src.combo_id ORDER BY src.team_key) - 1) / @chunkSize)::int AS chunk_ordinal,
                        src.team_key,
                        NULLIF(src.ranking_generation, 0) AS ranking_generation
                    FROM {BandRankingStorageNames.QuoteIdentifier(rankingsTable)} src
                    JOIN scoped_stats stats
                      ON stats.ranking_scope = src.ranking_scope
                     AND stats.combo_id = src.combo_id
                    WHERE src.band_type = @bandType
                ), range_chunks AS (
                    SELECT
                        ranking_scope,
                        combo_id,
                        chunk_ordinal,
                        min(team_key) AS team_key_start,
                        max(team_key) AS team_key_end,
                        count(*)::bigint AS estimated_rows,
                        COALESCE(max(ranking_generation), 0)::bigint AS source_generation
                    FROM numbered
                    GROUP BY ranking_scope, combo_id, chunk_ordinal
                ), empty_chunks AS (
                    SELECT
                        stats.ranking_scope,
                        stats.combo_id,
                        0 AS chunk_ordinal,
                        NULL::text AS team_key_start,
                        NULL::text AS team_key_end,
                        stats.estimated_rows,
                        0::bigint AS source_generation
                    FROM scoped_stats stats
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM {BandRankingStorageNames.QuoteIdentifier(rankingsTable)} src
                        WHERE src.band_type = @bandType
                          AND src.ranking_scope = stats.ranking_scope
                          AND src.combo_id = stats.combo_id)
                )
                SELECT ranking_scope, combo_id, chunk_ordinal, team_key_start, team_key_end, estimated_rows, source_generation
                FROM range_chunks
                UNION ALL
                SELECT ranking_scope, combo_id, chunk_ordinal, team_key_start, team_key_end, estimated_rows, source_generation
                FROM empty_chunks
                ORDER BY ranking_scope, combo_id, chunk_ordinal";
            cmd.Parameters.AddWithValue("chunkSize", effectiveChunkSize);
        }
        cmd.Parameters.AddWithValue("bandType", bandType);

        var chunks = new List<BandRankHistoryChunkKey>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            chunks.Add(new BandRankHistoryChunkKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt64(6)));
        }
        return chunks;
    }

    private static void SeedBandRankHistoryLatestState(
        NpgsqlConnection conn,
        string bandType,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, commandTimeoutSeconds);
        cmd.CommandText = $@"
            INSERT INTO band_team_rank_history_latest (
                band_type, ranking_scope, combo_id, team_key, team_members,
                songs_played, total_charted_songs, coverage, raw_skill_rating,
                adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating,
                computed_at, snapshot_date, fingerprint, updated_at)
            SELECT DISTINCT ON (band_type, ranking_scope, combo_id, team_key)
                h.band_type,
                h.ranking_scope,
                h.combo_id,
                h.team_key,
                h.team_members,
                h.songs_played,
                h.total_charted_songs,
                h.coverage,
                h.raw_skill_rating,
                h.adjusted_skill_rating,
                h.adjusted_skill_rank,
                h.weighted_rating,
                h.weighted_rank,
                h.fc_rate,
                h.fc_rate_rank,
                h.total_score,
                h.total_score_rank,
                h.avg_accuracy,
                h.full_combo_count,
                h.avg_stars,
                h.best_rank,
                h.avg_rank,
                h.raw_weighted_rating,
                h.computed_at,
                h.snapshot_date,
                {BandRankHistoryFingerprintExpression("h")},
                now()
            FROM band_team_rank_history h
            WHERE h.band_type = @bandType
              AND NOT EXISTS (
                SELECT 1 FROM band_team_rank_history_latest latest
                WHERE latest.band_type = h.band_type
                  AND latest.ranking_scope = h.ranking_scope
                  AND latest.combo_id = h.combo_id
                  AND latest.team_key = h.team_key)
            ORDER BY band_type, ranking_scope, combo_id, team_key, snapshot_date DESC
            ON CONFLICT (band_type, ranking_scope, combo_id, team_key) DO NOTHING";
        cmd.Parameters.AddWithValue("bandType", bandType);
        ct.ThrowIfCancellationRequested();
        using var registration = ct.Register(static state => ((NpgsqlCommand)state!).Cancel(), cmd);
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    private static BandRankHistoryChunkResult SnapshotBandRankHistoryChunk(
        NpgsqlConnection conn,
        string rankingsTable,
        string statsTable,
        string bandType,
        string rankingScope,
        string comboId,
        string? teamKeyStart,
        string? teamKeyEnd,
        long sourceGeneration,
        DateOnly today,
        BandRankHistorySnapshotOptions options,
        CancellationToken ct)
    {
        using var tx = conn.BeginTransaction();
        if (options.SynchronousCommitOff)
        {
            using var syncCmd = conn.CreateCommand();
            syncCmd.Transaction = tx;
            syncCmd.CommandText = "SET LOCAL synchronous_commit = off";
            syncCmd.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, options.CommandTimeoutSeconds);
        cmd.Transaction = tx;
        cmd.CommandText = $@"
            WITH src AS (
                SELECT
                    src.*,
                    COALESCE(NULLIF(src.row_fingerprint, ''), {BandRankHistoryFingerprintExpression("src")}) AS fingerprint
                FROM {BandRankingStorageNames.QuoteIdentifier(rankingsTable)} src
                WHERE src.band_type = @bandType
                  AND src.ranking_scope = @scope
                  AND src.combo_id = @comboId
                                    AND (@teamKeyStart IS NULL OR src.team_key >= @teamKeyStart)
                                    AND (@teamKeyEnd IS NULL OR src.team_key <= @teamKeyEnd)
            ), stats AS (
                SELECT total_teams, computed_at
                FROM {BandRankingStorageNames.QuoteIdentifier(statsTable)}
                WHERE band_type = @bandType
                  AND ranking_scope = @scope
                  AND combo_id = @comboId
            ), changed AS (
                SELECT src.*, stats.total_teams
                FROM src
                CROSS JOIN stats
                LEFT JOIN band_team_rank_history_latest latest
                    ON latest.band_type = src.band_type
                   AND latest.ranking_scope = src.ranking_scope
                   AND latest.combo_id = src.combo_id
                   AND latest.team_key = src.team_key
                LEFT JOIN band_team_rank_history_latest_v2 latest_v2
                    ON latest_v2.band_type = src.band_type
                   AND latest_v2.ranking_scope = src.ranking_scope
                   AND latest_v2.combo_id = src.combo_id
                   AND latest_v2.team_key = src.team_key
                WHERE NOT @useLatestState
                   OR (
                       @useV2LatestState
                       AND (
                           latest_v2.team_key IS NULL
                           OR latest_v2.row_fingerprint IS DISTINCT FROM src.fingerprint
                       )
                   )
                   OR (
                       NOT @useV2LatestState
                       AND (
                           latest.team_key IS NULL
                           OR latest.fingerprint IS DISTINCT FROM src.fingerprint
                       )
                   )
            ), wide AS (
                INSERT INTO band_team_rank_history (
                    band_type, ranking_scope, combo_id, team_key, team_members,
                    songs_played, total_charted_songs, coverage, raw_skill_rating,
                    adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                    fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                    full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating,
                    computed_at, snapshot_date)
                SELECT
                    band_type, ranking_scope, combo_id, team_key, team_members,
                    songs_played, total_charted_songs, coverage, raw_skill_rating,
                    adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                    fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                    full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating,
                    computed_at, @today
                FROM changed
                WHERE @writeWide
                ON CONFLICT (band_type, ranking_scope, combo_id, team_key, snapshot_date) DO UPDATE SET
                    team_members = EXCLUDED.team_members,
                    songs_played = EXCLUDED.songs_played,
                    total_charted_songs = EXCLUDED.total_charted_songs,
                    coverage = EXCLUDED.coverage,
                    raw_skill_rating = EXCLUDED.raw_skill_rating,
                    adjusted_skill_rating = EXCLUDED.adjusted_skill_rating,
                    adjusted_skill_rank = EXCLUDED.adjusted_skill_rank,
                    weighted_rating = EXCLUDED.weighted_rating,
                    weighted_rank = EXCLUDED.weighted_rank,
                    fc_rate = EXCLUDED.fc_rate,
                    fc_rate_rank = EXCLUDED.fc_rate_rank,
                    total_score = EXCLUDED.total_score,
                    total_score_rank = EXCLUDED.total_score_rank,
                    avg_accuracy = EXCLUDED.avg_accuracy,
                    full_combo_count = EXCLUDED.full_combo_count,
                    avg_stars = EXCLUDED.avg_stars,
                    best_rank = EXCLUDED.best_rank,
                    avg_rank = EXCLUDED.avg_rank,
                    raw_weighted_rating = EXCLUDED.raw_weighted_rating,
                    computed_at = EXCLUDED.computed_at
                RETURNING 1
            ), points AS (
                INSERT INTO band_team_rank_history_points (
                    band_type, ranking_scope, combo_id, team_key, snapshot_date, snapshot_taken_at,
                    adjusted_skill_rank, weighted_rank, fc_rate_rank, total_score_rank,
                    adjusted_skill_rating, weighted_rating, fc_rate, total_score,
                    songs_played, coverage, full_combo_count, total_charted_songs,
                    total_ranked_teams, raw_weighted_rating, raw_skill_rating)
                SELECT
                    band_type, ranking_scope, combo_id, team_key, @today, computed_at,
                    adjusted_skill_rank, weighted_rank, fc_rate_rank, total_score_rank,
                    adjusted_skill_rating, weighted_rating, fc_rate, total_score,
                    songs_played, coverage, full_combo_count, total_charted_songs,
                    total_teams, raw_weighted_rating, raw_skill_rating
                FROM changed
                WHERE @writeNarrow
                ON CONFLICT (band_type, ranking_scope, combo_id, team_key, snapshot_date) DO UPDATE SET
                    snapshot_taken_at = EXCLUDED.snapshot_taken_at,
                    adjusted_skill_rank = EXCLUDED.adjusted_skill_rank,
                    weighted_rank = EXCLUDED.weighted_rank,
                    fc_rate_rank = EXCLUDED.fc_rate_rank,
                    total_score_rank = EXCLUDED.total_score_rank,
                    adjusted_skill_rating = EXCLUDED.adjusted_skill_rating,
                    weighted_rating = EXCLUDED.weighted_rating,
                    fc_rate = EXCLUDED.fc_rate,
                    total_score = EXCLUDED.total_score,
                    songs_played = EXCLUDED.songs_played,
                    coverage = EXCLUDED.coverage,
                    full_combo_count = EXCLUDED.full_combo_count,
                    total_charted_songs = EXCLUDED.total_charted_songs,
                    total_ranked_teams = EXCLUDED.total_ranked_teams,
                    raw_weighted_rating = EXCLUDED.raw_weighted_rating,
                    raw_skill_rating = EXCLUDED.raw_skill_rating
                RETURNING 1
            ), latest AS (
                INSERT INTO band_team_rank_history_latest (
                    band_type, ranking_scope, combo_id, team_key, team_members,
                    songs_played, total_charted_songs, coverage, raw_skill_rating,
                    adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                    fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                    full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating,
                    computed_at, snapshot_date, fingerprint, updated_at)
                SELECT
                    band_type, ranking_scope, combo_id, team_key, team_members,
                    songs_played, total_charted_songs, coverage, raw_skill_rating,
                    adjusted_skill_rating, adjusted_skill_rank, weighted_rating, weighted_rank,
                    fc_rate, fc_rate_rank, total_score, total_score_rank, avg_accuracy,
                    full_combo_count, avg_stars, best_rank, avg_rank, raw_weighted_rating,
                    computed_at, @today, fingerprint, now()
                FROM changed
                WHERE @writeLegacyLatest
                ON CONFLICT (band_type, ranking_scope, combo_id, team_key) DO UPDATE SET
                    team_members = EXCLUDED.team_members,
                    songs_played = EXCLUDED.songs_played,
                    total_charted_songs = EXCLUDED.total_charted_songs,
                    coverage = EXCLUDED.coverage,
                    raw_skill_rating = EXCLUDED.raw_skill_rating,
                    adjusted_skill_rating = EXCLUDED.adjusted_skill_rating,
                    adjusted_skill_rank = EXCLUDED.adjusted_skill_rank,
                    weighted_rating = EXCLUDED.weighted_rating,
                    weighted_rank = EXCLUDED.weighted_rank,
                    fc_rate = EXCLUDED.fc_rate,
                    fc_rate_rank = EXCLUDED.fc_rate_rank,
                    total_score = EXCLUDED.total_score,
                    total_score_rank = EXCLUDED.total_score_rank,
                    avg_accuracy = EXCLUDED.avg_accuracy,
                    full_combo_count = EXCLUDED.full_combo_count,
                    avg_stars = EXCLUDED.avg_stars,
                    best_rank = EXCLUDED.best_rank,
                    avg_rank = EXCLUDED.avg_rank,
                    raw_weighted_rating = EXCLUDED.raw_weighted_rating,
                    computed_at = EXCLUDED.computed_at,
                    snapshot_date = EXCLUDED.snapshot_date,
                    fingerprint = EXCLUDED.fingerprint,
                    updated_at = now()
                RETURNING 1
            ), stats_history AS (
                INSERT INTO band_team_ranking_stats_history (
                    band_type, ranking_scope, combo_id, total_teams, computed_at, snapshot_date)
                SELECT @bandType, @scope, @comboId, total_teams, computed_at, @today
                FROM stats
                WHERE @writeWide OR @writeNarrow
                ON CONFLICT (band_type, ranking_scope, combo_id, snapshot_date) DO UPDATE SET
                    total_teams = EXCLUDED.total_teams,
                    computed_at = EXCLUDED.computed_at
                RETURNING 1
            ), snapshot_v2 AS (
                INSERT INTO band_team_rank_history_snapshot_v2 (
                    generation_id, band_type, ranking_scope, combo_id, snapshot_date,
                    computed_at, source_row_count, changed_row_count, status, completed_at, updated_at)
                SELECT
                    COALESCE(NULLIF(@sourceGeneration, 0), NULLIF((SELECT max(ranking_generation) FROM src), 0), 0),
                    @bandType,
                    @scope,
                    @comboId,
                    @today,
                    stats.computed_at,
                    (SELECT count(*) FROM src),
                    (SELECT count(*) FROM changed),
                    'complete',
                    now(),
                    now()
                FROM stats
                WHERE @writeV2
                ON CONFLICT (band_type, ranking_scope, combo_id, snapshot_date) DO UPDATE SET
                    generation_id = EXCLUDED.generation_id,
                    computed_at = EXCLUDED.computed_at,
                    source_row_count = EXCLUDED.source_row_count,
                    changed_row_count = EXCLUDED.changed_row_count,
                    status = EXCLUDED.status,
                    completed_at = EXCLUDED.completed_at,
                    updated_at = now()
                RETURNING snapshot_id, generation_id
            ), points_v2 AS (
                INSERT INTO band_team_rank_history_points_v2 (
                    band_type, ranking_scope, combo_id, team_key, snapshot_date, snapshot_id, generation_id,
                    snapshot_taken_at, row_fingerprint, adjusted_skill_rank, weighted_rank, fc_rate_rank,
                    total_score_rank, adjusted_skill_rating, weighted_rating, fc_rate, total_score,
                    songs_played, coverage, full_combo_count, total_charted_songs, total_ranked_teams,
                    raw_weighted_rating, raw_skill_rating)
                SELECT
                    band_type, ranking_scope, combo_id, team_key, @today,
                    (SELECT snapshot_id FROM snapshot_v2),
                    COALESCE(NULLIF(ranking_generation, 0), (SELECT generation_id FROM snapshot_v2)),
                    computed_at,
                    fingerprint,
                    adjusted_skill_rank,
                    weighted_rank,
                    fc_rate_rank,
                    total_score_rank,
                    adjusted_skill_rating,
                    weighted_rating,
                    fc_rate,
                    total_score,
                    songs_played,
                    coverage,
                    full_combo_count,
                    total_charted_songs,
                    total_teams,
                    raw_weighted_rating,
                    raw_skill_rating
                FROM changed
                WHERE @writeV2
                ON CONFLICT (band_type, ranking_scope, combo_id, team_key, snapshot_date) DO UPDATE SET
                    snapshot_id = EXCLUDED.snapshot_id,
                    generation_id = EXCLUDED.generation_id,
                    snapshot_taken_at = EXCLUDED.snapshot_taken_at,
                    row_fingerprint = EXCLUDED.row_fingerprint,
                    adjusted_skill_rank = EXCLUDED.adjusted_skill_rank,
                    weighted_rank = EXCLUDED.weighted_rank,
                    fc_rate_rank = EXCLUDED.fc_rate_rank,
                    total_score_rank = EXCLUDED.total_score_rank,
                    adjusted_skill_rating = EXCLUDED.adjusted_skill_rating,
                    weighted_rating = EXCLUDED.weighted_rating,
                    fc_rate = EXCLUDED.fc_rate,
                    total_score = EXCLUDED.total_score,
                    songs_played = EXCLUDED.songs_played,
                    coverage = EXCLUDED.coverage,
                    full_combo_count = EXCLUDED.full_combo_count,
                    total_charted_songs = EXCLUDED.total_charted_songs,
                    total_ranked_teams = EXCLUDED.total_ranked_teams,
                    raw_weighted_rating = EXCLUDED.raw_weighted_rating,
                    raw_skill_rating = EXCLUDED.raw_skill_rating
                RETURNING 1
            ), latest_v2 AS (
                INSERT INTO band_team_rank_history_latest_v2 (
                    band_type, ranking_scope, combo_id, team_key, generation_id,
                    snapshot_id, snapshot_date, row_fingerprint, updated_at)
                SELECT
                    band_type,
                    ranking_scope,
                    combo_id,
                    team_key,
                    COALESCE(NULLIF(ranking_generation, 0), (SELECT generation_id FROM snapshot_v2)),
                    (SELECT snapshot_id FROM snapshot_v2),
                    @today,
                    fingerprint,
                    now()
                FROM changed
                WHERE @writeV2
                ON CONFLICT (band_type, ranking_scope, combo_id, team_key) DO UPDATE SET
                    generation_id = EXCLUDED.generation_id,
                    snapshot_id = EXCLUDED.snapshot_id,
                    snapshot_date = EXCLUDED.snapshot_date,
                    row_fingerprint = EXCLUDED.row_fingerprint,
                    updated_at = now()
                WHERE band_team_rank_history_latest_v2.snapshot_date <= EXCLUDED.snapshot_date
                RETURNING 1
            )
            SELECT
                (SELECT count(*) FROM src) AS rows_scanned,
                (SELECT count(*) FROM changed) AS rows_inserted;";

        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", comboId);
        cmd.Parameters.Add("teamKeyStart", NpgsqlDbType.Text).Value = string.IsNullOrEmpty(teamKeyStart) ? DBNull.Value : teamKeyStart;
        cmd.Parameters.Add("teamKeyEnd", NpgsqlDbType.Text).Value = string.IsNullOrEmpty(teamKeyEnd) ? DBNull.Value : teamKeyEnd;
        cmd.Parameters.AddWithValue("sourceGeneration", sourceGeneration);
        cmd.Parameters.AddWithValue("today", today);
        cmd.Parameters.AddWithValue("useLatestState", options.UseLatestState);
        var writeMode = options.WriteMode;
        var (writeLegacy, writeV2, useV2LatestState) = writeMode switch
        {
            BandRankHistoryWriteMode.Legacy => (true, false, false),
            BandRankHistoryWriteMode.Dual => (true, true, false),
            BandRankHistoryWriteMode.V2Only => (false, true, true),
            _ => throw new ArgumentOutOfRangeException(nameof(options.WriteMode), options.WriteMode, "Unsupported band rank-history write mode."),
        };
        cmd.Parameters.AddWithValue("useV2LatestState", useV2LatestState);
        cmd.Parameters.AddWithValue("writeWide", writeLegacy && options.UseWideHistoryCompatibilityWrite);
        cmd.Parameters.AddWithValue("writeNarrow", writeLegacy && options.UseNarrowHistory);
        cmd.Parameters.AddWithValue("writeLegacyLatest", writeLegacy);
        cmd.Parameters.AddWithValue("writeV2", writeV2);

        ct.ThrowIfCancellationRequested();
        using var registration = ct.Register(static state => ((NpgsqlCommand)state!).Cancel(), cmd);
        BandRankHistoryChunkResult result;
        try
        {
            using var reader = cmd.ExecuteReader();
            reader.Read();
            result = new BandRankHistoryChunkResult(reader.GetInt64(0), reader.GetInt64(1));
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        tx.Commit();
        return result;
    }

    public int CleanupBandRankHistoryRetention(
        string bandType,
        int retentionDays = 365,
        int commandTimeoutSeconds = 0,
        CancellationToken ct = default,
        int batchSize = 5000,
        int maxBatches = 1)
    {
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (maxBatches <= 0) throw new ArgumentOutOfRangeException(nameof(maxBatches));

        using var conn = _ds.OpenConnection();
        return CleanupBandRankHistoryRetention(conn, bandType, retentionDays, commandTimeoutSeconds, ct, batchSize, maxBatches);
    }

    private static int CleanupBandRankHistoryRetention(
        NpgsqlConnection conn,
        string bandType,
        int retentionDays,
        int commandTimeoutSeconds,
        CancellationToken ct,
        int batchSize = FSTService.DatabaseMaintenanceOptions.DefaultCleanupBatchSize,
        int maxBatches = FSTService.DatabaseMaintenanceOptions.DefaultCleanupMaxBatches)
    {
        if (retentionDays <= 0)
            return 0;

        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-retentionDays);
        var totalDeleted = 0;
        totalDeleted += CleanupBandRankHistoryRetentionTable(
            conn,
            "band_team_rank_history_points",
            bandType,
            cutoff,
            true,
            batchSize,
            maxBatches,
            commandTimeoutSeconds,
            ct);
        totalDeleted += CleanupBandRankHistoryRetentionTable(
            conn,
            "band_team_rank_history",
            bandType,
            cutoff,
            true,
            batchSize,
            maxBatches,
            commandTimeoutSeconds,
            ct);
        totalDeleted += CleanupBandRankHistoryRetentionTable(
            conn,
            "band_team_ranking_stats_history",
            bandType,
            cutoff,
            false,
            batchSize,
            maxBatches,
            commandTimeoutSeconds,
            ct);
        return totalDeleted;
    }

    private static int CleanupBandRankHistoryRetentionTable(
        NpgsqlConnection conn,
        string tableName,
        string bandType,
        DateOnly cutoff,
        bool hasTeamKey,
        int batchSize,
        int maxBatches,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        var totalDeleted = 0;
        var teamKeyPredicate = hasTeamKey ? "AND newer.team_key = history.team_key" : string.Empty;
        var orderByTeamKey = hasTeamKey ? ", history.team_key ASC" : string.Empty;

        for (var batch = 0; batch < maxBatches; batch++)
        {
            ct.ThrowIfCancellationRequested();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            ConfigureCommandTimeout(cmd, commandTimeoutSeconds);
            cmd.CommandText = $@"
                WITH doomed AS (
                    SELECT history.ctid
                    FROM {tableName} history
                    WHERE history.band_type = @bandType
                      AND history.snapshot_date < @cutoff
                      AND EXISTS (
                        SELECT 1
                        FROM {tableName} newer
                        WHERE newer.band_type = history.band_type
                          AND newer.ranking_scope = history.ranking_scope
                          AND newer.combo_id = history.combo_id
                          {teamKeyPredicate}
                          AND newer.snapshot_date > history.snapshot_date
                          AND newer.snapshot_date <= @cutoff
                      )
                    ORDER BY history.snapshot_date ASC, history.ranking_scope ASC, history.combo_id ASC{orderByTeamKey}
                    LIMIT @batchSize
                )
                DELETE FROM {tableName} history
                USING doomed
                WHERE history.ctid = doomed.ctid";
            cmd.Parameters.AddWithValue("bandType", bandType);
            cmd.Parameters.AddWithValue("cutoff", cutoff);
            cmd.Parameters.AddWithValue("batchSize", batchSize);
            var deleted = ExecuteNonQueryWithCancellation(cmd, ct);
            tx.Commit();
            totalDeleted += deleted;

            if (deleted < batchSize)
                break;
        }

        return totalDeleted;
    }

    private static int ExecuteNonQueryWithCancellation(NpgsqlCommand cmd, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var registration = ct.Register(static state => ((NpgsqlCommand)state!).Cancel(), cmd);
        try
        {
            return cmd.ExecuteNonQuery();
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    public BandRankHistoryJobInfo EnqueueBandRankHistoryJob(long scrapeId, string bandType, DateOnly snapshotDate, string mode, bool coalesceSameDay = true)
    {
        using var conn = _ds.OpenConnection();
        EnsureBandRankHistoryPollingSchema(conn);
        var sourceGeneration = ReadCurrentBandRankingGeneration(conn, bandType);

        if (coalesceSameDay)
        {
            using var supersede = conn.CreateCommand();
            supersede.CommandText = @"
                UPDATE band_rank_history_jobs
                SET status = 'superseded', superseded_at = now(), updated_at = now(), last_error = 'Superseded by newer same-day history job.'
                WHERE band_type = @bandType
                  AND snapshot_date = @snapshotDate
                  AND scrape_id < @scrapeId
                  AND (
                      NOT EXISTS (
                          SELECT 1
                          FROM scrape_publication_state
                          WHERE id = TRUE
                            AND published_scrape_id IS NOT NULL
                      )
                      OR EXISTS (
                          SELECT 1
                          FROM scrape_publication_state
                          WHERE id = TRUE
                            AND published_scrape_id = @scrapeId
                      )
                  )
                  AND status IN ('queued', 'running', 'paused', 'failed')";
            supersede.Parameters.AddWithValue("bandType", bandType);
            supersede.Parameters.AddWithValue("snapshotDate", snapshotDate);
            supersede.Parameters.AddWithValue("scrapeId", scrapeId);
            supersede.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO band_rank_history_jobs (scrape_id, snapshot_date, band_type, mode, status, source_generation, updated_at)
            VALUES (@scrapeId, @snapshotDate, @bandType, @mode, 'queued', @sourceGeneration, now())
            ON CONFLICT (scrape_id, band_type, snapshot_date) DO UPDATE SET
                mode = EXCLUDED.mode,
                status = CASE
                    WHEN band_rank_history_jobs.status = 'complete' THEN band_rank_history_jobs.status
                    ELSE 'queued'
                END,
                source_generation = CASE
                    WHEN band_rank_history_jobs.status = 'complete' THEN band_rank_history_jobs.source_generation
                    ELSE EXCLUDED.source_generation
                END,
                updated_at = now(),
                last_error = NULL
            RETURNING job_id, scrape_id, snapshot_date, band_type, mode, status, started_at, completed_at,
                      failed_at, paused_at, superseded_at, last_error, attempts, chunks_total,
                      chunks_completed, rows_scanned, rows_inserted, rows_skipped,
                      current_ranking_scope, current_combo_id, updated_at";
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        cmd.Parameters.AddWithValue("snapshotDate", snapshotDate);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("mode", mode);
        cmd.Parameters.AddWithValue("sourceGeneration", sourceGeneration);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return ReadBandRankHistoryJob(reader);
    }

    public BandRankHistoryJobInfo? GetNextBandRankHistoryJob(
        int maxAttempts = int.MaxValue,
        TimeSpan? retryDelay = null,
        bool requirePublishedScrape = false)
    {
        var effectiveMaxAttempts = Math.Max(1, maxAttempts);
        var effectiveRetryDelay = retryDelay ?? TimeSpan.Zero;

        using var conn = _ds.OpenConnection();
        using (var tx = conn.BeginTransaction())
        {
            EnsureBandRankHistoryTables(conn, tx);
            tx.Commit();
        }

        using var cmd = conn.CreateCommand();
        if (requirePublishedScrape)
        {
            using var supersede = conn.CreateCommand();
            supersede.CommandText = """
                UPDATE band_rank_history_jobs job
                SET status = 'superseded',
                    superseded_at = now(),
                    updated_at = now(),
                    last_error = CASE
                        WHEN EXISTS (
                            SELECT 1
                            FROM scrape_log scrape
                            WHERE scrape.id = job.scrape_id
                              AND scrape.status = 'failed'
                        )
                            THEN 'Superseded because the candidate scrape failed.'
                        ELSE 'Superseded by a newer published scrape.'
                    END
                WHERE job.status IN ('queued', 'running', 'paused', 'failed')
                  AND (
                      EXISTS (
                          SELECT 1
                          FROM scrape_log scrape
                          WHERE scrape.id = job.scrape_id
                            AND scrape.status = 'failed'
                      )
                      OR EXISTS (
                          SELECT 1
                          FROM scrape_publication_state publication
                          WHERE publication.id = TRUE
                            AND publication.published_scrape_id IS NOT NULL
                            AND job.scrape_id < publication.published_scrape_id
                      )
                  )
                """;
            supersede.ExecuteNonQuery();
        }

        cmd.CommandText = @"
            SELECT job_id, scrape_id, snapshot_date, band_type, mode, status, started_at, completed_at,
                   failed_at, paused_at, superseded_at, last_error, attempts, chunks_total,
                   chunks_completed, rows_scanned, rows_inserted, rows_skipped,
                   current_ranking_scope, current_combo_id, updated_at
            FROM band_rank_history_jobs
            WHERE (
                    status IN ('queued', 'paused')
                    OR (
                        status = 'failed'
                        AND attempts < @maxAttempts
                        AND updated_at <= now() - @retryDelay
                    )
                  )
              AND (
                  @requirePublishedScrape = FALSE
                  OR scrape_id = (
                      SELECT published_scrape_id
                      FROM scrape_publication_state
                      WHERE id = TRUE
                  )
              )
            ORDER BY CASE WHEN status IN ('queued', 'paused') THEN 0 ELSE 1 END,
                     snapshot_date DESC, scrape_id DESC, job_id ASC
            LIMIT 1";
        cmd.Parameters.AddWithValue("maxAttempts", effectiveMaxAttempts);
        cmd.Parameters.AddWithValue("retryDelay", effectiveRetryDelay);
        cmd.Parameters.AddWithValue("requirePublishedScrape", requirePublishedScrape);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadBandRankHistoryJob(reader) : null;
    }

    public int RecoverStaleBandRankHistoryJobs(TimeSpan staleAfter, TimeSpan maxCatchupAge)
    {
        using var conn = _ds.OpenConnection();
        using (var tx = conn.BeginTransaction())
        {
            EnsureBandRankHistoryTables(conn, tx);
            tx.Commit();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH stale_running AS (
                UPDATE band_rank_history_jobs
                SET status = 'paused',
                    paused_at = now(),
                    updated_at = now(),
                    last_error = 'Recovered stale running job after worker inactivity.'
                WHERE status = 'running'
                  AND updated_at < now() - @staleAfter::interval
                RETURNING job_id
            ), stale_chunks AS (
                UPDATE band_rank_history_job_chunks chunks
                SET status = 'queued',
                    updated_at = now(),
                    last_error = 'Recovered stale running chunk after worker inactivity.'
                FROM stale_running jobs
                WHERE chunks.job_id = jobs.job_id
                  AND chunks.status = 'running'
                RETURNING chunks.job_id
            ), aged_jobs AS (
                UPDATE band_rank_history_jobs
                SET status = 'superseded',
                    superseded_at = now(),
                    updated_at = now(),
                    last_error = 'Superseded because catch-up age exceeded the configured window.'
                WHERE status IN ('queued', 'paused', 'failed')
                  AND snapshot_date < (CURRENT_DATE - @maxCatchupAge::interval)::date
                RETURNING job_id
            )
            SELECT (SELECT count(*) FROM stale_running) + (SELECT count(*) FROM aged_jobs)";
        cmd.Parameters.AddWithValue("staleAfter", staleAfter);
        cmd.Parameters.AddWithValue("maxCatchupAge", maxCatchupAge);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public bool TryStartBandRankHistoryJob(long jobId, int maxAttempts = int.MaxValue)
    {
        var effectiveMaxAttempts = Math.Max(1, maxAttempts);
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE band_rank_history_jobs
            SET status = 'running',
                started_at = COALESCE(started_at, now()),
                failed_at = NULL,
                paused_at = NULL,
                last_error = NULL,
                current_ranking_scope = NULL,
                current_combo_id = NULL,
                attempts = attempts + 1,
                updated_at = now()
            WHERE job_id = @jobId
              AND (
                  status IN ('queued', 'paused')
                  OR (status = 'failed' AND attempts < @maxAttempts)
              )";
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.Parameters.AddWithValue("maxAttempts", effectiveMaxAttempts);
        return cmd.ExecuteNonQuery() == 1;
    }

    public void CompleteBandRankHistoryJob(long jobId, BandRankHistorySnapshotResult result)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH counters AS (
                SELECT count(*)::int AS chunks_total,
                       count(*) FILTER (WHERE status = 'complete')::int AS chunks_completed,
                       COALESCE(sum(rows_scanned), 0)::bigint AS rows_scanned,
                       COALESCE(sum(rows_inserted), 0)::bigint AS rows_inserted,
                       COALESCE(sum(rows_skipped), 0)::bigint AS rows_skipped
                FROM band_rank_history_job_chunks
                WHERE job_id = @jobId
            )
            UPDATE band_rank_history_jobs
            SET status = 'complete', completed_at = now(), updated_at = now(), last_error = NULL,
                chunks_total = CASE WHEN counters.chunks_total > 0 THEN counters.chunks_total ELSE @chunksTotal END,
                chunks_completed = CASE WHEN counters.chunks_total > 0 THEN counters.chunks_completed ELSE @chunksCompleted END,
                rows_scanned = CASE WHEN counters.chunks_total > 0 THEN counters.rows_scanned ELSE @rowsScanned END,
                rows_inserted = CASE WHEN counters.chunks_total > 0 THEN counters.rows_inserted ELSE @rowsInserted END,
                rows_skipped = CASE WHEN counters.chunks_total > 0 THEN counters.rows_skipped ELSE @rowsSkipped END,
                current_ranking_scope = NULL, current_combo_id = NULL
            FROM counters
            WHERE job_id = @jobId";
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.Parameters.AddWithValue("chunksTotal", result.ChunksTotal);
        cmd.Parameters.AddWithValue("chunksCompleted", result.ChunksCompleted);
        cmd.Parameters.AddWithValue("rowsScanned", result.RowsScanned);
        cmd.Parameters.AddWithValue("rowsInserted", result.RowsInserted);
        cmd.Parameters.AddWithValue("rowsSkipped", result.RowsSkipped);
        cmd.ExecuteNonQuery();
    }

    public void PauseBandRankHistoryJob(long jobId, string? reason = null)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE band_rank_history_jobs
            SET status = 'paused', paused_at = now(), updated_at = now(), last_error = @reason
            WHERE job_id = @jobId AND status = 'running';

            UPDATE band_rank_history_job_chunks
            SET status = 'queued', updated_at = now(), last_error = @reason
            WHERE job_id = @jobId AND status = 'running';";
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void FailBandRankHistoryJob(long jobId, string error)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE band_rank_history_jobs
            SET status = 'failed', failed_at = now(), updated_at = now(), last_error = @error
            WHERE job_id = @jobId;

            UPDATE band_rank_history_job_chunks
            SET status = 'failed', updated_at = now(), last_error = @error
            WHERE job_id = @jobId AND status = 'running';";
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.Parameters.AddWithValue("error", error);
        cmd.ExecuteNonQuery();
    }

    private static BandRankHistoryJobInfo ReadBandRankHistoryJob(NpgsqlDataReader reader) => new()
    {
        JobId = reader.GetInt64(0),
        ScrapeId = reader.GetInt64(1),
        SnapshotDate = reader.GetDateTime(2).ToString("yyyy-MM-dd"),
        BandType = reader.GetString(3),
        Mode = reader.GetString(4),
        Status = reader.GetString(5),
        StartedAt = reader.IsDBNull(6) ? null : reader.GetDateTime(6).ToString("o"),
        CompletedAt = reader.IsDBNull(7) ? null : reader.GetDateTime(7).ToString("o"),
        FailedAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8).ToString("o"),
        PausedAt = reader.IsDBNull(9) ? null : reader.GetDateTime(9).ToString("o"),
        SupersededAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10).ToString("o"),
        LastError = reader.IsDBNull(11) ? null : reader.GetString(11),
        Attempts = reader.GetInt32(12),
        ChunksTotal = reader.GetInt32(13),
        ChunksCompleted = reader.GetInt32(14),
        RowsScanned = reader.GetInt64(15),
        RowsInserted = reader.GetInt64(16),
        RowsSkipped = reader.GetInt64(17),
        CurrentRankingScope = reader.IsDBNull(18) ? null : reader.GetString(18),
        CurrentComboId = reader.IsDBNull(19) ? null : reader.GetString(19),
        UpdatedAt = reader.GetDateTime(20).ToString("o"),
    };

    private static List<BandRankHistoryChunkInfo> EnsureAndGetBandRankHistoryJobChunks(
        NpgsqlConnection conn,
        long jobId,
        string bandType,
        string rankingsTable,
        string statsTable,
        BandRankHistorySnapshotOptions options,
        int commandTimeoutSeconds)
    {
        var jobSourceGeneration = ReadBandRankHistoryJobSourceGeneration(conn, jobId, commandTimeoutSeconds);
        if (!BandRankHistoryJobHasChunks(conn, jobId, commandTimeoutSeconds))
        {
            var chunks = GetBandRankHistoryChunks(conn, bandType, rankingsTable, statsTable, options, commandTimeoutSeconds);

            using var insert = conn.CreateCommand();
            ConfigureCommandTimeout(insert, commandTimeoutSeconds);
            insert.CommandText = @"
                INSERT INTO band_rank_history_job_chunks (
                    job_id, band_type, ranking_scope, combo_id, chunk_ordinal,
                    team_key_start, team_key_end, estimated_rows, source_generation, status, updated_at)
                VALUES (
                    @jobId, @bandType, @scope, @comboId, @chunkOrdinal,
                    @teamKeyStart, @teamKeyEnd, @estimatedRows, @sourceGeneration, 'queued', now())
                ON CONFLICT (job_id, ranking_scope, combo_id, chunk_ordinal) DO NOTHING";
            insert.Parameters.AddWithValue("jobId", jobId);
            insert.Parameters.AddWithValue("bandType", bandType);
            var scopeParam = insert.Parameters.Add("scope", NpgsqlDbType.Text);
            var comboParam = insert.Parameters.Add("comboId", NpgsqlDbType.Text);
            var ordinalParam = insert.Parameters.Add("chunkOrdinal", NpgsqlDbType.Integer);
            var startParam = insert.Parameters.Add("teamKeyStart", NpgsqlDbType.Text);
            var endParam = insert.Parameters.Add("teamKeyEnd", NpgsqlDbType.Text);
            var estimatedRowsParam = insert.Parameters.Add("estimatedRows", NpgsqlDbType.Bigint);
            var generationParam = insert.Parameters.Add("sourceGeneration", NpgsqlDbType.Bigint);
            foreach (var chunk in chunks)
            {
                scopeParam.Value = chunk.RankingScope;
                comboParam.Value = chunk.ComboId;
                ordinalParam.Value = chunk.ChunkOrdinal;
                startParam.Value = string.IsNullOrEmpty(chunk.TeamKeyStart) ? DBNull.Value : chunk.TeamKeyStart;
                endParam.Value = string.IsNullOrEmpty(chunk.TeamKeyEnd) ? DBNull.Value : chunk.TeamKeyEnd;
                estimatedRowsParam.Value = chunk.EstimatedRows;
                generationParam.Value = chunk.SourceGeneration > 0 ? chunk.SourceGeneration : jobSourceGeneration;
                insert.ExecuteNonQuery();
            }
        }
        else if (jobSourceGeneration > 0)
        {
            using var updateGeneration = conn.CreateCommand();
            ConfigureCommandTimeout(updateGeneration, commandTimeoutSeconds);
            updateGeneration.CommandText = @"
                UPDATE band_rank_history_job_chunks
                SET source_generation = @sourceGeneration
                WHERE job_id = @jobId
                  AND source_generation = 0";
            updateGeneration.Parameters.AddWithValue("jobId", jobId);
            updateGeneration.Parameters.AddWithValue("sourceGeneration", jobSourceGeneration);
            updateGeneration.ExecuteNonQuery();
        }

        using (var update = conn.CreateCommand())
        {
            ConfigureCommandTimeout(update, commandTimeoutSeconds);
            update.CommandText = @"
                UPDATE band_rank_history_jobs job
                SET chunks_total = counts.total_count,
                    chunks_completed = counts.completed_count,
                    updated_at = now()
                FROM (
                    SELECT job_id,
                           count(*)::int AS total_count,
                           count(*) FILTER (WHERE status = 'complete')::int AS completed_count
                    FROM band_rank_history_job_chunks
                    WHERE job_id = @jobId
                    GROUP BY job_id
                ) counts
                WHERE job.job_id = counts.job_id";
            update.Parameters.AddWithValue("jobId", jobId);
            update.ExecuteNonQuery();
        }

        using var select = conn.CreateCommand();
        ConfigureCommandTimeout(select, commandTimeoutSeconds);
        select.CommandText = @"
            SELECT job_id, band_type, ranking_scope, combo_id, chunk_ordinal,
                   team_key_start, team_key_end, estimated_rows, source_generation, status
            FROM band_rank_history_job_chunks
            WHERE job_id = @jobId AND status IN ('queued', 'failed')
            ORDER BY estimated_rows NULLS LAST, ranking_scope, combo_id, chunk_ordinal";
        select.Parameters.AddWithValue("jobId", jobId);
        var pending = new List<BandRankHistoryChunkInfo>();
        using var reader = select.ExecuteReader();
        while (reader.Read())
        {
            pending.Add(new BandRankHistoryChunkInfo
            {
                JobId = reader.GetInt64(0),
                BandType = reader.GetString(1),
                RankingScope = reader.GetString(2),
                ComboId = reader.GetString(3),
                ChunkOrdinal = reader.GetInt32(4),
                TeamKeyStart = reader.IsDBNull(5) ? null : reader.GetString(5),
                TeamKeyEnd = reader.IsDBNull(6) ? null : reader.GetString(6),
                EstimatedRows = reader.GetInt64(7),
                SourceGeneration = reader.GetInt64(8),
                Status = reader.GetString(9),
            });
        }

        return pending;
    }

    private static bool BandRankHistoryJobHasChunks(NpgsqlConnection conn, long jobId, int commandTimeoutSeconds)
    {
        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, commandTimeoutSeconds);
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM band_rank_history_job_chunks WHERE job_id = @jobId)";
        cmd.Parameters.AddWithValue("jobId", jobId);
        return Convert.ToBoolean(cmd.ExecuteScalar() ?? false);
    }

    private static long ReadBandRankHistoryJobSourceGeneration(NpgsqlConnection conn, long jobId, int commandTimeoutSeconds)
    {
        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, commandTimeoutSeconds);
        cmd.CommandText = "SELECT source_generation FROM band_rank_history_jobs WHERE job_id = @jobId";
        cmd.Parameters.AddWithValue("jobId", jobId);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private static void MarkBandRankHistoryChunkRunning(NpgsqlConnection conn, long jobId, BandRankHistoryChunkInfo chunk, int commandTimeoutSeconds)
    {
        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, commandTimeoutSeconds);
        cmd.CommandText = @"
            UPDATE band_rank_history_job_chunks
            SET status = 'running', started_at = COALESCE(started_at, now()), updated_at = now(), last_error = NULL
            WHERE job_id = @jobId AND ranking_scope = @scope AND combo_id = @comboId AND chunk_ordinal = @chunkOrdinal;

            UPDATE band_rank_history_jobs
            SET current_ranking_scope = @scope, current_combo_id = @comboId, updated_at = now()
            WHERE job_id = @jobId";
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.Parameters.AddWithValue("scope", chunk.RankingScope);
        cmd.Parameters.AddWithValue("comboId", chunk.ComboId);
        cmd.Parameters.AddWithValue("chunkOrdinal", chunk.ChunkOrdinal);
        cmd.ExecuteNonQuery();
    }

    private static void CompleteBandRankHistoryChunk(
        NpgsqlConnection conn,
        long jobId,
        BandRankHistoryChunkInfo chunk,
        long rowsScanned,
        long rowsInserted,
        long rowsSkipped,
        int commandTimeoutSeconds)
    {
        using var cmd = conn.CreateCommand();
        ConfigureCommandTimeout(cmd, commandTimeoutSeconds);
        cmd.CommandText = @"
            UPDATE band_rank_history_job_chunks
            SET status = 'complete', completed_at = now(), updated_at = now(),
                rows_scanned = @rowsScanned, rows_inserted = @rowsInserted, rows_skipped = @rowsSkipped,
                last_error = NULL
            WHERE job_id = @jobId AND ranking_scope = @scope AND combo_id = @comboId AND chunk_ordinal = @chunkOrdinal;

            UPDATE band_rank_history_jobs job
            SET chunks_completed = counts.completed_count,
                rows_scanned = counters.rows_scanned,
                rows_inserted = counters.rows_inserted,
                rows_skipped = counters.rows_skipped,
                updated_at = now()
            FROM (
                SELECT job_id, count(*) FILTER (WHERE status = 'complete')::int AS completed_count
                FROM band_rank_history_job_chunks
                WHERE job_id = @jobId
                GROUP BY job_id
            ) counts,
            (
                SELECT job_id,
                       COALESCE(sum(rows_scanned), 0)::bigint AS rows_scanned,
                       COALESCE(sum(rows_inserted), 0)::bigint AS rows_inserted,
                       COALESCE(sum(rows_skipped), 0)::bigint AS rows_skipped
                FROM band_rank_history_job_chunks
                WHERE job_id = @jobId
                GROUP BY job_id
            ) counters
            WHERE job.job_id = counts.job_id AND job.job_id = counters.job_id";
        cmd.Parameters.AddWithValue("jobId", jobId);
        cmd.Parameters.AddWithValue("scope", chunk.RankingScope);
        cmd.Parameters.AddWithValue("comboId", chunk.ComboId);
        cmd.Parameters.AddWithValue("chunkOrdinal", chunk.ChunkOrdinal);
        cmd.Parameters.AddWithValue("rowsScanned", rowsScanned);
        cmd.Parameters.AddWithValue("rowsInserted", rowsInserted);
        cmd.Parameters.AddWithValue("rowsSkipped", rowsSkipped);
        cmd.ExecuteNonQuery();
    }

    public BandRankHistoryStatusDto GetBandRankHistoryStatus(string bandType, string? comboId = null)
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;

        using var conn = _ds.OpenConnection();
        var readSource = _bandRankHistoryOptions.ApiReadSource;
        var historyJobsExists = TableExists(conn, null, "band_rank_history_jobs");

        DateTime? currentComputedAtUtc = null;
        string? currentComputedAt = null;
        try
        {
            var statsTable = ResolveBandRankingStatsReadTable(conn, bandType);
            if (TableExists(conn, null, statsTable))
            {
                using var current = conn.CreateCommand();
                current.CommandText = $@"
                    SELECT max(computed_at)
                    FROM {BandRankingStorageNames.QuoteIdentifier(statsTable)}
                    WHERE band_type = @bandType
                      AND ranking_scope = @scope
                      AND combo_id = @comboId";
                current.Parameters.AddWithValue("bandType", bandType);
                current.Parameters.AddWithValue("scope", rankingScope);
                current.Parameters.AddWithValue("comboId", normalizedComboId);
                var result = current.ExecuteScalar();
                if (result is DateTime dt)
                {
                    currentComputedAtUtc = NormalizeUtc(dt);
                    currentComputedAt = currentComputedAtUtc.Value.ToString("o");
                }
                else if (result is DateTimeOffset dto)
                {
                    currentComputedAtUtc = dto.UtcDateTime;
                    currentComputedAt = currentComputedAtUtc.Value.ToString("o");
                }
            }
        }
        catch
        {
            // Current ranking tables are created lazily by the ranking publisher.
        }

        DateOnly? historyThroughDate;
        if (TryResolveReadyBandRankHistoryCompactV3Source(conn, bandType, out _))
        {
            historyThroughDate = GetBandRankHistoryThroughFromCompactV3(conn, bandType);
        }
        else
        {
            historyThroughDate = null;
            if (readSource is BandRankHistoryApiReadSource.V2NarrowOnly or BandRankHistoryApiReadSource.V2NarrowWithLegacyFallback)
            {
                historyThroughDate = GetBandRankHistoryThroughFromV2(conn, bandType, rankingScope, normalizedComboId);
            }

            if (historyThroughDate is null && readSource != BandRankHistoryApiReadSource.V2NarrowOnly)
            {
                historyThroughDate = GetBandRankHistoryThroughFromLegacy(conn, bandType, rankingScope, normalizedComboId, readSource);
            }
        }

        var historyThrough = historyThroughDate?.ToString("yyyy-MM-dd");
        var freshnessStatus = GetBandRankHistoryFreshnessStatus(currentComputedAtUtc, historyThroughDate);
        var freshnessMessage = GetBandRankHistoryFreshnessMessage(currentComputedAtUtc, historyThroughDate);

        BandRankHistoryJobInfo? job = null;
        if (historyJobsExists)
        {
            using var jobs = conn.CreateCommand();
            jobs.CommandText = @"
                SELECT job_id, scrape_id, snapshot_date, band_type, mode, status, started_at, completed_at,
                       failed_at, paused_at, superseded_at, last_error, attempts, chunks_total,
                       chunks_completed, rows_scanned, rows_inserted, rows_skipped,
                       current_ranking_scope, current_combo_id, updated_at
                FROM band_rank_history_jobs
                WHERE band_type = @bandType
                ORDER BY snapshot_date DESC, scrape_id DESC, job_id DESC
                LIMIT 1";
            jobs.Parameters.AddWithValue("bandType", bandType);
            using var reader = jobs.ExecuteReader();
            if (reader.Read())
                job = ReadBandRankHistoryJob(reader);
        }

        if (_bandRankHistoryOptions.Mode == BandRankHistoryMode.Disabled)
        {
            return new BandRankHistoryStatusDto
            {
                HistoryStatus = "disabled",
                CurrentRankingsComputedAt = currentComputedAt,
                HistoryComputedThrough = historyThrough,
                HistoryJobUpdatedAt = job?.UpdatedAt,
                HistoryMessage = GetBandRankHistoryDisabledMessage(currentComputedAtUtc, historyThroughDate),
            };
        }

        if (job is null)
        {
            return new BandRankHistoryStatusDto
            {
                HistoryStatus = freshnessStatus,
                CurrentRankingsComputedAt = currentComputedAt,
                HistoryComputedThrough = historyThrough,
                HistoryMessage = freshnessMessage,
            };
        }

        var status = job.Status switch
        {
            "queued" or "running" or "paused" => "catching_up",
            "failed" => "failed",
            "disabled" => "disabled",
            "superseded" => freshnessStatus,
            _ => freshnessStatus,
        };

        return new BandRankHistoryStatusDto
        {
            HistoryStatus = status,
            CurrentRankingsComputedAt = currentComputedAt,
            HistoryComputedThrough = historyThrough,
            HistoryJobUpdatedAt = job.UpdatedAt,
            HistoryMessage = job.Status switch
            {
                "queued" => "Band rank history is queued for background catch-up.",
                "running" => $"Band rank history is catching up ({job.ChunksCompleted}/{job.ChunksTotal} chunks).",
                "paused" => "Band rank history is paused while current scrape work has priority.",
                "failed" => job.LastError ?? "Band rank history catch-up failed.",
                "disabled" => GetBandRankHistoryDisabledMessage(currentComputedAtUtc, historyThroughDate),
                _ => freshnessMessage,
            },
        };
    }

    private static DateOnly? GetBandRankHistoryThroughFromCompactV3(NpgsqlConnection conn, string bandType)
    {
        using var hist = conn.CreateCommand();
        hist.CommandText = $"""
            SELECT max_snapshot_date
            FROM {BandRankHistoryCompactV3StateTable}
            WHERE band_type = @bandType
              AND status = 'ready'
            """;
        hist.Parameters.AddWithValue("bandType", bandType);
        return ReadSnapshotDate(hist.ExecuteScalar());
    }

    private static DateOnly? GetBandRankHistoryThroughFromV2(NpgsqlConnection conn, string bandType, string rankingScope, string normalizedComboId)
    {
        if (!TableExists(conn, null, "band_team_rank_history_snapshot_v2"))
            return null;

        using var hist = conn.CreateCommand();
        hist.CommandText = @"
            SELECT max(snapshot_date)
            FROM band_team_rank_history_snapshot_v2
            WHERE band_type = @bandType
              AND ranking_scope = @scope
              AND combo_id = @comboId
              AND status = 'complete'";
        hist.Parameters.AddWithValue("bandType", bandType);
        hist.Parameters.AddWithValue("scope", rankingScope);
        hist.Parameters.AddWithValue("comboId", normalizedComboId);
        return ReadSnapshotDate(hist.ExecuteScalar());
    }

    private static DateOnly? GetBandRankHistoryThroughFromLegacy(
        NpgsqlConnection conn,
        string bandType,
        string rankingScope,
        string normalizedComboId,
        BandRankHistoryApiReadSource readSource)
    {
        if (readSource != BandRankHistoryApiReadSource.Wide
            && TableExists(conn, null, "band_team_rank_history_points"))
        {
            var narrowDate = ReadBandRankHistoryMaxSnapshotDate(conn, "band_team_rank_history_points", bandType, rankingScope, normalizedComboId);
            if (narrowDate is not null || readSource == BandRankHistoryApiReadSource.Narrow)
                return narrowDate;
        }

        if (readSource != BandRankHistoryApiReadSource.Narrow
            && TableExists(conn, null, "band_team_ranking_stats_history"))
        {
            return ReadBandRankHistoryMaxSnapshotDate(conn, "band_team_ranking_stats_history", bandType, rankingScope, normalizedComboId);
        }

        return null;
    }

    private static DateOnly? ReadBandRankHistoryMaxSnapshotDate(
        NpgsqlConnection conn,
        string tableName,
        string bandType,
        string rankingScope,
        string normalizedComboId)
    {
        using var hist = conn.CreateCommand();
        hist.CommandText = $@"
            SELECT max(snapshot_date)
            FROM {BandRankingStorageNames.QuoteIdentifier(tableName)}
            WHERE band_type = @bandType
              AND ranking_scope = @scope
              AND combo_id = @comboId";
        hist.Parameters.AddWithValue("bandType", bandType);
        hist.Parameters.AddWithValue("scope", rankingScope);
        hist.Parameters.AddWithValue("comboId", normalizedComboId);
        return ReadSnapshotDate(hist.ExecuteScalar());
    }

    private static DateOnly? ReadSnapshotDate(object? result) => result switch
    {
        DateOnly date => date,
        DateTime dt => DateOnly.FromDateTime(dt),
        _ => null,
    };

    private static string GetBandRankHistoryFreshnessStatus(DateTime? currentComputedAtUtc, DateOnly? historyThrough)
    {
        // History snapshots use UTC calendar dates, so freshness aligns to the current ranking's UTC date.
        if (currentComputedAtUtc is null || historyThrough is null)
            return "stale";

        return historyThrough.Value == DateOnly.FromDateTime(currentComputedAtUtc.Value)
            ? "current"
            : "stale";
    }

    private static string? GetBandRankHistoryFreshnessMessage(DateTime? currentComputedAtUtc, DateOnly? historyThrough)
    {
        if (historyThrough is null)
            return "No band rank history is available from the configured API read source.";

        if (currentComputedAtUtc is null)
            return $"Band rank history is through {historyThrough:yyyy-MM-dd}, but the current ranking timestamp is unavailable.";

        var currentRankingDate = DateOnly.FromDateTime(currentComputedAtUtc.Value);
        if (historyThrough.Value < currentRankingDate)
        {
            return $"Band rank history is through {historyThrough:yyyy-MM-dd}; current rankings are dated {currentRankingDate:yyyy-MM-dd} UTC.";
        }

        if (historyThrough.Value > currentRankingDate)
        {
            return $"Band rank history is through {historyThrough:yyyy-MM-dd}, which does not align with the current ranking date {currentRankingDate:yyyy-MM-dd} UTC.";
        }

        return null;
    }

    private static string GetBandRankHistoryDisabledMessage(DateTime? currentComputedAtUtc, DateOnly? historyThrough)
    {
        var message = "Band rank history writes are disabled.";
        if (historyThrough is not null)
            message += $" Readable history is through {historyThrough:yyyy-MM-dd}.";
        else
            message += " No readable history is available from the configured API read source.";

        if (currentComputedAtUtc is not null)
        {
            var currentRankingDate = DateOnly.FromDateTime(currentComputedAtUtc.Value);
            message += $" Current rankings are dated {currentRankingDate:yyyy-MM-dd} UTC.";
        }

        return message;
    }

    public (List<BandTeamRankingDto> Entries, int TotalTeams) GetBandTeamRankings(string bandType, string? comboId = null, string rankBy = "adjusted", int page = 1, int pageSize = 50, bool usePublishedSnapshot = false)
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;
        var totalTeams = GetBandRankingTotalTeams(bandType, rankingScope, normalizedComboId, usePublishedSnapshot);
        var rankColumn = BandRankColumn(rankBy);

        using var conn = _ds.OpenConnection();
        var rankingsTable = ResolveBandRankingReadTable(conn, bandType, usePublishedSnapshot);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                r.band_type, r.combo_id, r.team_key, r.team_members, r.songs_played, r.total_charted_songs,
                r.coverage, r.raw_skill_rating, r.adjusted_skill_rating, r.adjusted_skill_rank,
                r.weighted_rating, r.weighted_rank, r.fc_rate, r.fc_rate_rank, r.total_score,
                r.total_score_rank, r.avg_accuracy, r.full_combo_count, r.avg_stars, r.best_rank,
                r.avg_rank, r.raw_weighted_rating, r.computed_at,
                projection.member_instruments_json::text AS member_instruments_json
            FROM {BandRankingStorageNames.QuoteIdentifier(rankingsTable)} r
            LEFT JOIN {BandSearchProjectionBuilder.TeamProjectionTable} projection
              ON projection.band_type = r.band_type
             AND projection.team_key = r.team_key
            WHERE r.band_type = @bandType AND r.ranking_scope = @scope AND r.combo_id = @comboId
            ORDER BY r.{rankColumn} ASC
            LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("limit", pageSize);
        cmd.Parameters.AddWithValue("offset", (page - 1) * pageSize);

        var entries = new List<BandTeamRankingDto>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                entries.Add(ReadBandTeamRanking(reader, totalTeams));
        }

        AttachBandRankingConfigurations(conn, entries, bandType, normalizedComboId);

        return (entries, totalTeams);
    }

    public BandTeamRankingDto? GetBandTeamRanking(string bandType, string teamKey, string? comboId = null, bool usePublishedSnapshot = false)
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;
        var totalTeams = GetBandRankingTotalTeams(bandType, rankingScope, normalizedComboId, usePublishedSnapshot);

        using var conn = _ds.OpenConnection();
        var rankingsTable = ResolveBandRankingReadTable(conn, bandType, usePublishedSnapshot);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                r.band_type, r.combo_id, r.team_key, r.team_members, r.songs_played, r.total_charted_songs,
                r.coverage, r.raw_skill_rating, r.adjusted_skill_rating, r.adjusted_skill_rank,
                r.weighted_rating, r.weighted_rank, r.fc_rate, r.fc_rate_rank, r.total_score,
                r.total_score_rank, r.avg_accuracy, r.full_combo_count, r.avg_stars, r.best_rank,
                r.avg_rank, r.raw_weighted_rating, r.computed_at,
                projection.member_instruments_json::text AS member_instruments_json
            FROM {BandRankingStorageNames.QuoteIdentifier(rankingsTable)} r
            LEFT JOIN {BandSearchProjectionBuilder.TeamProjectionTable} projection
              ON projection.band_type = r.band_type
             AND projection.team_key = r.team_key
            WHERE r.band_type = @bandType AND r.ranking_scope = @scope AND r.combo_id = @comboId AND r.team_key = @teamKey";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        BandTeamRankingDto? ranking;
        using (var reader = cmd.ExecuteReader())
        {
            ranking = reader.Read() ? ReadBandTeamRanking(reader, totalTeams) : null;
        }
        if (ranking is not null)
            AttachBandRankingConfigurations(conn, [ranking], bandType, normalizedComboId);
        return ranking;
    }

    public BandTeamRankingDto? GetBandTeamRankingForAccount(string bandType, string accountId, string? comboId = null, string rankBy = "adjusted", bool usePublishedSnapshot = false)
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;
        var totalTeams = GetBandRankingTotalTeams(bandType, rankingScope, normalizedComboId, usePublishedSnapshot);
        var rankColumn = BandRankColumn(rankBy);

        using var conn = _ds.OpenConnection();
        var rankingsTable = ResolveBandRankingReadTable(conn, bandType, usePublishedSnapshot);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            WITH candidate_teams AS (
                SELECT DISTINCT team_key
                FROM band_team_membership
                WHERE account_id = @accountId
                  AND band_type = @bandType
            )
            SELECT
                r.band_type, r.combo_id, r.team_key, r.team_members, r.songs_played, r.total_charted_songs,
                r.coverage, r.raw_skill_rating, r.adjusted_skill_rating, r.adjusted_skill_rank,
                r.weighted_rating, r.weighted_rank, r.fc_rate, r.fc_rate_rank, r.total_score,
                r.total_score_rank, r.avg_accuracy, r.full_combo_count, r.avg_stars, r.best_rank,
                r.avg_rank, r.raw_weighted_rating, r.computed_at,
                projection.member_instruments_json::text AS member_instruments_json
            FROM candidate_teams candidate
            JOIN {BandRankingStorageNames.QuoteIdentifier(rankingsTable)} r
              ON r.band_type = @bandType
             AND r.ranking_scope = @scope
             AND r.combo_id = @comboId
             AND r.team_key = candidate.team_key
            LEFT JOIN {BandSearchProjectionBuilder.TeamProjectionTable} projection
              ON projection.band_type = r.band_type
             AND projection.team_key = r.team_key
            ORDER BY r.{rankColumn} ASC
            LIMIT 1";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("accountId", accountId);

        BandTeamRankingDto? ranking;
        using (var reader = cmd.ExecuteReader())
        {
            ranking = reader.Read() ? ReadBandTeamRanking(reader, totalTeams) : null;
        }
        if (ranking is not null)
            AttachBandRankingConfigurations(conn, [ranking], bandType, normalizedComboId);
        return ranking;
    }

    public List<BandRankHistoryDto> GetBandRankHistory(string bandType, string teamKey, string? comboId = null, int days = 30)
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-Math.Max(days, 1)));
        var readSource = _bandRankHistoryOptions.ApiReadSource;

        using var conn = _ds.OpenConnection();

        if (TryResolveReadyBandRankHistoryCompactV3Source(conn, bandType, out var compactV3Source))
        {
            return GetBandRankHistoryFromCompactV3(
                conn,
                compactV3Source.PointsTable,
                compactV3Source.TeamTable,
                compactV3Source.ComboTable,
                teamKey,
                rankingScope,
                normalizedComboId,
                cutoff);
        }

        if (readSource is BandRankHistoryApiReadSource.V2NarrowOnly or BandRankHistoryApiReadSource.V2NarrowWithLegacyFallback
            && TableExists(conn, null, "band_team_rank_history_points_v2"))
        {
            var v2 = GetBandRankHistoryFromV2Points(conn, bandType, teamKey, rankingScope, normalizedComboId, cutoff);
            if (v2.Count > 0 || readSource == BandRankHistoryApiReadSource.V2NarrowOnly)
                return v2;
        }

        if (readSource == BandRankHistoryApiReadSource.V2NarrowOnly)
            return [];

        if (readSource is BandRankHistoryApiReadSource.Narrow or BandRankHistoryApiReadSource.NarrowWithWideFallback or BandRankHistoryApiReadSource.V2NarrowWithLegacyFallback
            && TableExists(conn, null, "band_team_rank_history_points"))
        {
            var narrow = GetBandRankHistoryFromPoints(conn, bandType, teamKey, rankingScope, normalizedComboId, cutoff);
            if (narrow.Count > 0 || readSource == BandRankHistoryApiReadSource.Narrow)
                return narrow;
        }

        if (readSource == BandRankHistoryApiReadSource.Narrow)
            return [];

        return GetBandRankHistoryFromWide(conn, bandType, teamKey, rankingScope, normalizedComboId, cutoff);
    }

    private static List<BandRankHistoryDto> GetBandRankHistoryFromWide(
        NpgsqlConnection conn,
        string bandType,
        string teamKey,
        string rankingScope,
        string normalizedComboId,
        DateOnly cutoff)
    {
        if (!TableExists(conn, null, "band_team_rank_history") || !TableExists(conn, null, "band_team_ranking_stats_history"))
            return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                h.snapshot_date,
                h.computed_at,
                h.adjusted_skill_rank,
                h.weighted_rank,
                h.fc_rate_rank,
                h.total_score_rank,
                h.adjusted_skill_rating,
                h.weighted_rating,
                h.fc_rate,
                h.total_score,
                h.songs_played,
                h.coverage,
                h.full_combo_count,
                h.raw_weighted_rating,
                h.raw_skill_rating,
                h.total_charted_songs,
                stats.total_teams
            FROM (
                SELECT DISTINCT ON (snapshot_date)
                    snapshot_date,
                    computed_at,
                    adjusted_skill_rank,
                    weighted_rank,
                    fc_rate_rank,
                    total_score_rank,
                    adjusted_skill_rating,
                    weighted_rating,
                    fc_rate,
                    total_score,
                    songs_played,
                    coverage,
                    full_combo_count,
                    raw_weighted_rating,
                    raw_skill_rating,
                    total_charted_songs
                FROM band_team_rank_history
                WHERE band_type = @bandType
                  AND ranking_scope = @scope
                  AND combo_id = @comboId
                  AND team_key = @teamKey
                  AND snapshot_date >= @cutoff
                ORDER BY snapshot_date DESC
            ) h
            LEFT JOIN band_team_ranking_stats_history stats
                ON stats.band_type = @bandType
               AND stats.ranking_scope = @scope
               AND stats.combo_id = @comboId
               AND stats.snapshot_date = h.snapshot_date
            ORDER BY h.snapshot_date;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("cutoff", cutoff);

        var history = new List<BandRankHistoryDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            history.Add(new BandRankHistoryDto
            {
                SnapshotDate = reader.GetDateTime(0).ToString("yyyy-MM-dd"),
                SnapshotTakenAt = reader.GetDateTime(1).ToString("o"),
                AdjustedSkillRank = reader.GetInt32(2),
                WeightedRank = reader.GetInt32(3),
                FcRateRank = reader.GetInt32(4),
                TotalScoreRank = reader.GetInt32(5),
                AdjustedSkillRating = reader.GetDouble(6),
                WeightedRating = reader.GetDouble(7),
                FcRate = reader.GetDouble(8),
                TotalScore = reader.GetInt64(9),
                SongsPlayed = reader.GetInt32(10),
                Coverage = reader.GetDouble(11),
                FullComboCount = reader.GetInt32(12),
                RawWeightedRating = reader.IsDBNull(13) ? null : reader.GetDouble(13),
                RawSkillRating = reader.GetDouble(14),
                TotalChartedSongs = reader.GetInt32(15),
                TotalRankedTeams = reader.IsDBNull(16) ? null : reader.GetInt32(16),
            });
        }

        return history;
    }

    private static List<BandRankHistoryDto> GetBandRankHistoryFromPoints(
        NpgsqlConnection conn,
        string bandType,
        string teamKey,
        string rankingScope,
        string normalizedComboId,
        DateOnly cutoff) => GetBandRankHistoryFromPointsTable(
            conn,
            "band_team_rank_history_points",
            bandType,
            teamKey,
            rankingScope,
            normalizedComboId,
            cutoff);

    private static List<BandRankHistoryDto> GetBandRankHistoryFromV2Points(
        NpgsqlConnection conn,
        string bandType,
        string teamKey,
        string rankingScope,
        string normalizedComboId,
        DateOnly cutoff) => GetBandRankHistoryFromPointsTable(
            conn,
            "band_team_rank_history_points_v2",
            bandType,
            teamKey,
            rankingScope,
            normalizedComboId,
            cutoff);

    private sealed record BandRankHistoryCompactV3Source(
        string PointsTable,
        string TeamTable,
        string ComboTable);

    private bool TryResolveReadyBandRankHistoryCompactV3Source(
        NpgsqlConnection conn,
        string bandType,
        out BandRankHistoryCompactV3Source source)
    {
        if (_bandRankHistoryOptions.CompactV3DuetsReadEnabled
            && string.Equals(bandType, "Band_Duets", StringComparison.Ordinal)
            && IsBandRankHistoryCompactV3Ready(
                conn,
                bandType,
                BandRankHistoryCompactV3DuetsTable,
                BandRankHistoryCompactV3DuetsTeamTable,
                BandRankHistoryCompactV3DuetsComboTable,
                ref _bandRankHistoryCompactV3DuetsReady))
        {
            source = new BandRankHistoryCompactV3Source(
                BandRankHistoryCompactV3DuetsTable,
                BandRankHistoryCompactV3DuetsTeamTable,
                BandRankHistoryCompactV3DuetsComboTable);
            return true;
        }

        if (_bandRankHistoryOptions.CompactV3TriosReadEnabled
            && string.Equals(bandType, "Band_Trios", StringComparison.Ordinal)
            && IsBandRankHistoryCompactV3Ready(
                conn,
                bandType,
                BandRankHistoryCompactV3TriosTable,
                BandRankHistoryCompactV3TriosTeamTable,
                BandRankHistoryCompactV3TriosComboTable,
                ref _bandRankHistoryCompactV3TriosReady))
        {
            source = new BandRankHistoryCompactV3Source(
                BandRankHistoryCompactV3TriosTable,
                BandRankHistoryCompactV3TriosTeamTable,
                BandRankHistoryCompactV3TriosComboTable);
            return true;
        }

        if (_bandRankHistoryOptions.CompactV3QuadReadEnabled
            && string.Equals(bandType, "Band_Quad", StringComparison.Ordinal)
            && IsBandRankHistoryCompactV3Ready(
                conn,
                bandType,
                BandRankHistoryCompactV3QuadTable,
                BandRankHistoryCompactV3QuadTeamTable,
                BandRankHistoryCompactV3QuadComboTable,
                ref _bandRankHistoryCompactV3QuadReady))
        {
            source = new BandRankHistoryCompactV3Source(
                BandRankHistoryCompactV3QuadTable,
                BandRankHistoryCompactV3QuadTeamTable,
                BandRankHistoryCompactV3QuadComboTable);
            return true;
        }

        source = null!;
        return false;
    }

    private static bool IsBandRankHistoryCompactV3Ready(
        NpgsqlConnection conn,
        string bandType,
        string pointsTable,
        string teamTable,
        string comboTable,
        ref int readyCache)
    {
        if (Volatile.Read(ref readyCache) == 1)
            return true;

        if (!TableExists(conn, null, BandRankHistoryCompactV3StateTable)
            || !TableExists(conn, null, pointsTable)
            || !TableExists(conn, null, teamTable)
            || !TableExists(conn, null, comboTable))
        {
            return false;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT EXISTS (
                SELECT 1
                FROM {BandRankHistoryCompactV3StateTable}
                WHERE band_type = @bandType
                  AND status = 'ready'
            )
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        var ready = Convert.ToBoolean(cmd.ExecuteScalar() ?? false);
        if (ready)
            Volatile.Write(ref readyCache, 1);
        return ready;
    }

    private static List<BandRankHistoryDto> GetBandRankHistoryFromCompactV3(
        NpgsqlConnection conn,
        string pointsTable,
        string teamTable,
        string comboTable,
        string teamKey,
        string rankingScope,
        string normalizedComboId,
        DateOnly cutoff)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                points.snapshot_date,
                points.snapshot_taken_at,
                points.adjusted_skill_rank,
                points.weighted_rank,
                points.fc_rate_rank,
                points.total_score_rank,
                points.adjusted_skill_rating,
                points.weighted_rating,
                points.fc_rate,
                points.total_score,
                points.songs_played,
                points.coverage,
                points.full_combo_count,
                points.raw_weighted_rating,
                points.raw_skill_rating,
                points.total_charted_songs,
                points.total_ranked_teams
            FROM {pointsTable} points
            WHERE points.team_id = (
                    SELECT team_id
                    FROM {teamTable}
                    WHERE team_key = @teamKey
                )
              AND points.scope_id = @scopeId
              AND points.combo_ref = CASE
                    WHEN @scopeId = 0 THEN 0
                    ELSE COALESCE((
                        SELECT combo_ref
                        FROM {comboTable}
                        WHERE combo_id = @comboId
                    ), -1)
                END
              AND points.snapshot_date >= @cutoff
            ORDER BY points.snapshot_date DESC
            """;
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("scopeId", rankingScope == "overall" ? (short)0 : (short)1);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("cutoff", cutoff);
        return ReadBandRankHistoryPoints(cmd);
    }

    private static List<BandRankHistoryDto> GetBandRankHistoryFromPointsTable(
        NpgsqlConnection conn,
        string tableName,
        string bandType,
        string teamKey,
        string rankingScope,
        string normalizedComboId,
        DateOnly cutoff)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT DISTINCT ON (snapshot_date)
                snapshot_date,
                snapshot_taken_at,
                adjusted_skill_rank,
                weighted_rank,
                fc_rate_rank,
                total_score_rank,
                adjusted_skill_rating,
                weighted_rating,
                fc_rate,
                total_score,
                songs_played,
                coverage,
                full_combo_count,
                raw_weighted_rating,
                raw_skill_rating,
                total_charted_songs,
                total_ranked_teams
                        FROM {BandRankingStorageNames.QuoteIdentifier(tableName)}
            WHERE band_type = @bandType
              AND ranking_scope = @scope
              AND combo_id = @comboId
              AND team_key = @teamKey
              AND snapshot_date >= @cutoff
            ORDER BY snapshot_date DESC, snapshot_taken_at DESC";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("cutoff", cutoff);

        return ReadBandRankHistoryPoints(cmd);
    }

    private static List<BandRankHistoryDto> ReadBandRankHistoryPoints(NpgsqlCommand cmd)
    {
        var history = new List<BandRankHistoryDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            history.Add(new BandRankHistoryDto
            {
                SnapshotDate = reader.GetDateTime(0).ToString("yyyy-MM-dd"),
                SnapshotTakenAt = reader.GetDateTime(1).ToString("o"),
                AdjustedSkillRank = reader.GetInt32(2),
                WeightedRank = reader.GetInt32(3),
                FcRateRank = reader.GetInt32(4),
                TotalScoreRank = reader.GetInt32(5),
                AdjustedSkillRating = reader.GetDouble(6),
                WeightedRating = reader.GetDouble(7),
                FcRate = reader.GetDouble(8),
                TotalScore = reader.GetInt64(9),
                SongsPlayed = reader.GetInt32(10),
                Coverage = reader.GetDouble(11),
                FullComboCount = reader.GetInt32(12),
                RawWeightedRating = reader.IsDBNull(13) ? null : reader.GetDouble(13),
                RawSkillRating = reader.GetDouble(14),
                TotalChartedSongs = reader.GetInt32(15),
                TotalRankedTeams = reader.GetInt32(16),
            });
        }

        history.Reverse();
        return history;
    }

    public BandSongPerformancesResult GetPublishedBandSongPerformances(
        string bandType,
        string teamKey,
        string? comboId = null)
    {
        if (TryGetPublishedBandSongPerformances(bandType, teamKey, comboId, out var performances))
            return new BandSongPerformancesResult(true, performances);

        _log.LogWarning(
            "Published band song projections are unavailable for band_type={BandType}, combo_id={ComboId}; public song-row read failed closed.",
            bandType,
            comboId ?? string.Empty);
        return new BandSongPerformancesResult(false, []);
    }

    private bool TryGetPublishedBandSongPerformances(
        string bandType,
        string teamKey,
        string? comboId,
        out List<BandSongPerformanceDto> performances) =>
        TryGetBandSongPerformancesFromCurrentProjection(
            bandType,
            teamKey,
            comboId,
            out performances);

    private List<BandSongPerformanceDto> GetBandSongPerformancesLive(string bandType, string teamKey, string? comboId)
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            WITH NormalizedEntries AS (
                SELECT
                    be.song_id,
                    be.team_key,
                    be.score,
                    be.accuracy,
                    be.is_full_combo,
                    be.stars,
                    be.season,
                    COALESCE(be.end_time, '') AS end_time,
                    COALESCE((
                        SELECT string_agg(mapped.instrument, '+' ORDER BY mapped.sort_order, mapped.instrument)
                        FROM (
                            SELECT
                                CASE part::INT
                                    WHEN 0 THEN 'Solo_Guitar'
                                    WHEN 1 THEN 'Solo_Bass'
                                    WHEN 3 THEN 'Solo_Drums'
                                    WHEN 2 THEN 'Solo_Vocals'
                                    WHEN 4 THEN 'Solo_PeripheralGuitar'
                                    WHEN 5 THEN 'Solo_PeripheralBass'
                                    WHEN 7 THEN 'Solo_PeripheralVocals'
                                    WHEN 8 THEN 'Solo_PeripheralCymbals'
                                    WHEN 6 THEN 'Solo_PeripheralDrums'
                                    ELSE NULL
                                END AS instrument,
                                CASE part::INT
                                    WHEN 0 THEN 0
                                    WHEN 1 THEN 1
                                    WHEN 3 THEN 2
                                    WHEN 2 THEN 3
                                    WHEN 4 THEN 4
                                    WHEN 5 THEN 5
                                    WHEN 7 THEN 6
                                    WHEN 8 THEN 7
                                    WHEN 6 THEN 8
                                    ELSE 999
                                END AS sort_order
                            FROM unnest(string_to_array(be.instrument_combo, ':')) AS parts(part)
                        ) mapped
                        WHERE mapped.instrument IS NOT NULL
                    ), '') AS combo_id
                FROM band_entries be
                WHERE be.band_type = @bandType
                  AND NOT be.is_over_threshold
            ),
            ScopedEntries AS (
                SELECT *
                FROM NormalizedEntries
                WHERE @scope = 'overall' OR combo_id = @comboId
            ),
            ChosenEntries AS (
                SELECT *
                FROM (
                    SELECT
                        se.*,
                        ROW_NUMBER() OVER (
                            PARTITION BY se.song_id, se.team_key
                            ORDER BY se.score DESC, se.end_time ASC, se.combo_id ASC, se.team_key ASC
                        ) AS choice_rank
                    FROM ScopedEntries se
                ) ranked
                WHERE @scope = 'combo' OR choice_rank = 1
            ),
            RankedEntries AS (
                SELECT
                    ce.*,
                    (COUNT(*) OVER (PARTITION BY ce.song_id))::INT AS total_entries,
                    (ROW_NUMBER() OVER (
                        PARTITION BY ce.song_id
                        ORDER BY ce.score DESC, ce.end_time ASC, ce.team_key ASC
                    ))::INT AS effective_rank
                FROM ChosenEntries ce
            )
            SELECT
                song_id,
                NULLIF(combo_id, '') AS combo_id,
                effective_rank,
                total_entries,
                (effective_rank::DOUBLE PRECISION / NULLIF(total_entries, 0)) * 100.0 AS percentile,
                score,
                accuracy,
                is_full_combo,
                stars,
                season,
                NULLIF(end_time, '') AS end_time
            FROM RankedEntries
            WHERE team_key = @teamKey
            ORDER BY percentile ASC, effective_rank ASC, score DESC, song_id ASC;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);

        var performances = new List<BandSongPerformanceDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            performances.Add(ReadBandSongPerformance(reader));

        return performances;
    }

    private bool TryGetBandSongPerformancesFromCurrentProjection(
        string bandType,
        string teamKey,
        string? comboId,
        out List<BandSongPerformanceDto> performances)
    {
        var rankingScope = string.IsNullOrWhiteSpace(comboId) ? "overall" : "combo";
        var normalizedComboId = comboId ?? string.Empty;
        performances = [];

        using var conn = _ds.OpenConnection();

        using (var tableCmd = conn.CreateCommand())
        {
            tableCmd.CommandText = "SELECT to_regclass('public.current_band_leaderboard_entries') IS NOT NULL;";
            if (tableCmd.ExecuteScalar() is not bool tableExists || !tableExists)
                return false;
        }

        using (var scopeCmd = conn.CreateCommand())
        {
            scopeCmd.CommandText = @"
                SELECT EXISTS (
                    SELECT 1
                    FROM band_current_projection_scope
                    WHERE band_type = @bandType
                      AND ranking_scope = @scope
                      AND scope_combo_id = @comboId
                      AND published_generation IS NOT NULL
                );";
            scopeCmd.Parameters.AddWithValue("bandType", bandType);
            scopeCmd.Parameters.AddWithValue("scope", rankingScope);
            scopeCmd.Parameters.AddWithValue("comboId", normalizedComboId);
            if (scopeCmd.ExecuteScalar() is not bool hasScopeRows || !hasScopeRows)
                return false;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                cble.song_id,
                NULLIF(cble.entry_combo_id, '') AS combo_id,
                cble.rank AS effective_rank,
                cble.total_entries,
                cble.percentile,
                cble.score,
                cble.accuracy,
                cble.is_full_combo,
                cble.stars,
                cble.season,
                cble.end_time
            FROM current_band_leaderboard_entries cble
            JOIN band_current_projection_scope scope
              ON scope.song_id = cble.song_id
             AND scope.band_type = cble.band_type
             AND scope.ranking_scope = cble.ranking_scope
             AND scope.scope_combo_id = cble.scope_combo_id
             AND scope.published_generation = cble.projection_generation
            WHERE cble.band_type = @bandType
              AND cble.ranking_scope = @scope
              AND cble.scope_combo_id = @comboId
              AND cble.team_key = @teamKey
            ORDER BY cble.percentile ASC, cble.rank ASC, cble.score DESC, cble.song_id ASC;";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", normalizedComboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            performances.Add(ReadBandSongPerformance(reader));

        return true;
    }

    public BandSongPerformanceExtremesResult GetBandSongPerformanceExtremes(
        string bandType,
        string teamKey,
        string? comboId = null,
        int limit = 5,
        BandSongPerformanceReadMode readMode = BandSongPerformanceReadMode.Published)
    {
        if (TryGetPublishedBandSongPerformances(bandType, teamKey, comboId, out var performances))
            return CreateBandSongPerformanceExtremes(performances, limit);

        if (readMode == BandSongPerformanceReadMode.CurrentState)
        {
            _log.LogInformation(
                "Published band song projections are unavailable for band_type={BandType}, combo_id={ComboId}; explicitly reading current live state.",
                bandType,
                comboId ?? string.Empty);
            return CreateBandSongPerformanceExtremes(GetBandSongPerformancesLive(bandType, teamKey, comboId), limit);
        }

        _log.LogWarning(
            "Published band song projections are unavailable for band_type={BandType}, combo_id={ComboId}; public read failed closed.",
            bandType,
            comboId ?? string.Empty);
        return new BandSongPerformanceExtremesResult(false, [], []);
    }

    private static BandSongPerformanceExtremesResult CreateBandSongPerformanceExtremes(
        IReadOnlyCollection<BandSongPerformanceDto> performances,
        int limit)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 20);
        var best = performances
            .OrderBy(static performance => performance.Percentile)
            .ThenBy(static performance => performance.Rank)
            .ThenByDescending(static performance => performance.Score)
            .ThenBy(static performance => performance.SongId, StringComparer.Ordinal)
            .Take(effectiveLimit)
            .ToList();
        var worst = performances.Count > effectiveLimit
            ? performances
                .OrderByDescending(static performance => performance.Percentile)
                .ThenByDescending(static performance => performance.Rank)
                .ThenBy(static performance => performance.Score)
                .ThenBy(static performance => performance.SongId, StringComparer.Ordinal)
                .Take(effectiveLimit)
                .ToList()
            : [];

        return new BandSongPerformanceExtremesResult(true, best, worst);
    }

    private static BandSongPerformanceDto ReadBandSongPerformance(NpgsqlDataReader reader, int offset = 0) => new()
    {
        SongId = reader.GetString(offset),
        ComboId = reader.IsDBNull(offset + 1) ? null : reader.GetString(offset + 1),
        Rank = reader.GetInt32(offset + 2),
        TotalEntries = reader.GetInt32(offset + 3),
        Percentile = reader.IsDBNull(offset + 4) ? 0 : reader.GetDouble(offset + 4),
        Score = reader.GetInt32(offset + 5),
        Accuracy = reader.IsDBNull(offset + 6) ? null : reader.GetInt32(offset + 6),
        IsFullCombo = reader.IsDBNull(offset + 7) ? null : reader.GetBoolean(offset + 7),
        Stars = reader.IsDBNull(offset + 8) ? null : reader.GetInt32(offset + 8),
        Season = reader.IsDBNull(offset + 9) ? null : reader.GetInt32(offset + 9),
        EndTime = reader.IsDBNull(offset + 10) ? null : reader.GetString(offset + 10),
    };

    private const string SongBandLeaderboardBaseCtes = """
        ScopedEntries AS (
            SELECT
                be.song_id,
                be.band_type,
                be.team_key,
                be.instrument_combo,
                be.team_members,
                be.score,
                be.accuracy,
                be.is_full_combo,
                be.stars,
                be.difficulty,
                be.season,
                COALESCE(be.end_time, '') AS end_time
            FROM band_entries be
            WHERE be.song_id = @songId
              AND be.band_type = @bandType
              AND (@comboId IS NULL OR be.instrument_combo = ANY(@comboRawIds))
              AND NOT be.is_over_threshold
        ),
        ChosenEntries AS (
            SELECT *
            FROM (
                SELECT
                    se.*,
                    ROW_NUMBER() OVER (
                        PARTITION BY se.team_key
                        ORDER BY se.score DESC, se.end_time ASC, se.instrument_combo ASC, se.team_key ASC
                    ) AS choice_rank
                FROM ScopedEntries se
            ) ranked
            WHERE choice_rank = 1
        )
        """;

    private const string SongBandLeaderboardEntryRowsSql = """
            SELECT
                pe.team_key,
                pe.instrument_combo,
                pe.score,
                pe.accuracy,
                pe.is_full_combo,
                pe.stars,
                pe.difficulty,
                pe.season,
                pe.effective_rank,
                pe.total_entries,
                (pe.effective_rank::DOUBLE PRECISION / NULLIF(pe.total_entries, 0)) * 100.0 AS percentile,
                NULLIF(pe.end_time, '') AS end_time,
                pe.team_members,
                COALESCE(
                    ARRAY_AGG(bms.account_id ORDER BY bms.member_index) FILTER (WHERE bms.account_id IS NOT NULL),
                    ARRAY[]::TEXT[]
                ) AS account_ids,
                COALESCE(
                    ARRAY_AGG(COALESCE(bms.instrument_id, -1) ORDER BY bms.member_index) FILTER (WHERE bms.account_id IS NOT NULL),
                    ARRAY[]::INT[]
                ) AS instrument_ids,
                COALESCE(
                    ARRAY_AGG(COALESCE(bms.score, -1) ORDER BY bms.member_index) FILTER (WHERE bms.account_id IS NOT NULL),
                    ARRAY[]::INT[]
                ) AS member_scores,
                COALESCE(
                    ARRAY_AGG(COALESCE(bms.accuracy, -1) ORDER BY bms.member_index) FILTER (WHERE bms.account_id IS NOT NULL),
                    ARRAY[]::INT[]
                ) AS member_accuracies,
                COALESCE(
                    ARRAY_AGG(
                        CASE
                            WHEN bms.is_full_combo IS TRUE THEN 1
                            WHEN bms.is_full_combo IS FALSE THEN 0
                            ELSE -1
                        END
                        ORDER BY bms.member_index
                    ) FILTER (WHERE bms.account_id IS NOT NULL),
                    ARRAY[]::INT[]
                ) AS member_full_combos,
                COALESCE(
                    ARRAY_AGG(COALESCE(bms.stars, -1) ORDER BY bms.member_index) FILTER (WHERE bms.account_id IS NOT NULL),
                    ARRAY[]::INT[]
                ) AS member_stars,
                COALESCE(
                    ARRAY_AGG(COALESCE(bms.difficulty, -1) ORDER BY bms.member_index) FILTER (WHERE bms.account_id IS NOT NULL),
                    ARRAY[]::INT[]
                ) AS member_difficulties
            FROM PagedEntries pe
            LEFT JOIN band_member_stats bms
                ON bms.song_id = pe.song_id
               AND bms.band_type = pe.band_type
               AND bms.team_key = pe.team_key
               AND bms.instrument_combo = pe.instrument_combo
            GROUP BY
                pe.song_id,
                pe.band_type,
                pe.team_key,
                pe.instrument_combo,
                pe.team_members,
                pe.score,
                pe.accuracy,
                pe.is_full_combo,
                pe.stars,
                pe.difficulty,
                pe.season,
                pe.end_time,
                pe.total_entries,
                pe.effective_rank
            ORDER BY pe.effective_rank ASC
        """;

    public (List<SongBandLeaderboardEntryDto> Entries, int TotalEntries) GetSongBandLeaderboard(string songId, string bandType, int limit = 25, int offset = 0, string? comboId = null, bool requireCurrentProjection = false)
    {
        var effectiveLimit = Math.Clamp(limit, 1, 200);
        var effectiveOffset = Math.Max(0, offset);

        using var conn = _ds.OpenConnection();
        if (TryGetSongBandLeaderboardFromCurrentProjection(conn, songId, bandType, effectiveLimit, effectiveOffset, comboId, out var projected))
            return projected;

        if (requireCurrentProjection)
            return ([], 0);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH {SongBandLeaderboardBaseCtes}
            SELECT COUNT(*)::INT FROM ChosenEntries;

            WITH {SongBandLeaderboardBaseCtes},
            RankedEntries AS (
                SELECT
                    ce.*,
                    (COUNT(*) OVER ())::INT AS total_entries,
                    (ROW_NUMBER() OVER (
                        ORDER BY ce.score DESC, ce.end_time ASC, ce.team_key ASC
                    ))::INT AS effective_rank
                FROM ChosenEntries ce
            ),
            PagedEntries AS (
                SELECT *
                FROM RankedEntries
                ORDER BY effective_rank ASC
                LIMIT @limit OFFSET @offset
            )
            {SongBandLeaderboardEntryRowsSql};
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("limit", effectiveLimit);
        cmd.Parameters.AddWithValue("offset", effectiveOffset);
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = (object?)comboId ?? DBNull.Value;
        cmd.Parameters.Add("comboRawIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = BandComboIds.ToEpicRawComboCandidates(comboId).ToArray();

        var entries = new List<SongBandLeaderboardEntryDto>();
        var totalEntries = 0;
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
            totalEntries = reader.GetInt32(0);

        if (!reader.NextResult())
            return (entries, totalEntries);

        entries.AddRange(ReadSongBandLeaderboardEntries(reader, bandType));

        return (entries, totalEntries);
    }

    public SongBandLeaderboardEntryDto? GetSongBandLeaderboardEntryForAccount(string songId, string bandType, string accountId, string? comboId = null, bool requireCurrentProjection = false)
    {
        using var conn = _ds.OpenConnection();
        if (TryGetSongBandLeaderboardEntryForAccountFromCurrentProjection(conn, songId, bandType, accountId, comboId, out var projected))
            return projected;

        if (requireCurrentProjection)
            return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH {SongBandLeaderboardBaseCtes},
            RankedEntries AS (
                SELECT
                    ce.*,
                    (COUNT(*) OVER ())::INT AS total_entries,
                    (ROW_NUMBER() OVER (
                        ORDER BY ce.score DESC, ce.end_time ASC, ce.team_key ASC
                    ))::INT AS effective_rank
                FROM ChosenEntries ce
            ),
            PagedEntries AS (
                SELECT *
                FROM RankedEntries
                WHERE @accountId = ANY(team_members)
                ORDER BY effective_rank ASC
                LIMIT 1
            )
            {SongBandLeaderboardEntryRowsSql};
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = (object?)comboId ?? DBNull.Value;
        cmd.Parameters.Add("comboRawIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = BandComboIds.ToEpicRawComboCandidates(comboId).ToArray();

        using var reader = cmd.ExecuteReader();
        return ReadSongBandLeaderboardEntries(reader, bandType).FirstOrDefault();
    }

    public SongBandLeaderboardEntryDto? GetSongBandLeaderboardEntryForTeam(string songId, string bandType, string teamKey, string? comboId = null, bool requireCurrentProjection = false)
    {
        using var conn = _ds.OpenConnection();
        if (TryGetSongBandLeaderboardEntryForTeamFromCurrentProjection(conn, songId, bandType, teamKey, comboId, out var projected))
            return projected;

        if (requireCurrentProjection)
            return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH {SongBandLeaderboardBaseCtes},
            RankedEntries AS (
                SELECT
                    ce.*,
                    (COUNT(*) OVER ())::INT AS total_entries,
                    (ROW_NUMBER() OVER (
                        ORDER BY ce.score DESC, ce.end_time ASC, ce.team_key ASC
                    ))::INT AS effective_rank
                FROM ChosenEntries ce
            ),
            PagedEntries AS (
                SELECT *
                FROM RankedEntries
                WHERE team_key = @teamKey
                ORDER BY effective_rank ASC
                LIMIT 1
            )
            {SongBandLeaderboardEntryRowsSql};
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.Add("comboId", NpgsqlDbType.Text).Value = (object?)comboId ?? DBNull.Value;
        cmd.Parameters.Add("comboRawIds", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = BandComboIds.ToEpicRawComboCandidates(comboId).ToArray();

        using var reader = cmd.ExecuteReader();
        return ReadSongBandLeaderboardEntries(reader, bandType).FirstOrDefault();
    }

    private bool TryGetSongBandLeaderboardFromCurrentProjection(
        NpgsqlConnection conn,
        string songId,
        string bandType,
        int limit,
        int offset,
        string? comboId,
        out (List<SongBandLeaderboardEntryDto> Entries, int TotalEntries) result)
    {
        result = ([], 0);

        if (!TryGetCurrentBandProjectionScope(bandType, comboId, out var rankingScope, out var scopeComboId) ||
            !TryGetPublishedCurrentBandProjectionScope(conn, songId, bandType, rankingScope, scopeComboId, out var totalEntries, out var projectionGeneration))
            return false;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH PagedEntries AS (
                SELECT
                    cble.song_id,
                    cble.band_type,
                    cble.team_key,
                    cble.entry_instrument_combo AS instrument_combo,
                    cble.team_members,
                    cble.score,
                    cble.accuracy,
                    cble.is_full_combo,
                    cble.stars,
                    cble.difficulty,
                    cble.season,
                    COALESCE(cble.end_time, '') AS end_time,
                    cble.rank AS effective_rank,
                    cble.total_entries
                FROM current_band_leaderboard_entries cble
                WHERE cble.song_id = @songId
                  AND cble.band_type = @bandType
                  AND cble.ranking_scope = @rankingScope
                  AND cble.scope_combo_id = @scopeComboId
                  AND cble.projection_generation = @projectionGeneration
                ORDER BY cble.rank ASC
                LIMIT @limit OFFSET @offset
            )
            {SongBandLeaderboardEntryRowsSql};
            """;
        AddCurrentBandProjectionScopeParameters(cmd, songId, bandType, rankingScope, scopeComboId);
        cmd.Parameters.AddWithValue("projectionGeneration", projectionGeneration);
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("offset", offset);

        using var reader = cmd.ExecuteReader();
        result = (ReadSongBandLeaderboardEntries(reader, bandType), totalEntries);
        return true;
    }

    private bool TryGetSongBandLeaderboardEntryForAccountFromCurrentProjection(
        NpgsqlConnection conn,
        string songId,
        string bandType,
        string accountId,
        string? comboId,
        out SongBandLeaderboardEntryDto? entry)
    {
        entry = null;

        if (!TryGetCurrentBandProjectionScope(bandType, comboId, out var rankingScope, out var scopeComboId) ||
            !TryGetPublishedCurrentBandProjectionScope(conn, songId, bandType, rankingScope, scopeComboId, out _, out var projectionGeneration))
            return false;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH PagedEntries AS (
                SELECT
                    cble.song_id,
                    cble.band_type,
                    cble.team_key,
                    cble.entry_instrument_combo AS instrument_combo,
                    cble.team_members,
                    cble.score,
                    cble.accuracy,
                    cble.is_full_combo,
                    cble.stars,
                    cble.difficulty,
                    cble.season,
                    COALESCE(cble.end_time, '') AS end_time,
                    cble.rank AS effective_rank,
                    cble.total_entries
                FROM current_band_leaderboard_entries cble
                WHERE cble.song_id = @songId
                  AND cble.band_type = @bandType
                  AND cble.ranking_scope = @rankingScope
                  AND cble.scope_combo_id = @scopeComboId
                  AND cble.projection_generation = @projectionGeneration
                  AND @accountId = ANY(cble.team_members)
                ORDER BY cble.rank ASC
                LIMIT 1
            )
            {SongBandLeaderboardEntryRowsSql};
            """;
        AddCurrentBandProjectionScopeParameters(cmd, songId, bandType, rankingScope, scopeComboId);
        cmd.Parameters.AddWithValue("projectionGeneration", projectionGeneration);
        cmd.Parameters.AddWithValue("accountId", accountId);

        using var reader = cmd.ExecuteReader();
        entry = ReadSongBandLeaderboardEntries(reader, bandType).FirstOrDefault();
        return true;
    }

    private bool TryGetSongBandLeaderboardEntryForTeamFromCurrentProjection(
        NpgsqlConnection conn,
        string songId,
        string bandType,
        string teamKey,
        string? comboId,
        out SongBandLeaderboardEntryDto? entry)
    {
        entry = null;

        if (!TryGetCurrentBandProjectionScope(bandType, comboId, out var rankingScope, out var scopeComboId) ||
            !TryGetPublishedCurrentBandProjectionScope(conn, songId, bandType, rankingScope, scopeComboId, out _, out var projectionGeneration))
            return false;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH PagedEntries AS (
                SELECT
                    cble.song_id,
                    cble.band_type,
                    cble.team_key,
                    cble.entry_instrument_combo AS instrument_combo,
                    cble.team_members,
                    cble.score,
                    cble.accuracy,
                    cble.is_full_combo,
                    cble.stars,
                    cble.difficulty,
                    cble.season,
                    COALESCE(cble.end_time, '') AS end_time,
                    cble.rank AS effective_rank,
                    cble.total_entries
                FROM current_band_leaderboard_entries cble
                WHERE cble.song_id = @songId
                  AND cble.band_type = @bandType
                  AND cble.ranking_scope = @rankingScope
                  AND cble.scope_combo_id = @scopeComboId
                  AND cble.projection_generation = @projectionGeneration
                  AND cble.team_key = @teamKey
                ORDER BY cble.rank ASC
                LIMIT 1
            )
            {SongBandLeaderboardEntryRowsSql};
            """;
        AddCurrentBandProjectionScopeParameters(cmd, songId, bandType, rankingScope, scopeComboId);
        cmd.Parameters.AddWithValue("projectionGeneration", projectionGeneration);
        cmd.Parameters.AddWithValue("teamKey", teamKey);

        using var reader = cmd.ExecuteReader();
        entry = ReadSongBandLeaderboardEntries(reader, bandType).FirstOrDefault();
        return true;
    }

    private static bool TryGetCurrentBandProjectionScope(string bandType, string? comboId, out string rankingScope, out string scopeComboId)
    {
        rankingScope = "overall";
        scopeComboId = string.Empty;

        if (!IsCurrentBandProjectionReadBandType(bandType))
            return false;

        if (string.IsNullOrWhiteSpace(comboId))
            return true;

        var normalized = BandComboIds.TryNormalizeForBandType(bandType, comboId);
        if (normalized.Error is not null || string.IsNullOrWhiteSpace(normalized.ComboId))
            return false;

        rankingScope = "combo";
        scopeComboId = normalized.ComboId;
        return true;
    }

    private static bool IsCurrentBandProjectionReadBandType(string bandType) =>
        string.Equals(bandType, "Band_Duets", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(bandType, "Band_Trios", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(bandType, "Band_Quad", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetPublishedCurrentBandProjectionScope(
        NpgsqlConnection conn,
        string songId,
        string bandType,
        string rankingScope,
        string scopeComboId,
        out int rowCount,
        out long projectionGeneration)
    {
        rowCount = 0;
        projectionGeneration = 0;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT published_row_count, published_generation
            FROM band_current_projection_scope
            WHERE song_id = @songId
              AND band_type = @bandType
              AND ranking_scope = @rankingScope
              AND scope_combo_id = @scopeComboId
              AND published_generation IS NOT NULL
            LIMIT 1;
            """;
        AddCurrentBandProjectionScopeParameters(cmd, songId, bandType, rankingScope, scopeComboId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return false;

        var count = reader.GetInt64(0);
        rowCount = count > int.MaxValue ? int.MaxValue : (int)count;
        projectionGeneration = reader.GetInt64(1);
        return true;
    }

    private static void AddCurrentBandProjectionScopeParameters(
        NpgsqlCommand cmd,
        string songId,
        string bandType,
        string rankingScope,
        string scopeComboId)
    {
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("rankingScope", rankingScope);
        cmd.Parameters.AddWithValue("scopeComboId", scopeComboId);
    }

    public IReadOnlyList<string> GetBandLeaderboardSongIds()
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT song_id
            FROM band_entries
            WHERE NOT is_over_threshold
            ORDER BY song_id
            """;
        var songIds = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            songIds.Add(reader.GetString(0));
        return songIds;
    }

    private static List<SongBandLeaderboardEntryDto> ReadSongBandLeaderboardEntries(NpgsqlDataReader reader, string bandType)
    {
        var entries = new List<SongBandLeaderboardEntryDto>();
        while (reader.Read())
        {
            var teamKey = reader.GetString(0);
            var rawCombo = reader.GetString(1);
            var teamMembers = reader.GetFieldValue<string[]>(12);
            var memberAccountIds = reader.GetFieldValue<string[]>(13);
            var memberInstrumentIds = reader.GetFieldValue<int[]>(14);
            var memberScores = reader.GetFieldValue<int[]>(15);
            var memberAccuracies = reader.GetFieldValue<int[]>(16);
            var memberFullComboValues = reader.GetFieldValue<int[]>(17);
            var memberStars = reader.GetFieldValue<int[]>(18);
            var memberDifficulties = reader.GetFieldValue<int[]>(19);
            var entrySeason = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7);
            var members = new List<PlayerBandMemberDto>();

            if (memberAccountIds.Length > 0)
            {
                for (var i = 0; i < memberAccountIds.Length; i++)
                {
                    var instrument = i < memberInstrumentIds.Length
                        ? BandInstrumentMapping.ToLeaderboardType(memberInstrumentIds[i])
                        : null;
                    members.Add(new PlayerBandMemberDto
                    {
                        AccountId = memberAccountIds[i],
                        Instruments = instrument is null ? [] : [instrument],
                        Score = ReadOptionalNonNegative(memberScores, i),
                        Accuracy = ReadOptionalNonNegative(memberAccuracies, i),
                        IsFullCombo = ReadOptionalBool(memberFullComboValues, i),
                        Stars = ReadOptionalNonNegative(memberStars, i),
                        Difficulty = ReadOptionalNonNegative(memberDifficulties, i),
                        Season = entrySeason,
                    });
                }
            }
            else
            {
                members.AddRange(teamMembers.Select(accountId => new PlayerBandMemberDto { AccountId = accountId }));
            }

            entries.Add(new SongBandLeaderboardEntryDto
            {
                BandId = BandIdentity.CreateBandId(bandType, teamKey),
                BandType = bandType,
                TeamKey = teamKey,
                ComboId = string.IsNullOrWhiteSpace(rawCombo) ? null : BandComboIds.FromEpicRawCombo(rawCombo),
                Members = members,
                Score = reader.GetInt32(2),
                Accuracy = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                IsFullCombo = !reader.IsDBNull(4) && reader.GetBoolean(4),
                Stars = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                Difficulty = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                Season = entrySeason ?? 0,
                Rank = reader.GetInt32(8),
                Percentile = reader.IsDBNull(10) ? 0 : reader.GetDouble(10),
                EndTime = reader.IsDBNull(11) ? null : reader.GetString(11),
            });
        }

        return entries;
    }

    private static int? ReadOptionalNonNegative(IReadOnlyList<int> values, int index) =>
        index < values.Count && values[index] >= 0 ? values[index] : null;

    private static bool? ReadOptionalBool(IReadOnlyList<int> values, int index) =>
        index < values.Count && values[index] >= 0 ? values[index] == 1 : null;

    public List<BandComboCatalogEntry> GetBandRankingCombos(string bandType, bool usePublishedSnapshot = false)
    {
        using var conn = _ds.OpenConnection();
        var statsTable = ResolveBandRankingStatsReadTable(conn, bandType, usePublishedSnapshot);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT combo_id, total_teams
            FROM {BandRankingStorageNames.QuoteIdentifier(statsTable)}
            WHERE band_type = @bandType AND ranking_scope = 'combo'
            ORDER BY total_teams DESC, combo_id ASC";
        cmd.Parameters.AddWithValue("bandType", bandType);

        var combos = new List<BandComboCatalogEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            combos.Add(new BandComboCatalogEntry
            {
                ComboId = reader.GetString(0),
                TeamCount = reader.GetInt32(1),
            });
        }

        return combos;
    }

    // ── API response cache ───────────────────────────────────────────

    public PublicationCacheLookup GetCurrentCacheLookup(
        string cacheKey)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT publication.current_publication_id,
                   publication.published_scrape_id,
                   publication.published_at,
                   cache.json_data,
                   cache.etag
            FROM scrape_publication_state publication
            LEFT JOIN publication_api_response_cache cache
              ON cache.publication_id =
                    publication.current_publication_id
             AND cache.cache_key = @key
            WHERE publication.id = TRUE
              AND publication.current_publication_id IS NOT NULL
              AND publication.published_scrape_id IS NOT NULL
            """;
        cmd.Parameters.AddWithValue("key", cacheKey);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return new PublicationCacheLookup(false, null);

        var cachedResponse = reader.IsDBNull(3)
            ? null
            : new PublicationCachedResponse(
                reader.GetInt64(0),
                Convert.ToInt64(reader.GetValue(1)),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                (byte[])reader[3],
                reader.GetString(4));
        return new PublicationCacheLookup(true, cachedResponse);
    }

    public PublicationCachedResponse? GetCurrentCachedResponse(
        string cacheKey) =>
        GetCurrentCacheLookup(cacheKey).CachedResponse;

    public (byte[] Json, string ETag)? GetCachedResponse(string cacheKey)
    {
        var lookup = GetCurrentCacheLookup(cacheKey);
        if (lookup.CachedResponse is not null)
        {
            return (
                lookup.CachedResponse.Json,
                lookup.CachedResponse.ETag);
        }
        if (lookup.HasCurrentPublication)
            return null;

        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT legacy.json_data, legacy.etag
            FROM api_response_cache legacy
            WHERE legacy.cache_key = @key
              AND NOT EXISTS (
                  SELECT 1
                  FROM scrape_publication_state publication
                  WHERE publication.id = TRUE
                    AND publication.current_publication_id IS NOT NULL
              )
            """;
        cmd.Parameters.AddWithValue("key", cacheKey);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? ((byte[])reader[0], reader.GetString(1))
            : null;
    }

    public (byte[] Json, string ETag)? GetCachedResponse(
        long publicationId,
        string cacheKey)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT json_data, etag
            FROM publication_api_response_cache
            WHERE publication_id = @publicationId
              AND cache_key = @key
            """;
        cmd.Parameters.AddWithValue("publicationId", publicationId);
        cmd.Parameters.AddWithValue("key", cacheKey);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? ((byte[])reader[0], reader.GetString(1))
            : null;
    }

    public IDisposable AcquirePublicationCacheBuildLease(
        long publicationId,
        bool requireCurrentPublication)
    {
        var conn = _ds.OpenConnection();
        var globalLockAcquired = false;
        var buildLockAcquired = false;
        try
        {
            EnsureMaxScoreProtectedCacheBuildCanStart(
                conn,
                publicationId);
            using (var globalLock = conn.CreateCommand())
            {
                globalLock.CommandText =
                    "SELECT pg_advisory_lock_shared(@lockKey)";
                globalLock.Parameters.AddWithValue(
                    "lockKey",
                    PublicationGenerationSchema.AdvisoryLockKey);
                globalLock.ExecuteNonQuery();
                globalLockAcquired = true;
            }

            using (var buildLock = conn.CreateCommand())
            {
                buildLock.CommandText =
                    "SELECT pg_advisory_lock(@lockKey)";
                buildLock.Parameters.AddWithValue(
                    "lockKey",
                    PublicationGenerationSchema.CacheBuildAdvisoryLockBase
                    + publicationId);
                buildLock.ExecuteNonQuery();
                buildLockAcquired = true;
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                WITH publication AS (
                    SELECT current_publication_id,
                           working_publication_id,
                           published_scrape_id,
                           COALESCE(
                               public_reads_frozen,
                               FALSE) AS public_reads_frozen,
                           public_reads_frozen_reason
                    FROM scrape_publication_state
                    WHERE id = TRUE
                )
                SELECT publication.current_publication_id,
                       publication.working_publication_id,
                       EXISTS (
                           SELECT 1
                           FROM scrape_log scrape
                           WHERE scrape.id > COALESCE(
                               publication.published_scrape_id,
                               0)
                             AND scrape.status = 'failed'
                             AND scrape.failure_phase = ANY(@failurePhases)
                       ) AS failed_candidate_isolation,
                       EXISTS (
                           SELECT 1
                           FROM max_score_maintenance_runs run
                           WHERE publication.public_reads_frozen
                             AND publication.current_publication_id =
                                 @publicationId
                             AND publication.published_scrape_id =
                                 run.expected_published_scrape_id
                             AND publication.public_reads_frozen_reason =
                                 run.freeze_reason
                             AND run.expected_publication_id =
                                 @publicationId
                             AND run.phase NOT IN (
                                 'completed',
                                 'rolled_back')
                             AND run.status IN ('running', 'failed')
                       ) AS protected_max_score_cache
                FROM publication
                """;
            cmd.Parameters.AddWithValue(
                "failurePhases",
                NpgsqlDbType.Array | NpgsqlDbType.Text,
                FailedCandidateReadIsolationFailurePhases);
            cmd.Parameters.AddWithValue(
                "publicationId",
                publicationId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                if (publicationId == 0)
                    return new PublicationCacheBuildLease(
                        conn,
                        publicationId);

                throw new InvalidOperationException(
                    "Publication cache build requires publication state.");
            }

            var currentPublicationId =
                reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0);
            var workingPublicationId =
                reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            var failedCandidateIsolation = reader.GetBoolean(2);
            var protectedMaxScoreCache = reader.GetBoolean(3);
            var expectedPublicationId = workingPublicationId
                ?? currentPublicationId;

            if (protectedMaxScoreCache)
            {
                throw new InvalidOperationException(
                    $"Publication {publicationId} cache build is blocked by max-score maintenance cache evidence for the same generation.");
            }

            if (publicationId == 0)
            {
                if (currentPublicationId.HasValue
                    || workingPublicationId.HasValue)
                {
                    throw new InvalidOperationException(
                        $"Legacy cache build cannot start while current={currentPublicationId?.ToString() ?? "null"} and working={workingPublicationId?.ToString() ?? "null"}.");
                }
                if (requireCurrentPublication && failedCandidateIsolation)
                {
                    throw new InvalidOperationException(
                        "Legacy cache build is blocked by failed-candidate read isolation.");
                }

                return new PublicationCacheBuildLease(
                    conn,
                    publicationId);
            }

            if (requireCurrentPublication)
            {
                if (currentPublicationId != publicationId
                    || workingPublicationId.HasValue)
                {
                    throw new InvalidOperationException(
                        $"Current publication cache build {publicationId} cannot start while current={currentPublicationId?.ToString() ?? "null"} and working={workingPublicationId?.ToString() ?? "null"}.");
                }
                if (failedCandidateIsolation)
                {
                    throw new InvalidOperationException(
                        "Current publication cache build is blocked by failed-candidate read isolation.");
                }
            }
            else if (expectedPublicationId != publicationId)
            {
                throw new InvalidOperationException(
                    $"Publication cache build {publicationId} does not own the current/working target {expectedPublicationId?.ToString() ?? "null"}.");
            }

            return new PublicationCacheBuildLease(
                conn,
                publicationId);
        }
        catch
        {
            ReleasePublicationCacheBuildLocks(
                conn,
                publicationId,
                globalLockAcquired,
                buildLockAcquired);
            conn.Dispose();
            throw;
        }
    }

    public IDisposable AcquireCurrentPublicationMaintenanceLease(
        long publicationId)
    {
        var conn = _ds.OpenConnection();
        NpgsqlTransaction? tx = null;
        try
        {
            tx = conn.BeginTransaction();
            using (var timeouts = conn.CreateCommand())
            {
                timeouts.Transaction = tx;
                timeouts.CommandText = """
                    SET LOCAL lock_timeout = '5s';
                    SET LOCAL statement_timeout = '30s';
                    SET LOCAL idle_in_transaction_session_timeout = 0;
                    """;
                timeouts.ExecuteNonQuery();
            }
            using (var advisoryLock = conn.CreateCommand())
            {
                advisoryLock.Transaction = tx;
                advisoryLock.CommandTimeout = 5;
                advisoryLock.CommandText =
                    "SELECT pg_try_advisory_xact_lock(@lockKey)";
                advisoryLock.Parameters.AddWithValue(
                    "lockKey",
                    PublicationGenerationSchema.AdvisoryLockKey);
                if (advisoryLock.ExecuteScalar() is not true)
                {
                    throw new InvalidOperationException(
                        "Current-publication maintenance requires the global publication lock.");
                }
            }
            using (var sourceLocks = conn.CreateCommand())
            {
                sourceLocks.Transaction = tx;
                sourceLocks.CommandText =
                    MaxScoreMaintenanceSourceLockSql;
                sourceLocks.ExecuteNonQuery();
            }
            using (var publicationRowLock = conn.CreateCommand())
            {
                publicationRowLock.Transaction = tx;
                publicationRowLock.CommandText = """
                    SELECT id
                    FROM scrape_publication_state
                    WHERE id = TRUE
                    FOR UPDATE
                    """;
                if (publicationRowLock.ExecuteScalar() is not true)
                {
                    throw new InvalidOperationException(
                        "Current-publication maintenance requires publication state.");
                }
            }

            using var state = conn.CreateCommand();
            state.Transaction = tx;
            state.CommandText = """
                WITH publication AS (
                    SELECT current_publication_id,
                           working_publication_id,
                           published_scrape_id,
                           COALESCE(public_reads_frozen, FALSE) AS public_reads_frozen
                    FROM scrape_publication_state
                    WHERE id = TRUE
                ), worker AS (
                    SELECT status, last_heartbeat_at
                    FROM service_worker_status
                    WHERE worker_key = @workerKey
                )
                SELECT publication.current_publication_id,
                       publication.working_publication_id,
                       publication.public_reads_frozen,
                       EXISTS (
                           SELECT 1
                           FROM scrape_log scrape
                           WHERE scrape.status = 'running'
                       ) AS running_scrape,
                       COALESCE((SELECT status FROM worker), 'offline'),
                       (SELECT last_heartbeat_at FROM worker),
                       EXISTS (
                           SELECT 1
                           FROM scrape_log scrape
                           WHERE scrape.id > COALESCE(
                               publication.published_scrape_id,
                               0)
                             AND scrape.status = 'failed'
                             AND scrape.failure_phase = ANY(@failurePhases)
                       ) AS failed_candidate_isolation
                FROM publication
                """;
            state.Parameters.AddWithValue(
                "workerKey",
                WorkerStatusPublisher.ScraperWorkerKey);
            state.Parameters.AddWithValue(
                "failurePhases",
                NpgsqlDbType.Array | NpgsqlDbType.Text,
                FailedCandidateReadIsolationFailurePhases);
            using var reader = state.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException(
                    "Current-publication maintenance requires publication state.");
            }

            var currentPublicationId =
                reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0);
            var workingPublicationId =
                reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            var frozen = reader.GetBoolean(2);
            var runningScrape = reader.GetBoolean(3);
            var workerStatus = reader.GetString(4);
            var workerHeartbeat =
                reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);
            var failedCandidateIsolation = reader.GetBoolean(6);
            var workerOffline =
                workerStatus.Equals("offline", StringComparison.OrdinalIgnoreCase)
                || workerStatus.Equals("stale", StringComparison.OrdinalIgnoreCase)
                || workerHeartbeat is { } heartbeat
                   && DateTime.UtcNow - heartbeat > TimeSpan.FromSeconds(90);

            if (currentPublicationId != publicationId
                || workingPublicationId.HasValue
                || frozen
                || runningScrape
                || !workerOffline
                || failedCandidateIsolation)
            {
                throw new InvalidOperationException(
                    $"Current-publication maintenance gate failed: requested={publicationId}, current={currentPublicationId?.ToString() ?? "null"}, working={workingPublicationId?.ToString() ?? "null"}, frozen={frozen}, runningScrape={runningScrape}, workerStatus={workerStatus}, workerOffline={workerOffline}, failedIsolation={failedCandidateIsolation}.");
            }

            return new CurrentPublicationMaintenanceLease(
                conn,
                tx,
                publicationId);
        }
        catch
        {
            tx?.Rollback();
            tx?.Dispose();
            conn.Dispose();
            throw;
        }
    }

    public async Task<IMaxScoreMaintenanceLease>
        AcquireMaxScoreMaintenanceLeaseAsync(
            long publicationId,
            CancellationToken ct = default)
        => await AcquireMaxScoreMaintenanceLeaseCoreAsync(
            publicationId,
            applicationName: "fst-max-score-maintenance",
            retainPublicationLock: true,
            ct);

    public async Task<IMaxScoreMaintenanceLease>
        AcquireMaxScoreMaintenanceRollbackLeaseAsync(
            long publicationId,
            CancellationToken ct = default)
        => await AcquireMaxScoreMaintenanceLeaseCoreAsync(
            publicationId,
            applicationName: "fst-max-score-rollback",
            retainPublicationLock: false,
            ct);

    public async Task<IMaxScoreMaintenanceLease>
        AcquireMaxScoreMaintenanceResumeLeaseAsync(
            long publicationId,
            CancellationToken ct = default)
        => await AcquireMaxScoreMaintenanceLeaseCoreAsync(
            publicationId,
            applicationName: "fst-max-score-resume",
            retainPublicationLock: false,
            ct);

    private async Task<IMaxScoreMaintenanceLease>
        AcquireMaxScoreMaintenanceLeaseCoreAsync(
            long publicationId,
            string applicationName,
            bool retainPublicationLock,
            CancellationToken ct)
    {
        if (publicationId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(publicationId),
                "Max-score maintenance requires a positive publication ID.");
        }

        ct.ThrowIfCancellationRequested();
        NpgsqlConnection? conn = null;
        var mutationGateLockAcquired = false;
        var durableMutationGateClaimed = false;
        var pathLockAcquired = false;
        var publicationLockAcquired = false;
        var leaseToken = CreateLeaseToken();
        var backendProcessId = 0;
        try
        {
            conn = _unpooledConnections.CreateConnection();
            await conn.OpenAsync(ct);
            await using (var identity = conn.CreateCommand())
            {
                identity.CommandTimeout = 5;
                identity.CommandText = """
                    SELECT
                        set_config(
                            'application_name',
                            @applicationName,
                            FALSE),
                        set_config(
                            'fst.max_score_maintenance_lease_token',
                            @leaseToken,
                            FALSE),
                        pg_backend_pid()
                    """;
                identity.Parameters.AddWithValue(
                    "applicationName",
                    applicationName);
                identity.Parameters.AddWithValue(
                    "leaseToken",
                    leaseToken);
                await using var reader =
                    await identity.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                    throw new MaxScoreMaintenanceLeaseLostException();
                backendProcessId = reader.GetInt32(2);
            }
            await using (var mutationGate =
                         conn.CreateCommand())
            {
                mutationGate.CommandTimeout = 0;
                mutationGate.CommandText =
                    "SELECT pg_advisory_lock(@lockKey)";
                mutationGate.Parameters.AddWithValue(
                    "lockKey",
                    RegistrationMutationGate.AdvisoryLockKey);
                await mutationGate.ExecuteScalarAsync(ct);
                mutationGateLockAcquired = true;
            }
            await ClaimMaxScoreMutationGateAsync(
                conn,
                publicationId,
                leaseToken,
                backendProcessId,
                ct);
            durableMutationGateClaimed = true;
            await using (var pathLock = conn.CreateCommand())
            {
                pathLock.CommandTimeout = 5;
                pathLock.CommandText =
                    "SELECT pg_try_advisory_lock(@lockKey)";
                pathLock.Parameters.AddWithValue(
                    "lockKey",
                    PathGenerationAdmissionLock.AdvisoryLockKey);
                pathLockAcquired =
                    await pathLock.ExecuteScalarAsync(ct)
                        is true;
                if (!pathLockAcquired)
                {
                    throw new InvalidOperationException(
                        "Max-score maintenance is blocked by active path generation.");
                }
            }
            await using (var publicationLock =
                             conn.CreateCommand())
            {
                publicationLock.CommandTimeout = 5;
                publicationLock.CommandText =
                    "SELECT pg_try_advisory_lock(@lockKey)";
                publicationLock.Parameters.AddWithValue(
                    "lockKey",
                    PublicationGenerationSchema.AdvisoryLockKey);
                publicationLockAcquired =
                    await publicationLock.ExecuteScalarAsync(ct)
                        is true;
                if (!publicationLockAcquired)
                {
                    throw new InvalidOperationException(
                        "Max-score maintenance is blocked by publication or another maintenance operation.");
                }
            }
            if (!retainPublicationLock)
            {
                // Rollback keeps the durable freeze and mutation fences, but
                // yields this global lock between atomic commit boundaries so
                // cached public reads do not queue behind long reconciliation.
                await using var releasePublicationLock =
                    conn.CreateCommand();
                releasePublicationLock.CommandTimeout = 5;
                releasePublicationLock.CommandText =
                    "SELECT pg_advisory_unlock(@lockKey)";
                releasePublicationLock.Parameters.AddWithValue(
                    "lockKey",
                    PublicationGenerationSchema.AdvisoryLockKey);
                if (await releasePublicationLock.ExecuteScalarAsync(ct)
                    is not true)
                {
                    throw new MaxScoreMaintenanceLeaseLostException();
                }
                publicationLockAcquired = false;
            }

            return new MaxScoreMaintenanceLease(
                this,
                conn,
                publicationId,
                leaseToken,
                backendProcessId,
                retainPublicationLock);
        }
        catch
        {
            if (conn is not null)
            {
                try
                {
                    ReleaseMaxScoreMaintenanceLocks(
                        conn,
                        mutationGateLockAcquired,
                        pathLockAcquired,
                        publicationLockAcquired);
                    if (durableMutationGateClaimed)
                    {
                        await ClearMaxScoreMutationGateAsync(
                            conn,
                            leaseToken,
                            CancellationToken.None);
                    }
                }
                catch
                {
                }
                finally
                {
                    await conn.DisposeAsync();
                }
            }
            throw;
        }
    }

    public void BulkSetCachedResponses(
        IEnumerable<(string Key, byte[] Json, string ETag)> entries,
        long? publicationId = null)
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var tx = conn.BeginTransaction();
        if (publicationId.HasValue
            && !IsPublicationCacheBuildLockHeld(publicationId)
            && !IsPublicationMaintenanceLockHeld(publicationId))
        {
            throw new InvalidOperationException(
                $"Publication {publicationId.Value} cache mutation requires its build or maintenance lease.");
        }
        if (!publicationId.HasValue)
            AcquirePublicationCacheMutationLock(conn, tx);
        publicationId = publicationId.HasValue
            ? ResolveCacheTargetPublicationId(conn, tx, publicationId)
            : ReadCacheTargetPublicationId(conn, tx);

        using var legacy = conn.CreateCommand();
        legacy.Transaction = tx;
        legacy.CommandText = """
            INSERT INTO api_response_cache (cache_key, json_data, etag, cached_at)
            VALUES (@key, @json, @etag, now())
            ON CONFLICT (cache_key) DO UPDATE SET json_data = EXCLUDED.json_data, etag = EXCLUDED.etag, cached_at = now()
            """;
        legacy.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text));
        legacy.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Bytea));
        legacy.Parameters.Add(new NpgsqlParameter("etag", NpgsqlDbType.Text));
        legacy.Prepare();

        using var generation = conn.CreateCommand();
        generation.Transaction = tx;
        generation.CommandText = """
            INSERT INTO publication_api_response_cache (
                publication_id, cache_key, json_data, etag, cached_at)
            VALUES (@publicationId, @key, @json, @etag, now())
            ON CONFLICT (publication_id, cache_key) DO UPDATE SET
                json_data = EXCLUDED.json_data,
                etag = EXCLUDED.etag,
                cached_at = now()
            """;
        generation.Parameters.Add(new NpgsqlParameter("publicationId", NpgsqlDbType.Bigint));
        generation.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text));
        generation.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Bytea));
        generation.Parameters.Add(new NpgsqlParameter("etag", NpgsqlDbType.Text));
        if (publicationId.HasValue)
            generation.Prepare();

        foreach (var (key, json, etag) in entries)
        {
            legacy.Parameters["key"].Value = key;
            legacy.Parameters["json"].Value = json;
            legacy.Parameters["etag"].Value = etag;
            legacy.ExecuteNonQuery();

            if (publicationId.HasValue)
            {
                generation.Parameters["publicationId"].Value = publicationId.Value;
                generation.Parameters["key"].Value = key;
                generation.Parameters["json"].Value = json;
                generation.Parameters["etag"].Value = etag;
                generation.ExecuteNonQuery();
            }
        }

        if (publicationId.HasValue)
        {
            using var binding = conn.CreateCommand();
            binding.Transaction = tx;
            binding.CommandText = """
                INSERT INTO publication_surface_bindings (
                    publication_id, surface_name, binding_kind, binding_json,
                    row_count, content_hash, status, built_at)
                VALUES (
                    @publicationId,
                    'api_response_cache',
                    'generation_cache_table',
                    COALESCE((
                        SELECT binding_json
                        FROM publication_surface_bindings
                        WHERE publication_id = @publicationId
                          AND surface_name = 'api_response_cache'
                    ), '{}'::jsonb)
                    || jsonb_build_object(
                           'table', 'publication_api_response_cache',
                           'publicationId', @publicationId),
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
                    now())
                ON CONFLICT (publication_id, surface_name) DO UPDATE SET
                    binding_kind = EXCLUDED.binding_kind,
                    binding_json = EXCLUDED.binding_json,
                    row_count = EXCLUDED.row_count,
                    content_hash = EXCLUDED.content_hash,
                    status = EXCLUDED.status,
                    built_at = EXCLUDED.built_at
                """;
            binding.Parameters.AddWithValue(
                "publicationId",
                publicationId.Value);
            binding.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void ClearCachedResponses()
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var tx = conn.BeginTransaction();
        AcquirePublicationCacheMutationLock(conn, tx);
        var publicationId = ReadCacheTargetPublicationId(conn, tx);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = publicationId.HasValue
            ? """
              TRUNCATE api_response_cache;
              DELETE FROM publication_api_response_cache
              WHERE publication_id = @publicationId;
              """
            : "TRUNCATE api_response_cache";
        if (publicationId.HasValue)
            cmd.Parameters.AddWithValue("publicationId", publicationId.Value);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public void BulkSetCachedResponsesStaging(
        IEnumerable<(string Key, byte[] Json, string ETag)> entries,
        long? publicationId = null)
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var tx = conn.BeginTransaction();
        if (!IsPublicationCacheBuildLockHeld(publicationId)
            && !IsPublicationMaintenanceLockHeld(publicationId))
            AcquirePublicationCacheMutationLock(conn, tx);
        BulkSetCachedResponsesStaging(
            entries,
            publicationId,
            conn,
            tx);
        tx.Commit();
    }

    public void BulkSetCachedResponsesStaging(
        IEnumerable<(string Key, byte[] Json, string ETag)> entries,
        long? publicationId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The cache staging transaction must belong to the supplied connection.",
                nameof(transaction));
        }
        publicationId = ResolveCacheTargetPublicationId(
            connection,
            transaction,
            publicationId);
        EnsureMaxScoreProtectedCacheStagingIsMutable(
            connection,
            transaction,
            publicationId);

        // Start with a clean staging table
        using (var trunc = connection.CreateCommand())
        {
            trunc.Transaction = transaction;
            trunc.CommandText = publicationId.HasValue
                ? """
                  TRUNCATE api_response_cache_staging;
                  DELETE FROM publication_api_response_cache_staging
                  WHERE publication_id = @publicationId;
                  """
                : "TRUNCATE api_response_cache_staging";
            if (publicationId.HasValue)
                trunc.Parameters.AddWithValue("publicationId", publicationId.Value);
            trunc.ExecuteNonQuery();
        }

        using var legacy = connection.CreateCommand();
        legacy.Transaction = transaction;
        legacy.CommandText = """
            INSERT INTO api_response_cache_staging (cache_key, json_data, etag, cached_at)
            VALUES (@key, @json, @etag, now())
            ON CONFLICT (cache_key) DO UPDATE SET json_data = EXCLUDED.json_data, etag = EXCLUDED.etag, cached_at = now()
            """;
        legacy.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text));
        legacy.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Bytea));
        legacy.Parameters.Add(new NpgsqlParameter("etag", NpgsqlDbType.Text));
        legacy.Prepare();

        using var generation = connection.CreateCommand();
        generation.Transaction = transaction;
        generation.CommandText = """
            INSERT INTO publication_api_response_cache_staging (
                publication_id, cache_key, json_data, etag, cached_at)
            VALUES (@publicationId, @key, @json, @etag, now())
            ON CONFLICT (publication_id, cache_key) DO UPDATE SET
                json_data = EXCLUDED.json_data,
                etag = EXCLUDED.etag,
                cached_at = now()
            """;
        generation.Parameters.Add(new NpgsqlParameter("publicationId", NpgsqlDbType.Bigint));
        generation.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text));
        generation.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Bytea));
        generation.Parameters.Add(new NpgsqlParameter("etag", NpgsqlDbType.Text));
        if (publicationId.HasValue)
            generation.Prepare();

        foreach (var (key, json, etag) in entries)
        {
            legacy.Parameters["key"].Value = key;
            legacy.Parameters["json"].Value = json;
            legacy.Parameters["etag"].Value = etag;
            legacy.ExecuteNonQuery();

            if (publicationId.HasValue)
            {
                generation.Parameters["publicationId"].Value = publicationId.Value;
                generation.Parameters["key"].Value = key;
                generation.Parameters["json"].Value = json;
                generation.Parameters["etag"].Value = etag;
                generation.ExecuteNonQuery();
            }
        }
    }

    public void SwapCachedResponsesFromStaging(long? publicationId = null)
    {
        using var conn = _ds.OpenConnection();
        EnsureScrapePublicationStateTable(conn);
        using var tx = conn.BeginTransaction();
        if (!IsPublicationCacheBuildLockHeld(publicationId)
            && !IsPublicationMaintenanceLockHeld(publicationId))
            AcquirePublicationCacheMutationLock(conn, tx);
        publicationId = ResolveCacheTargetPublicationId(
            conn,
            tx,
            publicationId);
        EnsureMaxScoreProtectedCacheStagingIsMutable(
            conn,
            tx,
            publicationId);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = publicationId.HasValue
            ? """
              TRUNCATE api_response_cache;
              INSERT INTO api_response_cache (cache_key, json_data, etag, cached_at)
              SELECT cache_key, json_data, etag, cached_at FROM api_response_cache_staging;
              TRUNCATE api_response_cache_staging;

              DELETE FROM publication_api_response_cache
              WHERE publication_id = @publicationId;
              INSERT INTO publication_api_response_cache (
                  publication_id, cache_key, json_data, etag, cached_at)
              SELECT publication_id, cache_key, json_data, etag, cached_at
              FROM publication_api_response_cache_staging
              WHERE publication_id = @publicationId;
              DELETE FROM publication_api_response_cache_staging
              WHERE publication_id = @publicationId;

              INSERT INTO publication_surface_bindings (
                  publication_id, surface_name, binding_kind, binding_json,
                  row_count, content_hash, status, built_at)
              VALUES (
                  @publicationId,
                  'api_response_cache',
                  'generation_cache_table',
                  jsonb_build_object(
                      'table', 'publication_api_response_cache',
                      'publicationId', @publicationId),
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
                  now())
              ON CONFLICT (publication_id, surface_name) DO UPDATE SET
                  binding_kind = EXCLUDED.binding_kind,
                  binding_json = EXCLUDED.binding_json,
                  row_count = EXCLUDED.row_count,
                  content_hash = EXCLUDED.content_hash,
                  status = EXCLUDED.status,
                  built_at = EXCLUDED.built_at;
              """
            : """
            TRUNCATE api_response_cache;
            INSERT INTO api_response_cache (cache_key, json_data, etag, cached_at)
            SELECT cache_key, json_data, etag, cached_at FROM api_response_cache_staging;
            TRUNCATE api_response_cache_staging;
            """;
        if (publicationId.HasValue)
            cmd.Parameters.AddWithValue("publicationId", publicationId.Value);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private void CompleteMaxScoreMaintenance(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long publicationId,
        long publishedScrapeId,
        string manifestSha256,
        string leaseToken)
        => CompleteMaxScoreMaintenanceCore(
            conn,
            tx,
            publicationId,
            publishedScrapeId,
            manifestSha256,
            leaseToken,
            rollback: false);

    private void CompleteMaxScoreMaintenanceRollback(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long publicationId,
        long publishedScrapeId,
        string manifestSha256,
        string leaseToken)
        => CompleteMaxScoreMaintenanceCore(
            conn,
            tx,
            publicationId,
            publishedScrapeId,
            manifestSha256,
            leaseToken,
            rollback: true);

    private void CompleteMaxScoreMaintenanceCore(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long publicationId,
        long publishedScrapeId,
        string manifestSha256,
        string leaseToken,
        bool rollback)
    {
        var normalizedDigest =
            MaxScoreMaintenanceManifest.NormalizeSha256(
                manifestSha256,
                nameof(manifestSha256));
        if (publicationId <= 0 || publishedScrapeId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(publicationId),
                "Maintenance publication and scrape IDs must be positive.");
        }

        var expectedFreezeReason =
            PublicReadFreezeState.MaxScoreMaintenanceReasonPrefix
            + normalizedDigest;
        using (var timeouts = conn.CreateCommand())
        {
            timeouts.Transaction = tx;
            timeouts.CommandText = """
                SET LOCAL lock_timeout = '5s';
                SET LOCAL statement_timeout = '120s';
                """;
            timeouts.ExecuteNonQuery();
        }
        using (var state = conn.CreateCommand())
        {
            state.Transaction = tx;
            state.CommandText = """
                SELECT current_publication_id,
                       working_publication_id,
                       published_scrape_id,
                       public_reads_frozen,
                       public_reads_frozen_scrape_id,
                       public_reads_frozen_reason,
                       max_score_mutation_gate_token,
                       max_score_mutation_gate_publication_id
                FROM scrape_publication_state
                WHERE id = TRUE
                FOR UPDATE
                """;
            using var reader = state.ExecuteReader();
            if (!reader.Read()
                || reader.IsDBNull(0)
                || reader.GetInt64(0) != publicationId
                || !reader.IsDBNull(1)
                || reader.IsDBNull(2)
                || reader.GetInt64(2) != publishedScrapeId
                || !reader.GetBoolean(3)
                || reader.IsDBNull(4)
                || reader.GetInt64(4) != publishedScrapeId
                || reader.IsDBNull(5)
                || !string.Equals(
                    reader.GetString(5),
                    expectedFreezeReason,
                    StringComparison.Ordinal)
                || reader.IsDBNull(6)
                || !string.Equals(
                    reader.GetString(6),
                    leaseToken,
                    StringComparison.Ordinal)
                || reader.IsDBNull(7)
                || reader.GetInt64(7) != publicationId)
            {
                throw new InvalidOperationException(
                    "Max-score maintenance completion lost its exact publication or freeze identity.");
            }
        }

        using (var cacheLocks = conn.CreateCommand())
        {
            cacheLocks.Transaction = tx;
            cacheLocks.CommandText = """
                LOCK TABLE api_response_cache_staging
                    IN SHARE MODE;
                LOCK TABLE publication_api_response_cache_staging
                    IN SHARE MODE;
                """;
            cacheLocks.ExecuteNonQuery();
        }
        long stagedCacheEntryCount;
        using (var run = conn.CreateCommand())
        {
            run.Transaction = tx;
            run.CommandText = """
                SELECT CASE
                           WHEN @rollback
                               THEN rollback_staged_cache_entry_count
                           ELSE staged_cache_entry_count
                       END
                FROM max_score_maintenance_runs
                WHERE manifest_sha256 = @manifestSha256
                  AND expected_publication_id = @publicationId
                  AND expected_published_scrape_id =
                      @publishedScrapeId
                  AND phase = @validatedPhase
                  AND status IN ('running', 'failed')
                  AND CASE
                          WHEN @rollback
                              THEN rollback_cache_evidence IS NOT NULL
                          ELSE staged_cache_evidence IS NOT NULL
                      END
                FOR UPDATE
                """;
            run.Parameters.AddWithValue(
                "manifestSha256",
                normalizedDigest);
            run.Parameters.AddWithValue(
                "publicationId",
                publicationId);
            run.Parameters.AddWithValue(
                "publishedScrapeId",
                publishedScrapeId);
            run.Parameters.AddWithValue(
                "rollback",
                rollback);
            run.Parameters.AddWithValue(
                "validatedPhase",
                rollback
                    ? "rollback_validated"
                    : "validated");
            var value = run.ExecuteScalar();
            if (value is null or DBNull)
            {
                throw new InvalidOperationException(
                    "Validated max-score cache evidence is missing from the durable run.");
            }
            stagedCacheEntryCount = Convert.ToInt64(value);
        }
        ConfigureMaxScoreMaintenanceCompletionStatementTimeout(
            conn,
            tx,
            _maxScoreMaintenanceCommandTimeoutSeconds,
            "final-cache-validation");
        if (rollback)
        {
            MaxScoreMaintenanceCacheEntryEvidenceStore
                .ValidateRollback(
                    normalizedDigest,
                    publicationId,
                    stagedCacheEntryCount,
                    conn,
                    tx,
                    _maxScoreMaintenanceCommandTimeoutSeconds);
        }
        else
        {
            MaxScoreMaintenanceCacheEntryEvidenceStore.Validate(
                normalizedDigest,
                publicationId,
                stagedCacheEntryCount,
                conn,
                tx,
                _maxScoreMaintenanceCommandTimeoutSeconds);
        }
        ConfigureMaxScoreMaintenanceCompletionStatementTimeout(
            conn,
            tx,
            MaxScoreMaintenanceFinalMutationStatementTimeoutSeconds,
            "final-bounded-mutations");

        using (var swap = conn.CreateCommand())
        {
            swap.Transaction = tx;
            swap.CommandText = """
                TRUNCATE api_response_cache;
                INSERT INTO api_response_cache (
                    cache_key, json_data, etag, cached_at)
                SELECT cache_key, json_data, etag, cached_at
                FROM api_response_cache_staging;
                TRUNCATE api_response_cache_staging;

                DELETE FROM publication_api_response_cache
                WHERE publication_id = @publicationId;
                INSERT INTO publication_api_response_cache (
                    publication_id, cache_key, json_data, etag, cached_at)
                SELECT publication_id, cache_key, json_data, etag, cached_at
                FROM publication_api_response_cache_staging
                WHERE publication_id = @publicationId;
                DELETE FROM publication_api_response_cache_staging
                WHERE publication_id = @publicationId;

                INSERT INTO publication_surface_bindings (
                    publication_id, surface_name, binding_kind, binding_json,
                    row_count, content_hash, status, built_at)
                VALUES (
                    @publicationId,
                    'api_response_cache',
                    'generation_cache_table',
                    jsonb_build_object(
                        'table', 'publication_api_response_cache',
                        'publicationId', @publicationId),
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
                    now())
                ON CONFLICT (publication_id, surface_name) DO UPDATE SET
                    binding_kind = EXCLUDED.binding_kind,
                    binding_json = EXCLUDED.binding_json,
                    row_count = EXCLUDED.row_count,
                    content_hash = EXCLUDED.content_hash,
                    status = EXCLUDED.status,
                    built_at = EXCLUDED.built_at;

                INSERT INTO publication_surface_bindings (
                    publication_id, surface_name, binding_kind, binding_json,
                    row_count, content_hash, status, built_at)
                VALUES (
                    @publicationId,
                    'path_artifacts',
                    'legacy_live_unversioned',
                    jsonb_build_object(
                        'table', 'songs',
                        'maintenanceManifestSha256',
                            @manifestSha256,
                        'maintenanceRollback',
                            @rollback),
                    (
                        SELECT COUNT(*)
                        FROM songs
                        WHERE paths_generated_at IS NOT NULL
                    ),
                    (
                        SELECT md5(COALESCE(
                            string_agg(
                                song_id || ':'
                                || path_generation_revision || ':'
                                || COALESCE(
                                    path_artifact_generation_id,
                                    ''),
                                '|' ORDER BY song_id),
                            ''))
                        FROM songs
                        WHERE paths_generated_at IS NOT NULL
                    ),
                    'building',
                    now())
                ON CONFLICT (publication_id, surface_name) DO UPDATE SET
                    binding_kind = EXCLUDED.binding_kind,
                    binding_json = EXCLUDED.binding_json,
                    row_count = EXCLUDED.row_count,
                    content_hash = EXCLUDED.content_hash,
                    status = EXCLUDED.status,
                    built_at = EXCLUDED.built_at;

                UPDATE max_score_maintenance_runs
                SET phase = @terminalPhase,
                    status = @terminalStatus,
                    failure_stage = CASE
                        WHEN @rollback
                            THEN failure_stage
                        ELSE NULL
                    END,
                    failure_detail = CASE
                        WHEN @rollback
                            THEN failure_detail
                        ELSE NULL
                    END,
                    rollback_failure_stage = NULL,
                    rollback_failure_detail = NULL,
                    completed_at = now(),
                    rolled_back_at = CASE
                        WHEN @rollback THEN now()
                        ELSE rolled_back_at
                    END,
                    updated_at = now()
                WHERE manifest_sha256 = @manifestSha256
                  AND expected_publication_id = @publicationId
                  AND expected_published_scrape_id = @publishedScrapeId
                  AND phase = @validatedPhase
                  AND status IN ('running', 'failed');
                """;
            swap.Parameters.AddWithValue(
                "publicationId",
                publicationId);
            swap.Parameters.AddWithValue(
                "publishedScrapeId",
                publishedScrapeId);
            swap.Parameters.AddWithValue(
                "manifestSha256",
                normalizedDigest);
            swap.Parameters.AddWithValue(
                "rollback",
                rollback);
            swap.Parameters.AddWithValue(
                "terminalPhase",
                rollback ? "rolled_back" : "completed");
            swap.Parameters.AddWithValue(
                "terminalStatus",
                rollback ? "rolled_back" : "completed");
            swap.Parameters.AddWithValue(
                "validatedPhase",
                rollback
                    ? "rollback_validated"
                    : "validated");
            swap.ExecuteNonQuery();
        }

        using (var unfreeze = conn.CreateCommand())
        {
            unfreeze.Transaction = tx;
            unfreeze.CommandText = """
                UPDATE scrape_publication_state
                SET public_reads_frozen = FALSE,
                    public_reads_frozen_at = NULL,
                    public_reads_frozen_scrape_id = NULL,
                    public_reads_frozen_reason = NULL,
                    updated_at = now()
                WHERE id = TRUE
                  AND current_publication_id = @publicationId
                  AND working_publication_id IS NULL
                  AND published_scrape_id = @publishedScrapeId
                  AND public_reads_frozen
                  AND public_reads_frozen_scrape_id = @publishedScrapeId
                  AND public_reads_frozen_reason = @freezeReason
                  AND max_score_mutation_gate_token = @leaseToken
                  AND max_score_mutation_gate_publication_id =
                      @publicationId
                """;
            unfreeze.Parameters.AddWithValue(
                "publicationId",
                publicationId);
            unfreeze.Parameters.AddWithValue(
                "publishedScrapeId",
                publishedScrapeId);
            unfreeze.Parameters.AddWithValue(
                "freezeReason",
                expectedFreezeReason);
            unfreeze.Parameters.AddWithValue(
                "leaseToken",
                leaseToken);
            if (unfreeze.ExecuteNonQuery() != 1)
            {
                throw new MaxScoreMaintenanceLeaseLostException();
            }
        }

        using (var verify = conn.CreateCommand())
        {
            verify.Transaction = tx;
            verify.CommandText = """
                SELECT
                    (
                        SELECT phase = @terminalPhase
                           AND status = @terminalStatus
                        FROM max_score_maintenance_runs
                        WHERE manifest_sha256 = @manifestSha256
                    ),
                    (
                        SELECT NOT public_reads_frozen
                           AND max_score_mutation_gate_token =
                               @leaseToken
                           AND max_score_mutation_gate_publication_id =
                               @publicationId
                        FROM scrape_publication_state
                        WHERE id = TRUE
                    )
                """;
            verify.Parameters.AddWithValue(
                "manifestSha256",
                normalizedDigest);
            verify.Parameters.AddWithValue(
                "leaseToken",
                leaseToken);
            verify.Parameters.AddWithValue(
                "publicationId",
                publicationId);
            verify.Parameters.AddWithValue(
                "terminalPhase",
                rollback ? "rolled_back" : "completed");
            verify.Parameters.AddWithValue(
                "terminalStatus",
                rollback ? "rolled_back" : "completed");
            using var reader = verify.ExecuteReader();
            if (!reader.Read()
                || reader.IsDBNull(0)
                || !reader.GetBoolean(0)
                || reader.IsDBNull(1)
                || !reader.GetBoolean(1))
            {
                throw new InvalidOperationException(
                    "Atomic max-score cache publication and unfreeze did not complete under the durable mutation gate.");
            }
        }
    }

    private void ConfigureMaxScoreMaintenanceCompletionStatementTimeout(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        int statementTimeoutSeconds,
        string stage)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        MaxScoreMaintenanceCommandTimeout.Configure(
            command,
            statementTimeoutSeconds,
            stage);
        command.CommandText = """
            WITH configured AS MATERIALIZED (
                SELECT set_config(
                    'statement_timeout',
                    @statementTimeout,
                    TRUE) AS statement_timeout
            )
            SELECT
                (
                    EXTRACT(
                        EPOCH FROM
                        configured.statement_timeout::INTERVAL)
                )::INTEGER,
                (
                    EXTRACT(
                        EPOCH FROM
                        current_setting('lock_timeout')::INTERVAL)
                )::INTEGER,
                current_setting('transaction_isolation')
            FROM configured
            """;
        command.Parameters.AddWithValue(
            "statementTimeout",
            $"{statementTimeoutSeconds}s");
        using var reader = command.ExecuteReader();
        if (!reader.Read()
            || reader.GetInt32(0) != statementTimeoutSeconds)
        {
            throw new InvalidOperationException(
                $"PostgreSQL did not apply the {stage} statement timeout.");
        }

        MaxScoreMaintenanceServerTimeoutTestHook?.Invoke(
            new MaxScoreMaintenanceServerTimeoutTestContext(
                stage,
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2)));
    }

    private static long? ReadCacheTargetPublicationId(
        NpgsqlConnection conn,
        NpgsqlTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT COALESCE(working_publication_id, current_publication_id)
            FROM scrape_publication_state
            WHERE id = TRUE
            """;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static void AcquirePublicationCacheMutationLock(
        NpgsqlConnection conn,
        NpgsqlTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT pg_advisory_xact_lock_shared(@lockKey)";
        cmd.Parameters.AddWithValue(
            "lockKey",
            PublicationGenerationSchema.AdvisoryLockKey);
        cmd.ExecuteNonQuery();
    }

    private static bool IsPublicationCacheBuildLockHeld(
        long? publicationId) =>
        PublicationCacheBuildTarget.Value.HasValue
        && PublicationCacheBuildTarget.Value.Value
            == (publicationId ?? 0);

    private static bool IsPublicationMaintenanceLockHeld(
        long? publicationId)
        => CurrentPublicationMaintenanceTarget.Value.HasValue
           && CurrentPublicationMaintenanceTarget.Value.Value
               == (publicationId ?? 0);

    private static void
        EnsureMaxScoreProtectedCacheBuildCanStart(
            NpgsqlConnection connection,
            long publicationId)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM scrape_publication_state publication
                JOIN max_score_maintenance_runs run
                  ON run.freeze_reason =
                        publication.public_reads_frozen_reason
                 AND run.expected_publication_id =
                        publication.current_publication_id
                 AND run.expected_published_scrape_id =
                        publication.published_scrape_id
                WHERE publication.id = TRUE
                  AND publication.current_publication_id =
                        @publicationId
                  AND publication.public_reads_frozen
                  AND run.phase NOT IN (
                      'completed',
                      'rolled_back')
                  AND run.status IN ('running', 'failed')
            )
            """;
        command.Parameters.AddWithValue(
            "publicationId",
            publicationId);
        if (command.ExecuteScalar() is true)
        {
            throw new InvalidOperationException(
                $"Publication {publicationId} cache build is blocked by active max-score maintenance for the same generation.");
        }
    }

    private static void EnsureMaxScoreProtectedCacheStagingIsMutable(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long? publicationId)
    {
        if (!publicationId.HasValue)
            return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM scrape_publication_state publication
                JOIN max_score_maintenance_runs run
                  ON run.freeze_reason =
                     publication.public_reads_frozen_reason
                 AND run.expected_publication_id =
                     publication.current_publication_id
                WHERE publication.id = TRUE
                  AND publication.current_publication_id =
                      @publicationId
                  AND publication.public_reads_frozen
                  AND run.expected_published_scrape_id =
                      publication.published_scrape_id
                  AND run.phase NOT IN (
                      'completed',
                      'rolled_back')
                  AND run.status IN ('running', 'failed')
                  AND (
                      publication.max_score_mutation_gate_token
                          IS NULL
                      OR current_setting(
                              'fst.max_score_maintenance_lease_token',
                              TRUE)
                          IS DISTINCT FROM
                          publication.max_score_mutation_gate_token
                  )
            )
            """;
        command.Parameters.AddWithValue(
            "publicationId",
            publicationId.Value);
        if (command.ExecuteScalar() is true)
        {
            throw new InvalidOperationException(
                $"Publication {publicationId.Value} cache staging is blocked by active max-score maintenance.");
        }
    }

    private static void ReleasePublicationCacheBuildLocks(
        NpgsqlConnection connection,
        long publicationId,
        bool globalLockAcquired,
        bool buildLockAcquired)
    {
        if (!globalLockAcquired && !buildLockAcquired)
            return;

        using var unlock = connection.CreateCommand();
        var statements = new List<string>(2);
        if (buildLockAcquired)
        {
            statements.Add(
                "SELECT pg_advisory_unlock(@buildLockKey)");
            unlock.Parameters.AddWithValue(
                "buildLockKey",
                PublicationGenerationSchema.CacheBuildAdvisoryLockBase
                + publicationId);
        }
        if (globalLockAcquired)
        {
            statements.Add(
                "SELECT pg_advisory_unlock_shared(@publicationLockKey)");
            unlock.Parameters.AddWithValue(
                "publicationLockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
        }
        unlock.CommandText = string.Join(";", statements);
        unlock.ExecuteNonQuery();
    }

    private static async Task ClaimMaxScoreMutationGateAsync(
        NpgsqlConnection connection,
        long publicationId,
        string leaseToken,
        int backendProcessId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = """
            INSERT INTO scrape_publication_state (
                id,
                max_score_mutation_gate_token,
                max_score_mutation_gate_publication_id,
                max_score_mutation_gate_backend_pid,
                max_score_mutation_gate_backend_start,
                max_score_mutation_gate_acquired_at,
                updated_at)
            VALUES (
                TRUE,
                @leaseToken,
                @publicationId,
                @backendProcessId,
                (
                    SELECT backend_start
                    FROM pg_stat_activity
                    WHERE pid = pg_backend_pid()
                ),
                now(),
                now())
            ON CONFLICT (id) DO UPDATE SET
                max_score_mutation_gate_token =
                    EXCLUDED.max_score_mutation_gate_token,
                max_score_mutation_gate_publication_id =
                    EXCLUDED.max_score_mutation_gate_publication_id,
                max_score_mutation_gate_backend_pid =
                    EXCLUDED.max_score_mutation_gate_backend_pid,
                max_score_mutation_gate_backend_start =
                    EXCLUDED.max_score_mutation_gate_backend_start,
                max_score_mutation_gate_acquired_at =
                    EXCLUDED.max_score_mutation_gate_acquired_at,
                updated_at = EXCLUDED.updated_at
            """;
        command.Parameters.AddWithValue(
            "leaseToken",
            leaseToken);
        command.Parameters.AddWithValue(
            "publicationId",
            publicationId);
        command.Parameters.AddWithValue(
            "backendProcessId",
            backendProcessId);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new MaxScoreMaintenanceLeaseLostException();
    }

    private static async Task ClearMaxScoreMutationGateAsync(
        NpgsqlConnection connection,
        string leaseToken,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = """
            UPDATE scrape_publication_state
            SET max_score_mutation_gate_token = NULL,
                max_score_mutation_gate_publication_id = NULL,
                max_score_mutation_gate_backend_pid = NULL,
                max_score_mutation_gate_backend_start = NULL,
                max_score_mutation_gate_acquired_at = NULL,
                updated_at = now()
            WHERE id = TRUE
              AND max_score_mutation_gate_token = @leaseToken
            """;
        command.Parameters.AddWithValue(
            "leaseToken",
            leaseToken);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new MaxScoreMaintenanceLeaseLostException();
    }

    private static void ReleaseMaxScoreMaintenanceLocks(
        NpgsqlConnection connection,
        bool mutationGateLockAcquired,
        bool pathLockAcquired,
        bool publicationLockAcquired)
    {
        if (!mutationGateLockAcquired
            && !pathLockAcquired
            && !publicationLockAcquired)
            return;

        using var unlock = connection.CreateCommand();
        unlock.CommandTimeout = 5;
        var statements = new List<string>(3);
        if (publicationLockAcquired)
        {
            statements.Add(
                "SELECT pg_advisory_unlock(@publicationLockKey)");
            unlock.Parameters.AddWithValue(
                "publicationLockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
        }
        if (pathLockAcquired)
        {
            statements.Add(
                "SELECT pg_advisory_unlock(@pathLockKey)");
            unlock.Parameters.AddWithValue(
                "pathLockKey",
                PathGenerationAdmissionLock.AdvisoryLockKey);
        }
        if (mutationGateLockAcquired)
        {
            statements.Add(
                "SELECT pg_advisory_unlock(@mutationGateLockKey)");
            unlock.Parameters.AddWithValue(
                "mutationGateLockKey",
                RegistrationMutationGate.AdvisoryLockKey);
        }
        unlock.CommandText = string.Join(";", statements);
        unlock.ExecuteNonQuery();
    }

    private static long? ResolveCacheTargetPublicationId(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long? requestedPublicationId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            WITH publication AS (
                SELECT current_publication_id,
                       working_publication_id,
                       published_scrape_id
                FROM scrape_publication_state
                WHERE id = TRUE
            )
            SELECT publication.current_publication_id,
                   publication.working_publication_id,
                   EXISTS (
                       SELECT 1
                       FROM scrape_log scrape
                       WHERE scrape.id > COALESCE(
                           publication.published_scrape_id,
                           0)
                         AND scrape.status = 'failed'
                         AND scrape.failure_phase = ANY(@failurePhases)
                   )
            FROM publication
            """;
        cmd.Parameters.AddWithValue(
            "failurePhases",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            FailedCandidateReadIsolationFailurePhases);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            if (requestedPublicationId == 0)
                return null;

            if (requestedPublicationId.HasValue)
            {
                throw new InvalidOperationException(
                    $"Publication {requestedPublicationId.Value} cannot own cache staging because publication state is missing.");
            }

            return null;
        }

        var currentPublicationId =
            reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0);
        var workingPublicationId =
            reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
        var failedCandidateIsolation = reader.GetBoolean(2);
        if (requestedPublicationId == 0)
        {
            if (currentPublicationId.HasValue
                || workingPublicationId.HasValue)
            {
                throw new InvalidOperationException(
                    $"Legacy cache target is no longer empty (current={currentPublicationId?.ToString() ?? "null"}, working={workingPublicationId?.ToString() ?? "null"}).");
            }

            return null;
        }
        var resolvedPublicationId =
            requestedPublicationId
            ?? workingPublicationId
            ?? currentPublicationId;
        if (requestedPublicationId.HasValue
            && requestedPublicationId == currentPublicationId
            && workingPublicationId.HasValue
            && workingPublicationId != requestedPublicationId)
        {
            throw new InvalidOperationException(
                $"Publication {requestedPublicationId.Value} cannot mutate the cache while working publication {workingPublicationId.Value} exists.");
        }
        if (requestedPublicationId.HasValue
            && requestedPublicationId == currentPublicationId
            && failedCandidateIsolation)
        {
            throw new InvalidOperationException(
                $"Publication {requestedPublicationId.Value} cannot mutate the cache during failed-candidate read isolation.");
        }

        if (resolvedPublicationId.HasValue
            && resolvedPublicationId != currentPublicationId
            && resolvedPublicationId != workingPublicationId)
        {
            throw new InvalidOperationException(
                $"Publication {resolvedPublicationId.Value} is not the current or working cache target.");
        }

        return resolvedPublicationId;
    }

    private sealed class PublicationCacheBuildLease : IDisposable
    {
        private NpgsqlConnection? _connection;
        private readonly long? _previousTarget;
        private readonly long _publicationId;

        public PublicationCacheBuildLease(
            NpgsqlConnection connection,
            long publicationId)
        {
            _connection = connection;
            _publicationId = publicationId;
            _previousTarget = PublicationCacheBuildTarget.Value;
            PublicationCacheBuildTarget.Value = publicationId;
        }

        public void Dispose()
        {
            PublicationCacheBuildTarget.Value = _previousTarget;
            var connection = Interlocked.Exchange(ref _connection, null);
            if (connection is null)
                return;

            try
            {
                ReleasePublicationCacheBuildLocks(
                    connection,
                    _publicationId,
                    globalLockAcquired: true,
                    buildLockAcquired: true);
            }
            finally
            {
                connection.Dispose();
            }
        }
    }

    private sealed class CurrentPublicationMaintenanceLease : IDisposable
    {
        private NpgsqlConnection? _connection;
        private NpgsqlTransaction? _transaction;
        private readonly long? _previousTarget;

        public CurrentPublicationMaintenanceLease(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long publicationId)
        {
            _connection = connection;
            _transaction = transaction;
            _previousTarget =
                CurrentPublicationMaintenanceTarget.Value;
            CurrentPublicationMaintenanceTarget.Value =
                publicationId;
        }

        public void Dispose()
        {
            CurrentPublicationMaintenanceTarget.Value =
                _previousTarget;
            var connection = Interlocked.Exchange(ref _connection, null);
            if (connection is null)
                return;
            var transaction = Interlocked.Exchange(
                ref _transaction,
                null);

            try
            {
                transaction?.Rollback();
            }
            finally
            {
                transaction?.Dispose();
                connection.Dispose();
            }
        }
    }

    private sealed class MaxScoreMaintenanceLease
        : IMaxScoreMaintenanceLease
    {
        private readonly MetaDatabase _owner;
        private NpgsqlConnection? _connection;
        private readonly string _leaseToken;
        private readonly bool _retainPublicationLock;
        private readonly SemaphoreSlim _operationGate = new(1, 1);

        public MaxScoreMaintenanceLease(
            MetaDatabase owner,
            NpgsqlConnection connection,
            long publicationId,
            string leaseToken,
            int backendProcessId,
            bool retainPublicationLock)
        {
            _owner = owner;
            _connection = connection;
            PublicationId = publicationId;
            _leaseToken = leaseToken;
            BackendProcessId = backendProcessId;
            _retainPublicationLock = retainPublicationLock;
        }

        public int BackendProcessId { get; }
        public long PublicationId { get; }

        public void VerifyHeld(bool requireSourceLocks)
        {
            _operationGate.Wait();
            try
            {
                var connection = _connection
                    ?? throw new MaxScoreMaintenanceLeaseLostException();
                if (!requireSourceLocks
                    && _retainPublicationLock)
                {
                    VerifyOwnedConnection(
                        connection,
                        transaction: null,
                        requireSourceLocks: false,
                        requirePublicationLock: true);
                    return;
                }

                using var transaction = connection.BeginTransaction();
                ConfigureOwnedTransaction(connection, transaction);
                if (requireSourceLocks)
                    AcquireSourceLocks(connection, transaction);
                VerifyOwnedConnection(
                    connection,
                    transaction,
                    requireSourceLocks,
                    requirePublicationLock:
                        _retainPublicationLock);
                transaction.Commit();
            }
            catch (MaxScoreMaintenanceLeaseLostException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new MaxScoreMaintenanceLeaseLostException(ex);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public async Task VerifyHeldAsync(
            bool requireSourceLocks,
            CancellationToken ct = default)
        {
            await _operationGate.WaitAsync(ct);
            try
            {
                var connection = _connection
                    ?? throw new MaxScoreMaintenanceLeaseLostException();
                if (!requireSourceLocks
                    && _retainPublicationLock)
                {
                    await VerifyOwnedConnectionAsync(
                        connection,
                        transaction: null,
                        ct,
                        requireSourceLocks: false,
                        requirePublicationLock: true);
                    return;
                }

                await using var transaction =
                    await connection.BeginTransactionAsync(ct);
                await ConfigureOwnedTransactionAsync(
                    connection,
                    transaction,
                    ct);
                if (requireSourceLocks)
                {
                    await AcquireSourceLocksAsync(
                        connection,
                        transaction,
                        ct);
                }
                await VerifyOwnedConnectionAsync(
                    connection,
                    transaction,
                    ct,
                    requireSourceLocks,
                    requirePublicationLock:
                        _retainPublicationLock);
                await transaction.CommitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (MaxScoreMaintenanceLeaseLostException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new MaxScoreMaintenanceLeaseLostException(ex);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public Task ExecuteTransactionAsync(
            string operation,
            bool requireSourceLocks,
            Func<
                NpgsqlConnection,
                NpgsqlTransaction,
                CancellationToken,
                Task> action,
            IsolationLevel isolationLevel =
                IsolationLevel.ReadCommitted,
            CancellationToken ct = default)
            => ExecuteTransactionAsync<object?>(
                operation,
                requireSourceLocks,
                async (connection, transaction, token) =>
                {
                    await action(connection, transaction, token);
                    return null;
                },
                isolationLevel,
                ct);

        public async Task<T> ExecuteTransactionAsync<T>(
            string operation,
            bool requireSourceLocks,
            Func<
                NpgsqlConnection,
                NpgsqlTransaction,
                CancellationToken,
                Task<T>> action,
            IsolationLevel isolationLevel =
                IsolationLevel.ReadCommitted,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operation);
            ArgumentNullException.ThrowIfNull(action);
            return await ExecuteOwnedTransactionAsync(
                operation,
                requireSourceLocks,
                action,
                isolationLevel,
                verifyAfterAction: true,
                ct);
        }

        public async Task CompleteAsync(
            long publishedScrapeId,
            string manifestSha256,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await ExecuteOwnedTransactionAsync<object?>(
                "final-cache-publication-unfreeze",
                requireSourceLocks: true,
                (connection, transaction, _) =>
                {
                    _owner.CompleteMaxScoreMaintenance(
                        connection,
                        transaction,
                        PublicationId,
                        publishedScrapeId,
                        manifestSha256,
                        _leaseToken);
                    return Task.FromResult<object?>(null);
                },
                IsolationLevel.Serializable,
                verifyAfterAction: true,
                ct);
        }

        public async Task CompleteRollbackAsync(
            long publishedScrapeId,
            string manifestSha256,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await ExecuteOwnedTransactionAsync<object?>(
                "final-rollback-cache-publication-unfreeze",
                requireSourceLocks: true,
                (connection, transaction, _) =>
                {
                    _owner.CompleteMaxScoreMaintenanceRollback(
                        connection,
                        transaction,
                        PublicationId,
                        publishedScrapeId,
                        manifestSha256,
                        _leaseToken);
                    return Task.FromResult<object?>(null);
                },
                IsolationLevel.Serializable,
                verifyAfterAction: true,
                ct);
        }

        public void Dispose()
        {
            var connection = Interlocked.Exchange(ref _connection, null);
            if (connection is null)
                return;
            try
            {
                try
                {
                    ReleaseMaxScoreMaintenanceLocks(
                        connection,
                        mutationGateLockAcquired: true,
                        pathLockAcquired: true,
                        publicationLockAcquired:
                            _retainPublicationLock);
                    _owner
                        .MaxScoreMaintenanceAfterLocksReleasedTestHook
                        ?.Invoke(
                            new MaxScoreMaintenanceCommitTestContext(
                                "lease-disposal",
                                BackendProcessId));
                    ClearMaxScoreMutationGateAsync(
                            connection,
                            _leaseToken,
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception ex)
                {
                    _owner._log.LogWarning(
                        ex,
                        "Failed to explicitly clear or release the max-score maintenance lease; closing its isolated PostgreSQL session.");
                }
            }
            finally
            {
                _operationGate.Dispose();
                connection.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            var connection = Interlocked.Exchange(
                ref _connection,
                null);
            if (connection is null)
                return;
            try
            {
                try
                {
                    ReleaseMaxScoreMaintenanceLocks(
                        connection,
                        mutationGateLockAcquired: true,
                        pathLockAcquired: true,
                        publicationLockAcquired:
                            _retainPublicationLock);
                    _owner
                        .MaxScoreMaintenanceAfterLocksReleasedTestHook
                        ?.Invoke(
                            new MaxScoreMaintenanceCommitTestContext(
                                "lease-disposal",
                                BackendProcessId));
                    await ClearMaxScoreMutationGateAsync(
                        connection,
                        _leaseToken,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _owner._log.LogWarning(
                        ex,
                        "Failed to explicitly clear or release the max-score maintenance lease; closing its isolated PostgreSQL session.");
                }
            }
            finally
            {
                _operationGate.Dispose();
                await connection.DisposeAsync();
            }
        }

        private async Task<T> ExecuteOwnedTransactionAsync<T>(
            string operation,
            bool requireSourceLocks,
            Func<
                NpgsqlConnection,
                NpgsqlTransaction,
                CancellationToken,
                Task<T>> action,
            IsolationLevel isolationLevel,
            bool verifyAfterAction,
            CancellationToken ct)
        {
            await _operationGate.WaitAsync(ct);
            var commitStarted = false;
            try
            {
                var connection = _connection
                    ?? throw new MaxScoreMaintenanceLeaseLostException();
                await using var transaction =
                    await connection.BeginTransactionAsync(
                        isolationLevel,
                        ct);
                await ConfigureOwnedTransactionAsync(
                    connection,
                    transaction,
                    ct);
                if (requireSourceLocks)
                {
                    await AcquireSourceLocksAsync(
                        connection,
                        transaction,
                        ct);
                }
                await VerifyOwnedConnectionAsync(
                    connection,
                    transaction,
                    ct,
                    requireSourceLocks,
                    requirePublicationLock:
                        _retainPublicationLock);

                var result = await action(
                    connection,
                    transaction,
                    ct);
                if (!_retainPublicationLock)
                {
                    // MVCC keeps uncommitted rollback rows invisible. Drain
                    // existing publication readers only at commit so each
                    // request observes the state before or after this unit.
                    await AcquirePublicationCommitLockAsync(
                        connection,
                        transaction,
                        ct);
                }
                await using (var durableCommit =
                             connection.CreateCommand())
                {
                    durableCommit.Transaction = transaction;
                    durableCommit.CommandText =
                        "SET LOCAL synchronous_commit = on";
                    await durableCommit.ExecuteNonQueryAsync(ct);
                }
                if (verifyAfterAction)
                {
                    await VerifyOwnedConnectionAsync(
                        connection,
                        transaction,
                        ct,
                        requireSourceLocks,
                        requirePublicationLock: true);
                }

                _owner.MaxScoreMaintenanceBeforeCommitTestHook
                    ?.Invoke(new MaxScoreMaintenanceCommitTestContext(
                        operation,
                        BackendProcessId));
                commitStarted = true;
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (OperationCanceledException) when (
                ct.IsCancellationRequested)
            {
                throw;
            }
            catch (MaxScoreMaintenanceLeaseLostException)
            {
                throw;
            }
            catch (Exception ex) when (
                commitStarted
                || _connection is null
                || _connection.State != ConnectionState.Open)
            {
                throw new MaxScoreMaintenanceLeaseLostException(ex);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        private void ConfigureOwnedTransaction(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SET LOCAL lock_timeout = '5s';
                SET LOCAL statement_timeout = 0;
                SET LOCAL idle_in_transaction_session_timeout = 0;
                SELECT set_config(
                    'fst.max_score_registration_guard_bypass',
                    @leaseToken,
                    TRUE);
                """;
            command.Parameters.AddWithValue(
                "leaseToken",
                _leaseToken);
            command.ExecuteNonQuery();
        }

        private async Task ConfigureOwnedTransactionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SET LOCAL lock_timeout = '5s';
                SET LOCAL statement_timeout = 0;
                SET LOCAL idle_in_transaction_session_timeout = 0;
                SELECT set_config(
                    'fst.max_score_registration_guard_bypass',
                    @leaseToken,
                    TRUE);
                """;
            command.Parameters.AddWithValue(
                "leaseToken",
                _leaseToken);
            await command.ExecuteNonQueryAsync(ct);
        }

        private static void AcquireSourceLocks(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                MaxScoreMaintenanceSourceLockSql;
            command.ExecuteNonQuery();
        }

        private static async Task AcquireSourceLocksAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                MaxScoreMaintenanceSourceLockSql;
            await command.ExecuteNonQueryAsync(ct);
        }

        private static async Task AcquirePublicationCommitLockAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT pg_advisory_xact_lock(@lockKey)";
            command.Parameters.AddWithValue(
                "lockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            await command.ExecuteNonQueryAsync(ct);
        }

        private void VerifyOwnedConnection(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            bool requireSourceLocks,
            bool requirePublicationLock)
        {
            using var command = CreateVerificationCommand(
                connection,
                transaction,
                requireSourceLocks,
                requirePublicationLock);
            if (command.ExecuteScalar() is not true)
                throw new MaxScoreMaintenanceLeaseLostException();
        }

        private async Task VerifyOwnedConnectionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            CancellationToken ct,
            bool requireSourceLocks = false,
            bool requirePublicationLock = true)
        {
            await using var command = CreateVerificationCommand(
                connection,
                transaction,
                requireSourceLocks,
                requirePublicationLock);
            try
            {
                if (await command.ExecuteScalarAsync(ct) is not true)
                    throw new MaxScoreMaintenanceLeaseLostException();
            }
            catch (OperationCanceledException) when (
                ct.IsCancellationRequested)
            {
                throw;
            }
            catch (MaxScoreMaintenanceLeaseLostException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new MaxScoreMaintenanceLeaseLostException(ex);
            }
        }

        private NpgsqlCommand CreateVerificationCommand(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            bool requireSourceLocks,
            bool requirePublicationLock)
        {
            var command = connection.CreateCommand();
            command.CommandTimeout = 5;
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    pg_backend_pid() = @backendProcessId
                    AND current_setting(
                            'fst.max_score_maintenance_lease_token',
                            TRUE) = @leaseToken
                    AND (
                        SELECT COUNT(*) =
                            CASE
                                WHEN @requirePublicationLock
                                    THEN 3
                                ELSE 2
                            END
                        FROM unnest(@lockKeys::BIGINT[])
                            AS expected(lock_key)
                        WHERE (
                            @requirePublicationLock
                            OR expected.lock_key <>
                                @publicationLockKey
                        )
                          AND EXISTS (
                            SELECT 1
                            FROM pg_locks held
                            WHERE held.pid = pg_backend_pid()
                              AND held.locktype = 'advisory'
                              AND held.mode = 'ExclusiveLock'
                              AND held.granted
                              AND held.classid =
                                  (((expected.lock_key >> 32)
                                      & 4294967295)::OID)
                              AND held.objid =
                                  ((expected.lock_key
                                      & 4294967295)::OID)
                              AND held.objsubid = 1
                        )
                    )
                    AND EXISTS (
                        SELECT 1
                        FROM scrape_publication_state state
                        WHERE state.id = TRUE
                          AND state.max_score_mutation_gate_token =
                              @leaseToken
                          AND state.max_score_mutation_gate_publication_id =
                              @publicationId
                          AND state.max_score_mutation_gate_backend_pid =
                              @backendProcessId
                    )
                    AND (
                        NOT @requireSourceLocks
                        OR (
                            SELECT COUNT(DISTINCT held.relation) = 5
                            FROM pg_locks held
                            WHERE held.pid = pg_backend_pid()
                              AND held.locktype = 'relation'
                              AND held.mode = 'ShareLock'
                              AND held.granted
                              AND held.relation = ANY(
                                  ARRAY[
                                      'leaderboard_entries_overlay'::REGCLASS,
                                      'leaderboard_entries'::REGCLASS,
                                      'score_history'::REGCLASS,
                                      'band_member_stats'::REGCLASS,
                                      'leaderboard_population'::REGCLASS
                                  ]::OID[])
                        )
                    )
                """;
            command.Parameters.AddWithValue(
                "backendProcessId",
                BackendProcessId);
            command.Parameters.AddWithValue(
                "leaseToken",
                _leaseToken);
            command.Parameters.AddWithValue(
                "publicationId",
                PublicationId);
            command.Parameters.AddWithValue(
                "publicationLockKey",
                PublicationGenerationSchema.AdvisoryLockKey);
            command.Parameters.Add(
                    "lockKeys",
                    NpgsqlDbType.Array | NpgsqlDbType.Bigint)
                .Value = new[]
                {
                    RegistrationMutationGate.AdvisoryLockKey,
                    PathGenerationAdmissionLock.AdvisoryLockKey,
                    PublicationGenerationSchema.AdvisoryLockKey,
                };
            command.Parameters.AddWithValue(
                "requireSourceLocks",
                requireSourceLocks);
            command.Parameters.AddWithValue(
                "requirePublicationLock",
                requirePublicationLock);
            return command;
        }
    }

    // ── Private helpers ──────────────────────────────────────────────

    private static string? GetLeaderboardStagingPartitionName(string instrument) => instrument switch
    {
        "Solo_Guitar" => "leaderboard_staging_v2_solo_guitar",
        "Solo_Bass" => "leaderboard_staging_v2_solo_bass",
        "Solo_Drums" => "leaderboard_staging_v2_solo_drums",
        "Solo_Vocals" => "leaderboard_staging_v2_solo_vocals",
        "Solo_PeripheralGuitar" => "leaderboard_staging_v2_pro_guitar",
        "Solo_PeripheralBass" => "leaderboard_staging_v2_pro_bass",
        "Solo_PeripheralVocals" => "leaderboard_staging_v2_pro_vocals",
        "Solo_PeripheralCymbals" => "leaderboard_staging_v2_pro_cymbals",
        "Solo_PeripheralDrums" => "leaderboard_staging_v2_pro_drums",
        _ => null,
    };

    // ── Leaderboard staging ──────────────────────────────────────────

    public void StageChunk(long scrapeId, string songId, string instrument,
        IReadOnlyList<(int PageNum, LeaderboardEntry Entry)> entries)
    {
        if (entries.Count == 0) return;
        var now = DateTime.UtcNow;
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var sc = conn.CreateCommand()) { sc.Transaction = tx; sc.CommandText = "SET LOCAL synchronous_commit = off"; sc.ExecuteNonQuery(); }

        using (var writer = conn.BeginBinaryImport(
            $"COPY {LeaderboardStagingTable} (scrape_id, song_id, instrument, page_num, account_id, score, accuracy, " +
            "is_full_combo, stars, season, difficulty, percentile, rank, end_time, api_rank, source, staged_at) " +
            "FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var (pageNum, e) in entries)
            {
                writer.StartRow();
                writer.Write((int)scrapeId, NpgsqlDbType.Integer);
                writer.Write(songId, NpgsqlDbType.Text);
                writer.Write(instrument, NpgsqlDbType.Text);
                writer.Write(pageNum, NpgsqlDbType.Integer);
                writer.Write(e.AccountId, NpgsqlDbType.Text);
                writer.Write(e.Score, NpgsqlDbType.Integer);
                writer.Write(e.Accuracy, NpgsqlDbType.Integer);
                writer.Write(e.IsFullCombo, NpgsqlDbType.Boolean);
                writer.Write(e.Stars, NpgsqlDbType.Integer);
                writer.Write(e.Season, NpgsqlDbType.Integer);
                writer.Write(e.Difficulty, NpgsqlDbType.Integer);
                writer.Write(e.Percentile, NpgsqlDbType.Double);
                writer.Write(e.Rank, NpgsqlDbType.Integer);
                if (e.EndTime is not null) writer.Write(e.EndTime, NpgsqlDbType.Text);
                else writer.WriteNull();
                if (e.ApiRank > 0) writer.Write(e.ApiRank, NpgsqlDbType.Integer);
                else writer.WriteNull();
                writer.Write(e.Source ?? "scrape", NpgsqlDbType.Text);
                writer.Write(now, NpgsqlDbType.TimestampTz);
            }
            writer.Complete();
        }
        tx.Commit();
    }

    public void UpsertStagingMeta(long scrapeId, string songId, string instrument, StagingMetaUpdate update)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO leaderboard_staging_meta (scrape_id, song_id, instrument, reported_pages, pages_scraped, entries_staged, valid_entry_count, requests, bytes_received, deep_scrape_status) " +
            "VALUES (@scrapeId, @songId, @instrument, @reportedPages, @pagesScraped, @entriesStaged, @validEntryCount, @requests, @bytesReceived, @deepScrapeStatus) " +
            "ON CONFLICT (scrape_id, song_id, instrument) DO UPDATE SET " +
            "reported_pages = GREATEST(leaderboard_staging_meta.reported_pages, EXCLUDED.reported_pages), " +
            "pages_scraped = leaderboard_staging_meta.pages_scraped + EXCLUDED.pages_scraped, " +
            "entries_staged = leaderboard_staging_meta.entries_staged + EXCLUDED.entries_staged, " +
            "valid_entry_count = COALESCE(EXCLUDED.valid_entry_count, leaderboard_staging_meta.valid_entry_count), " +
            "requests = leaderboard_staging_meta.requests + EXCLUDED.requests, " +
            "bytes_received = leaderboard_staging_meta.bytes_received + EXCLUDED.bytes_received, " +
            "deep_scrape_status = COALESCE(EXCLUDED.deep_scrape_status, leaderboard_staging_meta.deep_scrape_status)";
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("reportedPages", update.ReportedPages);
        cmd.Parameters.AddWithValue("pagesScraped", update.PagesScraped);
        cmd.Parameters.AddWithValue("entriesStaged", update.EntriesStaged);
        cmd.Parameters.AddWithValue("validEntryCount", (object?)update.ValidEntryCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("requests", update.Requests);
        cmd.Parameters.AddWithValue("bytesReceived", update.BytesReceived);
        cmd.Parameters.AddWithValue("deepScrapeStatus", (object?)update.DeepScrapeStatus ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<StagingMetaRow> GetStagingMeta(long scrapeId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT scrape_id, song_id, instrument, reported_pages, pages_scraped, entries_staged, " +
            "valid_entry_count, requests, bytes_received, deep_scrape_status, wave1_finalized_at, wave2_finalized_at " +
            "FROM leaderboard_staging_meta WHERE scrape_id = @scrapeId";
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        var list = new List<StagingMetaRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new StagingMetaRow
            {
                ScrapeId = r.GetInt32(0),
                SongId = r.GetString(1),
                Instrument = r.GetString(2),
                ReportedPages = r.GetInt32(3),
                PagesScraped = r.GetInt32(4),
                EntriesStaged = r.GetInt32(5),
                ValidEntryCount = r.IsDBNull(6) ? null : r.GetInt32(6),
                Requests = r.GetInt32(7),
                BytesReceived = r.GetInt64(8),
                DeepScrapeStatus = r.IsDBNull(9) ? null : r.GetString(9),
                Wave1FinalizedAt = r.IsDBNull(10) ? null : r.GetDateTime(10),
                Wave2FinalizedAt = r.IsDBNull(11) ? null : r.GetDateTime(11),
            });
        }
        return list;
    }

    public void MarkWaveFinalized(long scrapeId, string songId, string instrument, int wave)
    {
        var column = wave == 1 ? "wave1_finalized_at" : "wave2_finalized_at";
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE leaderboard_staging_meta SET {column} = @now WHERE scrape_id = @scrapeId AND song_id = @songId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.ExecuteNonQuery();
    }

    public void EnqueueDeepScrapeJob(DeepScrapeJobInfo job)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO deep_scrape_queue (scrape_id, song_id, instrument, label, valid_cutoff, valid_entry_target, " +
            "wave2_start_page, reported_pages, initial_valid_count, status) " +
            "VALUES (@scrapeId, @songId, @instrument, @label, @validCutoff, @validEntryTarget, " +
            "@wave2StartPage, @reportedPages, @initialValidCount, 'pending') " +
            "ON CONFLICT (scrape_id, song_id, instrument) DO NOTHING";
        cmd.Parameters.AddWithValue("scrapeId", (int)job.ScrapeId);
        cmd.Parameters.AddWithValue("songId", job.SongId);
        cmd.Parameters.AddWithValue("instrument", job.Instrument);
        cmd.Parameters.AddWithValue("label", (object?)job.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("validCutoff", job.ValidCutoff);
        cmd.Parameters.AddWithValue("validEntryTarget", job.ValidEntryTarget);
        cmd.Parameters.AddWithValue("wave2StartPage", job.Wave2StartPage);
        cmd.Parameters.AddWithValue("reportedPages", job.ReportedPages);
        cmd.Parameters.AddWithValue("initialValidCount", job.InitialValidCount);
        cmd.ExecuteNonQuery();
    }

    public List<DeepScrapeQueueRow> GetDeepScrapeJobs(long scrapeId, string? status = null)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        var filter = status is not null ? " AND status = @status" : "";
        cmd.CommandText =
            "SELECT scrape_id, song_id, instrument, label, valid_cutoff, valid_entry_target, " +
            "wave2_start_page, reported_pages, initial_valid_count, status, cursor_page, " +
            "current_valid_count, created_at, completed_at " +
            $"FROM deep_scrape_queue WHERE scrape_id = @scrapeId{filter} ORDER BY created_at";
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        if (status is not null) cmd.Parameters.AddWithValue("status", status);
        var list = new List<DeepScrapeQueueRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new DeepScrapeQueueRow
            {
                ScrapeId = r.GetInt32(0),
                SongId = r.GetString(1),
                Instrument = r.GetString(2),
                Label = r.IsDBNull(3) ? null : r.GetString(3),
                ValidCutoff = r.GetInt32(4),
                ValidEntryTarget = r.GetInt32(5),
                Wave2StartPage = r.GetInt32(6),
                ReportedPages = r.GetInt32(7),
                InitialValidCount = r.GetInt32(8),
                Status = r.GetString(9),
                CursorPage = r.IsDBNull(10) ? null : r.GetInt32(10),
                CurrentValidCount = r.IsDBNull(11) ? null : r.GetInt32(11),
                CreatedAt = r.GetDateTime(12),
                CompletedAt = r.IsDBNull(13) ? null : r.GetDateTime(13),
            });
        }
        return list;
    }

    public void UpdateDeepScrapeJobCursor(long scrapeId, string songId, string instrument,
        int cursorPage, int currentValidCount)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE deep_scrape_queue SET cursor_page = @cursor, current_valid_count = @valid, status = 'running' " +
            "WHERE scrape_id = @scrapeId AND song_id = @songId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("cursor", cursorPage);
        cmd.Parameters.AddWithValue("valid", currentValidCount);
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.ExecuteNonQuery();
    }

    public void CompleteDeepScrapeJob(long scrapeId, string songId, string instrument, string status)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE deep_scrape_queue SET status = @status, completed_at = @now " +
            "WHERE scrape_id = @scrapeId AND song_id = @songId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.ExecuteNonQuery();
    }

    public int CleanupAbandonedStaging(long currentScrapeId)
    {
        using var conn = _ds.OpenConnection();
        int total = 0;

        total += CleanupAbandonedStagingTable(conn, LeaderboardStagingTable, currentScrapeId);
        total += CleanupAbandonedStagingTable(conn, LegacyLeaderboardStagingTable, currentScrapeId);

        // staging_meta and deep_scrape_queue are tiny — single deletes are fine
        using (var tx = conn.BeginTransaction())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM leaderboard_staging_meta WHERE scrape_id < @id";
                cmd.Parameters.AddWithValue("id", (int)currentScrapeId);
                total += cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM deep_scrape_queue WHERE scrape_id < @id";
                cmd.Parameters.AddWithValue("id", (int)currentScrapeId);
                total += cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    DELETE FROM scrape_log log
                    WHERE log.id < @id
                      AND log.completed_at IS NULL
                      AND log.status = 'running'
                      AND NOT EXISTS (
                          SELECT 1
                          FROM scrape_publication_state state
                          WHERE state.public_reads_frozen_scrape_id = log.id
                      )
                    """;
                cmd.Parameters.AddWithValue("id", (int)currentScrapeId);
                total += cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return total;
    }

    private static int CleanupAbandonedStagingTable(NpgsqlConnection conn, string tableName, long currentScrapeId)
    {
        int total = 0;

        int staleRows;
        bool hasCurrentRows;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"SELECT COUNT(*) FILTER (WHERE scrape_id < @id), COUNT(*) FILTER (WHERE scrape_id >= @id) FROM {tableName}";
            cmd.Parameters.AddWithValue("id", (int)currentScrapeId);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            staleRows = reader.GetInt32(0);
            hasCurrentRows = reader.GetInt32(1) > 0;
        }

        // Delete staging rows in batches to avoid a single massive DELETE that
        // generates excessive WAL and exceeds command timeouts. Each batch
        // runs in its own transaction so progress is incremental.
        if (staleRows > 0 && !hasCurrentRows)
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"TRUNCATE {tableName}";
            cmd.ExecuteNonQuery();
            tx.Commit();
            total += staleRows;
        }
        else
        {
            const int batchSize = 500_000;
            int deleted;
            do
            {
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandTimeout = 0;
                cmd.CommandText =
                    $"DELETE FROM {tableName} WHERE ctid = ANY(" +
                    $"ARRAY(SELECT ctid FROM {tableName} WHERE scrape_id < @id LIMIT @limit))";
                cmd.Parameters.AddWithValue("id", (int)currentScrapeId);
                cmd.Parameters.AddWithValue("limit", batchSize);
                deleted = cmd.ExecuteNonQuery();
                tx.Commit();
                total += deleted;
            } while (deleted >= batchSize);
        }
        return total;
    }

    public int DeleteStagedEntries(long scrapeId, string songId, string instrument)
    {
        using var conn = _ds.OpenConnection();
        return DeleteStagedEntries(conn, LeaderboardStagingTable, scrapeId, songId, instrument)
            + DeleteStagedEntries(conn, LegacyLeaderboardStagingTable, scrapeId, songId, instrument);
    }

    private static int DeleteStagedEntries(NpgsqlConnection conn, string tableName, long scrapeId, string songId, string instrument)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {tableName} WHERE scrape_id = @scrapeId AND song_id = @songId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        return cmd.ExecuteNonQuery();
    }

    public int DeleteStagedEntriesForInstrument(long scrapeId, string instrument)
    {
        using var conn = _ds.OpenConnection();
        var partitionName = GetLeaderboardStagingPartitionName(instrument);
        var deleted = 0;

        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM {LeaderboardStagingTable} WHERE scrape_id = @scrapeId AND instrument = @instrument";
            countCmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
            countCmd.Parameters.AddWithValue("instrument", instrument);
            var stagedRows = Convert.ToInt32(countCmd.ExecuteScalar());
            if (stagedRows > 0 && partitionName is not null)
            {
                using var probeCmd = conn.CreateCommand();
                probeCmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {partitionName} WHERE scrape_id <> @scrapeId)";
                probeCmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
                var hasOtherScrapeRows = Convert.ToBoolean(probeCmd.ExecuteScalar());
                if (!hasOtherScrapeRows)
                {
                    using var tx = conn.BeginTransaction();
                    using var truncateCmd = conn.CreateCommand();
                    truncateCmd.Transaction = tx;
                    truncateCmd.CommandText = $"TRUNCATE {partitionName}";
                    truncateCmd.ExecuteNonQuery();
                    tx.Commit();
                    deleted += stagedRows;
                }
                else
                {
                    deleted += DeleteStagedEntriesForInstrument(conn, LeaderboardStagingTable, scrapeId, instrument);
                }
            }
            else if (stagedRows > 0)
            {
                deleted += DeleteStagedEntriesForInstrument(conn, LeaderboardStagingTable, scrapeId, instrument);
            }
        }

        using (var legacyCountCmd = conn.CreateCommand())
        {
            legacyCountCmd.CommandText = $"SELECT COUNT(*) FROM {LegacyLeaderboardStagingTable} WHERE scrape_id = @scrapeId AND instrument = @instrument";
            legacyCountCmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
            legacyCountCmd.Parameters.AddWithValue("instrument", instrument);
            var legacyRows = Convert.ToInt32(legacyCountCmd.ExecuteScalar());
            if (legacyRows > 0)
            {
                using var legacyProbeCmd = conn.CreateCommand();
                legacyProbeCmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {LegacyLeaderboardStagingTable} WHERE scrape_id <> @scrapeId OR instrument <> @instrument)";
                legacyProbeCmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
                legacyProbeCmd.Parameters.AddWithValue("instrument", instrument);
                var hasOtherLegacyRows = Convert.ToBoolean(legacyProbeCmd.ExecuteScalar());
                if (!hasOtherLegacyRows)
                {
                    using var tx = conn.BeginTransaction();
                    using var truncateCmd = conn.CreateCommand();
                    truncateCmd.Transaction = tx;
                    truncateCmd.CommandText = $"TRUNCATE {LegacyLeaderboardStagingTable}";
                    truncateCmd.ExecuteNonQuery();
                    tx.Commit();
                    deleted += legacyRows;
                }
                else
                {
                    deleted += DeleteStagedEntriesForInstrument(conn, LegacyLeaderboardStagingTable, scrapeId, instrument);
                }
            }
        }

        return deleted;
    }

    private static int DeleteStagedEntriesForInstrument(NpgsqlConnection conn, string tableName, long scrapeId, string instrument)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {tableName} WHERE scrape_id = @scrapeId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        return cmd.ExecuteNonQuery();
    }

    public void MarkWaveFinalizedForInstrument(long scrapeId, string instrument, int wave)
    {
        var column = wave == 1 ? "wave1_finalized_at" : "wave2_finalized_at";
        using var conn = _ds.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE leaderboard_staging_meta SET {column} = @now WHERE scrape_id = @scrapeId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.ExecuteNonQuery();
    }

    public int GetStagedEntryCount(long scrapeId, string songId, string instrument)
    {
        using var conn = _ds.OpenConnection();
        return GetStagedEntryCount(conn, LeaderboardStagingTable, scrapeId, songId, instrument)
            + GetStagedEntryCount(conn, LegacyLeaderboardStagingTable, scrapeId, songId, instrument);
    }

    private static int GetStagedEntryCount(NpgsqlConnection conn, string tableName, long scrapeId, string songId, string instrument)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {tableName} WHERE scrape_id = @scrapeId AND song_id = @songId AND instrument = @instrument";
        cmd.Parameters.AddWithValue("scrapeId", (int)scrapeId);
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int GetBandRankingTotalTeams(string bandType, string rankingScope, string comboId, bool usePublishedSnapshot = false)
    {
        using var conn = _ds.OpenConnection();
        var statsTable = ResolveBandRankingStatsReadTable(conn, bandType, usePublishedSnapshot);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT total_teams
            FROM {BandRankingStorageNames.QuoteIdentifier(statsTable)}
            WHERE band_type = @bandType AND ranking_scope = @scope AND combo_id = @comboId";
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("scope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", comboId);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? 0 : Convert.ToInt32(result);
    }

    private static void EnsureBandRankHistoryTables(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT pg_advisory_xact_lock(hashtextextended('fst.band_rank_history_schema', 0));

            CREATE TABLE IF NOT EXISTS band_team_rank_history (
                band_type             TEXT             NOT NULL,
                ranking_scope         TEXT             NOT NULL,
                combo_id              TEXT             NOT NULL DEFAULT '',
                team_key              TEXT             NOT NULL,
                team_members          TEXT[]           NOT NULL,
                songs_played          INT              NOT NULL,
                total_charted_songs   INT              NOT NULL,
                coverage              DOUBLE PRECISION NOT NULL,
                raw_skill_rating      DOUBLE PRECISION NOT NULL,
                adjusted_skill_rating DOUBLE PRECISION NOT NULL,
                adjusted_skill_rank   INT              NOT NULL,
                weighted_rating       DOUBLE PRECISION NOT NULL,
                weighted_rank         INT              NOT NULL,
                fc_rate               DOUBLE PRECISION NOT NULL,
                fc_rate_rank          INT              NOT NULL,
                total_score           BIGINT           NOT NULL,
                total_score_rank      INT              NOT NULL,
                avg_accuracy          DOUBLE PRECISION NOT NULL,
                full_combo_count      INT              NOT NULL,
                avg_stars             DOUBLE PRECISION NOT NULL,
                best_rank             INT              NOT NULL,
                avg_rank              DOUBLE PRECISION NOT NULL,
                raw_weighted_rating   DOUBLE PRECISION,
                computed_at           TIMESTAMPTZ      NOT NULL,
                snapshot_date         DATE             NOT NULL,
                PRIMARY KEY (band_type, ranking_scope, combo_id, team_key, snapshot_date)
            );

            CREATE TABLE IF NOT EXISTS band_team_rank_history_latest (
                band_type             TEXT             NOT NULL,
                ranking_scope         TEXT             NOT NULL,
                combo_id              TEXT             NOT NULL DEFAULT '',
                team_key              TEXT             NOT NULL,
                team_members          TEXT[]           NOT NULL,
                songs_played          INT              NOT NULL,
                total_charted_songs   INT              NOT NULL,
                coverage              DOUBLE PRECISION NOT NULL,
                raw_skill_rating      DOUBLE PRECISION NOT NULL,
                adjusted_skill_rating DOUBLE PRECISION NOT NULL,
                adjusted_skill_rank   INT              NOT NULL,
                weighted_rating       DOUBLE PRECISION NOT NULL,
                weighted_rank         INT              NOT NULL,
                fc_rate               DOUBLE PRECISION NOT NULL,
                fc_rate_rank          INT              NOT NULL,
                total_score           BIGINT           NOT NULL,
                total_score_rank      INT              NOT NULL,
                avg_accuracy          DOUBLE PRECISION NOT NULL,
                full_combo_count      INT              NOT NULL,
                avg_stars             DOUBLE PRECISION NOT NULL,
                best_rank             INT              NOT NULL,
                avg_rank              DOUBLE PRECISION NOT NULL,
                raw_weighted_rating   DOUBLE PRECISION,
                computed_at           TIMESTAMPTZ      NOT NULL,
                snapshot_date         DATE             NOT NULL,
                fingerprint           TEXT             NOT NULL,
                updated_at            TIMESTAMPTZ      NOT NULL DEFAULT now(),
                PRIMARY KEY (band_type, ranking_scope, combo_id, team_key)
            );

            CREATE TABLE IF NOT EXISTS band_team_rank_history_points (
                band_type             TEXT             NOT NULL,
                ranking_scope         TEXT             NOT NULL,
                combo_id              TEXT             NOT NULL DEFAULT '',
                team_key              TEXT             NOT NULL,
                snapshot_date         DATE             NOT NULL,
                snapshot_taken_at     TIMESTAMPTZ      NOT NULL,
                adjusted_skill_rank   INT              NOT NULL,
                weighted_rank         INT              NOT NULL,
                fc_rate_rank          INT              NOT NULL,
                total_score_rank      INT              NOT NULL,
                adjusted_skill_rating DOUBLE PRECISION,
                weighted_rating       DOUBLE PRECISION,
                fc_rate               DOUBLE PRECISION,
                total_score           BIGINT,
                songs_played          INT,
                coverage              DOUBLE PRECISION,
                full_combo_count      INT,
                total_charted_songs   INT,
                total_ranked_teams    INT,
                raw_weighted_rating   DOUBLE PRECISION,
                raw_skill_rating      DOUBLE PRECISION,
                PRIMARY KEY (band_type, ranking_scope, combo_id, team_key, snapshot_date)
            );

            CREATE TABLE IF NOT EXISTS band_team_ranking_stats_history (
                band_type      TEXT        NOT NULL,
                ranking_scope  TEXT        NOT NULL,
                combo_id       TEXT        NOT NULL DEFAULT '',
                total_teams    INT         NOT NULL,
                computed_at    TIMESTAMPTZ NOT NULL,
                snapshot_date  DATE        NOT NULL,
                PRIMARY KEY (band_type, ranking_scope, combo_id, snapshot_date)
            );

            CREATE TABLE IF NOT EXISTS band_rank_history_jobs (
                job_id                 BIGSERIAL PRIMARY KEY,
                scrape_id              BIGINT      NOT NULL,
                snapshot_date          DATE        NOT NULL,
                band_type              TEXT        NOT NULL,
                mode                   TEXT        NOT NULL,
                status                 TEXT        NOT NULL,
                started_at             TIMESTAMPTZ,
                completed_at           TIMESTAMPTZ,
                failed_at              TIMESTAMPTZ,
                paused_at              TIMESTAMPTZ,
                superseded_at          TIMESTAMPTZ,
                last_error             TEXT,
                attempts               INT         NOT NULL DEFAULT 0,
                chunks_total           INT         NOT NULL DEFAULT 0,
                chunks_completed       INT         NOT NULL DEFAULT 0,
                rows_scanned           BIGINT      NOT NULL DEFAULT 0,
                rows_inserted          BIGINT      NOT NULL DEFAULT 0,
                rows_skipped           BIGINT      NOT NULL DEFAULT 0,
                source_generation      BIGINT      NOT NULL DEFAULT 0,
                current_ranking_scope  TEXT,
                current_combo_id       TEXT,
                updated_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
                UNIQUE (scrape_id, band_type, snapshot_date)
            );

            CREATE INDEX IF NOT EXISTS ix_brhj_status
                ON band_rank_history_jobs (status, snapshot_date DESC, scrape_id DESC);

            CREATE INDEX IF NOT EXISTS ix_brhj_band_snapshot
                ON band_rank_history_jobs (band_type, snapshot_date DESC, scrape_id DESC, job_id DESC);

            CREATE TABLE IF NOT EXISTS band_rank_history_job_chunks (
                job_id          BIGINT      NOT NULL REFERENCES band_rank_history_jobs(job_id) ON DELETE CASCADE,
                band_type       TEXT        NOT NULL,
                ranking_scope   TEXT        NOT NULL,
                combo_id        TEXT        NOT NULL DEFAULT '',
                chunk_ordinal   INT         NOT NULL DEFAULT 0,
                team_key_start  TEXT,
                team_key_end    TEXT,
                estimated_rows  BIGINT      NOT NULL DEFAULT 0,
                source_generation BIGINT    NOT NULL DEFAULT 0,
                status          TEXT        NOT NULL,
                started_at      TIMESTAMPTZ,
                completed_at    TIMESTAMPTZ,
                rows_scanned    BIGINT      NOT NULL DEFAULT 0,
                rows_inserted   BIGINT      NOT NULL DEFAULT 0,
                rows_skipped    BIGINT      NOT NULL DEFAULT 0,
                last_error      TEXT,
                updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (job_id, ranking_scope, combo_id, chunk_ordinal)
            );

            ALTER TABLE IF EXISTS band_rank_history_jobs
                ADD COLUMN IF NOT EXISTS source_generation BIGINT NOT NULL DEFAULT 0;

            ALTER TABLE IF EXISTS band_rank_history_job_chunks
                ADD COLUMN IF NOT EXISTS chunk_ordinal INT NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS team_key_start TEXT,
                ADD COLUMN IF NOT EXISTS team_key_end TEXT,
                ADD COLUMN IF NOT EXISTS estimated_rows BIGINT NOT NULL DEFAULT 0,
                ADD COLUMN IF NOT EXISTS source_generation BIGINT NOT NULL DEFAULT 0;

            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_constraint con
                    JOIN pg_class rel ON rel.oid = con.conrelid
                    JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
                    CROSS JOIN LATERAL (
                        SELECT array_agg(att.attname ORDER BY keys.ordinality) AS key_columns
                        FROM unnest(con.conkey) WITH ORDINALITY AS keys(attnum, ordinality)
                        JOIN pg_attribute att ON att.attrelid = rel.oid AND att.attnum = keys.attnum
                    ) cols
                    WHERE nsp.nspname = 'public'
                      AND rel.relname = 'band_rank_history_job_chunks'
                      AND con.conname = 'band_rank_history_job_chunks_pkey'
                      AND con.contype = 'p'
                      AND cols.key_columns = ARRAY['job_id', 'ranking_scope', 'combo_id', 'chunk_ordinal']::name[]
                ) THEN
                    ALTER TABLE band_rank_history_job_chunks DROP CONSTRAINT IF EXISTS band_rank_history_job_chunks_pkey;
                    ALTER TABLE band_rank_history_job_chunks ADD CONSTRAINT band_rank_history_job_chunks_pkey PRIMARY KEY (job_id, ranking_scope, combo_id, chunk_ordinal);
                END IF;
            END $$;

            CREATE INDEX IF NOT EXISTS ix_brhjc_job_status_weight
                ON band_rank_history_job_chunks (job_id, status, estimated_rows, ranking_scope, combo_id, chunk_ordinal);

            CREATE TABLE IF NOT EXISTS band_team_ranking_generation (
                generation_id BIGSERIAL PRIMARY KEY,
                scrape_id      BIGINT,
                band_type      TEXT        NOT NULL,
                status         TEXT        NOT NULL,
                computed_at    TIMESTAMPTZ NOT NULL,
                published_at   TIMESTAMPTZ,
                ranking_table  TEXT,
                stats_table    TEXT,
                row_count      BIGINT      NOT NULL DEFAULT 0,
                scope_count    INT         NOT NULL DEFAULT 0,
                created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at     TIMESTAMPTZ NOT NULL DEFAULT now()
            );

            CREATE INDEX IF NOT EXISTS ix_btrg_band_status
                ON band_team_ranking_generation (band_type, status, generation_id DESC);

            CREATE TABLE IF NOT EXISTS band_team_rank_history_snapshot_v2 (
                snapshot_id      BIGSERIAL PRIMARY KEY,
                generation_id    BIGINT      NOT NULL,
                band_type        TEXT        NOT NULL,
                ranking_scope    TEXT        NOT NULL,
                combo_id         TEXT        NOT NULL DEFAULT '',
                snapshot_date    DATE        NOT NULL,
                computed_at      TIMESTAMPTZ NOT NULL,
                source_row_count BIGINT      NOT NULL DEFAULT 0,
                changed_row_count BIGINT     NOT NULL DEFAULT 0,
                status           TEXT        NOT NULL DEFAULT 'complete',
                completed_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
                created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
                UNIQUE (band_type, ranking_scope, combo_id, snapshot_date)
            );

            CREATE INDEX IF NOT EXISTS ix_btrhsv2_generation
                ON band_team_rank_history_snapshot_v2 (generation_id, band_type, ranking_scope, combo_id);

            CREATE TABLE IF NOT EXISTS band_team_rank_history_points_v2 (
                band_type             TEXT             NOT NULL,
                ranking_scope         TEXT             NOT NULL,
                combo_id              TEXT             NOT NULL DEFAULT '',
                team_key              TEXT             NOT NULL,
                snapshot_date         DATE             NOT NULL,
                snapshot_id           BIGINT           NOT NULL,
                generation_id         BIGINT           NOT NULL,
                snapshot_taken_at     TIMESTAMPTZ      NOT NULL,
                row_fingerprint       TEXT             NOT NULL,
                adjusted_skill_rank   INT              NOT NULL,
                weighted_rank         INT              NOT NULL,
                fc_rate_rank          INT              NOT NULL,
                total_score_rank      INT              NOT NULL,
                adjusted_skill_rating DOUBLE PRECISION,
                weighted_rating       DOUBLE PRECISION,
                fc_rate               DOUBLE PRECISION,
                total_score           BIGINT,
                songs_played          INT,
                coverage              DOUBLE PRECISION,
                full_combo_count      INT,
                total_charted_songs   INT,
                total_ranked_teams    INT,
                raw_weighted_rating   DOUBLE PRECISION,
                raw_skill_rating      DOUBLE PRECISION,
                PRIMARY KEY (band_type, ranking_scope, combo_id, team_key, snapshot_date)
            ) PARTITION BY LIST (band_type);

            CREATE TABLE IF NOT EXISTS band_team_rank_history_points_v2_duets
                PARTITION OF band_team_rank_history_points_v2 FOR VALUES IN ('Band_Duets');

            CREATE TABLE IF NOT EXISTS band_team_rank_history_points_v2_trios
                PARTITION OF band_team_rank_history_points_v2 FOR VALUES IN ('Band_Trios');

            CREATE TABLE IF NOT EXISTS band_team_rank_history_points_v2_quad
                PARTITION OF band_team_rank_history_points_v2 FOR VALUES IN ('Band_Quad');

            CREATE INDEX IF NOT EXISTS ix_btrhpv2_team_date
                ON band_team_rank_history_points_v2 (band_type, ranking_scope, combo_id, team_key, snapshot_date DESC);

            CREATE TABLE IF NOT EXISTS band_team_rank_history_latest_v2 (
                band_type       TEXT        NOT NULL,
                ranking_scope   TEXT        NOT NULL,
                combo_id        TEXT        NOT NULL DEFAULT '',
                team_key        TEXT        NOT NULL,
                generation_id   BIGINT      NOT NULL,
                snapshot_id     BIGINT      NOT NULL,
                snapshot_date   DATE        NOT NULL,
                row_fingerprint TEXT        NOT NULL,
                updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (band_type, ranking_scope, combo_id, team_key)
            ) PARTITION BY LIST (band_type);

            CREATE TABLE IF NOT EXISTS band_team_rank_history_latest_v2_duets
                PARTITION OF band_team_rank_history_latest_v2 FOR VALUES IN ('Band_Duets');

            CREATE TABLE IF NOT EXISTS band_team_rank_history_latest_v2_trios
                PARTITION OF band_team_rank_history_latest_v2 FOR VALUES IN ('Band_Trios');

            CREATE TABLE IF NOT EXISTS band_team_rank_history_latest_v2_quad
                PARTITION OF band_team_rank_history_latest_v2 FOR VALUES IN ('Band_Quad');
            ";
        cmd.ExecuteNonQuery();

        using var metadataCmd = conn.CreateCommand();
        metadataCmd.Transaction = tx;
        metadataCmd.CommandText = string.Join(
            Environment.NewLine,
            BandRankingStorageNames.AllBandTypes.Select(bandType =>
                BandRankingStorageNames.GetEnsureRankingMetadataColumnsSql(BandRankingStorageNames.GetCurrentRankingTable(bandType))));
        metadataCmd.ExecuteNonQuery();
    }

    private void EnsureBandRankHistoryPollingSchema(NpgsqlConnection conn)
    {
        if (_bandRankHistoryPollingSchemaEnsured)
            return;

        lock (_bandRankHistoryPollingSchemaLock)
        {
            if (_bandRankHistoryPollingSchemaEnsured)
                return;

            using var tx = conn.BeginTransaction();
            EnsureBandRankHistoryTables(conn, tx);
            tx.Commit();
            _bandRankHistoryPollingSchemaEnsured = true;
        }
    }

    private static string ResolveBandRankingReadTable(NpgsqlConnection conn, string bandType, bool usePublishedSnapshot = false)
        => usePublishedSnapshot
            ? BandRankingStorageNames.GetPublishedRankingTable(bandType)
            : BandRankingStorageNames.GetCurrentRankingTable(bandType);

    private static string ResolveBandRankingStatsReadTable(NpgsqlConnection conn, string bandType, bool usePublishedSnapshot = false)
        => usePublishedSnapshot
            ? BandRankingStorageNames.GetPublishedStatsTable(bandType)
            : BandRankingStorageNames.GetCurrentStatsTable(bandType);

    private static long ReadCurrentBandRankingGeneration(NpgsqlConnection conn, string bandType)
    {
        var rankingsTable = ResolveBandRankingReadTable(conn, bandType);
        if (!TableExists(conn, null, rankingsTable))
            return 0;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT COALESCE(NULLIF(max(ranking_generation), 0), 0)
            FROM {BandRankingStorageNames.QuoteIdentifier(rankingsTable)}
            WHERE band_type = @bandType";
        cmd.Parameters.AddWithValue("bandType", bandType);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private static bool TableExists(NpgsqlConnection conn, NpgsqlTransaction? transaction, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT to_regclass(@tableName) IS NOT NULL";
        cmd.Parameters.AddWithValue("tableName", $"public.{tableName}");
        return Convert.ToBoolean(cmd.ExecuteScalar() ?? false);
    }

    // ── Private helpers ──────────────────────────────────────────────

    private void SimpleUpdate(string sql, string accountId) { using var conn = _ds.OpenConnection(); using var cmd = conn.CreateCommand(); cmd.CommandText = sql; cmd.Parameters.AddWithValue("id", accountId); cmd.Parameters.AddWithValue("now", DateTime.UtcNow); cmd.ExecuteNonQuery(); }
    private static int EstimateBackfillSongCount(int pairCount, int instrumentCount, bool roundUp)
    {
        if (pairCount <= 0 || instrumentCount <= 0) return 0;
        return roundUp ? (pairCount + instrumentCount - 1) / instrumentCount : pairCount / instrumentCount;
    }
    private static BackfillStatusInfo ReadBackfillStatus(NpgsqlDataReader r) => new() { AccountId = r.GetString(0), Status = r.GetString(1), SongsChecked = r.GetInt32(2), EntriesFound = r.GetInt32(3), TotalSongsToCheck = r.GetInt32(4), StartedAt = r.IsDBNull(5) ? null : r.GetDateTime(5).ToString("o"), CompletedAt = r.IsDBNull(6) ? null : r.GetDateTime(6).ToString("o"), LastResumedAt = r.IsDBNull(7) ? null : r.GetDateTime(7).ToString("o"), ErrorMessage = r.IsDBNull(8) ? null : r.GetString(8), RankingsPending = !r.IsDBNull(9) && r.GetBoolean(9), DeferredReason = r.IsDBNull(10) ? null : r.GetString(10) };
    private static HistoryReconStatusInfo ReadHistoryReconStatus(NpgsqlDataReader r) => new() { AccountId = r.GetString(0), Status = r.GetString(1), SongsProcessed = r.GetInt32(2), TotalSongsToProcess = r.GetInt32(3), SeasonsQueried = r.GetInt32(4), HistoryEntriesFound = r.GetInt32(5), StartedAt = r.IsDBNull(6) ? null : r.GetDateTime(6).ToString("o"), CompletedAt = r.IsDBNull(7) ? null : r.GetDateTime(7).ToString("o"), ErrorMessage = r.IsDBNull(8) ? null : r.GetString(8), ReconstructionVersion = r.GetInt32(9), WindowFingerprint = r.GetString(10), AdmissionRevision = r.GetInt64(11) };
    private static CompositeRankingDto ReadCompositeRanking(NpgsqlDataReader r) => new() { AccountId = r.GetString(0), InstrumentsPlayed = r.GetInt32(1), TotalSongsPlayed = r.GetInt32(2), CompositeRating = r.GetDouble(3), CompositeRank = r.GetInt32(4), GuitarAdjustedSkill = r.IsDBNull(5) ? null : r.GetDouble(5), GuitarSkillRank = r.IsDBNull(6) ? null : r.GetInt32(6), BassAdjustedSkill = r.IsDBNull(7) ? null : r.GetDouble(7), BassSkillRank = r.IsDBNull(8) ? null : r.GetInt32(8), DrumsAdjustedSkill = r.IsDBNull(9) ? null : r.GetDouble(9), DrumsSkillRank = r.IsDBNull(10) ? null : r.GetInt32(10), VocalsAdjustedSkill = r.IsDBNull(11) ? null : r.GetDouble(11), VocalsSkillRank = r.IsDBNull(12) ? null : r.GetInt32(12), ProGuitarAdjustedSkill = r.IsDBNull(13) ? null : r.GetDouble(13), ProGuitarSkillRank = r.IsDBNull(14) ? null : r.GetInt32(14), ProBassAdjustedSkill = r.IsDBNull(15) ? null : r.GetDouble(15), ProBassSkillRank = r.IsDBNull(16) ? null : r.GetInt32(16), ProVocalsAdjustedSkill = r.IsDBNull(17) ? null : r.GetDouble(17), ProVocalsSkillRank = r.IsDBNull(18) ? null : r.GetInt32(18), ProCymbalsAdjustedSkill = r.IsDBNull(19) ? null : r.GetDouble(19), ProCymbalsSkillRank = r.IsDBNull(20) ? null : r.GetInt32(20), ProDrumsAdjustedSkill = r.IsDBNull(21) ? null : r.GetDouble(21), ProDrumsSkillRank = r.IsDBNull(22) ? null : r.GetInt32(22), CompositeRatingWeighted = r.IsDBNull(23) ? null : r.GetDouble(23), CompositeRankWeighted = r.IsDBNull(24) ? null : r.GetInt32(24), CompositeRatingFcRate = r.IsDBNull(25) ? null : r.GetDouble(25), CompositeRankFcRate = r.IsDBNull(26) ? null : r.GetInt32(26), CompositeRatingTotalScore = r.IsDBNull(27) ? null : r.GetDouble(27), CompositeRankTotalScore = r.IsDBNull(28) ? null : r.GetInt32(28), CompositeRatingMaxScore = r.IsDBNull(29) ? null : r.GetDouble(29), CompositeRankMaxScore = r.IsDBNull(30) ? null : r.GetInt32(30), ComputedAt = r.GetDateTime(31).ToString("o") };
    private static string SoloFamilyRankColumn(string rankBy) => (rankBy ?? "adjusted").ToLowerInvariant() switch
    {
        "weighted" => "weighted_rank",
        "fcrate" or "fc" or "fc_rate" => "fc_rate_rank",
        "totalscore" or "total_score" => "total_score_rank",
        "maxscore" or "max_score" => "max_score_percent_rank",
        _ => "adjusted_skill_rank",
    };
    private static int GetSoloFamilyTotalAccounts(NpgsqlConnection conn, string scopeId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM solo_family_rankings WHERE scope_id = @scopeId";
        cmd.Parameters.AddWithValue("scopeId", scopeId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
    private static SoloFamilyRankingDto ReadSoloFamilyRanking(NpgsqlDataReader r, int totalRankedAccounts = 0) => new()
    {
        ScopeId = r.GetString(0),
        AccountId = r.GetString(1),
        SongsPlayed = r.GetInt32(2),
        TotalChartedSongs = r.GetInt32(3),
        Coverage = r.GetDouble(4),
        RawSkillRating = r.GetDouble(5),
        AdjustedSkillRating = r.GetDouble(6),
        AdjustedSkillRank = r.GetInt32(7),
        WeightedRating = r.GetDouble(8),
        WeightedRank = r.GetInt32(9),
        FcRate = r.GetDouble(10),
        FcRateRank = r.GetInt32(11),
        TotalScore = r.GetInt64(12),
        TotalScoreRank = r.GetInt32(13),
        MaxScorePercent = r.GetDouble(14),
        MaxScorePercentRank = r.GetInt32(15),
        FullComboCount = r.GetInt32(16),
        RawMaxScorePercent = r.IsDBNull(17) ? null : r.GetDouble(17),
        RawWeightedRating = r.IsDBNull(18) ? null : r.GetDouble(18),
        ComputedAt = r.GetDateTime(19).ToString("o"),
        TotalRankedAccounts = totalRankedAccounts,
    };
    private static ComboLeaderboardEntry ReadComboEntry(NpgsqlDataReader r) => new() { Rank = (int)r.GetInt64(0), AccountId = r.GetString(1), AdjustedRating = r.GetDouble(2), WeightedRating = r.GetDouble(3), FcRate = r.GetDouble(4), TotalScore = r.GetInt32(5), MaxScorePercent = r.GetDouble(6), SongsPlayed = r.GetInt32(7), FullComboCount = r.GetInt32(8), ComputedAt = r.GetDateTime(9).ToString("o") };
    private static BandTeamRankingDto ReadBandTeamRanking(NpgsqlDataReader r, int totalRankedTeams)
    {
        var teamMembers = r.GetFieldValue<string[]>(3);
        return new()
        {
            BandId = BandIdentity.CreateBandId(r.GetString(0), r.GetString(2)),
            BandType = r.GetString(0),
            ComboId = string.IsNullOrEmpty(r.GetString(1)) ? null : r.GetString(1),
            TeamKey = r.GetString(2),
            TeamMembers = teamMembers,
            Members = ReadBandTeamRankingMembers(teamMembers, r.FieldCount > 23 && !r.IsDBNull(23) ? r.GetString(23) : null),
            SongsPlayed = r.GetInt32(4),
            TotalChartedSongs = r.GetInt32(5),
            Coverage = r.GetDouble(6),
            RawSkillRating = r.GetDouble(7),
            AdjustedSkillRating = r.GetDouble(8),
            AdjustedSkillRank = r.GetInt32(9),
            WeightedRating = r.GetDouble(10),
            WeightedRank = r.GetInt32(11),
            FcRate = r.GetDouble(12),
            FcRateRank = r.GetInt32(13),
            TotalScore = r.GetInt64(14),
            TotalScoreRank = r.GetInt32(15),
            AvgAccuracy = r.GetDouble(16),
            FullComboCount = r.GetInt32(17),
            AvgStars = r.GetDouble(18),
            BestRank = r.GetInt32(19),
            AvgRank = r.GetDouble(20),
            RawWeightedRating = r.IsDBNull(21) ? null : r.GetDouble(21),
            ComputedAt = r.GetDateTime(22).ToString("o"),
            TotalRankedTeams = totalRankedTeams,
        };
    }

    private static List<PlayerBandMemberDto> ReadBandTeamRankingMembers(string[] teamMembers, string? memberInstrumentsJson)
    {
        var instrumentsByMember = ParseMemberInstrumentsJson(memberInstrumentsJson);
        return teamMembers.Select(accountId => new PlayerBandMemberDto
        {
            AccountId = accountId,
            Instruments = instrumentsByMember.TryGetValue(accountId, out var instruments) ? instruments : [],
        }).ToList();
    }

    private static void AttachBandRankingConfigurations(NpgsqlConnection conn, IReadOnlyCollection<BandTeamRankingDto> rankings, string bandType, string comboId)
    {
        if (!ShouldAttachBandRankingConfigurations(bandType, comboId) || rankings.Count == 0)
            return;

        var rawCombos = BandComboIds.ToEpicRawComboCandidates(comboId).ToArray();
        if (rawCombos.Length == 0)
            return;

        var rankingsByTeamKey = rankings
            .GroupBy(static ranking => ranking.TeamKey, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);
        var teamKeys = rankingsByTeamKey.Keys.ToArray();
        if (teamKeys.Length == 0)
            return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT team_key, instrument_combo, assignment_key, appearance_count, member_assignments_json::text
            FROM {BandLeaderboardPersistence.BandTeamConfigurationTable}
            WHERE band_type = @bandType
              AND team_key = ANY(@teamKeys)
              AND instrument_combo = ANY(@rawCombos)
            ORDER BY team_key, appearance_count DESC, assignment_key
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("teamKeys", NpgsqlDbType.Array | NpgsqlDbType.Text, teamKeys);
        cmd.Parameters.AddWithValue("rawCombos", NpgsqlDbType.Array | NpgsqlDbType.Text, rawCombos);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var teamKey = reader.GetString(0);
            if (!rankingsByTeamKey.TryGetValue(teamKey, out var matchingRankings))
                continue;

            var rawCombo = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var observedComboId = BandComboIds.FromEpicRawCombo(rawCombo);
            var configuration = new BandConfigurationDto
            {
                RawInstrumentCombo = rawCombo,
                ComboId = observedComboId,
                Instruments = BandComboIds.ToInstruments(observedComboId).ToList(),
                AssignmentKey = reader.GetString(2),
                AppearanceCount = reader.GetInt32(3),
                MemberInstruments = ParseMemberAssignmentJson(reader.IsDBNull(4) ? "{}" : reader.GetString(4)),
            };

            foreach (var ranking in matchingRankings)
                ranking.Configurations.Add(configuration);
        }
    }

    private static bool ShouldAttachBandRankingConfigurations(string bandType, string comboId) =>
        !string.IsNullOrWhiteSpace(comboId)
        && string.Equals(bandType, "Band_Duets", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, List<string>> ParseMemberInstrumentsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var instruments = property.Value.ValueKind == JsonValueKind.Array
                ? property.Value.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString())
                    .Where(static instrument => !string.IsNullOrWhiteSpace(instrument))
                    .Select(static instrument => instrument!)
                    .ToList()
                : [];

            result[property.Name] = instruments;
        }

        return result;
    }

    private static Dictionary<string, string> ParseMemberAssignmentJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;

            var instrument = property.Value.GetString();
            if (!string.IsNullOrWhiteSpace(instrument))
                result[property.Name] = instrument;
        }

        return result;
    }
    private static (string Column, string Direction) RankByColumn(string rankBy) => rankBy.ToLowerInvariant() switch { "weighted" => ("weighted_rating", "ASC"), "fcrate" => ("fc_rate", "DESC"), "totalscore" => ("total_score", "DESC"), "maxscore" => ("max_score_percent", "DESC"), _ => ("adjusted_rating", "ASC") };
    private static string ComboRankOrderBy(string rankBy) { var (col, dir) = RankByColumn(rankBy); return rankBy.Equals("fcrate", StringComparison.OrdinalIgnoreCase) ? $"{col} {dir}, total_score DESC, songs_played DESC, account_id ASC" : $"{col} {dir}, songs_played DESC, account_id ASC"; }
    private static string ComboRankPrecedesPredicate(string rankBy)
    {
        var (column, direction) = RankByColumn(rankBy);
        if (rankBy.Equals("fcrate", StringComparison.OrdinalIgnoreCase))
        {
            return """
                other.fc_rate > target.fc_rate
                OR (other.fc_rate = target.fc_rate AND other.total_score > target.total_score)
                OR (other.fc_rate = target.fc_rate AND other.total_score = target.total_score AND other.songs_played > target.songs_played)
                OR (other.fc_rate = target.fc_rate AND other.total_score = target.total_score AND other.songs_played = target.songs_played AND other.account_id < target.account_id)
                """;
        }

        var comparison = direction.Equals("ASC", StringComparison.OrdinalIgnoreCase) ? "<" : ">";
        return $"""
            other.{column} {comparison} target.{column}
            OR (other.{column} = target.{column} AND other.songs_played > target.songs_played)
            OR (other.{column} = target.{column} AND other.songs_played = target.songs_played AND other.account_id < target.account_id)
            """;
    }
    private static string BandRankColumn(string rankBy) => rankBy switch { "weighted" => "weighted_rank", "fcrate" => "fc_rate_rank", "totalscore" => "total_score_rank", _ => "adjusted_skill_rank" };
    private static List<RivalSongSampleRow> ReadRivalSamples(NpgsqlCommand cmd) { var list = new List<RivalSongSampleRow>(); using var r = cmd.ExecuteReader(); while (r.Read()) list.Add(ReadRivalSample(r)); return list; }
    private static RivalSongSampleRow ReadRivalSample(NpgsqlDataReader r) => new() { UserId = r.GetString(0), RivalAccountId = r.GetString(1), Instrument = r.GetString(2), SongId = r.GetString(3), UserRank = r.GetInt32(4), RivalRank = r.GetInt32(5), RankDelta = r.GetInt32(6), UserScore = r.IsDBNull(7) ? null : r.GetInt32(7), RivalScore = r.IsDBNull(8) ? null : r.GetInt32(8) };

    /// <summary>Parse an ISO 8601 string to UTC DateTime (required by Npgsql for TIMESTAMPTZ).</summary>
    private static DateTime ParseUtc(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    /// <summary>Write a nullable int to a binary importer, or NULL if absent.</summary>
    private static void WriteNullableInt(NpgsqlBinaryImporter writer, int? value)
    {
        if (value.HasValue) writer.Write(value.Value, NpgsqlDbType.Integer);
        else writer.WriteNull();
    }

    private static void WriteNullableReal(NpgsqlBinaryImporter writer, double? value)
    {
        if (value.HasValue) writer.Write((float)value.Value, NpgsqlDbType.Real);
        else writer.WriteNull();
    }

    public void Dispose() { }
}
