using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using FSTService.Scraping;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

internal sealed record MaxScoreMaintenanceScoreHistorySnapshot(
    MaxScoreMaintenanceScoreHistoryEvidence Evidence,
    IReadOnlyList<string> AffectedPlayerStatsAccounts,
    IReadOnlyList<string> AffectedRegisteredAccounts,
    IReadOnlyList<string> OverlayOnlyRegisteredAccounts);

internal static class MaxScoreMaintenanceScoreHistoryEvidenceCalculator
{
    internal const string PublicationTable =
        "fst_max_score_evidence_publication";
    internal const string MaximaTable =
        "fst_max_score_evidence_maxima";
    internal const string SourcesTable =
        "fst_max_score_evidence_sources";
    internal const string AffectedAccountsTable =
        "fst_max_score_evidence_affected_accounts";
    internal const string RegisteredAccountsTable =
        "fst_max_score_evidence_registered_accounts";
    internal const string FallbackScopesTable =
        "fst_max_score_evidence_fallback_scopes";

    internal static readonly IReadOnlyList<string> SelectorTableNames =
    [
        PublicationTable,
        MaximaTable,
        SourcesTable,
        AffectedAccountsTable,
        RegisteredAccountsTable,
        FallbackScopesTable,
    ];

    private const string EvidenceStage =
        "complete-score-history-evidence";

    private const string PrepareSelectorsSql = """
        CREATE TEMP TABLE fst_max_score_evidence_publication (
            publication_id BIGINT PRIMARY KEY,
            published_scrape_id BIGINT NOT NULL UNIQUE
        ) ON COMMIT DROP;

        CREATE TEMP TABLE fst_max_score_evidence_maxima (
            song_id TEXT NOT NULL,
            instrument TEXT NOT NULL,
            max_score INTEGER NOT NULL,
            max_threshold INTEGER NOT NULL,
            is_changed BOOLEAN NOT NULL,
            is_affected_instrument BOOLEAN NOT NULL,
            PRIMARY KEY (song_id, instrument)
        ) ON COMMIT DROP;

        CREATE TEMP TABLE fst_max_score_evidence_sources (
            published_scrape_id BIGINT NOT NULL,
            song_id TEXT NOT NULL,
            instrument TEXT NOT NULL,
            source_kind TEXT NOT NULL,
            source_snapshot_id BIGINT,
            source_scrape_id BIGINT NOT NULL,
            is_complete BOOLEAN NOT NULL,
            PRIMARY KEY (song_id, instrument)
        ) ON COMMIT DROP;

        CREATE TEMP TABLE fst_max_score_evidence_affected_accounts (
            account_id TEXT PRIMARY KEY,
            is_registered BOOLEAN NOT NULL DEFAULT FALSE,
            is_overlay_only BOOLEAN NOT NULL DEFAULT FALSE
        ) ON COMMIT DROP;

        CREATE TEMP TABLE fst_max_score_evidence_registered_accounts (
            account_id TEXT NOT NULL
        ) ON COMMIT DROP;

        CREATE TEMP TABLE fst_max_score_evidence_fallback_scopes (
            song_id TEXT NOT NULL,
            instrument TEXT NOT NULL,
            account_id TEXT NOT NULL,
            max_threshold INTEGER NOT NULL,
            PRIMARY KEY (song_id, instrument, account_id)
        ) ON COMMIT DROP;

        INSERT INTO fst_max_score_evidence_maxima (
            song_id,
            instrument,
            max_score,
            max_threshold,
            is_changed,
            is_affected_instrument)
        SELECT *
        FROM unnest(
            @songIds::TEXT[],
            @instruments::TEXT[],
            @maxScores::INTEGER[],
            @maxThresholds::INTEGER[],
            @changed::BOOLEAN[],
            @affectedInstruments::BOOLEAN[]);

        INSERT INTO fst_max_score_evidence_publication (
            publication_id,
            published_scrape_id)
        SELECT state.current_publication_id,
               state.published_scrape_id
        FROM scrape_publication_state state
        JOIN publication_generations generation
          ON generation.publication_id =
                 state.current_publication_id
         AND generation.scrape_id =
                 state.published_scrape_id
         AND generation.status = 'current'
        WHERE state.id = TRUE
          AND state.current_publication_id =
                  @publicationId
          AND state.published_scrape_id =
                  @publishedScrapeId
          AND state.working_publication_id IS NULL;

        INSERT INTO fst_max_score_evidence_sources (
            published_scrape_id,
            song_id,
            instrument,
            source_kind,
            source_snapshot_id,
            source_scrape_id,
            is_complete)
        SELECT source.published_scrape_id,
               source.song_id,
               source.instrument,
               source.source_kind,
               source.source_snapshot_id,
               source.source_scrape_id,
               source.is_complete
        FROM leaderboard_published_scope_source source
        JOIN fst_max_score_evidence_publication publication
          ON publication.published_scrape_id =
             source.published_scrape_id
        WHERE source.scope_kind = 'alltime';

        INSERT INTO fst_max_score_evidence_registered_accounts (
            account_id)
        SELECT account_id
        FROM registered_users;

        CREATE INDEX
            fst_max_score_evidence_registered_accounts_account_idx
        ON fst_max_score_evidence_registered_accounts (
            account_id);

        ANALYZE fst_max_score_evidence_maxima;
        ANALYZE fst_max_score_evidence_sources;
        ANALYZE fst_max_score_evidence_registered_accounts;
        """;

