using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FSTService.Persistence.Maintenance;
using FSTService.Scraping.Replay;
using FstSnapshotGenerationDrop;
using FstSnapshotGenerationEvidence;
using FstSnapshotGenerationQuarantine;

namespace FSTService.Tests.Unit;

public sealed class SnapshotGenerationDropToolTests
{
    [Fact]
    public void StandaloneContractsMatchDatabaseContracts()
    {
        Assert.Equal(
            SnapshotGenerationDropContract.SchemaVersion,
            SnapshotGenerationDropToolContract.SchemaVersion);
        Assert.Equal(
            SnapshotGenerationDropContract.ToolId,
            SnapshotGenerationDropToolContract.ToolId);
        Assert.Equal(
            SnapshotGenerationDropContract.DropAdvisoryLockKey,
            SnapshotGenerationDropToolContract.DropAdvisoryLockKey);
        Assert.Equal(
            SnapshotGenerationDropContract.MinimumSoakSeconds,
            SnapshotGenerationDropToolContract.MinimumSoakSeconds);
        Assert.Equal(
            SnapshotGenerationDropContract.MinimumHealthSamples,
            SnapshotGenerationDropToolContract.MinimumHealthSamples);
        Assert.Equal(
            SnapshotGenerationDropContract
                .HealthSampleIntervalSeconds,
            SnapshotGenerationDropToolContract
                .HealthSampleIntervalSeconds);
        Assert.Equal(
            SnapshotGenerationQuarantineContract.ToolId,
            SnapshotGenerationQuarantineEvidenceContract.ToolId);
        Assert.Equal(
            SnapshotGenerationQuarantineContract.SchemaVersion,
            SnapshotGenerationQuarantineEvidenceContract
                .SchemaVersion);
        Assert.Equal(
            SnapshotGenerationQuarantineContract.QuarantineSchema,
            SnapshotGenerationQuarantineEvidenceContract
                .QuarantineSchema);
        Assert.Equal(
            SnapshotGenerationQuarantineContract
                .RegistrationAdvisoryLockKey,
            SnapshotGenerationQuarantineEvidenceContract
                .RegistrationAdvisoryLockKey);
        Assert.Equal(
            SnapshotGenerationQuarantineContract
                .ServiceMaintenanceAdvisoryLockKey,
            SnapshotGenerationQuarantineEvidenceContract
                .ServiceMaintenanceAdvisoryLockKey);
        Assert.Equal(
            SnapshotGenerationQuarantineContract
                .PublicationAdvisoryLockKey,
            SnapshotGenerationQuarantineEvidenceContract
                .PublicationAdvisoryLockKey);
        Assert.Equal(
            SnapshotGenerationQuarantineContract
                .PlannerAdvisoryLockKey,
            SnapshotGenerationQuarantineEvidenceContract
                .PlannerAdvisoryLockKey);
        Assert.Equal(
            SnapshotGenerationQuarantineContract
                .ExecutorAdvisoryLockKey,
            SnapshotGenerationQuarantineEvidenceContract
                .ExecutorAdvisoryLockKey);
        Assert.Equal(
            SnapshotGenerationQuarantineContract
                .SnapshotDdlLockName,
            SnapshotGenerationQuarantineEvidenceContract
                .SnapshotDdlLockName);
        var canonicalFixture = new
        {
            Zeta = "quoted+<&>",
            Alpha = new[] { 2, 1 },
            Optional = (string?)null,
        };
        Assert.Equal(
            TierZeroCanonicalJson.Serialize(
                canonicalFixture),
            SnapshotGenerationCanonicalJson.Serialize(
                canonicalFixture));
    }

    [Fact]
    public void CommandArgumentsRejectUnknownDuplicateAndMissingValues()
    {
        Assert.Throws<ArgumentException>(
            () => DropCommandArguments.Parse(
                ["--relation", "unsafe"],
                ["plan"]));
        Assert.Throws<ArgumentException>(
            () => DropCommandArguments.Parse(
                ["--plan", "one", "--plan", "two"],
                ["plan"]));
        Assert.Throws<ArgumentException>(
            () => DropCommandArguments.Parse(
                ["--plan"],
                ["plan"]));
    }

