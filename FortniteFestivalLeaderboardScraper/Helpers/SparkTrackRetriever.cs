using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FortniteFestivalLeaderboardScraper.Helpers
{
    public class SparkTrackRetriever
    {
        public async Task<List<Song>> GetSparkTracks(List<LeaderboardData> previousData)
        {
            var client = new RestClient("https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/spark-tracks");
            var request = new RestRequest();
            request.Method = Method.Get;

            var res = await client.ExecuteAsync(request);

            var result = JsonConvert.DeserializeObject<JToken>(res.Content);
            var items = new List<Song>();

            foreach (var item in result.Children())
            {
                try
                {
                    var a = item.ToString().Substring(item.ToString().IndexOf('{'));
                    var parsedItem = JsonConvert.DeserializeObject<Song>(a);
                    parsedItem.isInLocalData = previousData.FindIndex(x => x.songId == parsedItem.track.su) >= 0 ? "✔" : "❌";
                    items.Add(parsedItem);
                } catch (Exception ex)
                {

                }
            }

            return items;
        }
    }
}
