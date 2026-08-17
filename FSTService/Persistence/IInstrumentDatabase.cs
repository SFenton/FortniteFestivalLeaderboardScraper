using FSTService.Scraping;

namespace FSTService.Persistence;

/// <summary>
/// Abstraction over a per-instrument leaderboard database.
/// Each instance is scoped to a single instrument.
/// </summary>
public interface IInstrumentDatabase : IDisposable
{
    string Instrument { get; }

    void EnsureSchema();

    // ── Leaderboard entries ──────────────────────────────────────────
    int UpsertEntries(string songId, IReadOnlyList<LeaderboardEntry> entries);
    LeaderboardEntry? GetEntry(string songId, string accountId);
    Dictionary<string, LeaderboardEntry> GetEntriesForAccounts(string songId, IReadOnlyCollection<string> accountIds);
    int? GetMinSeason(string songId);
    int? GetMaxSeason();
    long GetTotalEntryCount();
    string? GetAnySongId();

    // ── Leaderboard reads ────────────────────────────────────────────
    List<LeaderboardEntryDto> GetLeaderboard(string songId, int? top = null, int offset = 0);
    int GetLeaderboardCount(string songId);
    Dictionary<string, int> GetAllSongCounts();
    Dictionary<string, int> GetCurrentStateAllSongCounts();
    (List<LeaderboardEntryDto> Entries, int TotalCount) GetLeaderboardWithCount(string songId, int? top = null, int offset = 0, int? maxScore = null);
    List<LeaderboardEntryDto> GetCurrentStateLeaderboard(string songId, int? top = null, int offset = 0);
    (List<LeaderboardEntryDto> Entries, int TotalCount) GetCurrentStateLeaderboardWithCount(string songId, int? top = null, int offset = 0, int? maxScore = null);
    List<(string AccountId, int Rank, int Score)> GetNeighborhood(string songId, int centerRank, int rankRadius, string excludeAccountId);
    List<(string AccountId, int Rank, int Score)> GetCurrentStateNeighborhood(string songId, int centerRank, int rankRadius, string excludeAccountId);
    List<string> GetAccountsInRankRange(string songId, int minRank, int maxRank);

    // ── Player queries ───────────────────────────────────────────────
    HashSet<string> GetSongIdsForAccount(string accountId);
    HashSet<string> GetCurrentStateSongIdsForAccount(string accountId);
    List<PlayerScoreDto> GetPlayerScoresForSongs(string accountId, IReadOnlyCollection<string> songIds);
    List<PlayerScoreDto> GetCurrentStatePlayerScoresForSongs(string accountId, IReadOnlyCollection<string> songIds);
    List<PlayerScoreDto> GetPlayerScores(string accountId, string? songId = null);
    List<PlayerScoreDto> GetCurrentStatePlayerScores(string accountId, string? songId = null);
    Dictionary<string, List<PlayerScoreDto>>
        GetCurrentStatePlayerScoresForAccounts(
            IReadOnlyCollection<string> accountIds,
            string? songId = null,
            bool includeBlankAccountIds = false);
    Dictionary<string, int> GetPlayerRankings(string accountId, string? songId = null);
    Dictionary<string, int> GetCurrentStatePlayerRankings(string accountId, string? songId = null);
    Dictionary<string, int> GetPlayerRankingsFiltered(string accountId, Dictionary<string, int> maxScores, string? songId = null);
    Dictionary<string, int> GetCurrentStatePlayerRankingsFiltered(string accountId, Dictionary<string, int> maxScores, string? songId = null);
    int GetRankForScore(string songId, int score, int? maxScore = null);
    int GetCurrentStateRankForScore(string songId, int score, int? maxScore = null);
    (int TotalCount, int? MaxScore, int? MinScrapeScore) GetCurrentStateRankOffsetCoverage(string songId);
    Dictionary<string, int> GetFilteredEntryCounts(Dictionary<string, int> maxScores);
    Dictionary<string, int> GetCurrentStateFilteredEntryCounts(Dictionary<string, int> maxScores);
    Dictionary<string, (int Rank, int Total)> GetPlayerStoredRankings(string accountId, string? songId = null);

    // ── Rank computation ─────────────────────────────────────────────
    int RecomputeAllRanks();
    int RecomputeRanksForSong(string songId);
    int RecomputeRanksForSongs(IReadOnlyCollection<string> songIds);

    // ── Pruning ──────────────────────────────────────────────────────
    int PruneExcessEntries(string songId, int maxEntries, IReadOnlySet<string> preserveAccountIds, int? overThresholdScore = null);
    int PruneAllSongs(int maxEntriesPerSong, IReadOnlySet<string> preserveAccountIds, IReadOnlyDictionary<string, int>? songThresholds = null);

    // ── Threshold band queries (for precomputation) ────────────────
    /// <summary>
    /// Returns all distinct scores in the [lowerBound, upperBound] band for a given song, sorted ascending.
    /// Used for population tier and rank tier precomputation.
    /// </summary>
    List<int> GetScoresInBand(string songId, int lowerBound, int upperBound);

