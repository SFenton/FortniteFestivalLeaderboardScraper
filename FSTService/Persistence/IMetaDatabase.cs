namespace FSTService.Persistence;

/// <summary>
/// Abstraction over the central metadata database.
/// </summary>
public interface IMetaDatabase : IDisposable
{
    void EnsureSchema();

    // ── Scrape log ───────────────────────────────────────────────────
    long StartScrapeRun();
    void CompleteScrapeRun(long scrapeId, int songsScraped, long totalEntries, int totalRequests, long totalBytes);
    ScrapeRunInfo? GetLastCompletedScrapeRun();

    // ── Score history ────────────────────────────────────────────────
    void InsertScoreChange(string songId, string instrument, string accountId,
        int? oldScore, int newScore, int? oldRank, int newRank,
        int? accuracy = null, bool? isFullCombo = null, int? stars = null,
        double? percentile = null, int? season = null, string? scoreAchievedAt = null,
        int? seasonRank = null, int? allTimeRank = null, int? difficulty = null);
    void BackfillScoreHistoryDifficulty(string accountId, string songId, string instrument, int score, int difficulty);
    int InsertScoreChanges(IReadOnlyList<ScoreChangeRecord> changes);
    List<ScoreHistoryEntry> GetScoreHistory(string accountId, int limit = 100, string? songId = null, string? instrument = null);
    Dictionary<(string SongId, string Instrument), ValidScoreFallback> GetBestValidScores(
        string accountId, Dictionary<(string SongId, string Instrument), int> thresholds);
    Dictionary<(string AccountId, string SongId), ValidScoreFallback> GetBulkBestValidScores(
        string instrument, Dictionary<(string AccountId, string SongId), int> entries);

    /// <summary>
    /// Returns ALL distinct historical scores per (songId, instrument) for a given account
    /// that are at or below the specified threshold, ordered by score descending.
    /// Used for precomputing validity tiers.
    /// </summary>
    Dictionary<(string SongId, string Instrument), List<ValidScoreFallback>> GetAllValidScoreTiers(
        string accountId, Dictionary<(string SongId, string Instrument), int> maxThresholds);

    // ── Account names ────────────────────────────────────────────────
    int InsertAccountIds(IEnumerable<string> accountIds);
    List<string> GetUnresolvedAccountIds();
    int GetUnresolvedAccountCount();
    int InsertAccountNames(IReadOnlyList<(string AccountId, string? DisplayName)> accounts);
    string? GetDisplayName(string accountId);
    List<(string AccountId, string DisplayName)> SearchAccountNames(string query, int limit = 10);
    Dictionary<string, string> GetDisplayNames(IEnumerable<string> accountIds);

    // ── Registered users ─────────────────────────────────────────────
    HashSet<string> GetRegisteredAccountIds();
    bool RegisterUser(string deviceId, string accountId);
    bool UnregisterUser(string deviceId, string accountId);
    string? GetAccountIdForUsername(string username);

    // ── Backfill ─────────────────────────────────────────────────────
    void EnqueueBackfill(string accountId, int totalSongsToCheck);
    List<BackfillStatusInfo> GetPendingBackfills();
    BackfillStatusInfo? GetBackfillStatus(string accountId);
    void StartBackfill(string accountId);
    void CompleteBackfill(string accountId);
    void FailBackfill(string accountId, string errorMessage);
    void UpdateBackfillProgress(string accountId, int songsChecked, int entriesFound);
    void MarkBackfillSongChecked(string accountId, string songId, string instrument, bool entryFound);
    HashSet<(string SongId, string Instrument)> GetCheckedBackfillPairs(string accountId);

    // ── History reconstruction ───────────────────────────────────────
    void EnqueueHistoryRecon(string accountId, int totalSongsToProcess);
    List<HistoryReconStatusInfo> GetPendingHistoryRecons();
    HistoryReconStatusInfo? GetHistoryReconStatus(string accountId);
    void StartHistoryRecon(string accountId);
    void CompleteHistoryRecon(string accountId);
    void FailHistoryRecon(string accountId, string errorMessage);
    void UpdateHistoryReconProgress(string accountId, int songsProcessed, int seasonsQueried, int historyEntriesFound);
    void MarkHistoryReconSongProcessed(string accountId, string songId, string instrument);
    HashSet<(string SongId, string Instrument)> GetProcessedHistoryReconPairs(string accountId);

    // ── Season windows ───────────────────────────────────────────────
    void UpsertSeasonWindow(int seasonNumber, string eventId, string windowId);
    List<SeasonWindowInfo> GetSeasonWindows();
    int GetCurrentSeason();


    // ── Player stats ─────────────────────────────────────────────────
    void UpsertPlayerStats(PlayerStatsDto stats);
    List<PlayerStatsDto> GetPlayerStats(string accountId);

    // ── Player stats tiers (leeway breakpoint system) ────────────────
    void UpsertPlayerStatsTiers(string accountId, string instrument, string tiersJson);
    void UpsertPlayerStatsTiersBatch(IReadOnlyList<PlayerStatsTiersRow> rows);
    List<PlayerStatsTiersRow> GetPlayerStatsTiers(string accountId);

