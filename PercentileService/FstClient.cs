using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PercentileService;

/// <summary>
/// HTTP client that talks to FSTService to discover which songs/instruments
/// the account has scores for, and to POST leaderboard population data back.
/// </summary>
public sealed class FstClient
{
    private readonly HttpClient _http;
    private readonly PercentileOptions _opts;
    private readonly ILogger<FstClient> _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public FstClient(HttpClient http, IOptions<PercentileOptions> opts, ILogger<FstClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;

        _http.BaseAddress = new Uri(_opts.FstBaseUrl.TrimEnd('/') + "/");
    }

    /// <summary>
    /// Fetch the player profile from FSTService to discover song/instrument combos
    /// that have scores (and therefore can be queried for percentile).
    /// </summary>
    public async Task<List<PlayerEntry>> GetPlayerEntriesAsync(string accountId, CancellationToken ct)
    {
        var url = $"api/player/{accountId}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-API-Key", _opts.FstApiKey);

        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<PlayerProfileResponse>(JsonOpts, ct);
        if (body?.Scores is null)
            return [];

        return body.Scores
            .Where(s => s.Score > 0)
            .Select(s => new PlayerEntry
            {
                SongId = s.SongId,
                Instrument = s.Instrument,
            })
            .ToList();
    }

    /// <summary>
    /// POST leaderboard population data to FSTService.
    /// </summary>
    public async Task PostLeaderboardPopulationAsync(
        List<LeaderboardPopulationItem> items,
        CancellationToken ct)
    {
        if (items.Count == 0) return;

        var url = "api/leaderboard-population";
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(items, options: JsonOpts),
        };
        req.Headers.Add("X-API-Key", _opts.FstApiKey);

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            _log.LogError("Failed to POST leaderboard-population: {Status} {Body}",
                resp.StatusCode, errBody);
            resp.EnsureSuccessStatusCode();
        }

        _log.LogInformation("Posted {Count} leaderboard population entries to FSTService.", items.Count);
    }
}

// ── Response DTOs ──

public sealed class PlayerProfileResponse
{
    public string AccountId { get; set; } = "";
    public string? DisplayName { get; set; }
    public int TotalScores { get; set; }
    public List<PlayerScoreItem>? Scores { get; set; }
}

public sealed class PlayerScoreItem
{
    public string SongId { get; set; } = "";
    public string Instrument { get; set; } = "";
    public int Score { get; set; }
}

public sealed class PlayerEntry
{
    public string SongId { get; set; } = "";
    public string Instrument { get; set; } = "";
}

public sealed class LeaderboardPopulationItem
{
    [JsonPropertyName("songId")]
    public string SongId { get; set; } = "";

    [JsonPropertyName("instrument")]
    public string Instrument { get; set; } = "";

    [JsonPropertyName("totalEntries")]
    public long TotalEntries { get; set; }
}
