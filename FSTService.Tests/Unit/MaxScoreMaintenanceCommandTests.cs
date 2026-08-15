using System.Text;
using System.Text.Json;
using FSTService.Persistence;
using FSTService.Scraping;

namespace FSTService.Tests.Unit;

public sealed class MaxScoreMaintenanceCommandTests : IDisposable
{
    private const string ShowThemWhoWeAreSongId =
        "3d7901c9-7ae2-4adb-9393-4ec4c54c2e3b";
    private const string RunItSongId =
        "ddd5447c-b5d7-4fe4-8f22-c9854168d11b";
    private static readonly string[] ExpectedChangedInstruments =
    [
        "Solo_Guitar",
        "Solo_PeripheralGuitar",
        "Solo_PeripheralCymbals",
        "Solo_PeripheralDrums",
    ];
    private const string ValidPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

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
    public void Stage_parser_requires_scoped_request_file()
    {
        var command = MaxScoreMaintenanceCommand.Parse(
        [
            MaxScoreMaintenanceCommand.StageFlag,
            MaxScoreMaintenanceCommand.PublishedScrapeIdFlag,
            "1296",
            MaxScoreMaintenanceCommand.StageRequestFlag,
            "maintenance/request.json",
            MaxScoreMaintenanceCommand.ManifestOutputFlag,
            "maintenance/manifest.json",
            MaxScoreMaintenanceCommand.ReportOutputFlag,
            "maintenance/stage-report.json",
        ]);

        Assert.NotNull(command);
        Assert.Equal(MaxScoreMaintenanceAction.Stage, command.Action);
        Assert.Empty(command.SongIds);
        Assert.Equal(
            "maintenance/request.json",
            command.StageRequestPath);
    }

    [Fact]
    public void Parser_ignores_notification_recovery_shared_scrape_id()
    {
        var command = MaxScoreMaintenanceCommand.Parse(
        [
            "--recover-improvement-notifications",
            "--published-scrape-id",
            "1296",
            "--notification-dry-run",
        ]);

        Assert.Null(command);
    }

    [Theory]
    [InlineData(
        "--published-scrape-id",
        "1296")]
    [InlineData(
        "--published-scrape-id=1296",
        null)]
    public void Shared_scrape_id_parser_accepts_token_and_equals_forms(
        string argument,
        string? separateValue)
    {
        var args = separateValue is null
            ? new[] { argument }
            : new[] { argument, separateValue };

        var parsed = PublishedScrapeIdArgument.Parse(args);

        Assert.True(parsed.IsPresent);
        Assert.Equal(1296, parsed.Value);
    }

    [Theory]
    [InlineData("--published-scrape-id")]
    [InlineData("--published-scrape-id=")]
    [InlineData("--published-scrape-id=abc")]
    [InlineData("--published-scrape-id=0")]
    [InlineData("--published-scrape-id=-1")]
    public void Shared_scrape_id_parser_rejects_missing_or_malformed_values(
        string argument)
    {
        Assert.Throws<ArgumentException>(() =>
            PublishedScrapeIdArgument.Parse([argument]));
    }

