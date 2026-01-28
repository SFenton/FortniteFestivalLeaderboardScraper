using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FortniteFestival.Core.Config;

namespace FortniteFestival.Core.Services
{
    public interface IFestivalService
    {
        IReadOnlyList<Song> Songs { get; }
        IReadOnlyDictionary<string, LeaderboardData> ScoresIndex { get; }
        bool IsFetching { get; }
        event Action<string> Log; // log line
        event Action<string> SongAvailabilityChanged; // songId
        event Action<LeaderboardData> ScoreUpdated; // per song
        event Action<int, int, string, bool> SongProgress; // current, total, title, started(true)/completed(false)

        // Per-song update tracking
        event Action<string> SongUpdateStarted; // songId - fired when a song starts updating
        event Action<string> SongUpdateCompleted; // songId - fired when a song finishes updating
        
        /// <summary>
        /// Checks if a song has been completed in the current fetch pass.
        /// </summary>
        bool IsSongCompletedThisPass(string songId);
        
        /// <summary>
        /// Checks if a song is currently being updated.
        /// </summary>
        bool IsSongUpdating(string songId);
        
        /// <summary>
        /// Prioritizes a song to be fetched next (moves it to front of queue).
        /// Returns true if the song was found and prioritized.
        /// </summary>
        bool PrioritizeSong(string songId);

        // Returns instrumentation counters: improved scores, empty leaderboards, errors, total requests, total bytes, elapsed seconds
        (
            long improved,
            long empty,
            long errors,
            long requests,
            long bytes,
            double elapsedSec
        ) GetInstrumentation();
        Task InitializeAsync(); // load DB, initial song sync
        Task SyncSongsAsync();
        Task<bool> FetchScoresAsync(
            string exchangeCode,
            int degreeOfParallelism,
            IList<string> filteredSongIds,
            Settings settings
        );
        Task<bool> FetchScoresAsync(
            string exchangeCode,
            int degreeOfParallelism,
            IList<string> filteredSongIds,
            IEnumerable<InstrumentType> instruments,
            Settings settings
        );
    }
}
