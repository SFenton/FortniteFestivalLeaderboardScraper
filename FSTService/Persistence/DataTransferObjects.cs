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
    public double Percentile { get; init; }
    /// <summary>ISO 8601 timestamp when the session ended (from Epic API). Null for legacy data.</summary>
    public string? EndTime { get; init; }
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
    public double Percentile { get; init; }
    /// <summary>All-time rank from Epic API (0 = not yet enriched).</summary>
    public int Rank { get; init; }
    /// <summary>ISO 8601 timestamp when the session ended (from Epic API). Null for legacy data.</summary>
    public string? EndTime { get; init; }
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
/// DTO for an active user session from the UserSessions table.
/// </summary>
public sealed class UserSessionInfo
{
    public long Id { get; init; }
    public string Username { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string? Platform { get; init; }
    public DateTime IssuedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
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
    public double? Percentile { get; init; }
    public int? Season { get; init; }
    public string? ScoreAchievedAt { get; init; }
    public int? SeasonRank { get; init; }
    public int? AllTimeRank { get; init; }
}
