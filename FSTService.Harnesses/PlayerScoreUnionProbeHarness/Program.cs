using System.Globalization;
using FSTService.Auth;
using FSTService.Scraping;
using FortniteFestival.Core.Scraping;
using Microsoft.Extensions.Logging;
using Npgsql;

var options = ProbeOptions.Parse(args);
if (options.ShowHelp)
{
    PrintUsage();
    return 0;
}

if (options is { AccessToken: not null, CallerAccountId: null } or { AccessToken: null, CallerAccountId: not null })
    return Fail("--access-token and --caller must be supplied together when bypassing device auth.");

await using var dataSource = NpgsqlDataSource.Create(options.PostgresConnectionString);
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSimpleConsole(console =>
    {
        console.SingleLine = true;
        console.TimestampFormat = "HH:mm:ss ";
    });
    builder.SetMinimumLevel(LogLevel.Warning);
    builder.AddFilter("FSTService.Auth", LogLevel.Information);
    builder.AddFilter("FSTService.Scraping.GlobalLeaderboardScraper", LogLevel.Warning);
});

var song = await ResolveSongAsync(dataSource, options.SongId, options.SongTitle);
if (song is null)
    return Fail($"Could not find song '{options.SongTitle}' in PostgreSQL. Supply --song-id if needed.");

var seasonWindows = await LoadSeasonWindowsAsync(dataSource);
if (seasonWindows.Count == 0)
    return Fail("No season_windows rows found in PostgreSQL. Run service season discovery first or add cached windows.");

var topBands = await LoadTopBandsAsync(dataSource, options.AccountId, options.TopBands);

var (accessToken, callerAccountId, displayName) = options.AccessToken is not null
    ? (options.AccessToken, options.CallerAccountId!, "provided-token")
    : await GetAccessTokenAsync(options.DeviceAuthPath, loggerFactory);
if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(callerAccountId))
    return Fail("No Epic access token available. Supply --device-auth pointing at device-auth.json or --access-token/--caller.");

Console.WriteLine("Player Score Union Probe");
Console.WriteLine($"Target account: SFentonX ({options.AccountId})");
Console.WriteLine($"Auth caller:     {displayName} ({callerAccountId})");
Console.WriteLine($"Song:            {song.Title} - {song.Artist} ({song.SongId})");
Console.WriteLine($"Instrument:      {options.Instrument}");
Console.WriteLine($"Seasons:         {seasonWindows.Count} cached windows; querying {BuildSeasonQueryList(song, options.Instrument, seasonWindows).Count} for this song/instrument");
Console.WriteLine($"Top local bands: {topBands.Count}");
Console.WriteLine();

using var httpClient = new HttpClient(new SocketsHttpHandler
{
    MaxConnectionsPerServer = 4,
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    AutomaticDecompression = System.Net.DecompressionMethods.All,
})
{
    Timeout = Timeout.InfiniteTimeSpan,
};

var progress = new ScrapeProgressTracker();
var scraper = new GlobalLeaderboardScraper(
    httpClient,
    progress,
    loggerFactory.CreateLogger<GlobalLeaderboardScraper>(),
    maxLookupRetries: 2);
using var limiter = new AdaptiveConcurrencyLimiter(
    initialDop: 1,
    minDop: 1,
    maxDop: 2,
    loggerFactory.CreateLogger("ProbeLimiter"),
    maxRequestsPerSecond: options.MaxRequestsPerSecond);

var queriedSeasons = BuildSeasonQueryList(song, options.Instrument, seasonWindows);
var soloObservations = await FetchSoloHistoryAsync(
    scraper, song.SongId, options.Instrument, options.AccountId, queriedSeasons,
    accessToken, callerAccountId, limiter);

var discoveredBandEntries = await FetchAccountBandHistoryAsync(
    scraper, song.SongId, options.AccountId, seasonWindows,
    accessToken, callerAccountId, limiter);
var discoveredBandObservations = ExtractMemberObservations(
    discoveredBandEntries, song.SongId, options.AccountId, options.Instrument, "band-findteams");

var exactBandEntries = await FetchExactTopBandHistoryAsync(
    scraper, song.SongId, topBands, seasonWindows,
    accessToken, callerAccountId, limiter);
var exactBandObservations = ExtractMemberObservations(
    exactBandEntries, song.SongId, options.AccountId, options.Instrument, "band-exact-top-local");