    [Fact]
    public void Shared_scrape_id_parser_rejects_duplicate_forms()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            PublishedScrapeIdArgument.Parse(
            [
                "--published-scrape-id",
                "1296",
                "--published-scrape-id=1296",
            ]));

        Assert.Contains("exactly once", error.Message);
    }

    [Fact]
    public void Shared_scrape_id_parser_rejects_orphaned_option()
    {
        var parsed = PublishedScrapeIdArgument.Parse(
            ["--published-scrape-id=1296"]);

        Assert.Throws<ArgumentException>(() =>
            parsed.RejectIfOrphaned(
                hasOwningCommand: false));
    }

    [Theory]
    [InlineData(
        MaxScoreMaintenanceCommand.StageFlag,
        MaxScoreMaintenanceAction.Stage)]
    [InlineData(
        MaxScoreMaintenanceCommand.PlanFlag,
        MaxScoreMaintenanceAction.Plan)]
    [InlineData(
        MaxScoreMaintenanceCommand.ApplyFlag,
        MaxScoreMaintenanceAction.Apply)]
    [InlineData(
        MaxScoreMaintenanceCommand.ResumeFlag,
        MaxScoreMaintenanceAction.Resume)]
    public void Max_score_actions_route_equals_form_shared_scrape_id(
        string actionFlag,
        MaxScoreMaintenanceAction expectedAction)
    {
        var args = new List<string>
        {
            actionFlag,
            "--published-scrape-id=1296",
            MaxScoreMaintenanceCommand.ReportOutputFlag,
            "report.json",
        };
        switch (expectedAction)
        {
            case MaxScoreMaintenanceAction.Stage:
                args.AddRange(
                [
                    MaxScoreMaintenanceCommand.StageRequestFlag,
                    "request.json",
                    MaxScoreMaintenanceCommand.ManifestOutputFlag,
                    "manifest.json",
                ]);
                break;
            case MaxScoreMaintenanceAction.Plan:
                args.AddRange(
                [
                    MaxScoreMaintenanceCommand.ManifestFlag,
                    "manifest.json",
                    MaxScoreMaintenanceCommand
                        .ExpectedManifestDigestFlag,
                    new string('a', 64),
                ]);
                break;
            case MaxScoreMaintenanceAction.Apply:
                args.AddRange(
                [
                    MaxScoreMaintenanceCommand.ManifestFlag,
                    "manifest.json",
                    MaxScoreMaintenanceCommand.RollbackOutputFlag,
                    "rollback.json",
                    MaxScoreMaintenanceCommand
                        .ExpectedManifestDigestFlag,
                    new string('a', 64),
                    MaxScoreMaintenanceCommand
                        .ExpectedPlanDigestFlag,
                    new string('b', 64),
                ]);
                break;
            case MaxScoreMaintenanceAction.Resume:
                args.AddRange(
                [
                    MaxScoreMaintenanceCommand.ManifestFlag,
                    "manifest.json",
                    MaxScoreMaintenanceCommand
                        .ExpectedManifestDigestFlag,
                    new string('a', 64),
                    MaxScoreMaintenanceCommand
                        .ExpectedPlanDigestFlag,
                    new string('b', 64),
                ]);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(expectedAction));
        }

        var command = MaxScoreMaintenanceCommand.Parse(args);

        Assert.NotNull(command);
        Assert.Equal(expectedAction, command.Action);
        Assert.Equal(1296, command.ExpectedPublishedScrapeId);
    }

    [Fact]
    public void Stage_parser_rejects_unscoped_song_ids()
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

        Assert.Contains(
            MaxScoreMaintenanceCommand.SongIdFlag,
            error.Message);
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
    public void Parser_rejects_multiple_actions()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            MaxScoreMaintenanceCommand.Parse(
            [
                MaxScoreMaintenanceCommand.StageFlag,
                MaxScoreMaintenanceCommand.PlanFlag,
                MaxScoreMaintenanceCommand.PublishedScrapeIdFlag,
                "1296",
                MaxScoreMaintenanceCommand.ReportOutputFlag,
                "report.json",
            ]));

        Assert.Contains("exactly one", error.Message);
    }

    [Fact]
    public void Parser_rejects_duplicate_song_ids_before_scope_validation()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            MaxScoreMaintenanceCommand.Parse(
            [
                MaxScoreMaintenanceCommand.StageFlag,
                MaxScoreMaintenanceCommand.PublishedScrapeIdFlag,
                "1296",
                MaxScoreMaintenanceCommand.SongIdFlag,
                "song-a",
                MaxScoreMaintenanceCommand.SongIdFlag,
                "song-a",
                MaxScoreMaintenanceCommand.ReportOutputFlag,
                "report.json",
            ]));

        Assert.Contains("unique", error.Message);
    }

    [Fact]
    public void Parser_rejects_more_than_maximum_song_ids()
    {
        var args = new List<string>
        {
            MaxScoreMaintenanceCommand.StageFlag,
            MaxScoreMaintenanceCommand.PublishedScrapeIdFlag,
            "1296",
            MaxScoreMaintenanceCommand.ReportOutputFlag,
            "report.json",
        };
        foreach (var index in Enumerable.Range(
                     0,
                     MaxScoreMaintenanceManifest.MaximumSongs + 1))
        {
            args.Add(MaxScoreMaintenanceCommand.SongIdFlag);
            args.Add($"song-{index:D2}");
        }

        var error = Assert.Throws<ArgumentException>(() =>
            MaxScoreMaintenanceCommand.Parse(args));

        Assert.Contains("at most", error.Message);
    }

    [Fact]
    public void Parser_rejects_missing_option_value()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            MaxScoreMaintenanceCommand.Parse(
            [
                MaxScoreMaintenanceCommand.StageFlag,
                MaxScoreMaintenanceCommand.PublishedScrapeIdFlag,
                "1296",
                MaxScoreMaintenanceCommand.ReportOutputFlag,
            ]));

        Assert.Contains("requires a value", error.Message);
    }

    [Fact]
    public void Parser_rejects_action_flag_value()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            MaxScoreMaintenanceCommand.Parse(
            [
                $"{MaxScoreMaintenanceCommand.StageFlag}=true",
                MaxScoreMaintenanceCommand.PublishedScrapeIdFlag,
                "1296",
                MaxScoreMaintenanceCommand.ReportOutputFlag,
                "report.json",
            ]));

        Assert.Contains("without a value", error.Message);
    }

    [Fact]
    public void Parser_rejects_duplicate_single_value_option()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            MaxScoreMaintenanceCommand.Parse(
            [
                MaxScoreMaintenanceCommand.StageFlag,
                MaxScoreMaintenanceCommand.PublishedScrapeIdFlag,
                "1296",
                MaxScoreMaintenanceCommand.StageRequestFlag,
                "request.json",
                MaxScoreMaintenanceCommand.ManifestOutputFlag,
                "manifest.json",
                MaxScoreMaintenanceCommand.ReportOutputFlag,
                "first.json",
                MaxScoreMaintenanceCommand.ReportOutputFlag,
                "second.json",
            ]));

        Assert.Contains("specified once", error.Message);
    }

    [Fact]
    public void Parser_rejects_action_specific_option()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            MaxScoreMaintenanceCommand.Parse(
            [
                MaxScoreMaintenanceCommand.PlanFlag,
                MaxScoreMaintenanceCommand.PublishedScrapeIdFlag,
                "1296",
                MaxScoreMaintenanceCommand.ManifestFlag,
                "manifest.json",
                MaxScoreMaintenanceCommand.ExpectedManifestDigestFlag,
                new string('a', 64),
                MaxScoreMaintenanceCommand.RollbackOutputFlag,
                "rollback.json",
                MaxScoreMaintenanceCommand.ReportOutputFlag,
                "report.json",
            ]));

        Assert.Contains(
            MaxScoreMaintenanceCommand.RollbackOutputFlag,
            error.Message);
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
    public async Task Stage_request_is_canonical_and_binds_discovery_or_promotion_scope()
    {
        foreach (var purpose in new[]
                 {
                     MaxScoreMaintenanceStagePurposes.Discovery,
                     MaxScoreMaintenanceStagePurposes.Promotion,
                 })
        {
            var request = CreateStageRequest(purpose);
            var path = Path.Combine(
                _dataDirectory,
                $"{purpose}-request.json");
            await File.WriteAllBytesAsync(
                path,
                request.SerializeCanonical());

            var loaded =
                await MaxScoreMaintenanceFileStore
                    .LoadStageRequestAsync(
                        _dataDirectory,
                        path,
                        CancellationToken.None);

            Assert.Equal(purpose, loaded.Purpose);
            Assert.Equal(
                MaxScoreMaintenanceManifest.AllInstruments,
                loaded.ExpectedPathInstruments);
            Assert.Equal(
                ExpectedChangedInstruments,
                loaded.ExpectedChangedInstruments);
            Assert.Equal(
                request.ComputeDigest(),
                loaded.ComputeDigest());
            Assert.All(
                loaded.Songs,
                song =>
                {
                    if (purpose
                        == MaxScoreMaintenanceStagePurposes.Discovery)
                    {
                        Assert.Null(song.ExpectedOldMaxima);
                        Assert.Null(song.ExpectedNewMaxima);
                        Assert.Equal(
                            ExpectedChangedInstruments,
                            song.ExpectedOldConstraints!
                                .Select(constraint =>
                                    constraint.Instrument));
                        Assert.Equal(
                            [
                                "Solo_Guitar",
                                "Solo_PeripheralGuitar",
                            ],
                            song.ExpectedNewConstraints!
                                .Select(constraint =>
                                    constraint.Instrument));
                    }
                    else
                    {
                        Assert.NotNull(song.ExpectedOldMaxima);
                        Assert.NotNull(song.ExpectedNewMaxima);
                        Assert.Empty(
                            song.ExpectedOldConstraints!);
                        Assert.Empty(
                            song.ExpectedNewConstraints!);
                    }
                });
        }
    }

    [Fact]
    public async Task Stage_request_loader_rejects_noncanonical_scope()
    {
        var request = CreateStageRequest(
            MaxScoreMaintenanceStagePurposes.Discovery);
        var path = Path.Combine(
            _dataDirectory,
            "noncanonical-request.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                request,
                MaxScoreMaintenanceJson.Report));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            MaxScoreMaintenanceFileStore.LoadStageRequestAsync(
                _dataDirectory,
                path,
                CancellationToken.None));
    }

    [Fact]
    public void Discovery_manifest_is_never_promotion_ready()
    {
        var promotion = CreateManifest();
        var discovery = (promotion with
        {
            Scope = promotion.Scope with
            {
                Purpose =
                    MaxScoreMaintenanceStagePurposes.Discovery,
            },
        }).ValidateAndNormalize();

        var error = Assert.Throws<InvalidOperationException>(
            discovery.RequirePromotionReady);

        Assert.Contains("Discovery", error.Message);
    }

    [Fact]
    public void Plastic_drums_manifest_rejects_staged_v3()
    {
        var manifest = CreateManifest();
        var invalid = manifest with
        {
            Runtime = new PathGenerationRuntimeIdentity(
                "1.16.3",
                new string('a', 64),
                PathGenerationProfiles.InvalidPlasticDrumsV3),
            Songs = manifest.Songs
                .Select(song => song with
                {
                    StagedPath = song.StagedPath with
                    {
                        ChoptVersion = "1.16.3",
                        ChoptBinarySha256 = new string('a', 64),
                        GenerationProfile =
                            PathGenerationProfiles
                                .InvalidPlasticDrumsV3,
                    },
                })
                .ToArray(),
        };

        Assert.Throws<ArgumentException>(
            invalid.ValidateAndNormalize);
    }

    [Fact]
    public void Plastic_drums_manifest_rejects_current_v3()
    {
        var manifest = CreateManifest();
        var invalid = manifest with
        {
            Songs = manifest.Songs
                .Select(song => song with
                {
                    CurrentPath = song.CurrentPath with
                    {
                        GenerationProfile =
                            PathGenerationProfiles
                                .InvalidPlasticDrumsV3,
                    },
                })
                .ToArray(),
        };

        Assert.Throws<ArgumentException>(
            invalid.ValidateAndNormalize);
    }

    [Theory]
    [InlineData(60_000, null, true)]
    [InlineData(60_000, 59_999, true)]
    [InlineData(60_000, 60_000, true)]
    [InlineData(60_000, 60_001, false)]
    public void Observed_score_gate_rejects_maximum_below_live_score(
        int newMaximum,
        int? highestObservedScore,
        bool expected)
    {
        Assert.Equal(
            expected,
            MaxScoreMaintenanceService
                .IsObservedScoreCompatible(
                    newMaximum,
                    highestObservedScore));
    }

    [Fact]
    public void Current_v2_artifact_tree_is_required_for_plan_and_rollback_identity()
    {
        var template = CreateManifest();
        var song = template.Songs[0];
        WriteGeneration(song.SongId, song.CurrentPath);
        WriteGeneration(song.SongId, song.StagedPath);
        var currentValidated =
            PathArtifactResolver.ValidateImmutableGeneration(
                _dataDirectory,
                song.SongId,
                song.CurrentPath.ArtifactGenerationId!);
        var stagedValidated =
            PathArtifactResolver.ValidateImmutableGeneration(
                _dataDirectory,
                song.SongId,
                song.StagedPath.ArtifactGenerationId!);
        var boundSong = (song with
        {
            CurrentPath = song.CurrentPath with
            {
                ArtifactTreeSha256 =
                    currentValidated.ArtifactTreeSha256,
                ArtifactFileCount =
                    currentValidated.ArtifactFileCount,
            },
            StagedPath = song.StagedPath with
            {
                ArtifactTreeSha256 =
                    stagedValidated.ArtifactTreeSha256,
                ArtifactFileCount =
                    stagedValidated.ArtifactFileCount,
            },
            PlasticDrumsEvidence =
                MaxScoreMaintenanceArtifactValidator
                    .CapturePlasticDrumsEvidence(
                        stagedValidated),
        }).ValidateAndNormalize();

        var evidence =
            MaxScoreMaintenanceArtifactValidator
                .ValidateManifestSong(
                    _dataDirectory,
                    boundSong);

        Assert.Equal(
            currentValidated.ArtifactTreeSha256,
            evidence.CurrentArtifactTreeSha256);
        Assert.Equal(
            currentValidated.ArtifactFileCount,
            evidence.CurrentArtifactFileCount);

        File.Delete(Path.Combine(
            currentValidated.GenerationDirectory,
            "Solo_Bass",
            "expert.json"));
        Assert.Throws<InvalidOperationException>(() =>
            MaxScoreMaintenanceArtifactValidator
                .ValidateManifestSong(
                    _dataDirectory,
                    boundSong));
    }

    [Fact]
    public void Publication1296_manifest_binds_exact_four_changes_and_eight_instruments()
    {
        var manifest = CreateManifest();

        Assert.Equal(1296, manifest.ExpectedPublishedScrapeId);
        Assert.Equal(
            MaxScoreMaintenanceStagePurposes.Promotion,
            manifest.Scope.Purpose);
        Assert.Equal(
            MaxScoreMaintenanceManifest.AllInstruments,
            manifest.Scope.ExpectedPathInstruments);
        Assert.Equal(
            ExpectedChangedInstruments,
            manifest.Scope.ExpectedChangedInstruments);
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
                Assert.Null(song.CurrentPath.Maxima.ProCymbals);
                Assert.Null(song.CurrentPath.Maxima.ProDrums);
                Assert.Equal(
                    ExpectedChangedInstruments,
                    song.ChangedInstruments);
                Assert.Equal(
                    MaxScoreMaintenanceManifest.AllInstruments,
                    song.StagedPath.ExpectedInstruments);
                Assert.NotNull(song.PlasticDrumsEvidence);
            });
    }

    [Fact]
    public void Promotion_admission_includes_exact_four_live_shaped_pairs()
    {
        var template = CreateManifest();
        var manifest = (template with
        {
            Songs = [template.Songs[0]],
        }).ValidateAndNormalize();

        var pairs =
            MaxScoreMaintenanceService
                .GetNewlyAdmittedPathPairs(manifest);

        Assert.Equal(
            [
                new SoloCurrentProjectionScopeKey(
                    ShowThemWhoWeAreSongId,
                    "Solo_Guitar"),
                new SoloCurrentProjectionScopeKey(
                    ShowThemWhoWeAreSongId,
                    "Solo_PeripheralGuitar"),
                new SoloCurrentProjectionScopeKey(
                    ShowThemWhoWeAreSongId,
                    "Solo_PeripheralCymbals"),
                new SoloCurrentProjectionScopeKey(
                    ShowThemWhoWeAreSongId,
                    "Solo_PeripheralDrums"),
            ],
            pairs);
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
            manifest.CatalogVersion,
            manifest.CatalogSchemaVersion,
            manifest.CatalogContentHash,
            manifest.CatalogSongCount,
            manifest.CatalogSourceCapturedAtUtc,
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
        Assert.Equal(2, loaded.Songs.Count);
        Assert.Equal(3, loaded.SnapshotVersion);
        Assert.All(
            loaded.Songs,
            song =>
            {
                Assert.NotNull(song.Path.ArtifactTreeSha256);
                Assert.True(song.Path.ArtifactFileCount > 0);
                Assert.Equal(
                    MaxScoreMaintenanceStagePurposes.Promotion,
                    manifest.Scope.Purpose);
            });
    }

    private static MaxScoreMaintenanceStageRequest CreateStageRequest(
        string purpose)
    {
        var manifest = CreateManifest();
        return new MaxScoreMaintenanceStageRequest(
            MaxScoreMaintenanceStageRequest.CurrentRequestVersion,
            purpose,
            manifest.ExpectedPublishedScrapeId,
            manifest.Scope.ExpectedPathInstruments,
            manifest.Scope.ExpectedChangedInstruments,
            manifest.Songs
                .Select(song =>
                    new MaxScoreMaintenanceStageRequestSong(
                        song.SongId,
                        purpose
                            == MaxScoreMaintenanceStagePurposes
                                .Promotion
                            ? song.CurrentPath.Maxima
                            : null,
                        purpose
                            == MaxScoreMaintenanceStagePurposes
                                .Promotion
                            ? song.StagedPath.Maxima
                            : null,
                        purpose
                            == MaxScoreMaintenanceStagePurposes
                                .Discovery
                            ? ExpectedChangedInstruments
                                .Select(instrument =>
                                    new MaxScoreMaintenanceMaximaConstraint(
                                        instrument,
                                        null))
                                .ToArray()
                            : null,
                        purpose
                            == MaxScoreMaintenanceStagePurposes
                                .Discovery
                            ?
                            [
                                new(
                                    "Solo_Guitar",
                                    song.StagedPath.Maxima.Lead),
                                new(
                                    "Solo_PeripheralGuitar",
                                    song.StagedPath.Maxima.ProLead),
                            ]
                            : null))
                .ToArray(),
            manifest.Runtime.Version,
            manifest.Runtime.BinarySha256,
            manifest.Runtime.Profile)
            .ValidateAndNormalize();
    }

    private void WriteGeneration(
        string songId,
        MaxScoreMaintenancePathIdentity identity)
    {
        var generationDirectory =
            PathArtifactResolver.GetGenerationDirectory(
                _dataDirectory,
                songId,
                identity.ArtifactGenerationId!);
        Directory.CreateDirectory(generationDirectory);
        var expertScores = identity.ExpectedInstruments
            .ToDictionary(
                instrument => instrument,
                instrument => identity.Maxima
                    .GetByInstrument(instrument)!.Value,
                StringComparer.Ordinal);
        var manifest = new PathArtifactManifest(
            identity.ArtifactGenerationId!,
            songId,
            identity.DatFileHash!,
            identity.SongLastModified,
            identity.ChoptVersion!,
            identity.ChoptBinarySha256!,
            identity.GenerationProfile!,
            identity.ExpectedInstruments.ToArray(),
            expertScores,
            identity.GeneratedAtUtc!.Value);
        File.WriteAllText(
            Path.Combine(
                generationDirectory,
                PathArtifactResolver.ManifestFileName),
            JsonSerializer.Serialize(
                manifest,
                PathArtifactManifest.JsonOptions));

        var png = Convert.FromBase64String(ValidPngBase64);
        foreach (var instrument in identity.ExpectedInstruments)
        {
            var instrumentDirectory = Path.Combine(
                generationDirectory,
                instrument);
            Directory.CreateDirectory(instrumentDirectory);
            foreach (var difficulty in
                     PathGenerationInstruments.Difficulties)
            {
                File.WriteAllBytes(
                    Path.Combine(
                        instrumentDirectory,
                        $"{difficulty}.png"),
                    png);
                File.WriteAllText(
                    Path.Combine(
                        instrumentDirectory,
                        $"{difficulty}.json"),
                    BuildPathJson(
                        difficulty,
                        difficulty == "expert"
                            ? expertScores[instrument]
                            : 0,
                        instrument,
                        identity.GenerationProfile!));
            }
        }
    }

    private static string BuildPathJson(
        string difficulty,
        int totalScore,
        string instrument,
        string generationProfile)
        => JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            songName = "Song",
            artist = "Artist",
            charter = "Charter",
            difficulty,
            totalScore,
            pathSummary = string.Empty,
            activations = Array.Empty<object>(),
            notes = new[]
            {
                new
                {
                    beat = 1,
                    seconds = 0.5,
                    isSpNote = false,
                    frets = new Dictionary<string, int>
                    {
                        [instrument] = 1,
                    },
                },
            },
            spPhrases = Array.Empty<object>(),
            measures = Array.Empty<object>(),
            bpms = Array.Empty<object>(),
            timeSignatures = Array.Empty<object>(),
            drumFills =
                difficulty == "expert"
                && PathGenerationProfiles.RequiresAuthoredDrumFills(
                    generationProfile)
                && PathGenerationInstruments
                    .IsPlasticDrumsInstrument(instrument)
                    ? new object[]
                    {
                        new
                        {
                            startBeat = 1,
                            endBeat = 2,
                        },
                    }
                    : Array.Empty<object>(),
        });

    private static MaxScoreMaintenanceManifest CreateManifest()
    {
        var runtime = new PathGenerationRuntimeIdentity(
            PathGenerationProfiles.PlasticDrumsV4ChoptVersion,
            PathGenerationProfiles.PlasticDrumsV4BinarySha256,
            PathGenerationProfiles.PlasticDrumsV4);
        var oldMaxima = new MaxScoreMaintenanceMaxima(
            null,
            20_000,
            30_000,
            40_000,
            null,
            60_000,
            null,
            null);
        MaxScoreMaintenanceManifestSong Song(
            string songId,
            string generationSuffix,
            int lead,
            int proLead,
            int proCymbals,
            int proDrums)
        {
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
                ChoptVersion: "1.16.3",
                ChoptBinarySha256: new string('a', 64),
                GenerationProfile:
                    "chopt-fnf-ew0-s20-json-png-v2",
                ArtifactGenerationId:
                    $"generation-{generationSuffix}-v2",
                ExpectedInstruments:
                [
                    "Solo_Bass",
                    "Solo_Drums",
                    "Solo_Vocals",
                    "Solo_PeripheralBass",
                ],
                Maxima: oldMaxima,
                PathGenerationPending: false,
                ArtifactTreeSha256: new string('4', 64),
                ArtifactFileCount: 33);
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
                ArtifactGenerationId =
                    $"generation-{generationSuffix}-v4",
                ExpectedInstruments =
                    MaxScoreMaintenanceManifest.AllInstruments,
                Maxima = oldMaxima with
                {
                    Lead = lead,
                    ProLead = proLead,
                    ProCymbals = proCymbals,
                    ProDrums = proDrums,
                },
                ArtifactTreeSha256 = new string('5', 64),
                ArtifactFileCount = 65,
            };
            return new MaxScoreMaintenanceManifestSong(
                songId,
                "2026-08-01T00:00:00Z",
                current,
                staged,
                ExpectedChangedInstruments,
                new MaxScoreMaintenancePlasticDrumsEvidence(
                    2,
                    2,
                    new string('1', 64),
                    new string('2', 64),
                    new string('3', 64)));
        }
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
            Scope: new MaxScoreMaintenanceScope(
                MaxScoreMaintenanceStagePurposes.Promotion,
                new string('d', 64),
                MaxScoreMaintenanceManifest.AllInstruments,
                ExpectedChangedInstruments),
            Runtime: runtime,
            Songs:
            [
                Song(
                    ShowThemWhoWeAreSongId,
                    "show",
                    63_750,
                    65_367,
                    70_000,
                    68_000),
                Song(
                    RunItSongId,
                    "run-it",
                    51_573,
                    51_573,
                    60_000,
                    58_000),
            ])
            .ValidateAndNormalize();
    }
}
