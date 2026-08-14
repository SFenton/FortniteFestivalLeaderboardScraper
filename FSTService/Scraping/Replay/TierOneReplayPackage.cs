using System.Text;
using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Scraping.Replay;

public sealed class TierOneReplayPackageReader
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    public async Task<TierOneReplayInput> LoadAsync(
        AdmittedReplayPaths paths,
        ReplayPhaseDescriptor phase,
        CancellationToken cancellationToken)
    {
        var parentResult = await TierZeroPackageVerifier.VerifyAsync(
            paths.ParentPackage,
            cancellationToken: cancellationToken);
        var inputResult = await TierZeroPackageVerifier.VerifyAsync(
            paths.InputPackage,
            cancellationToken: cancellationToken);
        var parent = RequireValidPackage(parentResult, "Tier-0 parent");
        var inputPackage = RequireValidPackage(
            inputResult,
            "Tier-1 input envelope");
        if (parent.Status != TierZeroPackageStatus.Sealed ||
            inputPackage.Status != TierZeroPackageStatus.Sealed)
        {
            throw Rejected("Replay input packages must be successfully sealed.");
        }

        var parentRoot = parent.PackageRootHash!;
        var inputParent = inputPackage.ParentRootHashes.SingleOrDefault(
            static item => item.LogicalParent == "tier0-parent");
        if (inputParent is null ||
            !string.Equals(
                inputParent.Sha256,
                parentRoot,
                StringComparison.Ordinal))
        {
            throw Rejected("Tier-1 input envelope does not reference the verified Tier-0 parent.");
        }
        RequireCanonicalEqual(
            inputPackage.Build,
            parent.Build,
            "build lineage");
        RequireCanonicalEqual(
            inputPackage.Database,
            parent.Database,
            "database lineage");
        RequireCanonicalEqual(
            inputPackage.Configuration,
            parent.Configuration,
            "configuration lineage");
        RequireCanonicalEqual(
            inputPackage.PhasePlan,
            parent.PhasePlan,
            "phase-plan lineage");

        var manifestArtifact = RequireArtifact(
            inputPackage,
            TierOneReplayFormat.InputManifestPath);
        var manifestBytes = await ReadArtifactAsync(
            paths.InputPackage,
            manifestArtifact,
            cancellationToken);
        TierOnePhaseInputManifest manifest;
        try
        {
            manifest =
                TierZeroCanonicalJson.Deserialize<TierOnePhaseInputManifest>(
                    manifestBytes);
        }
        catch (JsonException exception)
        {
            throw new ReplayException(
                ReplayFailureKind.PackageRejected,
                ReplayExitCode.PackageRejected,
                "Tier-1 input manifest is invalid JSON.",
                exception);
        }
        TierOneReplayCanonical.RequireValidInputRoot(manifest);
        if (!manifestBytes.AsSpan().SequenceEqual(
                TierOneReplayCanonical.SerializeInput(manifest)))
        {
            throw Rejected("Tier-1 input manifest bytes are not canonical.");
        }
        ValidateManifest(
            manifest,
            parent,
            inputPackage,
            phase);

        var packageBytes = Directory
            .EnumerateFiles(
                paths.InputPackage,
                "*",
                SearchOption.AllDirectories)
            .Select(static file => new FileInfo(file).Length)
            .Aggregate(0L, static (total, length) =>
                checked(total + length));
        if (packageBytes > manifest.Bounds.MaximumPackageBytes ||
            packageBytes >
            TierOneReplayBounds.Conservative.MaximumPackageBytes)
        {
            throw Rejected("Tier-1 input package exceeds the bounded replay size.");
        }

        var scopes = await ReadDatasetAsync<ReplayRequestedScopeRow>(
            paths.InputPackage,
            inputPackage,
            manifest,
            TierOneReplayFormat.ScopesDatasetId,
            TierOneReplayBounds.Conservative.MaximumScopes,
            cancellationToken);
        var entries = await ReadDatasetAsync<ReplayBandEntryRow>(
            paths.InputPackage,
            inputPackage,
            manifest,
            TierOneReplayFormat.EntriesDatasetId,
            TierOneReplayBounds.Conservative.MaximumBandEntries,
            cancellationToken);
        var memberStats = await ReadDatasetAsync<ReplayBandMemberStatRow>(
            paths.InputPackage,
            inputPackage,
            manifest,
            TierOneReplayFormat.MemberStatsDatasetId,
            TierOneReplayBounds.Conservative.MaximumMemberStats,
            cancellationToken);
        ValidateRows(
            scopes,
            entries,
            memberStats,
            manifest.Bounds,
            cancellationToken);
        return new TierOneReplayInput(
            parent,
            inputPackage,
            manifest,
            scopes,
            entries,
            memberStats,
            packageBytes);
    }

    internal static async Task<byte[]> ReadArtifactAsync(
        string packageRoot,
        TierZeroArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        var path = TierZeroPackagePath.ResolveUnderRoot(
            packageRoot,
            artifact.Path);
        var snapshot = TierZeroRegularFile.Inspect(path);
        if (snapshot.Length != artifact.CompressedBytes)
            throw Rejected($"Replay artifact size changed: {artifact.Path}");
        var bytes = await TierZeroRegularFile.ReadAllBytesAsync(
            path,
            snapshot,
            TierOneReplayBounds.Conservative.MaximumPackageBytes,
            cancellationToken);
        if (!string.Equals(
                TierZeroCanonicalJson.Sha256Hex(bytes),
                artifact.Sha256,
                StringComparison.Ordinal))
        {
            throw Rejected($"Replay artifact hash changed: {artifact.Path}");
        }
        return bytes;
    }

    private static TierZeroEvidenceManifest RequireValidPackage(
        TierZeroVerificationResult result,
        string description)
    {
        if (!result.IsValid ||
            result.Manifest is null)
        {
            throw Rejected(
                $"{description} verification failed: " +
                string.Join(
                    ",",
                    result.Failures.Select(static failure =>
                        failure.Kind)));
        }
        return result.Manifest;
    }

    private static void ValidateManifest(
        TierOnePhaseInputManifest manifest,
        TierZeroEvidenceManifest parent,
        TierZeroEvidenceManifest inputPackage,
        ReplayPhaseDescriptor phase)
    {
        if (!string.Equals(
                manifest.FormatId,
                TierOneReplayFormat.InputFormatId,
                StringComparison.Ordinal) ||
            manifest.Version != TierOneReplayFormat.Version ||
            string.IsNullOrWhiteSpace(manifest.ReplayId) ||
            manifest.SourceCutUtc == default ||
            manifest.SourceCutUtc.Offset != TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(
                manifest.SourceDatabaseSystemIdentifier) ||
            !manifest.SourceDatabaseSystemIdentifier.All(char.IsDigit) ||
            !string.Equals(
                manifest.TierZeroParentRootHash,
                parent.PackageRootHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.PhasePlanId,
                PhaseProgressCatalog.OperationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.PhasePlanVersion,
                PhaseProgressCatalog.PlanVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.PhaseId,
                phase.PhaseId,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.SubphaseId,
                phase.SubphaseId,
                StringComparison.Ordinal) ||
            manifest.AdapterVersion != phase.AdapterVersion ||
            !manifest.DependencyPhaseIds.SequenceEqual(
                phase.DependencyPhaseIds) ||
            manifest.Bounds is null ||
            manifest.Datasets is null)
        {
            throw Rejected("Tier-1 input manifest identity is invalid.");
        }
        if (!string.Equals(
                inputPackage.PhasePlan.Id,
                manifest.PhasePlanId,
                StringComparison.Ordinal) ||
            !string.Equals(
                inputPackage.PhasePlan.Version,
                manifest.PhasePlanVersion,
                StringComparison.Ordinal))
        {
            throw Rejected("Tier-1 input phase-plan identity does not match its envelope.");
        }
        if (manifest.Bounds.MaximumPackageBytes <= 0 ||
            manifest.Bounds.MaximumScopes <= 0 ||
            manifest.Bounds.MaximumBandEntries <= 0 ||
            manifest.Bounds.MaximumMemberStats <= 0 ||
            manifest.Bounds.MaximumOutputRows <= 0 ||
            manifest.Bounds.StatementTimeoutSeconds is <= 0 or > 300 ||
            manifest.Bounds.LockTimeoutSeconds is <= 0 or > 30 ||
            manifest.Bounds.MaximumPackageBytes >
            TierOneReplayBounds.Conservative.MaximumPackageBytes ||
            manifest.Bounds.MaximumScopes >
            TierOneReplayBounds.Conservative.MaximumScopes ||
            manifest.Bounds.MaximumBandEntries >
            TierOneReplayBounds.Conservative.MaximumBandEntries ||
            manifest.Bounds.MaximumMemberStats >
            TierOneReplayBounds.Conservative.MaximumMemberStats ||
            manifest.Bounds.MaximumOutputRows >
            TierOneReplayBounds.Conservative.MaximumOutputRows)
        {
            throw Rejected("Tier-1 input manifest exceeds replay safety bounds.");
        }

        var expectedIds = phase.InputDatasetIds
            .ToHashSet(StringComparer.Ordinal);
        if (manifest.Datasets.Count != expectedIds.Count ||
            manifest.Datasets.Select(static dataset => dataset.DatasetId)
                .Distinct(StringComparer.Ordinal)
                .Count() != expectedIds.Count ||
            manifest.Datasets.Any(dataset =>
                !expectedIds.Contains(dataset.DatasetId)))
        {
            throw Rejected("Tier-1 input dataset allowlist is invalid.");
        }
        if (!manifest.Datasets
            .Select(static dataset => dataset.DatasetId)
            .SequenceEqual(manifest.Datasets
                .Select(static dataset => dataset.DatasetId)
                .OrderBy(static id => id, StringComparer.Ordinal)))
        {
            throw Rejected("Tier-1 input datasets are not in canonical ID order.");
        }

        var allowedArtifacts = manifest.Datasets
            .Select(static dataset => dataset.Path)
            .Append(TierOneReplayFormat.InputManifestPath)
            .ToHashSet(StringComparer.Ordinal);
        if (inputPackage.Artifacts.Count != allowedArtifacts.Count ||
            inputPackage.Artifacts.Any(artifact =>
                !allowedArtifacts.Contains(artifact.Path)))
        {
            throw Rejected("Tier-1 input envelope contains undeclared artifacts.");
        }
        foreach (var dataset in manifest.Datasets)
        {
            var contract = DatasetContract(dataset.DatasetId);
            if (dataset.SchemaVersion != 1 ||
                dataset.RowCount < 0 ||
                dataset.UncompressedBytes < 0 ||
                !string.Equals(
                    dataset.Path,
                    contract.Path,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    dataset.Completeness,
                    contract.Completeness,
                    StringComparison.Ordinal))
            {
                throw Rejected("Tier-1 input dataset metadata is invalid.");
            }
            var artifact = RequireArtifact(inputPackage, dataset.Path);
            if (artifact.RowCount != dataset.RowCount ||
                artifact.UncompressedBytes != dataset.UncompressedBytes ||
                !string.Equals(
                    artifact.Sha256,
                    dataset.Sha256,
                    StringComparison.Ordinal))
            {
                throw Rejected(
                    $"Tier-1 dataset '{dataset.DatasetId}' does not match its envelope.");
            }
        }
    }

    private static async Task<IReadOnlyList<T>> ReadDatasetAsync<T>(
        string packageRoot,
        TierZeroEvidenceManifest envelope,
        TierOnePhaseInputManifest manifest,
        string datasetId,
        int hardMaximumRows,
        CancellationToken cancellationToken)
    {
        var dataset = manifest.Datasets.Single(
            item => item.DatasetId == datasetId);
        if (dataset.RowCount > hardMaximumRows)
            throw Rejected($"Tier-1 dataset '{datasetId}' exceeds its row limit.");
        var artifact = RequireArtifact(envelope, dataset.Path);
        var bytes = await ReadArtifactAsync(
            packageRoot,
            artifact,
            cancellationToken);
        if (bytes.LongLength != dataset.UncompressedBytes ||
            (bytes.Length > 0 && bytes[^1] != (byte)'\n'))
        {
            throw Rejected($"Tier-1 dataset '{datasetId}' bytes are invalid.");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ReplayException(
                ReplayFailureKind.PackageRejected,
                ReplayExitCode.PackageRejected,
                $"Tier-1 dataset '{datasetId}' is not valid UTF-8.",
                exception);
        }
        var lines = text.Split(
            '\n',
            StringSplitOptions.None);
        if (lines.Length == 0 ||
            lines[^1].Length != 0 ||
            lines[..^1].Any(static line => line.Length == 0))
        {
            throw Rejected(
                $"Tier-1 dataset '{datasetId}' contains noncanonical blank lines.");
        }

        var rows = new List<T>(
            checked((int)dataset.RowCount));
        foreach (var line in lines[..^1])
        {
            if (rows.Count >= hardMaximumRows)
                throw Rejected($"Tier-1 dataset '{datasetId}' exceeds its row limit.");
            try
            {
                var lineBytes = Encoding.UTF8.GetBytes(line);
                var row =
                    TierZeroCanonicalJson.Deserialize<T>(
                        lineBytes);
                if (!lineBytes.AsSpan().SequenceEqual(
                        TierZeroCanonicalJson.Serialize(row)))
                {
                    throw Rejected(
                        $"Tier-1 dataset '{datasetId}' contains noncanonical JSON.");
                }
                rows.Add(row);
            }
            catch (JsonException exception)
            {
                throw new ReplayException(
                    ReplayFailureKind.PackageRejected,
                    ReplayExitCode.PackageRejected,
                    $"Tier-1 dataset '{datasetId}' contains invalid JSON.",
                    exception);
            }
        }
        if (rows.Count != dataset.RowCount)
            throw Rejected($"Tier-1 dataset '{datasetId}' row count changed.");
        return rows;
    }

    private static void ValidateRows(
        IReadOnlyList<ReplayRequestedScopeRow> scopes,
        IReadOnlyList<ReplayBandEntryRow> entries,
        IReadOnlyList<ReplayBandMemberStatRow> memberStats,
        TierOneReplayBounds bounds,
        CancellationToken cancellationToken)
    {
        if (scopes.Count is 0 ||
            scopes.Count > bounds.MaximumScopes ||
            entries.Count is 0 ||
            entries.Count > bounds.MaximumBandEntries ||
            memberStats.Count is 0 ||
            memberStats.Count > bounds.MaximumMemberStats)
        {
            throw Rejected("Tier-1 replay datasets are empty or exceed declared bounds.");
        }
        var normalizedScopes = scopes
            .Select(static scope => new ReplayRequestedScopeRow(
                scope.SongId.Trim(),
                scope.BandType.Trim(),
                scope.RankingScope.Trim(),
                scope.ScopeComboId.Trim()))
            .ToArray();
        if (normalizedScopes.Any(static scope =>
                string.IsNullOrWhiteSpace(scope.SongId) ||
                scope.RankingScope != "overall" ||
                scope.ScopeComboId.Length != 0 ||
                !BandInstrumentMapping.AllBandTypes.Contains(
                    scope.BandType,
                    StringComparer.Ordinal)) ||
            normalizedScopes.Distinct().Count() != normalizedScopes.Length ||
            normalizedScopes.Select(static scope => scope.BandType)
                .Distinct(StringComparer.Ordinal)
                .Count() != 1)
        {
            throw Rejected(
                "Protocol v1 accepts unique overall scopes for exactly one supported band type.");
        }
        var scopeKeys = normalizedScopes
            .Select(static scope => (scope.SongId, scope.BandType))
            .ToHashSet();
        if (entries.Any(entry =>
                !scopeKeys.Contains((entry.SongId, entry.BandType)) ||
                string.IsNullOrWhiteSpace(entry.TeamKey) ||
                string.IsNullOrWhiteSpace(entry.InstrumentCombo) ||
                entry.TeamMembers.Count !=
                BandInstrumentMapping.ExpectedMemberCount(entry.BandType) ||
                !entry.TeamMembers.SequenceEqual(
                    entry.TeamMembers.OrderBy(
                        static account => account,
                        StringComparer.Ordinal)) ||
                entry.TeamMembers.Distinct(StringComparer.Ordinal).Count() !=
                entry.TeamMembers.Count ||
                !string.Equals(
                    entry.TeamKey,
                    string.Join(':', entry.TeamMembers),
                    StringComparison.Ordinal) ||
                entry.InstrumentCombo.Split(
                    ':',
                    StringSplitOptions.RemoveEmptyEntries).Length !=
                BandInstrumentMapping.ExpectedMemberCount(entry.BandType) ||
                entry.Score < 0 ||
                entry.FirstSeenAtUtc == default ||
                entry.LastUpdatedAtUtc == default ||
                entry.FirstSeenAtUtc.Offset != TimeSpan.Zero ||
                entry.LastUpdatedAtUtc.Offset != TimeSpan.Zero ||
                entry.FirstSeenAtUtc > entry.LastUpdatedAtUtc) ||
            entries.Select(static entry => (
                    entry.SongId,
                    entry.BandType,
                    entry.TeamKey,
                    entry.InstrumentCombo))
                .Distinct()
                .Count() != entries.Count)
        {
            throw Rejected("Tier-1 band-entry rows violate the bounded input contract.");
        }
        var entriesByKey = entries.ToDictionary(static entry => (
            entry.SongId,
            entry.BandType,
            entry.TeamKey,
            entry.InstrumentCombo));
        if (memberStats.Any(stat =>
                !entriesByKey.ContainsKey((
                    stat.SongId,
                    stat.BandType,
                    stat.TeamKey,
                    stat.InstrumentCombo)) ||
                stat.MemberIndex < 0 ||
                string.IsNullOrWhiteSpace(stat.AccountId)) ||
            memberStats.Select(static stat => (
                    stat.SongId,
                    stat.BandType,
                    stat.TeamKey,
                    stat.InstrumentCombo,
                    stat.MemberIndex))
                .Distinct()
                .Count() != memberStats.Count)
        {
            throw Rejected("Tier-1 member-stat rows violate the bounded input contract.");
        }
        var populatedScopes = entries
            .Where(static entry => !entry.IsOverThreshold)
            .Select(static entry => (
                entry.SongId,
                entry.BandType))
            .ToHashSet();
        if (scopeKeys.Any(scope =>
                !populatedScopes.Contains(scope)))
        {
            throw Rejected(
                "Tier-1 input is incomplete for a requested projection scope.");
        }
        var statsByEntry = memberStats
            .GroupBy(static stat => (
                stat.SongId,
                stat.BandType,
                stat.TeamKey,
                stat.InstrumentCombo))
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static stat => stat.MemberIndex)
                    .ToArray());
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedCount =
                BandInstrumentMapping.ExpectedMemberCount(entry.BandType);
            if (!statsByEntry.TryGetValue(
                    (
                        entry.SongId,
                        entry.BandType,
                        entry.TeamKey,
                        entry.InstrumentCombo),
                    out var stats))
            {
                throw Rejected(
                    "Tier-1 member stats are incomplete for a declared band entry.");
            }
            if (stats.Length != expectedCount ||
                !stats.Select(static stat => stat.MemberIndex)
                    .SequenceEqual(Enumerable.Range(0, expectedCount)) ||
                !stats.Select(static stat => stat.AccountId)
                    .SequenceEqual(entry.TeamMembers))
            {
                throw Rejected(
                    "Tier-1 member stats are incomplete for a declared band entry.");
            }
        }
    }

    internal static TierZeroArtifactDescriptor RequireArtifact(
        TierZeroEvidenceManifest manifest,
        string path) =>
        manifest.Artifacts.SingleOrDefault(artifact =>
            string.Equals(
                artifact.Path,
                path,
                StringComparison.Ordinal))
        ?? throw Rejected($"Replay package artifact is missing: {path}");

    private static (string Path, string Completeness) DatasetContract(
        string datasetId) =>
        datasetId switch
        {
            TierOneReplayFormat.ScopesDatasetId => (
                TierOneReplayFormat.ScopesPath,
                "complete-requested-scope-list"),
            TierOneReplayFormat.EntriesDatasetId => (
                TierOneReplayFormat.EntriesPath,
                "complete-for-requested-overall-scopes"),
            TierOneReplayFormat.MemberStatsDatasetId => (
                TierOneReplayFormat.MemberStatsPath,
                "complete-for-band-entry-primary-keys"),
            _ => throw Rejected(
                $"Tier-1 dataset '{datasetId}' is not allowlisted."),
        };

    private static void RequireCanonicalEqual<T>(
        T first,
        T second,
        string description)
    {
        if (!TierZeroCanonicalJson.Serialize(first).AsSpan()
            .SequenceEqual(TierZeroCanonicalJson.Serialize(second)))
        {
            throw Rejected($"Tier-1 input {description} does not match its parent.");
        }
    }

    private static ReplayException Rejected(string message) =>
        new(
            ReplayFailureKind.PackageRejected,
            ReplayExitCode.PackageRejected,
            message);
}

