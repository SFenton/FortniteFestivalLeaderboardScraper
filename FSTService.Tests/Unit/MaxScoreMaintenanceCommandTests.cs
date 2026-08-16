using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public static TheoryData<string> MaximumScoreInstruments { get; } =
        new()
        {
            "Solo_Guitar",
            "Solo_Bass",
            "Solo_Drums",
            "Solo_Vocals",
            "Solo_PeripheralGuitar",
            "Solo_PeripheralBass",
            "Solo_PeripheralCymbals",
            "Solo_PeripheralDrums",
        };
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
    public async Task Apply_report_v3_round_trips_required_cache_evidence()
    {
        var report = CreateApplyReport(
            MaxScoreMaintenancePhase.Completed,
            succeeded: true,
            resumable: false,
            frozen: false,
            includeCacheEvidence: true);

        await MaxScoreMaintenanceFileStore.WriteNewReportAsync(
            _dataDirectory,
            "apply-report-v3.json",
            report,
            CancellationToken.None);
        var loaded =
            await MaxScoreMaintenanceFileStore
                .LoadApplyReportAsync(
                    _dataDirectory,
                    "apply-report-v3.json",
                    CancellationToken.None);

        Assert.Equal(
            MaxScoreMaintenanceApplyReport
                .CurrentReportVersion,
            loaded.ReportVersion);
        Assert.Equal(report, loaded);
        Assert.NotNull(loaded.CacheEvidence);
        using var document = JsonDocument.Parse(
            await File.ReadAllBytesAsync(
                Path.Combine(
                    _dataDirectory,
                    "apply-report-v3.json")));
        var cacheEvidence = document.RootElement
            .GetProperty("cacheEvidence");
        Assert.Equal(
            4,
            cacheEvidence
                .GetProperty(
                    "publishedScopeCacheKeyCount")
                .GetInt32());
        Assert.Equal(
            new string('e', 64),
            cacheEvidence
                .GetProperty(
                    "overlayOnlyAccountFingerprint")
                .GetString());
    }

    [Fact]
    public async Task Apply_report_v3_accepts_pre_cache_failure_without_evidence()
    {
        var report = CreateApplyReport(
            MaxScoreMaintenancePhase.FreezeEstablished,
            succeeded: false,
            resumable: true,
            frozen: true,
            includeCacheEvidence: false);
        await MaxScoreMaintenanceFileStore.WriteNewReportAsync(
            _dataDirectory,
            "apply-report-pre-cache.json",
            report,
            CancellationToken.None);

        var loaded =
            await MaxScoreMaintenanceFileStore
                .LoadApplyReportAsync(
                    _dataDirectory,
                    "apply-report-pre-cache.json",
                    CancellationToken.None);

        Assert.Null(loaded.CacheEvidence);
        Assert.Equal(0, loaded.StagedCacheEntryCount);
    }

    [Theory]
    [InlineData("legacy-version")]
    [InlineData("missing-cache-evidence")]
    [InlineData("unknown-property")]
    public async Task Apply_report_parser_rejects_incompatible_or_non_strict_contracts(
        string mutation)
    {
        var report = CreateApplyReport(
            MaxScoreMaintenancePhase.CachesStaged,
            succeeded: false,
            resumable: true,
            frozen: true,
            includeCacheEvidence: true);
        var incompatible = mutation switch
        {
            "legacy-version" => report with
            {
                ReportVersion = 2,
            },
            "missing-cache-evidence" => report with
            {
                CacheEvidence = null,
            },
            "unknown-property" => report,
            _ => throw new ArgumentOutOfRangeException(
                nameof(mutation)),
        };
        var json = JsonSerializer.Serialize(
            incompatible,
            MaxScoreMaintenanceJson.Report);
        if (mutation == "unknown-property")
        {
            json = json.Insert(
                json.LastIndexOf(
                    '}'),
                ",\"unexpected\":true");
        }
        var fileName =
            $"apply-report-invalid-{mutation}.json";
        await File.WriteAllTextAsync(
            Path.Combine(
                _dataDirectory,
                fileName),
            json);

        var error = await Assert.ThrowsAsync<
            ArgumentException>(
            () => MaxScoreMaintenanceFileStore
                .LoadApplyReportAsync(
                    _dataDirectory,
                    fileName,
                    CancellationToken.None));
        Assert.Contains(
            "strict version 3",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
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
    public void Ranking_cutoff_uses_the_largest_exact_int_boundary()
    {
        Assert.Equal(
            2_045_222_521,
            RankingsCalculator
                .MaximumScoreWithRepresentableRankingCutoff);
        Assert.Equal(
            int.MaxValue,
            RankingsCalculator.ComputeMaxScoreThreshold(
                RankingsCalculator
                    .MaximumScoreWithRepresentableRankingCutoff));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RankingsCalculator.ComputeMaxScoreThreshold(
                checked(
                    RankingsCalculator
                        .MaximumScoreWithRepresentableRankingCutoff
                    + 1)));
    }

    [Theory]
    [MemberData(nameof(MaximumScoreInstruments))]
    public void Maxima_validation_enforces_the_cutoff_boundary_for_every_field(
        string instrument)
    {
        var maxima = new MaxScoreMaintenanceMaxima(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        var accepted = WithMaximum(
            maxima,
            instrument,
            RankingsCalculator
                .MaximumScoreWithRepresentableRankingCutoff);

        Assert.Equal(accepted, accepted.Validate("maxima"));

        var rejected = WithMaximum(
            maxima,
            instrument,
            checked(
                RankingsCalculator
                    .MaximumScoreWithRepresentableRankingCutoff
                + 1));
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => rejected.Validate("maxima"));
        Assert.Contains("PostgreSQL INTEGER", error.Message);
    }

    [Theory]
    [MemberData(nameof(MaximumScoreInstruments))]
    public void Discovery_constraints_enforce_the_cutoff_boundary(
        string instrument)
    {
        var accepted = new MaxScoreMaintenanceMaximaConstraint(
            instrument,
            RankingsCalculator
                .MaximumScoreWithRepresentableRankingCutoff);

        Assert.Equal(
            accepted,
            accepted.ValidateAndNormalize());

        var rejected = accepted with
        {
            ExpectedValue = checked(
                RankingsCalculator
                    .MaximumScoreWithRepresentableRankingCutoff
                + 1),
        };
        Assert.Throws<ArgumentOutOfRangeException>(
            rejected.ValidateAndNormalize);
    }

    [Fact]
    public void Stage_actual_maxima_reject_an_unrepresentable_cutoff_before_manifest()
    {
        var request = CreateStageRequest(
            MaxScoreMaintenanceStagePurposes.Discovery);
        var song = request.Songs[0];
        var invalid = WithMaximum(
            CreateManifest().Songs[0].CurrentPath.Maxima,
            "Solo_Guitar",
            checked(
                RankingsCalculator
                    .MaximumScoreWithRepresentableRankingCutoff
                + 1));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => song.ValidateOldMaxima(invalid));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => song.ValidateNewMaxima(invalid));
    }

    [Theory]
    [MemberData(nameof(MaximumScoreInstruments))]
    public async Task Promotion_request_loader_enforces_the_cutoff_boundary_for_every_field(
        string instrument)
    {
        var request = CreateStageRequest(
            MaxScoreMaintenanceStagePurposes.Promotion);
        var accepted = WithPromotionMaximum(
            request,
            instrument,
            RankingsCalculator
                .MaximumScoreWithRepresentableRankingCutoff);
        var path = Path.Combine(
            _dataDirectory,
            $"promotion-boundary-{instrument}.json");
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                accepted,
                MaxScoreMaintenanceJson.Canonical));

        var loaded =
            await MaxScoreMaintenanceFileStore
                .LoadStageRequestAsync(
                    _dataDirectory,
                    path,
                    CancellationToken.None);
        Assert.All(
            loaded.Songs,
            song => Assert.Equal(
                RankingsCalculator
                    .MaximumScoreWithRepresentableRankingCutoff,
                song.ExpectedNewMaxima!
                    .GetByInstrument(instrument)));

        var rejected = WithPromotionMaximum(
            request,
            instrument,
            checked(
                RankingsCalculator
                    .MaximumScoreWithRepresentableRankingCutoff
                + 1));
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                rejected,
                MaxScoreMaintenanceJson.Canonical));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            MaxScoreMaintenanceFileStore.LoadStageRequestAsync(
                _dataDirectory,
                path,
                CancellationToken.None));
    }

    [Fact]
    public async Task Manifest_loader_rejects_an_unrepresentable_cutoff_before_plan()
    {
        var manifest = CreateManifest();
        MaxScoreMaintenanceManifest WithLeadMaximum(int maximum)
            => manifest with
            {
                Songs = manifest.Songs
                    .Select(song => song with
                    {
                        StagedPath = song.StagedPath with
                        {
                            Maxima = song.StagedPath.Maxima with
                            {
                                Lead = maximum,
                            },
                        },
                    })
                    .ToArray(),
            };

        var path = Path.Combine(
            _dataDirectory,
            "manifest-cutoff-boundary.json");
        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                WithLeadMaximum(
                    RankingsCalculator
                        .MaximumScoreWithRepresentableRankingCutoff),
                MaxScoreMaintenanceJson.Canonical));
        var loaded =
            await MaxScoreMaintenanceFileStore.LoadManifestAsync(
                _dataDirectory,
                path,
                CancellationToken.None);
        Assert.All(
            loaded.Songs,
            song => Assert.Equal(
                RankingsCalculator
                    .MaximumScoreWithRepresentableRankingCutoff,
                song.StagedPath.Maxima.Lead));

        await File.WriteAllBytesAsync(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                WithLeadMaximum(
                    checked(
                        RankingsCalculator
                            .MaximumScoreWithRepresentableRankingCutoff
                        + 1)),
                MaxScoreMaintenanceJson.Canonical));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            MaxScoreMaintenanceFileStore.LoadManifestAsync(
                _dataDirectory,
                path,
                CancellationToken.None));
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
    [InlineData(60_000, true, null, 63_000, true)]
    [InlineData(60_000, true, 60_000, 63_000, true)]
    [InlineData(60_000, true, 60_001, 63_000, true)]
    [InlineData(60_000, true, 63_000, 63_000, true)]
    [InlineData(60_000, true, 63_001, 63_000, false)]
    [InlineData(60_000, false, null, 63_000, false)]
    [InlineData(51_573, true, 54_151, 54_151, true)]
    [InlineData(51_573, true, 54_152, 54_151, false)]
    public void Observed_score_gate_uses_mapped_ranking_validity_cutoff(
        int newMaximum,
        bool sourceMapped,
        int? highestObservedScore,
        int expectedValidCutoff,
        bool expected)
    {
        var validCutoff =
            RankingsCalculator.ComputeMaxScoreThreshold(
                newMaximum);

        Assert.Equal(expectedValidCutoff, validCutoff);
        Assert.Equal(
            expected,
            MaxScoreMaintenanceService
                .IsObservedScoreCompatible(
                    newMaximum,
                    sourceMapped,
                    highestObservedScore));
        new MaxScoreMaintenanceObservedScoreCheck(
                "song",
                "Solo_Guitar",
                newMaximum,
                validCutoff,
                sourceMapped,
                highestObservedScore,
                expected)
            .ValidateContract();
    }

    [Theory]
    [InlineData(
        ShowThemWhoWeAreSongId,
        "Solo_Guitar",
        63_750,
        63_764,
        66_937)]
    [InlineData(
        RunItSongId,
        "Solo_Guitar",
        51_573,
        52_809,
        54_151)]
    [InlineData(
        RunItSongId,
        "Solo_PeripheralGuitar",
        51_573,
        51_588,
        54_151)]
    [InlineData(
        ShowThemWhoWeAreSongId,
        "Solo_PeripheralGuitar",
        65_367,
        65_228,
        68_635)]
    public void Live_shaped_observed_scores_are_valid(
        string songId,
        string instrument,
        int newMaximum,
        int highestObservedScore,
        int expectedValidCutoff)
    {
        var check =
            new MaxScoreMaintenanceObservedScoreCheck(
                songId,
                instrument,
                newMaximum,
                RankingsCalculator.ComputeMaxScoreThreshold(
                    newMaximum),
                SourceMapped: true,
                highestObservedScore,
                Passed:
                    MaxScoreMaintenanceService
                        .IsObservedScoreCompatible(
                            newMaximum,
                            sourceMapped: true,
                            highestObservedScore));

        Assert.Equal(expectedValidCutoff, check.ValidCutoff);
        Assert.True(check.Passed);
        check.ValidateContract();
    }

    [Fact]
    public void Plan_report_v5_serializes_valid_cutoff_and_rejects_incompatible_json()
    {
        var check =
            new MaxScoreMaintenanceObservedScoreCheck(
                RunItSongId,
                "Solo_Guitar",
                51_573,
                54_151,
                SourceMapped: true,
                HighestObservedScore: 52_809,
                Passed: true);
        var report = CreatePlanReport(check);
        Assert.Equal(
            5,
            MaxScoreMaintenancePlanReport
                .CurrentPlanDigestContractVersion);
        var json = JsonSerializer.Serialize(
            report,
            MaxScoreMaintenanceJson.Report);
        using (var document = JsonDocument.Parse(json))
        {
            Assert.Equal(
                MaxScoreMaintenancePlanReport
                    .CurrentReportVersion,
                document.RootElement
                    .GetProperty("reportVersion")
                    .GetInt32());
            Assert.Equal(
                54_151,
                document.RootElement
                    .GetProperty("observedScoreChecks")[0]
                    .GetProperty("validCutoff")
                    .GetInt32());
        }
        JsonSerializer.Deserialize<
                MaxScoreMaintenancePlanReport>(
                json,
                MaxScoreMaintenanceJson.Strict)!
            .ValidateContract();

        var legacy = JsonNode.Parse(json)!.AsObject();
        legacy["reportVersion"] = 4;
        var legacyReport = JsonSerializer.Deserialize<
            MaxScoreMaintenancePlanReport>(
            legacy.ToJsonString(),
            MaxScoreMaintenanceJson.Strict)!;
        Assert.Throws<ArgumentException>(
            legacyReport.ValidateContract);

        var missingCutoff =
            JsonNode.Parse(json)!.AsObject();
        Assert.True(
            missingCutoff["observedScoreChecks"]!
                .AsArray()[0]!
                .AsObject()
                .Remove("validCutoff"));
        var missingCutoffReport =
            JsonSerializer.Deserialize<
                MaxScoreMaintenancePlanReport>(
                missingCutoff.ToJsonString(),
                MaxScoreMaintenanceJson.Strict)!;
        Assert.Throws<ArgumentException>(
            missingCutoffReport.ValidateContract);

        var unknownProperty =
            JsonNode.Parse(json)!.AsObject();
        unknownProperty["unexpected"] = true;
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<
                MaxScoreMaintenancePlanReport>(
                unknownProperty.ToJsonString(),
                MaxScoreMaintenanceJson.Strict));
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

    private static MaxScoreMaintenanceStageRequest
        WithPromotionMaximum(
            MaxScoreMaintenanceStageRequest request,
            string instrument,
            int maximum)
    {
        var instrumentChanges =
            request.ExpectedChangedInstruments.Contains(
                instrument,
                StringComparer.Ordinal);
        return request with
        {
            Songs = request.Songs
                .Select(song => song with
                {
                    ExpectedOldMaxima = instrumentChanges
                        ? song.ExpectedOldMaxima
                        : WithMaximum(
                            song.ExpectedOldMaxima!,
                            instrument,
                            maximum),
                    ExpectedNewMaxima = WithMaximum(
                        song.ExpectedNewMaxima!,
                        instrument,
                        maximum),
                })
                .ToArray(),
        };
    }

    private static MaxScoreMaintenanceMaxima WithMaximum(
        MaxScoreMaintenanceMaxima maxima,
        string instrument,
        int maximum)
        => instrument switch
        {
            "Solo_Guitar" => maxima with { Lead = maximum },
            "Solo_Bass" => maxima with { Bass = maximum },
            "Solo_Drums" => maxima with { Drums = maximum },
            "Solo_Vocals" => maxima with { Vocals = maximum },
            "Solo_PeripheralGuitar" =>
                maxima with { ProLead = maximum },
            "Solo_PeripheralBass" =>
                maxima with { ProBass = maximum },
            "Solo_PeripheralCymbals" =>
                maxima with { ProCymbals = maximum },
            "Solo_PeripheralDrums" =>
                maxima with { ProDrums = maximum },
            _ => throw new ArgumentOutOfRangeException(
                nameof(instrument),
                instrument,
                "Unsupported maximum instrument."),
        };

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

    private static MaxScoreMaintenancePlanReport CreatePlanReport(
        params MaxScoreMaintenanceObservedScoreCheck[] checks)
        => new(
            MaxScoreMaintenancePlanReport
                .CurrentReportVersion,
            CanApply: true,
            ManifestSha256: new string('a', 64),
            PlanDigest: new string('b', 64),
            ExpectedPublishedScrapeId: 1296,
            ExpectedPublicationId: 500,
            CatalogContentHash: new string('c', 64),
            PublishedScoreSourceFingerprint:
                new string('d', 64),
            NotificationStateFingerprint:
                new string('e', 64),
            RankHistoryFingerprint:
                new string('f', 64),
            ScoreHistoryFingerprint:
                new string('1', 64),
            PopulationEvidence:
                new MaxScoreMaintenancePopulationEvidence(
                    ScopeCount: 1,
                    MinimumTotalEntries: 1,
                    MaximumTotalEntries: 1,
                    Fingerprint: new string('2', 64)),
            ScoreHistoryEvidence:
                new MaxScoreMaintenanceScoreHistoryEvidence(
                    RowCount: 0,
                    MinimumId: null,
                    MaximumId: null,
                    MinimumChangedAtUtc: null,
                    MaximumChangedAtUtc: null,
                    Fingerprint: new string('1', 64)),
            AffectedInstruments: ["Solo_Guitar"],
            RoutineCandidateCount: 0,
            Checks:
            [
                new MaxScoreMaintenancePlanCheck(
                    "observed-scores",
                    Passed: true,
                    "valid"),
            ],
            RoutineCandidates: [],
            ArtifactEvidence: [],
            ObservedScoreChecks: checks);

    private static MaxScoreMaintenanceApplyReport
        CreateApplyReport(
            MaxScoreMaintenancePhase phase,
            bool succeeded,
            bool resumable,
            bool frozen,
            bool includeCacheEvidence)
    {
        var cacheEvidence = includeCacheEvidence
            ? new MaxScoreMaintenanceCacheEvidence(
                EntryCount: 7,
                ContentFingerprint: new string('1', 64),
                PublishedScopeCacheKeyCount: 4,
                PublishedScopeCacheKeyFingerprint:
                    new string('2', 64),
                TargetScopeCount: 2,
                TargetScopeFingerprint:
                    new string('3', 64),
                AffectedAccountCount: 3,
                AffectedAccountFingerprint:
                    new string('4', 64),
                OverlayOnlyAccountCount: 1,
                OverlayOnlyAccountFingerprint:
                    new string('e', 64))
            : null;
        return new MaxScoreMaintenanceApplyReport(
            MaxScoreMaintenanceApplyReport
                .CurrentReportVersion,
            succeeded,
            resumable,
            frozen,
            ManifestSha256: new string('a', 64),
            PlanDigest: new string('b', 64),
            Phase: phase,
            ExpectedPublishedScrapeId: 1296,
            ExpectedPublicationId: 500,
            RollbackSnapshotPath:
                "maintenance/rollback.json",
            RollbackSnapshotSha256:
                new string('c', 64),
            PromotedSongCount: 2,
            RebuiltInstrumentCount: 4,
            QuarantinedCandidateCount: 3,
            VisibleDeliveryCount: 0,
            StagedCacheEntryCount:
                includeCacheEvidence ? 7 : 0,
            CacheEvidence: cacheEvidence,
            FailureStage:
                succeeded ? null : "injected",
            Detail:
                succeeded ? null : "injected failure");
    }

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
