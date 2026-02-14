using FortniteFestival.Core;
using FortniteFestival.Core.Auth;
using FortniteFestival.Core.Config;
using FortniteFestival.Core.Services;
using FortniteFestival.Core.Persistence;
using FSTService.Auth;
using FSTService.Scraping;
using Microsoft.Extensions.Options;

namespace FSTService;

/// <summary>
/// Background worker that continuously scrapes Fortnite Festival leaderboard scores.
///
/// Lifecycle:
///   1. Ensure authenticated (device auth → refresh → device code setup)
///   2. Initialize FestivalService (song catalog, images)
///   3. Fetch scores for all songs/instruments
///   4. Sleep for configured interval
///   5. Repeat
/// </summary>
public sealed class ScraperWorker : BackgroundService
{
    private readonly TokenManager _tokenManager;
    private readonly GlobalLeaderboardScraper _globalScraper;
    private readonly IOptions<ScraperOptions> _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ScraperWorker> _log;

    public ScraperWorker(
        TokenManager tokenManager,
        GlobalLeaderboardScraper globalScraper,
        IOptions<ScraperOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<ScraperWorker> log)
    {
        _tokenManager = tokenManager;
        _globalScraper = globalScraper;
        _options = options;
        _lifetime = lifetime;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "ScraperWorker failed with an unhandled exception.");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;

        // --setup mode: only do device code auth, then exit
        if (opts.SetupOnly)
        {
            _log.LogInformation("Running in --setup mode (device code authentication only).");
            var ok = await _tokenManager.PerformDeviceCodeSetupAsync(stoppingToken);
            _log.LogInformation(ok ? "Setup complete! You can now run the service normally."
                                   : "Setup failed. Please try again.");
            return;
        }

        _log.LogInformation("ScraperWorker starting. Interval={Interval}, DOP={Dop}",
            opts.ScrapeInterval, opts.DegreeOfParallelism);

        // Ensure we have a valid auth session before entering the loop
        if (!await EnsureAuthenticatedAsync(stoppingToken))
        {
            return;
        }

        // Create the persistence layer
        var dbPath = Path.GetFullPath(opts.DatabasePath);
        var dbDir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
            Directory.CreateDirectory(dbDir);

        var persistence = new SqlitePersistence(dbPath);
        var service = new FestivalService(persistence);

        service.Log += msg => _log.LogInformation("[Core] {Message}", msg);

        // Initialize: fetch song catalog, images, load cached scores
        _log.LogInformation("Initializing song catalog...");
        await service.InitializeAsync();
        _log.LogInformation("Initialization complete. {SongCount} songs loaded.", service.Songs.Count);

        // --test mode: fetch one song and exit
        if (!string.IsNullOrEmpty(opts.TestSongQuery))
        {
            await RunSingleSongTestAsync(service, opts, stoppingToken);
            return;
        }

        // Main scrape loop
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunScrapePassAsync(service, opts, stoppingToken);

