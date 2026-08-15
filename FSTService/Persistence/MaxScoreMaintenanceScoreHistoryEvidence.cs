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
    internal const string MaximaTable =
        "fst_max_score_evidence_maxima";
    internal const string SourcesTable =
        "fst_max_score_evidence_sources";
    internal const string CandidatesTable =
        "fst_max_score_evidence_candidates";
    internal const string AffectedAccountsTable =
        "fst_max_score_evidence_affected_accounts";
    internal const string RegisteredAccountsTable =
        "fst_max_score_evidence_registered_accounts";
    internal const string FallbackScopesTable =
        "fst_max_score_evidence_fallback_scopes";

    internal static readonly IReadOnlyList<string> SelectorTableNames =
    [
        MaximaTable,
        SourcesTable,
        CandidatesTable,
        AffectedAccountsTable,
        RegisteredAccountsTable,
        FallbackScopesTable,
    ];

    private const string EvidenceStage =
        "complete-score-history-evidence";

    private const string PrepareSelectorsSql = """
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
            song_id TEXT NOT NULL,
            instrument TEXT NOT NULL,
            source_kind TEXT NOT NULL,
            source_snapshot_id BIGINT,
            PRIMARY KEY (song_id, instrument)
        ) ON COMMIT DROP;

        CREATE TEMP TABLE fst_max_score_evidence_candidates (
            song_id TEXT NOT NULL,
            instrument TEXT NOT NULL,
            account_id TEXT NOT NULL,
            score INTEGER NOT NULL,
            origin_precedence SMALLINT NOT NULL,
            source_priority INTEGER NOT NULL,
            has_snapshot BOOLEAN NOT NULL,
            PRIMARY KEY (song_id, instrument, account_id)
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

        INSERT INTO fst_max_score_evidence_sources (
            song_id,
            instrument,
            source_kind,
            source_snapshot_id)
        SELECT source.song_id,
               source.instrument,
               source.source_kind,
               source.source_snapshot_id
        FROM leaderboard_published_scope_source source
        JOIN scrape_publication_state publication
          ON publication.id = TRUE
         AND publication.published_scrape_id =
             source.published_scrape_id
        JOIN fst_max_score_evidence_maxima maxima
          ON maxima.song_id = source.song_id
         AND maxima.instrument = source.instrument
        WHERE source.scope_kind = 'alltime'
          AND source.is_complete;

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

        INSERT INTO fst_max_score_evidence_candidates (
            song_id,
            instrument,
            account_id,
            score,
            origin_precedence,
            source_priority,
            has_snapshot)
        SELECT snapshot.song_id,
               snapshot.instrument,
               snapshot.account_id,
               snapshot.score,
               1,
               0,
               TRUE
        FROM fst_max_score_evidence_sources source
        JOIN fst_max_score_evidence_maxima maxima
          ON maxima.song_id = source.song_id
         AND maxima.instrument = source.instrument
         AND maxima.is_affected_instrument
        JOIN leaderboard_entries_snapshot snapshot
          ON source.source_kind = 'snapshot'
         AND source.source_snapshot_id = snapshot.snapshot_id
         AND source.song_id = snapshot.song_id
         AND source.instrument = snapshot.instrument
        ON CONFLICT (
            song_id,
            instrument,
            account_id) DO NOTHING;

        INSERT INTO fst_max_score_evidence_candidates AS existing (
            song_id,
            instrument,
            account_id,
            score,
            origin_precedence,
            source_priority,
            has_snapshot)
        SELECT overlay.song_id,
               overlay.instrument,
               overlay.account_id,
               overlay.score,
               0,
               overlay.source_priority,
               FALSE
        FROM fst_max_score_evidence_sources source
        JOIN fst_max_score_evidence_maxima maxima
          ON maxima.song_id = source.song_id
         AND maxima.instrument = source.instrument
         AND maxima.is_affected_instrument
        JOIN leaderboard_entries_overlay overlay
          ON source.song_id = overlay.song_id
         AND source.instrument = overlay.instrument
        ON CONFLICT (
            song_id,
            instrument,
            account_id) DO UPDATE
        SET score = EXCLUDED.score,
            origin_precedence =
                EXCLUDED.origin_precedence,
            source_priority = EXCLUDED.source_priority,
            has_snapshot =
                existing.has_snapshot
                OR EXCLUDED.has_snapshot
        WHERE EXCLUDED.origin_precedence
                  < existing.origin_precedence
           OR (
               EXCLUDED.origin_precedence
                   = existing.origin_precedence
               AND EXCLUDED.source_priority
                   > existing.source_priority);

        INSERT INTO fst_max_score_evidence_affected_accounts (
            account_id)
        SELECT candidate.account_id
        FROM fst_max_score_evidence_candidates candidate
        JOIN fst_max_score_evidence_maxima maxima
          ON maxima.song_id = candidate.song_id
         AND maxima.instrument = candidate.instrument
         AND maxima.is_changed
        ON CONFLICT (account_id) DO NOTHING;

        ANALYZE fst_max_score_evidence_affected_accounts;

        INSERT INTO fst_max_score_evidence_candidates (
            song_id,
            instrument,
            account_id,
            score,
            origin_precedence,
            source_priority,
            has_snapshot)
        SELECT snapshot.song_id,
               snapshot.instrument,
               snapshot.account_id,
               snapshot.score,
               1,
               0,
               TRUE
        FROM fst_max_score_evidence_sources source
        JOIN fst_max_score_evidence_maxima maxima
          ON maxima.song_id = source.song_id
         AND maxima.instrument = source.instrument
         AND NOT maxima.is_affected_instrument
        CROSS JOIN fst_max_score_evidence_affected_accounts affected
        JOIN leaderboard_entries_snapshot snapshot
          ON source.source_kind = 'snapshot'
         AND source.source_snapshot_id = snapshot.snapshot_id
         AND source.song_id = snapshot.song_id
         AND source.instrument = snapshot.instrument
         AND affected.account_id = snapshot.account_id
        ON CONFLICT (
            song_id,
            instrument,
            account_id) DO NOTHING;

        INSERT INTO fst_max_score_evidence_candidates AS existing (
            song_id,
            instrument,
            account_id,
            score,
            origin_precedence,
            source_priority,
            has_snapshot)
        SELECT overlay.song_id,
               overlay.instrument,
               overlay.account_id,
               overlay.score,
               0,
               overlay.source_priority,
               FALSE
        FROM fst_max_score_evidence_sources source
        JOIN fst_max_score_evidence_maxima maxima
          ON maxima.song_id = source.song_id
         AND maxima.instrument = source.instrument
         AND NOT maxima.is_affected_instrument
        CROSS JOIN fst_max_score_evidence_affected_accounts affected
        JOIN leaderboard_entries_overlay overlay
          ON source.song_id = overlay.song_id
         AND source.instrument = overlay.instrument
         AND affected.account_id = overlay.account_id
        ON CONFLICT (
            song_id,
            instrument,
            account_id) DO UPDATE
        SET score = EXCLUDED.score,
            origin_precedence =
                EXCLUDED.origin_precedence,
            source_priority = EXCLUDED.source_priority,
            has_snapshot =
                existing.has_snapshot
                OR EXCLUDED.has_snapshot
        WHERE EXCLUDED.origin_precedence
                  < existing.origin_precedence
           OR (
               EXCLUDED.origin_precedence
                   = existing.origin_precedence
               AND EXCLUDED.source_priority
                   > existing.source_priority);

        UPDATE fst_max_score_evidence_affected_accounts affected
        SET is_registered = TRUE
        FROM fst_max_score_evidence_registered_accounts registered
        WHERE registered.account_id = affected.account_id;

        UPDATE fst_max_score_evidence_affected_accounts affected
        SET is_overlay_only = TRUE
        WHERE EXISTS (
            SELECT 1
            FROM fst_max_score_evidence_candidates candidate
            JOIN fst_max_score_evidence_maxima maxima
              ON maxima.song_id = candidate.song_id
             AND maxima.instrument = candidate.instrument
             AND maxima.is_changed
            WHERE candidate.account_id = affected.account_id
              AND candidate.origin_precedence = 0
              AND NOT candidate.has_snapshot);

        ANALYZE fst_max_score_evidence_candidates;

        INSERT INTO fst_max_score_evidence_fallback_scopes (
            song_id,
            instrument,
            account_id,
            max_threshold)
        SELECT candidate.song_id,
               candidate.instrument,
               candidate.account_id,
               maxima.max_threshold
        FROM fst_max_score_evidence_candidates candidate
        JOIN fst_max_score_evidence_maxima maxima
          ON maxima.song_id = candidate.song_id
         AND maxima.instrument = candidate.instrument
        LEFT JOIN fst_max_score_evidence_affected_accounts affected
          ON affected.account_id = candidate.account_id
        WHERE (
                  affected.account_id IS NOT NULL
                  AND candidate.score > maxima.max_score
              )
           OR (
                  maxima.is_affected_instrument
                  AND candidate.score > maxima.max_threshold
              )
        ON CONFLICT (
            song_id,
            instrument,
            account_id) DO NOTHING;

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
            fst_max_score_evidence_candidates,
            fst_max_score_evidence_sources,
            fst_max_score_evidence_maxima;
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
            Action<string, int>? commandTimeoutConfigured = null)
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

    private static async Task PrepareSelectorsAsync(
        IReadOnlyList<(
            string SongId,
            string Instrument,
            int Maximum,
            int Threshold,
            bool Changed,
            bool AffectedInstrument)> maxima,
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

    private sealed record BranchEvidence(
        long RowCount,
        long? MinimumId,
        long? MaximumId,
        DateTime? MinimumChangedAtUtc,
        DateTime? MaximumChangedAtUtc,
        BigInteger HashSum,
        long HashXor);
}