public static class TierOneReplayDatabaseSchema
{
    public const string Version = "band-current-projection-input.v1";
    public static string Fingerprint { get; } =
        TierZeroCanonicalJson.Sha256Hex(
            Encoding.UTF8.GetBytes(
                Version + "\n" +
                InputSchemaSql));

    internal const string InputSchemaSql = """
        CREATE TABLE band_entries (
            song_id TEXT NOT NULL,
            band_type TEXT NOT NULL,
            team_key TEXT NOT NULL,
            instrument_combo TEXT NOT NULL,
            team_members TEXT[] NOT NULL,
            score INTEGER NOT NULL,
            accuracy INTEGER,
            is_full_combo BOOLEAN,
            stars INTEGER,
            difficulty INTEGER,
            season INTEGER,
            end_time TEXT,
            is_over_threshold BOOLEAN NOT NULL,
            first_seen_at TIMESTAMPTZ NOT NULL,
            last_updated_at TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (song_id, band_type, team_key, instrument_combo)
        );

        CREATE TABLE band_member_stats (
            song_id TEXT NOT NULL,
            band_type TEXT NOT NULL,
            team_key TEXT NOT NULL,
            instrument_combo TEXT NOT NULL,
            member_index INTEGER NOT NULL,
            account_id TEXT NOT NULL,
            instrument_id INTEGER,
            score INTEGER,
            accuracy INTEGER,
            is_full_combo BOOLEAN,
            stars INTEGER,
            difficulty INTEGER,
            PRIMARY KEY (
                song_id,
                band_type,
                team_key,
                instrument_combo,
                member_index
            )
        );
        """;
}

