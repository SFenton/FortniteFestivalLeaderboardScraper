using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

internal sealed record RegisteredPlayerBandDiscoveryIntent(
    string SongId,
    string BandType,
    RegisteredBandLookupScope Scope,
    int Season)
{
    public string ProgressScope => Scope == RegisteredBandLookupScope.AllTime ? "alltime" : "season";
    public string WindowId => Scope == RegisteredBandLookupScope.AllTime ? "alltime" : HistoryReconstructor.GetSeasonPrefix(Season);
}

internal sealed record RegisteredPlayerBandDiscoveryLookupResult(IReadOnlyList<BandLeaderboardEntry> Entries)
{
    public static RegisteredPlayerBandDiscoveryLookupResult Empty { get; } = new([]);
}

internal interface IRegisteredPlayerBandDiscoveryStrategy
{
    Task<RegisteredPlayerBandDiscoveryLookupResult> FetchAsync(
        string accountId,
        RegisteredPlayerBandDiscoveryIntent intent,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter,
        CancellationToken ct);
}

internal sealed class DirectRegisteredPlayerBandDiscoveryStrategy : IRegisteredPlayerBandDiscoveryStrategy
{
    private readonly ILeaderboardQuerier _scraper;

    public DirectRegisteredPlayerBandDiscoveryStrategy(ILeaderboardQuerier scraper)
    {
        _scraper = scraper;
    }

    public async Task<RegisteredPlayerBandDiscoveryLookupResult> FetchAsync(
        string accountId,
        RegisteredPlayerBandDiscoveryIntent intent,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter,
        CancellationToken ct)
    {
        var entries = await _scraper.FindBandsForAccountAsync(
            intent.SongId,
            intent.BandType,
            accountId,
            intent.WindowId,
            accessToken,
            callerAccountId,
            limiter,
            ct);

        foreach (var entry in entries)
        {
            entry.Source = "findteams";
            BandScrapePhase.ApplyChOptValidation(entry, null);
        }

        return entries.Count == 0
            ? RegisteredPlayerBandDiscoveryLookupResult.Empty
            : new RegisteredPlayerBandDiscoveryLookupResult(entries);
    }
}

public sealed class RegisteredPlayerBandDiscoveryResult
{
    public int AccountsProcessed { get; init; }
    public int LookupsChecked { get; init; }
    public int EntriesFound { get; init; }
    public int EntriesPersisted { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> ImpactedTeamsByBandType { get; init; } =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<BandCurrentProjectionScopeKey> ImpactedCurrentProjectionScopes { get; init; } = [];

    public static RegisteredPlayerBandDiscoveryResult Empty { get; } = new();
}

public sealed class RegisteredPlayerBandDiscoveryOrchestrator
{
    private static readonly string[] BandTypes = ["Band_Duets", "Band_Trios", "Band_Quad"];

    private readonly IMetaDatabase _metaDb;
    private readonly BandLeaderboardPersistence _bandPersistence;
    private readonly IRegisteredPlayerBandDiscoveryStrategy _lookupStrategy;
    private readonly ScrapeProgressTracker _progress;
    private readonly ScraperOptions _options;
    private readonly ILogger<RegisteredPlayerBandDiscoveryOrchestrator> _log;
    private readonly SongMachineApiLookupRunner _lookupRunner;

    internal RegisteredPlayerBandDiscoveryOrchestrator(
        IMetaDatabase metaDb,
        BandLeaderboardPersistence bandPersistence,
        IRegisteredPlayerBandDiscoveryStrategy lookupStrategy,
        ScrapeProgressTracker progress,
        IOptions<ScraperOptions> options,
        ILogger<RegisteredPlayerBandDiscoveryOrchestrator> log,
        ResilientHttpExecutor? executor = null)
    {
        _metaDb = metaDb;
        _bandPersistence = bandPersistence;
        _lookupStrategy = lookupStrategy;
        _progress = progress;
        _options = options.Value;
        _log = log;
        _lookupRunner = new SongMachineApiLookupRunner(executor, progress);
    }

