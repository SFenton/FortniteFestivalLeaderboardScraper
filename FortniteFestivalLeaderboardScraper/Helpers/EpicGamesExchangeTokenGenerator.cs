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
    public class EpicGamesExchangeTokenGenerator
    {
        public async Task<Tuple<bool, ExchangeCodeObject>> GetTokenWithPermissions(string initialToken)
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
