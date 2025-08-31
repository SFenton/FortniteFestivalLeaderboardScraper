using System;
using System.Collections.Generic;

namespace FortniteFestival.Core.Auth
{
    public class ExchangeCodeToken
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
    internal class ExchangeCodeResponse
    {
        public int expiresInSeconds { get; set; }
        public string code { get; set; }
        public string creatingClientId { get; set; }
    }
}
