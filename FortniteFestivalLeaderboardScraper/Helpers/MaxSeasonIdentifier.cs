using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FortniteFestivalLeaderboardScraper.Helpers
{
    public class MaxSeasonIdentifier
    {
        public async Task<int> GetMaxSeason(string accessToken)
        {
            var calendarClient = new RestClient("https://fngw-mcp-gc-livefn.ol.epicgames.com/fortnite/api/calendar/v1/timeline");
            var request = new RestRequest();
            request.Method = Method.Get;
            request.AddHeader("Authorization", "bearer " + accessToken);

            var result = await calendarClient.ExecuteAsync(request);

            var serialized = JsonConvert.DeserializeObject<CalendarResponse>(result.Content);

            return serialized.channels.clientevents.states[0].state.seasonNumber;
        }
    }
}
