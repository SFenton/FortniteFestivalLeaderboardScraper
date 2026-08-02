using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Persistence;

public sealed class ScoreHistoryDedupMaintenanceService
{
    private const int DryRunCommandTimeoutSeconds = 125;
    private const int ExecuteCommandTimeoutSeconds = 185;
    private const string DryRunLockTimeout = "2s";
    private const string DryRunStatementTimeout = "120s";
    private const string ExecuteLockTimeout = "3s";
    private const string ExecuteStatementTimeout = "180s";
    private const string AdvisoryLockKey =
        "fst.score_history.null_timestamp_dedup.v1";
    private const string LegacyIndexState = "legacy_nulls_distinct";
    private const string TargetIndexState = "nulls_not_distinct";
    private const string UnexpectedIndexState = "unexpected";
    private const string MissingIndexState = "missing";

    private static readonly string[] ExpectedIndexColumns =
    [
        "account_id",
        "song_id",
        "instrument",
        "new_score",
        "score_achieved_at",
    ];

    private static readonly string[] InvariantFieldNames =
    [
        "old_score",
        "old_rank",
        "accuracy",
        "is_full_combo",
        "stars",
        "percentile",
        "season",
        "season_rank",
        "difficulty",
    ];

    private static readonly ScoreHistoryDedupMergeSemantics MergeSemantics =
        new(
            Survivor: "lowest_id",
            ChangedAt: "earliest_changed_at",
            NewRank:
                "minimum_positive_non_null_else_minimum_non_null",
            AllTimeRank:
                "minimum_positive_non_null_else_minimum_non_null",
            InvariantFields: InvariantFieldNames,
            DeleteRule: "delete_non_survivors_only");

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ScoreHistoryDedupMaintenanceService> _log;

    public ScoreHistoryDedupMaintenanceService(
        NpgsqlDataSource dataSource,
        ILogger<ScoreHistoryDedupMaintenanceService> log)
    {
        _dataSource = dataSource;
        _log = log;
    }

    public async Task<ScoreHistoryDedupDryRunReport> DryRunAsync(
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            ct);
        await ConfigureTransactionAsync(
            conn,
            tx,
            readOnly: true,
            DryRunLockTimeout,
            DryRunStatementTimeout,
            DryRunCommandTimeoutSeconds,
            ct);

        var analysis = await BuildAnalysisAsync(
            conn,
            tx,
            reportReadOnly: true,
            ct);
        await tx.CommitAsync(ct);

        _log.LogInformation(
            "Score-history dedup dry run {Decision}: digest={Digest}, " +
            "rows={Rows:N0}, groups={Groups:N0}, excess={Excess:N0}, " +
            "index={IndexState}.",
            analysis.Report.SafetyDecision,
            analysis.Report.DryRunDigest,
            analysis.Report.DuplicateRowCount,
            analysis.Report.DuplicateGroupCount,
            analysis.Report.ExcessRowCount,
            analysis.Report.Index.State);