var bandObservations = discoveredBandObservations
    .Concat(exactBandObservations)
    .GroupBy(observation => observation.SourceIdentity, StringComparer.OrdinalIgnoreCase)
    .Select(group => group.First())
    .ToList();

var unionGroups = soloObservations
    .Concat(bandObservations)
    .GroupBy(UnionKey.ForObservation)
    .Select(group => new UnionObservation(group.Key, group.OrderBy(item => item.SourceKind).ToList()))
    .OrderByDescending(item => item.Score)
    .ThenBy(item => item.AchievedAt ?? string.Empty, StringComparer.Ordinal)
    .ToList();

var soloKeys = soloObservations.Select(UnionKey.ForObservation).ToHashSet();
var bandKeys = bandObservations.Select(UnionKey.ForObservation).ToHashSet();
var bandOnly = unionGroups.Where(group => group.Observations.Any(item => item.SourceKind.StartsWith("band", StringComparison.OrdinalIgnoreCase)) && !soloKeys.Contains(group.Key)).ToList();
var soloOnly = unionGroups.Where(group => group.Observations.Any(item => item.SourceKind.Equals("solo-season", StringComparison.OrdinalIgnoreCase)) && !bandKeys.Contains(group.Key)).ToList();
var overlap = unionGroups.Where(group => group.Observations.Any(item => item.SourceKind.Equals("solo-season", StringComparison.OrdinalIgnoreCase)) && group.Observations.Any(item => item.SourceKind.StartsWith("band", StringComparison.OrdinalIgnoreCase))).ToList();

PrintSummary(soloObservations, discoveredBandEntries, discoveredBandObservations, exactBandEntries, exactBandObservations, unionGroups, bandOnly, soloOnly, overlap);

return bandOnly.Count > 0 || soloOnly.Count > 0 ? 0 : 10;

static void PrintUsage()
{
    Console.Error.WriteLine("""
        Usage:
          PlayerScoreUnionProbeHarness [options]

        Options:
          --pg <conn>             PostgreSQL connection string.
                                  Default: Host=localhost;Port=5432;Database=fstservice;Username=fst;Password=fst_dev;Command Timeout=0
          --device-auth <path>    Path to device-auth.json. Default checks FST_DEVICE_AUTH, FSTService/data/device-auth.json, data/device-auth.json.
          --access-token <token>  Optional token override. Must pair with --caller. Prefer --device-auth for normal use.
          --caller <account-id>   Caller account id for --access-token override.
          --account-id <id>       Target account. Default: SFentonX.
          --song-title <title>    Song title lookup. Default: Through the Fire and Flames.
          --song-id <id>          Song id override.
          --instrument <type>     Solo leaderboard type. Default: Solo_Guitar.
          --top-bands <n>         Exact local bands to probe. Default: 10.
          --rps <n>               Max Epic requests per second. Default: 2.
          --help                  Show usage.
        """);
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}

static async Task<(string AccessToken, string CallerAccountId, string DisplayName)> GetAccessTokenAsync(
    string? deviceAuthPath,
    ILoggerFactory loggerFactory)
{
    var resolvedPath = ResolveDeviceAuthPath(deviceAuthPath);
    if (resolvedPath is null)
    {
        Console.Error.WriteLine("No device-auth.json found. Checked --device-auth, FST_DEVICE_AUTH, FSTService/data/device-auth.json, and data/device-auth.json.");
        return (string.Empty, string.Empty, string.Empty);
    }

    using var authHttp = new HttpClient(new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    var auth = new EpicAuthService(authHttp, loggerFactory.CreateLogger<EpicAuthService>());
    var store = new FileCredentialStore(resolvedPath, loggerFactory.CreateLogger<FileCredentialStore>());
    var tokenManager = new TokenManager(auth, store, loggerFactory.CreateLogger<TokenManager>());
    var accessToken = await tokenManager.GetAccessTokenAsync();
    return (accessToken ?? string.Empty, tokenManager.AccountId ?? string.Empty, tokenManager.DisplayName ?? string.Empty);
}

static string? ResolveDeviceAuthPath(string? explicitPath)
{
    var candidates = new List<string>();
    if (!string.IsNullOrWhiteSpace(explicitPath))
        candidates.Add(explicitPath);

    var envPath = Environment.GetEnvironmentVariable("FST_DEVICE_AUTH");
    if (!string.IsNullOrWhiteSpace(envPath))
        candidates.Add(envPath);

    candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "FSTService", "data", "device-auth.json"));
    candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "data", "device-auth.json"));
    candidates.Add(Path.Combine(AppContext.BaseDirectory, "data", "device-auth.json"));

    return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
}

