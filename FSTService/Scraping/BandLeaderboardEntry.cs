namespace FSTService.Scraping;

/// <summary>
/// A single band team's entry on a global leaderboard.
/// Unlike <see cref="LeaderboardEntry"/> (single account), band entries
/// represent a team of 2–4 players with per-member breakdown.
/// </summary>
public sealed class BandLeaderboardEntry
{
    /// <summary>
    /// Canonical team key: account IDs sorted lexicographically and colon-joined.
    /// Epic's <c>teamId</c> ordering is NOT deterministic, so we normalize server-side.
    /// </summary>
    public string TeamKey { get; set; } = "";

    /// <summary>
    /// Team member account IDs in Epic's original order (preserved for reference).
    /// </summary>
    public string[] TeamMembers { get; set; } = [];

    /// <summary>Band total score (<c>B_SCORE</c> or top-level <c>SCORE</c>).</summary>
    public int Score { get; set; }

    /// <summary>Sum of member base scores before bonuses (<c>B_BASESCORE</c>). Null until enriched.</summary>
    public int? BaseScore { get; set; }

    /// <summary>Instrument diversity bonus (<c>B_INSTRUMENT_BONUS</c>). Null until enriched.</summary>
    public int? InstrumentBonus { get; set; }

    /// <summary>Overdrive bonus (<c>B_OVERDRIVE_BONUS</c>). Null until enriched.</summary>
    public int? OverdriveBonus { get; set; }

    /// <summary>Band average accuracy in millionths (<c>B_ACCURACY</c> or <c>ACCURACY</c>).</summary>
    public int Accuracy { get; set; }

    /// <summary>True if ALL members achieved a full combo (<c>B_FULL_COMBO</c> = 1).</summary>
    public bool IsFullCombo { get; set; }

    /// <summary>Band star rating (<c>B_STARS</c> or <c>STARS_EARNED</c>).</summary>
    public int Stars { get; set; }

    /// <summary>Max of member difficulties (<c>DIFFICULTY</c>).</summary>
    public int Difficulty { get; set; }

    /// <summary>Season number.</summary>
    public int Season { get; set; }

    /// <summary>Rank on the leaderboard.</summary>
    public int Rank { get; set; }

    /// <summary>Percentile on the leaderboard.</summary>
    public double Percentile { get; set; }

    /// <summary>ISO 8601 timestamp when the session ended.</summary>
    public string? EndTime { get; set; }

    /// <summary>Origin: "scrape", "enrichment", or "findteams".</summary>
    public string Source { get; set; } = "scrape";

    /// <summary>
    /// Per-member stats extracted from <c>trackedStats</c>.
    /// Populated during V1 parsing (for CHOpt validation) and V2 enrichment.
    /// </summary>
    public List<BandMemberStats> MemberStats { get; set; } = [];

    /// <summary>
    /// True if any member's individual score exceeds CHOpt threshold for their instrument.
    /// Used for client-side filtering — over-threshold entries are still persisted.
    /// </summary>
    public bool IsOverThreshold { get; set; }
}

/// <summary>
/// Per-member stats extracted from band entry <c>trackedStats</c>.
/// </summary>
public sealed class BandMemberStats
{
    /// <summary>0-based member index from <c>M_{i}_*</c> fields.</summary>
    public int MemberIndex { get; set; }

    /// <summary>Account ID from <c>M_{i}_ID_{accountId}</c>.</summary>
    public string AccountId { get; set; } = "";

    /// <summary>Epic numeric instrument ID (0=Guitar, 1=Bass, 2=Vocals, 3=Drums, 4=ProGuitar, 5=ProBass).</summary>
    public int InstrumentId { get; set; }

    /// <summary>Individual contribution score.</summary>
    public int Score { get; set; }

    /// <summary>Individual accuracy in millionths.</summary>
    public int Accuracy { get; set; }

    /// <summary>Whether this member achieved a full combo.</summary>
    public bool IsFullCombo { get; set; }

    /// <summary>Individual stars earned.</summary>
    public int Stars { get; set; }

    /// <summary>Individual difficulty level.</summary>
    public int Difficulty { get; set; }
}
