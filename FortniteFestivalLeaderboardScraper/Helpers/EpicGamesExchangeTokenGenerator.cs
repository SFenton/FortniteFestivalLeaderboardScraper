using Newtonsoft.Json;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FortniteFestivalLeaderboardScraper.Helpers
{
    class ExchangeCodeObject
{
    public string access_token { get; set; }
    public int expires_in { get; set; }
    public DateTime expires_at { get; set; }
    public string token_type { get; set; }
    public string refresh_token { get; set; }
    public int refresh_expires { get; set; }
    public DateTime refresh_expires_at { get; set; }
    public string account_id { get; set; }
    public string client_id { get; set; }
    public bool internal_client { get; set; }
    public string client_service { get; set; }
    public List<string> scope { get; set; }
    public string displayName { get; set; }
    public string app { get; set; }
    public string in_app_id { get; set; }
    public string device_id { get; set; }
    public string product_id { get; set; }
    public string application_id { get; set; }
    public string acr { get; set; }
    public DateTime auth_time { get; set; }
}

class ExchangeCodeResponse
    {
        public int expiresInSeconds { get; set; }
        public string code { get; set; }
        public string creatingClientId { get; set; }
    }

static class EpicGamesExchangeTokenGenerator
    {
        public static async Task<Tuple<bool, ExchangeCodeObject>> GetTokenWithPermissions(string initialToken)
        {
            try
            {
            var client = new RestClient("https://account-public-service-prod.ol.epicgames.com/account/api/oauth/token");
            var request = new RestRequest();
            request.Method = Method.Post;
            request.AddHeader("Authorization", "basic ZWM2ODRiOGM2ODdmNDc5ZmFkZWEzY2IyYWQ4M2Y1YzY6ZTFmMzFjMjExZjI4NDEzMTg2MjYyZDM3YTEzZmM4NGQ=");
            request.AddHeader("Content-Type", "application/x-www-form-urlencoded");
            request.AddHeader("Accept-Encoding", "gzip, deflate, br");
            request.AddParameter("application/x-www-form-urlencoded", "grant_type=authorization_code&code=" + initialToken, ParameterType.RequestBody);

            var res = await client.ExecuteAsync(request);
            var exchangeJson = JsonConvert.DeserializeObject<ExchangeCodeObject>(res.Content);

            var exchangeAccessToken = exchangeJson.access_token;

            var exchangeClient = new RestClient("https://account-public-service-prod.ol.epicgames.com/account/api/oauth/exchange");
            var exchangeRequest = new RestRequest();
            exchangeRequest.Method = Method.Get;
            exchangeRequest.AddHeader("Authorization", "bearer " + exchangeAccessToken);
            request.AddHeader("Accept", "*/*");
            request.AddHeader("Accept-Encoding", "gzip, deflate, br");

            var exchangeRes = await exchangeClient.ExecuteAsync(exchangeRequest);
            var exchangeResponse = JsonConvert.DeserializeObject<ExchangeCodeResponse>(exchangeRes.Content);
            var exchangeCode = exchangeResponse.code;

            request = new RestRequest();
            request.Method = Method.Post;
            request.AddHeader("Authorization", "basic ZWM2ODRiOGM2ODdmNDc5ZmFkZWEzY2IyYWQ4M2Y1YzY6ZTFmMzFjMjExZjI4NDEzMTg2MjYyZDM3YTEzZmM4NGQ=");
            request.AddHeader("Content-Type", "application/x-www-form-urlencoded");
            request.AddHeader("Accept-Encoding", "gzip, deflate, br");
            request.AddParameter("application/x-www-form-urlencoded", "grant_type=exchange_code&token_type=eg1&exchange_code=" + exchangeCode, ParameterType.RequestBody);
            res = await client.ExecuteAsync(request);
            exchangeJson = JsonConvert.DeserializeObject<ExchangeCodeObject>(res.Content);

            return new Tuple<bool, ExchangeCodeObject>(true, exchangeJson);
        }
            catch (Exception ex)
            {
                return new Tuple<bool, ExchangeCodeObject>(false, new ExchangeCodeObject());
            }
        }
    }
}