    public async Task<RegisteredPlayerBandDiscoveryResult> RunAsync(
        IReadOnlyList<string> songIds,
        IReadOnlyList<SeasonWindowInfo> seasonWindows,
        string accessToken,
        string callerAccountId,
        SharedDopPool pool,
        CancellationToken ct = default)
    {
        if (!_options.EnableRegisteredPlayerBandDiscovery)
            return RegisteredPlayerBandDiscoveryResult.Empty;
        if (songIds.Count == 0)
            return RegisteredPlayerBandDiscoveryResult.Empty;

        var accounts = _metaDb.GetRegisteredAccountIds().ToList();
        if (accounts.Count == 0)
            return RegisteredPlayerBandDiscoveryResult.Empty;

        var intents = BuildLookupIntents(songIds, seasonWindows);
        if (intents.Count == 0)
            return RegisteredPlayerBandDiscoveryResult.Empty;

        _progress.SetAdaptiveLimiter(pool.Limiter);
        _progress.BeginPhaseProgress(accounts.Count);
        _progress.SetPhaseAccounts(accounts.Count);

        var impactedTeams = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var impactedCurrentProjectionScopes = new HashSet<BandCurrentProjectionScopeKey>();
        var accountsProcessed = 0;
        var lookupsCheckedTotal = 0;
        var entriesFoundTotal = 0;
        var entriesPersistedTotal = 0;

        var maxAccounts = _options.RegisteredPlayerBandDiscoveryMaxAccountsPerPass;
        foreach (var accountId in accounts)
        {
            ct.ThrowIfCancellationRequested();
            if (maxAccounts > 0 && accountsProcessed >= maxAccounts)
                break;

            var accountResult = await ProcessAccountAsync(accountId, intents, accessToken, callerAccountId, pool, ct);
            if (accountResult.LookupsChecked == 0)
                continue;

            accountsProcessed++;
            lookupsCheckedTotal += accountResult.LookupsChecked;
            entriesFoundTotal += accountResult.EntriesFound;
            entriesPersistedTotal += accountResult.EntriesPersisted;

            foreach (var (bandType, teamKeys) in accountResult.ImpactedTeamsByBandType)
            {
                if (!impactedTeams.TryGetValue(bandType, out var teams))
                {
                    teams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    impactedTeams[bandType] = teams;
                }

                foreach (var teamKey in teamKeys)
                    teams.Add(teamKey);
            }

            foreach (var scope in accountResult.ImpactedCurrentProjectionScopes)
                impactedCurrentProjectionScopes.Add(scope);

            _progress.ReportPhaseItemComplete();
        }

        _progress.SetAdaptiveLimiter(null);

        _log.LogInformation(
            "Registered-player band discovery complete: {Accounts} account(s), {Lookups} lookup(s), {Entries} entry(s), {Persisted} persisted row(s).",
            accountsProcessed, lookupsCheckedTotal, entriesFoundTotal, entriesPersistedTotal);

        return new RegisteredPlayerBandDiscoveryResult
        {
            AccountsProcessed = accountsProcessed,
            LookupsChecked = lookupsCheckedTotal,
            EntriesFound = entriesFoundTotal,
            EntriesPersisted = entriesPersistedTotal,
            ImpactedTeamsByBandType = impactedTeams.ToDictionary(
                static kvp => kvp.Key,
                static kvp => (IReadOnlyCollection<string>)kvp.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase),
            ImpactedCurrentProjectionScopes = BandCurrentProjectionScopeTracker.OrderedDistinct(impactedCurrentProjectionScopes),
        };
    }

    internal static List<RegisteredPlayerBandDiscoveryIntent> BuildLookupIntents(
        IReadOnlyList<string> songIds,
        IReadOnlyList<SeasonWindowInfo> seasonWindows)
    {
        var distinctSongIds = songIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var seasons = seasonWindows
            .Select(static window => window.SeasonNumber)
            .Where(static season => season > 0)
            .Distinct()
            .OrderDescending()
            .ToList();

        var intents = new List<RegisteredPlayerBandDiscoveryIntent>(distinctSongIds.Count * BandTypes.Length * (1 + seasons.Count));
        foreach (var bandType in BandTypes)
        foreach (var songId in distinctSongIds)
            intents.Add(new RegisteredPlayerBandDiscoveryIntent(songId, bandType, RegisteredBandLookupScope.AllTime, 0));

        foreach (var season in seasons)
        foreach (var bandType in BandTypes)
        foreach (var songId in distinctSongIds)
            intents.Add(new RegisteredPlayerBandDiscoveryIntent(songId, bandType, RegisteredBandLookupScope.Season, season));

        return intents;
    }