    private const string ValidateSelectorsSql = """
        SELECT
            (
                SELECT COUNT(*)::INTEGER
                FROM fst_max_score_evidence_publication
            ) AS publication_count,
            (
                SELECT COUNT(*)::INTEGER
                FROM fst_max_score_evidence_sources
            ) AS source_count,
            COALESCE(
                (
                    SELECT BOOL_AND(source.is_complete)
                    FROM fst_max_score_evidence_sources source
                ),
                FALSE) AS sources_complete,
            EXISTS (
                SELECT 1
                FROM fst_max_score_evidence_publication publication
                JOIN publication_surface_bindings binding
                  ON binding.publication_id =
                         publication.publication_id
                 AND binding.surface_name =
                         'solo_scope_sources'
                 AND binding.binding_kind = 'scrape_id'
                 AND binding.status = 'ready'
                 AND binding.binding_json ->> 'table' =
                         'leaderboard_published_scope_source'
                 AND binding.binding_json ->>
                         'publicationId' =
                         publication.publication_id::TEXT
                 AND binding.binding_json ->>
                         'publishedScrapeId' =
                         publication.published_scrape_id::TEXT
                 AND binding.row_count = (
                         SELECT COUNT(*)::BIGINT
                         FROM fst_max_score_evidence_sources
                     )
            ) AS source_binding_ready,
            (
                SELECT COUNT(*)::INTEGER
                FROM fst_max_score_evidence_maxima maxima
                WHERE maxima.is_changed
                  AND NOT EXISTS (
                      SELECT 1
                      FROM fst_max_score_evidence_sources source
                      WHERE source.song_id = maxima.song_id
                        AND source.instrument =
                            maxima.instrument
                  )
            ) AS missing_changed_scope_count,
            (
                SELECT COUNT(*)::INTEGER
                FROM fst_max_score_evidence_sources source
                JOIN fst_max_score_evidence_maxima maxima
                  ON maxima.song_id = source.song_id
                 AND maxima.instrument = source.instrument
            ) AS usable_scope_count;
        """;

    private const string LoadScopeSourcesSql = """
        SELECT source.song_id,
               source.instrument,
               source.source_kind,
               source.source_snapshot_id,
               maxima.max_score,
               maxima.max_threshold,
               maxima.is_changed,
               maxima.is_affected_instrument
        FROM fst_max_score_evidence_sources source
        JOIN fst_max_score_evidence_maxima maxima
          ON maxima.song_id = source.song_id
         AND maxima.instrument = source.instrument
        ORDER BY source.instrument,
                 source.song_id;
        """;

    internal const string ChangedSnapshotAccountsProbeSql = """
        INSERT INTO fst_max_score_evidence_affected_accounts (
            account_id,
            is_overlay_only)
        SELECT snapshot.account_id,
               FALSE
        FROM leaderboard_entries_snapshot snapshot
        WHERE snapshot.snapshot_id = @sourceSnapshotId
          AND snapshot.song_id = @songId
          AND snapshot.instrument = @instrument
        ON CONFLICT (account_id) DO NOTHING;
        """;

    internal const string ChangedOverlayAccountsProbeSql = """
        INSERT INTO fst_max_score_evidence_affected_accounts
            AS affected (
            account_id,
            is_overlay_only)
        SELECT overlay.account_id,
               NOT EXISTS (
                   SELECT 1
                   FROM leaderboard_entries_snapshot snapshot
                   WHERE snapshot.snapshot_id =
                             @sourceSnapshotId
                     AND snapshot.song_id = @songId
                     AND snapshot.instrument = @instrument
                     AND snapshot.account_id =
                             overlay.account_id
               )
        FROM leaderboard_entries_overlay overlay
        WHERE overlay.song_id = @songId
          AND overlay.instrument = @instrument
        ON CONFLICT (account_id) DO UPDATE
        SET is_overlay_only =
                affected.is_overlay_only
                OR EXCLUDED.is_overlay_only;
        """;

    internal const string RankingSnapshotProbeSql = """
        INSERT INTO fst_max_score_evidence_fallback_scopes (
            song_id,
            instrument,
            account_id,
            max_threshold)
        SELECT @songId,
               @instrument,
               snapshot.account_id,
               @maxThreshold
        FROM leaderboard_entries_snapshot snapshot
        WHERE snapshot.snapshot_id = @sourceSnapshotId
          AND snapshot.song_id = @songId
          AND snapshot.instrument = @instrument
          AND snapshot.score > @scoreThreshold
          AND NOT EXISTS (
              SELECT 1
              FROM leaderboard_entries_overlay overlay
              WHERE overlay.song_id = @songId
                AND overlay.instrument = @instrument
                AND overlay.account_id =
                        snapshot.account_id
          )
        ORDER BY snapshot.score DESC
        ON CONFLICT (
            song_id,
            instrument,
            account_id) DO NOTHING;
        """;

    internal const string RankingOverlayProbeSql = """
        INSERT INTO fst_max_score_evidence_fallback_scopes (
            song_id,
            instrument,
            account_id,
            max_threshold)
        SELECT @songId,
               @instrument,
               overlay.account_id,
               @maxThreshold
        FROM leaderboard_entries_overlay overlay
        WHERE overlay.song_id = @songId
          AND overlay.instrument = @instrument
          AND overlay.score > @scoreThreshold
        ON CONFLICT (
            song_id,
            instrument,
            account_id) DO NOTHING;
        """;

