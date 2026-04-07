namespace FSTService.Scraping;

/// <summary>Identifies the orchestrator that attached users to the CyclicalSongMachine.</summary>
public enum SongMachineSource
{
    PostScrape,
    Backfill,
    HistoryRecon,
    PlayerTrackCover,
}

/// <summary>
/// Specifies what kind of work the machine should perform for a user.
/// Flags allow a single user to participate in multiple work types simultaneously.
/// </summary>
[Flags]
public enum WorkPurpose
{
    /// <summary>Post-scrape refresh: alltime lookup + current season sessions.</summary>
    PostScrape = 1,

    /// <summary>Full backfill: alltime lookup for all songs (fills missing below-60K entries).</summary>
    Backfill = 2,

    /// <summary>History reconstruction: seasonal session queries to build complete score timeline.</summary>
    HistoryRecon = 4,
}

/// <summary>
/// Work specification for one user in the <see cref="SongProcessingMachine"/>.
/// Each machine instance processes ALL songs for ALL its users — no partial tracking needed.
/// </summary>
public sealed class UserWorkItem
{
    public required string AccountId { get; init; }

    /// <summary>Why this user is in the machine (can be a combination of purposes).</summary>
    public WorkPurpose Purposes { get; init; }

    /// <summary>
    /// Seasons to query for session history (e.g., {13} for post-scrape, {1..13} for full backfill).
    /// The machine queries each season in this set per song/instrument.
    /// </summary>
    public HashSet<int> SeasonsNeeded { get; init; } = [];

    /// <summary>Whether to perform alltime leaderboard lookups for this user.</summary>
    public bool AllTimeNeeded { get; init; }

    /// <summary>
    /// Set of (SongId, Instrument) pairs already checked in a prior run (for resumption).
    /// Populated from BackfillProgress or HistoryReconProgress tables on startup.
    /// </summary>
    public HashSet<(string SongId, string Instrument)>? AlreadyChecked { get; init; }

    /// <summary>Whether the given (songId, instrument) was already processed in a prior run.</summary>
    public bool IsAlreadyChecked(string songId, string instrument)
        => AlreadyChecked?.Contains((songId, instrument)) == true;
}
