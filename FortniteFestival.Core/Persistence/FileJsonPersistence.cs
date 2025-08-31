using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FortniteFestival.Core;
using Newtonsoft.Json;

namespace FortniteFestival.Core.Persistence
{
    public class FileJsonPersistence : IFestivalPersistence
    {
        private readonly string _path;
        public FileJsonPersistence(string path) { _path = path; }
        public Task<IList<LeaderboardData>> LoadScoresAsync()
        {
            try
            {
                if (!File.Exists(_path)) return Task.FromResult<IList<LeaderboardData>>(new List<LeaderboardData>());
                var json = File.ReadAllText(_path);
                var list = JsonConvert.DeserializeObject<List<LeaderboardData>>(json) ?? new List<LeaderboardData>();
                return Task.FromResult<IList<LeaderboardData>>(list);
            }
            catch { return Task.FromResult<IList<LeaderboardData>>(new List<LeaderboardData>()); }
        }
        public Task SaveScoresAsync(IEnumerable<LeaderboardData> scores)
        {
            try { File.WriteAllText(_path, JsonConvert.SerializeObject(scores.ToList(), Formatting.Indented)); } catch { }
            return Task.CompletedTask;
        }
        public Task<IList<Song>> LoadSongsAsync(){ return Task.FromResult<IList<Song>>(new List<Song>()); }
        public Task SaveSongsAsync(IEnumerable<Song> songs){ return Task.CompletedTask; }
    }
}
