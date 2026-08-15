using System.Data;
using FSTService.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FSTService.Scraping;

public sealed class SoloFamilyRankingBackfillService
{
    internal const int DefaultSeparateReadCommandTimeoutSeconds = 30;
    internal const int DefaultMaintenanceStatementTimeoutSeconds = 30;
    internal const int DefaultReplacementStatementTimeoutSeconds = 180;
    private static readonly TimeSpan WorkerHeartbeatStaleAfter =
        TimeSpan.FromSeconds(90);
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly IMetaDatabase _metaDb;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<SoloFamilyRankingBackfillService> _log;
    private readonly IPathDataStore? _pathDataStore;
    private readonly int _separateReadCommandTimeoutSeconds;
    private readonly int _maintenanceStatementTimeoutSeconds;
    private readonly int _replacementStatementTimeoutSeconds;
    private readonly Action<NpgsqlConnection, NpgsqlTransaction>?
        _afterMaintenanceLocksAcquired;

    public SoloFamilyRankingBackfillService(
        GlobalLeaderboardPersistence persistence,
        IMetaDatabase metaDb,
        NpgsqlDataSource dataSource,
        ILogger<SoloFamilyRankingBackfillService> log,
        IPathDataStore? pathDataStore = null)
        : this(
            persistence,
            metaDb,
            dataSource,
            log,
            afterMaintenanceLocksAcquired: null,
            separateReadCommandTimeoutSeconds:
                DefaultSeparateReadCommandTimeoutSeconds,
            maintenanceStatementTimeoutSeconds:
                DefaultMaintenanceStatementTimeoutSeconds,
            replacementStatementTimeoutSeconds:
                DefaultReplacementStatementTimeoutSeconds,
            pathDataStore: pathDataStore)
    {
    }

