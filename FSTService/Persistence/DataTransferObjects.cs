namespace FSTService.Persistence;

/// <summary>
/// DTO for a single leaderboard entry returned by the API.
/// </summary>
public sealed class LeaderboardEntryDto
{
    public string AccountId { get; init; } = "";
    public string? DisplayName { get; init; }
    public int Score { get; init; }
    public int Rank { get; init; }
    public int Accuracy { get; init; }
    public bool IsFullCombo { get; init; }
    public int Stars { get; init; }
    public int Season { get; init; }
    /// <summary>Epic difficulty level: 0 = Easy, 1 = Medium, 2 = Hard, 3 = Expert.</summary>
    public int Difficulty { get; init; }
    public double Percentile { get; init; }
    /// <summary>ISO 8601 timestamp when the session ended (from Epic API). Null for legacy data.</summary>
    public string? EndTime { get; init; }
    /// <summary>Real rank from Epic API (backfill/lookup). 0 = not set.</summary>
    public int ApiRank { get; init; }
    /// <summary>Origin: "scrape", "backfill", or "neighbor".</summary>
    public string Source { get; init; } = "scrape";
}

/// <summary>
/// DTO for a player's score on one song/instrument, used in player profile responses.
/// </summary>
public sealed class PlayerScoreDto
{
    public string SongId { get; init; } = "";
    public string Instrument { get; init; } = "";
    public int Score { get; init; }
    public int Accuracy { get; init; }
    public bool IsFullCombo { get; init; }
    public int Stars { get; init; }
    public int Season { get; init; }
    /// <summary>Epic difficulty level: 0 = Easy, 1 = Medium, 2 = Hard, 3 = Expert.</summary>
    public int Difficulty { get; init; }
    public double Percentile { get; init; }
    /// <summary>All-time rank from Epic API (0 = not yet enriched).</summary>
    public int Rank { get; init; }
    /// <summary>Real rank from Epic API (backfill/lookup). 0 = not set.</summary>
    public int ApiRank { get; init; }
    /// <summary>ISO 8601 timestamp when the session ended (from Epic API). Null for legacy data.</summary>
    public string? EndTime { get; init; }
}

/// <summary>
/// Best valid score from ScoreHistory for a player whose leaderboard entry exceeds the max-score threshold.
/// </summary>
public sealed class ValidScoreFallback
{
    public int Score { get; init; }
    public int? Accuracy { get; init; }
    public bool? IsFullCombo { get; init; }
    public int? Stars { get; init; }
}

/// <summary>
/// DTO for a score history entry.
/// </summary>
public sealed class ScoreHistoryEntry
{
    public string SongId { get; init; } = "";
    public string Instrument { get; init; } = "";
    public int? OldScore { get; init; }
    public int NewScore { get; init; }
    public int? OldRank { get; init; }
    public int NewRank { get; init; }
    /// <summary>Accuracy percentage at the time of this score (snapshot).</summary>
    public int? Accuracy { get; init; }
    /// <summary>Whether a full combo was achieved (snapshot).</summary>
    public bool? IsFullCombo { get; init; }
    /// <summary>Star rating achieved (snapshot — difficulty level).</summary>
    public int? Stars { get; init; }
    /// <summary>Epic difficulty level at the time of this score: 0 = Easy, 1 = Medium, 2 = Hard, 3 = Expert.</summary>
    public int? Difficulty { get; init; }
    /// <summary>Percentile ranking at the time of this score (snapshot).</summary>
    public double? Percentile { get; init; }
    /// <summary>Season in which this score was set (snapshot).</summary>
    public int? Season { get; init; }
    /// <summary>ISO 8601 timestamp when the session ended (from Epic API). Null for legacy or live-detected entries.</summary>
    public string? ScoreAchievedAt { get; init; }
    /// <summary>Player's rank on the seasonal leaderboard at the time of this score. Populated for history-reconstructed entries.</summary>
    public int? SeasonRank { get; init; }
    /// <summary>Player's rank on the all-time leaderboard at the time of this score. Populated for live-detected and backfill entries.</summary>
    public int? AllTimeRank { get; init; }
    public string ChangedAt { get; init; } = "";
}

/// <summary>
/// DTO for registered user information.
/// </summary>
public sealed class RegisteredUserInfo
{
    public string AccountId { get; init; } = "";
    public string? DisplayName { get; init; }
    public string RegisteredAt { get; init; } = "";
    public string? LastLoginAt { get; init; }
}

