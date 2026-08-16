using System.Diagnostics;
using System.Text.Json;
using FSTService.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FSTService.Scraping.Replay;

public sealed record ReplayPhaseExecutionResult(
    int RefreshedScopes,
    int FailedScopes,
    long InsertedRows,
    long DeletedRows,
    BandCurrentProjectionOperationMetrics OperationMetrics);

public sealed record ReplayExecutionResult(
    string OutputPath,
    string PackageRootHash,
    ReplayPhaseMetrics Metrics);

public interface IReplayPhaseAdapter
{
    ReplayPhaseDescriptor Descriptor { get; }

    Task<ReplayPhaseExecutionResult> ExecuteAsync(
        NpgsqlDataSource dataSource,
        TierOneReplayInput input,
        ReplayExecutionProfile executionProfile,
        CancellationToken cancellationToken);
}

public sealed class BandCurrentProjectionReplayAdapter(
    ILoggerFactory loggerFactory) : IReplayPhaseAdapter
{
    public ReplayPhaseDescriptor Descriptor =>
        ReplayPhaseCatalog.BandCurrentProjectionRefresh;

    public async Task<ReplayPhaseExecutionResult> ExecuteAsync(
        NpgsqlDataSource dataSource,
        TierOneReplayInput input,
        ReplayExecutionProfile executionProfile,
        CancellationToken cancellationToken)
    {
        var builder = new BandCurrentProjectionBuilder(
            dataSource,
            loggerFactory.CreateLogger<BandCurrentProjectionBuilder>());
        await builder.EnsureSchemaAsync(cancellationToken);
        var scopes = input.Scopes
            .Select(static scope =>
                new BandCurrentProjectionScopeKey(
                    scope.SongId,
                    scope.BandType,
                    scope.RankingScope,
                    scope.ScopeComboId))
            .ToArray();
        var result = await builder.RefreshScopesAsync(
            scopes,
            new BandCurrentProjectionRebuildOptions
            {
                CommandTimeoutSeconds =
                    input.InputManifest.Bounds.StatementTimeoutSeconds,
                DisableSynchronousCommit =
                    executionProfile.DisableSynchronousCommit,
                SkipUnchangedScopes =
                    executionProfile.SkipUnchangedScopes,
                MaxParallelBandTypes =
                    executionProfile.MaxParallelBandTypes,
                CandidateCleanupBatchSize =
                    executionProfile.CandidateCleanupBatchSize,
                CandidateCleanupMaxBatches =
                    executionProfile.CandidateCleanupMaxBatches,
                UseBatchedMemberStatsAggregation =
                    executionProfile
                        .UseBatchedMemberStatsAggregation,
                ClearExisting = false,
                PublishOnSuccess = true,
                BandTypes = scopes
                    .Select(static scope => scope.BandType)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                IncludeOverallScopes = true,
                IncludeComboScopes = false,
            },
            cancellationToken);
        if (result.FailedScopes > 0 ||
            !result.PublishResult.Published ||
            result.PublishResult.MissingScopes > 0)
        {
            throw new ReplayException(
                ReplayFailureKind.PhaseFailed,
                ReplayExitCode.PhaseFailed,
                "Band current-projection replay did not publish every isolated scope.");
        }
        return new ReplayPhaseExecutionResult(
            result.ScopeCount,
            result.FailedScopes,
            result.InsertedRows,
            result.DeletedRows,
            result.OperationMetrics
                ?? BandCurrentProjectionOperationMetrics.Empty);
    }
}

public sealed record ReplayExportedArtifact(
    string DatasetId,
    string Path,
    int SchemaVersion,
    long RowCount,
    byte[] Bytes)
{
    public string Sha256 =>
        TierZeroCanonicalJson.Sha256Hex(Bytes);
}

public sealed class TierOneReplayExporter
{
    public async Task<IReadOnlyList<ReplayExportedArtifact>> ExportAsync(
        NpgsqlDataSource dataSource,
        TierOneReplayBounds bounds,
        CancellationToken cancellationToken)
    {
        var projection = await ExportProjectionAsync(
            dataSource,
            bounds.MaximumOutputRows,
            cancellationToken);
        var scopes = await ExportScopesAsync(
            dataSource,
            bounds.MaximumScopes,
            cancellationToken);
        var state = await ExportStateAsync(
            dataSource,
            cancellationToken);
        return
        [
            new ReplayExportedArtifact(
                TierOneReplayFormat.ProjectionOutputId,
                TierOneReplayFormat.ProjectionOutputPath,
                1,
                projection.Count,
                TierOneReplayCanonical.ToJsonLines(projection)),
            new ReplayExportedArtifact(
                TierOneReplayFormat.ScopeOutputId,
                TierOneReplayFormat.ScopeOutputPath,
                1,
                scopes.Count,
                TierOneReplayCanonical.ToJsonLines(scopes)),
            new ReplayExportedArtifact(
                TierOneReplayFormat.StateOutputId,
                TierOneReplayFormat.StateOutputPath,
                1,
                state.Count,
                TierOneReplayCanonical.ToJsonLines(state)),
        ];
    }