    [Fact]
    public void HealthEvidenceRequiresThirtyMinutesAndSixtyExactSamples()
    {
        var started =
            DateTimeOffset.Parse(
                "2026-08-30T12:00:00Z");
        var evidence = new SnapshotGenerationHealthEvidence(
            1,
            "fst.snapshot-generation-drop-health.v1",
            started,
            started.AddMinutes(30),
            30,
            60,
            153,
            1331,
            true,
            Enumerable.Range(0, 60)
                .Select(index =>
                    new SnapshotGenerationHealthSample(
                        started.AddSeconds(index * 30),
                        153,
                        1331,
                        true,
                        true,
                        false,
                        0,
                        0))
                .ToArray(),
            null).Seal();

        evidence.Validate();
        Assert.Equal(
            "e514d1d903fc1d6116f568bdd738ee91b74ed356e89ca0cb6e8bba38384c958a",
            evidence.EvidenceSha256);
        Assert.Throws<InvalidDataException>(
            () => (evidence with
            {
                SuccessfulSampleCount = 59,
            }).Seal().Validate());
        Assert.Throws<InvalidDataException>(
            () => (evidence with
            {
                CompletedAtUtc =
                    started.AddMinutes(29),
            }).Seal().Validate());
    }

    [Fact]
    public void ArchiveSemanticProjectionIgnoresOnlyLeafNames()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            $"semantic-archive-{Guid.NewGuid():N}");
        try
        {
            var first = CreateSemanticArchive(
                Path.Combine(root, "first"),
                "legacy_pk",
                "legacy_score",
                scoreDescending: true,
                archiveHashCharacter: '1');
            var second = CreateSemanticArchive(
                Path.Combine(root, "second"),
                "sgqi_0123456789abcdef0123456789abcdef_pk",
                "sgqi_0123456789abcdef0123456789abcdef_score",
                scoreDescending: true,
                archiveHashCharacter: '2');

            var firstSemantic =
                DropEvidenceValidator
                    .ReadArchiveSemanticEvidence(first);
            var secondSemantic =
                DropEvidenceValidator
                    .ReadArchiveSemanticEvidence(second);

            Assert.NotEqual(
                first.ArchiveSha256,
                second.ArchiveSha256);
            Assert.Equal(
                firstSemantic.SemanticCatalogSha256,
                secondSemantic.SemanticCatalogSha256);
            Assert.Equal(
                firstSemantic.LogicalIndexShapeSha256,
                secondSemantic.LogicalIndexShapeSha256);
            Assert.Equal(
                firstSemantic.PhysicalIndexInventorySha256,
                secondSemantic.PhysicalIndexInventorySha256);
            DropEvidenceValidator.ValidateMatchingSemantics(
                firstSemantic,
                secondSemantic);

            var drifted = CreateSemanticArchive(
                Path.Combine(root, "drifted"),
                "legacy_pk",
                "legacy_score",
                scoreDescending: false,
                archiveHashCharacter: '3');
            Assert.Throws<InvalidDataException>(
                () => DropEvidenceValidator
                    .ReadArchiveSemanticEvidence(
                        drifted));
            var opclassDrifted =
                CreateSemanticArchive(
                    Path.Combine(
                        root,
                        "opclass-drifted"),
                    "opclass_drift_pk",
                    "opclass_drift_score",
                    scoreDescending: true,
                    archiveHashCharacter: '4',
                    scoreOpclass: 3124);
            Assert.Throws<InvalidDataException>(
                () => DropEvidenceValidator
                    .ReadArchiveSemanticEvidence(
                        opclassDrifted));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProductionShapedCatalogNumericStringsRemainCompatible()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            $"production-catalog-{Guid.NewGuid():N}");
        try
        {
            var cycle14 = CreateSemanticArchive(
                Path.Combine(root, "cycle14"),
                "legacy_pk",
                "legacy_score",
                scoreDescending: true,
                archiveHashCharacter: '5',
                relationIdentifiersAsStrings: true,
                includeOptionalMetadata: false);
            var cycle16 = CreateSemanticArchive(
                Path.Combine(root, "cycle16"),
                "sgqi_0123456789abcdef0123456789abcdef_pk",
                "sgqi_0123456789abcdef0123456789abcdef_score",
                scoreDescending: true,
                archiveHashCharacter: '6',
                relationIdentifiersAsStrings: true,
                optionalOidArraysAsStrings: true);

            var cycle14Semantic =
                DropEvidenceValidator
                    .ReadArchiveSemanticEvidence(cycle14);
            var cycle16Semantic =
                DropEvidenceValidator
                    .ReadArchiveSemanticEvidence(cycle16);

            Assert.NotEqual(
                cycle14.ArchiveSha256,
                cycle16.ArchiveSha256);
            Assert.NotEqual(
                cycle14Semantic.CatalogSha256,
                cycle16Semantic.CatalogSha256);
            Assert.Equal(
                cycle14Semantic.SemanticCatalogSha256,
                cycle16Semantic.SemanticCatalogSha256);
            Assert.Equal(
                cycle14Semantic.LogicalIndexShapeSha256,
                cycle16Semantic.LogicalIndexShapeSha256);
            Assert.Equal(
                cycle14Semantic.PhysicalIndexInventorySha256,
                cycle16Semantic.PhysicalIndexInventorySha256);
            DropEvidenceValidator.ValidateMatchingSemantics(
                cycle14Semantic,
                cycle16Semantic);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("relationOid", "0300")]
    [InlineData("relationRelfilenode", "-300")]
    [InlineData("indexOid", "+3000")]
    [InlineData("indexRelfilenode", " 3000")]
    [InlineData("parentIndexOid", "2000 ")]
    [InlineData("opclassOid", "4294967296")]
    [InlineData("collationOid", "00")]
    [InlineData("indNKeyAtts", "4")]
    [InlineData("keyAttnum", "1")]
    [InlineData("indOption", "0")]
    public void CatalogNumericStringsMustBeCanonical(
        string malformedField,
        string malformedValue)
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            $"malformed-catalog-{Guid.NewGuid():N}");
        try
        {
            var archive = CreateSemanticArchive(
                root,
                "legacy_pk",
                "legacy_score",
                scoreDescending: true,
                archiveHashCharacter: '7',
                relationIdentifiersAsStrings: true,
                optionalOidArraysAsStrings: true,
                indexIdentifiersAsStrings: true,
                malformedField: malformedField,
                malformedValue: malformedValue);

            Assert.Throws<InvalidDataException>(
                () => DropEvidenceValidator
                    .ReadArchiveSemanticEvidence(archive));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DropSurfaceIsDockerFreePrebuiltAndSingleStatement()
    {
        var repository = FindRepositoryRoot();
        var directory = Path.Combine(
            repository,
            "tools",
            "FstSnapshotGenerationDrop");
        var source = string.Join(
            "\n",
            Directory.EnumerateFiles(
                    directory,
                    "*.cs",
                    SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        var wrapper = File.ReadAllText(
            Path.Combine(
                repository,
                "tools",
                "postgres-snapshot-generation-drop.sh"));
        var project = File.ReadAllText(
            Path.Combine(
                directory,
                "FstSnapshotGenerationDrop.csproj"));
        var configuration =
            new DirectoryInfo(
                AppContext.BaseDirectory)
                .Parent!.Name;
        var dependencies = File.ReadAllText(
            Path.Combine(
                directory,
                "bin",
                configuration,
                "net9.0",
                "FstSnapshotGenerationDrop.deps.json"));

        Assert.DoesNotContain(
            "\"docker\"",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            " docker ",
            wrapper,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Docker.DotNet",
            dependencies,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            @"..\..\FSTService\FSTService.csproj",
            project,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "dotnet build",
            wrapper,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "--instrument",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "--snapshot-id",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "--relation",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "--sql",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "--batch",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "--force",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "--yes",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "FST_SNAPSHOT_DROP_BINARY_SHA256",
            wrapper,
            StringComparison.Ordinal);
        Assert.Contains(
            "RehearsalQuarantineReport.Reference",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ActiveQuarantineReport.Reference",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "DROP approval must be distinct",
            source,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(
                SnapshotGenerationDropSchema.Sql,
                "'DROP TABLE %I.%I RESTRICT'"));
        Assert.DoesNotContain(
            "DROP TABLE IF EXISTS",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "CASCADE",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "'LOCK TABLE ONLY %I.%I IN SHARE MODE'",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "'LOCK TABLE ONLY %I.%I IN ACCESS EXCLUSIVE MODE'",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "active.instrument = 'Solo_Bass'",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "active.snapshot_id = 1308",
            SnapshotGenerationDropSchema.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "observation.instrument = 'Solo_Bass'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "observation.snapshot_id = 1308",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"drop\" =>",
            ReadQuarantineSource(repository),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DropAssemblyCanBeContentAddressed()
    {
        var assembly =
            typeof(SnapshotGenerationDropPlan)
                .Assembly.Location;
        var hash =
            DropEvidenceValidator.Sha256File(
                assembly);

        Assert.Equal(64, hash.Length);
        Assert.All(
            hash,
            character => Assert.True(
                character is >= '0' and <= '9'
                    or >= 'a' and <= 'f'));
    }

    [Fact]
    public void DropWrapperRejectsMissingAndMismatchedBinaryHashes()
    {
        var wrapper = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "postgres-snapshot-generation-drop.sh");

        var missing = RunWrapper(wrapper, null);
        Assert.Equal(64, missing);
        var mismatched = RunWrapper(
            wrapper,
            new string('0', 64));
        Assert.Equal(1, mismatched);
        var binary = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "FstSnapshotGenerationDrop",
            "bin",
            "Release",
            "net9.0",
            "FstSnapshotGenerationDrop.dll");
        if (File.Exists(binary))
        {
            Assert.Equal(
                0,
                RunWrapper(
                    wrapper,
                    DropEvidenceValidator.Sha256File(
                        binary)));
        }
    }

    [Fact]
    public void RecoveryBundleIsChecksummedSealedAndTamperEvident()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            $"drop-bundle-{Guid.NewGuid():N}");
        var archive = Path.Combine(root, "source-archive");
        var output = Path.Combine(root, "bundle");
        Directory.CreateDirectory(archive);
        var evidence = Path.Combine(root, "evidence.json");
        var restore = Path.Combine(root, "restore.py");
        var proofDirectory = Path.Combine(root, "proof");
        Directory.CreateDirectory(proofDirectory);
        var proof = Path.Combine(
            proofDirectory,
            "proof-manifest.json");
        File.WriteAllText(
            Path.Combine(archive, "archive.custom"),
            "archive");
        File.WriteAllText(
            Path.Combine(archive, "manifest.json"),
            "{}");
        File.WriteAllText(evidence, "{}");
        File.WriteAllText(restore, "restore");
        File.WriteAllText(proof, "{}");
        File.WriteAllText(
            Path.Combine(proofDirectory, "cleanup.json"),
            "{}");
        File.WriteAllText(
            Path.Combine(proofDirectory, "SHA256SUMS"),
            "fixture");
        try
        {
            var manifestSha =
                DropEvidenceValidator.CreateRecoveryBundle(
                    output,
                    archive,
                    archive,
                    new Dictionary<string, string>
                    {
                        ["evidence"] = evidence,
                        ["fresh-proof"] = proof,
                    },
                    Assembly.GetExecutingAssembly().Location,
                    restore,
                    restore,
                    physicalBytes: 1,
                    reserveBytes: 0);

            var manifest =
                DropEvidenceValidator.ValidateRecoveryBundle(
                    output);
            Assert.Equal(
                manifestSha,
                DropEvidenceValidator.Sha256File(
                    Path.Combine(
                        output,
                        "bundle-manifest.json")));
            Assert.Contains(
                manifest.Files,
                item => item.Path == "drop-binary");
            Assert.Contains(
                manifest.Files,
                item => item.Path == "restore-tool.py");
            Assert.Contains(
                manifest.Files,
                item => item.Path ==
                    "postgres-snapshot-generation-archive.py");
            Assert.Contains(
                manifest.Files,
                item => item.Path ==
                    "evidence/fresh-proof/cleanup.json");
            Assert.False(
                string.IsNullOrWhiteSpace(
                    manifest.FilesystemRoot));
            Assert.True(
                manifest.AvailableBeforeCopyBytes > 0);
            Assert.True(
                manifest.AvailableAfterCopyBytes >=
                    manifest.RequiredCapacityBytes
                    + manifest.CapacityReserveBytes);
            Assert.True(manifest.BundleCopyBytes > 0);

            var copiedEvidence = Path.Combine(
                output,
                "evidence",
                "evidence.json");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    copiedEvidence,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite);
            }
            File.AppendAllText(copiedEvidence, "tampered");
            Assert.Throws<InvalidDataException>(
                () =>
                    DropEvidenceValidator
                        .ValidateRecoveryBundle(output));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ServiceRuntimeDoesNotInvokeDropFunction()
    {
        var repository = FindRepositoryRoot();
        var service = Path.Combine(repository, "FSTService");
        var references = Directory.EnumerateFiles(
                    service,
                    "*.cs",
                    SearchOption.AllDirectories)
                .Where(path =>
                    !path.EndsWith(
                        "SnapshotGenerationDropSchema.cs",
                        StringComparison.Ordinal))
                .Where(path =>
                    File.ReadAllText(path).Contains(
                        "fst_drop_quarantined_snapshot_generation",
                        StringComparison.Ordinal))
                .ToArray();
        var restoreReferences = Directory.EnumerateFiles(
                    service,
                    "*.cs",
                    SearchOption.AllDirectories)
                .Where(path =>
                    !path.EndsWith(
                        "SnapshotGenerationDropSchema.cs",
                        StringComparison.Ordinal))
                .Where(path =>
                    File.ReadAllText(path).Contains(
                        "fst_restore_snapshot_generation",
                        StringComparison.Ordinal))
                .ToArray();

        Assert.Empty(references);
        Assert.Empty(restoreReferences);
    }

    [Fact]
    public void GenerationCreationAndDropShareTheDdlLock()
    {
        var repository = FindRepositoryRoot();
        var initializer = File.ReadAllText(
            Path.Combine(
                repository,
                "FSTService",
                "Persistence",
                "DatabaseInitializer.cs"));
        var dropSchema = SnapshotGenerationDropSchema.Sql;
        var lockText =
            "'fst.snapshot-generation-partition-ddl'";

        Assert.Contains(lockText, initializer);
        Assert.Contains(lockText, dropSchema);
        Assert.True(
            initializer.IndexOf(
                lockText,
                StringComparison.Ordinal)
            < initializer.IndexOf(
                "'CREATE TABLE IF NOT EXISTS public.%I PARTITION OF public.%I FOR VALUES IN (%s)'",
                StringComparison.Ordinal));
    }

    [Fact]
    public void DropFunctionUsesTheSevenLockOrder()
    {
        var sql = SnapshotGenerationDropSchema.Sql;
        var start = sql.IndexOf(
            "fst_lock_snapshot_generation_for_drop",
            StringComparison.Ordinal);
        var end = sql.IndexOf(
            "$drop_lock$;",
            start,
            StringComparison.Ordinal);
        var function = sql[start..end];
        var expected = new[]
        {
            "5067481511116518500",
            "2026050901",
            "5067481511116519500",
            "2026082301",
            "'fst.snapshot-generation-partition-ddl'",
            "2026083001",
            "2026083002",
        };
        var previous = -1;
        foreach (var token in expected)
        {
            var offset = function.IndexOf(
                token,
                StringComparison.Ordinal);
            Assert.True(offset > previous);
            previous = offset;
        }
    }

    [Fact]
    public void DropTransactionLocksOnlyDefaultAndPrivateRelations()
    {
        var sql = SnapshotGenerationDropSchema.Sql;
        var start = sql.IndexOf(
            "fst_drop_quarantined_snapshot_generation",
            StringComparison.Ordinal);
        var end = sql.IndexOf(
            "$drop_execute$;",
            start,
            StringComparison.Ordinal);
        var function = sql[start..end];

        Assert.Equal(
            2,
            CountOccurrences(function, "'LOCK TABLE"));
        Assert.Contains(
            "'LOCK TABLE ONLY %I.%I IN SHARE MODE'",
            function,
            StringComparison.Ordinal);
        Assert.Contains(
            "'LOCK TABLE ONLY %I.%I IN ACCESS EXCLUSIVE MODE'",
            function,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LOCK TABLE public.leaderboard_entries_snapshot",
            function,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadQuarantineSource(
        string repository)
    {
        var directory = Path.Combine(
            repository,
            "tools",
            "FstSnapshotGenerationQuarantine");
        return string.Join(
            "\n",
            Directory.EnumerateFiles(
                    directory,
                    "*.cs",
                    SearchOption.AllDirectories)
                .Select(File.ReadAllText));
    }

    private static ArchivePackageEvidence
        CreateSemanticArchive(
            string directory,
            string pkName,
            string scoreName,
            bool scoreDescending,
            char archiveHashCharacter,
            long scoreOpclass = 1978,
            bool relationIdentifiersAsStrings = false,
            bool includeOptionalMetadata = true,
            bool optionalOidArraysAsStrings = false,
            bool indexIdentifiersAsStrings = false,
            string? malformedField = null,
            string? malformedValue = null)
    {
        Directory.CreateDirectory(directory);
        var childName =
            "leaderboard_entries_snapshot_pro_cymbals_s1314";
        var rootName =
            "leaderboard_entries_snapshot_pro_cymbals";
        var scoreOrder = scoreDescending
            ? "score DESC"
            : "score";
        var catalog = new
        {
            physicalCatalog = new object[]
            {
                new
                {
                    oid = 100L,
                    relfilenode = 0L,
                    name = "leaderboard_entries_snapshot",
                    indexes = new object[]
                    {
                        Index(
                            1000,
                            "leaderboard_entries_snapshot_pkey",
                            true,
                            true,
                            null,
                            "snapshot_id, song_id, instrument, account_id",
                            [
                                "snapshot_id",
                                "song_id",
                                "instrument",
                                "account_id",
                            ]),
                        Index(
                            1001,
                            "ix_les_snapshot_song_score",
                            false,
                            false,
                            null,
                            "snapshot_id, song_id, instrument, score DESC",
                            [
                                "snapshot_id",
                                "song_id",
                                "instrument",
                                "score",
                            ],
                            "ignored",
                            scoreOpclass),
                    },
                },
                new
                {
                    oid = 200L,
                    relfilenode = 0L,
                    name = rootName,
                    indexes = new object[]
                    {
                        Index(
                            2000,
                            $"{rootName}_pkey",
                            true,
                            true,
                            1000,
                            "snapshot_id, song_id, instrument, account_id",
                            [
                                "snapshot_id",
                                "song_id",
                                "instrument",
                                "account_id",
                            ]),
                        Index(
                            2001,
                            $"{rootName}_score_idx",
                            false,
                            false,
                            1001,
                            "snapshot_id, song_id, instrument, score DESC",
                            [
                                "snapshot_id",
                                "song_id",
                                "instrument",
                                "score",
                            ],
                            "ignored",
                            scoreOpclass),
                    },
                },
                new
                {
                    oid = 300L,
                    relfilenode = 300L,
                    name = childName,
                    relationKind = "r",
                    persistenceKind = "p",
                    accessMethod = "heap",
                    tablespace = "pg_default",
                    relationOptions =
                        Array.Empty<string>(),
                    partitionBound =
                        "FOR VALUES IN ('1314')",
                    columns = Array.Empty<object>(),
                    constraints = new[]
                    {
                        new
                        {
                            name = pkName,
                            type = "p",
                            definition =
                                "PRIMARY KEY (snapshot_id, song_id, instrument, account_id)",
                            validated = true,
                        },
                    },
                    indexes = new object[]
                    {
                        Index(
                            3000,
                            pkName,
                            true,
                            true,
                            2000,
                            "snapshot_id, song_id, instrument, account_id",
                            [
                                "snapshot_id",
                                "song_id",
                                "instrument",
                                "account_id",
                            ],
                            childName),
                        Index(
                            3001,
                            scoreName,
                            false,
                            false,
                            2001,
                            $"snapshot_id, song_id, instrument, {scoreOrder}",
                            [
                                "snapshot_id",
                                "song_id",
                                "instrument",
                                "score",
                            ],
                            childName,
                            scoreOpclass),
                    },
                },
            },
        };
        var catalogNode = JsonNode.Parse(
            JsonSerializer.Serialize(catalog))!
            .AsObject();
        var relations = catalogNode[
            "physicalCatalog"]!.AsArray();
        var malformedApplied = false;
        foreach (var relationNode in relations)
        {
            var relation = relationNode!.AsObject();
            var relationOid = relation["oid"]!
                .GetValue<long>();
            var relationName = relation["name"]!
                .GetValue<string>();
            foreach (var indexNode in relation[
                         "indexes"]!.AsArray())
            {
                var index = indexNode!.AsObject();
                index["tableOid"] = relationOid;
                if (!includeOptionalMetadata)
                {
                    foreach (var propertyName in new[]
                             {
                                 "indNKeyAtts",
                                 "indNAtts",
                                 "keyAttnums",
                                 "opclassOids",
                                 "collationOids",
                                 "indOptions",
                                 "relationOptions",
                             })
                    {
                        index.Remove(propertyName);
                    }
                }
                if (optionalOidArraysAsStrings)
                {
                    ConvertArrayToStrings(
                        index,
                        "opclassOids");
                    ConvertArrayToStrings(
                        index,
                        "collationOids");
                }
                if (indexIdentifiersAsStrings)
                {
                    ConvertValueToString(
                        index,
                        "indexOid");
                    ConvertValueToString(
                        index,
                        "indexRelfilenode");
                    ConvertValueToString(
                        index,
                        "parentIndexOid");
                }
                if (!malformedApplied
                    && relationName == childName
                    && malformedField is (
                        "indexOid"
                        or "indexRelfilenode"
                        or "parentIndexOid"
                        or "opclassOid"
                        or "collationOid"
                        or "indNKeyAtts"
                        or "keyAttnum"
                        or "indOption"))
                {
                    if (malformedField is
                        "opclassOid"
                        or "collationOid"
                        or "keyAttnum"
                        or "indOption")
                    {
                        var propertyName =
                            malformedField switch
                            {
                                "opclassOid" =>
                                    "opclassOids",
                                "collationOid" =>
                                    "collationOids",
                                "keyAttnum" =>
                                    "keyAttnums",
                                _ => "indOptions",
                            };
                        index[propertyName]!
                            .AsArray()[0] =
                            malformedValue;
                    }
                    else
                    {
                        index[malformedField] =
                            malformedValue;
                    }
                    malformedApplied = true;
                }
            }
            if (relationIdentifiersAsStrings)
            {
                ConvertValueToString(
                    relation,
                    "oid");
                ConvertValueToString(
                    relation,
                    "relfilenode");
            }
            if (!malformedApplied
                && relationName == childName
                && malformedField is (
                    "relationOid"
                    or "relationRelfilenode"))
            {
                relation[
                    malformedField == "relationOid"
                        ? "oid"
                        : "relfilenode"] =
                    malformedValue;
                malformedApplied = true;
            }
        }
        var catalogPath =
            Path.Combine(directory, "catalog.json");
        File.WriteAllText(
            catalogPath,
            catalogNode.ToJsonString());
        var catalogSha =
            DropEvidenceValidator.Sha256File(
                catalogPath);
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(
                new
                {
                    catalog = new
                    {
                        sha256 = catalogSha,
                    },
                }));
        return new ArchivePackageEvidence(
            directory,
            new string('a', 64),
            new string(archiveHashCharacter, 64),
            Path.Combine(directory, "proof.json"),
            new string('b', 64),
            1,
            1,
            1,
            new string('c', 64),
            new string('d', 64),
            1,
            "Solo_PeripheralCymbals",
            1314,
            "public",
            rootName,
            200,
            "public",
            childName,
            300,
            300,
            new string('e', 64),
            new string('f', 64),
            1,
            new string('1', 64),
            new string('2', 64),
            4096,
            "fstservice",
            1,
            "1",
            170000);

        static void ConvertValueToString(
            JsonObject value,
            string propertyName)
        {
            if (value[propertyName] is not JsonValue item
                || !item.TryGetValue<long>(
                    out var parsed))
            {
                return;
            }
            value[propertyName] =
                parsed.ToString(
                    CultureInfo.InvariantCulture);
        }

        static void ConvertArrayToStrings(
            JsonObject value,
            string propertyName)
        {
            if (value[propertyName] is not JsonArray array)
                return;
            for (var index = 0;
                 index < array.Count;
                 index++)
            {
                var parsed = array[index]!
                    .GetValue<long>();
                array[index] =
                    parsed.ToString(
                        CultureInfo.InvariantCulture);
            }
        }

        static object Index(
            long oid,
            string name,
            bool primary,
            bool unique,
            long? parentOid,
            string definitionColumns,
            string[] columns,
            string table = "ignored",
            long scoreOpclass = 1978) =>
            new
            {
                indexOid = oid,
                indexRelfilenode = oid,
                indexName = name,
                isPrimary = primary,
                isUnique = unique,
                isValid = true,
                isReady = true,
                accessMethod = "btree",
                tablespaceName = "pg_default",
                parentIndexOid = parentOid,
                definition =
                    $"{(unique ? "CREATE UNIQUE INDEX" : "CREATE INDEX")} {name} ON public.{table} USING btree ({definitionColumns})",
                columnNames = columns,
                indNKeyAtts = 4,
                indNAtts = 4,
                keyAttnums = primary
                    ? new[] { 1, 2, 3, 4 }
                    : new[] { 1, 2, 3, 5 },
                opclassOids = primary
                    ? new long[]
                    {
                        3124,
                        3126,
                        3126,
                        3126,
                    }
                    : new long[]
                    {
                        3124,
                        3126,
                        3126,
                        scoreOpclass,
                    },
                collationOids = primary
                    ? new long[]
                    {
                        0,
                        100,
                        100,
                        100,
                    }
                    : new long[]
                    {
                        0,
                        100,
                        100,
                        0,
                    },
                indOptions = primary
                    ? new[] { 0, 0, 0, 0 }
                    : new[] { 0, 0, 0, 3 },
                relationOptions =
                    Array.Empty<string>(),
            };
    }

    private static int CountOccurrences(
        string value,
        string token)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(
                   token,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }
        return count;
    }

    private static int RunWrapper(
        string wrapper,
        string? expectedHash)
    {
        var start = new ProcessStartInfo(
            "/bin/bash",
            $"\"{wrapper}\" --help")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (expectedHash is null)
        {
            start.Environment.Remove(
                "FST_SNAPSHOT_DROP_BINARY_SHA256");
        }
        else
        {
            start.Environment[
                "FST_SNAPSHOT_DROP_BINARY_SHA256"] =
                expectedHash;
        }
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Could not start the DROP wrapper.");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(
            Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location)!);
        while (current is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        current.FullName,
                        "FortniteFestivalLeaderboardScraper.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException(
            "Repository root was not found.");
    }
}
