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
    public void Precompute_ExpiresOlderBandRankNotification_WhenNewSameComboMetricNotificationArrives()
    {
        InsertCurrentBandRanking(rankingScope: "combo", comboId: "Solo_Guitar+Solo_Bass", weightedRank: 100);
        BaselineBandRankState();

        UpdateCurrentBandRanking(rankingScope: "combo", comboId: "Solo_Guitar+Solo_Bass", weightedRank: 90);
        var firstReport = DetectBandRankEvents();
        Assert.Equal(1, firstReport.BandRankEventsInserted);

        UpdateCurrentBandRanking(rankingScope: "combo", comboId: "Solo_Guitar+Solo_Bass", weightedRank: 80);
        var secondReport = DetectBandRankEvents();

        var liveNotifications = _sut.GetBandNotificationsByTeamKey(
            BandType,
            TeamKey,
            rankingScope: "combo",
            comboId: "Solo_Guitar+Solo_Bass");
        var allNotifications = _sut.GetBandNotificationsByTeamKey(
            BandType,
            TeamKey,
            includeExpired: true,
            rankingScope: "combo",
            comboId: "Solo_Guitar+Solo_Bass");

        Assert.Equal(1, secondReport.BandRankEventsInserted);
        var liveNotification = Assert.Single(liveNotifications.Items);
        Assert.Equal("band_weighted_rank_improved", liveNotification.EventKind);
        AssertValidNotificationGuid(liveNotification);
        Assert.Equal("weighted_rank", liveNotification.Metric);
        Assert.Equal(80, liveNotification.NewRank);
        Assert.Equal(2, allNotifications.Items.Count);
        Assert.Contains(allNotifications.Items, item => item.NewRank == 90 && item.ExpiresAt <= DateTime.UtcNow.AddSeconds(1));
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

    private void InsertAccountRanking(int weightedRank)
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
                0, 0, 1000,
                0, @weightedRank, 0, 1000,
                100000, 1000, 100, 1000,
                100, 1, 6, 1, 1, now());
            """;
        cmd.Parameters.AddWithValue("accountId", AccountId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("weightedRank", weightedRank);
        cmd.ExecuteNonQuery();
    }

    private void UpdateAccountRanking(int weightedRank)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE account_rankings
            SET weighted_rank = @weightedRank,
                computed_at = now()
            WHERE account_id = @accountId
              AND instrument = @instrument;
            """;
        cmd.Parameters.AddWithValue("accountId", AccountId);
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("weightedRank", weightedRank);
        cmd.ExecuteNonQuery();
    }

    private void InsertCurrentBandRanking(string rankingScope, string comboId, int weightedRank)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO band_team_rankings_current_band_duets (
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
                0, @weightedRank, 0, 1000,
                200000, 1000, 100, 1,
                6, 1, 1, NULL, now());
            """;
        cmd.Parameters.AddWithValue("bandType", BandType);
        cmd.Parameters.AddWithValue("rankingScope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", comboId);
        cmd.Parameters.AddWithValue("teamKey", TeamKey);
        cmd.Parameters.AddWithValue("teamMembers", new[] { "account-1", "account-2" });
        cmd.Parameters.AddWithValue("weightedRank", weightedRank);
        cmd.ExecuteNonQuery();
    }

    private void UpdateCurrentBandRanking(string rankingScope, string comboId, int weightedRank)
    {
        using var conn = _metaFixture.DataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE band_team_rankings_current_band_duets
            SET weighted_rank = @weightedRank,
                computed_at = now()
            WHERE band_type = @bandType
              AND ranking_scope = @rankingScope
              AND combo_id = @comboId
              AND team_key = @teamKey;
            """;
        cmd.Parameters.AddWithValue("bandType", BandType);
        cmd.Parameters.AddWithValue("rankingScope", rankingScope);
        cmd.Parameters.AddWithValue("comboId", comboId);
        cmd.Parameters.AddWithValue("teamKey", TeamKey);
        cmd.Parameters.AddWithValue("weightedRank", weightedRank);
        cmd.ExecuteNonQuery();
    }

    private void InsertCurrentEntry(int score, int rank, int stars = 6, bool isFullCombo = true)
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
        cmd.Parameters.AddWithValue("instrument", Instrument);
        cmd.Parameters.AddWithValue("accountId", AccountId);
        cmd.Parameters.AddWithValue("score", score);
        cmd.Parameters.AddWithValue("rank", rank);
        cmd.Parameters.AddWithValue("stars", stars);
        cmd.Parameters.AddWithValue("isFullCombo", isFullCombo);
        cmd.ExecuteNonQuery();
    }

    private void UpdateCurrentEntry(int score, int rank, int stars = 6, bool isFullCombo = true)
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
        cmd.Parameters.AddWithValue("instrument", Instrument);
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