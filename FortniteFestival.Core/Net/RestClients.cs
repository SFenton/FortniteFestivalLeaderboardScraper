using RestSharp;

namespace FortniteFestival.Core.Net
{
    public static class RestClients
    {
        public static readonly RestClient Events = new RestClient("https://events-public-service-live.ol.epicgames.com");
        public static readonly RestClient Content = new RestClient("https://fortnitecontent-website-prod07.ol.epicgames.com");
        public static readonly RestClient Account = new RestClient("https://account-public-service-prod.ol.epicgames.com");
    }
}
