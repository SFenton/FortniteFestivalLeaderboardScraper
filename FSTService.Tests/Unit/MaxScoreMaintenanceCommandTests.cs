using System.Text;
using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class MaxScoreMaintenanceCommandTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(
        Directory.GetCurrentDirectory(),
        ".test-temp",
        $"max-score-command-{Guid.NewGuid():N}");

    public MaxScoreMaintenanceCommandTests()
    {
        Directory.CreateDirectory(_dataDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDirectory))
                Directory.Delete(_dataDirectory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Stage_parser_accepts_bounded_explicit_song_ids()
    {
        var command = MaxScoreMaintenanceCommand.Parse(
        [
            MaxScoreMaintenanceCommand.StageFlag,
            MaxScoreMaintenanceCommand.PublishedScrapeIdFlag,
            "1296",
            MaxScoreMaintenanceCommand.SongIdFlag,
            "song-b",
            MaxScoreMaintenanceCommand.SongIdFlag,
            "song-a",
            MaxScoreMaintenanceCommand.ManifestOutputFlag,
            "maintenance/manifest.json",
            MaxScoreMaintenanceCommand.ReportOutputFlag,
            "maintenance/stage-report.json",
        ]);

        Assert.NotNull(command);
        Assert.Equal(MaxScoreMaintenanceAction.Stage, command.Action);
        Assert.Equal(["song-a", "song-b"], command.SongIds);
        Assert.Null(command.StageRequestPath);
    }

    [Fact]
    public void Stage_parser_requires_exactly_one_input_form()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            MaxScoreMaintenanceCommand.Parse(
            [
                MaxScoreMaintenanceCommand.StageFlag,
                MaxScoreMaintenanceCommand.PublishedScrapeIdFlag,
                "1296",
                MaxScoreMaintenanceCommand.StageRequestFlag,
                "request.json",
                MaxScoreMaintenanceCommand.SongIdFlag,
                "song-a",
                MaxScoreMaintenanceCommand.ManifestOutputFlag,
                "manifest.json",
                MaxScoreMaintenanceCommand.ReportOutputFlag,
                "report.json",
            ]));

        Assert.Contains("exactly one", error.Message);
    }

    [Fact]
    public void Apply_parser_requires_both_digests_and_rollback_output()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            MaxScoreMaintenanceCommand.Parse(
            [
                MaxScoreMaintenanceCommand.ApplyFlag,
                MaxScoreMaintenanceCommand.PublishedScrapeIdFlag,
                "1296",
                MaxScoreMaintenanceCommand.ManifestFlag,
                "manifest.json",
                MaxScoreMaintenanceCommand.ExpectedManifestDigestFlag,
                new string('a', 64),
                MaxScoreMaintenanceCommand.ReportOutputFlag,
                "report.json",
            ]));

        Assert.Contains(
            MaxScoreMaintenanceCommand.RollbackOutputFlag,
            error.Message);
    }

    [Fact]
    public void Parser_rejects_unknown_maintenance_options()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            MaxScoreMaintenanceCommand.Parse(
            [
                MaxScoreMaintenanceCommand.StageFlag,
                "--max-score-maintenance-manfiest-output",
                "manifest.json",
            ]));

        Assert.Contains("Unknown", error.Message);
    }

    [Fact]
    public async Task Canonical_manifest_has_stable_digest_and_strict_loader()
    {
        var manifest = CreateManifest();
        var first = manifest.ComputeDigest();
        var second = manifest.ComputeDigest();
        Assert.Equal(first, second);

        var path = Path.Combine(_dataDirectory, "manifest.json");
        var written =
            await MaxScoreMaintenanceFileStore
                .WriteCanonicalManifestAsync(
                    _dataDirectory,
                    path,
                    manifest,
                    CancellationToken.None);
        var loaded = await MaxScoreMaintenanceFileStore.LoadManifestAsync(
            _dataDirectory,
            path,
            CancellationToken.None);

        Assert.Equal(first, written.Sha256);
        Assert.Equal(first, loaded.ComputeDigest());
        Assert.Equal(
            manifest.SerializeCanonical(),
            loaded.SerializeCanonical());
    }

    [Fact]
    public async Task Manifest_loader_rejects_unknown_or_noncanonical_json()
    {
        var manifest = CreateManifest();
        var noncanonicalPath = Path.Combine(
            _dataDirectory,
            "noncanonical.json");
        await File.WriteAllTextAsync(
            noncanonicalPath,
            JsonSerializer.Serialize(
                manifest,
                MaxScoreMaintenanceJson.Report));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            MaxScoreMaintenanceFileStore.LoadManifestAsync(
                _dataDirectory,
                noncanonicalPath,
                CancellationToken.None));

        var unknownPath = Path.Combine(_dataDirectory, "unknown.json");
        var canonical = Encoding.UTF8.GetString(
            manifest.SerializeCanonical());
        await File.WriteAllTextAsync(
            unknownPath,
            canonical[..^1] + ",\"unknown\":true}");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            MaxScoreMaintenanceFileStore.LoadManifestAsync(
                _dataDirectory,
                unknownPath,
                CancellationToken.None));
    }

    [Fact]
    public void Manifest_rejects_changed_instrument_mismatch()
    {
        var manifest = CreateManifest();
        var invalid = manifest with
        {
            Songs =
            [
                manifest.Songs[0] with
                {
                    ChangedInstruments =
                        ["Solo_PeripheralGuitar"],
                },
            ],
        };

        Assert.Throws<ArgumentException>(
            invalid.ValidateAndNormalize);
    }

    [Fact]
    public void Publication1296_manifest_binds_the_two_approved_maxima()
    {
        var template = CreateManifest();
        MaxScoreMaintenanceManifestSong Song(
            string songId,
            string generationId,
            int lead,
            int proLead)
        {
            var current = template.Songs[0].CurrentPath with
            {
                ArtifactGenerationId =
                    $"old-{generationId}",
            };
            var staged = template.Songs[0].StagedPath with
            {
                ArtifactGenerationId = generationId,
                Maxima = current.Maxima with
                {
                    Lead = lead,
                    ProLead = proLead,
                },
            };
            return new MaxScoreMaintenanceManifestSong(
                songId,
                template.Songs[0].ExpectedCatalogLastModified,
                current,
                staged,
                ["Solo_Guitar", "Solo_PeripheralGuitar"]);
        }

        var manifest = (template with
        {
            Songs =
            [
                Song(
                    "3d7901c9-7ae2-4adb-9393-4ec4c54c2e3b",
                    "generation-3d7901c9",
                    63_750,
                    65_367),
                Song(
                    "ddd5447c-b5d7-4fe4-8f22-c9854168d11b",
                    "generation-ddd5447c",
                    51_573,
                    51_573),
            ],
        }).ValidateAndNormalize();

        Assert.Equal(1296, manifest.ExpectedPublishedScrapeId);
        Assert.Equal(
            63_750,
            manifest.Songs[0].StagedPath.Maxima.Lead);
        Assert.Equal(
            65_367,
            manifest.Songs[0].StagedPath.Maxima.ProLead);
        Assert.Equal(
            51_573,
            manifest.Songs[1].StagedPath.Maxima.Lead);
        Assert.Equal(
            51_573,
            manifest.Songs[1].StagedPath.Maxima.ProLead);
        Assert.All(
            manifest.Songs,
            song =>
            {
                Assert.Null(song.CurrentPath.Maxima.Lead);
                Assert.Null(song.CurrentPath.Maxima.ProLead);
            });
    }

    [Fact]
    public async Task Rollback_snapshot_round_trips_exact_identity()
    {
        var manifest = CreateManifest();
        var snapshot = new MaxScoreMaintenanceRollbackSnapshot(
            MaxScoreMaintenanceRollbackSnapshot.CurrentSnapshotVersion,
            new DateTime(
                2026,
                8,
                14,
                1,
                2,
                3,
                DateTimeKind.Utc),
            manifest.ComputeDigest(),
            new string('d', 64),
            manifest.ExpectedPublishedScrapeId,
            manifest.ExpectedPublicationId,
            manifest.CatalogContentHash,
            manifest.Songs.Select(song =>
                new MaxScoreMaintenanceRollbackSong(
                    song.SongId,
                    song.ExpectedCatalogLastModified,
                    song.CurrentPath))
                .ToArray());
        var path = Path.Combine(_dataDirectory, "rollback.json");

        var written = await MaxScoreMaintenanceFileStore
            .WriteCanonicalRollbackSnapshotAsync(
                _dataDirectory,
                path,
                snapshot,
                CancellationToken.None);
        var loaded =
            await MaxScoreMaintenanceFileStore.LoadRollbackSnapshotAsync(
                _dataDirectory,
                path,
                CancellationToken.None);

        Assert.Equal(
            JsonSerializer.SerializeToUtf8Bytes(
                snapshot.ValidateAndNormalize(),
                MaxScoreMaintenanceJson.Canonical),
            JsonSerializer.SerializeToUtf8Bytes(
                loaded,
                MaxScoreMaintenanceJson.Canonical));
        Assert.Equal(
            written.Sha256,
            await MaxScoreMaintenanceFileStore.ComputeSha256Async(
                path,
                CancellationToken.None));
    }

    private static MaxScoreMaintenanceManifest CreateManifest()
    {
        var runtime = new PathGenerationRuntimeIdentity(
            "1.16.3",
            new string('b', 64),
            "profile-v3");
        var current = new MaxScoreMaintenancePathIdentity(
            Revision: 4,
            DatFileHash: new string('c', 64),
            SongLastModified: "2026-08-01T00:00:00Z",
            GeneratedAtUtc: new DateTime(
                2026,
                8,
                1,
                0,
                0,
                0,
                DateTimeKind.Utc),
            ChoptVersion: "1.16.2",
            ChoptBinarySha256: new string('a', 64),
            GenerationProfile: "profile-v2",
            ArtifactGenerationId: "generation-old",
            ExpectedInstruments:
                ["Solo_Guitar", "Solo_PeripheralGuitar"],
            Maxima: new MaxScoreMaintenanceMaxima(
                null,
                20,
                30,
                40,
                null,
                60,
                70,
                80),
            PathGenerationPending: false);
        var staged = current with
        {
            DatFileHash = new string('e', 64),
            GeneratedAtUtc = new DateTime(
                2026,
                8,
                14,
                0,
                0,
                0,
                DateTimeKind.Utc),
            ChoptVersion = runtime.Version,
            ChoptBinarySha256 = runtime.BinarySha256,
            GenerationProfile = runtime.Profile,
            ArtifactGenerationId = "generation-new",
            Maxima = current.Maxima with
            {
                Lead = 51_573,
                ProLead = 51_573,
            },
        };
        return new MaxScoreMaintenanceManifest(
            MaxScoreMaintenanceManifest.CurrentManifestVersion,
            ExpectedPublishedScrapeId: 1296,
            ExpectedPublicationId: 500,
            CatalogVersion: 42,
            CatalogSchemaVersion:
                SongCatalogSnapshotBuilder.SchemaVersion,
            CatalogContentHash: new string('f', 64),
            CatalogSongCount: 700,
            CatalogSourceCapturedAtUtc: new DateTime(
                2026,
                8,
                13,
                0,
                0,
                0,
                DateTimeKind.Utc),
            CreatedAtUtc: new DateTime(
                2026,
                8,
                14,
                0,
                0,
                0,
                DateTimeKind.Utc),
            Runtime: runtime,
            Songs:
            [
                new MaxScoreMaintenanceManifestSong(
                    "song-a",
                    "2026-08-01T00:00:00Z",
                    current,
                    staged,
                    ["Solo_Guitar", "Solo_PeripheralGuitar"]),
            ])
            .ValidateAndNormalize();
    }
}
