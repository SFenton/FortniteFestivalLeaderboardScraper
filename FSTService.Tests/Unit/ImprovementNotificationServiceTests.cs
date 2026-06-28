using FSTService.Persistence;
using FSTService.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSTService.Tests.Unit;

public sealed class ImprovementNotificationServiceTests : IDisposable
{
    private const string AccountId = "account-1";
    private const string SongId = "song-1";
    private const string Instrument = "Solo_Guitar";
    private const string BandType = "Band_Duets";
    private const string TeamKey = "account-1:account-2";

    private readonly InMemoryMetaDatabase _metaFixture = new();
    private readonly ImprovementNotificationService _sut;

    public ImprovementNotificationServiceTests()
    {
        _sut = new ImprovementNotificationService(
            _metaFixture.DataSource,
            NullLogger<ImprovementNotificationService>.Instance);
    }

    public void Dispose()
    {
        _metaFixture.Dispose();
    }

    [Fact]
    public void Precompute_DoesNotEmitSongRankImprovement_WhenScoreDidNotIncrease()
    {
        InsertCurrentEntry(score: 100000, rank: 100);
        BaselineCurrentState();

        UpdateCurrentEntry(score: 100000, rank: 90);

        var report = DetectPlayerSongEvents();
        var notifications = _sut.GetPlayerNotifications(AccountId, includeExpired: true);

        Assert.Equal(0, report.PlayerSongEventsInserted);
        Assert.DoesNotContain(notifications.Items, item => item.EventKind == "player_song_rank_improved");
    }

    [Fact]
    public void Precompute_CoalescesPlayerSongImprovements_FromSameScoreRun()
    {
        InsertCurrentEntry(score: 100000, rank: 100, stars: 5, isFullCombo: false);
        BaselineCurrentState();

        UpdateCurrentEntry(score: 101000, rank: 90, stars: 6, isFullCombo: true);

        var report = DetectPlayerSongEvents();
        var notifications = _sut.GetPlayerNotifications(AccountId, includeExpired: true);

        Assert.Equal(1, report.PlayerSongEventsInserted);
        var notification = Assert.Single(notifications.Items);
        AssertValidNotificationGuid(notification);
        Assert.Equal("player_score_pb", notification.EventKind);
        Assert.Equal(100000m, notification.OldNumeric);
        Assert.Equal(101000m, notification.NewNumeric);
        AssertCoalescedKinds(
            notification,
            "player_score_pb",
            "player_fc_achieved",
            "player_gold_stars_achieved",
            "player_stars_improved",
            "player_song_rank_improved");
    }

    [Fact]
    public void Precompute_CoalescesPlayerSongImprovementsAcrossInstruments_FromSameScrapeRun()
    {
        InsertCurrentEntry(score: 100000, rank: 100, stars: 5, isFullCombo: false, instrument: "Solo_Guitar");
        InsertCurrentEntry(score: 200000, rank: 200, stars: 5, isFullCombo: false, instrument: "Solo_Drums");
        BaselineCurrentState();

        UpdateCurrentEntry(score: 101000, rank: 90, stars: 6, isFullCombo: true, instrument: "Solo_Guitar");
        UpdateCurrentEntry(score: 201000, rank: 180, stars: 5, isFullCombo: false, instrument: "Solo_Drums");

        var report = DetectPlayerSongEvents();
        var notifications = _sut.GetPlayerNotifications(AccountId, includeExpired: true);

        Assert.Equal(1, report.PlayerSongEventsInserted);
        var notification = Assert.Single(notifications.Items);
        Assert.Equal(SongId, notification.SongId);
        AssertCoalescedInstruments(notification, "Solo_Guitar", "Solo_Drums");

        var childInstruments = notification.Payload.GetProperty("coalescedEvents")
            .EnumerateArray()
            .Select(element => element.GetProperty("instrument").GetString())
            .Distinct()
            .ToArray();

        Assert.Equal(new[] { "Solo_Guitar", "Solo_Drums" }, childInstruments);
    }

    [Fact]
    public void GetPlayerNotifications_ReportsLatestCompletedSourceRun_WhenNoNewNotificationRowsArrive()
    {
        InsertCurrentEntry(score: 100000, rank: 100);
        BaselineCurrentState();

        UpdateCurrentEntry(score: 101000, rank: 90);
        var firstDetection = DetectPlayerSongEvents();
        var firstEnvelope = _sut.GetPlayerNotifications(AccountId, includeExpired: true);

        var secondDetection = DetectPlayerSongEvents();
        var secondEnvelope = _sut.GetPlayerNotifications(AccountId, includeExpired: true);

        Assert.Equal(1, firstDetection.PlayerSongEventsInserted);
        Assert.Equal(0, secondDetection.PlayerSongEventsInserted);
        Assert.NotNull(firstDetection.RunId);
        Assert.NotNull(secondDetection.RunId);
        Assert.Equal(firstDetection.RunId, firstEnvelope.SourceRunId);
        Assert.Equal(secondDetection.RunId, secondEnvelope.SourceRunId);
        Assert.True(secondEnvelope.SourceRunId > firstEnvelope.SourceRunId);
        Assert.NotNull(secondEnvelope.SourceCompletedAt);
        var notification = Assert.Single(secondEnvelope.Items);
        Assert.Equal(firstDetection.RunId, notification.RunId);
    }

    [Fact]
    public void GetPlayerNotifications_ReportsLatestCompletedSourceRun_WhenFeedIsEmpty()
    {
        InsertCurrentEntry(score: 100000, rank: 100);
        BaselineCurrentState();

        var detection = DetectPlayerSongEvents();
        var envelope = _sut.GetPlayerNotifications(AccountId, includeExpired: true);

        Assert.Equal(0, detection.PlayerSongEventsInserted);
        Assert.NotNull(detection.RunId);
        Assert.Equal(detection.RunId, envelope.SourceRunId);
        Assert.NotNull(envelope.SourceCompletedAt);
        Assert.Empty(envelope.Items);
    }