            _log.LogInformation("Next scrape in {Interval}. Sleeping...", opts.ScrapeInterval);
            try
            {
                await Task.Delay(opts.ScrapeInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log.LogInformation("ScraperWorker stopping.");
    }

    // ─── Auth helpers ───────────────────────────────────────────

    private async Task<bool> EnsureAuthenticatedAsync(CancellationToken ct)
    {
        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is not null)
            return true;

        _log.LogWarning("No stored credentials. Running interactive device code setup...");
        var ok = await _tokenManager.PerformDeviceCodeSetupAsync(ct);
        if (!ok)
        {
            _log.LogError("Device code setup failed. Cannot start scraping. Exiting.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Build an <see cref="ExchangeCodeToken"/> that Core's FestivalService understands
    /// from the service's token manager state. Core only reads access_token + account_id.
    /// </summary>
    private async Task<ExchangeCodeToken?> BuildCoreTokenAsync(CancellationToken ct)
    {
        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is null) return null;

        var accountId = _tokenManager.AccountId;
        if (string.IsNullOrEmpty(accountId))
        {
            _log.LogError("Have access token but no account ID.");
            return null;
        }

        return new ExchangeCodeToken
        {
            access_token = accessToken,
            account_id = accountId,
        };
    }

    private static Settings BuildSettings(ScraperOptions opts) => new()
    {
        DegreeOfParallelism = opts.DegreeOfParallelism,
        QueryLead = opts.QueryLead,
        QueryDrums = opts.QueryDrums,
        QueryVocals = opts.QueryVocals,
        QueryBass = opts.QueryBass,
        QueryProLead = opts.QueryProLead,
        QueryProBass = opts.QueryProBass,
    };

    // ─── Scrape pass (full) ─────────────────────────────────────

    private async Task RunScrapePassAsync(
        FestivalService service,
        ScraperOptions opts,
        CancellationToken ct)
    {
        _log.LogInformation("Starting scrape pass...");

        var token = await BuildCoreTokenAsync(ct);
        if (token is null)
        {
            _log.LogError("Cannot obtain access token. Skipping this pass.");
            return;
        }

        // Re-sync the song catalog in case new songs appeared
        await service.SyncSongsAsync();

        var settings = BuildSettings(opts);
        var success = await service.FetchScoresWithTokenAsync(token, filteredSongIds: null, settings);

        LogInstrumentation(service, success ? "succeeded" : "FAILED");
    }

    // ─── Song test ───────────────────────────────────────────────

    private async Task RunSingleSongTestAsync(
        FestivalService service,
        ScraperOptions opts,
        CancellationToken ct)
    {
        // Support comma-separated queries: --test "Song A,Song B"
        var queries = opts.TestSongQuery!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        _log.LogInformation("Test mode. Searching for {Count} song(s): {Queries}",
            queries.Length, string.Join(", ", queries.Select(q => $"\"{q}\"")));

        // Resolve each query to a Song
        var matched = new List<Song>();
        foreach (var query in queries)
        {
            var match = service.Songs.FirstOrDefault(s =>
                s.track?.tt != null &&
                s.track.tt.Contains(query, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                _log.LogError("No song matching \"{Query}\" found in catalog ({Total} songs).",
                    query, service.Songs.Count);
                continue;
            }

            _log.LogInformation("Found: \"{Title}\" by {Artist}  [id={SongId}]",
                match.track.tt, match.track.an, match.track.su);
            matched.Add(match);
        }

        if (matched.Count == 0)
        {
            _log.LogError("No songs matched. Exiting.");
            return;
        }

        var accessToken = await _tokenManager.GetAccessTokenAsync(ct);
        if (accessToken is null)
        {
            _log.LogError("Cannot obtain access token for test.");
            return;
        }

        var accountId = _tokenManager.AccountId!;

        // Build scrape requests, filtering to only charted instruments per song
        var scrapeRequests = matched.Select(song =>
        {
            var available = GlobalLeaderboardScraper.GetAvailableInstruments(song);
            var skipped = GlobalLeaderboardScraper.AllInstruments.Except(available).ToList();
            if (skipped.Count > 0)
                _log.LogInformation("[{Title}] Skipping {Count} uncharted instruments: {Instruments}",
                    song.track.tt, skipped.Count, string.Join(", ", skipped));

            return new GlobalLeaderboardScraper.SongScrapeRequest
            {
                SongId = song.track.su,
                Instruments = available,
                Label = song.track.tt,
            };
        }).ToList();

        _log.LogInformation("Scraping {SongCount} song(s) across all instruments (DOP={Dop})...",
            scrapeRequests.Count, opts.DegreeOfParallelism);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var allResults = await _globalScraper.ScrapeManySongsAsync(
            scrapeRequests, accessToken, accountId, opts.DegreeOfParallelism, ct);
        sw.Stop();

        // Grand summary
        int grandEntries = allResults.Values.SelectMany(r => r).Sum(r => r.Entries.Count);
        int grandRequests = allResults.Values.SelectMany(r => r).Sum(r => r.Requests);
        long grandBytes = allResults.Values.SelectMany(r => r).Sum(r => r.BytesReceived);

        _log.LogInformation(
            "All done. {Songs} songs, {Entries} total entries, {Requests} requests, {Bytes} bytes, {Elapsed:F1}s",
            allResults.Count, grandEntries, grandRequests, grandBytes, sw.Elapsed.TotalSeconds);

        // Per-song detail
        foreach (var song in matched)
        {
            if (!allResults.TryGetValue(song.track.su, out var results)) continue;

            _log.LogInformation("═══ {Title} by {Artist} ═══", song.track.tt, song.track.an);

            foreach (var result in results)
            {
                _log.LogInformation("── {Instrument}: {Count} entries, {Pages} pages ──",
                    result.Instrument, result.Entries.Count, result.TotalPages);

                foreach (var entry in result.Entries.Take(3))
                {
                    _log.LogInformation(
                        "    #{Rank}  {AccountId}  Score={Score}  Accuracy={Accuracy}%  Stars={Stars}  FC={FC}",
                        entry.Rank, entry.AccountId, entry.Score, entry.Accuracy,
                        entry.Stars, entry.IsFullCombo ? "YES" : "no");
                }

                if (result.Entries.Count > 3)
                    _log.LogInformation("    ... and {More} more entries", result.Entries.Count - 3);
            }
        }
    }

    // ─── Logging helpers ────────────────────────────────────────

    private void LogInstrumentation(FestivalService service, string result)
    {
        var (improved, empty, errors, requests, bytes, elapsed) = service.GetInstrumentation();
        _log.LogInformation(
            "Scrape pass {Result}. Improved={Improved}, Empty={Empty}, Errors={Errors}, " +
            "Requests={Requests}, Bytes={Bytes}, Elapsed={Elapsed:F1}s",
            result, improved, empty, errors, requests, bytes, elapsed);
    }

    private void PrintTracker(string instrument, ScoreTracker? tracker)
    {
        if (tracker is null || !tracker.initialized)
        {
            _log.LogInformation("  {Instrument}: (no data)", instrument);
            return;
        }
        _log.LogInformation(
            "  {Instrument}: Score={Score}, Rank={Rank}, Stars={Stars}, Accuracy={Accuracy}%, FC={FC}",
            instrument, tracker.maxScore, tracker.rank, tracker.numStars,
            tracker.percentHit, tracker.isFullCombo ? "YES" : "no");
    }
}
