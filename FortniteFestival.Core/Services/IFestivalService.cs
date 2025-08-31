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
        event Action<int,int,string,bool> SongProgress; // current, total, title, started(true)/completed(false)
        Task InitializeAsync(); // load DB, initial song sync
        Task SyncSongsAsync();
        Task<bool> FetchScoresAsync(string exchangeCode, int degreeOfParallelism, IList<string> filteredSongIds, Settings settings);
    }
}