    [Fact]
    public void GetPlayerNotifications_ReportsLatestPlayerSourceRun_WhenBandRunCompletesLater()
    {
        InsertCurrentEntry(score: 100000, rank: 100);
        BaselineCurrentState();

        UpdateCurrentEntry(score: 101000, rank: 90);
        var playerDetection = DetectPlayerSongEvents();

        InsertCurrentBandEntry(score: 200000, rank: 100);
        BaselineCurrentBandState();

        var envelope = _sut.GetPlayerNotifications(AccountId, includeExpired: true);

        Assert.NotNull(playerDetection.RunId);
        Assert.Equal(playerDetection.RunId, envelope.SourceRunId);
        Assert.Single(envelope.Items);
    }

    [Fact]
    public void GetBandNotifications_ReportsLatestBandSourceRun_WhenPlayerRunCompletesLater()
    {
        InsertCurrentBandEntry(score: 200000, rank: 100);
        BaselineCurrentBandState();

        UpdateCurrentBandEntry(score: 201000, rank: 90);
        var bandDetection = DetectBandSongEvents();

        InsertCurrentEntry(score: 100000, rank: 100);
        BaselineCurrentState();

        var envelope = _sut.GetBandNotificationsByTeamKey(BandType, TeamKey, includeExpired: true);

        Assert.NotNull(bandDetection.RunId);
        Assert.Equal(bandDetection.RunId, envelope.SourceRunId);
        Assert.Single(envelope.Items);
    }

    [Fact]
    public void ServiceNotifications_AreVisibleInPlayerAndBandFeeds()
    {
        var detectedAt = DateTime.UtcNow.AddMinutes(-5);

        var inserted = _sut.UpsertNewShopSongNotifications(
            new[]
            {
                new NewShopSongServiceNotification(
                    SongId,
                    "Folded",
                    "Kehlani",
                    "folded.jpg",
                    "in:2026-05-22T00:00:00.0000000Z",
                    new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc)),
            },
            detectedAt);

        var playerNotifications = _sut.GetPlayerNotifications("late-player");
        var bandNotifications = _sut.GetBandNotificationsByTeamKey("Band_Duets", "late-player:friend");

