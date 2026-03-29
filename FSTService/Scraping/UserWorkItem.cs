using System.Collections.Concurrent;

namespace FSTService.Scraping;

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
/// Tracks what seasons and lookup types are needed and which songs have been processed.
/// </summary>
public sealed class UserWorkItem
{
    public required string AccountId { get; init; }

    /// <summary>Why this user is in the machine (can be a combination of purposes).</summary>
    public WorkPurpose Purposes { get; set; }

    /// <summary>
    /// Seasons to query for session history (e.g., {13} for post-scrape, {1..13} for full backfill).
    /// The machine queries each season in this set per song/instrument.
    /// </summary>
    public HashSet<int> SeasonsNeeded { get; init; } = [];

    /// <summary>Whether to perform alltime leaderboard lookups for this user.</summary>
    public bool AllTimeNeeded { get; set; }

    /// <summary>
    /// Song index (into the machine's song list) at which this user was added.
    /// The user is NOT processed for songs at or before this index during the first pass.
    /// The machine loops back to cover songs 0..StartingSongIndex after the first pass completes.
    /// Set to -1 for users added before the machine starts (process all songs on first pass).
    /// </summary>
    public int StartingSongIndex { get; set; } = -1;

    /// <summary>Number of songs fully processed for this user (across all passes).</summary>
    public int CompletedSongCount;

    /// <summary>
    /// Set of (SongId, Instrument) pairs already checked in a prior run (for resumption).
    /// Populated from BackfillProgress or HistoryReconProgress tables on startup.
    /// </summary>
    public HashSet<(string SongId, string Instrument)>? AlreadyChecked { get; set; }

    /// <summary>Whether this user needs work at the given song index.</summary>
    public bool NeedsWorkAtSongIndex(int songIndex)
    {
        // Users added before the machine (StartingSongIndex == -1) need all songs.
        if (StartingSongIndex < 0) return true;
        // Hot-added users: skip songs at or before their starting index on first pass.
        return songIndex > StartingSongIndex;
    }

    /// <summary>Whether the given (songId, instrument) was already processed in a prior run.</summary>
    public bool IsAlreadyChecked(string songId, string instrument)
        => AlreadyChecked?.Contains((songId, instrument)) == true;

    /// <summary>Total number of songs this user must be processed for.</summary>
    public int TotalSongsNeeded { get; set; }

    /// <summary>Whether all songs have been processed for this user.</summary>
    public bool IsComplete => CompletedSongCount >= TotalSongsNeeded;
}

/// <summary>
/// Thread-safe queue for hot-adding users to a running <see cref="SongProcessingMachine"/>.
/// </summary>
public sealed class UserWorkQueue
{
    private readonly ConcurrentQueue<UserWorkItem> _queue = new();

    /// <summary>Enqueue a user for processing. Thread-safe.</summary>
    public void Enqueue(UserWorkItem item) => _queue.Enqueue(item);

    /// <summary>Drain all pending items. Returns an empty list if none.</summary>
    public List<UserWorkItem> DrainAll()
    {
        var items = new List<UserWorkItem>();
        while (_queue.TryDequeue(out var item))
            items.Add(item);
        return items;
    }

    /// <summary>Number of pending items (approximate, for progress reporting).</summary>
    public int Count => _queue.Count;
}