    private static async Task<IReadOnlyList<ReplayOutputProjectionRow>>
        ExportProjectionAsync(
            NpgsqlDataSource dataSource,
            int maximumRows,
            CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT song_id,
                   band_type,
                   ranking_scope,
                   scope_combo_id,
                   team_key,
                   entry_combo_id,
                   entry_instrument_combo,
                   team_members,
                   member_account_ids,
                   member_instrument_ids,
                   member_scores,
                   member_accuracies,
                   member_full_combos,
                   member_stars,
                   member_difficulties,
                   score,
                   accuracy,
                   is_full_combo,
                   stars,
                   difficulty,
                   season,
                   rank,
                   total_entries,
                   percentile,
                   end_time,
                   first_seen_at,
                   last_updated_at,
                   projection_generation
            FROM current_band_leaderboard_entries
            ORDER BY song_id,
                     band_type,
                     ranking_scope,
                     scope_combo_id,
                     projection_generation,
                     rank,
                     team_key
            """;
        var rows = new List<ReplayOutputProjectionRow>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count >= maximumRows)
                throw OutputRejected("Replay projection output exceeded its row bound.");
            rows.Add(new ReplayOutputProjectionRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetFieldValue<string[]>(7),
                reader.GetFieldValue<string[]>(8),
                reader.GetFieldValue<int[]>(9),
                reader.GetFieldValue<int[]>(10),
                reader.GetFieldValue<int[]>(11),
                reader.GetFieldValue<int[]>(12),
                reader.GetFieldValue<int[]>(13),
                reader.GetFieldValue<int[]>(14),
                reader.GetInt32(15),
                reader.IsDBNull(16) ? null : reader.GetInt32(16),
                reader.IsDBNull(17) ? null : reader.GetBoolean(17),
                reader.IsDBNull(18) ? null : reader.GetInt32(18),
                reader.IsDBNull(19) ? null : reader.GetInt32(19),
                reader.IsDBNull(20) ? null : reader.GetInt32(20),
                reader.GetInt32(21),
                reader.GetInt32(22),
                reader.GetDouble(23),
                reader.IsDBNull(24) ? null : reader.GetString(24),
                ToUtc(reader.GetDateTime(25)),
                ToUtc(reader.GetDateTime(26)),
                reader.GetInt64(27)));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<ReplayOutputScopeRow>>
        ExportScopesAsync(
            NpgsqlDataSource dataSource,
            int maximumRows,
            CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT song_id,
                   band_type,
                   ranking_scope,
                   scope_combo_id,
                   projection_generation,
                   published_generation,
                   row_count,
                   published_row_count,
                   status
            FROM band_current_projection_scope
            ORDER BY song_id, band_type, ranking_scope, scope_combo_id
            """;
        var rows = new List<ReplayOutputScopeRow>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count >= maximumRows)
                throw OutputRejected("Replay scope output exceeded its row bound.");
            rows.Add(new ReplayOutputScopeRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetString(8)));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<ReplayOutputStateRow>>
        ExportStateAsync(
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT current_generation,
                   row_count,
                   scope_count,
                   failed_scope_count
            FROM band_current_projection_state
            WHERE id = TRUE
            """;
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw OutputRejected("Replay projection state output is missing.");
        var row = new ReplayOutputStateRow(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3));
        if (await reader.ReadAsync(cancellationToken))
            throw OutputRejected("Replay projection state output is ambiguous.");
        return [row];
    }

    private static DateTimeOffset ToUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static ReplayException OutputRejected(string message) =>
        new(
            ReplayFailureKind.OutputFailed,
            ReplayExitCode.OutputFailed,
            message);
}

public sealed class TierOneReplayRunner(
    ReplayRootAdmission rootAdmission,
    ReplayDatabaseTargetGuard targetGuard,
    ReplayExecutionEnvironment environment,
    ILoggerFactory loggerFactory,
    TimeProvider? timeProvider = null,
    TierOneReplayPackageReader? packageReader = null,
    TierOneReplayImporter? importer = null,
    TierOneReplayExporter? exporter = null,
    IReadOnlyList<IReplayPhaseAdapter>? adapters = null)
{
    private readonly ILogger<TierOneReplayRunner> _logger =
        loggerFactory.CreateLogger<TierOneReplayRunner>();
    private readonly TimeProvider _timeProvider =
        timeProvider ?? TimeProvider.System;
    private readonly TierOneReplayPackageReader _packageReader =
        packageReader ?? new TierOneReplayPackageReader();
    private readonly TierOneReplayImporter _importer =
        importer ?? new TierOneReplayImporter();
    private readonly TierOneReplayExporter _exporter =
        exporter ?? new TierOneReplayExporter();
    private readonly IReadOnlyDictionary<(string Phase, string Subphase),
        IReplayPhaseAdapter> _adapters =
        (adapters ??
         [new BandCurrentProjectionReplayAdapter(loggerFactory)])
        .ToDictionary(
            static adapter => (
                adapter.Descriptor.PhaseId,
                adapter.Descriptor.SubphaseId));

    public async Task<ReplayExecutionResult> ExecuteAsync(
        ReplayCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Kind != ReplayCommandKind.Execute)
            throw new ArgumentException("Replay execution command is required.", nameof(command));
        var descriptor = ReplayPhaseCatalog.Resolve(
            command.PhaseId!,
            command.SubphaseId!);
        var executionProfile =
            ReplayExecutionProfileCatalog.Resolve(
                command.ExecutionProfile);
        if (!_adapters.TryGetValue(
                (descriptor.PhaseId, descriptor.SubphaseId),
                out var adapter))
        {
            throw new ReplayException(
                ReplayFailureKind.Usage,
                ReplayExitCode.Usage,
                "No explicit adapter is registered for the replay phase.");
        }
        var paths = rootAdmission.AdmitExecution(
            command.ParentPackagePath!,
            command.InputPackagePath!,
            command.OutputPath!);
        var input = await _packageReader.LoadAsync(
            paths,
            descriptor,
            cancellationToken);
        if (!string.Equals(
                input.InputManifest.ReplayId,
                command.ReplayId,
                StringComparison.Ordinal))
        {
            throw new ReplayException(
                ReplayFailureKind.PackageRejected,
                ReplayExitCode.PackageRejected,
                "Replay command ID does not match the Tier-1 input manifest.");
        }
        rootAdmission.RequireCapacity(
            input.PackageBytes,
            Math.Max(
                input.PackageBytes,
                input.InputManifest.Bounds.MaximumPackageBytes));

        await using var dataSource = targetGuard.CreateDataSource(
            input.InputManifest.Bounds);
        var inputRoot = input.InputPackageManifest.PackageRootHash!;
        var database = await targetGuard.ValidateAsync(
            dataSource,
            command.ReplayId!,
            inputRoot,
            input.InputManifest.SourceDatabaseSystemIdentifier,
            ReplayDatabaseTargetGuard.CreatedStatus,
            TierOneReplayDatabaseSchema.Fingerprint,
            cancellationToken);
        var startedAt = _timeProvider.GetUtcNow();
        var outputDraft = CreateOutputDraft(
            command,
            input,
            database,
            startedAt);
        var writer = await TierZeroPackageWriter.CreateAsync(
            paths.OutputPackage,
            outputDraft,
            cancellationToken);

        try
        {
            await _importer.ImportAsync(
                dataSource,
                input,
                cancellationToken);
            await ReplayDatabaseTargetGuard.TransitionAsync(
                dataSource,
                command.ReplayId!,
                inputRoot,
                ReplayDatabaseTargetGuard.CreatedStatus,
                ReplayDatabaseTargetGuard.ImportedStatus,
                cancellationToken);
            _ = await targetGuard.ValidateAsync(
                dataSource,
                command.ReplayId!,
                inputRoot,
                input.InputManifest.SourceDatabaseSystemIdentifier,
                ReplayDatabaseTargetGuard.ImportedStatus,
                TierOneReplayDatabaseSchema.Fingerprint,
                cancellationToken);

            var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes();
            var databaseBefore = await CaptureDatabaseMetricsAsync(
                dataSource,
                cancellationToken);
            var stopwatch = Stopwatch.StartNew();
            var phaseResult = await adapter.ExecuteAsync(
                dataSource,
                input,
                executionProfile,
                cancellationToken);
            stopwatch.Stop();
            var databaseAfter = await CaptureDatabaseMetricsAsync(
                dataSource,
                cancellationToken);
            var exported = await _exporter.ExportAsync(
                dataSource,
                input.InputManifest.Bounds,
                cancellationToken);
            var metrics = new ReplayPhaseMetrics(
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
                Math.Round(
                    (process.TotalProcessorTime - cpuBefore)
                    .TotalMilliseconds,
                    3),
                Math.Max(
                    0,
                    GC.GetTotalAllocatedBytes() - allocatedBefore),
                process.PeakWorkingSet64,
                Math.Max(0, databaseAfter.WalBytes - databaseBefore.WalBytes),
                Math.Max(0, databaseAfter.TempBytes - databaseBefore.TempBytes),
                phaseResult.RefreshedScopes,
                phaseResult.FailedScopes,
                phaseResult.InsertedRows,
                phaseResult.DeletedRows,
                phaseResult.OperationMetrics
                    .SuccessfulScopeTransactions,
                phaseResult.OperationMetrics
                    .DerivedSuccessfulScopeCommandExecutions,
                phaseResult.OperationMetrics
                    .DerivedSuccessfulScopeRoundTrips,
                phaseResult.OperationMetrics
                    .DerivedMemberStatsAggregationPasses);

            _ = await targetGuard.ValidateAsync(
                dataSource,
                command.ReplayId!,
                inputRoot,
                input.InputManifest.SourceDatabaseSystemIdentifier,
                ReplayDatabaseTargetGuard.ImportedStatus,
                TierOneReplayDatabaseSchema.Fingerprint,
                cancellationToken);
            await ReplayDatabaseTargetGuard.TransitionAsync(
                dataSource,
                command.ReplayId!,
                inputRoot,
                ReplayDatabaseTargetGuard.ImportedStatus,
                ReplayDatabaseTargetGuard.PhaseCompletedStatus,
                cancellationToken);

            var completedAt = _timeProvider.GetUtcNow();
            var outputManifest = CreateOutputManifest(
                command,
                input,
                database,
                exported,
                metrics,
                executionProfile,
                startedAt,
                completedAt);
            var outputManifestBytes =
                TierOneReplayCanonical.SerializeOutput(outputManifest);
            var metricsBytes =
                TierZeroCanonicalJson.Serialize(metrics);
            foreach (var artifact in exported)
            {
                await writer.AddArtifactAsync(
                    new TierZeroArtifactRegistration(
                        artifact.DatasetId.Replace(
                            '.',
                            '-'),
                        artifact.Path,
                        "application/x-ndjson",
                        artifact.SchemaVersion,
                        artifact.RowCount,
                        artifact.Bytes.LongLength),
                    artifact.Bytes,
                    cancellationToken);
            }
            await writer.AddArtifactAsync(
                new TierZeroArtifactRegistration(
                    "replay-resource-metrics",
                    TierOneReplayFormat.MetricsPath,
                    "application/json",
                    1,
                    1,
                    metricsBytes.LongLength),
                metricsBytes,
                cancellationToken);
            await writer.AddArtifactAsync(
                new TierZeroArtifactRegistration(
                    "tier1-phase-output",
                    TierOneReplayFormat.OutputManifestPath,
                    "application/json",
                    1,
                    1,
                    outputManifestBytes.LongLength),
                outputManifestBytes,
                cancellationToken);
            var sealedPackage = await writer.SealAsync(
                completedAt,
                cancellationToken: cancellationToken);
            try
            {
                await ReplayDatabaseTargetGuard.TransitionAsync(
                    dataSource,
                    command.ReplayId!,
                    inputRoot,
                    ReplayDatabaseTargetGuard.PhaseCompletedStatus,
                    ReplayDatabaseTargetGuard.CompletedStatus,
                    CancellationToken.None);
            }
            catch (Exception exception) when (
                exception is NpgsqlException or
                TimeoutException or
                IOException or
                ReplayException)
            {
                _logger.LogWarning(
                    exception,
                    "Sealed replay output succeeded but final isolated database marker completion failed.");
            }
            return new ReplayExecutionResult(
                paths.OutputPackage,
                sealedPackage.PackageRootHash!,
                metrics);
        }
        catch (OperationCanceledException)
        {
            await MarkFailureAsync(
                writer,
                dataSource,
                command,
                inputRoot,
                ReplayFailureKind.PhaseFailed,
                ReplayExitCode.Cancelled,
                cancelled: true);
            throw;
        }
        catch (ReplayException exception)
        {
            await MarkFailureAsync(
                writer,
                dataSource,
                command,
                inputRoot,
                exception.Kind,
                exception.ExitCode,
                cancelled: false);
            throw;
        }
        catch (Exception exception)
        {
            await MarkFailureAsync(
                writer,
                dataSource,
                command,
                inputRoot,
                ReplayFailureKind.PhaseFailed,
                ReplayExitCode.PhaseFailed,
                cancelled: false);
            throw new ReplayException(
                ReplayFailureKind.PhaseFailed,
                ReplayExitCode.PhaseFailed,
                "Replay phase failed.",
                exception);
        }
    }

    private TierZeroPackageDraft CreateOutputDraft(
        ReplayCommand command,
        TierOneReplayInput input,
        ReplayDatabaseIdentity database,
        DateTimeOffset createdAt)
    {
        var configuration =
            TierZeroConfigurationFingerprinter.Create(
                new Dictionary<string, string?>
                {
                    ["Replay:Adapter"] =
                        ReplayPhaseCatalog.CurrentProjectionSubphaseId,
                    ["Replay:NoPublication"] = "true",
                    ["Replay:Phase"] =
                        "post_band_maintenance",
                    ["Replay:ExecutionProfile"] =
                        command.ExecutionProfile,
                    ["Replay:ProtocolVersion"] =
                        TierOneReplayFormat.OutputVersion.ToString(),
                },
                [
                    "Replay:Adapter",
                    "Replay:NoPublication",
                    "Replay:Phase",
                    "Replay:ExecutionProfile",
                    "Replay:ProtocolVersion",
                ]);
        return new TierZeroPackageDraft(
            $"{command.ReplayId}-output-{command.Attempt}",
            input.ParentManifest.Source,
            environment.Implementation,
            new TierZeroDatabaseIdentity(
                database.PostgreSqlMajorVersion,
                database.Extensions,
                database.SchemaFingerprint),
            configuration,
            TierZeroSummaryReferences.Empty,
            [
                new TierZeroParentRootHash(
                    "tier0-parent",
                    input.ParentManifest.PackageRootHash!),
                new TierZeroParentRootHash(
                    "tier1-input",
                    input.InputPackageManifest.PackageRootHash!),
            ],
            command.Attempt,
            environment.ProducerIdentity,
            createdAt);
    }

    private TierOnePhaseOutputManifest CreateOutputManifest(
        ReplayCommand command,
        TierOneReplayInput input,
        ReplayDatabaseIdentity database,
        IReadOnlyList<ReplayExportedArtifact> exported,
        ReplayPhaseMetrics metrics,
        ReplayExecutionProfile executionProfile,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt) =>
        new(
            TierOneReplayFormat.OutputFormatId,
            TierOneReplayFormat.OutputVersion,
            command.ReplayId!,
            command.Attempt,
            command.PhaseId!,
            command.SubphaseId!,
            ReplayPhaseCatalog.CurrentProjectionAdapterVersion,
            input.ParentManifest.PackageRootHash!,
            input.InputPackageManifest.PackageRootHash!,
            PhaseProgressCatalog.OperationId,
            PhaseProgressCatalog.PlanVersion,
            environment.Implementation,
            database,
            exported.Select(static artifact =>
                new TierOneOutputDatasetReference(
                    artifact.DatasetId,
                    artifact.Path,
                    artifact.SchemaVersion,
                    artifact.RowCount,
                    artifact.Sha256))
                .OrderBy(
                    static artifact => artifact.DatasetId,
                    StringComparer.Ordinal)
                .ToArray(),
            executionProfile.Id,
            ReplayTimingSemantics.ProductionComparableTiming,
            executionProfile.TimingComparisonReason,
            metrics,
            startedAt,
            completedAt,
            NoPublication: true,
            ManifestRootHash: null);

    private async Task MarkFailureAsync(
        TierZeroPackageWriter writer,
        NpgsqlDataSource dataSource,
        ReplayCommand command,
        string inputRoot,
        ReplayFailureKind kind,
        ReplayExitCode exitCode,
        bool cancelled)
    {
        try
        {
            await ReplayDatabaseTargetGuard.MarkFailedAsync(
                dataSource,
                command.ReplayId!,
                inputRoot);
        }
        catch (Exception exception) when (
            exception is NpgsqlException or
            TimeoutException or
            IOException)
        {
            _logger.LogWarning(
                exception,
                "Failed to mark isolated replay database attempt as failed.");
        }
        try
        {
            var failure = new ReplayFailureRecord(
                command.ReplayId!,
                command.Attempt,
                command.PhaseId!,
                command.SubphaseId!,
                kind,
                exitCode,
                cancelled,
                _timeProvider.GetUtcNow());
            var bytes = TierZeroCanonicalJson.Serialize(failure);
            await writer.AddArtifactAsync(
                new TierZeroArtifactRegistration(
                    "replay-failure",
                    TierOneReplayFormat.FailurePath,
                    "application/json",
                    1,
                    1,
                    bytes.LongLength),
                bytes,
                CancellationToken.None);
            await writer.MarkInterruptedAsync(
                cancelled
                    ? "isolated replay cancelled"
                    : $"isolated replay failed: {kind}",
                cancellationToken: CancellationToken.None);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            TierZeroPackageException)
        {
            _logger.LogWarning(
                exception,
                "Failed to persist isolated replay failure evidence.");
        }
    }

    private static async Task<DatabaseMetricsSnapshot>
        CaptureDatabaseMetricsAsync(
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pg_wal_lsn_diff(
                       pg_current_wal_lsn(),
                       '0/0'::pg_lsn)::BIGINT,
                   temp_bytes
            FROM pg_stat_database
            WHERE datname = current_database()
            """;
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new DatabaseMetricsSnapshot(0, 0);
        return new DatabaseMetricsSnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1));
    }

    private sealed record DatabaseMetricsSnapshot(
        long WalBytes,
        long TempBytes);
}