    internal SoloFamilyRankingBackfillService(
        GlobalLeaderboardPersistence persistence,
        IMetaDatabase metaDb,
        NpgsqlDataSource dataSource,
        ILogger<SoloFamilyRankingBackfillService> log,
        Action<NpgsqlConnection, NpgsqlTransaction>?
            afterMaintenanceLocksAcquired,
        int separateReadCommandTimeoutSeconds =
            DefaultSeparateReadCommandTimeoutSeconds,
        int maintenanceStatementTimeoutSeconds =
            DefaultMaintenanceStatementTimeoutSeconds,
        int replacementStatementTimeoutSeconds =
            DefaultReplacementStatementTimeoutSeconds,
        IPathDataStore? pathDataStore = null)
    {
        if (separateReadCommandTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(separateReadCommandTimeoutSeconds),
                "Separate maintenance reads require a finite positive " +
                "command timeout.");
        }
        if (maintenanceStatementTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maintenanceStatementTimeoutSeconds),
                "Maintenance statements require a finite positive timeout.");
        }
        if (replacementStatementTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(replacementStatementTimeoutSeconds),
                "Replacement statements require a finite positive timeout.");
        }

        _persistence = persistence;
        _metaDb = metaDb;
        _dataSource = dataSource;
        _log = log;
        _pathDataStore = pathDataStore;
        _separateReadCommandTimeoutSeconds =
            separateReadCommandTimeoutSeconds;
        _maintenanceStatementTimeoutSeconds =
            maintenanceStatementTimeoutSeconds;
        _replacementStatementTimeoutSeconds =
            replacementStatementTimeoutSeconds;
        _afterMaintenanceLocksAcquired =
            afterMaintenanceLocksAcquired;
    }

    public SoloFamilyRankingBackfillResult Rebuild(bool execute)
    {
        using var maintenanceConnection = _dataSource.OpenConnection();
        try
        {
            using var stateTransaction = maintenanceConnection.BeginTransaction(
                IsolationLevel.ReadCommitted);
            ConfigureStateTransaction(
                maintenanceConnection,
                stateTransaction,
                _maintenanceStatementTimeoutSeconds);
            AcquireMaintenanceLock(
                maintenanceConnection,
                stateTransaction);

            var state = ReadMaintenanceState(
                maintenanceConnection,
                stateTransaction);
            var runtime = _metaDb.GetServiceRuntimeState(
                WorkerStatusPublisher.ScraperWorkerKey,
                _separateReadCommandTimeoutSeconds);
            ValidateMaintenanceState(state, runtime);
            LockCanonicalRankings(
                maintenanceConnection,
                stateTransaction);
            _afterMaintenanceLocksAcquired?.Invoke(
                maintenanceConnection,
                stateTransaction);

            var catalogSongs = SongCatalogSnapshotBuilder.DeserializeCatalog(
                state.CatalogJson);
            if (catalogSongs.Count != state.CatalogSongCount)
            {
                throw new InvalidOperationException(
                    "Solo-family ranking backfill safety gate failed: the " +
                    $"published catalog declared {state.CatalogSongCount:N0} " +
                    $"song(s) but deserialized {catalogSongs.Count:N0}.");
            }

            var perInstrument = new Dictionary<
                string,
                Dictionary<string, RankingsCalculator.AccountMetrics>>(
                StringComparer.OrdinalIgnoreCase);
            var catalogDenominators = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
            var sourceRowsByInstrument = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
            var pathGenerationStates =
                _pathDataStore?.GetPathGenerationStates()
                ?? new Dictionary<string, PathGenerationState>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var instrument in GlobalLeaderboardScraper.AllInstruments)
            {
                var db = _persistence.GetOrCreateInstrumentDb(instrument);
                var rows = db.GetAllRankingSummariesDetailed(
                    _separateReadCommandTimeoutSeconds);
                var instrumentRows = new Dictionary<
                    string,
                    RankingsCalculator.AccountMetrics>(
                    StringComparer.OrdinalIgnoreCase);

                foreach (var summary in rows)
                {
                    instrumentRows[summary.AccountId] =
                        new RankingsCalculator.AccountMetrics(
                            summary.AdjustedSkillRating,
                            summary.WeightedRating,
                            summary.FcRate,
                            summary.TotalScore,
                            summary.MaxScorePercent,
                            summary.SongsPlayed,
                            summary.FullComboCount,
                            summary.TotalChartedSongs,
                            summary.RawSkillRating,
                            summary.RawWeightedRating,
                            summary.RawMaxScorePercent);
                }

                perInstrument[instrument] = instrumentRows;
                sourceRowsByInstrument[instrument] = instrumentRows.Count;
                catalogDenominators[instrument] =
                    RankingsCalculator.CountChartedSongs(
                        catalogSongs,
                        instrument,
                        pathGenerationStates);
            }

            var build = SoloFamilyRankingBuilder.BuildRankings(
                SoloFamilyRankingScopes.All,
                perInstrument,
                catalogDenominators,
                RankingsCalculator.CredibilityThreshold,
                RankingsCalculator.PopulationMedian);

            var canonicalDenominators = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
            var effectiveDenominators = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var denominator in build.InstrumentDenominators)
            {
                canonicalDenominators[denominator.Instrument] =
                    denominator.CanonicalDenominator;
                effectiveDenominators[denominator.Instrument] =
                    denominator.EffectiveDenominator;

                if (denominator.IsOverride)
                {
                    _log.LogWarning(
                        "Solo family ranking denominator override for {Instrument}: catalog={CatalogDenominator:N0}, canonical={CanonicalDenominator:N0}, effective={EffectiveDenominator:N0}.",
                        denominator.Instrument,
                        denominator.CatalogDenominator,
                        denominator.CanonicalDenominator,
                        denominator.EffectiveDenominator);
                }
            }

            var groupedScopeRows = build.Rankings
                .GroupBy(
                    row => row.ScopeId,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count(),
                    StringComparer.OrdinalIgnoreCase);
            var scopeRows = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var scope in SoloFamilyRankingScopes.All)
            {
                scopeRows[scope.ScopeId] =
                    groupedScopeRows.GetValueOrDefault(scope.ScopeId);
            }

            var shouldExecute = execute && build.InvalidRowCount == 0;
            if (shouldExecute)
            {
                SetStatementTimeout(
                    maintenanceConnection,
                    stateTransaction,
                    _replacementStatementTimeoutSeconds);
                _metaDb.ReplaceSoloFamilyRankings(
                    build.Rankings,
                    maintenanceConnection,
                    stateTransaction);
                SetStatementTimeout(
                    maintenanceConnection,
                    stateTransaction,
                    _maintenanceStatementTimeoutSeconds);
            }

            stateTransaction.Commit();
            if (shouldExecute)
            {
                _log.LogInformation(
                    "Rebuilt {Count:N0} solo family ranking rows across {Scopes:N0} scope(s) for published scrape {PublishedScrapeId}.",
                    build.Rankings.Count,
                    scopeRows.Count,
                    state.PublishedScrapeId);
            }
            else if (build.InvalidRowCount > 0)
            {
                _log.LogError(
                    "Solo family ranking backfill produced {InvalidRowCount:N0} publication-incompatible row(s); no rows were replaced.",
                    build.InvalidRowCount);
            }
            else
            {
                _log.LogInformation(
                    "Solo family ranking backfill dry run produced {Count:N0} rows across {Scopes:N0} scope(s); no rows were replaced.",
                    build.Rankings.Count,
                    scopeRows.Count);
            }

            return new SoloFamilyRankingBackfillResult(
                state.PublishedScrapeId,
                state.CurrentPublicationId,
                build.Rankings.Count,
                sourceRowsByInstrument,
                catalogDenominators,
                canonicalDenominators,
                effectiveDenominators,
                build.ScopeDenominators,
                scopeRows,
                build.InvalidRowCount,
                shouldExecute);
        }
        catch (PostgresException ex) when (ex.SqlState is
            PostgresErrorCodes.UndefinedTable or
            PostgresErrorCodes.UndefinedColumn)
        {
            throw new InvalidOperationException(
                "Solo-family ranking backfill requires the existing release " +
                "schema and will not initialize or repair it.",
                ex);
        }
    }

    private static void ConfigureStateTransaction(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int statementTimeoutSeconds)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SET LOCAL idle_in_transaction_session_timeout = 0;
            SET LOCAL lock_timeout = '5s';
            SELECT set_config(
                'statement_timeout',
                @statementTimeout,
                TRUE);
            """;
        command.Parameters.AddWithValue(
            "statementTimeout",
            $"{statementTimeoutSeconds}s");
        command.ExecuteNonQuery();
    }

    private static void SetStatementTimeout(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int statementTimeoutSeconds)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT set_config(
                'statement_timeout',
                @statementTimeout,
                TRUE)
            """;
        command.Parameters.AddWithValue(
            "statementTimeout",
            $"{statementTimeoutSeconds}s");
        command.ExecuteNonQuery();
    }

    private static void LockCanonicalRankings(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "LOCK TABLE account_rankings IN SHARE MODE";
        command.ExecuteNonQuery();
    }

    private static SoloFamilyRankingMaintenanceState ReadMaintenanceState(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                publication.current_publication_id,
                publication.previous_publication_id,
                publication.working_publication_id,
                publication.published_scrape_id,
                publication.published_at,
                publication.public_reads_frozen,
                generation.scrape_id,
                generation.status,
                generation.published_at,
                published_scrape.status,
                published_scrape.completed_at IS NOT NULL,
                (
                    SELECT COUNT(*)
                    FROM scrape_log active_scrape
                    WHERE active_scrape.status = 'running'
                ),
                catalog.catalog_version,
                catalog.schema_version,
                catalog.catalog_json::text,
                catalog.content_hash,
                catalog.song_count,
                catalog.is_exact,
                catalog.source_kind,
                binding.binding_kind,
                binding.status,
                binding.row_count,
                binding.content_hash
            FROM scrape_publication_state publication
            LEFT JOIN publication_generations generation
              ON generation.publication_id =
                 publication.current_publication_id
            LEFT JOIN scrape_log published_scrape
              ON published_scrape.id = publication.published_scrape_id
            LEFT JOIN publication_song_catalog catalog
              ON catalog.publication_id =
                 publication.current_publication_id
            LEFT JOIN publication_surface_bindings binding
              ON binding.publication_id =
                 publication.current_publication_id
             AND binding.surface_name = 'song_catalog'
            WHERE publication.id = TRUE
            FOR SHARE OF publication
            """;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                "Solo-family ranking backfill safety gate failed: the " +
                "publication-state singleton is missing.");
        }

        return new SoloFamilyRankingMaintenanceState(
            reader.IsDBNull(0) ? null : reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            reader.GetBoolean(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetDateTime(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            !reader.IsDBNull(10) && reader.GetBoolean(10),
            reader.GetInt64(11),
            reader.IsDBNull(12) ? null : reader.GetInt64(12),
            reader.IsDBNull(13) ? null : reader.GetInt32(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetInt32(16),
            !reader.IsDBNull(17) && reader.GetBoolean(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.IsDBNull(21) ? null : reader.GetInt64(21),
            reader.IsDBNull(22) ? null : reader.GetString(22));
    }

    private static void ValidateMaintenanceState(
        SoloFamilyRankingMaintenanceState state,
        ServiceRuntimeState runtime)
    {
        var failures = new List<string>();
        if (state.ActiveScrapeCount != 0)
            failures.Add($"{state.ActiveScrapeCount:N0} scrape run(s) are active");
        if (runtime.LatestScrape?.Status.Equals(
                "running",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            failures.Add(
                $"latest scrape {runtime.LatestScrape.Id} is still running");
        }
        if (!IsWorkerOfflineOrStale(
                runtime.WorkerStatus,
                DateTime.UtcNow,
                out var workerLiveness))
        {
            failures.Add(
                $"worker ledger is live ({workerLiveness})");
        }
        if (state.PublicReadsFrozen || runtime.PublicReadFreeze.IsFrozen)
            failures.Add("public reads are frozen");
        if (state.WorkingPublicationId is not null)
        {
            failures.Add(
                $"working publication {state.WorkingPublicationId} is active");
        }
        if (state.CurrentPublicationIdValue is null
            || state.PublishedScrapeIdValue is null
            || state.PublishedAtUtc is null)
        {
            failures.Add("the current published pointer is incomplete");
        }
        if (state.GenerationScrapeId != state.PublishedScrapeIdValue
            || !string.Equals(
                state.GenerationStatus,
                PublicationGenerationStatus.Current,
                StringComparison.Ordinal)
            || state.GenerationPublishedAtUtc is null)
        {
            failures.Add(
                "the current publication generation is not stable");
        }
        if (runtime.PublishedScrape?.Id != state.PublishedScrapeIdValue)
        {
            failures.Add(
                "the runtime published scrape does not match the publication pointer");
        }
        if (!string.Equals(
                state.PublishedScrapeStatus,
                "completed",
                StringComparison.Ordinal)
            || !state.PublishedScrapeCompleted)
        {
            failures.Add("the published scrape is not completed");
        }
        if (state.CatalogVersion is null
            || state.CatalogSchemaVersion !=
                SongCatalogSnapshotBuilder.SchemaVersion
            || string.IsNullOrWhiteSpace(state.CatalogJsonValue)
            || string.IsNullOrWhiteSpace(state.CatalogHash)
            || state.CatalogSongCountValue is null
            || !state.CatalogIsExact
            || !string.Equals(
                state.CatalogSourceKind,
                "provider_exact",
                StringComparison.Ordinal)
            || !string.Equals(
                state.CatalogBindingKind,
                "generation_catalog_snapshot",
                StringComparison.Ordinal)
            || !string.Equals(
                state.CatalogBindingStatus,
                "ready",
                StringComparison.Ordinal)
            || state.CatalogBindingRowCount != state.CatalogSongCountValue
            || !string.Equals(
                state.CatalogBindingHash,
                state.CatalogHash,
                StringComparison.Ordinal))
        {
            failures.Add(
                "the current publication catalog binding is not exact and ready");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Solo-family ranking backfill safety gate failed: " +
                string.Join("; ", failures) +
                ".");
        }
    }

    private static bool IsWorkerOfflineOrStale(
        WorkerStatusInfo? worker,
        DateTime nowUtc,
        out string description)
    {
        if (worker is null)
        {
            description = "no worker ledger row";
            return true;
        }

        var rawStatus = worker.Status.Trim().ToLowerInvariant();
        if (rawStatus is "offline" or "stale")
        {
            description = $"status={rawStatus}";
            return true;
        }

        if (worker.LastHeartbeatAtUtc is { } heartbeat)
        {
            var age = nowUtc - heartbeat;
            if (age > WorkerHeartbeatStaleAfter)
            {
                description =
                    $"status={rawStatus}, heartbeatAge={age.TotalSeconds:N0}s";
                return true;
            }

            description =
                $"status={rawStatus}, heartbeatAge={Math.Max(0, age.TotalSeconds):N0}s";
            return false;
        }

        description = $"status={rawStatus}, heartbeat=missing";
        return false;
    }

    private static void AcquireMaintenanceLock(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 5;
        command.CommandText =
            "SELECT pg_try_advisory_xact_lock(@lockKey)";
        command.Parameters.AddWithValue(
            "lockKey",
            PublicationGenerationSchema.AdvisoryLockKey);
        if (command.ExecuteScalar() is not true)
        {
            throw new InvalidOperationException(
                "Solo-family ranking backfill safety gate failed: the " +
                "global publication/maintenance lock is busy.");
        }
    }

    private sealed record SoloFamilyRankingMaintenanceState(
        long? CurrentPublicationIdValue,
        long? PreviousPublicationId,
        long? WorkingPublicationId,
        long? PublishedScrapeIdValue,
        DateTime? PublishedAtUtc,
        bool PublicReadsFrozen,
        long? GenerationScrapeId,
        string? GenerationStatus,
        DateTime? GenerationPublishedAtUtc,
        string? PublishedScrapeStatus,
        bool PublishedScrapeCompleted,
        long ActiveScrapeCount,
        long? CatalogVersion,
        int? CatalogSchemaVersion,
        string? CatalogJsonValue,
        string? CatalogHash,
        int? CatalogSongCountValue,
        bool CatalogIsExact,
        string? CatalogSourceKind,
        string? CatalogBindingKind,
        string? CatalogBindingStatus,
        long? CatalogBindingRowCount,
        string? CatalogBindingHash)
    {
        public long CurrentPublicationId =>
            CurrentPublicationIdValue
            ?? throw new InvalidOperationException(
                "Current publication ID was not validated.");

        public long PublishedScrapeId =>
            PublishedScrapeIdValue
            ?? throw new InvalidOperationException(
                "Published scrape ID was not validated.");

        public string CatalogJson =>
            CatalogJsonValue
            ?? throw new InvalidOperationException(
                "Published catalog JSON was not validated.");

        public int CatalogSongCount =>
            CatalogSongCountValue
            ?? throw new InvalidOperationException(
                "Published catalog count was not validated.");
    }
}

public sealed record SoloFamilyRankingBackfillResult(
    long PublishedScrapeId,
    long CurrentPublicationId,
    int TotalRows,
    IReadOnlyDictionary<string, int> SourceRowsByInstrument,
    IReadOnlyDictionary<string, int> CatalogDenominatorsByInstrument,
    IReadOnlyDictionary<string, int> CanonicalDenominatorsByInstrument,
    IReadOnlyDictionary<string, int> EffectiveDenominatorsByInstrument,
    IReadOnlyDictionary<string, int> ScopeDenominators,
    IReadOnlyDictionary<string, int> ScopeRows,
    int InvalidRowCount,
    bool Executed);