/// <summary>
/// DTO for backfill tracking status.
/// </summary>
public sealed class BackfillStatusInfo
{
    public string AccountId { get; init; } = "";
    public string Status { get; init; } = "pending";
    public int SongsChecked { get; init; }
    public int EntriesFound { get; init; }
    public int TotalSongsToCheck { get; init; }
    public string? StartedAt { get; init; }
    public string? CompletedAt { get; init; }
    public string? LastResumedAt { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// DTO for history reconstruction tracking status.
/// </summary>
public sealed class HistoryReconStatusInfo
{
    public string AccountId { get; init; } = "";
    public string Status { get; init; } = "pending";
    public int SongsProcessed { get; init; }
    public int TotalSongsToProcess { get; init; }
    public int SeasonsQueried { get; init; }
    public int HistoryEntriesFound { get; init; }
    public string? StartedAt { get; init; }
    public string? CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// DTO for a discovered season window.
/// </summary>
public sealed class SeasonWindowInfo
{
    public int SeasonNumber { get; init; }
    public string EventId { get; init; } = "";
    public string WindowId { get; init; } = "";
    public string DiscoveredAt { get; init; } = "";
}

/// <summary>
/// DTO for pre-computed player statistics (one per instrument, plus an "Overall" row).
/// </summary>
public sealed class PlayerStatsDto
{
    public string AccountId { get; init; } = "";
    public string Instrument { get; init; } = "";
    public int SongsPlayed { get; init; }
    public int FullComboCount { get; init; }
    public int GoldStarCount { get; init; }
    public double AvgAccuracy { get; init; }
    public int BestRank { get; init; }
    public string? BestRankSongId { get; init; }
    public long TotalScore { get; init; }
    /// <summary>JSON-encoded percentile distribution, e.g. {"1":5,"5":12,...}</summary>
    public string? PercentileDist { get; init; }
    /// <summary>Average percentile across songs played, e.g. "Top 3%".</summary>
    public string? AvgPercentile { get; init; }
    /// <summary>Overall percentile (unplayed songs count as 100%), e.g. "Top 15%".</summary>
    public string? OverallPercentile { get; init; }
}

/// <summary>
/// DTO for batch-inserting score changes via <see cref="MetaDatabase.InsertScoreChanges"/>.
/// </summary>
public sealed class ScoreChangeRecord
{
    public required string SongId { get; init; }
    public required string Instrument { get; init; }
    public required string AccountId { get; init; }
    public int? OldScore { get; init; }
    public required int NewScore { get; init; }
    public int? OldRank { get; init; }
    public required int NewRank { get; init; }
    public int? Accuracy { get; init; }
    public bool? IsFullCombo { get; init; }
    public int? Stars { get; init; }
    public int? Difficulty { get; init; }
    public double? Percentile { get; init; }
    public int? Season { get; init; }
    public string? ScoreAchievedAt { get; init; }
    public int? SeasonRank { get; init; }
    public int? AllTimeRank { get; init; }
}

/// <summary>
/// DTO for rivals computation status tracking.
/// </summary>
public sealed class RivalsStatusInfo
{
    public string AccountId { get; init; } = "";
    public string Status { get; init; } = "pending";
    public int CombosComputed { get; init; }
    public int TotalCombosToCompute { get; init; }
    public int RivalsFound { get; init; }
    public string? StartedAt { get; init; }
    public string? CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Row in the <c>UserRivals</c> table — a single rival for a user on a specific instrument combo.
/// </summary>
public sealed class UserRivalRow
{
    public string UserId { get; init; } = "";
    public string RivalAccountId { get; init; } = "";
    public string InstrumentCombo { get; init; } = "";
    public string Direction { get; init; } = "";
    public double RivalScore { get; init; }
    public double AvgSignedDelta { get; init; }
    public int SharedSongCount { get; init; }
    public int AheadCount { get; init; }
    public int BehindCount { get; init; }
    public string ComputedAt { get; init; } = "";
}

/// <summary>
/// Row in the <c>RivalSongSamples</c> table — a song comparison between user and rival.
/// </summary>
public sealed class RivalSongSampleRow
{
    public string UserId { get; init; } = "";
    public string RivalAccountId { get; init; } = "";
    public string Instrument { get; init; } = "";
    public string SongId { get; init; } = "";
    public int UserRank { get; init; }
    public int RivalRank { get; init; }
    public int RankDelta { get; init; }
    public int? UserScore { get; init; }
    public int? RivalScore { get; init; }
}

/// <summary>
/// Summary of how many above/below rivals exist for a specific instrument combo.
/// </summary>
public sealed class RivalComboSummary
{
    public string InstrumentCombo { get; init; } = "";
    public int AboveCount { get; init; }
    public int BelowCount { get; init; }
}

/// <summary>
/// A song that one player has scored on but the other hasn't — used for rival song gap analysis.
/// </summary>
public sealed class SongGapEntry
{
    public string SongId { get; init; } = "";
    public string Instrument { get; init; } = "";
    public int Score { get; init; }
    public int Rank { get; init; }
}

// ─── Leaderboard Rivals DTOs ────────────────────────────────

/// <summary>
/// A precomputed leaderboard rival — a player near the user on the global ranking
/// for a given instrument and rank method, with per-song comparison aggregates.
/// </summary>
public sealed class LeaderboardRivalRow
{
    public string UserId { get; init; } = "";
    public string RivalAccountId { get; init; } = "";
    public string Instrument { get; init; } = "";
    public string RankMethod { get; init; } = "";
    public string Direction { get; init; } = "";
    public int UserRank { get; init; }
    public int RivalRank { get; init; }
    public int SharedSongCount { get; init; }
    public int AheadCount { get; init; }
    public int BehindCount { get; init; }
    public double AvgSignedDelta { get; init; }
    public string ComputedAt { get; init; } = "";
}

/// <summary>
/// Per-song comparison data for a leaderboard rival — same semantics as
/// <see cref="RivalSongSampleRow"/> but keyed by rank method.
/// </summary>
public sealed class LeaderboardRivalSongSampleRow
{
    public string UserId { get; init; } = "";
    public string RivalAccountId { get; init; } = "";
    public string Instrument { get; init; } = "";
    public string RankMethod { get; init; } = "";
    public string SongId { get; init; } = "";
    public int UserRank { get; init; }
    public int RivalRank { get; init; }
    public int RankDelta { get; init; }
    public int? UserScore { get; init; }
    public int? RivalScore { get; init; }
}

// ─── Rankings DTOs ──────────────────────────────────────────

/// <summary>
/// Per-instrument ranking data for a single account, from the <c>AccountRankings</c> table.
/// </summary>
public sealed class AccountRankingDto
{
    public string AccountId { get; init; } = "";
    public string? DisplayName { get; init; }
    public string Instrument { get; init; } = "";
    public int SongsPlayed { get; init; }
    public int TotalChartedSongs { get; init; }
    public double Coverage { get; init; }

    public double RawSkillRating { get; init; }
    public double AdjustedSkillRating { get; init; }
    public int AdjustedSkillRank { get; init; }

    public double WeightedRating { get; init; }
    public int WeightedRank { get; init; }

    public double FcRate { get; init; }
    public int FcRateRank { get; init; }

    public long TotalScore { get; init; }
    public int TotalScoreRank { get; init; }

    public double MaxScorePercent { get; init; }
    public int MaxScorePercentRank { get; init; }

    public double AvgAccuracy { get; init; }
    public int FullComboCount { get; init; }
    public double AvgStars { get; init; }
    public int BestRank { get; init; }
    public double AvgRank { get; init; }

    public string ComputedAt { get; init; } = "";
    public int TotalRankedAccounts { get; init; }
}

/// <summary>
/// Cross-instrument composite ranking for a single account, from <c>CompositeRankings</c>.
/// </summary>
public sealed class CompositeRankingDto
{
    public string AccountId { get; init; } = "";
    public string? DisplayName { get; init; }
    public int InstrumentsPlayed { get; init; }
    public int TotalSongsPlayed { get; init; }
    public double CompositeRating { get; init; }
    public int CompositeRank { get; init; }

    public double? GuitarAdjustedSkill { get; init; }
    public int? GuitarSkillRank { get; init; }
    public double? BassAdjustedSkill { get; init; }
    public int? BassSkillRank { get; init; }
    public double? DrumsAdjustedSkill { get; init; }
    public int? DrumsSkillRank { get; init; }
    public double? VocalsAdjustedSkill { get; init; }
    public int? VocalsSkillRank { get; init; }
    public double? ProGuitarAdjustedSkill { get; init; }
    public int? ProGuitarSkillRank { get; init; }
    public double? ProBassAdjustedSkill { get; init; }
    public int? ProBassSkillRank { get; init; }

    public string ComputedAt { get; init; } = "";
    public int TotalRankedAccounts { get; init; }
}

/// <summary>
/// Daily rank snapshot for history tracking.
/// </summary>
public sealed class RankHistoryDto
{
    public string SnapshotDate { get; init; } = "";
    public int AdjustedSkillRank { get; init; }
    public int WeightedRank { get; init; }
    public int FcRateRank { get; init; }
    public int TotalScoreRank { get; init; }
    public int MaxScorePercentRank { get; init; }
    public double? AdjustedSkillRating { get; init; }
    public double? WeightedRating { get; init; }
    public double? FcRate { get; init; }
    public long? TotalScore { get; init; }
    public double? MaxScorePercent { get; init; }
    public int? SongsPlayed { get; init; }
    public double? Coverage { get; init; }
    public int? FullComboCount { get; init; }
}

/// <summary>
/// A single entry in a combo leaderboard.
/// </summary>
public sealed class ComboLeaderboardEntry
{
    public int Rank { get; init; }
    public string AccountId { get; init; } = "";
    public string? DisplayName { get; init; }
    public double AdjustedRating { get; init; }
    public double WeightedRating { get; init; }
    public double FcRate { get; init; }
    public long TotalScore { get; init; }
    public double MaxScorePercent { get; init; }
    public int SongsPlayed { get; init; }
    public int FullComboCount { get; init; }
    public string ComputedAt { get; init; } = "";
}

// ─── Rival Suggestions DTOs ────────────────────────────────

/// <summary>
/// A rival entry in the batch suggestions response,
/// containing the rival summary and their song samples for suggestion generation.
/// </summary>
public sealed class RivalSuggestionEntry
{
    public string AccountId { get; init; } = "";
    public string? DisplayName { get; init; }
    public string Direction { get; init; } = "";
    public int SharedSongCount { get; init; }
    public int AheadCount { get; init; }
    public int BehindCount { get; init; }
    public List<RivalSongSampleRow> Songs { get; init; } = [];
}
