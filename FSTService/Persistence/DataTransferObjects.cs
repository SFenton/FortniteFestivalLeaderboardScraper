namespace FSTService.Persistence;

/// <summary>
/// DTO for a single leaderboard entry returned by the API.
/// </summary>
public sealed class LeaderboardEntryDto
{
    public string AccountId { get; init; } = "";
    public string? DisplayName { get; init; }
    public int Score { get; init; }
    public int Accuracy { get; init; }
    public bool IsFullCombo { get; init; }
    public int Stars { get; init; }
    public int Season { get; init; }
    public double Percentile { get; init; }
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
    public string ChangedAt { get; init; } = "";
}