public sealed class ReplayComparisonService(
    TierOneReplayPackageReader? packageReader = null)
{
    private readonly TierOneReplayPackageReader _packageReader =
        packageReader ?? new TierOneReplayPackageReader();

    public async Task<ReplayComparisonReport> CompareAsync(
        string baselinePath,
        string candidatePath,
        string reportPath,
        ReplayComparisonExpectations expectations,
        CancellationToken cancellationToken)
    {
        var baseline = await LoadOutputAsync(
            baselinePath,
            expectations.BaselineImageDigest,
            expectations.BaselineGitCommit,
            expectations.BaselineRevision,
            expectations.BaselineAttempt,
            cancellationToken);
        var candidate = await LoadOutputAsync(
            candidatePath,
            expectations.CandidateImageDigest,
            expectations.CandidateGitCommit,
            expectations.CandidateRevision,
            expectations.CandidateAttempt,
            cancellationToken);
        if (!string.Equals(
                baseline.Manifest.TierOneInputRootHash,
                candidate.Manifest.TierOneInputRootHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                baseline.Manifest.PhaseId,
                candidate.Manifest.PhaseId,
                StringComparison.Ordinal) ||
            !string.Equals(
                baseline.Manifest.SubphaseId,
                candidate.Manifest.SubphaseId,
                StringComparison.Ordinal))
        {
            throw ComparisonRejected(
                "Replay comparison inputs do not share one phase/input lineage.");
        }

        var baselineById = baseline.Manifest.Outputs.ToDictionary(
            static output => output.DatasetId,
            StringComparer.Ordinal);
        var candidateById = candidate.Manifest.Outputs.ToDictionary(
            static output => output.DatasetId,
            StringComparer.Ordinal);
        if (!baselineById.Keys.OrderBy(static key => key, StringComparer.Ordinal)
            .SequenceEqual(
                candidateById.Keys.OrderBy(static key => key, StringComparer.Ordinal)))
        {
            throw ComparisonRejected(
                "Replay comparison output dataset sets differ.");
        }
        var datasets = baselineById.Keys
            .OrderBy(static key => key, StringComparer.Ordinal)
            .Select(key =>
            {
                var first = baselineById[key];
                var second = candidateById[key];
                return new ReplayDatasetComparison(
                    key,
                    first.RowCount,
                    second.RowCount,
                    first.Sha256,
                    second.Sha256,
                    first.RowCount == second.RowCount &&
                    string.Equals(
                        first.Sha256,
                        second.Sha256,
                        StringComparison.Ordinal));
            })
            .ToArray();
        var elapsedDelta =
            candidate.Manifest.Metrics.ElapsedMilliseconds -
            baseline.Manifest.Metrics.ElapsedMilliseconds;
        var report = new ReplayComparisonReport(
            "fst.tier1.phase-comparison",
            TierOneReplayFormat.ComparisonVersion,
            baseline.Envelope.PackageRootHash!,
            candidate.Envelope.PackageRootHash!,
            baseline.Manifest.TierOneInputRootHash,
            baseline.Manifest.PhaseId,
            baseline.Manifest.SubphaseId,
            baseline.Manifest.ExecutionProfile,
            candidate.Manifest.ExecutionProfile,
            datasets,
            datasets.All(static dataset => dataset.ExactParity),
            ReplayTimingSemantics.ProductionComparableTiming,
            ReplayTimingSemantics.ComparisonTimingReason,
            baseline.Manifest.Metrics.ElapsedMilliseconds,
            candidate.Manifest.Metrics.ElapsedMilliseconds,
            Math.Round(elapsedDelta, 3),
            baseline.Manifest.Metrics.ElapsedMilliseconds <= 0
                ? 0
                : Math.Round(
                    elapsedDelta /
                    baseline.Manifest.Metrics.ElapsedMilliseconds * 100,
                    3),
            baseline.Manifest.Metrics.WalBytes,
            candidate.Manifest.Metrics.WalBytes,
            candidate.Manifest.Metrics.WalBytes -
            baseline.Manifest.Metrics.WalBytes,
            baseline.Manifest.Metrics.PeakWorkingSetBytes,
            candidate.Manifest.Metrics.PeakWorkingSetBytes,
            candidate.Manifest.Metrics.PeakWorkingSetBytes -
            baseline.Manifest.Metrics.PeakWorkingSetBytes,
            baseline.Manifest.Metrics.SuccessfulScopeTransactions,
            candidate.Manifest.Metrics.SuccessfulScopeTransactions,
            baseline.Manifest.Metrics
                .DerivedSuccessfulScopeCommandExecutions,
            candidate.Manifest.Metrics
                .DerivedSuccessfulScopeCommandExecutions,
            baseline.Manifest.Metrics
                .DerivedSuccessfulScopeRoundTrips,
            candidate.Manifest.Metrics
                .DerivedSuccessfulScopeRoundTrips,
            baseline.Manifest.Metrics
                .DerivedMemberStatsAggregationPasses,
            candidate.Manifest.Metrics
                .DerivedMemberStatsAggregationPasses,
            candidate.Manifest.Metrics
                .DerivedMemberStatsAggregationPasses -
            baseline.Manifest.Metrics
                .DerivedMemberStatsAggregationPasses,
            baseline.Manifest.Metrics
                .DerivedMemberStatsAggregationPasses <= 0
                ? 0
                : Math.Round(
                    (
                        candidate.Manifest.Metrics
                            .DerivedMemberStatsAggregationPasses -
                        baseline.Manifest.Metrics
                            .DerivedMemberStatsAggregationPasses
                    ) /
                    (double)baseline.Manifest.Metrics
                        .DerivedMemberStatsAggregationPasses * 100,
                    3));
        var bytes = TierZeroCanonicalJson.Serialize(report);
        await AtomicWriteAsync(
            reportPath,
            bytes,
            cancellationToken);
        if (!report.ExactParity)
            throw ComparisonRejected("Replay output parity failed.");
        return report;
    }

    private async Task<LoadedOutput> LoadOutputAsync(
        string packagePath,
        string expectedImageDigest,
        string expectedGitCommit,
        string expectedRevision,
        int expectedAttempt,
        CancellationToken cancellationToken)
    {
        var verification = await TierZeroPackageVerifier.VerifyAsync(
            packagePath,
            cancellationToken: cancellationToken);
        if (!verification.IsValid ||
            verification.Manifest is not
            { Status: TierZeroPackageStatus.Sealed } envelope)
        {
            throw ComparisonRejected("Replay output package verification failed.");
        }
        var artifact = TierOneReplayPackageReader.RequireArtifact(
            envelope,
            TierOneReplayFormat.OutputManifestPath);
        var bytes = await TierOneReplayPackageReader.ReadArtifactAsync(
            packagePath,
            artifact,
            cancellationToken);
        TierOnePhaseOutputManifest manifest;
        try
        {
            manifest =
                TierZeroCanonicalJson.Deserialize<TierOnePhaseOutputManifest>(
                    bytes);
        }
        catch (JsonException exception)
        {
            throw new ReplayException(
                ReplayFailureKind.ComparisonFailed,
                ReplayExitCode.ComparisonFailed,
                "Replay output manifest is invalid JSON.",
                exception);
        }
        TierOneReplayCanonical.RequireValidOutputRoot(manifest);
        ReplayExecutionProfile executionProfile;
        try
        {
            executionProfile =
                ReplayExecutionProfileCatalog.Resolve(
                    manifest.ExecutionProfile);
        }
        catch (ReplayException)
        {
            throw ComparisonRejected(
                "Replay output execution profile is invalid.");
        }
        if (!bytes.AsSpan().SequenceEqual(
                TierOneReplayCanonical.SerializeOutput(manifest)) ||
            !manifest.NoPublication ||
            !string.Equals(
                manifest.FormatId,
                TierOneReplayFormat.OutputFormatId,
                StringComparison.Ordinal) ||
            manifest.Version != TierOneReplayFormat.OutputVersion ||
            manifest.Attempt != envelope.Attempt ||
            manifest.Attempt != expectedAttempt ||
            manifest.StartedAtUtc == default ||
            manifest.CompletedAtUtc < manifest.StartedAtUtc ||
            !string.Equals(
                manifest.PhasePlanId,
                envelope.PhasePlan.Id,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.PhasePlanVersion,
                envelope.PhasePlan.Version,
                StringComparison.Ordinal) ||
            manifest.ProductionComparableTiming ||
            !string.Equals(
                manifest.TimingComparisonReason,
                executionProfile.TimingComparisonReason,
                StringComparison.Ordinal))
        {
            throw ComparisonRejected("Replay output manifest is invalid.");
        }
        _ = ReplayPhaseCatalog.Resolve(
            manifest.PhaseId,
            manifest.SubphaseId);
        if (!string.Equals(
                envelope.Build.OciImageDigest,
                expectedImageDigest,
                StringComparison.Ordinal) ||
            !string.Equals(
                envelope.Build.OciImageRevision,
                expectedRevision,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                envelope.Build.GitCommit,
                expectedGitCommit,
                StringComparison.OrdinalIgnoreCase))
        {
            throw ComparisonRejected(
                "Replay output package does not match its expected lane image.");
        }
        var tierZeroParent = envelope.ParentRootHashes.SingleOrDefault(
            static parent =>
                parent.LogicalParent == "tier0-parent");
        var tierOneInput = envelope.ParentRootHashes.SingleOrDefault(
            static parent =>
                parent.LogicalParent == "tier1-input");
        if (envelope.ParentRootHashes.Count != 2 ||
            tierZeroParent is null ||
            tierOneInput is null ||
            !string.Equals(
                tierZeroParent.Sha256,
                manifest.TierZeroParentRootHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                tierOneInput.Sha256,
                manifest.TierOneInputRootHash,
                StringComparison.Ordinal) ||
            !TierZeroCanonicalJson.Serialize(envelope.Build).AsSpan()
                .SequenceEqual(
                    TierZeroCanonicalJson.Serialize(
                        manifest.Implementation)) ||
            envelope.Database.MajorVersion !=
            manifest.Database.PostgreSqlMajorVersion ||
            !envelope.Database.Extensions.SequenceEqual(
                manifest.Database.Extensions) ||
            !string.Equals(
                envelope.Database.SchemaFingerprint,
                manifest.Database.SchemaFingerprint,
                StringComparison.Ordinal))
        {
            throw ComparisonRejected(
                "Replay output manifest lineage does not match its envelope.");
        }
        var expectedOutputs =
            ReplayPhaseCatalog.BandCurrentProjectionRefresh
                .OutputDatasetIds
                .ToHashSet(StringComparer.Ordinal);
        if (manifest.Outputs.Count != expectedOutputs.Count ||
            manifest.Outputs
                .Select(static output => output.DatasetId)
                .Distinct(StringComparer.Ordinal)
                .Count() != expectedOutputs.Count ||
            manifest.Outputs.Any(output =>
                !expectedOutputs.Contains(output.DatasetId)) ||
            !manifest.Outputs
                .Select(static output => output.DatasetId)
                .SequenceEqual(manifest.Outputs
                    .Select(static output => output.DatasetId)
                    .OrderBy(static id => id, StringComparer.Ordinal)))
        {
            throw ComparisonRejected(
                "Replay output manifest dataset allowlist is incomplete.");
        }
        var allowedArtifacts = manifest.Outputs
            .Select(static output => output.Path)
            .Append(TierOneReplayFormat.OutputManifestPath)
            .Append(TierOneReplayFormat.MetricsPath)
            .ToHashSet(StringComparer.Ordinal);
        if (envelope.Artifacts.Count != allowedArtifacts.Count ||
            envelope.Artifacts.Any(artifact =>
                !allowedArtifacts.Contains(artifact.Path)))
        {
            throw ComparisonRejected(
                "Replay output envelope contains undeclared artifacts.");
        }
        foreach (var output in manifest.Outputs)
        {
            var expectedPath = output.DatasetId switch
            {
                TierOneReplayFormat.ProjectionOutputId =>
                    TierOneReplayFormat.ProjectionOutputPath,
                TierOneReplayFormat.ScopeOutputId =>
                    TierOneReplayFormat.ScopeOutputPath,
                TierOneReplayFormat.StateOutputId =>
                    TierOneReplayFormat.StateOutputPath,
                _ => throw ComparisonRejected(
                    "Replay output dataset is not allowlisted."),
            };
            var outputArtifact =
                TierOneReplayPackageReader.RequireArtifact(
                    envelope,
                    output.Path);
            if (!string.Equals(
                    output.Path,
                    expectedPath,
                    StringComparison.Ordinal) ||
                output.SchemaVersion != 1 ||
                output.RowCount < 0 ||
                outputArtifact.RowCount != output.RowCount ||
                !string.Equals(
                    outputArtifact.Sha256,
                    output.Sha256,
                    StringComparison.Ordinal))
            {
                throw ComparisonRejected(
                    $"Replay output dataset '{output.DatasetId}' does not match its envelope.");
            }
        }
        var metricsArtifact =
            TierOneReplayPackageReader.RequireArtifact(
                envelope,
                TierOneReplayFormat.MetricsPath);
        var metricsBytes =
            await TierOneReplayPackageReader.ReadArtifactAsync(
                packagePath,
                metricsArtifact,
                cancellationToken);
        ReplayPhaseMetrics metrics;
        try
        {
            metrics =
                TierZeroCanonicalJson.Deserialize<ReplayPhaseMetrics>(
                    metricsBytes);
        }
        catch (JsonException exception)
        {
            throw new ReplayException(
                ReplayFailureKind.ComparisonFailed,
                ReplayExitCode.ComparisonFailed,
                "Replay metrics artifact is invalid JSON.",
                exception);
        }
        if (!metricsBytes.AsSpan().SequenceEqual(
                TierZeroCanonicalJson.Serialize(metrics)) ||
            !TierZeroCanonicalJson.Serialize(metrics).AsSpan()
                .SequenceEqual(
                    TierZeroCanonicalJson.Serialize(
                        manifest.Metrics)))
        {
            throw ComparisonRejected(
                "Replay metrics artifact does not match its output manifest.");
        }
        return new LoadedOutput(envelope, manifest);
    }

    private static async Task AtomicWriteAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var temporary =
            $"{path}.partial-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            await using (var stream =
                         TierZeroRegularFile.CreateNewWrite(
                             temporary,
                             64 * 1024))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            TierZeroRegularFile.Move(
                temporary,
                path,
                overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
                TierZeroRegularFile.DeleteFile(temporary);
        }
    }

    private static ReplayException ComparisonRejected(
        string message) =>
        new(
            ReplayFailureKind.ComparisonFailed,
            ReplayExitCode.ComparisonFailed,
            message);

    private sealed record LoadedOutput(
        TierZeroEvidenceManifest Envelope,
        TierOnePhaseOutputManifest Manifest);
}
