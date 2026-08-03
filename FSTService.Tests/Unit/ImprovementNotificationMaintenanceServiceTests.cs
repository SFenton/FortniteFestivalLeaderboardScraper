using System.Net.WebSockets;
using System.Text.Json;
using FSTService.Api;
using FSTService.Persistence;
using FSTService.Scraping;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FSTService.Tests.Unit;

public sealed class ImprovementNotificationMaintenanceServiceTests : IDisposable
{
    private const string AccountId = "maintenance-account";
    private const string OtherAccountId = "maintenance-other-account";
    private const string BlockedAccountId = "maintenance-blocked-account";
    private const string RoutineAccountId = "maintenance-routine-account";
    private const string SongId = "maintenance-song";
    private const string ProLeadInstrument = "Solo_PeripheralGuitar";
    private const string LeadInstrument = "Solo_Guitar";
    private const string BandType = "Band_Duets";
    private const string TeamKey = "maintenance-account:maintenance-bandmate";
    private static readonly string[] RepairSongIds =
        ImprovementNotificationMaintenanceManifest.RequiredSongIds.ToArray();

    private readonly InMemoryMetaDatabase _fixture = new();
    private readonly ImprovementNotificationService _notifications;
    private readonly ImprovementNotificationMaintenanceService _maintenance;