    // ── First seen season ────────────────────────────────────────────
    HashSet<string> GetSongsWithFirstSeenSeason();
    void UpsertFirstSeenSeason(string songId, int? firstSeenSeason, int? minObservedSeason, int estimatedSeason, string? probeResult);
    Dictionary<string, (int? FirstSeenSeason, int EstimatedSeason)> GetAllFirstSeenSeasons();

    // ── Leaderboard population ───────────────────────────────────────
    void RaiseLeaderboardPopulationFloor(string songId, string instrument, long floor);
    void UpsertLeaderboardPopulation(IReadOnlyList<(string SongId, string Instrument, long TotalEntries)> items);
    long GetLeaderboardPopulation(string songId, string instrument);
    Dictionary<(string SongId, string Instrument), long> GetAllLeaderboardPopulation();

    // ── Rivals ───────────────────────────────────────────────────────
    void EnsureRivalsStatus(string accountId);
    void StartRivals(string accountId, int totalCombosToCompute = 0);
    void CompleteRivals(string accountId, int combosComputed, int rivalsFound);
    void FailRivals(string accountId, string errorMessage);
    RivalsStatusInfo? GetRivalsStatus(string accountId);
    List<string> GetPendingRivalsAccounts();
    void ReplaceRivalsData(string userId, IReadOnlyList<UserRivalRow> rivals, IReadOnlyList<RivalSongSampleRow> samples);
    List<UserRivalRow> GetUserRivals(string userId, string? instrumentCombo = null, string? direction = null);
    List<RivalComboSummary> GetRivalCombos(string userId);
    List<RivalSongSampleRow> GetRivalSongSamples(string userId, string rivalAccountId, string? instrument = null);
    Dictionary<string, List<RivalSongSampleRow>> GetAllRivalSongSamplesForUser(string userId);

    // ── Leaderboard Rivals ───────────────────────────────────────
    void ReplaceLeaderboardRivalsData(string userId, string instrument,
        IReadOnlyList<LeaderboardRivalRow> rivals, IReadOnlyList<LeaderboardRivalSongSampleRow> samples);
    List<LeaderboardRivalRow> GetLeaderboardRivals(string userId, string? instrument = null, string? rankMethod = null, string? direction = null);
    List<LeaderboardRivalSongSampleRow> GetLeaderboardRivalSongSamples(string userId, string rivalAccountId, string instrument, string rankMethod);

    // ── Item shop ────────────────────────────────────────────────────
    void SaveItemShopTracks(IReadOnlySet<string> songIds, IReadOnlySet<string> leavingTomorrow, DateTime scrapedAt);
    (HashSet<string> InShop, HashSet<string> LeavingTomorrow) LoadItemShopTracks();

    // ── Composite rankings ───────────────────────────────────────────
    void ReplaceCompositeRankings(IReadOnlyList<CompositeRankingDto> rankings);
    (List<CompositeRankingDto> Entries, int TotalCount) GetCompositeRankings(int page = 1, int pageSize = 50);
    CompositeRankingDto? GetCompositeRanking(string accountId);
    (List<CompositeRankingDto> Above, CompositeRankingDto? Self, List<CompositeRankingDto> Below) GetCompositeRankingNeighborhood(string accountId, int radius = 5);
    void SnapshotCompositeRankHistory(int topN, IReadOnlySet<string>? additionalAccountIds = null, int retentionDays = 365);

    // ── Composite ranking deltas ─────────────────────────────────────
    void TruncateCompositeRankingDeltas();
    void WriteCompositeRankingDeltas(IReadOnlyList<(string AccountId, double LeewayBucket,
        double AdjustedRating, double WeightedRating, double FcRateRating,
        double TotalScore, double MaxScoreRating, int InstrumentsPlayed, int TotalSongsPlayed)> deltas);

    // ── Combo leaderboard ────────────────────────────────────────────
    void ReplaceComboLeaderboard(string comboId,
        IReadOnlyList<(string AccountId, double AdjustedRating, double WeightedRating, double FcRate, long TotalScore, double MaxScorePercent, int SongsPlayed, int FullComboCount)> entries,
        int totalAccounts);
    (List<ComboLeaderboardEntry> Entries, int TotalAccounts) GetComboLeaderboard(string comboId, string rankBy = "adjusted", int page = 1, int pageSize = 50);
    ComboLeaderboardEntry? GetComboRank(string comboId, string accountId, string rankBy = "adjusted");
    int GetComboTotalAccounts(string comboId);

    // ── Combo ranking deltas ─────────────────────────────────────────
    void TruncateComboRankingDeltas();
    void WriteComboRankingDeltas(IReadOnlyList<(string ComboId, string AccountId, double LeewayBucket,
        double AdjustedRating, double WeightedRating, double FcRate,
        long TotalScore, double MaxScorePct, int SongsPlayed, int FullComboCount)> deltas);

    // ── Maintenance ──────────────────────────────────────────────────
    void Checkpoint();
}
