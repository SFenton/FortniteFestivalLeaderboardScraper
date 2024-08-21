using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using static FortniteFestivalLeaderboardScraper.Helpers.LeaderboardAPI;

namespace FortniteFestivalLeaderboardScraper.Helpers.FileIO
{
    public static class JSONReadWrite
    {
        public static bool WriteLeaderboardJSON(List<LeaderboardData> leaderboardEntries)
        {
            try
            {
                var exePath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
                var json = JsonConvert.SerializeObject(leaderboardEntries, Formatting.Indented);
                Console.WriteLine(exePath);

                File.WriteAllText(exePath + "\\FNFLS_data.json", json);
            } catch (Exception e)
            {
                return false;
            }

            return true;
        }

        public static List<LeaderboardData> ReadLeaderboardJSON()
        {
            try
            {
                var exePath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
                var str = File.ReadAllText(exePath + "\\FNFLS_data.json");
                var scores = JsonConvert.DeserializeObject<List<LeaderboardData>>(str);
                
                return scores;
            }
            catch (Exception e)
            {
                return new List<LeaderboardData>();
            }
        }
    }
}
