using RestSharp;

namespace FortniteFestivalLeaderboardScraper.Helpers.Net
{
    internal static class RestClients
    {
        // Shared RestSharp clients to reduce per-request allocations.
        // RestClient is thread-safe for concurrent ExecuteAsync calls.
        public static readonly RestClient EventsClient = new RestClient("https://events-public-service-live.ol.epicgames.com");
        public static readonly RestClient ContentClient = new RestClient("https://fortnitecontent-website-prod07.ol.epicgames.com");
    }
}