        Assert.Equal(1, inserted);
        var playerNotification = Assert.Single(playerNotifications.Items);
        var bandNotification = Assert.Single(bandNotifications.Items);
        Assert.Equal(playerNotification.NotificationGuid, bandNotification.NotificationGuid);
        Assert.Equal(ImprovementNotificationService.ServiceNewShopSongKind, playerNotification.EventKind);
        Assert.Equal(SongId, playerNotification.SongId);
        Assert.Null(playerNotification.AccountId);
        Assert.Null(playerNotification.BandSubjectId);
        Assert.Equal("Folded", playerNotification.Payload.GetProperty("songTitle").GetString());
        Assert.Equal("Kehlani", playerNotification.Payload.GetProperty("artist").GetString());
        Assert.Equal("folded.jpg", playerNotification.Payload.GetProperty("albumArt").GetString());
        AssertDistinctNotificationGuids(playerNotifications.Items);
    }

    [Fact]
    public void ServiceNotifications_DedupeBySongKindAndSourceKey()
    {
        var detectedAt = DateTime.UtcNow;
        var notification = new NewShopSongServiceNotification(
            SongId,
            "Folded",
            "Kehlani",
            null,
            "in:2026-05-22T00:00:00.0000000Z",
            new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc));

        var first = _sut.UpsertNewShopSongNotifications(new[] { notification }, detectedAt);
        var second = _sut.UpsertNewShopSongNotifications(new[] { notification }, detectedAt.AddMinutes(1));

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Single(_sut.GetPlayerNotifications(AccountId, includeExpired: true).Items);
    }

    [Fact]
    public void ServiceNotifications_CleanupExpiredRows()
    {
        var detectedAt = DateTime.UtcNow.AddHours(-(ImprovementNotificationService.DefaultLiveHours + 2));
        _sut.UpsertNewShopSongNotifications(
            new[]
            {
                new NewShopSongServiceNotification(SongId, "Folded", "Kehlani", null, "old-shop-window", detectedAt),
            },
            detectedAt);

        Assert.Empty(_sut.GetPlayerNotifications(AccountId).Items);
        Assert.Single(_sut.GetPlayerNotifications(AccountId, includeExpired: true).Items);

        var deleted = _sut.CleanupExpiredServiceNotifications(DateTime.UtcNow);

        Assert.Equal(1, deleted);
        Assert.Empty(_sut.GetPlayerNotifications(AccountId, includeExpired: true).Items);
    }

    [Fact]
    public void Precompute_ExpiresOlderPlayerSongNotification_WhenNewSameLaneNotificationArrives()
    {
        InsertCurrentEntry(score: 100000, rank: 100);
        BaselineCurrentState();

        UpdateCurrentEntry(score: 101000, rank: 90);
        var firstReport = DetectPlayerSongEvents();
        Assert.Equal(1, firstReport.PlayerSongEventsInserted);

        UpdateCurrentEntry(score: 102000, rank: 80);
        var secondReport = DetectPlayerSongEvents();

        var liveNotifications = _sut.GetPlayerNotifications(AccountId);
        var allNotifications = _sut.GetPlayerNotifications(AccountId, includeExpired: true);

        Assert.Equal(1, secondReport.PlayerSongEventsInserted);
        var liveNotification = Assert.Single(liveNotifications.Items);
        Assert.Equal("player_score_pb", liveNotification.EventKind);
        Assert.Equal(102000m, liveNotification.NewNumeric);
        Assert.Equal(2, allNotifications.Items.Count);
        AssertDistinctNotificationGuids(allNotifications.Items);
        Assert.Contains(allNotifications.Items, item => item.NewNumeric == 101000m && item.ExpiresAt <= DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void Precompute_DoesNotEmitBandSongRankImprovement_WhenScoreDidNotIncrease()
    {
        InsertCurrentBandEntry(score: 200000, rank: 100);
        BaselineCurrentBandState();

        UpdateCurrentBandEntry(score: 200000, rank: 90);

        var report = DetectBandSongEvents();
        var notifications = _sut.GetBandNotificationsByTeamKey(BandType, TeamKey, includeExpired: true);

        Assert.Equal(0, report.BandSongEventsInserted);
        Assert.DoesNotContain(notifications.Items, item => item.EventKind == "band_song_rank_improved");
    }

    [Fact]
    public void Precompute_CoalescesBandSongImprovements_FromSameScoreRun()
    {
        InsertCurrentBandEntry(score: 200000, rank: 100, stars: 5, isFullCombo: false);
        BaselineCurrentBandState();

        UpdateCurrentBandEntry(score: 201000, rank: 90, stars: 6, isFullCombo: true);

        var report = DetectBandSongEvents();
        var notifications = _sut.GetBandNotificationsByTeamKey(BandType, TeamKey, includeExpired: true);

        Assert.Equal(1, report.BandSongEventsInserted);
        var notification = Assert.Single(notifications.Items);
        AssertValidNotificationGuid(notification);
        Assert.Equal("band_score_pb", notification.EventKind);
        Assert.Equal(200000m, notification.OldNumeric);
        Assert.Equal(201000m, notification.NewNumeric);
        AssertCoalescedKinds(
            notification,
            "band_score_pb",
            "band_fc_achieved",
            "band_gold_stars_achieved",
            "band_stars_improved",
            "band_song_rank_improved");
    }

    [Fact]
    public void Precompute_CoalescesBandSongOverallAndComboRankImprovements_FromSameScoreRun()
    {
        const string comboId = "Solo_Guitar+Solo_Bass";

        InsertCurrentBandEntry(score: 200000, rank: 100, rankingScope: "overall", scopeComboId: "", entryComboId: comboId);
        InsertCurrentBandEntry(score: 200000, rank: 50, rankingScope: "combo", scopeComboId: comboId, entryComboId: comboId);
        BaselineCurrentBandState();

        UpdateCurrentBandEntry(score: 201000, rank: 90, rankingScope: "overall", scopeComboId: "");
        UpdateCurrentBandEntry(score: 201000, rank: 40, rankingScope: "combo", scopeComboId: comboId);

        var report = DetectBandSongEvents();
        var notifications = _sut.GetBandNotificationsByTeamKey(BandType, TeamKey, includeExpired: true);

        Assert.Equal(1, report.BandSongEventsInserted);
        var notification = Assert.Single(notifications.Items);
        Assert.Equal("band_score_pb", notification.EventKind);
        Assert.Equal("overall", notification.RankingScope);
        Assert.Equal("", notification.ComboId);
        AssertCoalescedKinds(
            notification,
            "band_score_pb",
            "band_song_rank_improved",
            "band_song_rank_improved");

        var rankEvents = notification.Payload.GetProperty("coalescedEvents")
            .EnumerateArray()
            .Where(element => element.GetProperty("eventKind").GetString() == "band_song_rank_improved")
            .ToArray();

        Assert.Equal(2, rankEvents.Length);
        Assert.Contains(rankEvents, element =>
            element.GetProperty("rankingScope").GetString() == "overall" &&
            element.GetProperty("scopeComboId").GetString() == "" &&
            element.GetProperty("oldRank").GetInt32() == 100 &&
            element.GetProperty("newRank").GetInt32() == 90);
        Assert.Contains(rankEvents, element =>
            element.GetProperty("rankingScope").GetString() == "combo" &&
            element.GetProperty("scopeComboId").GetString() == comboId &&
            element.GetProperty("oldRank").GetInt32() == 50 &&
            element.GetProperty("newRank").GetInt32() == 40);

        var comboNotifications = _sut.GetBandNotificationsByTeamKey(
            BandType,
            TeamKey,
            includeExpired: true,
            rankingScope: "combo",
            comboId: comboId);
        var comboNotification = Assert.Single(comboNotifications.Items);
        Assert.Equal(notification.EventId, comboNotification.EventId);
        Assert.Equal(notification.NotificationGuid, comboNotification.NotificationGuid);
    }

    [Fact]
    public void Precompute_ExpiresOlderBandSongNotification_WhenNewSameLaneNotificationArrives()
    {
        InsertCurrentBandEntry(score: 200000, rank: 100);
        BaselineCurrentBandState();

        UpdateCurrentBandEntry(score: 201000, rank: 90);
        var firstReport = DetectBandSongEvents();
        Assert.Equal(1, firstReport.BandSongEventsInserted);

        UpdateCurrentBandEntry(score: 202000, rank: 80);
        var secondReport = DetectBandSongEvents();

        var liveNotifications = _sut.GetBandNotificationsByTeamKey(BandType, TeamKey);
        var allNotifications = _sut.GetBandNotificationsByTeamKey(BandType, TeamKey, includeExpired: true);

        Assert.Equal(1, secondReport.BandSongEventsInserted);
        var liveNotification = Assert.Single(liveNotifications.Items);
        Assert.Equal("band_score_pb", liveNotification.EventKind);
        Assert.Equal(202000m, liveNotification.NewNumeric);
        Assert.Equal(2, allNotifications.Items.Count);
        AssertDistinctNotificationGuids(allNotifications.Items);
        Assert.Contains(allNotifications.Items, item => item.NewNumeric == 201000m && item.ExpiresAt <= DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void Precompute_ExpiresOlderPlayerRankNotification_WhenNewSameMetricNotificationArrives()
    {
        InsertAccountRanking(weightedRank: 100);
        BaselinePlayerRankState();

        UpdateAccountRanking(weightedRank: 90);
        var firstReport = DetectPlayerRankEvents();
        Assert.Equal(1, firstReport.PlayerRankEventsInserted);

        UpdateAccountRanking(weightedRank: 80);
        var secondReport = DetectPlayerRankEvents();

        var liveNotifications = _sut.GetPlayerNotifications(AccountId);
        var allNotifications = _sut.GetPlayerNotifications(AccountId, includeExpired: true);

        Assert.Equal(1, secondReport.PlayerRankEventsInserted);
        var liveNotification = Assert.Single(liveNotifications.Items);
        Assert.Equal("player_weighted_rank_improved", liveNotification.EventKind);
        AssertValidNotificationGuid(liveNotification);
        Assert.Equal("weighted_rank", liveNotification.Metric);
        Assert.Equal(80, liveNotification.NewRank);
        Assert.Equal(2, allNotifications.Items.Count);
        Assert.Contains(allNotifications.Items, item => item.NewRank == 90 && item.ExpiresAt <= DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void GetPlayerNotifications_HidesImprovementEventsDetectedDuringPublicReadFreeze()
    {
        InsertAccountRanking(fcRateRank: 4, fullComboCount: 672);
        BaselinePlayerRankState();
        _metaFixture.Db.SetPublicReadFreeze(true, reason: "publish");

        UpdateAccountRanking(fcRateRank: 1, fullComboCount: 674);
        var report = DetectPlayerRankEvents();

        var frozenNotifications = _sut.GetPlayerNotifications(AccountId, includeExpired: true);
        _metaFixture.Db.SetPublicReadFreeze(false);
        var publishedNotifications = _sut.GetPlayerNotifications(AccountId, includeExpired: true);

        Assert.Equal(1, report.PlayerRankEventsInserted);
        Assert.Empty(frozenNotifications.Items);
        Assert.Single(publishedNotifications.Items);
    }

    [Fact]
    public void Precompute_CoalescesPlayerAggregateRankImprovements_FromSameInstrumentRun()
    {
        InsertAccountRanking(weightedRank: 100, adjustedSkillRank: 200, totalScoreRank: 300, fcRateRank: 400);
        BaselinePlayerRankState();

        UpdateAccountRanking(weightedRank: 90, adjustedSkillRank: 180, totalScoreRank: 250, fcRateRank: 350);

        var report = DetectPlayerRankEvents();
        var notifications = _sut.GetPlayerNotifications(AccountId, includeExpired: true);

        Assert.Equal(1, report.PlayerRankEventsInserted);
        var notification = Assert.Single(notifications.Items);
        Assert.Equal("player_total_score_rank_improved", notification.EventKind);
        Assert.Equal("aggregateRank", notification.Payload.GetProperty("coalescedGroup").GetString());
        AssertCoalescedKinds(
            notification,
            "player_total_score_rank_improved",
            "player_skill_rank_improved",
            "player_weighted_rank_improved",
            "player_fc_rate_rank_improved");
    }

    [Fact]
    public void Precompute_CoalescesPlayerAggregateProgressAndRankImprovements_FromSameInstrumentRun()
    {
        InsertAccountRanking(totalScoreRank: 300, fcRateRank: 400, totalScore: 100000, fullComboCount: 10);
        BaselinePlayerRankState();

        UpdateAccountRanking(totalScoreRank: 250, fcRateRank: 350, totalScore: 123456, fullComboCount: 12);

        var report = DetectPlayerRankEvents();
        var notifications = _sut.GetPlayerNotifications(AccountId, includeExpired: true);

        Assert.Equal(1, report.PlayerRankEventsInserted);
        var notification = Assert.Single(notifications.Items);
        Assert.Equal("player_total_score_improved", notification.EventKind);
        Assert.Equal("total_score", notification.Metric);
        Assert.Equal(123456m, notification.NewNumeric);
        Assert.Equal("instrumentAggregate", notification.Payload.GetProperty("coalescedGroup").GetString());
        AssertCoalescedKinds(
            notification,
            "player_total_score_improved",
            "player_total_score_rank_improved",
            "player_fc_count_improved",
            "player_fc_rate_rank_improved");
    }

    [Fact]
    public void Precompute_DoesNotCoalescePlayerAggregateRankImprovementsAcrossInstruments()
    {
        InsertAccountRanking(weightedRank: 100, totalScoreRank: 300, instrument: "Solo_Guitar");
        InsertAccountRanking(weightedRank: 200, totalScoreRank: 400, instrument: "Solo_Drums");
        BaselinePlayerRankState();

        UpdateAccountRanking(weightedRank: 90, totalScoreRank: 250, instrument: "Solo_Guitar");
        UpdateAccountRanking(weightedRank: 180, totalScoreRank: 350, instrument: "Solo_Drums");

        var report = DetectPlayerRankEvents();
        var notifications = _sut.GetPlayerNotifications(AccountId, includeExpired: true);

        Assert.Equal(2, report.PlayerRankEventsInserted);
        Assert.Equal(2, notifications.Items.Count);
        Assert.Contains(notifications.Items, item => item.Instrument == "Solo_Guitar");
        Assert.Contains(notifications.Items, item => item.Instrument == "Solo_Drums");
    }

    [Fact]
    public void Precompute_DoesNotEmitBandAggregateRankImprovements_WhenRanksImproveWithoutAggregateProgress()
    {
        const string trioBandType = "Band_Trios";
        const string trioTeamKey = "kahnyri:phankie:sfentonx";
        const string leadLeadLeadCombo = "Solo_Guitar+Solo_Guitar+Solo_Guitar";

        InsertCurrentBandRanking(
            rankingScope: "combo",
            comboId: leadLeadLeadCombo,
            weightedRank: 100,
            totalScoreRank: 200,
            fcRateRank: 300,
            totalScore: 200000,
            fullComboCount: 1,
            bandType: trioBandType,
            teamKey: trioTeamKey,
            teamMembers: new[] { "kahnyri", "phankie", "sfentonx" });
        BaselineBandRankState();

        UpdateCurrentBandRanking(
            rankingScope: "combo",
            comboId: leadLeadLeadCombo,
            weightedRank: 90,
            totalScoreRank: 180,
            fcRateRank: 250,
            totalScore: 200000,
            fullComboCount: 1,
            bandType: trioBandType,
            teamKey: trioTeamKey);

        var report = DetectBandRankEvents();
        var notifications = _sut.GetBandNotificationsByTeamKey(
            trioBandType,
            trioTeamKey,
            includeExpired: true,
            rankingScope: "combo",
            comboId: leadLeadLeadCombo);

        Assert.Equal(0, report.BandRankEventsInserted);
        Assert.Empty(notifications.Items);
    }

    [Fact]
    public void Precompute_DoesNotEmitBandMetricRankImprovement_WhenOnlyOtherAggregateProgressChanged()
    {
        const string totalScoreRankCombo = "Solo_Guitar+Solo_Bass";
        const string fcRateRankCombo = "Solo_Drums+Solo_Vocals";

        InsertCurrentBandRanking(rankingScope: "combo", comboId: totalScoreRankCombo, weightedRank: 100, totalScoreRank: 200, fcRateRank: 300, totalScore: 200000, fullComboCount: 1);
        InsertCurrentBandRanking(rankingScope: "combo", comboId: fcRateRankCombo, weightedRank: 100, totalScoreRank: 200, fcRateRank: 300, totalScore: 200000, fullComboCount: 1);
        BaselineBandRankState();

        UpdateCurrentBandRanking(rankingScope: "combo", comboId: totalScoreRankCombo, weightedRank: 100, totalScoreRank: 180, fcRateRank: 300, totalScore: 200000, fullComboCount: 2);
        UpdateCurrentBandRanking(rankingScope: "combo", comboId: fcRateRankCombo, weightedRank: 100, totalScoreRank: 200, fcRateRank: 250, totalScore: 201000, fullComboCount: 1);

        var report = DetectBandRankEvents();
        var totalScoreRankNotifications = _sut.GetBandNotificationsByTeamKey(
            BandType,
            TeamKey,
            includeExpired: true,
            rankingScope: "combo",
            comboId: totalScoreRankCombo,
            kind: "band_total_score_rank_improved");
        var fcRateRankNotifications = _sut.GetBandNotificationsByTeamKey(
            BandType,
            TeamKey,
            includeExpired: true,
            rankingScope: "combo",
            comboId: fcRateRankCombo,
            kind: "band_fc_rate_rank_improved");

        Assert.Equal(2, report.BandRankEventsInserted);
        Assert.Empty(totalScoreRankNotifications.Items);
        Assert.Empty(fcRateRankNotifications.Items);
    }

    [Fact]
    public void Precompute_EmitsBandWeightedRankImprovement_WhenScoreOrFullComboProgressChanged()
    {
        const string scoreProgressCombo = "Solo_Guitar+Solo_Bass";
        const string fullComboProgressCombo = "Solo_Drums+Solo_Vocals";

        InsertCurrentBandRanking(rankingScope: "combo", comboId: scoreProgressCombo, weightedRank: 100, totalScoreRank: 200, fcRateRank: 300, totalScore: 200000, fullComboCount: 1);
        InsertCurrentBandRanking(rankingScope: "combo", comboId: fullComboProgressCombo, weightedRank: 100, totalScoreRank: 200, fcRateRank: 300, totalScore: 200000, fullComboCount: 1);
        BaselineBandRankState();

        UpdateCurrentBandRanking(rankingScope: "combo", comboId: scoreProgressCombo, weightedRank: 90, totalScoreRank: 200, fcRateRank: 300, totalScore: 201000, fullComboCount: 1);
        UpdateCurrentBandRanking(rankingScope: "combo", comboId: fullComboProgressCombo, weightedRank: 80, totalScoreRank: 200, fcRateRank: 300, totalScore: 200000, fullComboCount: 2);

        var report = DetectBandRankEvents();
        var scoreProgressNotifications = _sut.GetBandNotificationsByTeamKey(
            BandType,
            TeamKey,
            includeExpired: true,
            rankingScope: "combo",
            comboId: scoreProgressCombo,
            kind: "band_weighted_rank_improved");
        var fullComboProgressNotifications = _sut.GetBandNotificationsByTeamKey(
            BandType,
            TeamKey,
            includeExpired: true,
            rankingScope: "combo",
            comboId: fullComboProgressCombo,
            kind: "band_weighted_rank_improved");

        Assert.Equal(4, report.BandRankEventsInserted);
        var scoreProgressNotification = Assert.Single(scoreProgressNotifications.Items);
        Assert.Equal(90, scoreProgressNotification.NewRank);
        var fullComboProgressNotification = Assert.Single(fullComboProgressNotifications.Items);
        Assert.Equal(80, fullComboProgressNotification.NewRank);
    }

    [Fact]
    public void Precompute_ExpiresOlderBandRankNotification_WhenNewSameComboMetricNotificationArrives()
    {
        InsertCurrentBandRanking(rankingScope: "combo", comboId: "Solo_Guitar+Solo_Bass", weightedRank: 100, totalScore: 200000);
        BaselineBandRankState();

        UpdateCurrentBandRanking(rankingScope: "combo", comboId: "Solo_Guitar+Solo_Bass", weightedRank: 90, totalScore: 201000);
        var firstReport = DetectBandRankEvents();
        Assert.Equal(2, firstReport.BandRankEventsInserted);

        UpdateCurrentBandRanking(rankingScope: "combo", comboId: "Solo_Guitar+Solo_Bass", weightedRank: 80, totalScore: 202000);
        var secondReport = DetectBandRankEvents();

        var liveNotifications = _sut.GetBandNotificationsByTeamKey(
            BandType,
            TeamKey,
            rankingScope: "combo",
            comboId: "Solo_Guitar+Solo_Bass",
            kind: "band_weighted_rank_improved");
        var allNotifications = _sut.GetBandNotificationsByTeamKey(
            BandType,
            TeamKey,
            includeExpired: true,
            rankingScope: "combo",
            comboId: "Solo_Guitar+Solo_Bass",
            kind: "band_weighted_rank_improved");

        Assert.Equal(2, secondReport.BandRankEventsInserted);
        var liveNotification = Assert.Single(liveNotifications.Items);
        Assert.Equal("band_weighted_rank_improved", liveNotification.EventKind);
        AssertValidNotificationGuid(liveNotification);
        Assert.Equal("weighted_rank", liveNotification.Metric);
        Assert.Equal(80, liveNotification.NewRank);
        Assert.Equal(2, allNotifications.Items.Count);
        Assert.Contains(allNotifications.Items, item => item.NewRank == 90 && item.ExpiresAt <= DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void GetBandNotifications_HidesImprovementEventsDetectedDuringPublicReadFreeze()
    {
        InsertCurrentBandRanking(rankingScope: "overall", comboId: "", weightedRank: 100, totalScore: 200000);
        BaselineBandRankState();
        _metaFixture.Db.SetPublicReadFreeze(true, reason: "publish");

        UpdateCurrentBandRanking(rankingScope: "overall", comboId: "", weightedRank: 90, totalScore: 201000);
        var report = DetectBandRankEvents();

        var frozenNotifications = _sut.GetBandNotificationsByTeamKey(BandType, TeamKey, includeExpired: true);
        _metaFixture.Db.SetPublicReadFreeze(false);
        var publishedNotifications = _sut.GetBandNotificationsByTeamKey(BandType, TeamKey, includeExpired: true);

        Assert.Equal(2, report.BandRankEventsInserted);
        Assert.Empty(frozenNotifications.Items);
        Assert.Equal(report.BandRankEventsInserted, publishedNotifications.Items.Count);
    }

    [Fact]
    public void Precompute_CoalescesBandAggregateRankImprovements_FromSameScopeRun()
    {
        InsertCurrentBandRanking(rankingScope: "combo", comboId: "Solo_Guitar+Solo_Bass", weightedRank: 100, totalScoreRank: 200, fcRateRank: 300, totalScore: 200000, fullComboCount: 1);
        BaselineBandRankState();

        UpdateCurrentBandRanking(rankingScope: "combo", comboId: "Solo_Guitar+Solo_Bass", weightedRank: 90, totalScoreRank: 180, fcRateRank: 250, totalScore: 201000, fullComboCount: 2);

        var report = DetectBandRankEvents();
        var notifications = _sut.GetBandNotificationsByTeamKey(
            BandType,
            TeamKey,
            includeExpired: true,
            rankingScope: "combo",
            comboId: "Solo_Guitar+Solo_Bass",
            kind: "band_total_score_rank_improved");

        Assert.Equal(3, report.BandRankEventsInserted);
        var notification = Assert.Single(notifications.Items);
        Assert.Equal("band_total_score_rank_improved", notification.EventKind);
        Assert.Equal("aggregateRank", notification.Payload.GetProperty("coalescedGroup").GetString());
        AssertCoalescedKinds(
            notification,
            "band_total_score_rank_improved",
            "band_weighted_rank_improved",
            "band_fc_rate_rank_improved");
    }

    private static void AssertCoalescedKinds(ImprovementNotificationDto notification, params string[] expectedKinds)
    {
        var payload = notification.Payload;
        var actualKinds = payload.GetProperty("coalescedEventKinds")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToArray();

        Assert.Equal(expectedKinds, actualKinds);
        Assert.Equal(expectedKinds.Length, payload.GetProperty("coalescedEventCount").GetInt32());
    }

    private static void AssertValidNotificationGuid(ImprovementNotificationDto notification)
    {
        Assert.NotEqual(Guid.Empty, notification.NotificationGuid);
    }

    private static void AssertCoalescedInstruments(ImprovementNotificationDto notification, params string[] expectedInstruments)
    {
        var payload = notification.Payload;
        var actualInstruments = payload.GetProperty("coalescedInstruments")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToArray();

        Assert.Equal(expectedInstruments, actualInstruments);
    }

    private static void AssertDistinctNotificationGuids(IReadOnlyList<ImprovementNotificationDto> notifications)
    {
        Assert.All(notifications, AssertValidNotificationGuid);
        Assert.Equal(notifications.Count, notifications.Select(notification => notification.NotificationGuid).Distinct().Count());
    }

    private void BaselineCurrentState()
    {
        _sut.Precompute(new ImprovementNotificationPrecomputeOptions(
            Execute: true,
            BaselineOnly: true,
            Scope: "all",
            IncludePlayers: true,
            IncludeBands: false,
            IncludeSongEvents: true,
            IncludeRankings: false,
            PruneExpired: false));
    }

    private ImprovementNotificationPrecomputeReport DetectPlayerSongEvents()
    {
        return _sut.Precompute(new ImprovementNotificationPrecomputeOptions(
            Execute: true,
            BaselineOnly: false,
            Scope: "all",
            IncludePlayers: true,
            IncludeBands: false,
            IncludeSongEvents: true,
            IncludeRankings: false,
            PruneExpired: false));
    }

    private void BaselineCurrentBandState()
    {
        _sut.Precompute(new ImprovementNotificationPrecomputeOptions(
            Execute: true,
            BaselineOnly: true,
            Scope: "all",
            IncludePlayers: false,
            IncludeBands: true,
            IncludeSongEvents: true,
            IncludeRankings: false,
            PruneExpired: false));
    }

    private ImprovementNotificationPrecomputeReport DetectBandSongEvents()
    {
        return _sut.Precompute(new ImprovementNotificationPrecomputeOptions(
            Execute: true,
            BaselineOnly: false,
            Scope: "all",
            IncludePlayers: false,
            IncludeBands: true,
            IncludeSongEvents: true,
            IncludeRankings: false,
            PruneExpired: false));
    }

    private void BaselinePlayerRankState()
    {
        _sut.Precompute(new ImprovementNotificationPrecomputeOptions(
            Execute: true,
            BaselineOnly: true,
            Scope: "all",
            IncludePlayers: true,
            IncludeBands: false,
            IncludeSongEvents: false,
            IncludeRankings: true,
            PruneExpired: false));
    }

    private ImprovementNotificationPrecomputeReport DetectPlayerRankEvents()
    {
        return _sut.Precompute(new ImprovementNotificationPrecomputeOptions(
            Execute: true,
            BaselineOnly: false,
            Scope: "all",
            IncludePlayers: true,
            IncludeBands: false,
            IncludeSongEvents: false,
            IncludeRankings: true,
            PruneExpired: false));
    }

    private void BaselineBandRankState()
    {
        _sut.Precompute(new ImprovementNotificationPrecomputeOptions(
            Execute: true,
            BaselineOnly: true,
            Scope: "all",
            IncludePlayers: false,
            IncludeBands: true,
            IncludeSongEvents: false,
            IncludeRankings: true,
            PruneExpired: false));
    }

    private ImprovementNotificationPrecomputeReport DetectBandRankEvents()
    {
        return _sut.Precompute(new ImprovementNotificationPrecomputeOptions(
            Execute: true,
            BaselineOnly: false,
            Scope: "all",
            IncludePlayers: false,
            IncludeBands: true,
            IncludeSongEvents: false,
            IncludeRankings: true,
            PruneExpired: false));
    }

    private void InsertAccountRanking(
        int weightedRank = 1000,
        int adjustedSkillRank = 1000,
        int totalScoreRank = 1000,
        int fcRateRank = 1000,
        int totalScore = 100000,
        int fullComboCount = 1,
        string instrument = Instrument)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO account_rankings (
                account_id, instrument, songs_played, total_charted_songs, coverage,
                raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank,
                weighted_rating, weighted_rank, fc_rate, fc_rate_rank,
                total_score, total_score_rank, max_score_percent, max_score_percent_rank,
                avg_accuracy, full_combo_count, avg_stars, best_rank, avg_rank, computed_at)
            VALUES (
                @accountId, @instrument, 1, 1, 1,
                0, 0, @adjustedSkillRank,
                0, @weightedRank, 0, @fcRateRank,
                @totalScore, @totalScoreRank, 100, 1000,
                100, @fullComboCount, 6, 1, 1, now());
            """;
        cmd.Parameters.AddWithValue("accountId", AccountId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("adjustedSkillRank", adjustedSkillRank);
        cmd.Parameters.AddWithValue("weightedRank", weightedRank);
        cmd.Parameters.AddWithValue("totalScoreRank", totalScoreRank);
        cmd.Parameters.AddWithValue("fcRateRank", fcRateRank);
        cmd.Parameters.AddWithValue("totalScore", totalScore);
        cmd.Parameters.AddWithValue("fullComboCount", fullComboCount);
        cmd.ExecuteNonQuery();
    }

    private void UpdateAccountRanking(
        int weightedRank = 1000,
        int adjustedSkillRank = 1000,
        int totalScoreRank = 1000,
        int fcRateRank = 1000,
        int totalScore = 100000,
        int fullComboCount = 1,
        string instrument = Instrument)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE account_rankings
            SET weighted_rank = @weightedRank,
                adjusted_skill_rank = @adjustedSkillRank,
                total_score_rank = @totalScoreRank,
                fc_rate_rank = @fcRateRank,
                                total_score = @totalScore,
                                full_combo_count = @fullComboCount,
                computed_at = now()
            WHERE account_id = @accountId
              AND instrument = @instrument;
            """;
        cmd.Parameters.AddWithValue("accountId", AccountId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("weightedRank", weightedRank);
        cmd.Parameters.AddWithValue("adjustedSkillRank", adjustedSkillRank);
        cmd.Parameters.AddWithValue("totalScoreRank", totalScoreRank);
        cmd.Parameters.AddWithValue("fcRateRank", fcRateRank);
        cmd.Parameters.AddWithValue("totalScore", totalScore);
        cmd.Parameters.AddWithValue("fullComboCount", fullComboCount);
        cmd.ExecuteNonQuery();
    }

    private void InsertCurrentBandRanking(
        string rankingScope,
        string comboId,
        int weightedRank = 1000,
        int totalScoreRank = 1000,
        int fcRateRank = 1000,
        int totalScore = 200000,
        int fullComboCount = 1,
        string bandType = BandType,
        string teamKey = TeamKey,
        string[]? teamMembers = null)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {CurrentBandRankingTable(bandType)} (
                band_type, ranking_scope, combo_id, team_key, team_members,
                songs_played, total_charted_songs, coverage,
                raw_skill_rating, adjusted_skill_rating, adjusted_skill_rank,
                weighted_rating, weighted_rank, fc_rate, fc_rate_rank,
                total_score, total_score_rank, avg_accuracy, full_combo_count,
                avg_stars, best_rank, avg_rank, raw_weighted_rating, computed_at)
            VALUES (
                @bandType, @rankingScope, @comboId, @teamKey, @teamMembers,
                1, 1, 1,
                0, 0, 1000,
                0, @weightedRank, 0, @fcRateRank,
                @totalScore, @totalScoreRank, 100, @fullComboCount,
                6, 1, 1, NULL, now());
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("rankingScope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", comboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("teamMembers", teamMembers ?? new[] { "account-1", "account-2" });
        cmd.Parameters.AddWithValue("weightedRank", weightedRank);
        cmd.Parameters.AddWithValue("totalScoreRank", totalScoreRank);
        cmd.Parameters.AddWithValue("fcRateRank", fcRateRank);
        cmd.Parameters.AddWithValue("totalScore", totalScore);
        cmd.Parameters.AddWithValue("fullComboCount", fullComboCount);
        cmd.ExecuteNonQuery();
    }

    private void UpdateCurrentBandRanking(
        string rankingScope,
        string comboId,
        int weightedRank = 1000,
        int totalScoreRank = 1000,
        int fcRateRank = 1000,
        int totalScore = 200000,
        int fullComboCount = 1,
        string bandType = BandType,
        string teamKey = TeamKey)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {CurrentBandRankingTable(bandType)}
            SET weighted_rank = @weightedRank,
                total_score_rank = @totalScoreRank,
                fc_rate_rank = @fcRateRank,
                total_score = @totalScore,
                full_combo_count = @fullComboCount,
                computed_at = now()
            WHERE band_type = @bandType
              AND ranking_scope = @rankingScope
              AND combo_id = @comboId
              AND team_key = @teamKey;
            """;
        cmd.Parameters.AddWithValue("bandType", bandType);
        cmd.Parameters.AddWithValue("rankingScope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", comboId);
        cmd.Parameters.AddWithValue("teamKey", teamKey);
        cmd.Parameters.AddWithValue("weightedRank", weightedRank);
        cmd.Parameters.AddWithValue("totalScoreRank", totalScoreRank);
        cmd.Parameters.AddWithValue("fcRateRank", fcRateRank);
        cmd.Parameters.AddWithValue("totalScore", totalScore);
        cmd.Parameters.AddWithValue("fullComboCount", fullComboCount);
        cmd.ExecuteNonQuery();
    }

    private static string CurrentBandRankingTable(string bandType) => bandType switch
    {
        "Band_Duets" => "band_team_rankings_current_band_duets",
        "Band_Trios" => "band_team_rankings_current_band_trios",
        "Band_Quad" => "band_team_rankings_current_band_quad",
        _ => throw new ArgumentOutOfRangeException(nameof(bandType), bandType, "Unknown band type."),
    };

    private void InsertCurrentEntry(int score, int rank, int stars = 6, bool isFullCombo = true, string instrument = Instrument)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO current_leaderboard_entries (
                song_id, instrument, account_id, score, accuracy, is_full_combo,
                stars, season, percentile, rank, api_rank, source, difficulty,
                first_seen_at, last_updated_at, computed_at)
            VALUES (
                @songId, @instrument, @accountId, @score, 100, @isFullCombo,
                @stars, 14, -1, @rank, @rank, 'test', 3,
                now(), now(), now());
            """;
        cmd.Parameters.AddWithValue("songId", SongId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", AccountId);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("rank", rank);
        cmd.Parameters.AddWithValue("stars", stars);
        cmd.Parameters.AddWithValue("isFullCombo", isFullCombo);
        cmd.ExecuteNonQuery();

    }

    private void UpdateCurrentEntry(int score, int rank, int stars = 6, bool isFullCombo = true, string instrument = Instrument)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE current_leaderboard_entries
            SET score = @score,
                rank = @rank,
                api_rank = @rank,
                stars = @stars,
                is_full_combo = @isFullCombo,
                last_updated_at = now(),
                computed_at = now()
            WHERE song_id = @songId
              AND instrument = @instrument
              AND account_id = @accountId;
            """;
        cmd.Parameters.AddWithValue("songId", SongId);
        cmd.Parameters.AddWithValue("instrument", instrument);
        cmd.Parameters.AddWithValue("accountId", AccountId);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("rank", rank);
        cmd.Parameters.AddWithValue("stars", stars);
        cmd.Parameters.AddWithValue("isFullCombo", isFullCombo);
        cmd.ExecuteNonQuery();
    }

    private void InsertCurrentBandEntry(
        int score,
        int rank,
        int stars = 6,
        bool isFullCombo = true,
        string rankingScope = "overall",
        string scopeComboId = "",
        string entryComboId = "Solo_Guitar+Solo_Bass")
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO current_band_leaderboard_entries (
                song_id, band_type, ranking_scope, scope_combo_id, team_key,
                entry_combo_id, entry_instrument_combo, team_members, score,
                accuracy, is_full_combo, stars, difficulty, season, rank,
                total_entries, percentile, first_seen_at, last_updated_at, computed_at)
            VALUES (
                @songId, @bandType, @rankingScope, @scopeComboId, @teamKey,
                @entryComboId, 'Solo_Guitar+Solo_Bass', @teamMembers, @score,
                100, @isFullCombo, @stars, 3, 14, @rank,
                500, -1, now(), now(), now());
            """;
        cmd.Parameters.AddWithValue("songId", SongId);
        cmd.Parameters.AddWithValue("bandType", BandType);
        cmd.Parameters.AddWithValue("rankingScope", rankingScope);
        cmd.Parameters.AddWithValue("scopeComboId", scopeComboId);
        cmd.Parameters.AddWithValue("teamKey", TeamKey);
        cmd.Parameters.AddWithValue("entryComboId", entryComboId);
        cmd.Parameters.AddWithValue("teamMembers", new[] { "account-1", "account-2" });
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("rank", rank);
        cmd.Parameters.AddWithValue("stars", stars);
        cmd.Parameters.AddWithValue("isFullCombo", isFullCombo);
        cmd.ExecuteNonQuery();

        using var scopeCmd = conn.CreateCommand();
        scopeCmd.CommandText = """
            INSERT INTO band_current_projection_scope (
                song_id, band_type, ranking_scope, scope_combo_id,
                projection_generation, published_generation, row_count,
                published_row_count, status, last_rebuilt_at, updated_at)
            VALUES (
                @songId, @bandType, @rankingScope, @scopeComboId,
                0, 0, 1,
                1, 'ready', now(), now())
            ON CONFLICT (song_id, band_type, ranking_scope, scope_combo_id) DO UPDATE
            SET projection_generation = EXCLUDED.projection_generation,
                published_generation = EXCLUDED.published_generation,
                row_count = EXCLUDED.row_count,
                published_row_count = EXCLUDED.published_row_count,
                status = EXCLUDED.status,
                last_rebuilt_at = EXCLUDED.last_rebuilt_at,
                updated_at = EXCLUDED.updated_at;
            """;
        scopeCmd.Parameters.AddWithValue("songId", SongId);
        scopeCmd.Parameters.AddWithValue("bandType", BandType);
        scopeCmd.Parameters.AddWithValue("rankingScope", rankingScope);
        scopeCmd.Parameters.AddWithValue("scopeComboId", scopeComboId);
        scopeCmd.ExecuteNonQuery();
    }

    private void UpdateCurrentBandEntry(
        int score,
        int rank,
        int stars = 6,
        bool isFullCombo = true,
        string rankingScope = "overall",
        string scopeComboId = "")
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE current_band_leaderboard_entries
            SET score = @score,
                rank = @rank,
                stars = @stars,
                is_full_combo = @isFullCombo,
                last_updated_at = now(),
                computed_at = now()
            WHERE song_id = @songId
              AND band_type = @bandType
                            AND ranking_scope = @rankingScope
                            AND scope_combo_id = @scopeComboId
              AND team_key = @teamKey;
            """;
        cmd.Parameters.AddWithValue("songId", SongId);
        cmd.Parameters.AddWithValue("bandType", BandType);
                cmd.Parameters.AddWithValue("rankingScope", rankingScope);
                cmd.Parameters.AddWithValue("scopeComboId", scopeComboId);
        cmd.Parameters.AddWithValue("teamKey", TeamKey);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("rank", rank);
        cmd.Parameters.AddWithValue("stars", stars);
        cmd.Parameters.AddWithValue("isFullCombo", isFullCombo);
        cmd.ExecuteNonQuery();
    }
}