public sealed class TierOneReplayImporter
{
    public async Task ImportAsync(
        NpgsqlDataSource dataSource,
        TierOneReplayInput input,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using (var guard = connection.CreateCommand())
        {
            guard.Transaction = transaction;
            guard.CommandText = """
                SELECT NOT EXISTS (
                           SELECT 1
                           FROM pg_class relation
                           JOIN pg_namespace namespace
                             ON namespace.oid = relation.relnamespace
                           WHERE namespace.nspname = 'public'
                             AND relation.relkind IN (
                                 'r', 'p', 'v', 'm', 'S', 'f'
                             )
                       )
                   AND NOT EXISTS (
                           SELECT 1
                           FROM pg_namespace
                           WHERE nspname NOT IN (
                               'public',
                               'information_schema',
                               'fst_replay_control'
                           )
                             AND nspname NOT LIKE 'pg_%'
                       )
                   AND NOT EXISTS (
                           SELECT 1
                           FROM pg_class relation
                           JOIN pg_namespace namespace
                             ON namespace.oid = relation.relnamespace
                           WHERE namespace.nspname =
                                 'fst_replay_control'
                             AND NOT (
                                 relation.relname = 'target'
                                 AND relation.relkind = 'r'
                             )
                             AND NOT (
                                 relation.relname = 'target_pkey'
                                 AND relation.relkind = 'i'
                             )
                       )
                   AND NOT EXISTS (
                           SELECT 1
                           FROM pg_proc procedure
                           JOIN pg_namespace namespace
                             ON namespace.oid =
                                procedure.pronamespace
                           WHERE namespace.nspname IN (
                               'public',
                               'fst_replay_control'
                           )
                       )
                   AND NOT EXISTS (
                           SELECT 1
                           FROM pg_trigger
                           WHERE NOT tgisinternal
                       )
                   AND NOT EXISTS (
                           SELECT 1
                           FROM pg_event_trigger
                       )
                """;
            if (await guard.ExecuteScalarAsync(cancellationToken) is not true)
            {
                throw Rejected(
                    "Replay import requires a fresh isolated attempt database.");
            }
        }
        await using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText =
                TierOneReplayDatabaseSchema.InputSchemaSql;
            await schema.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var writer = await connection.BeginBinaryImportAsync(
            """
            COPY band_entries (
                song_id,
                band_type,
                team_key,
                instrument_combo,
                team_members,
                score,
                accuracy,
                is_full_combo,
                stars,
                difficulty,
                season,
                end_time,
                is_over_threshold,
                first_seen_at,
                last_updated_at
            ) FROM STDIN (FORMAT BINARY)
            """,
            cancellationToken))
        {
            foreach (var row in input.BandEntries)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(row.SongId, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(row.BandType, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(row.TeamKey, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(row.InstrumentCombo, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(row.TeamMembers.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(row.Score, NpgsqlDbType.Integer, cancellationToken);
                await WriteNullableAsync(writer, row.Accuracy, NpgsqlDbType.Integer, cancellationToken);
                await WriteNullableAsync(writer, row.IsFullCombo, NpgsqlDbType.Boolean, cancellationToken);
                await WriteNullableAsync(writer, row.Stars, NpgsqlDbType.Integer, cancellationToken);
                await WriteNullableAsync(writer, row.Difficulty, NpgsqlDbType.Integer, cancellationToken);
                await WriteNullableAsync(writer, row.Season, NpgsqlDbType.Integer, cancellationToken);
                await WriteNullableAsync(writer, row.EndTime, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(row.IsOverThreshold, NpgsqlDbType.Boolean, cancellationToken);
                await writer.WriteAsync(row.FirstSeenAtUtc.UtcDateTime, NpgsqlDbType.TimestampTz, cancellationToken);
                await writer.WriteAsync(row.LastUpdatedAtUtc.UtcDateTime, NpgsqlDbType.TimestampTz, cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }

        await using (var writer = await connection.BeginBinaryImportAsync(
            """
            COPY band_member_stats (
                song_id,
                band_type,
                team_key,
                instrument_combo,
                member_index,
                account_id,
                instrument_id,
                score,
                accuracy,
                is_full_combo,
                stars,
                difficulty
            ) FROM STDIN (FORMAT BINARY)
            """,
            cancellationToken))
        {
            foreach (var row in input.MemberStats)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(row.SongId, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(row.BandType, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(row.TeamKey, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(row.InstrumentCombo, NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(row.MemberIndex, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(row.AccountId, NpgsqlDbType.Text, cancellationToken);
                await WriteNullableAsync(writer, row.InstrumentId, NpgsqlDbType.Integer, cancellationToken);
                await WriteNullableAsync(writer, row.Score, NpgsqlDbType.Integer, cancellationToken);
                await WriteNullableAsync(writer, row.Accuracy, NpgsqlDbType.Integer, cancellationToken);
                await WriteNullableAsync(writer, row.IsFullCombo, NpgsqlDbType.Boolean, cancellationToken);
                await WriteNullableAsync(writer, row.Stars, NpgsqlDbType.Integer, cancellationToken);
                await WriteNullableAsync(writer, row.Difficulty, NpgsqlDbType.Integer, cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task WriteNullableAsync<T>(
        NpgsqlBinaryImporter writer,
        T? value,
        NpgsqlDbType type,
        CancellationToken cancellationToken)
    {
        if (value is null)
            await writer.WriteNullAsync(cancellationToken);
        else
            await writer.WriteAsync(value, type, cancellationToken);
    }

    private static ReplayException Rejected(string message) =>
        new(
            ReplayFailureKind.ImportRejected,
            ReplayExitCode.ImportRejected,
            message);
}
