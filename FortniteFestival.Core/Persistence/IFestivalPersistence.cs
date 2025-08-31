using System.Collections.Generic;
using System.Threading.Tasks;

namespace FortniteFestival.Core.Persistence
{
    public interface IFestivalPersistence
    {
        Task<IList<LeaderboardData>> LoadScoresAsync();
        Task SaveScoresAsync(IEnumerable<LeaderboardData> scores);
        Task<IList<Song>> LoadSongsAsync();
        Task SaveSongsAsync(IEnumerable<Song> songs);
    }
}