    /// <summary>
    /// Returns the count of entries with score &lt;= <paramref name="threshold"/> for a given song.
    /// </summary>
    int GetPopulationAtOrBelow(string songId, int threshold);

    List<int> GetCurrentStateScoresInBand(string songId, int lowerBound, int upperBound);

    int GetCurrentStatePopulationAtOrBelow(string songId, int threshold);

    // ── Song stats ───────────────────────────────────────────────────
    int ComputeSongStats(Dictionary<string, int?>? maxScoresByInstrument = null, Dictionary<string, long>? realPopulation = null);
    int ComputeCurrentStateSongStats(
        Dictionary<string, int?>? maxScoresByInstrument = null,
        IReadOnlyDictionary<string, long>? realPopulation = null,
        bool preserveExistingEntryCount = true);
    int ComputeCurrentStateSongStats(
        Dictionary<string, int?>? maxScoresByInstrument,
        IReadOnlyDictionary<string, long>? realPopulation,
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        bool preserveExistingEntryCount = true);
    int ReplaceCurrentStateSongStatsForMaxScoreMaintenance(
        IReadOnlyDictionary<string, int?> maxScoresByInstrument,
        IReadOnlyDictionary<string, long> publicationPopulation,
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction);
    List<(string AccountId, string SongId)> GetOverThresholdEntries();
    List<(string AccountId, string SongId)> GetCurrentStateOverThresholdEntries();
    List<(string AccountId, string SongId)>
        GetCurrentStateOverThresholdEntries(
            Npgsql.NpgsqlConnection connection,
            Npgsql.NpgsqlTransaction transaction);
    void PopulateValidScoreOverrides(IReadOnlyList<(string SongId, string AccountId, int Score, int? Accuracy, bool? IsFullCombo, int? Stars)> overrides);
    void PopulateValidScoreOverrides(
        IReadOnlyList<(string SongId, string AccountId, int Score, int? Accuracy, bool? IsFullCombo, int? Stars)> overrides,
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction);

    // ── Account rankings ─────────────────────────────────────────────
    int ComputeAccountRankings(int totalChartedSongs, int credibilityThreshold = 50, double populationMedian = 0.5, double thresholdMultiplier = 1.05);
    int SnapshotRankHistory(int retentionDays = 365, bool cleanupRetention = true);
    int CleanupRankHistoryRetention(int retentionDays = 365, int batchSize = 5000, int maxBatches = 1);
    (List<AccountRankingDto> Entries, int TotalCount) GetAccountRankings(string rankBy = "adjusted", int page = 1, int pageSize = 50);
    List<AccountRankingDto> GetAllAccountRankings();
    AccountRankingDto? GetAccountRanking(string accountId);
    (List<AccountRankingDto> Above, AccountRankingDto? Self, List<AccountRankingDto> Below) GetAccountRankingNeighborhood(string accountId, int radius = 5, string rankBy = "totalscore");
    List<RankHistoryDto> GetRankHistory(string accountId, int days = 30);
    int GetRankedAccountCount();
    int GetTotalChartedSongs();
    List<(string AccountId, double AdjustedSkillRating, int SongsPlayed, int AdjustedSkillRank)> GetAllRankingSummaries();
    List<(string AccountId, double AdjustedSkillRating, double WeightedRating, double FcRate, long TotalScore, double MaxScorePercent, int SongsPlayed, int FullComboCount)> GetAllRankingSummariesFull();
    List<AccountRankingSummary> GetAllRankingSummariesDetailed(
        int commandTimeoutSeconds = 0);

    // ── Materialized ranking pipeline ────────────────────────────────
    void MaterializeValidEntries(Npgsql.NpgsqlConnection conn, double baseThreshold);
    void MaterializeCurrentStateValidEntries(Npgsql.NpgsqlConnection conn, double baseThreshold);
    void MaterializeCurrentStateValidEntries(
        Npgsql.NpgsqlConnection conn,
        IReadOnlyCollection<string> currentCatalogSongIds,
        double baseThreshold);
    void MaterializeCurrentStateValidEntries(
        Npgsql.NpgsqlConnection conn,
        Npgsql.NpgsqlTransaction? transaction,
        double baseThreshold);
    void MaterializeCurrentStateValidEntries(
        Npgsql.NpgsqlConnection conn,
        Npgsql.NpgsqlTransaction? transaction,
        IReadOnlyCollection<string> currentCatalogSongIds,
        double baseThreshold);
    int ComputeAccountRankingsFromMaterialized(Npgsql.NpgsqlConnection conn, int totalChartedSongs,
        int credibilityThreshold, double populationMedian, double thresholdMultiplier);
    int ComputeAccountRankingsFromMaterialized(
        Npgsql.NpgsqlConnection conn,
        Npgsql.NpgsqlTransaction transaction,
        int totalChartedSongs,
        int credibilityThreshold,
        double populationMedian,
        double thresholdMultiplier);
    Npgsql.NpgsqlConnection OpenConnection();

}
