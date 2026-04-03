using FSTService.Scraping;

namespace FSTService.Persistence;

/// <summary>
/// Abstraction over a per-instrument leaderboard database.
/// Implemented by InstrumentDatabase (SQLite) and PgInstrumentDatabase (PostgreSQL).
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
    (List<LeaderboardEntryDto> Entries, int TotalCount) GetLeaderboardWithCount(string songId, int? top = null, int offset = 0, int? maxScore = null);
    List<(string AccountId, int Rank, int Score)> GetNeighborhood(string songId, int centerRank, int rankRadius, string excludeAccountId);

    // ── Player queries ───────────────────────────────────────────────
    HashSet<string> GetSongIdsForAccount(string accountId);
    List<PlayerScoreDto> GetPlayerScoresForSongs(string accountId, IReadOnlyCollection<string> songIds);
    List<PlayerScoreDto> GetPlayerScores(string accountId, string? songId = null);
    Dictionary<string, int> GetPlayerRankings(string accountId, string? songId = null);
    Dictionary<string, int> GetPlayerRankingsFiltered(string accountId, Dictionary<string, int> maxScores, string? songId = null);
    int GetRankForScore(string songId, int score, int? maxScore = null);
    Dictionary<string, int> GetFilteredEntryCounts(Dictionary<string, int> maxScores);
    Dictionary<string, (int Rank, int Total)> GetPlayerStoredRankings(string accountId, string? songId = null);

    // ── Rank computation ─────────────────────────────────────────────
    int RecomputeAllRanks();
    int RecomputeRanksForSong(string songId);

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

    // ── Song stats ───────────────────────────────────────────────────
    int ComputeSongStats(Dictionary<string, int?>? maxScoresByInstrument = null, Dictionary<string, long>? realPopulation = null);
    List<(string AccountId, string SongId)> GetOverThresholdEntries();
    void PopulateValidScoreOverrides(IReadOnlyList<(string SongId, string AccountId, int Score, int? Accuracy, bool? IsFullCombo, int? Stars)> overrides);

    // ── Account rankings ─────────────────────────────────────────────
    int ComputeAccountRankings(int totalChartedSongs, int credibilityThreshold = 50, double populationMedian = 0.5);
    int SnapshotRankHistory(int topN, IReadOnlySet<string>? additionalAccountIds = null, int retentionDays = 365);
    (List<AccountRankingDto> Entries, int TotalCount) GetAccountRankings(string rankBy = "adjusted", int page = 1, int pageSize = 50);
    AccountRankingDto? GetAccountRanking(string accountId);
    (List<AccountRankingDto> Above, AccountRankingDto? Self, List<AccountRankingDto> Below) GetAccountRankingNeighborhood(string accountId, int radius = 5, string rankBy = "totalscore");
    List<RankHistoryDto> GetRankHistory(string accountId, int days = 30);
    int GetRankedAccountCount();
    List<(string AccountId, double AdjustedSkillRating, int SongsPlayed, int AdjustedSkillRank)> GetAllRankingSummaries();
    List<(string AccountId, double AdjustedSkillRating, double WeightedRating, double FcRate, long TotalScore, double MaxScorePercent, int SongsPlayed, int FullComboCount)> GetAllRankingSummariesFull();

    // ── Cache pre-warming ────────────────────────────────────────────
    void PreWarmRankingsBatch(IReadOnlyCollection<string> accountIds);

    // ── Maintenance ──────────────────────────────────────────────────
    void Checkpoint();
}
