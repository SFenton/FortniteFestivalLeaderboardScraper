using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

// Config
var accountId = "195e93ef108143b2975ee46662d4d0e1"; // SFentonX
var songSu = "aa144fd6-c51c-49b8-8798-1ad23f60d41f"; // The Night Porter
var instrument = "Solo_Drums";
var seasonNum = "013";

// Switch client — supports device_code, has FNFestival permissions
var switchClientId = "98f7e42c2e3a4f86a74eb43fbb41ed39";
var switchClientSecret = "0a2449a2-001a-451e-afec-3e812901c4d7";
var switchAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{switchClientId}:{switchClientSecret}"));
const string accountBase = "https://account-public-service-prod.ol.epicgames.com";

using var http = new HttpClient();

// Step 1: Get a client_credentials token
Console.WriteLine("Step 1: Getting client_credentials token...");
var ccReq = new HttpRequestMessage(HttpMethod.Post, $"{accountBase}/account/api/oauth/token");
ccReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", switchAuth);
ccReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"] = "client_credentials",
});
var ccResp = await http.SendAsync(ccReq);
var ccBody = await ccResp.Content.ReadAsStringAsync();
if (!ccResp.IsSuccessStatusCode) { Console.WriteLine($"Failed: {ccResp.StatusCode}\n{ccBody}"); return; }
var ccToken = JsonDocument.Parse(ccBody).RootElement.GetProperty("access_token").GetString()!;

// Step 2: Start device_code flow
Console.WriteLine("\nStep 2: Starting device_code flow...");
var dcReq = new HttpRequestMessage(HttpMethod.Post, $"{accountBase}/account/api/oauth/deviceAuthorization");
dcReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ccToken);
dcReq.Content = new FormUrlEncodedContent([]);
var dcResp = await http.SendAsync(dcReq);
var dcBody = await dcResp.Content.ReadAsStringAsync();
if (!dcResp.IsSuccessStatusCode) { Console.WriteLine($"Device auth failed: {dcResp.StatusCode}\n{dcBody}"); return; }

var dcDoc = JsonDocument.Parse(dcBody);
var deviceCode = dcDoc.RootElement.GetProperty("device_code").GetString()!;
var userCode = dcDoc.RootElement.GetProperty("user_code").GetString()!;
var verifyUri = dcDoc.RootElement.GetProperty("verification_uri_complete").GetString()!;
var expiresIn = dcDoc.RootElement.GetProperty("expires_in").GetInt32();
var interval = dcDoc.RootElement.GetProperty("interval").GetInt32();

Console.WriteLine($"\n╔══════════════════════════════════════╗");
Console.WriteLine($"║  GO TO: {verifyUri}");
Console.WriteLine($"║  CODE:  {userCode}");
Console.WriteLine($"║  Expires in {expiresIn} seconds");
Console.WriteLine($"╚══════════════════════════════════════╝\n");

System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(verifyUri) { UseShellExecute = true });

// Step 3: Poll until user authorizes
Console.Write("Waiting for approval");
var deadline = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
var pollInterval = TimeSpan.FromSeconds(Math.Max(interval, 5));
string? gameToken = null;
string? gameAcctId = null;

while (DateTimeOffset.UtcNow < deadline)
{
    await Task.Delay(pollInterval);
    Console.Write(".");

    var pollReq = new HttpRequestMessage(HttpMethod.Post, $"{accountBase}/account/api/oauth/token");
    pollReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", switchAuth);
    pollReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "device_code",
        ["device_code"] = deviceCode,
    });

    var pollResp = await http.SendAsync(pollReq);
    var pollBody = await pollResp.Content.ReadAsStringAsync();

    if (pollResp.IsSuccessStatusCode)
    {
        var pollDoc = JsonDocument.Parse(pollBody);
        gameToken = pollDoc.RootElement.GetProperty("access_token").GetString()!;
        gameAcctId = pollDoc.RootElement.GetProperty("account_id").GetString()!;
        var displayName = pollDoc.RootElement.TryGetProperty("displayName", out var dn) ? dn.GetString() : "unknown";
        Console.WriteLine($"\n\nAuthorized as {displayName} ({gameAcctId})");
        break;
    }

    if (pollBody.Contains("authorization_pending")) continue;
    Console.WriteLine($"\nError: {pollResp.StatusCode}\n{pollBody}");
    return;
}

if (gameToken is null) { Console.WriteLine("\nTimeout — not approved in time."); return; }

// Step 4: Call V1 with teamAccountIds — query SFentonX's own entry
var targetAccountId = "195e93ef108143b2975ee46662d4d0e1"; // SFentonX (self)
var eventId = $"season{seasonNum}_{songSu}";
var windowId = $"{songSu}_{instrument}";
var url = $"https://events-public-service-live.ol.epicgames.com/api/v1/leaderboards/FNFestival/{eventId}/{windowId}/{gameAcctId}?teamAccountIds={targetAccountId}&appId=Fortnite&showLiveSessions=false";

Console.WriteLine($"\nStep 4: Calling V1 for SFentonX — The Night Porter / Solo_Drums / Season 013...\n");
Console.WriteLine($"URL: {url}\n");

var apiReq = new HttpRequestMessage(HttpMethod.Get, url);
apiReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gameToken);
var apiResp = await http.SendAsync(apiReq);
Console.WriteLine($"V1 Status: {apiResp.StatusCode}");
var apiBody = await apiResp.Content.ReadAsStringAsync();

// Dump full pretty-printed JSON
try
{
    var parsed = JsonDocument.Parse(apiBody);
    var pretty = JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(pretty);
}
catch (Exception ex)
{
    Console.WriteLine($"Parse error: {ex.Message}");
    Console.WriteLine(apiBody.Length > 4000 ? apiBody[..4000] : apiBody);
}