    internal const string PlayerSnapshotProbeSql = """
        INSERT INTO fst_max_score_evidence_fallback_scopes (
            song_id,
            instrument,
            account_id,
            max_threshold)
        SELECT @songId,
               @instrument,
               snapshot.account_id,
               @maxThreshold
        FROM leaderboard_entries_snapshot snapshot
        JOIN fst_max_score_evidence_affected_accounts affected
          ON affected.account_id = snapshot.account_id
        WHERE snapshot.snapshot_id = @sourceSnapshotId
          AND snapshot.song_id = @songId
          AND snapshot.instrument = @instrument
          AND snapshot.score > @scoreThreshold
          AND NOT EXISTS (
              SELECT 1
              FROM leaderboard_entries_overlay overlay
              WHERE overlay.song_id = @songId
                AND overlay.instrument = @instrument
                AND overlay.account_id =
                        snapshot.account_id
          )
        ORDER BY snapshot.score DESC
        ON CONFLICT (
            song_id,
            instrument,
            account_id) DO NOTHING;
        """;

    internal const string PlayerOverlayProbeSql = """
        INSERT INTO fst_max_score_evidence_fallback_scopes (
            song_id,
            instrument,
            account_id,
            max_threshold)
        SELECT @songId,
               @instrument,
               overlay.account_id,
               @maxThreshold
        FROM leaderboard_entries_overlay overlay
        JOIN fst_max_score_evidence_affected_accounts affected
          ON affected.account_id = overlay.account_id
        WHERE overlay.song_id = @songId
          AND overlay.instrument = @instrument
          AND overlay.score > @scoreThreshold
        ON CONFLICT (
            song_id,
            instrument,
            account_id) DO NOTHING;
        """;

    private const string FinalizeAffectedAccountsSql = """
        UPDATE fst_max_score_evidence_affected_accounts affected
        SET is_registered = TRUE
        FROM fst_max_score_evidence_registered_accounts registered
        WHERE registered.account_id = affected.account_id;

        ANALYZE fst_max_score_evidence_affected_accounts;
        """;

    private const string SeedAffectedAccountsSql = """
        INSERT INTO fst_max_score_evidence_affected_accounts (
            account_id,
            is_overlay_only)
        SELECT account_id,
               FALSE
        FROM unnest(@accountIds::TEXT[]) account_id
        ON CONFLICT (account_id) DO NOTHING;
        """;

    private const string AnalyzeFallbackScopesSql = """
        ANALYZE fst_max_score_evidence_fallback_scopes;
        """;

    private const string LoadAffectedAccountsSql = """
        SELECT account_id,
               is_registered,
               is_overlay_only
        FROM fst_max_score_evidence_affected_accounts
        ORDER BY account_id;
        """;

    private const string AggregateProjectionSql = """
        SELECT COUNT(*)::BIGINT AS row_count,
               MIN(history.id)::BIGINT AS minimum_id,
               MAX(history.id)::BIGINT AS maximum_id,
               MIN(history.changed_at) AS minimum_changed_at,
               MAX(history.changed_at) AS maximum_changed_at,
               COALESCE(
                   SUM(
                       hashtextextended(
                           jsonb_build_array(
                               history.id,
                               history.song_id,
                               history.instrument,
                               history.account_id,
                               history.old_score,
                               history.new_score,
                               history.old_rank,
                               history.new_rank,
                               history.accuracy,
                               history.is_full_combo,
                               history.stars,
                               history.percentile,
                               history.season,
                               CASE
                                   WHEN history.score_achieved_at
                                            IS NULL
                                       THEN NULL
                                   ELSE (
                                       EXTRACT(
                                           EPOCH FROM
                                               history.score_achieved_at)
                                       * 1000000)::BIGINT
                               END,
                               history.season_rank,
                               history.all_time_rank,
                               history.difficulty,
                               (
                                   EXTRACT(
                                       EPOCH FROM
                                           history.changed_at)
                                   * 1000000)::BIGINT)::TEXT,
                           0)::NUMERIC),
                   0)::TEXT AS hash_sum,
               COALESCE(
                   bit_xor(
                       hashtextextended(
                           jsonb_build_array(
                               history.id,
                               history.song_id,
                               history.instrument,
                               history.account_id,
                               history.old_score,
                               history.new_score,
                               history.old_rank,
                               history.new_rank,
                               history.accuracy,
                               history.is_full_combo,
                               history.stars,
                               history.percentile,
                               history.season,
                               CASE
                                   WHEN history.score_achieved_at
                                            IS NULL
                                       THEN NULL
                                   ELSE (
                                       EXTRACT(
                                           EPOCH FROM
                                               history.score_achieved_at)
                                       * 1000000)::BIGINT
                               END,
                               history.season_rank,
                               history.all_time_rank,
                               history.difficulty,
                               (
                                   EXTRACT(
                                       EPOCH FROM
                                           history.changed_at)
                                   * 1000000)::BIGINT)::TEXT,
                           1)),
                   0)::TEXT AS hash_xor
        """;