static async Task<SongTarget?> ResolveSongAsync(NpgsqlDataSource dataSource, string? songId, string songTitle)
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();

    if (!string.IsNullOrWhiteSpace(songId))
    {
        command.CommandText = """
            SELECT songs.song_id, songs.title, songs.artist,
                   COALESCE(song_first_seen_season.first_seen_season, song_first_seen_season.estimated_season, 1) AS first_seen_season
            FROM songs
            LEFT JOIN song_first_seen_season ON song_first_seen_season.song_id = songs.song_id
            WHERE songs.song_id = @songId
            LIMIT 1
            """;
        command.Parameters.AddWithValue("songId", songId);
    }
    else
    {
        command.CommandText = """
            SELECT songs.song_id, songs.title, songs.artist,
                   COALESCE(song_first_seen_season.first_seen_season, song_first_seen_season.estimated_season, 1) AS first_seen_season
            FROM songs
            LEFT JOIN song_first_seen_season ON song_first_seen_season.song_id = songs.song_id
            WHERE lower(songs.title) = lower(@title)
               OR lower(songs.title) LIKE lower(@likeTitle)
            ORDER BY CASE WHEN lower(songs.title) = lower(@title) THEN 0 ELSE 1 END,
                     songs.title
            LIMIT 1
            """;
        command.Parameters.AddWithValue("title", songTitle);
        command.Parameters.AddWithValue("likeTitle", $"%{songTitle}%");
    }

    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        return null;

    return new SongTarget(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
        reader.IsDBNull(3) ? 1 : reader.GetInt32(3));
}

static async Task<List<SeasonWindow>> LoadSeasonWindowsAsync(NpgsqlDataSource dataSource)
{
    var windows = new List<SeasonWindow>();
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT season_number, window_id FROM season_windows ORDER BY season_number";
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        windows.Add(new SeasonWindow(reader.GetInt32(0), reader.GetString(1)));
    return windows;
}