    public ImprovementNotificationMaintenanceServiceTests()
    {
        _notifications = new ImprovementNotificationService(
            _fixture.DataSource,
            NullLogger<ImprovementNotificationService>.Instance);
        _maintenance = new ImprovementNotificationMaintenanceService(
            _fixture.DataSource,
            NullLogger<ImprovementNotificationMaintenanceService>.Instance);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void ManifestRequiresExactlyFourSortedUniqueSongs()
    {
        var valid = CreateRepairManifest();
        Assert.Equal(4, valid.ValidateAndNormalize().Songs.Count);

        var tooShort = valid with { Songs = valid.Songs.Take(3).ToArray() };
        Assert.Throws<ArgumentException>(() => tooShort.ValidateAndNormalize());

        var unsorted = valid with { Songs = valid.Songs.Reverse().ToArray() };
        Assert.Throws<ArgumentException>(() => unsorted.ValidateAndNormalize());

        var duplicate = valid with
        {
            Songs =
            [
                valid.Songs[0],
                valid.Songs[0],
                valid.Songs[2],
                valid.Songs[3],
            ],
        };
        Assert.Throws<ArgumentException>(() => duplicate.ValidateAndNormalize());

        var wrongAllowlist = valid with
        {
            Songs =
            [
                valid.Songs[0] with
                {
                    SongId = "00000000-0000-0000-0000-000000000000",
                },
                .. valid.Songs.Skip(1),
            ],
        };
        Assert.Throws<ArgumentException>(
            () => wrongAllowlist.ValidateAndNormalize());

        var missingRuntime = valid with
        {
            Songs =
            [
                valid.Songs[0] with
                {
                    StagedChoptVersion = null,
                    StagedChoptBinarySha256 = null,
                    StagedGenerationProfile = null,
                },
                .. valid.Songs.Skip(1),
            ],
        };
        Assert.Throws<ArgumentException>(
            () => missingRuntime.ValidateAndNormalize());
    }

    [Fact]
    public void ProjectedFallbackCutoffMatchesProductionTruncation()
    {
        const int maximum = 1030;
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT FLOOR(@maximum * @threshold)::INTEGER";
        cmd.Parameters.AddWithValue("maximum", maximum);
        cmd.Parameters.AddWithValue("threshold", 1.05d);

        Assert.Equal(
            RankingsCalculator.ComputeMaxScoreThreshold(maximum),
            Convert.ToInt32(cmd.ExecuteScalar()));
        Assert.Equal(1081, RankingsCalculator.ComputeMaxScoreThreshold(maximum));
    }

    [Fact]
    public void ProductionRankingExcludesRoundedBoundaryFallback()
    {
        var original = CreateRepairManifest();
        var manifest = original with
        {
            Songs =
            [
                original.Songs[0] with
                {
                    ProposedProLeadMaxScore = 1030,
                },
                .. original.Songs.Skip(1),
            ],
        };
        SeedRepairProjection(manifest);
        CreatePublishedScrape();
        using (var conn = _fixture.DataSource.OpenConnection())
        using (var history = conn.CreateCommand())
        {
            history.CommandText = """
                INSERT INTO score_history (
                    song_id, instrument, account_id,
                    new_score, new_rank, accuracy, is_full_combo, stars,
                    changed_at)
                VALUES
                    (@songId, @instrument, @accountId, 1081, 1, 100, FALSE, 5, now()),
                    (@songId, @instrument, @accountId, 1082, 1, 100, FALSE, 5, now());
                """;
            history.Parameters.AddWithValue(
                "songId",
                manifest.Songs[0].SongId);
            history.Parameters.AddWithValue(
                "instrument",
                ProLeadInstrument);
            history.Parameters.AddWithValue("accountId", AccountId);
            history.ExecuteNonQuery();
        }

        ApplyProposedMaximaAndComputeProductionRankings(manifest);

        using var verify = _fixture.DataSource.OpenConnection();
        using var query = verify.CreateCommand();
        query.CommandText = """
            SELECT total_score
            FROM account_rankings
            WHERE account_id = @accountId
              AND instrument = @instrument
            """;
        query.Parameters.AddWithValue("accountId", AccountId);
        query.Parameters.AddWithValue("instrument", ProLeadInstrument);
        Assert.Equal(1681L, Convert.ToInt64(query.ExecuteScalar()));
    }

    [Fact]
    public async Task DryRunProjectsRankMovementWithoutChangingLiveRankingsAndIsDeterministic()
    {
        var manifest = CreateRepairManifest();
        SeedRepairProjection(manifest);
        var scrapeId = CreatePublishedScrape();
        PrepareReadyNotificationInputs(scrapeId);
        var before = ReadWriteSnapshot(AccountId, ProLeadInstrument);
        var beforeRanks = ReadCurrentMaxScoreRanks();

        var first = await _maintenance.DryRunProLeadMaxScoreRepairAsync(
            scrapeId,
            manifest);
        var second = await _maintenance.DryRunProLeadMaxScoreRepairAsync(
            scrapeId,
            manifest);
        var after = ReadWriteSnapshot(AccountId, ProLeadInstrument);

        Assert.Equal(
            ImprovementNotificationSafetyContract.ProLeadMaxScoreRepairPurpose,
            first.Purpose);
        Assert.Equal(0, first.VisibleDeliveryCap);
        Assert.Equal(0, first.VisibleDeliveryCount);
        Assert.Equal("quarantine_only_zero_visible_delivery", first.CapDecision);
        Assert.Equal(4, first.TotalChartedSongs);
        Assert.Equal(manifest.ManifestVersion, first.Manifest.ManifestVersion);
        Assert.Equal(manifest.Songs.ToArray(), first.Manifest.Songs.ToArray());
        Assert.Equal(2, first.CandidateCount);
        Assert.Equal(2, first.AllowedCandidateCount);
        Assert.Equal(0, first.ExternalRoutineCandidateCount);
        Assert.Equal(0, first.RejectedCandidateCount);
        Assert.Equal(1, first.MaxCandidatesForAnySubject);
        Assert.Equal(first.DryRunDigest, second.DryRunDigest);
        Assert.Equal(first.Candidates, second.Candidates);
        Assert.Equal(64, first.DryRunDigest.Length);
        Assert.All(first.Candidates, candidate =>
        {
            Assert.Equal(
                "pro_lead_denominator_rank_movement",
                candidate.Classification);
            Assert.True(candidate.Allowed);
            Assert.Equal("max_score_percent_rank", candidate.Metric);
        });
        Assert.Contains(
            first.Candidates,
            candidate =>
                candidate.SubjectKey == AccountId
                && candidate.OldRank == 1
                && candidate.NewRank == 2);
        Assert.Contains(
            first.Candidates,
            candidate =>
                candidate.SubjectKey == OtherAccountId
                && candidate.OldRank == 2
                && candidate.NewRank == 1);
        Assert.Equal(before, after);
        Assert.Equal(beforeRanks, ReadCurrentMaxScoreRanks());
    }

    [Fact]
    public async Task ProjectedRanksMatchProductionRankingBuilder()
    {
        var manifest = CreateRepairManifest();
        SeedRepairProjection(manifest);
        var scrapeId = CreatePublishedScrape();
        PrepareReadyNotificationInputs(scrapeId);

        var dryRun = await _maintenance.DryRunProLeadMaxScoreRepairAsync(
            scrapeId,
            manifest);
        ApplyProposedMaximaAndComputeProductionRankings(manifest);
        var actualRanks = ReadCurrentMaxScoreRanks();

        Assert.Equal(
            dryRun.Candidates
                .Where(candidate =>
                    candidate.Metric == "max_score_percent_rank")
                .OrderBy(candidate => candidate.SubjectKey)
                .Select(candidate => (
                    candidate.SubjectKey,
                    candidate.NewRank!.Value))
                .ToArray(),
            new[]
            {
                (SubjectKey: AccountId, Rank: actualRanks.AccountRank),
                (SubjectKey: OtherAccountId, Rank: actualRanks.OtherRank),
            }
            .OrderBy(pair => pair.SubjectKey)
            .ToArray());
    }

    [Fact]
    public async Task DryRunDigestBindsPublishedScrapeId()
    {
        var manifest = CreateRepairManifest();
        SeedRepairProjection(manifest);
        var firstScrapeId = CreatePublishedScrape();
        PrepareReadyNotificationInputs(firstScrapeId);
        var first = await _maintenance.DryRunProLeadMaxScoreRepairAsync(
            firstScrapeId,
            manifest);

        var secondScrapeId = CreatePublishedScrape();
        PrepareReadyNotificationInputs(secondScrapeId);
        var second = await _maintenance.DryRunProLeadMaxScoreRepairAsync(
            secondScrapeId,
            manifest);

        Assert.NotEqual(firstScrapeId, secondScrapeId);
        Assert.Equal(first.Candidates, second.Candidates);
        Assert.NotEqual(first.DryRunDigest, second.DryRunDigest);
    }

    [Fact]
    public async Task Execute_QuarantinesWithoutSupersedingExpiringOrBroadcasting()
    {
        var manifest = CreateRepairManifest();
        SeedRepairProjection(manifest);
        var scrapeId = CreatePublishedScrape();
        PrepareReadyNotificationInputs(scrapeId);
        var visibleEventId = InsertPlayerEvent(
            AccountId,
            ProLeadInstrument,
            deliveryState: null,
            purpose: null,
            expiresAtUtc: DateTime.UtcNow.AddDays(2));
        var visibleExpiryBefore = ReadEventExpiry(visibleEventId);

        var notificationService = new NotificationService(
            Substitute.For<ILogger<NotificationService>>());
        var socket = Substitute.For<WebSocket>();
        socket.State.Returns(WebSocketState.Open);
        notificationService.AddConnection(AccountId, "maintenance-device", socket);

        var dryRun = await _maintenance.DryRunProLeadMaxScoreRepairAsync(
            scrapeId,
            manifest);
        PromoteRepairManifest(manifest);
        ApplyProjectedAccountRankings();
        var execute = await _maintenance.ExecuteProLeadMaxScoreRepairAsync(
            scrapeId,
            dryRun.DryRunDigest,
            manifest);

        Assert.Equal(0, execute.VisibleDeliveryCap);
        Assert.Equal(0, execute.VisibleDeliveryCount);
        Assert.Equal(4, execute.TotalChartedSongs);
        Assert.Equal(2, execute.QuarantinedCandidateCount);
        Assert.Equal(0, execute.ExternalRoutineCandidateCount);
        Assert.Equal(2, execute.SelectivePlayerRankStateRowsUpdated);
        Assert.False(execute.BroadcastRequested);
        Assert.Equal(1, CountRows("improvement_notification_maintenance_runs"));
        Assert.Equal(2, CountRows("improvement_notification_maintenance_candidates"));
        Assert.Equal(1, CountRows("player_improvement_events"));
        AssertMaintenanceAuditBindsScrapeAndManifest(
            scrapeId,
            manifest,
            dryRun.DryRunDigest);
        Assert.Equal(visibleExpiryBefore, ReadEventExpiry(visibleEventId));
        Assert.Equal(
            2,
            ReadRankState(AccountId, ProLeadInstrument).MaxScorePercentRank);
        Assert.Equal(
            1,
            ReadRankState(OtherAccountId, ProLeadInstrument).MaxScorePercentRank);
        Assert.Single(
            _notifications.GetPlayerNotifications(
                AccountId,
                includeExpired: true).Items);
        await socket.DidNotReceive().SendAsync(
            Arg.Any<ArraySegment<byte>>(),
            Arg.Any<WebSocketMessageType>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());

        _notifications.Precompute(new ImprovementNotificationPrecomputeOptions(
            Execute: true,
            BaselineOnly: false,
            Scope: "all",
            IncludePlayers: true,
            IncludeBands: false,
            IncludeSongEvents: false,
            IncludeRankings: false,
            PruneExpired: true,
            DetectedAtUtc: DateTime.UtcNow.AddYears(5)));

        Assert.Equal(2, CountRows("improvement_notification_maintenance_candidates"));
    }

    [Fact]
    public void PublicFeedsReturnOnlyVisibleRowsAndLegacyDefaultsAreVisible()
    {
        var visiblePlayerEvent = InsertPlayerEvent(
            AccountId,
            LeadInstrument,
            deliveryState: null,
            purpose: null,
            expiresAtUtc: DateTime.UtcNow.AddDays(1));
        InsertPlayerEvent(
            AccountId,
            LeadInstrument,
            ImprovementNotificationSafetyContract.QuarantinedDeliveryState,
            ImprovementNotificationSafetyContract.ProLeadMaxScoreRepairPurpose,
            DateTime.UtcNow.AddDays(1));
        var bandSubjectId = InsertBandSubject();
        var visibleBandEvent = InsertBandEvent(
            bandSubjectId,
            deliveryState: null,
            purpose: null);
        InsertBandEvent(
            bandSubjectId,
            ImprovementNotificationSafetyContract.QuarantinedDeliveryState,
            ImprovementNotificationSafetyContract.ProLeadMaxScoreRepairPurpose);
        var visibleServiceEvent = InsertServiceEvent(
            "visible-service-song",
            deliveryState: null,
            purpose: null);
        InsertServiceEvent(
            "hidden-service-song",
            ImprovementNotificationSafetyContract.QuarantinedDeliveryState,
            ImprovementNotificationSafetyContract.ProLeadMaxScoreRepairPurpose);
        var visibleRunId = InsertDetectionRun(
            deliveryState: null,
            purpose: null);
        InsertDetectionRun(
            ImprovementNotificationSafetyContract.QuarantinedDeliveryState,
            ImprovementNotificationSafetyContract.ProLeadMaxScoreRepairPurpose);

        var player = _notifications.GetPlayerNotifications(
            AccountId,
            includeExpired: true);
        var band = _notifications.GetBandNotificationsByTeamKey(
            BandType,
            TeamKey,
            includeExpired: true);

        Assert.Equal(2, player.Items.Count);
        Assert.Equal(visibleRunId, player.SourceRunId);
        Assert.Contains(player.Items, item => item.EventId == visiblePlayerEvent);
        Assert.Contains(player.Items, item => item.EventId == visibleServiceEvent);
        Assert.Equal(2, band.Items.Count);
        Assert.Equal(visibleRunId, band.SourceRunId);
        Assert.Contains(band.Items, item => item.EventId == visibleBandEvent);
        Assert.Contains(band.Items, item => item.EventId == visibleServiceEvent);
        Assert.DoesNotContain(player.Items, item => item.SongId == "hidden-service-song");
        Assert.DoesNotContain(band.Items, item => item.SongId == "hidden-service-song");

        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT notification_purpose, notification_cause, delivery_state
            FROM player_improvement_events
            WHERE event_id = @eventId;
            """;
        cmd.Parameters.AddWithValue("eventId", visiblePlayerEvent);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(
            ImprovementNotificationSafetyContract.RoutineScoreObservationPurpose,
            reader.GetString(0));
        Assert.Equal(
            ImprovementNotificationSafetyContract.RoutineScoreObservationCause,
            reader.GetString(1));
        Assert.Equal(
            ImprovementNotificationSafetyContract.VisibleDeliveryState,
            reader.GetString(2));
    }

    [Fact]
    public async Task ExecuteRejectsScrapeAndDigestMismatchBeforeWrites()
    {
        var manifest = CreateRepairManifest();
        SeedRepairProjection(manifest);
        var scrapeId = CreatePublishedScrape();
        PrepareReadyNotificationInputs(scrapeId);
        var dryRun = await _maintenance.DryRunProLeadMaxScoreRepairAsync(
            scrapeId,
            manifest);

        var scrapeException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _maintenance.ExecuteProLeadMaxScoreRepairAsync(
                scrapeId + 1,
                dryRun.DryRunDigest,
                manifest));
        Assert.Contains("Published scrape changed", scrapeException.Message);

        PromoteRepairManifest(manifest);
        ApplyProjectedAccountRankings();
        var digestException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _maintenance.ExecuteProLeadMaxScoreRepairAsync(
                scrapeId,
                new string('0', 64),
                manifest));
        Assert.Contains("digest changed", digestException.Message);

        Assert.Equal(0, CountRows("improvement_notification_maintenance_runs"));
        Assert.Equal(0, CountRows("improvement_notification_maintenance_candidates"));
        Assert.Equal(
            1,
            ReadRankState(AccountId, ProLeadInstrument).MaxScorePercentRank);
    }

    [Fact]
    public async Task DryRunRejectsManifestDatabaseIdentityMismatch()
    {
        var manifest = CreateRepairManifest();
        SeedRepairProjection(manifest);
        var scrapeId = CreatePublishedScrape();
        PrepareReadyNotificationInputs(scrapeId);
        var mismatched = manifest with
        {
            Songs = manifest.Songs
                .Select((song, index) => index == 0
                    ? song with
                    {
                        ExpectedCurrentPathRevision =
                            song.ExpectedCurrentPathRevision + 1,
                    }
                    : song)
                .ToArray(),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _maintenance.DryRunProLeadMaxScoreRepairAsync(
                scrapeId,
                mismatched));

        Assert.Contains("database identity mismatch", exception.Message);
        Assert.Equal(0, CountRows("improvement_notification_maintenance_runs"));
    }

    [Fact]
    public async Task ExecuteRejectsActualVsProjectedMismatchBeforeWrites()
    {
        var manifest = CreateRepairManifest();
        SeedRepairProjection(manifest);
        var scrapeId = CreatePublishedScrape();
        PrepareReadyNotificationInputs(scrapeId);
        var dryRun = await _maintenance.DryRunProLeadMaxScoreRepairAsync(
            scrapeId,
            manifest);
        PromoteRepairManifest(manifest);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _maintenance.ExecuteProLeadMaxScoreRepairAsync(
                scrapeId,
                dryRun.DryRunDigest,
                manifest));

        Assert.Contains("does not exactly match", exception.Message);
        Assert.Equal(0, CountRows("improvement_notification_maintenance_runs"));
        Assert.Equal(0, CountRows("improvement_notification_maintenance_candidates"));
        Assert.Equal(
            1,
            ReadRankState(AccountId, ProLeadInstrument).MaxScorePercentRank);
        Assert.Equal(
            2,
            ReadRankState(OtherAccountId, ProLeadInstrument).MaxScorePercentRank);
    }

    [Fact]
    public async Task DryRunFailsClosedWhenRoutineInputsAreMissing()
    {
        var manifest = CreateRepairManifest();
        SeedRepairProjection(manifest);
        var scrapeId = CreatePublishedScrape();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _maintenance.DryRunProLeadMaxScoreRepairAsync(
                scrapeId,
                manifest));

        Assert.Contains("notification marker", exception.Message);
        Assert.Equal(0, CountRows("improvement_notification_maintenance_runs"));
        Assert.Equal(0, CountRows("improvement_notification_maintenance_candidates"));
    }

    [Fact]
    public async Task DryRunSeparatesRoutineCandidatesAndRejectsUnattributedOtherInstrumentMovement()
    {
        var manifest = CreateRepairManifest();
        SeedRepairProjection(manifest);
        InsertAccountRanking(
            RoutineAccountId,
            LeadInstrument,
            maxScorePercentRank: 200);
        InsertAccountRanking(
            BlockedAccountId,
            "Solo_Bass",
            maxScorePercentRank: 300,
            adjustedSkillRank: 1002,
            weightedRank: 1002,
            totalScoreRank: 1002,
            fcRateRank: 1002,
            totalScore: 80_000);
        InsertCurrentEntry(
            RoutineAccountId,
            SongId,
            LeadInstrument,
            score: 100_000,
            rank: 100);
        InsertCurrentBandEntry(score: 200_000, rank: 100);
        var scrapeId = CreatePublishedScrape();
        PrepareReadyNotificationInputs(scrapeId);

        UpdateAccountRanking(
            RoutineAccountId,
            LeadInstrument,
            maxScorePercentRank: 190);
        UpdateAccountRanking(
            BlockedAccountId,
            "Solo_Bass",
            maxScorePercentRank: 290);
        UpdateCurrentEntry(
            RoutineAccountId,
            SongId,
            LeadInstrument,
            score: 101_000,
            rank: 90);
        UpdateCurrentBandEntry(score: 201_000, rank: 90);

        var report = await _maintenance.DryRunProLeadMaxScoreRepairAsync(
            scrapeId,
            manifest);

        Assert.Equal(2, report.AllowedCandidateCount);
        Assert.True(report.ExternalRoutineCandidateCount >= 2);
        Assert.True(report.RejectedCandidateCount >= 1);
        Assert.Equal("blocked_disallowed_candidates", report.CapDecision);
        Assert.Contains(
            report.Candidates,
            candidate =>
                candidate.Classification == "pro_lead_denominator_rank_movement"
                && candidate.Allowed);
        Assert.Contains(
            report.Candidates,
            candidate =>
                candidate.Classification == "other_instrument_not_allowed");
        Assert.Contains(
            report.Candidates,
            candidate =>
                candidate.Classification
                    == "ordinary_score_observation_outside_maintenance"
                && !candidate.BlocksMaintenance);
        Assert.Contains(
            report.Candidates,
            candidate =>
                candidate.SubjectType == "band"
                && candidate.Classification
                    == "ordinary_score_observation_outside_maintenance"
                && !candidate.BlocksMaintenance);

        PromoteRepairManifest(manifest);
        ApplyProjectedAccountRankings();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _maintenance.ExecuteProLeadMaxScoreRepairAsync(
                scrapeId,
                report.DryRunDigest,
                manifest));
        Assert.Equal(0, CountRows("improvement_notification_maintenance_runs"));
    }

    [Fact]
    public async Task RegisteredScopeIgnoresHistoricalUnregisteredRankState()
    {
        var manifest = CreateRepairManifest();
        SeedRepairProjection(manifest);
        _fixture.Db.RegisterUser("maintenance-device", AccountId);
        _fixture.Db.RegisterUser("maintenance-other-device", OtherAccountId);
        InsertAccountRanking(
            BlockedAccountId,
            "Solo_Bass",
            maxScorePercentRank: 300);
        var scrapeId = CreatePublishedScrape();
        PrepareReadyNotificationInputs(scrapeId);
        UpdateAccountRanking(
            BlockedAccountId,
            "Solo_Bass",
            maxScorePercentRank: 290);
        var registeredMaintenance =
            new ImprovementNotificationMaintenanceService(
                _fixture.DataSource,
                NullLogger<ImprovementNotificationMaintenanceService>.Instance,
                Options.Create(new ImprovementNotificationOptions
                {
                    Scope = "registered",
                }));

        var report =
            await registeredMaintenance.DryRunProLeadMaxScoreRepairAsync(
                scrapeId,
                manifest);

        Assert.Equal(0, report.RejectedCandidateCount);
        Assert.DoesNotContain(
            report.Candidates,
            candidate => candidate.SubjectKey == BlockedAccountId);
    }

    [Fact]
    public async Task ExecuteLeavesIndependentRoutineScoreObservationsForRoutineDelivery()
    {
        var manifest = CreateRepairManifest();
        SeedRepairProjection(manifest);
        InsertCurrentEntry(
            RoutineAccountId,
            SongId,
            LeadInstrument,
            score: 100_000,
            rank: 100);
        InsertCurrentBandEntry(score: 200_000, rank: 100);
        var scrapeId = CreatePublishedScrape();
        PrepareReadyNotificationInputs(scrapeId);

        UpdateCurrentEntry(
            RoutineAccountId,
            SongId,
            LeadInstrument,
            score: 101_000,
            rank: 90);
        UpdateCurrentBandEntry(score: 201_000, rank: 90);

        var dryRun = await _maintenance.DryRunProLeadMaxScoreRepairAsync(
            scrapeId,
            manifest);
        Assert.Equal(2, dryRun.AllowedCandidateCount);
        Assert.True(dryRun.ExternalRoutineCandidateCount >= 2);
        Assert.Equal(0, dryRun.RejectedCandidateCount);

        PromoteRepairManifest(manifest);
        ApplyProjectedAccountRankings();
        var execute = await _maintenance.ExecuteProLeadMaxScoreRepairAsync(
            scrapeId,
            dryRun.DryRunDigest,
            manifest);

        Assert.Equal(2, execute.QuarantinedCandidateCount);
        Assert.Equal(dryRun.ExternalRoutineCandidateCount, execute.ExternalRoutineCandidateCount);
        Assert.Equal(100_000, ReadPlayerSongStateScore(RoutineAccountId));
        Assert.Equal(200_000, ReadBandSongStateScore());
        Assert.Empty(
            _notifications.GetPlayerNotifications(
                RoutineAccountId,
                includeExpired: true).Items);
        Assert.Empty(
            _notifications.GetBandNotificationsByTeamKey(
                BandType,
                TeamKey,
                includeExpired: true).Items);

        var routine = _notifications.Precompute(
            new ImprovementNotificationPrecomputeOptions(
                Execute: true,
                BaselineOnly: false,
                Scope: "all",
                IncludePlayers: true,
                IncludeBands: true,
                IncludeSongEvents: true,
                IncludeRankings: false,
                PruneExpired: false));

        Assert.Equal(1, routine.PlayerSongEventsInserted);
        Assert.Equal(1, routine.BandSongEventsInserted);
        Assert.Single(
            _notifications.GetPlayerNotifications(
                RoutineAccountId,
                includeExpired: true).Items);
        Assert.Single(
            _notifications.GetBandNotificationsByTeamKey(
                BandType,
                TeamKey,
                includeExpired: true).Items);
    }

    [Fact]
    public void RoutinePrecomputeRemainsVisibleAndNeverSupersedesQuarantine()
    {
        InsertCurrentEntry(
            AccountId,
            SongId,
            LeadInstrument,
            score: 100_000,
            rank: 100);
        _notifications.Precompute(new ImprovementNotificationPrecomputeOptions(
            Execute: true,
            BaselineOnly: true,
            Scope: "all",
            IncludePlayers: true,
            IncludeBands: false,
            IncludeSongEvents: true,
            IncludeRankings: false,
            PruneExpired: false));
        var quarantinedEventId = InsertPlayerEvent(
            AccountId,
            LeadInstrument,
            ImprovementNotificationSafetyContract.QuarantinedDeliveryState,
            ImprovementNotificationSafetyContract.ProLeadMaxScoreRepairPurpose,
            DateTime.UtcNow.AddDays(30),
            SongId);
        var quarantineExpiry = ReadEventExpiry(quarantinedEventId);

        UpdateCurrentEntry(
            AccountId,
            SongId,
            LeadInstrument,
            score: 101_000,
            rank: 90);
        var report = _notifications.Precompute(
            new ImprovementNotificationPrecomputeOptions(
                Execute: true,
                BaselineOnly: false,
                Scope: "all",
                IncludePlayers: true,
                IncludeBands: false,
                IncludeSongEvents: true,
                IncludeRankings: false,
                PruneExpired: false));

        Assert.Equal(1, report.PlayerSongEventsInserted);
        var visible = Assert.Single(
            _notifications.GetPlayerNotifications(
                AccountId,
                includeExpired: true).Items);
        Assert.Equal("player_score_pb", visible.EventKind);
        Assert.Equal(quarantineExpiry, ReadEventExpiry(quarantinedEventId));

        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT notification_purpose, notification_cause, delivery_state
            FROM player_improvement_events
            WHERE event_id = @eventId;
            """;
        cmd.Parameters.AddWithValue("eventId", visible.EventId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(
            ImprovementNotificationSafetyContract.RoutineScoreObservationPurpose,
            reader.GetString(0));
        Assert.Equal(
            ImprovementNotificationSafetyContract.RoutineScoreObservationCause,
            reader.GetString(1));
        Assert.Equal(
            ImprovementNotificationSafetyContract.VisibleDeliveryState,
            reader.GetString(2));
    }

    private long CreatePublishedScrape()
    {
        var scrapeId = _fixture.Db.StartScrapeRun();
        _fixture.Db.CompleteScrapeRun(scrapeId, 1, 10, 1, 100);
        _fixture.Db.PublishScrapeRun(
            scrapeId,
            promoteCachedResponses: false,
            queueImprovementNotifications: true,
            improvementNotificationProjectionScopes: []);
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO leaderboard_entries_snapshot (
                snapshot_id, song_id, instrument, account_id, score,
                accuracy, is_full_combo, stars, season, percentile,
                rank, source, difficulty, api_rank,
                first_seen_at, last_updated_at)
            SELECT @scrapeId,
                   current.song_id,
                   current.instrument,
                   current.account_id,
                   current.score,
                   current.accuracy,
                   current.is_full_combo,
                   current.stars,
                   current.season,
                   current.percentile,
                   current.rank,
                   current.source,
                   current.difficulty,
                   current.api_rank,
                   current.first_seen_at,
                   current.last_updated_at
            FROM current_leaderboard_entries current
            WHERE current.instrument = @instrument
            ON CONFLICT (snapshot_id, song_id, instrument, account_id)
            DO UPDATE SET
                score = EXCLUDED.score,
                accuracy = EXCLUDED.accuracy,
                is_full_combo = EXCLUDED.is_full_combo,
                stars = EXCLUDED.stars,
                season = EXCLUDED.season,
                percentile = EXCLUDED.percentile,
                rank = EXCLUDED.rank,
                api_rank = EXCLUDED.api_rank,
                last_updated_at = EXCLUDED.last_updated_at;

            INSERT INTO leaderboard_published_scope_source (
                published_scrape_id, song_id, instrument, scope_kind,
                source_kind, source_snapshot_id, source_scrape_id,
                row_count, content_fingerprint, coverage_fingerprint,
                reported_total_entries, reported_total_pages,
                is_complete, created_at, validated_at)
            SELECT @scrapeId,
                   snapshot.song_id,
                   snapshot.instrument,
                   'alltime',
                   'snapshot',
                   @scrapeId,
                   @scrapeId,
                   COUNT(*)::BIGINT,
                   md5(snapshot.song_id || snapshot.instrument),
                   md5(snapshot.instrument || snapshot.song_id),
                   COUNT(*)::BIGINT,
                   1,
                   true,
                   now(),
                   now()
            FROM leaderboard_entries_snapshot snapshot
            WHERE snapshot.snapshot_id = @scrapeId
              AND snapshot.instrument = @instrument
            GROUP BY snapshot.song_id, snapshot.instrument
            ON CONFLICT (published_scrape_id, instrument, song_id, scope_kind)
            DO UPDATE SET
                source_kind = EXCLUDED.source_kind,
                source_snapshot_id = EXCLUDED.source_snapshot_id,
                source_scrape_id = EXCLUDED.source_scrape_id,
                row_count = EXCLUDED.row_count,
                reported_total_entries = EXCLUDED.reported_total_entries,
                reported_total_pages = EXCLUDED.reported_total_pages,
                is_complete = EXCLUDED.is_complete,
                validated_at = EXCLUDED.validated_at;
            """;
        cmd.Parameters.AddWithValue("scrapeId", scrapeId);
        cmd.Parameters.AddWithValue("instrument", ProLeadInstrument);
        cmd.ExecuteNonQuery();
        return scrapeId;
    }

    private void PrepareReadyNotificationInputs(long scrapeId)
    {
        _notifications.Precompute(new ImprovementNotificationPrecomputeOptions(
            Execute: true,
            BaselineOnly: true,
            Scope: "all",
            IncludePlayers: true,
            IncludeBands: true,
            IncludeSongEvents: true,
            IncludeRankings: true,
            PruneExpired: false,
            PublishedScrapeId: scrapeId));
        _notifications.Precompute(new ImprovementNotificationPrecomputeOptions(
            Execute: true,
            BaselineOnly: false,
            Scope: "all",
            IncludePlayers: true,
            IncludeBands: true,
            IncludeSongEvents: true,
            IncludeRankings: true,
            PruneExpired: false,
            PublishedScrapeId: scrapeId));
        _notifications.MarkPublicationCompleted(scrapeId);
    }

    private static ImprovementNotificationMaintenanceManifest CreateRepairManifest()
        => new ImprovementNotificationMaintenanceManifest(
            ImprovementNotificationMaintenanceManifest.CurrentManifestVersion,
            RepairSongIds
                .Select((songId, index) =>
                    new ImprovementNotificationMaintenanceSong(
                        SongId: songId,
                        ExpectedCurrentPathRevision: 10 + index,
                        ExpectedCatalogLastModified:
                            $"2026-07-01T00:00:0{index}.0000000Z",
                        CurrentOldProLeadMaxScore: 1_000,
                        ProposedProLeadMaxScore: index == 0 ? 2_000 : 1_000,
                        StagedArtifactGenerationId: $"staged-generation-{index}",
                        StagedDatFileHash:
                            new string((char)('a' + index), 64),
                        StagedChoptVersion: "chopt-test-1",
                        StagedChoptBinarySha256: new string('f', 64),
                        StagedGenerationProfile: "maintenance-test"))
                .ToArray())
            .ValidateAndNormalize();

    private void SeedRepairProjection(
        ImprovementNotificationMaintenanceManifest manifest)
    {
        using (var conn = _fixture.DataSource.OpenConnection())
        {
            foreach (var (song, index) in manifest.Songs.Select(
                         (song, index) => (song, index)))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO songs (
                        song_id, title, artist, last_modified, pro_lead_diff,
                        plastic_guitar_diff, max_pro_lead_score,
                        dat_file_hash, song_last_modified, paths_generated_at,
                        chopt_version, chopt_binary_sha256,
                        path_generation_profile, path_artifact_generation_id,
                        path_expected_instruments, path_generation_revision,
                        path_generation_pending)
                    VALUES (
                        @songId, @songId, 'test', @catalogLastModified, 3,
                        3, @oldMaximum,
                        @currentDatHash, @catalogLastModified, now(),
                        'chopt-old', @oldBinaryHash,
                        'old-profile', @currentGenerationId,
                        @expectedInstruments, @revision,
                        TRUE);

                    INSERT INTO song_stats (
                        song_id, instrument, entry_count, previous_entry_count,
                        log_weight, max_score, computed_at)
                    VALUES (
                        @songId, @instrument, 2, 2,
                        1, @oldMaximum, now());
                    """;
                cmd.Parameters.AddWithValue("songId", song.SongId);
                cmd.Parameters.AddWithValue(
                    "catalogLastModified",
                    song.ExpectedCatalogLastModified);
                cmd.Parameters.AddWithValue(
                    "oldMaximum",
                    song.CurrentOldProLeadMaxScore!.Value);
                cmd.Parameters.AddWithValue(
                    "currentDatHash",
                    new string((char)('0' + index), 64));
                cmd.Parameters.AddWithValue(
                    "oldBinaryHash",
                    new string('e', 64));
                cmd.Parameters.AddWithValue(
                    "currentGenerationId",
                    $"current-generation-{index}");
                cmd.Parameters.AddWithValue(
                    "expectedInstruments",
                    new[] { ProLeadInstrument });
                cmd.Parameters.AddWithValue(
                    "revision",
                    song.ExpectedCurrentPathRevision);
                cmd.Parameters.AddWithValue("instrument", ProLeadInstrument);
                cmd.ExecuteNonQuery();
            }

        }

        var catalogJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            songs = manifest.Songs.Select(song => new
            {
                lastModified = song.ExpectedCatalogLastModified,
                track = new
                {
                    su = song.SongId,
                    @in = new { pg = 3 },
                },
            }),
        });
        using (var conn = _fixture.DataSource.OpenConnection())
        using (var catalog = conn.CreateCommand())
        {
            catalog.CommandText = """
                UPDATE live_song_catalog
                SET catalog_version = nextval('song_catalog_version_seq'),
                    schema_version = 2,
                    catalog_json = CAST(@catalogJson AS JSONB),
                    content_hash = @contentHash,
                    song_count = 4,
                    source_kind = 'provider_exact',
                    is_exact = TRUE,
                    captured_at = now()
                WHERE id = TRUE;
                """;
            catalog.Parameters.AddWithValue("catalogJson", catalogJson);
            catalog.Parameters.AddWithValue("contentHash", new string('b', 64));
            Assert.Equal(1, catalog.ExecuteNonQuery());
        }

        var accountScores = new[] { 2_200, 200, 200, 200 };
        var otherScores = new[] { 500, 300, 300, 300 };
        for (var index = 0; index < RepairSongIds.Length; index++)
        {
            InsertCurrentEntry(
                AccountId,
                RepairSongIds[index],
                ProLeadInstrument,
                accountScores[index],
                index == 0 ? 1 : 2);
            InsertCurrentEntry(
                OtherAccountId,
                RepairSongIds[index],
                ProLeadInstrument,
                otherScores[index],
                index == 0 ? 2 : 1);
        }

        using (var conn = _fixture.DataSource.OpenConnection())
        using (var fallback = conn.CreateCommand())
        {
            fallback.CommandText = """
                INSERT INTO score_history (
                    song_id, instrument, account_id,
                    new_score, new_rank, accuracy, is_full_combo, stars,
                    changed_at)
                VALUES (
                    @songId, @instrument, @accountId,
                    1000, 1, 100, FALSE, 5,
                    now());
                """;
            fallback.Parameters.AddWithValue("songId", RepairSongIds[0]);
            fallback.Parameters.AddWithValue("instrument", ProLeadInstrument);
            fallback.Parameters.AddWithValue("accountId", AccountId);
            Assert.Equal(1, fallback.ExecuteNonQuery());
        }

        InsertAccountRanking(
            AccountId,
            ProLeadInstrument,
            maxScorePercentRank: 1,
            adjustedSkillRank: 2,
            weightedRank: 2,
            totalScoreRank: 1,
            fcRateRank: 1,
            totalScore: 1_600,
            fullComboCount: 0,
            songsPlayed: 4,
            totalChartedSongs: 4);
        InsertAccountRanking(
            OtherAccountId,
            ProLeadInstrument,
            maxScorePercentRank: 2,
            adjustedSkillRank: 1,
            weightedRank: 1,
            totalScoreRank: 2,
            fcRateRank: 2,
            totalScore: 1_400,
            fullComboCount: 0,
            songsPlayed: 4,
            totalChartedSongs: 4);
    }

    private void ApplyProposedMaximaAndComputeProductionRankings(
        ImprovementNotificationMaintenanceManifest manifest)
    {
        using (var conn = _fixture.DataSource.OpenConnection())
        {
            foreach (var song in manifest.Songs)
            {
                using var update = conn.CreateCommand();
                update.CommandText = """
                    UPDATE song_stats
                    SET max_score = @maxScore
                    WHERE song_id = @songId
                      AND instrument = @instrument
                    """;
                update.Parameters.AddWithValue("songId", song.SongId);
                update.Parameters.AddWithValue(
                    "instrument",
                    ProLeadInstrument);
                update.Parameters.AddWithValue(
                    "maxScore",
                    song.ProposedProLeadMaxScore);
                Assert.Equal(1, update.ExecuteNonQuery());
            }
        }

        var db = new InstrumentDatabase(
            ProLeadInstrument,
            _fixture.DataSource,
            NullLogger<InstrumentDatabase>.Instance)
        {
            UsePublishedScopeSources = true,
        };
        var overThreshold = db.GetCurrentStateOverThresholdEntries();
        var thresholds = overThreshold.ToDictionary(
            entry => (entry.AccountId, entry.SongId),
            entry =>
            {
                var maximum = manifest.Songs.Single(
                    song => song.SongId == entry.SongId)
                    .ProposedProLeadMaxScore;
                return RankingsCalculator.ComputeMaxScoreThreshold(maximum);
            });
        var fallbacks = _fixture.Db.GetBulkBestValidScores(
            ProLeadInstrument,
            thresholds);
        db.PopulateValidScoreOverrides(
            fallbacks.Select(pair => (
                SongId: pair.Key.SongId,
                AccountId: pair.Key.AccountId,
                Score: pair.Value.Score,
                Accuracy: pair.Value.Accuracy,
                IsFullCombo: pair.Value.IsFullCombo,
                Stars: pair.Value.Stars)).ToList());

        using var rankingConnection = db.OpenConnection();
        db.MaterializeCurrentStateValidEntries(
            rankingConnection,
            baseThreshold: 1.05d);
        db.ComputeAccountRankingsFromMaterialized(
            rankingConnection,
            totalChartedSongs: 4,
            credibilityThreshold: 50,
            populationMedian: 0.5d,
            thresholdMultiplier: 1.05d);
    }

    private void PromoteRepairManifest(
        ImprovementNotificationMaintenanceManifest manifest)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        foreach (var song in manifest.Songs)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE songs
                SET max_pro_lead_score = @proposedMaximum,
                    dat_file_hash = @datHash,
                    song_last_modified = @catalogLastModified,
                    paths_generated_at = now(),
                    chopt_version = @choptVersion,
                    chopt_binary_sha256 = @binaryHash,
                    path_generation_profile = @profile,
                    path_artifact_generation_id = @generationId,
                    path_expected_instruments = @expectedInstruments,
                    path_generation_revision = @expectedRevision + 1,
                    path_generation_pending = FALSE
                WHERE song_id = @songId
                  AND path_generation_revision = @expectedRevision;

                UPDATE song_stats
                SET max_score = @proposedMaximum,
                    computed_at = now()
                WHERE song_id = @songId
                  AND instrument = @instrument;
                """;
            cmd.Parameters.AddWithValue("songId", song.SongId);
            cmd.Parameters.AddWithValue(
                "proposedMaximum",
                song.ProposedProLeadMaxScore);
            cmd.Parameters.AddWithValue("datHash", song.StagedDatFileHash);
            cmd.Parameters.AddWithValue(
                "catalogLastModified",
                song.ExpectedCatalogLastModified);
            cmd.Parameters.AddWithValue(
                "choptVersion",
                song.StagedChoptVersion!);
            cmd.Parameters.AddWithValue(
                "binaryHash",
                song.StagedChoptBinarySha256!);
            cmd.Parameters.AddWithValue(
                "profile",
                song.StagedGenerationProfile!);
            cmd.Parameters.AddWithValue(
                "generationId",
                song.StagedArtifactGenerationId);
            cmd.Parameters.AddWithValue(
                "expectedInstruments",
                new[] { ProLeadInstrument });
            cmd.Parameters.AddWithValue(
                "expectedRevision",
                song.ExpectedCurrentPathRevision);
            cmd.Parameters.AddWithValue("instrument", ProLeadInstrument);
            Assert.Equal(2, cmd.ExecuteNonQuery());
        }
    }

    private void ApplyProjectedAccountRankings()
    {
        UpdateAccountRanking(
            AccountId,
            ProLeadInstrument,
            maxScorePercentRank: 3);
        UpdateAccountRanking(
            OtherAccountId,
            ProLeadInstrument,
            maxScorePercentRank: 1);
        UpdateAccountRanking(
            AccountId,
            ProLeadInstrument,
            maxScorePercentRank: 2);
    }

    private (int AccountRank, int OtherRank) ReadCurrentMaxScoreRanks()
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT account_id, max_score_percent_rank
            FROM account_rankings
            WHERE instrument = @instrument
              AND account_id = ANY(@accountIds);
            """;
        cmd.Parameters.AddWithValue("instrument", ProLeadInstrument);
        cmd.Parameters.AddWithValue(
            "accountIds",
            new[] { AccountId, OtherAccountId });
        using var reader = cmd.ExecuteReader();
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read())
            ranks[reader.GetString(0)] = reader.GetInt32(1);
        return (ranks[AccountId], ranks[OtherAccountId]);
    }

    private void AssertMaintenanceAuditBindsScrapeAndManifest(
        long scrapeId,
        ImprovementNotificationMaintenanceManifest manifest,
        string digest)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT published_scrape_id,
                   dry_run_digest,
                   canonical_candidate_data,
                   repair_manifest
            FROM improvement_notification_maintenance_runs;
            """;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(checked((int)scrapeId), reader.GetInt32(0));
        Assert.Equal(digest, reader.GetString(1));
        Assert.Contains(
            $"\"publishedScrapeId\":{scrapeId}",
            reader.GetString(2),
            StringComparison.Ordinal);
        Assert.Contains(
            manifest.Songs[0].StagedArtifactGenerationId,
            reader.GetString(2),
            StringComparison.Ordinal);
        Assert.Contains(
            manifest.Songs[0].StagedArtifactGenerationId,
            reader.GetString(3),
            StringComparison.Ordinal);
    }

    private void InsertAccountRanking(
        string accountId,
        string instrument,
        int maxScorePercentRank,
        int adjustedSkillRank = 1000,
        int weightedRank = 1000,
        int totalScoreRank = 1000,
        int fcRateRank = 1000,
        int totalScore = 100_000,
        int fullComboCount = 1,
        int songsPlayed = 1,
        int totalChartedSongs = 1)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO account_rankings (
                account_id, instrument, songs_played, total_charted_songs, coverage,
                raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank,
                weighted_rating, weighted_rank, fc_rate, fc_rate_rank,
                total_score, total_score_rank, max_score_percent,
                max_score_percent_rank, avg_accuracy, full_combo_count,
                avg_stars, best_rank, avg_rank, computed_at)
            VALUES (
                    @accountId, @instrument, @songsPlayed, @totalChartedSongs,
                    @coverage,
                    0, 0, @adjustedSkillRank,
                0, @weightedRank, 0, @fcRateRank,
                @totalScore, @totalScoreRank, 1,
                @maxScorePercentRank, 100, @fullComboCount,
                6, 1, 1, now());
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("songsPlayed", songsPlayed);
        cmd.Parameters.AddWithValue("totalChartedSongs", totalChartedSongs);
        cmd.Parameters.AddWithValue(
            "coverage",
            (double)songsPlayed / totalChartedSongs);
        cmd.Parameters.AddWithValue("adjustedSkillRank", adjustedSkillRank);
        cmd.Parameters.AddWithValue("weightedRank", weightedRank);
        cmd.Parameters.AddWithValue("fcRateRank", fcRateRank);
        cmd.Parameters.AddWithValue("totalScore", totalScore);
        cmd.Parameters.AddWithValue("totalScoreRank", totalScoreRank);
        cmd.Parameters.AddWithValue("maxScorePercentRank", maxScorePercentRank);
        cmd.Parameters.AddWithValue("fullComboCount", fullComboCount);
        cmd.ExecuteNonQuery();
    }

    private void UpdateAccountRanking(
        string accountId,
        string instrument,
        int maxScorePercentRank,
        int? totalScore = null,
        int? totalScoreRank = null)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE account_rankings
            SET max_score_percent_rank = @maxScorePercentRank,
                total_score = COALESCE(@totalScore, total_score),
                total_score_rank = COALESCE(@totalScoreRank, total_score_rank),
                computed_at = now()
            WHERE account_id = @accountId
              AND instrument = @instrument;
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("maxScorePercentRank", maxScorePercentRank);
        cmd.Parameters.AddWithValue("totalScore", (object?)totalScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "totalScoreRank",
            (object?)totalScoreRank ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void InsertCurrentEntry(
        string accountId,
        string songId,
        string instrument,
        int score,
        int rank)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO current_leaderboard_entries (
                song_id, instrument, account_id, score, accuracy, is_full_combo,
                stars, season, percentile, rank, api_rank, source, difficulty,
                first_seen_at, last_updated_at, computed_at)
            VALUES (
                @songId, @instrument, @accountId, @score, 100, false,
                5, 14, -1, @rank, @rank, 'test', 3,
                now(), now(), now());
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("rank", rank);
        cmd.ExecuteNonQuery();

        if (instrument != ProLeadInstrument)
            return;

        using var source = conn.CreateCommand();
        source.CommandText = """
            WITH publication AS (
                SELECT published_scrape_id
                FROM scrape_publication_state
                WHERE id = TRUE
            ), inserted AS (
                INSERT INTO leaderboard_entries_snapshot (
                    snapshot_id, song_id, instrument, account_id, score,
                    accuracy, is_full_combo, stars, season, percentile,
                    rank, source, difficulty, api_rank,
                    first_seen_at, last_updated_at)
                SELECT publication.published_scrape_id,
                       @songId, @instrument, @accountId, @score,
                       100, false, 5, 14, -1,
                       @rank, 'test', 3, @rank,
                       now(), now()
                FROM publication
                ON CONFLICT (snapshot_id, song_id, instrument, account_id)
                DO UPDATE SET
                    score = EXCLUDED.score,
                    accuracy = EXCLUDED.accuracy,
                    is_full_combo = EXCLUDED.is_full_combo,
                    stars = EXCLUDED.stars,
                    season = EXCLUDED.season,
                    percentile = EXCLUDED.percentile,
                    rank = EXCLUDED.rank,
                    api_rank = EXCLUDED.api_rank,
                    last_updated_at = EXCLUDED.last_updated_at
                RETURNING snapshot_id
            )
            INSERT INTO leaderboard_published_scope_source (
                published_scrape_id, song_id, instrument, scope_kind,
                source_kind, source_snapshot_id, source_scrape_id,
                row_count, content_fingerprint, coverage_fingerprint,
                reported_total_entries, reported_total_pages,
                is_complete, created_at, validated_at)
            SELECT inserted.snapshot_id,
                   @songId, @instrument, 'alltime',
                   'snapshot', inserted.snapshot_id, inserted.snapshot_id,
                   1, md5(@songId || @instrument), md5(@instrument || @songId),
                   1, 1, true, now(), now()
            FROM inserted
            ON CONFLICT (published_scrape_id, instrument, song_id, scope_kind)
            DO UPDATE SET
                row_count =
                    leaderboard_published_scope_source.row_count + 1,
                reported_total_entries =
                    leaderboard_published_scope_source.reported_total_entries + 1,
                validated_at = now();
            """;
        source.Parameters.AddWithValue("songId", songId);
        source.Parameters.AddWithValue("instrument", instrument);
        source.Parameters.AddWithValue("accountId", accountId);
        source.Parameters.AddWithValue("score", score);
        source.Parameters.AddWithValue("rank", rank);
        source.ExecuteNonQuery();
    }

    private void UpdateCurrentEntry(
        string accountId,
        string songId,
        string instrument,
        int score,
        int rank)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE current_leaderboard_entries
            SET score = @score,
                rank = @rank,
                api_rank = @rank,
                last_updated_at = now(),
                computed_at = now()
            WHERE song_id = @songId
              AND instrument = @instrument
              AND account_id = @accountId;
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("rank", rank);
        cmd.ExecuteNonQuery();

        if (instrument == ProLeadInstrument)
        {
            using var snapshot = conn.CreateCommand();
            snapshot.CommandText = """
                UPDATE leaderboard_entries_snapshot
                SET score = @score,
                    rank = @rank,
                    api_rank = @rank,
                    last_updated_at = now()
                WHERE song_id = @songId
                  AND instrument = @instrument
                  AND account_id = @accountId
                  AND snapshot_id = (
                      SELECT published_scrape_id
                      FROM scrape_publication_state
                      WHERE id = TRUE
                  );
                """;
            snapshot.Parameters.AddWithValue("songId", songId);
            snapshot.Parameters.AddWithValue("instrument", instrument);
            snapshot.Parameters.AddWithValue("accountId", accountId);
            snapshot.Parameters.AddWithValue("score", score);
            snapshot.Parameters.AddWithValue("rank", rank);
            snapshot.ExecuteNonQuery();
        }
    }

    private void InsertCurrentBandEntry(int score, int rank)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO current_band_leaderboard_entries (
                song_id, band_type, ranking_scope, scope_combo_id, team_key,
                entry_combo_id, entry_instrument_combo, team_members, score,
                accuracy, is_full_combo, stars, difficulty, season, rank,
                total_entries, percentile, first_seen_at, last_updated_at, computed_at)
            VALUES (
                @songId, @bandType, 'overall', '', @teamKey,
                'Solo_Guitar+Solo_Bass', 'Solo_Guitar+Solo_Bass',
                @teamMembers, @score,
                100, false, 5, 3, 14, @rank,
                500, -1, now(), now(), now());

            INSERT INTO band_current_projection_scope (
                song_id, band_type, ranking_scope, scope_combo_id,
                projection_generation, published_generation, row_count,
                published_row_count, status, last_rebuilt_at, updated_at)
            VALUES (
                @songId, @bandType, 'overall', '',
                0, 0, 1,
                1, 'ready', now(), now());
            """;
        cmd.Parameters.AddWithValue("songId", SongId);
        cmd.Parameters.AddWithValue("bandType", BandType);
        cmd.Parameters.AddWithValue("teamKey", TeamKey);
        cmd.Parameters.AddWithValue(
            "teamMembers",
            new[] { AccountId, "maintenance-bandmate" });
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("rank", rank);
        cmd.ExecuteNonQuery();
    }

    private void UpdateCurrentBandEntry(int score, int rank)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE current_band_leaderboard_entries
            SET score = @score,
                rank = @rank,
                last_updated_at = now(),
                computed_at = now()
            WHERE song_id = @songId
              AND band_type = @bandType
              AND ranking_scope = 'overall'
              AND scope_combo_id = ''
              AND team_key = @teamKey;
            """;
        cmd.Parameters.AddWithValue("songId", SongId);
        cmd.Parameters.AddWithValue("bandType", BandType);
        cmd.Parameters.AddWithValue("teamKey", TeamKey);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("rank", rank);
        cmd.ExecuteNonQuery();
    }

    private long InsertPlayerEvent(
        string accountId,
        string instrument,
        string? deliveryState,
        string? purpose,
        DateTime expiresAtUtc,
        string? songId = null)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = deliveryState is null && purpose is null
            ? """
                INSERT INTO player_improvement_events (
                    account_id, event_kind, song_id, instrument, metric,
                    payload, detected_at, expires_at, source)
                VALUES (
                    @accountId, 'player_weighted_rank_improved', @songId,
                    @instrument, 'weighted_rank',
                    '{}'::jsonb, now(), @expiresAt, 'test')
                RETURNING event_id;
                """
            : """
                INSERT INTO player_improvement_events (
                    account_id, event_kind, song_id, instrument, metric,
                    payload, detected_at, expires_at, source,
                    notification_purpose, notification_cause, delivery_state)
                VALUES (
                    @accountId, 'player_weighted_rank_improved', @songId,
                    @instrument, 'weighted_rank',
                    '{}'::jsonb, now(), @expiresAt, 'test',
                    @purpose, 'max_score_recompute', @deliveryState)
                RETURNING event_id;
                """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("songId", (object?)songId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("expiresAt", expiresAtUtc);
        if (deliveryState is not null || purpose is not null)
        {
            cmd.Parameters.AddWithValue("purpose", purpose!);
            cmd.Parameters.AddWithValue("deliveryState", deliveryState!);
        }
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private long InsertBandSubject()
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO band_improvement_subjects (
                band_type, team_key, team_members)
            VALUES (@bandType, @teamKey, @teamMembers)
            RETURNING band_subject_id;
            """;
        cmd.Parameters.AddWithValue("bandType", BandType);
        cmd.Parameters.AddWithValue("teamKey", TeamKey);
        cmd.Parameters.AddWithValue(
            "teamMembers",
            new[] { AccountId, "maintenance-bandmate" });
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private long InsertBandEvent(
        long bandSubjectId,
        string? deliveryState,
        string? purpose)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO band_improvement_events (
                band_subject_id, event_kind, ranking_scope, combo_id, metric,
                payload, detected_at, expires_at, source,
                notification_purpose, notification_cause, delivery_state)
            VALUES (
                @bandSubjectId, 'band_weighted_rank_improved', 'overall', '',
                'weighted_rank',
                '{}'::jsonb, now(), now() + interval '1 day', 'test',
                COALESCE(@purpose, 'routine_score_observation_v1'),
                CASE
                    WHEN @purpose = 'maintenance_pro_lead_max_score_repair_v1'
                        THEN 'max_score_recompute'
                    ELSE 'score_observation'
                END,
                COALESCE(@deliveryState, 'visible'))
            RETURNING event_id;
            """;
        cmd.Parameters.AddWithValue("bandSubjectId", bandSubjectId);
        cmd.Parameters.AddWithValue("purpose", (object?)purpose ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "deliveryState",
            (object?)deliveryState ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private long InsertServiceEvent(
        string songId,
        string? deliveryState,
        string? purpose)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO service_notifications (
                notification_kind, song_id, title, artist, payload,
                detected_at, expires_at, source, source_key,
                notification_purpose, notification_cause, delivery_state)
            VALUES (
                'service_test', @songId, @songId, 'test', '{}'::jsonb,
                now(), now() + interval '1 day', 'test', @songId,
                COALESCE(@purpose, 'routine_item_shop_observation_v1'),
                CASE
                    WHEN @purpose = 'maintenance_pro_lead_max_score_repair_v1'
                        THEN 'max_score_recompute'
                    ELSE 'item_shop_observation'
                END,
                COALESCE(@deliveryState, 'visible'))
            RETURNING event_id;
            """;
        cmd.Parameters.AddWithValue("songId", songId);
        cmd.Parameters.AddWithValue("purpose", (object?)purpose ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            "deliveryState",
            (object?)deliveryState ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private long InsertDetectionRun(
        string? deliveryState,
        string? purpose)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = deliveryState is null && purpose is null
            ? """
                INSERT INTO improvement_detection_runs (
                    completed_at, status, mode, baseline_only,
                    include_players, include_bands,
                    include_song_events, include_rankings)
                VALUES (
                    now(), 'completed', 'execute', false,
                    true, true,
                    true, true)
                RETURNING run_id;
                """
            : """
                INSERT INTO improvement_detection_runs (
                    completed_at, status, mode, baseline_only,
                    include_players, include_bands,
                    include_song_events, include_rankings,
                    notification_purpose, notification_cause, delivery_state)
                VALUES (
                    now(), 'completed', 'execute', false,
                    true, true,
                    true, true,
                    @purpose, 'max_score_recompute', @deliveryState)
                RETURNING run_id;
                """;
        if (deliveryState is not null || purpose is not null)
        {
            cmd.Parameters.AddWithValue("purpose", purpose!);
            cmd.Parameters.AddWithValue("deliveryState", deliveryState!);
        }

        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private DateTime ReadEventExpiry(long eventId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT expires_at
            FROM player_improvement_events
            WHERE event_id = @eventId;
            """;
        cmd.Parameters.AddWithValue("eventId", eventId);
        return (DateTime)cmd.ExecuteScalar()!;
    }

    private RankStateSnapshot ReadRankState(string accountId, string instrument)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT max_score_percent_rank, total_score, full_combo_count
            FROM player_rank_improvement_state
            WHERE account_id = @accountId
              AND instrument = @instrument;
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return new RankStateSnapshot(
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.GetInt32(2));
    }

    private int ReadPlayerSongStateScore(string accountId)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT score
            FROM player_improvement_state
            WHERE account_id = @accountId
              AND song_id = @songId
              AND instrument = @instrument;
            """;
        cmd.Parameters.AddWithValue("accountId", accountId);
        cmd.Parameters.AddWithValue("songId", SongId);
        cmd.Parameters.AddWithValue("instrument", LeadInstrument);
        return (int)cmd.ExecuteScalar()!;
    }

    private int ReadBandSongStateScore()
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT state.score
            FROM band_improvement_state state
            JOIN band_improvement_subjects subject
              ON subject.band_subject_id = state.band_subject_id
            WHERE subject.band_type = @bandType
              AND subject.team_key = @teamKey
              AND state.song_id = @songId
              AND state.ranking_scope = 'overall'
              AND state.scope_combo_id = '';
            """;
        cmd.Parameters.AddWithValue("bandType", BandType);
        cmd.Parameters.AddWithValue("teamKey", TeamKey);
        cmd.Parameters.AddWithValue("songId", SongId);
        return (int)cmd.ExecuteScalar()!;
    }

    private WriteSnapshot ReadWriteSnapshot(string accountId, string instrument)
    {
        var rankState = ReadRankState(accountId, instrument);
        return new WriteSnapshot(
            CountRows("improvement_detection_runs"),
            CountRows("player_improvement_events"),
            CountRows("band_improvement_events"),
            CountRows("improvement_notification_maintenance_runs"),
            CountRows("improvement_notification_maintenance_candidates"),
            rankState);
    }

    private long CountRows(string tableName)
    {
        using var conn = _fixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private sealed record RankStateSnapshot(
        int MaxScorePercentRank,
        long TotalScore,
        int FullComboCount);

    private sealed record WriteSnapshot(
        long DetectionRuns,
        long PlayerEvents,
        long BandEvents,
        long MaintenanceRuns,
        long MaintenanceCandidates,
        RankStateSnapshot RankState);
}