    private const string RegisteredAggregateSql =
        AggregateProjectionSql +
        "\n" +
        """
        FROM score_history history
        JOIN fst_max_score_evidence_registered_accounts registered
          ON registered.account_id = history.account_id;
        """;

    private const string FallbackAggregateSql =
        AggregateProjectionSql +
        "\n" +
        """
        FROM fst_max_score_evidence_fallback_scopes fallback
        CROSS JOIN LATERAL (
            SELECT history_row.*
            FROM score_history history_row
            WHERE history_row.account_id =
                      fallback.account_id
              AND history_row.song_id = fallback.song_id
              AND history_row.instrument =
                  fallback.instrument
              AND history_row.new_score <=
                  fallback.max_threshold
        ) history
        WHERE NOT EXISTS (
            SELECT 1
            FROM fst_max_score_evidence_registered_accounts registered
            WHERE registered.account_id = history.account_id);
        """;

    private const string DropSelectorsSql = """
        DROP TABLE IF EXISTS
            fst_max_score_evidence_fallback_scopes,
            fst_max_score_evidence_affected_accounts,
            fst_max_score_evidence_registered_accounts,
            fst_max_score_evidence_sources,
            fst_max_score_evidence_maxima,
            fst_max_score_evidence_publication;
        """;

    internal static async Task<MaxScoreMaintenanceScoreHistorySnapshot>
        ComputeAsync(
            MaxScoreMaintenanceManifest manifest,
            IReadOnlyDictionary<string, SongMaxScores>
                postPromotionMaxScores,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct,
            int commandTimeoutSeconds,
            Action<string, int>? commandTimeoutConfigured = null,
            bool requirePositiveChangedMaxima = true,
            IReadOnlyCollection<string>? seededAffectedAccounts = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(postPromotionMaxScores);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The score-history evidence transaction must belong to the supplied connection.",
                nameof(transaction));
        }

        var changedPairs = manifest.Songs
            .SelectMany(song => song.ChangedInstruments.Select(
                instrument => (song.SongId, Instrument: instrument)))
            .ToHashSet();
        var affectedInstruments = manifest.Scope
            .ExpectedChangedInstruments
            .ToHashSet(StringComparer.Ordinal);
        var maxima = postPromotionMaxScores
            .SelectMany(song => GlobalLeaderboardScraper
                .AllInstruments.Select(instrument => (
                    SongId: song.Key,
                    Instrument: instrument,
                    Maximum: song.Value
                        .GetByInstrument(instrument))))
            .Where(item => item.Maximum is > 0)
            .Select(item => (
                item.SongId,
                item.Instrument,
                Maximum: item.Maximum!.Value,
                Threshold:
                    RankingsCalculator.ComputeMaxScoreThreshold(
                        item.Maximum.Value),
                Changed: changedPairs.Contains(
                    (item.SongId, item.Instrument)),
                AffectedInstrument:
                    affectedInstruments.Contains(
                        item.Instrument)))
            .OrderBy(item => item.SongId, StringComparer.Ordinal)
            .ThenBy(item => item.Instrument, StringComparer.Ordinal)
            .ToArray();
        if (maxima.Length == 0)
        {
            throw new InvalidOperationException(
                "Score-history evidence requires post-promotion maxima.");
        }
        var maximumPairs = maxima
            .Select(item => (item.SongId, item.Instrument))
            .ToHashSet();
        if (requirePositiveChangedMaxima
            && !changedPairs.IsSubsetOf(maximumPairs))
        {
            throw new InvalidOperationException(
                "Score-history evidence requires a positive post-promotion maximum for every changed scope.");
        }

        var savepointName =
            $"fst_score_history_evidence_{Guid.NewGuid():N}";
        using var deadline = new SharedCommandDeadline(
            commandTimeoutSeconds,
            ct,
            commandTimeoutConfigured);
        await transaction.SaveAsync(savepointName, ct);
        try
        {
            try
            {
                await PrepareSelectorsAsync(
                    maxima,
                    manifest.ExpectedPublicationId,
                    manifest.ExpectedPublishedScrapeId,
                    connection,
                    transaction,
                    deadline);
                var scopeSources = await LoadScopeSourcesAsync(
                    connection,
                    transaction,
                    deadline);
                await PopulateAffectedAccountsAsync(
                    scopeSources,
                    connection,
                    transaction,
                    deadline);
                await SeedAffectedAccountsAsync(
                    seededAffectedAccounts,
                    connection,
                    transaction,
                    deadline);
                await ExecuteSelectorNonQueryAsync(
                    FinalizeAffectedAccountsSql,
                    connection,
                    transaction,
                    deadline);
                await PopulateFallbackScopesAsync(
                    scopeSources.Where(scope =>
                        scope.AffectedInstrument),
                    RankingSnapshotProbeSql,
                    RankingOverlayProbeSql,
                    static scope => scope.MaximumThreshold,
                    connection,
                    transaction,
                    deadline);
                await PopulateFallbackScopesAsync(
                    scopeSources,
                    PlayerSnapshotProbeSql,
                    PlayerOverlayProbeSql,
                    static scope => scope.Maximum,
                    connection,
                    transaction,
                    deadline);
                await ExecuteSelectorNonQueryAsync(
                    AnalyzeFallbackScopesSql,
                    connection,
                    transaction,
                    deadline);
                var accounts = await LoadAffectedAccountsAsync(
                    connection,
                    transaction,
                    deadline);
                var registered = await AggregateAsync(
                    RegisteredAggregateSql,
                    connection,
                    transaction,
                    deadline);
                var fallback = await AggregateAsync(
                    FallbackAggregateSql,
                    connection,
                    transaction,
                    deadline);
                var evidence = Combine(registered, fallback);

                deadline.Token.ThrowIfCancellationRequested();
                await DropSelectorsAsync(
                    connection,
                    transaction);
                deadline.Token.ThrowIfCancellationRequested();
                await transaction.ReleaseAsync(
                    savepointName,
                    CancellationToken.None);
                return new MaxScoreMaintenanceScoreHistorySnapshot(
                    evidence,
                    accounts.Affected,
                    accounts.Registered,
                    accounts.OverlayOnly);
            }
            catch (OperationCanceledException ex)
                when (deadline.HasExpired
                      && !ct.IsCancellationRequested)
            {
                throw CreateTimeoutException(
                    commandTimeoutSeconds,
                    ex);
            }
            catch (NpgsqlException ex)
                when (!ct.IsCancellationRequested
                      && (
                          deadline.HasExpired
                          || ex.InnerException
                              is TimeoutException))
            {
                throw CreateTimeoutException(
                    commandTimeoutSeconds,
                    ex);
            }
        }
        catch
        {
            await RollbackToSavepointAsync(
                transaction,
                savepointName);
            throw;
        }
    }

    private static async Task SeedAffectedAccountsAsync(
        IReadOnlyCollection<string>? accountIds,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SharedCommandDeadline deadline)
    {
        if (accountIds is null || accountIds.Count == 0)
            return;

        var normalized = accountIds
            .Where(accountId =>
                !string.IsNullOrWhiteSpace(accountId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(accountId => accountId, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
            return;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        deadline.Configure(command);
        command.CommandText = SeedAffectedAccountsSql;
        command.Parameters.Add(
            "accountIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            normalized;
        await command.ExecuteNonQueryAsync(deadline.Token);
    }

    private static async Task PrepareSelectorsAsync(
        IReadOnlyList<(
            string SongId,
            string Instrument,
            int Maximum,
            int Threshold,
            bool Changed,
            bool AffectedInstrument)> maxima,
        long publicationId,
        long publishedScrapeId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SharedCommandDeadline deadline)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        deadline.Configure(command);
        command.CommandText = PrepareSelectorsSql;
        command.Parameters.Add(
            "songIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            maxima.Select(item => item.SongId).ToArray();
        command.Parameters.Add(
            "instruments",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            maxima.Select(item => item.Instrument).ToArray();
        command.Parameters.Add(
            "maxScores",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            maxima.Select(item => item.Maximum).ToArray();
        command.Parameters.Add(
            "maxThresholds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            maxima.Select(item => item.Threshold).ToArray();
        command.Parameters.Add(
            "changed",
            NpgsqlDbType.Array | NpgsqlDbType.Boolean).Value =
            maxima.Select(item => item.Changed).ToArray();
        command.Parameters.Add(
            "affectedInstruments",
            NpgsqlDbType.Array | NpgsqlDbType.Boolean).Value =
            maxima.Select(item => item.AffectedInstrument).ToArray();
        command.Parameters.Add(
            "publicationId",
            NpgsqlDbType.Bigint).Value = publicationId;
        command.Parameters.Add(
            "publishedScrapeId",
            NpgsqlDbType.Bigint).Value = publishedScrapeId;
        await command.ExecuteNonQueryAsync(deadline.Token);
        await ValidateSelectorsAsync(
            publicationId,
            publishedScrapeId,
            connection,
            transaction,
            deadline);
    }

    private static async Task ValidateSelectorsAsync(
        long publicationId,
        long publishedScrapeId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SharedCommandDeadline deadline)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        deadline.Configure(command);
        command.CommandText = ValidateSelectorsSql;
        await using var reader =
            await command.ExecuteReaderAsync(deadline.Token);
        if (!await reader.ReadAsync(deadline.Token))
        {
            throw new InvalidOperationException(
                "Score-history publication/source validation was unavailable.");
        }

        var publicationCount = reader.GetInt32(0);
        var sourceCount = reader.GetInt32(1);
        var sourcesComplete = reader.GetBoolean(2);
        var bindingReady = reader.GetBoolean(3);
        var missingChangedScopeCount = reader.GetInt32(4);
        var usableScopeCount = reader.GetInt32(5);
        if (publicationCount != 1)
        {
            throw new InvalidOperationException(
                $"Score-history evidence requires current publication {publicationId} " +
                $"to be bound to published scrape {publishedScrapeId} with no working publication.");
        }
        if (sourceCount == 0)
        {
            throw new InvalidOperationException(
                $"Score-history evidence found no all-time sources for published scrape {publishedScrapeId}.");
        }
        if (!sourcesComplete)
        {
            throw new InvalidOperationException(
                $"Score-history evidence requires every selected source for published scrape {publishedScrapeId} to be complete.");
        }
        if (!bindingReady)
        {
            throw new InvalidOperationException(
                $"Score-history evidence requires publication {publicationId} to have a ready solo_scope_sources scrape-id binding for published scrape {publishedScrapeId}.");
        }
        if (missingChangedScopeCount != 0)
        {
            throw new InvalidOperationException(
                $"Score-history evidence is missing {missingChangedScopeCount} changed publication scope source(s).");
        }
        if (usableScopeCount == 0)
        {
            throw new InvalidOperationException(
                "Score-history evidence found no published scopes with post-promotion maxima.");
        }
    }

    private static async Task<IReadOnlyList<ScopeSource>>
        LoadScopeSourcesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            SharedCommandDeadline deadline)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        deadline.Configure(command);
        command.CommandText = LoadScopeSourcesSql;
        var sources = new List<ScopeSource>();
        await using var reader =
            await command.ExecuteReaderAsync(deadline.Token);
        while (await reader.ReadAsync(deadline.Token))
        {
            var sourceKind = reader.GetString(2);
            var snapshotId = reader.IsDBNull(3)
                ? (long?)null
                : reader.GetInt64(3);
            if (sourceKind == "snapshot")
            {
                if (snapshotId is null)
                {
                    throw new InvalidOperationException(
                        "A selected snapshot source is missing its snapshot ID.");
                }
            }
            else if (sourceKind != "empty" || snapshotId is not null)
            {
                throw new InvalidOperationException(
                    $"Selected score-history source kind '{sourceKind}' is invalid.");
            }

            sources.Add(new ScopeSource(
                reader.GetString(0),
                reader.GetString(1),
                snapshotId,
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7)));
        }
        if (sources.Count == 0)
        {
            throw new InvalidOperationException(
                "Score-history evidence found no usable publication sources.");
        }
        return sources;
    }

    private static async Task PopulateAffectedAccountsAsync(
        IReadOnlyList<ScopeSource> scopeSources,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SharedCommandDeadline deadline)
    {
        foreach (var group in scopeSources
                     .Where(scope => scope.Changed)
                     .GroupBy(scope => scope.Instrument)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            await using var snapshotCommand =
                CreateChangedSnapshotProbeCommand(
                    group.Key,
                    connection,
                    transaction);
            await using var overlayCommand =
                CreateChangedOverlayProbeCommand(
                    group.Key,
                    connection,
                    transaction);
            await PrepareProbeCommandAsync(
                snapshotCommand,
                deadline);
            await PrepareProbeCommandAsync(
                overlayCommand,
                deadline);

            foreach (var scope in group.OrderBy(
                         item => item.SongId,
                         StringComparer.Ordinal))
            {
                if (scope.SourceSnapshotId is long snapshotId)
                {
                    SetChangedSnapshotProbeParameters(
                        snapshotCommand,
                        scope.SongId,
                        snapshotId);
                    await ExecuteProbeCommandAsync(
                        snapshotCommand,
                        deadline);
                }

                SetChangedOverlayProbeParameters(
                    overlayCommand,
                    scope.SongId,
                    scope.SourceSnapshotId);
                await ExecuteProbeCommandAsync(
                    overlayCommand,
                    deadline);
            }
        }
    }

    private static async Task PopulateFallbackScopesAsync(
        IEnumerable<ScopeSource> scopeSources,
        string snapshotSql,
        string overlaySql,
        Func<ScopeSource, int> scoreThreshold,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SharedCommandDeadline deadline)
    {
        foreach (var group in scopeSources
                     .GroupBy(scope => scope.Instrument)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            await using var snapshotCommand =
                CreateFallbackProbeCommand(
                    snapshotSql,
                    group.Key,
                    includeSnapshotId: true,
                    connection,
                    transaction);
            await using var overlayCommand =
                CreateFallbackProbeCommand(
                    overlaySql,
                    group.Key,
                    includeSnapshotId: false,
                    connection,
                    transaction);
            await PrepareProbeCommandAsync(
                snapshotCommand,
                deadline);
            await PrepareProbeCommandAsync(
                overlayCommand,
                deadline);

            foreach (var scope in group.OrderBy(
                         item => item.SongId,
                         StringComparer.Ordinal))
            {
                var threshold = scoreThreshold(scope);
                if (scope.SourceSnapshotId is long snapshotId)
                {
                    SetFallbackProbeParameters(
                        snapshotCommand,
                        scope.SongId,
                        snapshotId,
                        threshold,
                        scope.MaximumThreshold);
                    await ExecuteProbeCommandAsync(
                        snapshotCommand,
                        deadline);
                }

                SetFallbackProbeParameters(
                    overlayCommand,
                    scope.SongId,
                    null,
                    threshold,
                    scope.MaximumThreshold);
                await ExecuteProbeCommandAsync(
                    overlayCommand,
                    deadline);
            }
        }
    }

    private static NpgsqlCommand CreateChangedSnapshotProbeCommand(
        string instrument,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ChangedSnapshotAccountsProbeSql;
        command.Parameters.Add(
            "sourceSnapshotId",
            NpgsqlDbType.Bigint).Value = 0L;
        command.Parameters.Add(
            "songId",
            NpgsqlDbType.Text).Value = string.Empty;
        command.Parameters.Add(
            "instrument",
            NpgsqlDbType.Text).Value = instrument;
        return command;
    }

    private static NpgsqlCommand CreateChangedOverlayProbeCommand(
        string instrument,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ChangedOverlayAccountsProbeSql;
        command.Parameters.Add(
            "sourceSnapshotId",
            NpgsqlDbType.Bigint).Value = DBNull.Value;
        command.Parameters.Add(
            "songId",
            NpgsqlDbType.Text).Value = string.Empty;
        command.Parameters.Add(
            "instrument",
            NpgsqlDbType.Text).Value = instrument;
        return command;
    }

    private static NpgsqlCommand CreateFallbackProbeCommand(
        string sql,
        string instrument,
        bool includeSnapshotId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (includeSnapshotId)
        {
            command.Parameters.Add(
                "sourceSnapshotId",
                NpgsqlDbType.Bigint).Value = 0L;
        }
        command.Parameters.Add(
            "songId",
            NpgsqlDbType.Text).Value = string.Empty;
        command.Parameters.Add(
            "instrument",
            NpgsqlDbType.Text).Value = instrument;
        command.Parameters.Add(
            "scoreThreshold",
            NpgsqlDbType.Integer).Value = 0;
        command.Parameters.Add(
            "maxThreshold",
            NpgsqlDbType.Integer).Value = 0;
        return command;
    }

    private static async Task PrepareProbeCommandAsync(
        NpgsqlCommand command,
        SharedCommandDeadline deadline)
    {
        deadline.Configure(command);
        await command.PrepareAsync(deadline.Token);
    }

    private static async Task ExecuteProbeCommandAsync(
        NpgsqlCommand command,
        SharedCommandDeadline deadline)
    {
        deadline.Configure(command);
        await command.ExecuteNonQueryAsync(deadline.Token);
    }

    private static void SetChangedSnapshotProbeParameters(
        NpgsqlCommand command,
        string songId,
        long sourceSnapshotId)
    {
        command.Parameters["songId"].Value = songId;
        command.Parameters["sourceSnapshotId"].Value =
            sourceSnapshotId;
    }

    private static void SetChangedOverlayProbeParameters(
        NpgsqlCommand command,
        string songId,
        long? sourceSnapshotId)
    {
        command.Parameters["songId"].Value = songId;
        command.Parameters["sourceSnapshotId"].Value =
            sourceSnapshotId is long value
                ? value
                : DBNull.Value;
    }

    private static void SetFallbackProbeParameters(
        NpgsqlCommand command,
        string songId,
        long? sourceSnapshotId,
        int scoreThreshold,
        int maxThreshold)
    {
        command.Parameters["songId"].Value = songId;
        if (sourceSnapshotId is long value)
        {
            command.Parameters["sourceSnapshotId"].Value =
                value;
        }
        command.Parameters["scoreThreshold"].Value =
            scoreThreshold;
        command.Parameters["maxThreshold"].Value =
            maxThreshold;
    }

    private static async Task ExecuteSelectorNonQueryAsync(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SharedCommandDeadline deadline)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        deadline.Configure(command);
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(deadline.Token);
    }

    private static async Task<AffectedAccounts> LoadAffectedAccountsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SharedCommandDeadline deadline)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        deadline.Configure(command);
        command.CommandText = LoadAffectedAccountsSql;
        var affected = new List<string>();
        var registered = new List<string>();
        var overlayOnly = new List<string>();
        await using var reader =
            await command.ExecuteReaderAsync(deadline.Token);
        while (await reader.ReadAsync(deadline.Token))
        {
            var accountId = reader.GetString(0);
            affected.Add(accountId);
            if (!reader.GetBoolean(1))
                continue;
            registered.Add(accountId);
            if (reader.GetBoolean(2))
                overlayOnly.Add(accountId);
        }
        return new AffectedAccounts(
            affected,
            registered,
            overlayOnly);
    }

    private static async Task<BranchEvidence> AggregateAsync(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SharedCommandDeadline deadline)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        deadline.Configure(command);
        command.CommandText = sql;
        await using var reader =
            await command.ExecuteReaderAsync(deadline.Token);
        if (!await reader.ReadAsync(deadline.Token))
        {
            throw new InvalidOperationException(
                "Score-history branch evidence was unavailable.");
        }
        return new BranchEvidence(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3)
                ? null
                : reader.GetDateTime(3).ToUniversalTime(),
            reader.IsDBNull(4)
                ? null
                : reader.GetDateTime(4).ToUniversalTime(),
            BigInteger.Parse(
                reader.GetString(5),
                CultureInfo.InvariantCulture),
            long.Parse(
                reader.GetString(6),
                CultureInfo.InvariantCulture));
    }

    private static MaxScoreMaintenanceScoreHistoryEvidence Combine(
        BranchEvidence registered,
        BranchEvidence fallback)
    {
        var rowCount = checked(
            registered.RowCount + fallback.RowCount);
        var minimumId = Minimum(
            registered.MinimumId,
            fallback.MinimumId);
        var maximumId = Maximum(
            registered.MaximumId,
            fallback.MaximumId);
        var minimumChangedAt = Minimum(
            registered.MinimumChangedAtUtc,
            fallback.MinimumChangedAtUtc);
        var maximumChangedAt = Maximum(
            registered.MaximumChangedAtUtc,
            fallback.MaximumChangedAtUtc);
        var hashSum = registered.HashSum + fallback.HashSum;
        var hashXor = registered.HashXor ^ fallback.HashXor;
        var canonical = string.Join(
            ":",
            rowCount.ToString(CultureInfo.InvariantCulture),
            minimumId?.ToString(CultureInfo.InvariantCulture)
                ?? string.Empty,
            maximumId?.ToString(CultureInfo.InvariantCulture)
                ?? string.Empty,
            ToEpochMicroseconds(minimumChangedAt),
            ToEpochMicroseconds(maximumChangedAt),
            hashSum.ToString(CultureInfo.InvariantCulture),
            hashXor.ToString(CultureInfo.InvariantCulture));
        var fingerprint = Convert.ToHexStringLower(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(canonical)));
        return new MaxScoreMaintenanceScoreHistoryEvidence(
            rowCount,
            minimumId,
            maximumId,
            minimumChangedAt,
            maximumChangedAt,
            fingerprint);
    }

    private static long? Minimum(long? left, long? right)
        => left is null
            ? right
            : right is null
                ? left
                : Math.Min(left.Value, right.Value);

    private static long? Maximum(long? left, long? right)
        => left is null
            ? right
            : right is null
                ? left
                : Math.Max(left.Value, right.Value);

    private static DateTime? Minimum(
        DateTime? left,
        DateTime? right)
        => left is null
            ? right
            : right is null
                ? left
                : left.Value <= right.Value
                    ? left
                    : right;

    private static DateTime? Maximum(
        DateTime? left,
        DateTime? right)
        => left is null
            ? right
            : right is null
                ? left
                : left.Value >= right.Value
                    ? left
                    : right;

    private static string ToEpochMicroseconds(DateTime? value)
        => value is null
            ? string.Empty
            : (
                (
                    value.Value.ToUniversalTime().Ticks
                    - DateTime.UnixEpoch.Ticks)
                / 10)
            .ToString(CultureInfo.InvariantCulture);

    private static async Task DropSelectorsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 30;
        command.CommandText = DropSelectorsSql;
        await command.ExecuteNonQueryAsync(
            CancellationToken.None);
    }

    private static async Task RollbackToSavepointAsync(
        NpgsqlTransaction transaction,
        string savepointName)
    {
        try
        {
            await transaction.RollbackAsync(
                savepointName,
                CancellationToken.None);
            await transaction.ReleaseAsync(
                savepointName,
                CancellationToken.None);
        }
        catch
        {
        }
    }

    private static TimeoutException CreateTimeoutException(
        int commandTimeoutSeconds,
        Exception innerException)
        => new(
            $"Score-history evidence exceeded the shared {commandTimeoutSeconds}-second deadline.",
            innerException);

    private sealed class SharedCommandDeadline : IDisposable
    {
        private readonly int _commandTimeoutSeconds;
        private readonly CancellationToken _callerToken;
        private readonly Action<string, int>? _configured;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly CancellationTokenSource _deadline;
        private bool _reported;

        internal SharedCommandDeadline(
            int commandTimeoutSeconds,
            CancellationToken callerToken,
            Action<string, int>? configured)
        {
            if (!ScraperOptions
                    .IsValidMaxScoreMaintenanceCommandTimeout(
                        commandTimeoutSeconds))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(commandTimeoutSeconds),
                    commandTimeoutSeconds,
                    "Max-score maintenance command timeout is outside the validated range.");
            }
            _commandTimeoutSeconds = commandTimeoutSeconds;
            _callerToken = callerToken;
            _configured = configured;
            _deadline =
                CancellationTokenSource
                    .CreateLinkedTokenSource(callerToken);
            _deadline.CancelAfter(
                TimeSpan.FromSeconds(commandTimeoutSeconds));
        }

        internal CancellationToken Token => _deadline.Token;

        internal bool HasExpired =>
            !_callerToken.IsCancellationRequested
            && (
                _stopwatch.Elapsed
                    >= TimeSpan.FromSeconds(
                        _commandTimeoutSeconds)
                || _deadline.IsCancellationRequested);

        internal void Configure(NpgsqlCommand command)
        {
            var remaining = TimeSpan.FromSeconds(
                    _commandTimeoutSeconds)
                - _stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw CreateTimeoutException(
                    _commandTimeoutSeconds,
                    new TimeoutException(
                        "No score-history evidence deadline remained."));
            }

            if (!_reported)
            {
                MaxScoreMaintenanceCommandTimeout.Configure(
                    command,
                    _commandTimeoutSeconds,
                    EvidenceStage,
                    _configured);
                _reported = true;
                return;
            }

            MaxScoreMaintenanceCommandTimeout.Configure(
                command,
                Math.Max(
                    1,
                    (int)Math.Ceiling(
                        remaining.TotalSeconds)),
                EvidenceStage);
        }

        public void Dispose()
        {
            _deadline.Dispose();
        }
    }

    private sealed record AffectedAccounts(
        IReadOnlyList<string> Affected,
        IReadOnlyList<string> Registered,
        IReadOnlyList<string> OverlayOnly);

    private sealed record ScopeSource(
        string SongId,
        string Instrument,
        long? SourceSnapshotId,
        int Maximum,
        int MaximumThreshold,
        bool Changed,
        bool AffectedInstrument);

    private sealed record BranchEvidence(
        long RowCount,
        long? MinimumId,
        long? MaximumId,
        DateTime? MinimumChangedAtUtc,
        DateTime? MaximumChangedAtUtc,
        BigInteger HashSum,
        long HashXor);
}
