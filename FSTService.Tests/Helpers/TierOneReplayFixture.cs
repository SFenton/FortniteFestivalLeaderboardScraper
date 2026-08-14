using System.Text;
using FSTService.Scraping;
using FSTService.Scraping.Replay;
using Npgsql;

namespace FSTService.Tests.Helpers;

internal sealed record TierOneReplayFixture(
    string Root,
    string ParentPackage,
    string InputPackage,
    TierZeroEvidenceManifest ParentManifest,
    TierZeroEvidenceManifest InputManifest,
    TierOnePhaseInputManifest PhaseInputManifest,
    string ReplayId)
{
    internal static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 14, 16, 0, 0, TimeSpan.Zero);

    internal static TierZeroBuildIdentity Build { get; } =
        new(
            new string('a', 40),
            $"sha256:{Hash("fixture-image")}",
            new string('b', 40),
            "1.0.198");

    internal static async Task<TierOneReplayFixture> CreateAsync(
        string root,
        string replayId = "tier1-band-current-fixture",
        string parentVariant = "synthetic")
    {
        Directory.CreateDirectory(root);
        var parentPath = Path.Combine(root, "tier0-parent");
        var inputPath = Path.Combine(root, "tier1-input");
        var source = new TierZeroSourceIdentity(
            1296,
            61,
            CreatedAt.AddMinutes(-10),
            new TierZeroCatalogIdentity(
                "synthetic-replay-catalog",
                Hash("catalog")));
        var database = new TierZeroDatabaseIdentity(
            17,
            ["plpgsql@1.0"],
            Hash("captured-schema"));
        var configuration =
            TierZeroConfigurationFingerprinter.Create(
                new Dictionary<string, string?>
                {
                    ["ReplayFixture:BandType"] =
                        "Band_Duets",
                    ["ReplayFixture:Scope"] = "overall",
                },
                [
                    "ReplayFixture:BandType",
                    "ReplayFixture:Scope",
                ]);

        var parentWriter = await TierZeroPackageWriter.CreateAsync(
            parentPath,
            new TierZeroPackageDraft(
                $"tier0-{parentVariant}-parent",
                source,
                Build,
                database,
                configuration,
                TierZeroSummaryReferences.Empty,
                [
                    new TierZeroParentRootHash(
                        "capture",
                        Hash("capture")),
                ],
                1,
                "tier1-fixture-producer",
                CreatedAt));
        var catalogBytes = Encoding.UTF8.GetBytes(
            $$"""{"catalog":"{{parentVariant}}"}""");
        await parentWriter.AddArtifactAsync(
            new TierZeroArtifactRegistration(
                "catalog",
                "source/catalog.json",
                "application/json",
                1,
                1,
                catalogBytes.LongLength),
            catalogBytes);
        var parent = await parentWriter.SealAsync(
            CreatedAt.AddMinutes(1));

        var scopes = new[]
        {
            new ReplayRequestedScopeRow(
                "song-1",
                "Band_Duets",
                "overall",
                ""),
        };
        var entries = new[]
        {
            Entry(
                "account-a:account-b",
                "0:1",
                ["account-a", "account-b"],
                1000,
                "2026-08-14T15:00:00Z"),
            Entry(
                "account-a:account-b",
                "2:3",
                ["account-a", "account-b"],
                950,
                "2026-08-14T15:01:00Z"),
            Entry(
                "account-c:account-d",
                "0:3",
                ["account-c", "account-d"],
                900,
                "2026-08-14T15:02:00Z"),
        };
        var memberStats = entries
            .SelectMany(entry => entry.TeamMembers.Select(
                (accountId, index) =>
                    new ReplayBandMemberStatRow(
                        entry.SongId,
                        entry.BandType,
                        entry.TeamKey,
                        entry.InstrumentCombo,
                        index,
                        accountId,
                        index,
                        entry.Score / 2,
                        990_000 - index,
                        true,
                        5,
                        3)))
            .ToArray();
        var scopeBytes = TierOneReplayCanonical.ToJsonLines(scopes);
        var entryBytes = TierOneReplayCanonical.ToJsonLines(entries);
        var memberBytes =
            TierOneReplayCanonical.ToJsonLines(memberStats);
        var bounds = TierOneReplayBounds.Conservative with
        {
            MaximumPackageBytes = 2 * 1024 * 1024,
            MaximumScopes = 4,
            MaximumBandEntries = 16,
            MaximumMemberStats = 64,
            MaximumOutputRows = 128,
        };
        var datasets = new[]
            {
            Dataset(
                TierOneReplayFormat.ScopesDatasetId,
                TierOneReplayFormat.ScopesPath,
                scopes.Length,
                scopeBytes,
                "complete-requested-scope-list"),
            Dataset(
                TierOneReplayFormat.EntriesDatasetId,
                TierOneReplayFormat.EntriesPath,
                entries.Length,
                entryBytes,
                "complete-for-requested-overall-scopes"),
            Dataset(
                TierOneReplayFormat.MemberStatsDatasetId,
                TierOneReplayFormat.MemberStatsPath,
                memberStats.Length,
                memberBytes,
                "complete-for-band-entry-primary-keys"),
            }
            .OrderBy(
                static dataset => dataset.DatasetId,
                StringComparer.Ordinal)
            .ToArray();
        var phaseInput = new TierOnePhaseInputManifest(
            TierOneReplayFormat.InputFormatId,
            TierOneReplayFormat.Version,
            replayId,
            parent.PackageRootHash!,
            PhaseProgressCatalog.OperationId,
            PhaseProgressCatalog.PlanVersion,
            ReplayPhaseCatalog.BandMaintenancePhaseId,
            ReplayPhaseCatalog.CurrentProjectionSubphaseId,
            ReplayPhaseCatalog.CurrentProjectionAdapterVersion,
            CreatedAt,
            "999999999999999999",
            ["post.band_extraction"],
            datasets,
            bounds,
            null);
        var phaseInputBytes =
            TierOneReplayCanonical.SerializeInput(phaseInput);
        var inputWriter = await TierZeroPackageWriter.CreateAsync(
            inputPath,
            new TierZeroPackageDraft(
                "tier1-synthetic-input",
                source,
                Build,
                database,
                configuration,
                TierZeroSummaryReferences.Empty,
                [
                    new TierZeroParentRootHash(
                        "tier0-parent",
                        parent.PackageRootHash!),
                ],
                1,
                "tier1-fixture-producer",
                CreatedAt.AddMinutes(2)));
        await AddAsync(
            inputWriter,
            "tier1-phase-input",
            TierOneReplayFormat.InputManifestPath,
            1,
            phaseInputBytes);
        await AddAsync(
            inputWriter,
            TierOneReplayFormat.ScopesDatasetId,
            TierOneReplayFormat.ScopesPath,
            scopes.Length,
            scopeBytes);
        await AddAsync(
            inputWriter,
            TierOneReplayFormat.EntriesDatasetId,
            TierOneReplayFormat.EntriesPath,
            entries.Length,
            entryBytes);
        await AddAsync(
            inputWriter,
            TierOneReplayFormat.MemberStatsDatasetId,
            TierOneReplayFormat.MemberStatsPath,
            memberStats.Length,
            memberBytes);
        var input = await inputWriter.SealAsync(
            CreatedAt.AddMinutes(3));
        return new TierOneReplayFixture(
            root,
            parentPath,
            inputPath,
            parent,
            input,
            phaseInput,
            replayId);
    }

    internal static ReplayExecutionEnvironment Environment(
        string root,
        string connectionString) =>
        new(
            new ReplayRootPolicyOptions(
                root,
                TestOnly: true,
                RollbackReserveBytes: 0),
            connectionString,
            null,
            Build,
            "tier1-replay-test",
            AllowTestServerAddress: true);

    internal static ReplayCommand Command(
        TierOneReplayFixture fixture,
        string outputPath,
        int attempt = 1) =>
        new(
            ReplayCommandKind.Execute,
            fixture.ParentPackage,
            fixture.InputPackage,
            ReplayPhaseCatalog.BandMaintenancePhaseId,
            ReplayPhaseCatalog.CurrentProjectionSubphaseId,
            outputPath,
            fixture.ReplayId,
            attempt,
            null,
            null,
            null);

    internal static async Task BootstrapDatabaseAsync(
        string connectionString,
        string replayId,
        string packageRootHash)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        string databaseName;
        string systemIdentifier;
        await using (var identity = connection.CreateCommand())
        {
            identity.CommandText = """
                SELECT current_database(),
                       (SELECT system_identifier::TEXT
                        FROM pg_control_system())
                """;
            await using var reader = await identity.ExecuteReaderAsync();
            await reader.ReadAsync();
            databaseName = reader.GetString(0);
            systemIdentifier = reader.GetString(1);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE SCHEMA fst_replay_control;
            CREATE TABLE fst_replay_control.target (
                singleton BOOLEAN PRIMARY KEY DEFAULT TRUE CHECK (singleton),
                marker_version INTEGER NOT NULL,
                replay_id TEXT NOT NULL,
                package_root_hash TEXT NOT NULL,
                database_name TEXT NOT NULL,
                system_identifier TEXT NOT NULL,
                status TEXT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL
            );
            INSERT INTO fst_replay_control.target (
                singleton,
                marker_version,
                replay_id,
                package_root_hash,
                database_name,
                system_identifier,
                status,
                created_at,
                updated_at
            )
            VALUES (
                TRUE,
                @markerVersion,
                @replayId,
                @packageRootHash,
                @databaseName,
                @systemIdentifier,
                @status,
                @createdAt,
                @createdAt
            );
            """;
        command.Parameters.AddWithValue(
            "markerVersion",
            ReplayDatabaseTargetGuard.MarkerVersion);
        command.Parameters.AddWithValue("replayId", replayId);
        command.Parameters.AddWithValue(
            "packageRootHash",
            packageRootHash);
        command.Parameters.AddWithValue(
            "databaseName",
            databaseName);
        command.Parameters.AddWithValue(
            "systemIdentifier",
            systemIdentifier);
        command.Parameters.AddWithValue(
            "status",
            ReplayDatabaseTargetGuard.CreatedStatus);
        command.Parameters.AddWithValue(
            "createdAt",
            CreatedAt.UtcDateTime);
        await command.ExecuteNonQueryAsync();
    }

    private static ReplayBandEntryRow Entry(
        string teamKey,
        string instrumentCombo,
        IReadOnlyList<string> members,
        int score,
        string endTime) =>
        new(
            "song-1",
            "Band_Duets",
            teamKey,
            instrumentCombo,
            members,
            score,
            990_000,
            true,
            5,
            3,
            1,
            endTime,
            false,
            CreatedAt.AddDays(-1),
            CreatedAt);

    private static TierOneDatasetReference Dataset(
        string datasetId,
        string path,
        long rows,
        byte[] bytes,
        string completeness) =>
        new(
            datasetId,
            path,
            1,
            rows,
            bytes.LongLength,
            TierZeroCanonicalJson.Sha256Hex(bytes),
            completeness);

    private static async Task AddAsync(
        TierZeroPackageWriter writer,
        string owner,
        string path,
        long rows,
        byte[] bytes) =>
        await writer.AddArtifactAsync(
            new TierZeroArtifactRegistration(
                owner,
                path,
                path.EndsWith(
                    ".jsonl",
                    StringComparison.Ordinal)
                    ? "application/x-ndjson"
                    : "application/json",
                1,
                rows,
                bytes.LongLength),
            bytes);

    private static string Hash(string value) =>
        TierZeroCanonicalJson.Sha256Hex(
            Encoding.UTF8.GetBytes(value));
}