static async Task<List<LocalBand>> LoadTopBandsAsync(NpgsqlDataSource dataSource, string accountId, int topBands)
{
    var bands = new List<LocalBand>();
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT band_type,
               team_key,
               SUM(appearance_count)::INTEGER AS appearances,
               MAX(updated_at) AS updated_at
        FROM band_team_membership
        WHERE account_id = @accountId
        GROUP BY band_type, team_key
        ORDER BY SUM(appearance_count) DESC, MAX(updated_at) DESC NULLS LAST, team_key
        LIMIT @limit
        """;
    command.Parameters.AddWithValue("accountId", accountId);
    command.Parameters.AddWithValue("limit", topBands);

    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var teamKey = reader.GetString(1);
        bands.Add(new LocalBand(
            reader.GetString(0),
            teamKey,
            teamKey.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            reader.GetInt32(2)));
    }

    return bands;
}

static List<SeasonWindow> BuildSeasonQueryList(SongTarget song, string instrument, IReadOnlyList<SeasonWindow> seasonWindows)
{
    var instrumentFloor = GlobalLeaderboardScraper.GetInstrumentLaunchSeason(instrument);
    var startSeason = Math.Max(1, Math.Max(song.FirstSeenSeason, instrumentFloor));
    return seasonWindows.Where(window => window.SeasonNumber >= startSeason).OrderBy(window => window.SeasonNumber).ToList();
}

static async Task<List<ScoreObservation>> FetchSoloHistoryAsync(
    GlobalLeaderboardScraper scraper,
    string songId,
    string instrument,
    string accountId,
    IReadOnlyList<SeasonWindow> seasons,
    string accessToken,
    string callerAccountId,
    AdaptiveConcurrencyLimiter limiter)
{
    var observations = new List<ScoreObservation>();
    foreach (var season in seasons)
    {
        try
        {
            var sessions = await scraper.LookupSeasonalSessionsAsync(
                songId, instrument, season.WindowId, accountId, accessToken, callerAccountId, limiter);
            if (sessions is null)
                continue;

            observations.AddRange(sessions.Select(session => ScoreObservation.FromSolo(songId, instrument, season.SeasonNumber, session)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.WriteLine($"Solo season {season.SeasonNumber}: skipped ({exception.GetType().Name})");
        }
    }

    return observations;
}

static async Task<List<BandEntryObservation>> FetchAccountBandHistoryAsync(
    GlobalLeaderboardScraper scraper,
    string songId,
    string accountId,
    IReadOnlyList<SeasonWindow> seasonWindows,
    string accessToken,
    string callerAccountId,
    AdaptiveConcurrencyLimiter limiter)
{
    var results = new List<BandEntryObservation>();
    var scopes = new[] { new BandScope("alltime", 0) }
        .Concat(seasonWindows.Select(window => new BandScope(window.WindowId, window.SeasonNumber)))
        .ToList();

    foreach (var scope in scopes)
    {
        foreach (var bandType in BandInstrumentMapping.AllBandTypes)
        {
            try
            {
                var entries = await scraper.FindBandsForAccountAsync(
                    songId, bandType, accountId, scope.WindowId, accessToken, callerAccountId, limiter);
                results.AddRange(entries.Select(entry => new BandEntryObservation(entry, bandType, scope.WindowId, scope.SeasonNumber, "findteams")));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.WriteLine($"findTeams {bandType}/{scope.WindowId}: skipped ({exception.GetType().Name})");
            }
        }
    }

    return results;
}

static async Task<List<BandEntryObservation>> FetchExactTopBandHistoryAsync(
    GlobalLeaderboardScraper scraper,
    string songId,
    IReadOnlyList<LocalBand> localBands,
    IReadOnlyList<SeasonWindow> seasonWindows,
    string accessToken,
    string callerAccountId,
    AdaptiveConcurrencyLimiter limiter)
{
    var results = new List<BandEntryObservation>();
    var scopes = new[] { new BandScope("alltime", 0) }
        .Concat(seasonWindows.Select(window => new BandScope(window.WindowId, window.SeasonNumber)))
        .ToList();

    foreach (var localBand in localBands)
    {
        foreach (var scope in scopes)
        {
            try
            {
                var entry = await scraper.LookupBandAsync(
                    songId, localBand.BandType, localBand.MemberAccountIds,
                    scope.WindowId, accessToken, callerAccountId, limiter);
                if (entry is not null)
                    results.Add(new BandEntryObservation(entry, localBand.BandType, scope.WindowId, scope.SeasonNumber, "exact-top-local"));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.WriteLine($"exact {localBand.BandType}/{scope.WindowId}/{ShortTeam(localBand.TeamKey)}: skipped ({exception.GetType().Name})");
            }
        }
    }

    return results;
}

static List<ScoreObservation> ExtractMemberObservations(
    IEnumerable<BandEntryObservation> bandEntries,
    string songId,
    string accountId,
    string targetInstrument,
    string sourceKind)
{
    var observations = new List<ScoreObservation>();
    foreach (var bandEntry in bandEntries)
    {
        foreach (var member in bandEntry.Entry.MemberStats)
        {
            if (!member.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase))
                continue;

            var memberInstrument = BandInstrumentMapping.ToLeaderboardType(member.InstrumentId);
            if (!string.Equals(memberInstrument, targetInstrument, StringComparison.OrdinalIgnoreCase))
                continue;

            observations.Add(ScoreObservation.FromBand(
                bandEntry,
                sourceKind,
                songId,
                targetInstrument,
                member));
        }
    }

    return observations;
}

static void PrintSummary(
    IReadOnlyList<ScoreObservation> soloObservations,
    IReadOnlyList<BandEntryObservation> discoveredBandEntries,
    IReadOnlyList<ScoreObservation> discoveredBandObservations,
    IReadOnlyList<BandEntryObservation> exactBandEntries,
    IReadOnlyList<ScoreObservation> exactBandObservations,
    IReadOnlyList<UnionObservation> unionGroups,
    IReadOnlyList<UnionObservation> bandOnly,
    IReadOnlyList<UnionObservation> soloOnly,
    IReadOnlyList<UnionObservation> overlap)
{
    Console.WriteLine("Probe results");
    Console.WriteLine($"Solo seasonal observations:        {soloObservations.Count}");
    Console.WriteLine($"findTeams band entries returned:   {discoveredBandEntries.Count}");
    Console.WriteLine($"findTeams Lead member observations:{discoveredBandObservations.Count}");
    Console.WriteLine($"Exact top-band entries returned:   {exactBandEntries.Count}");
    Console.WriteLine($"Exact Lead member observations:    {exactBandObservations.Count}");
    Console.WriteLine($"Distinct union performances:       {unionGroups.Count}");
    Console.WriteLine($"Band-only union performances:      {bandOnly.Count}");
    Console.WriteLine($"Solo-only union performances:      {soloOnly.Count}");
    Console.WriteLine($"Overlapping union performances:    {overlap.Count}");
    Console.WriteLine();

    PrintExamples("Band-only examples", bandOnly);
    PrintExamples("Solo-only examples", soloOnly);
    PrintExamples("Overlap examples", overlap);
    PrintExamples("Union top score preview", unionGroups.Take(20).ToList());
}

static void PrintExamples(string title, IReadOnlyList<UnionObservation> groups)
{
    Console.WriteLine(title);
    if (groups.Count == 0)
    {
        Console.WriteLine("  (none)");
        Console.WriteLine();
        return;
    }

    foreach (var group in groups.Take(10))
    {
        var sources = string.Join(", ", group.Observations.Select(observation => observation.SourceKind).Distinct(StringComparer.OrdinalIgnoreCase));
        Console.WriteLine($"  score={group.Score:N0} time={group.AchievedAt ?? "(no-time)"} diff={group.Difficulty?.ToString(CultureInfo.InvariantCulture) ?? "?"} sources=[{sources}]");
        foreach (var observation in group.Observations.Take(4))
        {
            var context = observation.BandType is null
                ? $"season={observation.Season?.ToString(CultureInfo.InvariantCulture) ?? "?"} rank={observation.Rank?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
                : $"{observation.BandType} {ShortTeam(observation.TeamKey ?? string.Empty)} scope={observation.Scope} bandScore={observation.BandScore?.ToString("N0", CultureInfo.InvariantCulture) ?? "?"} bandRank={observation.BandRank?.ToString(CultureInfo.InvariantCulture) ?? "?"}";
            Console.WriteLine($"    - {observation.SourceKind}: {context}");
        }
    }
    Console.WriteLine();
}

static string ShortTeam(string teamKey)
{
    if (string.IsNullOrWhiteSpace(teamKey))
        return "(no-team)";

    var parts = teamKey.Split(':', StringSplitOptions.RemoveEmptyEntries);
    return string.Join(':', parts.Select(part => part.Length <= 8 ? part : part[..8]));
}

sealed record ProbeOptions(
    string PostgresConnectionString,
    string? DeviceAuthPath,
    string? AccessToken,
    string? CallerAccountId,
    string AccountId,
    string SongTitle,
    string? SongId,
    string Instrument,
    int TopBands,
    int MaxRequestsPerSecond,
    bool ShowHelp)
{
    public static ProbeOptions Parse(string[] args)
    {
        var options = new ProbeOptions(
            "Host=localhost;Port=5432;Database=fstservice;Username=fst;Password=fst_dev;Command Timeout=0",
            null,
            null,
            null,
            ProbeDefaults.AccountId,
            ProbeDefaults.SongTitle,
            null,
            ProbeDefaults.Instrument,
            ProbeDefaults.TopBands,
            2,
            false);

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--help" or "-h":
                    options = options with { ShowHelp = true };
                    break;
                case "--pg":
                    options = options with { PostgresConnectionString = args[++index] };
                    break;
                case "--device-auth":
                    options = options with { DeviceAuthPath = args[++index] };
                    break;
                case "--access-token":
                    options = options with { AccessToken = args[++index] };
                    break;
                case "--caller":
                    options = options with { CallerAccountId = args[++index] };
                    break;
                case "--account-id":
                    options = options with { AccountId = args[++index] };
                    break;
                case "--song-title":
                    options = options with { SongTitle = args[++index] };
                    break;
                case "--song-id":
                    options = options with { SongId = args[++index] };
                    break;
                case "--instrument":
                    options = options with { Instrument = args[++index] };
                    break;
                case "--top-bands":
                    options = options with { TopBands = int.Parse(args[++index], CultureInfo.InvariantCulture) };
                    break;
                case "--rps":
                    options = options with { MaxRequestsPerSecond = int.Parse(args[++index], CultureInfo.InvariantCulture) };
                    break;
            }
        }

        return options;
    }
}

sealed record SongTarget(string SongId, string Title, string Artist, int FirstSeenSeason);
sealed record SeasonWindow(int SeasonNumber, string WindowId);
sealed record LocalBand(string BandType, string TeamKey, IReadOnlyList<string> MemberAccountIds, int AppearanceCount);
sealed record BandScope(string WindowId, int? SeasonNumber);
sealed record BandEntryObservation(BandLeaderboardEntry Entry, string BandType, string Scope, int? Season, string QuerySource);

sealed record ScoreObservation(
    string SourceKind,
    string SourceIdentity,
    string SongId,
    string Instrument,
    int Score,
    int? Accuracy,
    bool? IsFullCombo,
    int? Stars,
    int? Difficulty,
    int? Season,
    string? AchievedAt,
    int? Rank,
    double? Percentile,
    string? BandType,
    string? TeamKey,
    string? InstrumentCombo,
    int? BandScore,
    int? BandRank,
    string Scope)
{
    public static ScoreObservation FromSolo(string songId, string instrument, int season, SessionHistoryEntry session)
    {
        var achievedAt = ProbeHelpers.NormalizeTime(session.EndTime);
        var identity = $"solo:{songId}:{instrument}:{season}:{session.Score}:{achievedAt}:{session.Difficulty}:{session.Rank}";
        return new ScoreObservation(
            "solo-season",
            identity,
            songId,
            instrument,
            session.Score,
            session.Accuracy,
            session.IsFullCombo,
            session.Stars,
            session.Difficulty,
            season,
            string.IsNullOrEmpty(achievedAt) ? null : achievedAt,
            session.Rank,
            session.Percentile,
            null,
            null,
            null,
            null,
            null,
            $"season:{season}");
    }

    public static ScoreObservation FromBand(
        BandEntryObservation bandEntry,
        string sourceKind,
        string songId,
        string instrument,
        BandMemberStats member)
    {
        var achievedAt = ProbeHelpers.NormalizeTime(bandEntry.Entry.EndTime);
        var identity = $"{sourceKind}:{bandEntry.BandType}:{bandEntry.Entry.TeamKey}:{bandEntry.Entry.InstrumentCombo}:{bandEntry.Scope}:{member.MemberIndex}:{member.Score}:{achievedAt}:{member.Difficulty}";
        return new ScoreObservation(
            sourceKind,
            identity,
            songId,
            instrument,
            member.Score,
            member.Accuracy,
            member.IsFullCombo,
            member.Stars,
            member.Difficulty,
            bandEntry.Season == 0 ? null : bandEntry.Season,
            string.IsNullOrEmpty(achievedAt) ? null : achievedAt,
            null,
            null,
            bandEntry.BandType,
            bandEntry.Entry.TeamKey,
            bandEntry.Entry.InstrumentCombo,
            bandEntry.Entry.Score,
            bandEntry.Entry.Rank,
            bandEntry.Scope);
    }
}

sealed record UnionKey(string Instrument, int Score, int? Difficulty, string AchievedAtOrSource)
{
    public static UnionKey ForObservation(ScoreObservation observation)
    {
        var achievedAtOrSource = !string.IsNullOrWhiteSpace(observation.AchievedAt)
            ? $"time:{observation.AchievedAt}"
            : $"source:{observation.SourceIdentity}";
        return new UnionKey(observation.Instrument, observation.Score, observation.Difficulty, achievedAtOrSource);
    }
}

sealed record UnionObservation(UnionKey Key, IReadOnlyList<ScoreObservation> Observations)
{
    public int Score => Key.Score;
    public int? Difficulty => Key.Difficulty;
    public string? AchievedAt => Key.AchievedAtOrSource.StartsWith("time:", StringComparison.Ordinal)
        ? Key.AchievedAtOrSource[5..]
        : null;
}

static class ProbeDefaults
{
    public const string AccountId = "195e93ef108143b2975ee46662d4d0e1";
    public const string SongTitle = "Through the Fire and Flames";
    public const string Instrument = "Solo_Guitar";
    public const int TopBands = 10;
}

static class ProbeHelpers
{
    public static string NormalizeTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            : value.Trim();
    }
}