        return analysis.Report;
    }

    public async Task<ScoreHistoryDedupExecuteReport> ExecuteAsync(
        string expectedDryRunDigest,
        CancellationToken ct = default)
    {
        var expectedDigest =
            ScoreHistoryDedupMaintenanceCommand.NormalizeDigest(
                expectedDryRunDigest);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            ct);
        await ConfigureTransactionAsync(
            conn,
            tx,
            readOnly: false,
            ExecuteLockTimeout,
            ExecuteStatementTimeout,
            ExecuteCommandTimeoutSeconds,
            ct);
        await AcquireMaintenanceLocksAsync(conn, tx, ct);

        var analysis = await BuildAnalysisAsync(
            conn,
            tx,
            reportReadOnly: false,
            ct);
        var priorRun = await LoadPriorRunAsync(
            conn,
            tx,
            expectedDigest,
            ct);

        if (!analysis.Report.DryRunDigest.Equals(
                expectedDigest,
                StringComparison.Ordinal))
        {
            if (priorRun is not null && IsCleanTargetState(analysis.Report))
            {
                await tx.CommitAsync(ct);
                return priorRun.ToExecuteReport(alreadyApplied: true);
            }

            throw new InvalidOperationException(
                $"Score-history dedup dry-run digest changed from expected " +
                $"{expectedDigest} to {analysis.Report.DryRunDigest}; " +
                "no rows were written.");
        }

        EnsureSafeToExecute(analysis.Report);

        if (IsCleanTargetState(analysis.Report))
        {
            await tx.CommitAsync(ct);
            return new ScoreHistoryDedupExecuteReport(
                Purpose: ScoreHistoryDedupMaintenanceSchema.Purpose,
                DryRunDigest: analysis.Report.DryRunDigest,
                MaintenanceRunId: null,
                AlreadyApplied: false,
                NoChangesRequired: true,
                OriginalRowsAudited: 0,
                DuplicateGroupsMerged: 0,
                SurvivorRowsUpdated: 0,
                RowsDeleted: 0,
                IndexReplaced: false,
                IndexStateAfter: TargetIndexState,
                RollbackSql: null);
        }

        var provenance = await LoadExecutionProvenanceAsync(
            conn,
            tx,
            ct);
        var maintenanceRunId = await ReserveMaintenanceRunIdAsync(
            conn,
            tx,
            ct);
        var rollbackSql = BuildRollbackSql(
            maintenanceRunId,
            analysis.Report.DryRunDigest);

        await InsertMaintenanceRunAsync(
            conn,
            tx,
            maintenanceRunId,
            analysis,
            provenance,
            rollbackSql,
            ct);
        var auditedRows = await InsertOriginalRowsAsync(
            conn,
            tx,
            maintenanceRunId,
            analysis.Rows,
            ct);
        if (auditedRows != analysis.Rows.Count)
        {
            throw new InvalidOperationException(
                $"Expected to audit {analysis.Rows.Count:N0} original " +
                $"score-history row(s), but audited {auditedRows:N0}; " +
                "all changes were rolled back.");
        }

        var survivorRowsUpdated = await UpdateSurvivorsAsync(
            conn,
            tx,
            analysis.Groups,
            ct);
        if (survivorRowsUpdated != analysis.Groups.Count)
        {
            throw new InvalidOperationException(
                $"Expected to update {analysis.Groups.Count:N0} survivor " +
                $"row(s), but updated {survivorRowsUpdated:N0}; " +
                "all changes were rolled back.");
        }

        var rowsDeleted = await DeleteNonSurvivorsAsync(
            conn,
            tx,
            analysis.Rows,
            analysis.Groups,
            ct);
        if (rowsDeleted != analysis.Report.ExcessRowCount)
        {
            throw new InvalidOperationException(
                $"Expected to delete {analysis.Report.ExcessRowCount:N0} " +
                $"non-survivor row(s), but deleted {rowsDeleted:N0}; " +
                "all changes were rolled back.");
        }

        await VerifyNoDuplicateGroupsAsync(conn, tx, ct);
        await ReplaceLegacyIndexAsync(conn, tx, ct);
        var indexAfter = await LoadIndexAsync(conn, tx, ct);
        if (indexAfter.State != TargetIndexState)
        {
            throw new InvalidOperationException(
                "ix_sh_dedup was not replaced by the expected unique " +
                "NULLS NOT DISTINCT btree; all changes were rolled back.");
        }

        await tx.CommitAsync(ct);

        _log.LogInformation(
            "Executed score-history null-timestamp dedup run {RunId}: " +
            "digest={Digest}, audited={Audited:N0}, groups={Groups:N0}, " +
            "deleted={Deleted:N0}, index={IndexState}.",
            maintenanceRunId,
            analysis.Report.DryRunDigest,
            auditedRows,
            analysis.Groups.Count,
            rowsDeleted,
            indexAfter.State);

        return new ScoreHistoryDedupExecuteReport(
            Purpose: ScoreHistoryDedupMaintenanceSchema.Purpose,
            DryRunDigest: analysis.Report.DryRunDigest,
            MaintenanceRunId: maintenanceRunId,
            AlreadyApplied: false,
            NoChangesRequired: false,
            OriginalRowsAudited: auditedRows,
            DuplicateGroupsMerged: analysis.Groups.Count,
            SurvivorRowsUpdated: survivorRowsUpdated,
            RowsDeleted: rowsDeleted,
            IndexReplaced: true,
            IndexStateAfter: indexAfter.State,
            RollbackSql: rollbackSql);
    }

    private static async Task ConfigureTransactionAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        bool readOnly,
        string lockTimeout,
        string statementTimeout,
        int commandTimeoutSeconds,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = commandTimeoutSeconds;
        cmd.CommandText = """
            SELECT set_config('lock_timeout', @lockTimeout, true);
            SELECT set_config('statement_timeout', @statementTimeout, true);
            """;
        if (readOnly)
            cmd.CommandText = $"SET TRANSACTION READ ONLY;{Environment.NewLine}{cmd.CommandText}";
        cmd.Parameters.AddWithValue("lockTimeout", lockTimeout);
        cmd.Parameters.AddWithValue("statementTimeout", statementTimeout);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task AcquireMaintenanceLocksAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        await using (var advisory = conn.CreateCommand())
        {
            advisory.Transaction = tx;
            advisory.CommandTimeout = ExecuteCommandTimeoutSeconds;
            advisory.CommandText = """
                SELECT pg_try_advisory_xact_lock(
                    hashtextextended(@lockKey, 0));
                """;
            advisory.Parameters.AddWithValue("lockKey", AdvisoryLockKey);
            if (await advisory.ExecuteScalarAsync(ct) is not true)
            {
                throw new InvalidOperationException(
                    "Another score-history dedup maintenance transaction " +
                    "holds the advisory lock; no rows were written.");
            }
        }

        await using var tableLock = conn.CreateCommand();
        tableLock.Transaction = tx;
        tableLock.CommandTimeout = ExecuteCommandTimeoutSeconds;
        tableLock.CommandText = """
            LOCK TABLE public.score_history
            IN SHARE ROW EXCLUSIVE MODE;
            """;
        await tableLock.ExecuteNonQueryAsync(ct);
    }

    private static async Task<ScoreHistoryDedupAnalysis> BuildAnalysisAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        bool reportReadOnly,
        CancellationToken ct)
    {
        await EnsureAuditSchemaAsync(conn, tx, ct);
        var counts = await LoadCountsAsync(conn, tx, ct);
        var rows = await LoadDuplicateRowsAsync(conn, tx, ct);
        var groups = BuildGroups(rows);
        var index = await LoadIndexAsync(conn, tx, ct);
        var storage = await LoadStorageAsync(conn, tx, index, ct);
        var classificationCounts = groups
            .GroupBy(group => group.Classification, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ScoreHistoryDedupClassificationCount(
                Classification: group.Key,
                GroupCount: group.LongCount(),
                RowCount: group.Sum(item => item.RowCount),
                ExcessRowCount: group.Sum(item => item.ExcessRowCount),
                Allowed: group.All(item => item.Allowed)))
            .ToArray();
        var affectedAccounts = groups
            .Select(group => group.AccountId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var affectedSongs = groups
            .Select(group => group.SongId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var duplicateRowCount = rows.LongCount();
        var excessRowCount = groups.Sum(group => group.ExcessRowCount);
        var indexAccepted = index.State is LegacyIndexState or TargetIndexState;
        var groupSemanticsAccepted = groups.All(group => group.Allowed);
        var targetContradiction =
            index.State == TargetIndexState && groups.Count > 0;
        var canExecute =
            indexAccepted
            && groupSemanticsAccepted
            && !targetContradiction;
        var safetyDecision = canExecute
            ? "ready"
            : !indexAccepted || targetContradiction
                ? "blocked_index_invariant"
                : "blocked_unexpected_history";
        var requiredAction = !canExecute
            ? "blocked"
            : groups.Count == 0 && index.State == TargetIndexState
                ? "none"
                : groups.Count == 0
                    ? "replace_index_only"
                    : "merge_and_replace_index";
        var maxima = new ScoreHistoryDedupMaxima(
            MaximumRowsInGroup:
                groups.Count == 0 ? 0 : groups.Max(group => group.RowCount),
            MaximumExcessRowsInGroup:
                groups.Count == 0 ? 0 : groups.Max(group => group.ExcessRowCount),
            MaximumOriginalId:
                rows.Count == 0 ? null : rows.Max(row => row.Id),
            MaximumObservedNewRank:
                MaxNullable(rows.Select(row => row.NewRank)),
            MaximumObservedAllTimeRank:
                MaxNullable(rows.Select(row => row.AllTimeRank)));

        var canonicalData = BuildCanonicalData(rows, groups, index);
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalData)));

        var report = new ScoreHistoryDedupDryRunReport(
            Purpose: ScoreHistoryDedupMaintenanceSchema.Purpose,
            ContractVersion:
                ScoreHistoryDedupMaintenanceSchema.ContractVersion,
            Transaction: new ScoreHistoryDedupTransactionContract(
                IsolationLevel: "repeatable_read",
                ReadOnly: reportReadOnly,
                LockTimeout: reportReadOnly
                    ? DryRunLockTimeout
                    : ExecuteLockTimeout,
                StatementTimeout: reportReadOnly
                    ? DryRunStatementTimeout
                    : ExecuteStatementTimeout),
            DryRunDigest: digest,
            DigestExcludes:
            [
                "transaction_clock",
                "report_generation_time",
                "estimated_relation_rows",
                "relation_and_index_size_bytes",
            ],
            CanonicalDataByteCount:
                Encoding.UTF8.GetByteCount(canonicalData),
            SafetyDecision: safetyDecision,
            CanExecute: canExecute,
            RequiredAction: requiredAction,
            TotalScoreHistoryRowCount: counts.TotalRows,
            NullScoreAchievedAtRowCount: counts.NullTimestampRows,
            DuplicateRowCount: duplicateRowCount,
            DuplicateGroupCount: groups.LongCount(),
            ExcessRowCount: excessRowCount,
            AffectedAccountCount: affectedAccounts.LongLength,
            AffectedSongCount: affectedSongs.LongLength,
            AffectedAccounts: affectedAccounts,
            AffectedSongs: affectedSongs,
            ClassificationCounts: classificationCounts,
            PerGroupMaxima: groups,
            Maxima: maxima,
            MergeSemantics: MergeSemantics,
            Storage: storage,
            Index: index);

        return new ScoreHistoryDedupAnalysis(
            report,
            rows,
            groups,
            canonicalData);
    }

    private static async Task EnsureAuditSchemaAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = DryRunCommandTimeoutSeconds;
        cmd.CommandText = """
            SELECT
                to_regclass(
                    'public.score_history_dedup_maintenance_runs')
                    IS NOT NULL,
                to_regclass(
                    'public.score_history_dedup_original_rows')
                    IS NOT NULL;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)
            || !reader.GetBoolean(0)
            || !reader.GetBoolean(1))
        {
            throw new InvalidOperationException(
                "Score-history dedup audit schema is missing. Run the " +
                "explicit --initialize-schema-only command before dry run; " +
                "maintenance never creates schema automatically.");
        }
    }

    private static async Task<ScoreHistoryCounts> LoadCountsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = DryRunCommandTimeoutSeconds;
        cmd.CommandText = """
            SELECT
                COUNT(*)::BIGINT,
                COUNT(*) FILTER (
                    WHERE score_achieved_at IS NULL)::BIGINT
            FROM public.score_history;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new ScoreHistoryCounts(
            reader.GetInt64(0),
            reader.GetInt64(1));
    }

    private static async Task<IReadOnlyList<ScoreHistoryOriginalRow>>
        LoadDuplicateRowsAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = DryRunCommandTimeoutSeconds;
        cmd.CommandText = """
            WITH duplicate_keys AS (
                SELECT
                    account_id,
                    song_id,
                    instrument,
                    new_score,
                    score_achieved_at
                FROM public.score_history
                GROUP BY
                    account_id,
                    song_id,
                    instrument,
                    new_score,
                    score_achieved_at
                HAVING COUNT(*) > 1
            )
            SELECT
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
                history.score_achieved_at,
                history.season_rank,
                history.all_time_rank,
                history.difficulty,
                history.changed_at
            FROM public.score_history history
            JOIN duplicate_keys duplicate
              ON duplicate.account_id = history.account_id
             AND duplicate.song_id = history.song_id
             AND duplicate.instrument = history.instrument
             AND duplicate.new_score IS NOT DISTINCT FROM history.new_score
             AND duplicate.score_achieved_at
                    IS NOT DISTINCT FROM history.score_achieved_at;
            """;

        var rows = new List<ScoreHistoryOriginalRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ScoreHistoryOriginalRow(
                Id: reader.GetInt32(0),
                SongId: reader.GetString(1),
                Instrument: reader.GetString(2),
                AccountId: reader.GetString(3),
                OldScore: GetNullableInt32(reader, 4),
                NewScore: GetNullableInt32(reader, 5),
                OldRank: GetNullableInt32(reader, 6),
                NewRank: GetNullableInt32(reader, 7),
                Accuracy: GetNullableInt32(reader, 8),
                IsFullCombo: GetNullableBoolean(reader, 9),
                Stars: GetNullableInt32(reader, 10),
                Percentile: GetNullableFloat(reader, 11),
                Season: GetNullableInt32(reader, 12),
                ScoreAchievedAt: GetNullableUtc(reader, 13),
                SeasonRank: GetNullableInt32(reader, 14),
                AllTimeRank: GetNullableInt32(reader, 15),
                Difficulty: GetNullableInt32(reader, 16),
                ChangedAt: GetUtc(reader, 17)));
        }

        return rows
            .OrderBy(row => row.AccountId, StringComparer.Ordinal)
            .ThenBy(row => row.SongId, StringComparer.Ordinal)
            .ThenBy(row => row.Instrument, StringComparer.Ordinal)
            .ThenBy(row => row.NewScore)
            .ThenBy(row => row.ScoreAchievedAt)
            .ThenBy(row => row.Id)
            .ToArray();
    }

    private static IReadOnlyList<ScoreHistoryDedupGroupReport> BuildGroups(
        IReadOnlyList<ScoreHistoryOriginalRow> rows)
    {
        return rows
            .GroupBy(row => new ScoreHistoryGroupKey(
                row.AccountId,
                row.SongId,
                row.Instrument,
                row.NewScore,
                row.ScoreAchievedAt))
            .OrderBy(group => group.Key.AccountId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.SongId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Instrument, StringComparer.Ordinal)
            .ThenBy(group => group.Key.NewScore)
            .ThenBy(group => group.Key.ScoreAchievedAt)
            .Select(group =>
            {
                var ordered = group.OrderBy(row => row.Id).ToArray();
                var first = ordered[0];
                var variedFields = GetVariedInvariantFields(first, ordered);
                var expectedScore = group.Key.NewScore == 0;
                var classification = expectedScore
                    ? variedFields.Count == 0
                        ? "expected_zero_score_rank_metadata_only"
                        : "blocked_semantic_variance"
                    : variedFields.Count == 0
                        ? "blocked_non_zero_score"
                        : "blocked_non_zero_score_and_semantic_variance";
                var selectedNewRank = SelectCanonicalRank(
                    ordered.Select(row => row.NewRank));
                var selectedAllTimeRank = SelectCanonicalRank(
                    ordered.Select(row => row.AllTimeRank));

                return new ScoreHistoryDedupGroupReport(
                    AccountId: group.Key.AccountId,
                    SongId: group.Key.SongId,
                    Instrument: group.Key.Instrument,
                    NewScore: group.Key.NewScore,
                    ScoreAchievedAt: group.Key.ScoreAchievedAt,
                    RowCount: ordered.LongLength,
                    ExcessRowCount: ordered.LongLength - 1,
                    SurvivorId: first.Id,
                    MaximumId: ordered.Max(row => row.Id),
                    EarliestChangedAt: ordered.Min(row => row.ChangedAt),
                    LatestChangedAt: ordered.Max(row => row.ChangedAt),
                    MinimumNewRank:
                        MinNullable(ordered.Select(row => row.NewRank)),
                    MaximumNewRank:
                        MaxNullable(ordered.Select(row => row.NewRank)),
                    SelectedNewRank: selectedNewRank,
                    MinimumAllTimeRank:
                        MinNullable(ordered.Select(row => row.AllTimeRank)),
                    MaximumAllTimeRank:
                        MaxNullable(ordered.Select(row => row.AllTimeRank)),
                    SelectedAllTimeRank: selectedAllTimeRank,
                    VariedInvariantFields: variedFields,
                    Classification: classification,
                    Allowed: expectedScore && variedFields.Count == 0);
            })
            .ToArray();
    }

    private static IReadOnlyList<string> GetVariedInvariantFields(
        ScoreHistoryOriginalRow first,
        IReadOnlyList<ScoreHistoryOriginalRow> rows)
    {
        var varied = new List<string>();
        AddIfVaried(varied, "old_score", rows, row => row.OldScore, first.OldScore);
        AddIfVaried(varied, "old_rank", rows, row => row.OldRank, first.OldRank);
        AddIfVaried(varied, "accuracy", rows, row => row.Accuracy, first.Accuracy);
        AddIfVaried(
            varied,
            "is_full_combo",
            rows,
            row => row.IsFullCombo,
            first.IsFullCombo);
        AddIfVaried(varied, "stars", rows, row => row.Stars, first.Stars);
        AddIfVaried(
            varied,
            "percentile",
            rows,
            row => row.Percentile,
            first.Percentile);
        AddIfVaried(varied, "season", rows, row => row.Season, first.Season);
        AddIfVaried(
            varied,
            "season_rank",
            rows,
            row => row.SeasonRank,
            first.SeasonRank);
        AddIfVaried(
            varied,
            "difficulty",
            rows,
            row => row.Difficulty,
            first.Difficulty);
        return varied;
    }

    private static void AddIfVaried<T>(
        ICollection<string> varied,
        string name,
        IEnumerable<ScoreHistoryOriginalRow> rows,
        Func<ScoreHistoryOriginalRow, T> selector,
        T first)
    {
        if (rows.Any(row =>
                !EqualityComparer<T>.Default.Equals(selector(row), first)))
        {
            varied.Add(name);
        }
    }

    private static int? SelectCanonicalRank(IEnumerable<int?> values)
    {
        var observed = values.Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        if (observed.Length == 0)
            return null;

        var positive = observed.Where(value => value > 0).ToArray();
        return positive.Length > 0 ? positive.Min() : observed.Min();
    }

    private static int? MinNullable(IEnumerable<int?> values)
    {
        var observed = values.Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        return observed.Length == 0 ? null : observed.Min();
    }

    private static int? MaxNullable(IEnumerable<int?> values)
    {
        var observed = values.Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        return observed.Length == 0 ? null : observed.Max();
    }

    private static async Task<ScoreHistoryDedupIndexReport> LoadIndexAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = DryRunCommandTimeoutSeconds;
        cmd.CommandText = """
            SELECT
                index_state.indisunique,
                index_state.indisvalid,
                index_state.indisready,
                index_state.indnullsnotdistinct,
                index_state.indnkeyatts,
                index_state.indnatts,
                access_method.amname,
                index_state.indpred IS NOT NULL,
                index_state.indexprs IS NOT NULL,
                pg_get_indexdef(index_relation.oid),
                pg_relation_size(index_relation.oid)::BIGINT,
                ARRAY(
                    SELECT attribute.attname
                    FROM unnest(index_state.indkey)
                        WITH ORDINALITY AS key_column(attnum, ordinality)
                    JOIN pg_attribute attribute
                      ON attribute.attrelid = index_state.indrelid
                     AND attribute.attnum = key_column.attnum
                    WHERE key_column.ordinality <= index_state.indnkeyatts
                    ORDER BY key_column.ordinality
                )
            FROM pg_class index_relation
            JOIN pg_namespace index_namespace
              ON index_namespace.oid = index_relation.relnamespace
            JOIN pg_index index_state
              ON index_state.indexrelid = index_relation.oid
            JOIN pg_class table_relation
              ON table_relation.oid = index_state.indrelid
            JOIN pg_namespace table_namespace
              ON table_namespace.oid = table_relation.relnamespace
            JOIN pg_am access_method
              ON access_method.oid = index_relation.relam
            WHERE index_namespace.nspname = 'public'
              AND index_relation.relname = 'ix_sh_dedup'
              AND table_namespace.nspname = 'public'
              AND table_relation.relname = 'score_history';
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new ScoreHistoryDedupIndexReport(
                Name: "ix_sh_dedup",
                State: MissingIndexState,
                Unique: false,
                Valid: false,
                Ready: false,
                NullsNotDistinct: false,
                AccessMethod: null,
                HasPredicate: false,
                HasExpressions: false,
                KeyColumns: [],
                Definition: null,
                SizeBytes: 0);
        }

        var unique = reader.GetBoolean(0);
        var valid = reader.GetBoolean(1);
        var ready = reader.GetBoolean(2);
        var nullsNotDistinct = reader.GetBoolean(3);
        var keyCount = reader.GetInt16(4);
        var attributeCount = reader.GetInt16(5);
        var accessMethod = reader.GetString(6);
        var hasPredicate = reader.GetBoolean(7);
        var hasExpressions = reader.GetBoolean(8);
        var definition = reader.GetString(9);
        var sizeBytes = reader.GetInt64(10);
        var keyColumns = reader.GetFieldValue<string[]>(11);
        var expectedDefinition =
            unique
            && valid
            && ready
            && accessMethod == "btree"
            && !hasPredicate
            && !hasExpressions
            && keyCount == ExpectedIndexColumns.Length
            && attributeCount == ExpectedIndexColumns.Length
            && keyColumns.SequenceEqual(
                ExpectedIndexColumns,
                StringComparer.Ordinal)
            && NormalizeIndexDefinition(definition).Equals(
                NormalizeIndexDefinition(
                    nullsNotDistinct
                        ? ScoreHistoryDedupMaintenanceSchema.NullSafeIndexDdl
                        : ScoreHistoryDedupMaintenanceSchema.LegacyIndexDdl),
                StringComparison.Ordinal);
        var state = !expectedDefinition
            ? UnexpectedIndexState
            : nullsNotDistinct
                ? TargetIndexState
                : LegacyIndexState;

        return new ScoreHistoryDedupIndexReport(
            Name: "ix_sh_dedup",
            State: state,
            Unique: unique,
            Valid: valid,
            Ready: ready,
            NullsNotDistinct: nullsNotDistinct,
            AccessMethod: accessMethod,
            HasPredicate: hasPredicate,
            HasExpressions: hasExpressions,
            KeyColumns: keyColumns,
            Definition: definition,
            SizeBytes: sizeBytes);
    }

    private static async Task<ScoreHistoryDedupStorageReport> LoadStorageAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        ScoreHistoryDedupIndexReport index,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = DryRunCommandTimeoutSeconds;
        cmd.CommandText = """
            SELECT
                GREATEST(table_relation.reltuples::BIGINT, 0),
                pg_relation_size(table_relation.oid)::BIGINT,
                pg_indexes_size(table_relation.oid)::BIGINT,
                pg_total_relation_size(table_relation.oid)::BIGINT
            FROM pg_class table_relation
            JOIN pg_namespace table_namespace
              ON table_namespace.oid = table_relation.relnamespace
            WHERE table_namespace.nspname = 'public'
              AND table_relation.relname = 'score_history';
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("score_history does not exist.");

        return new ScoreHistoryDedupStorageReport(
            EstimatedRows: reader.GetInt64(0),
            HeapSizeBytes: reader.GetInt64(1),
            AllIndexesSizeBytes: reader.GetInt64(2),
            TotalRelationSizeBytes: reader.GetInt64(3),
            DedupIndexSizeBytes: index.SizeBytes);
    }

    private static string BuildCanonicalData(
        IReadOnlyList<ScoreHistoryOriginalRow> rows,
        IReadOnlyList<ScoreHistoryDedupGroupReport> groups,
        ScoreHistoryDedupIndexReport index)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "purpose",
                ScoreHistoryDedupMaintenanceSchema.Purpose);
            writer.WriteNumber(
                "contractVersion",
                ScoreHistoryDedupMaintenanceSchema.ContractVersion);
            writer.WriteStartObject("mergeSemantics");
            writer.WriteString("survivor", MergeSemantics.Survivor);
            writer.WriteString("changedAt", MergeSemantics.ChangedAt);
            writer.WriteString("newRank", MergeSemantics.NewRank);
            writer.WriteString("allTimeRank", MergeSemantics.AllTimeRank);
            writer.WriteStartArray("invariantFields");
            foreach (var field in MergeSemantics.InvariantFields)
                writer.WriteStringValue(field);
            writer.WriteEndArray();
            writer.WriteString("deleteRule", MergeSemantics.DeleteRule);
            writer.WriteEndObject();
            writer.WriteStartObject("index");
            writer.WriteString("state", index.State);
            writer.WriteBoolean("unique", index.Unique);
            writer.WriteBoolean("valid", index.Valid);
            writer.WriteBoolean("ready", index.Ready);
            writer.WriteBoolean(
                "nullsNotDistinct",
                index.NullsNotDistinct);
            writer.WriteString("accessMethod", index.AccessMethod);
            writer.WriteBoolean("hasPredicate", index.HasPredicate);
            writer.WriteBoolean("hasExpressions", index.HasExpressions);
            writer.WriteStartArray("keyColumns");
            foreach (var column in index.KeyColumns)
                writer.WriteStringValue(column);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteStartArray("groups");
            foreach (var group in groups)
            {
                writer.WriteStartObject();
                writer.WriteString("accountId", group.AccountId);
                writer.WriteString("songId", group.SongId);
                writer.WriteString("instrument", group.Instrument);
                WriteNullableInt32(writer, "newScore", group.NewScore);
                if (group.ScoreAchievedAt.HasValue)
                {
                    writer.WriteNumber(
                        "scoreAchievedAtUtcTicks",
                        group.ScoreAchievedAt.Value.ToUniversalTime().Ticks);
                }
                else
                {
                    writer.WriteNull("scoreAchievedAtUtcTicks");
                }
                writer.WriteNumber("rowCount", group.RowCount);
                writer.WriteNumber("excessRowCount", group.ExcessRowCount);
                writer.WriteNumber("survivorId", group.SurvivorId);
                writer.WriteNumber(
                    "earliestChangedAtUtcTicks",
                    group.EarliestChangedAt.ToUniversalTime().Ticks);
                WriteNullableInt32(
                    writer,
                    "selectedNewRank",
                    group.SelectedNewRank);
                WriteNullableInt32(
                    writer,
                    "selectedAllTimeRank",
                    group.SelectedAllTimeRank);
                writer.WriteString("classification", group.Classification);
                writer.WriteStartArray("variedInvariantFields");
                foreach (var field in group.VariedInvariantFields)
                    writer.WriteStringValue(field);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("originalRows");
            foreach (var row in rows)
                WriteCanonicalRow(writer, row);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonicalRow(
        Utf8JsonWriter writer,
        ScoreHistoryOriginalRow row)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", row.Id);
        writer.WriteString("songId", row.SongId);
        writer.WriteString("instrument", row.Instrument);
        writer.WriteString("accountId", row.AccountId);
        WriteNullableInt32(writer, "oldScore", row.OldScore);
        WriteNullableInt32(writer, "newScore", row.NewScore);
        WriteNullableInt32(writer, "oldRank", row.OldRank);
        WriteNullableInt32(writer, "newRank", row.NewRank);
        WriteNullableInt32(writer, "accuracy", row.Accuracy);
        WriteNullableBoolean(writer, "isFullCombo", row.IsFullCombo);
        WriteNullableInt32(writer, "stars", row.Stars);
        if (row.Percentile.HasValue)
        {
            writer.WriteNumber(
                "percentileFloat32Bits",
                BitConverter.SingleToInt32Bits(row.Percentile.Value));
        }
        else
        {
            writer.WriteNull("percentileFloat32Bits");
        }
        WriteNullableInt32(writer, "season", row.Season);
        if (row.ScoreAchievedAt.HasValue)
        {
            writer.WriteNumber(
                "scoreAchievedAtUtcTicks",
                row.ScoreAchievedAt.Value.ToUniversalTime().Ticks);
        }
        else
        {
            writer.WriteNull("scoreAchievedAtUtcTicks");
        }
        WriteNullableInt32(writer, "seasonRank", row.SeasonRank);
        WriteNullableInt32(writer, "allTimeRank", row.AllTimeRank);
        WriteNullableInt32(writer, "difficulty", row.Difficulty);
        writer.WriteNumber(
            "changedAtUtcTicks",
            row.ChangedAt.ToUniversalTime().Ticks);
        writer.WriteEndObject();
    }

    private static void EnsureSafeToExecute(
        ScoreHistoryDedupDryRunReport report)
    {
        if (report.CanExecute)
            return;

        var blockedGroups = report.PerGroupMaxima
            .Where(group => !group.Allowed)
            .Take(5)
            .Select(group =>
                $"{group.AccountId}/{group.SongId}/{group.Instrument}/" +
                $"{group.NewScore?.ToString() ?? "null"}:" +
                $"{group.Classification}" +
                (group.VariedInvariantFields.Count == 0
                    ? string.Empty
                    : $"[{string.Join(",", group.VariedInvariantFields)}]"));
        throw new InvalidOperationException(
            $"Score-history dedup safety gate is {report.SafetyDecision}; " +
            $"index={report.Index.State}; blockedGroups=" +
            $"{string.Join(";", blockedGroups)}. No rows were written.");
    }

    private static bool IsCleanTargetState(
        ScoreHistoryDedupDryRunReport report)
        => report.CanExecute
           && report.DuplicateGroupCount == 0
           && report.Index.State == TargetIndexState;

    private static async Task<ExecutionProvenance> LoadExecutionProvenanceAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = ExecuteCommandTimeoutSeconds;
        cmd.CommandText = """
            SELECT
                current_database(),
                current_user,
                current_setting('server_version_num')::INTEGER;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new ExecutionProvenance(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2));
    }

    private static async Task<long> ReserveMaintenanceRunIdAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = ExecuteCommandTimeoutSeconds;
        cmd.CommandText = """
            SELECT nextval(
                pg_get_serial_sequence(
                    'public.score_history_dedup_maintenance_runs',
                    'maintenance_run_id'));
            """;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task InsertMaintenanceRunAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long maintenanceRunId,
        ScoreHistoryDedupAnalysis analysis,
        ExecutionProvenance provenance,
        string rollbackSql,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = ExecuteCommandTimeoutSeconds;
        cmd.CommandText = """
            INSERT INTO score_history_dedup_maintenance_runs (
                maintenance_run_id,
                maintenance_purpose,
                maintenance_contract_version,
                execution_source,
                dry_run_digest,
                canonical_candidate_data,
                safety_classification,
                database_name,
                database_user,
                server_version_num,
                duplicate_row_count,
                duplicate_group_count,
                excess_row_count,
                affected_account_count,
                affected_song_count,
                original_rows_audited,
                survivor_rows_updated,
                rows_deleted,
                index_replaced,
                index_definition_before,
                index_definition_after,
                rollback_sql)
            VALUES (
                @runId,
                @purpose,
                @contractVersion,
                @executionSource,
                @digest,
                @canonicalData,
                'ready',
                @databaseName,
                @databaseUser,
                @serverVersion,
                @duplicateRows,
                @duplicateGroups,
                @excessRows,
                @affectedAccounts,
                @affectedSongs,
                @duplicateRows,
                @duplicateGroups,
                @excessRows,
                TRUE,
                @indexBefore,
                @indexAfter,
                @rollbackSql);
            """;
        cmd.Parameters.AddWithValue("runId", maintenanceRunId);
        cmd.Parameters.AddWithValue(
            "purpose",
            ScoreHistoryDedupMaintenanceSchema.Purpose);
        cmd.Parameters.AddWithValue(
            "contractVersion",
            ScoreHistoryDedupMaintenanceSchema.ContractVersion);
        cmd.Parameters.AddWithValue(
            "executionSource",
            ScoreHistoryDedupMaintenanceSchema.ExecutionSource);
        cmd.Parameters.AddWithValue(
            "digest",
            analysis.Report.DryRunDigest);
        cmd.Parameters.AddWithValue(
            "canonicalData",
            analysis.CanonicalData);
        cmd.Parameters.AddWithValue(
            "databaseName",
            provenance.DatabaseName);
        cmd.Parameters.AddWithValue(
            "databaseUser",
            provenance.DatabaseUser);
        cmd.Parameters.AddWithValue(
            "serverVersion",
            provenance.ServerVersionNumber);
        cmd.Parameters.AddWithValue(
            "duplicateRows",
            analysis.Report.DuplicateRowCount);
        cmd.Parameters.AddWithValue(
            "duplicateGroups",
            analysis.Report.DuplicateGroupCount);
        cmd.Parameters.AddWithValue(
            "excessRows",
            analysis.Report.ExcessRowCount);
        cmd.Parameters.AddWithValue(
            "affectedAccounts",
            analysis.Report.AffectedAccountCount);
        cmd.Parameters.AddWithValue(
            "affectedSongs",
            analysis.Report.AffectedSongCount);
        cmd.Parameters.AddWithValue(
            "indexBefore",
            analysis.Report.Index.Definition
                ?? throw new InvalidOperationException(
                    "Expected ix_sh_dedup definition is missing."));
        cmd.Parameters.AddWithValue(
            "indexAfter",
            ScoreHistoryDedupMaintenanceSchema.NullSafeIndexDdl);
        cmd.Parameters.AddWithValue("rollbackSql", rollbackSql);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException(
                "Score-history dedup maintenance audit run was not inserted.");
        }
    }

    private static async Task<long> InsertOriginalRowsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        long maintenanceRunId,
        IReadOnlyList<ScoreHistoryOriginalRow> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0)
            return 0;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = ExecuteCommandTimeoutSeconds;
        cmd.CommandText = """
            INSERT INTO score_history_dedup_original_rows (
                maintenance_run_id,
                original_id,
                song_id,
                instrument,
                account_id,
                old_score,
                new_score,
                old_rank,
                new_rank,
                accuracy,
                is_full_combo,
                stars,
                percentile,
                season,
                score_achieved_at,
                season_rank,
                all_time_rank,
                difficulty,
                changed_at)
            SELECT
                @runId,
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
                history.score_achieved_at,
                history.season_rank,
                history.all_time_rank,
                history.difficulty,
                history.changed_at
            FROM public.score_history history
            WHERE history.id = ANY(@originalIds)
            ORDER BY history.id;
            """;
        cmd.Parameters.AddWithValue("runId", maintenanceRunId);
        cmd.Parameters.Add(
            "originalIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            rows.Select(row => row.Id).ToArray();
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> UpdateSurvivorsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        IReadOnlyList<ScoreHistoryDedupGroupReport> groups,
        CancellationToken ct)
    {
        if (groups.Count == 0)
            return 0;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = ExecuteCommandTimeoutSeconds;
        cmd.CommandText = """
            WITH merge_plan AS (
                SELECT *
                FROM unnest(
                    @survivorIds::INTEGER[],
                    @newRanks::INTEGER[],
                    @allTimeRanks::INTEGER[],
                    @changedAts::TIMESTAMPTZ[])
                    AS plan(
                        survivor_id,
                        selected_new_rank,
                        selected_all_time_rank,
                        selected_changed_at)
            )
            UPDATE public.score_history history
            SET
                new_rank = plan.selected_new_rank,
                all_time_rank = plan.selected_all_time_rank,
                changed_at = plan.selected_changed_at
            FROM merge_plan plan
            WHERE history.id = plan.survivor_id;
            """;
        cmd.Parameters.Add(
            "survivorIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            groups.Select(group => group.SurvivorId).ToArray();
        cmd.Parameters.Add(
            "newRanks",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            groups.Select(group => group.SelectedNewRank).ToArray();
        cmd.Parameters.Add(
            "allTimeRanks",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            groups.Select(group => group.SelectedAllTimeRank).ToArray();
        cmd.Parameters.Add(
            "changedAts",
            NpgsqlDbType.Array | NpgsqlDbType.TimestampTz).Value =
            groups.Select(group => group.EarliestChangedAt).ToArray();
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> DeleteNonSurvivorsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        IReadOnlyList<ScoreHistoryOriginalRow> rows,
        IReadOnlyList<ScoreHistoryDedupGroupReport> groups,
        CancellationToken ct)
    {
        if (rows.Count == 0)
            return 0;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = ExecuteCommandTimeoutSeconds;
        cmd.CommandText = """
            DELETE FROM public.score_history history
            WHERE history.id = ANY(@originalIds)
              AND NOT (history.id = ANY(@survivorIds));
            """;
        cmd.Parameters.Add(
            "originalIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            rows.Select(row => row.Id).ToArray();
        cmd.Parameters.Add(
            "survivorIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            groups.Select(group => group.SurvivorId).ToArray();
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task VerifyNoDuplicateGroupsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = ExecuteCommandTimeoutSeconds;
        cmd.CommandText = """
            SELECT COUNT(*)::BIGINT
            FROM (
                SELECT 1
                FROM public.score_history
                GROUP BY
                    account_id,
                    song_id,
                    instrument,
                    new_score,
                    score_achieved_at
                HAVING COUNT(*) > 1
            ) duplicate_groups;
            """;
        var duplicateGroups = Convert.ToInt64(
            await cmd.ExecuteScalarAsync(ct));
        if (duplicateGroups != 0)
        {
            throw new InvalidOperationException(
                $"Score-history still contains {duplicateGroups:N0} " +
                "dedup-key duplicate group(s); all changes were rolled back.");
        }
    }

    private static async Task ReplaceLegacyIndexAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        await using (var create = conn.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandTimeout = ExecuteCommandTimeoutSeconds;
            create.CommandText =
                ScoreHistoryDedupMaintenanceSchema
                    .NullSafeReplacementIndexDdl;
            await create.ExecuteNonQueryAsync(ct);
        }

        await using var cutover = conn.CreateCommand();
        cutover.Transaction = tx;
        cutover.CommandTimeout = ExecuteCommandTimeoutSeconds;
        cutover.CommandText = """
            DROP INDEX public.ix_sh_dedup;
            ALTER INDEX public.ix_sh_dedup_nulls_not_distinct_replacement
                RENAME TO ix_sh_dedup;
            """;
        await cutover.ExecuteNonQueryAsync(ct);
    }

    private static async Task<PriorMaintenanceRun?> LoadPriorRunAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string expectedDigest,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = ExecuteCommandTimeoutSeconds;
        cmd.CommandText = """
            SELECT
                run.maintenance_run_id,
                run.original_rows_audited,
                run.duplicate_group_count,
                run.survivor_rows_updated,
                run.rows_deleted,
                run.index_replaced,
                run.rollback_sql,
                (
                    SELECT COUNT(*)::BIGINT
                    FROM score_history_dedup_original_rows original
                    WHERE original.maintenance_run_id =
                        run.maintenance_run_id
                ) AS persisted_original_rows
            FROM score_history_dedup_maintenance_runs run
            WHERE run.maintenance_purpose = @purpose
              AND run.dry_run_digest = @digest
            ORDER BY run.maintenance_run_id DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue(
            "purpose",
            ScoreHistoryDedupMaintenanceSchema.Purpose);
        cmd.Parameters.AddWithValue("digest", expectedDigest);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var originalRows = reader.GetInt64(1);
        var persistedOriginalRows = reader.GetInt64(7);
        if (originalRows != persistedOriginalRows)
        {
            throw new InvalidOperationException(
                $"Maintenance run {reader.GetInt64(0)} audit row count " +
                $"does not match its immutable run record.");
        }

        return new PriorMaintenanceRun(
            MaintenanceRunId: reader.GetInt64(0),
            DryRunDigest: expectedDigest,
            OriginalRowsAudited: originalRows,
            DuplicateGroupsMerged: reader.GetInt64(2),
            SurvivorRowsUpdated: reader.GetInt64(3),
            RowsDeleted: reader.GetInt64(4),
            IndexReplaced: reader.GetBoolean(5),
            RollbackSql: reader.GetString(6));
    }

    internal static string BuildRollbackSql(
        long maintenanceRunId,
        string digest)
    {
        var normalizedDigest =
            ScoreHistoryDedupMaintenanceCommand.NormalizeDigest(digest);
        return $$"""
            BEGIN;
            SET LOCAL lock_timeout = '3s';
            SET LOCAL statement_timeout = '180s';
            LOCK TABLE public.score_history IN SHARE ROW EXCLUSIVE MODE;

            DO $rollback$
            DECLARE
                expected_original_rows BIGINT;
                persisted_original_rows BIGINT;
            BEGIN
                SELECT original_rows_audited
                INTO expected_original_rows
                FROM public.score_history_dedup_maintenance_runs
                WHERE maintenance_run_id = {{maintenanceRunId}}
                  AND maintenance_purpose =
                      'score_history_null_timestamp_dedup_v1'
                  AND dry_run_digest = '{{normalizedDigest}}';

                IF NOT FOUND THEN
                    RAISE EXCEPTION
                        'Score-history dedup audit run {{maintenanceRunId}} not found.';
                END IF;

                SELECT COUNT(*)::BIGINT
                INTO persisted_original_rows
                FROM public.score_history_dedup_original_rows
                WHERE maintenance_run_id = {{maintenanceRunId}};

                IF persisted_original_rows <> expected_original_rows THEN
                    RAISE EXCEPTION
                        'Score-history dedup audit row count mismatch.';
                END IF;

                IF NOT EXISTS (
                    SELECT 1
                    FROM public.score_history_dedup_maintenance_runs run
                    JOIN pg_class index_relation
                      ON index_relation.relname = 'ix_sh_dedup'
                    JOIN pg_namespace index_namespace
                      ON index_namespace.oid = index_relation.relnamespace
                     AND index_namespace.nspname = 'public'
                    JOIN pg_index index_state
                      ON index_state.indexrelid = index_relation.oid
                    JOIN pg_class table_relation
                      ON table_relation.oid = index_state.indrelid
                     AND table_relation.relname = 'score_history'
                    JOIN pg_namespace table_namespace
                      ON table_namespace.oid = table_relation.relnamespace
                     AND table_namespace.nspname = 'public'
                    WHERE run.maintenance_run_id = {{maintenanceRunId}}
                      AND index_state.indisunique
                      AND index_state.indisvalid
                      AND index_state.indnullsnotdistinct
                      AND regexp_replace(
                            pg_get_indexdef(index_relation.oid),
                            '\s+',
                            ' ',
                            'g')
                          = regexp_replace(
                            trim(trailing ';' FROM run.index_definition_after),
                            '\s+',
                            ' ',
                            'g')
                ) THEN
                    RAISE EXCEPTION
                        'ix_sh_dedup does not exactly match the audited NULLS NOT DISTINCT index.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM pg_class index_relation
                    JOIN pg_namespace index_namespace
                      ON index_namespace.oid = index_relation.relnamespace
                    WHERE index_namespace.nspname = 'public'
                      AND index_relation.relname IN (
                          'ix_sh_dedup_nulls_not_distinct_replacement',
                          'ix_sh_dedup_legacy_replacement')
                ) THEN
                    RAISE EXCEPTION
                        'A score-history dedup replacement index already exists.';
                END IF;

                IF EXISTS (
                    WITH expected_survivors AS (
                        SELECT
                            MIN(original_id) AS survivor_id,
                            (ARRAY_AGG(song_id ORDER BY original_id))[1]
                                AS song_id,
                            (ARRAY_AGG(instrument ORDER BY original_id))[1]
                                AS instrument,
                            (ARRAY_AGG(account_id ORDER BY original_id))[1]
                                AS account_id,
                            (ARRAY_AGG(old_score ORDER BY original_id))[1]
                                AS old_score,
                            (ARRAY_AGG(new_score ORDER BY original_id))[1]
                                AS new_score,
                            (ARRAY_AGG(old_rank ORDER BY original_id))[1]
                                AS old_rank,
                            COALESCE(
                                MIN(new_rank) FILTER (WHERE new_rank > 0),
                                MIN(new_rank) FILTER (
                                    WHERE new_rank IS NOT NULL)
                            ) AS new_rank,
                            (ARRAY_AGG(accuracy ORDER BY original_id))[1]
                                AS accuracy,
                            (ARRAY_AGG(is_full_combo ORDER BY original_id))[1]
                                AS is_full_combo,
                            (ARRAY_AGG(stars ORDER BY original_id))[1]
                                AS stars,
                            (ARRAY_AGG(percentile ORDER BY original_id))[1]
                                AS percentile,
                            (ARRAY_AGG(season ORDER BY original_id))[1]
                                AS season,
                            (ARRAY_AGG(score_achieved_at ORDER BY original_id))[1]
                                AS score_achieved_at,
                            (ARRAY_AGG(season_rank ORDER BY original_id))[1]
                                AS season_rank,
                            COALESCE(
                                MIN(all_time_rank) FILTER (
                                    WHERE all_time_rank > 0),
                                MIN(all_time_rank) FILTER (
                                    WHERE all_time_rank IS NOT NULL)
                            ) AS all_time_rank,
                            (ARRAY_AGG(difficulty ORDER BY original_id))[1]
                                AS difficulty,
                            MIN(changed_at) AS changed_at
                        FROM public.score_history_dedup_original_rows
                        WHERE maintenance_run_id = {{maintenanceRunId}}
                        GROUP BY
                            account_id,
                            song_id,
                            instrument,
                            new_score,
                            score_achieved_at
                    )
                    SELECT 1
                    FROM expected_survivors expected
                    LEFT JOIN public.score_history current
                      ON current.id = expected.survivor_id
                    WHERE current.id IS NULL
                       OR ROW(
                            current.song_id,
                            current.instrument,
                            current.account_id,
                            current.old_score,
                            current.new_score,
                            current.old_rank,
                            current.new_rank,
                            current.accuracy,
                            current.is_full_combo,
                            current.stars,
                            current.percentile,
                            current.season,
                            current.score_achieved_at,
                            current.season_rank,
                            current.all_time_rank,
                            current.difficulty,
                            current.changed_at)
                          IS DISTINCT FROM
                          ROW(
                            expected.song_id,
                            expected.instrument,
                            expected.account_id,
                            expected.old_score,
                            expected.new_score,
                            expected.old_rank,
                            expected.new_rank,
                            expected.accuracy,
                            expected.is_full_combo,
                            expected.stars,
                            expected.percentile,
                            expected.season,
                            expected.score_achieved_at,
                            expected.season_rank,
                            expected.all_time_rank,
                            expected.difficulty,
                            expected.changed_at)
                ) THEN
                    RAISE EXCEPTION
                        'Current survivor rows no longer match the audited merge; rollback refused.';
                END IF;
            END
            $rollback$;

            CREATE UNIQUE INDEX ix_sh_dedup_legacy_replacement
            ON public.score_history
            USING btree (
                account_id,
                song_id,
                instrument,
                new_score,
                score_achieved_at);

            DROP INDEX public.ix_sh_dedup;
            ALTER INDEX public.ix_sh_dedup_legacy_replacement
                RENAME TO ix_sh_dedup;

            DELETE FROM public.score_history current
            USING public.score_history_dedup_original_rows original
            WHERE original.maintenance_run_id = {{maintenanceRunId}}
              AND current.id = original.original_id;

            INSERT INTO public.score_history (
                id,
                song_id,
                instrument,
                account_id,
                old_score,
                new_score,
                old_rank,
                new_rank,
                accuracy,
                is_full_combo,
                stars,
                percentile,
                season,
                score_achieved_at,
                season_rank,
                all_time_rank,
                difficulty,
                changed_at)
            SELECT
                original_id,
                song_id,
                instrument,
                account_id,
                old_score,
                new_score,
                old_rank,
                new_rank,
                accuracy,
                is_full_combo,
                stars,
                percentile,
                season,
                score_achieved_at,
                season_rank,
                all_time_rank,
                difficulty,
                changed_at
            FROM public.score_history_dedup_original_rows
            WHERE maintenance_run_id = {{maintenanceRunId}}
            ORDER BY original_id;

            DO $rollback_verify$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM public.score_history_dedup_original_rows original
                    LEFT JOIN public.score_history restored
                      ON restored.id = original.original_id
                    WHERE original.maintenance_run_id = {{maintenanceRunId}}
                      AND (
                          restored.id IS NULL
                          OR ROW(
                              restored.song_id,
                              restored.instrument,
                              restored.account_id,
                              restored.old_score,
                              restored.new_score,
                              restored.old_rank,
                              restored.new_rank,
                              restored.accuracy,
                              restored.is_full_combo,
                              restored.stars,
                              restored.percentile,
                              restored.season,
                              restored.score_achieved_at,
                              restored.season_rank,
                              restored.all_time_rank,
                              restored.difficulty,
                              restored.changed_at)
                             IS DISTINCT FROM
                             ROW(
                              original.song_id,
                              original.instrument,
                              original.account_id,
                              original.old_score,
                              original.new_score,
                              original.old_rank,
                              original.new_rank,
                              original.accuracy,
                              original.is_full_combo,
                              original.stars,
                              original.percentile,
                              original.season,
                              original.score_achieved_at,
                              original.season_rank,
                              original.all_time_rank,
                              original.difficulty,
                              original.changed_at)
                      )
                ) THEN
                    RAISE EXCEPTION
                        'Restored score-history rows do not match immutable audit rows.';
                END IF;
            END
            $rollback_verify$;

            SELECT setval(
                'public.score_history_id_seq',
                GREATEST(
                    COALESCE((
                        SELECT MAX(id)
                        FROM public.score_history), 0) + 1,
                    (
                        SELECT last_value
                            + CASE WHEN is_called THEN 1 ELSE 0 END
                        FROM public.score_history_id_seq)),
                false);
            COMMIT;
            """;
    }

    private static string NormalizeIndexDefinition(string definition)
        => string.Join(
            ' ',
            definition
                .Trim()
                .TrimEnd(';')
                .Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries));

    private static int? GetNullableInt32(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static bool? GetNullableBoolean(
        NpgsqlDataReader reader,
        int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);

    private static float? GetNullableFloat(
        NpgsqlDataReader reader,
        int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetFloat(ordinal);

    private static DateTime GetUtc(NpgsqlDataReader reader, int ordinal)
        => NormalizeUtc(reader.GetDateTime(ordinal));

    private static DateTime? GetNullableUtc(
        NpgsqlDataReader reader,
        int ordinal)
        => reader.IsDBNull(ordinal) ? null : GetUtc(reader, ordinal);

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    private static void WriteNullableInt32(
        Utf8JsonWriter writer,
        string propertyName,
        int? value)
    {
        if (value.HasValue)
            writer.WriteNumber(propertyName, value.Value);
        else
            writer.WriteNull(propertyName);
    }

    private static void WriteNullableBoolean(
        Utf8JsonWriter writer,
        string propertyName,
        bool? value)
    {
        if (value.HasValue)
            writer.WriteBoolean(propertyName, value.Value);
        else
            writer.WriteNull(propertyName);
    }

    private sealed record ScoreHistoryCounts(
        long TotalRows,
        long NullTimestampRows);

    private sealed record ScoreHistoryGroupKey(
        string AccountId,
        string SongId,
        string Instrument,
        int? NewScore,
        DateTime? ScoreAchievedAt);

    private sealed record ScoreHistoryOriginalRow(
        int Id,
        string SongId,
        string Instrument,
        string AccountId,
        int? OldScore,
        int? NewScore,
        int? OldRank,
        int? NewRank,
        int? Accuracy,
        bool? IsFullCombo,
        int? Stars,
        float? Percentile,
        int? Season,
        DateTime? ScoreAchievedAt,
        int? SeasonRank,
        int? AllTimeRank,
        int? Difficulty,
        DateTime ChangedAt);

    private sealed record ScoreHistoryDedupAnalysis(
        ScoreHistoryDedupDryRunReport Report,
        IReadOnlyList<ScoreHistoryOriginalRow> Rows,
        IReadOnlyList<ScoreHistoryDedupGroupReport> Groups,
        string CanonicalData);

    private sealed record ExecutionProvenance(
        string DatabaseName,
        string DatabaseUser,
        int ServerVersionNumber);

    private sealed record PriorMaintenanceRun(
        long MaintenanceRunId,
        string DryRunDigest,
        long OriginalRowsAudited,
        long DuplicateGroupsMerged,
        long SurvivorRowsUpdated,
        long RowsDeleted,
        bool IndexReplaced,
        string RollbackSql)
    {
        public ScoreHistoryDedupExecuteReport ToExecuteReport(
            bool alreadyApplied)
            => new(
                Purpose: ScoreHistoryDedupMaintenanceSchema.Purpose,
                DryRunDigest: DryRunDigest,
                MaintenanceRunId: MaintenanceRunId,
                AlreadyApplied: alreadyApplied,
                NoChangesRequired: false,
                OriginalRowsAudited: OriginalRowsAudited,
                DuplicateGroupsMerged: DuplicateGroupsMerged,
                SurvivorRowsUpdated: SurvivorRowsUpdated,
                RowsDeleted: RowsDeleted,
                IndexReplaced: IndexReplaced,
                IndexStateAfter: TargetIndexState,
                RollbackSql: RollbackSql);
    }
}

public sealed record ScoreHistoryDedupTransactionContract(
    string IsolationLevel,
    bool ReadOnly,
    string LockTimeout,
    string StatementTimeout);

public sealed record ScoreHistoryDedupMergeSemantics(
    string Survivor,
    string ChangedAt,
    string NewRank,
    string AllTimeRank,
    IReadOnlyList<string> InvariantFields,
    string DeleteRule);

public sealed record ScoreHistoryDedupClassificationCount(
    string Classification,
    long GroupCount,
    long RowCount,
    long ExcessRowCount,
    bool Allowed);

public sealed record ScoreHistoryDedupGroupReport(
    string AccountId,
    string SongId,
    string Instrument,
    int? NewScore,
    DateTime? ScoreAchievedAt,
    long RowCount,
    long ExcessRowCount,
    int SurvivorId,
    int MaximumId,
    DateTime EarliestChangedAt,
    DateTime LatestChangedAt,
    int? MinimumNewRank,
    int? MaximumNewRank,
    int? SelectedNewRank,
    int? MinimumAllTimeRank,
    int? MaximumAllTimeRank,
    int? SelectedAllTimeRank,
    IReadOnlyList<string> VariedInvariantFields,
    string Classification,
    bool Allowed);

public sealed record ScoreHistoryDedupMaxima(
    long MaximumRowsInGroup,
    long MaximumExcessRowsInGroup,
    int? MaximumOriginalId,
    int? MaximumObservedNewRank,
    int? MaximumObservedAllTimeRank);

public sealed record ScoreHistoryDedupStorageReport(
    long EstimatedRows,
    long HeapSizeBytes,
    long AllIndexesSizeBytes,
    long TotalRelationSizeBytes,
    long DedupIndexSizeBytes);

public sealed record ScoreHistoryDedupIndexReport(
    string Name,
    string State,
    bool Unique,
    bool Valid,
    bool Ready,
    bool NullsNotDistinct,
    string? AccessMethod,
    bool HasPredicate,
    bool HasExpressions,
    IReadOnlyList<string> KeyColumns,
    string? Definition,
    long SizeBytes);

public sealed record ScoreHistoryDedupDryRunReport(
    string Purpose,
    int ContractVersion,
    ScoreHistoryDedupTransactionContract Transaction,
    string DryRunDigest,
    IReadOnlyList<string> DigestExcludes,
    long CanonicalDataByteCount,
    string SafetyDecision,
    bool CanExecute,
    string RequiredAction,
    long TotalScoreHistoryRowCount,
    long NullScoreAchievedAtRowCount,
    long DuplicateRowCount,
    long DuplicateGroupCount,
    long ExcessRowCount,
    long AffectedAccountCount,
    long AffectedSongCount,
    IReadOnlyList<string> AffectedAccounts,
    IReadOnlyList<string> AffectedSongs,
    IReadOnlyList<ScoreHistoryDedupClassificationCount>
        ClassificationCounts,
    IReadOnlyList<ScoreHistoryDedupGroupReport> PerGroupMaxima,
    ScoreHistoryDedupMaxima Maxima,
    ScoreHistoryDedupMergeSemantics MergeSemantics,
    ScoreHistoryDedupStorageReport Storage,
    ScoreHistoryDedupIndexReport Index);

public sealed record ScoreHistoryDedupExecuteReport(
    string Purpose,
    string DryRunDigest,
    long? MaintenanceRunId,
    bool AlreadyApplied,
    bool NoChangesRequired,
    long OriginalRowsAudited,
    long DuplicateGroupsMerged,
    long SurvivorRowsUpdated,
    long RowsDeleted,
    bool IndexReplaced,
    string IndexStateAfter,
    string? RollbackSql);