    private async Task<AccountDiscoveryRunResult> ProcessAccountAsync(
        string accountId,
        IReadOnlyList<RegisteredPlayerBandDiscoveryIntent> allIntents,
        string accessToken,
        string callerAccountId,
        SharedDopPool pool,
        CancellationToken ct)
    {
        var checkedProgress = _metaDb.GetCheckedRegisteredPlayerBandDiscoveryLookups(accountId);
        var checkedKeys = checkedProgress
            .Select(static row => (row.SongId, row.BandType, row.Scope, row.Season))
            .ToHashSet();

        var pendingIntents = allIntents
            .Where(intent => !checkedKeys.Contains((intent.SongId, intent.BandType, intent.ProgressScope, intent.Season)))
            .ToList();

        var maxLookups = _options.RegisteredPlayerBandDiscoveryMaxLookupsPerAccount;
        if (maxLookups > 0)
            pendingIntents = pendingIntents.Take(maxLookups).ToList();

        if (pendingIntents.Count == 0)
            return new AccountDiscoveryRunResult(0, 0, 0, new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase), []);

        var lookupsChecked = 0;
        var entriesFound = 0;
        var entriesPersisted = 0;
        var impactedTeams = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var impactedCurrentProjectionScopes = new HashSet<BandCurrentProjectionScopeKey>();

        foreach (var intent in pendingIntents)
        {
            ct.ThrowIfCancellationRequested();

            Func<Task<RegisteredPlayerBandDiscoveryLookupResult>> work = () =>
            {
                _progress.ReportPhaseRequest();
                return _lookupStrategy.FetchAsync(accountId, intent, accessToken, callerAccountId, pool.Limiter, ct);
            };

            var lookupResult = await _lookupRunner.TryRunAsync(
                pool,
                isHighPriority: false,
                EpicTrafficKind.Background,
                ct,
                work,
                ex => _log.LogDebug(ex, "Registered-player band discovery failed for {Account}/{Song}/{BandType}/{Scope}/{Season}.",
                    accountId, intent.SongId, intent.BandType, intent.ProgressScope, intent.Season));

            if (!lookupResult.Succeeded || lookupResult.Value is null)
                break;

            var entries = lookupResult.Value.Entries
                .Where(entry => entry.TeamMembers.Contains(accountId, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var found = entries.Count > 0;
            if (found)
            {
                var persisted = _bandPersistence.UpsertBandEntries(intent.SongId, intent.BandType, entries);
                entriesPersisted += persisted;
                entriesFound += entries.Count;

                if (persisted > 0)
                    _progress.ReportPhaseEntryUpdated(persisted);

                foreach (var entry in entries)
                {
                    _metaDb.RegisterDiscoveredBandActivity(intent.BandType, entry.TeamKey, entry.TeamMembers);
                    _metaDb.MarkRegisteredBandLookupChecked(
                        MetaDatabase.WebBandTrackerDeviceId,
                        intent.BandType,
                        entry.TeamKey,
                        intent.SongId,
                        intent.ProgressScope,
                        intent.Season,
                        true);

                    if (!impactedTeams.TryGetValue(intent.BandType, out var teams))
                    {
                        teams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        impactedTeams[intent.BandType] = teams;
                    }

                    teams.Add(entry.TeamKey);
                    BandCurrentProjectionScopeTracker.AddScopes(impactedCurrentProjectionScopes, intent.SongId, intent.BandType, entry.InstrumentCombo);
                }
            }

            _metaDb.MarkRegisteredPlayerBandDiscoveryChecked(
                accountId,
                intent.SongId,
                intent.BandType,
                intent.ProgressScope,
                intent.Season,
                found);

            lookupsChecked++;
        }

        return new AccountDiscoveryRunResult(
            lookupsChecked,
            entriesFound,
            entriesPersisted,
            impactedTeams,
            BandCurrentProjectionScopeTracker.OrderedDistinct(impactedCurrentProjectionScopes));
    }

    private sealed record AccountDiscoveryRunResult(
        int LookupsChecked,
        int EntriesFound,
        int EntriesPersisted,
        IReadOnlyDictionary<string, HashSet<string>> ImpactedTeamsByBandType,
        IReadOnlyCollection<BandCurrentProjectionScopeKey> ImpactedCurrentProjectionScopes);
}
