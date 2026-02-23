using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PercentileService;

/// <summary>
/// Calls Epic's V1 leaderboard API with <c>teamAccountIds</c> to get
/// real percentile values and derive total leaderboard population.
/// </summary>
public sealed class LeaderboardQuerier
{
    private const string EventsBase = "https://events-public-service-live.ol.epicgames.com";

    private readonly HttpClient _http;
    private readonly ILogger<LeaderboardQuerier> _log;

    public LeaderboardQuerier(HttpClient http, ILogger<LeaderboardQuerier> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Query a single song/instrument alltime leaderboard for the given account.
    /// Returns the entry with real percentile, or null if the account has no score.
    /// </summary>
    public async Task<PercentileEntry?> QueryAsync(
        string songId,
        string instrument,
        string accountId,
        string accessToken,
        CancellationToken ct = default)
    {
        // V1 URL: alltime_{songId}_{instrument} / alltime / {accountId}?teamAccountIds={accountId}
        var eventId = $"alltime_{songId}_{instrument}";
        var url = $"{EventsBase}/api/v1/leaderboards/FNFestival/{eventId}/alltime/{accountId}" +
                  $"?page=0&rank=0&teamAccountIds={accountId}&appId=Fortnite&showLiveSessions=false";

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "V1 request failed for {SongId}/{Instrument}", songId, instrument);
            return null;
        }

        if (!resp.IsSuccessStatusCode)
        {
            if ((int)resp.StatusCode == 404)
            {
                // No leaderboard for this song/instrument
                return null;
            }

            var errBody = await resp.Content.ReadAsStringAsync(ct);
            _log.LogWarning("V1 non-success for {SongId}/{Instrument}: {Status} {Body}",
                songId, instrument, resp.StatusCode, errBody);
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        if (!root.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
            return null;

        // Find our account's entry
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("teamAccountIds", out var teamIds) ||
                teamIds.ValueKind != JsonValueKind.Array ||
                teamIds.GetArrayLength() == 0)
                continue;

            var teamId = teamIds[0].GetString();
            if (!string.Equals(teamId, accountId, StringComparison.OrdinalIgnoreCase))
                continue;

            var rank = entry.GetProperty("rank").GetInt32();
            var score = entry.GetProperty("score").GetInt64();
            var percentile = entry.GetProperty("percentile").GetDouble();

            // Derive total entries: totalEntries ≈ rank / percentile
            long totalEntries = -1;
            if (percentile > 0)
            {
                totalEntries = (long)Math.Round(rank / percentile);
            }

            return new PercentileEntry
            {
                SongId = songId,
                Instrument = instrument,
                Rank = rank,
                Score = score,
                Percentile = percentile,
                TotalEntries = totalEntries,
            };
        }

        return null;
    }
}

public sealed class PercentileEntry
{
    public string SongId { get; init; } = "";
    public string Instrument { get; init; } = "";
    public int Rank { get; init; }
    public long Score { get; init; }
    public double Percentile { get; init; }
    public long TotalEntries { get; init; }
}
