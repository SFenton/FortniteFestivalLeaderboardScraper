using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

internal enum RegisteredBandLookupScope
{
    AllTime,
    Season,
}

internal sealed record RegisteredBandLookupIntent(
    string SongId,
    RegisteredBandLookupScope Scope,
    int Season)
{
    public string ProgressScope => Scope == RegisteredBandLookupScope.AllTime ? "alltime" : "season";
    public string WindowId => Scope == RegisteredBandLookupScope.AllTime ? "alltime" : HistoryReconstructor.GetSeasonPrefix(Season);
}

internal sealed record RegisteredBandLookupResult(IReadOnlyList<BandLeaderboardEntry> Entries)
{
    public static RegisteredBandLookupResult Empty { get; } = new([]);
}

internal interface IRegisteredBandLookupStrategy
{
    Task<RegisteredBandLookupResult> FetchAsync(
        BandWorkItem band,
        RegisteredBandLookupIntent intent,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter,
        CancellationToken ct);
}

internal sealed class DirectRegisteredBandLookupStrategy : IRegisteredBandLookupStrategy
{
    private readonly ILeaderboardQuerier _scraper;

    public DirectRegisteredBandLookupStrategy(ILeaderboardQuerier scraper)
    {
        _scraper = scraper;
    }

    public async Task<RegisteredBandLookupResult> FetchAsync(
        BandWorkItem band,
        RegisteredBandLookupIntent intent,
        string accessToken,
        string callerAccountId,
        AdaptiveConcurrencyLimiter? limiter,
        CancellationToken ct)
    {
        var entry = await _scraper.LookupBandAsync(
            intent.SongId,
            band.BandType,
            band.MemberAccountIds,
            intent.WindowId,
            accessToken,
            callerAccountId,
            limiter,
            ct);

        if (entry is null)
            return RegisteredBandLookupResult.Empty;

        entry.Source = "findteams";
        BandScrapePhase.ApplyChOptValidation(entry, null);
        return new RegisteredBandLookupResult([entry]);
    }
}

public sealed class RegisteredBandProcessingResult
{
    public int BandsProcessed { get; init; }
    public int LookupsChecked { get; init; }
    public int EntriesFound { get; init; }
    public int EntriesPersisted { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> ImpactedTeamsByBandType { get; init; } =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);

    public static RegisteredBandProcessingResult Empty { get; } = new();
}

public sealed class RegisteredBandProcessingOrchestrator
{
    private readonly IMetaDatabase _metaDb;
    private readonly BandLeaderboardPersistence _bandPersistence;
    private readonly IRegisteredBandLookupStrategy _lookupStrategy;
    private readonly ScrapeProgressTracker _progress;
    private readonly ScraperOptions _options;
    private readonly ILogger<RegisteredBandProcessingOrchestrator> _log;
    private readonly SongMachineApiLookupRunner _lookupRunner;

    internal RegisteredBandProcessingOrchestrator(
        IMetaDatabase metaDb,
        BandLeaderboardPersistence bandPersistence,
        IRegisteredBandLookupStrategy lookupStrategy,
        ScrapeProgressTracker progress,
        IOptions<ScraperOptions> options,
        ILogger<RegisteredBandProcessingOrchestrator> log,
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

    public async Task<RegisteredBandProcessingResult> RunAsync(
        IReadOnlyList<string> songIds,
        IReadOnlyList<SeasonWindowInfo> seasonWindows,
        string accessToken,
        string callerAccountId,
        SharedDopPool pool,
        CancellationToken ct = default)
    {
        if (!_options.EnableRegisteredBandTargetedProcessing)
            return RegisteredBandProcessingResult.Empty;
        if (songIds.Count == 0)
            return RegisteredBandProcessingResult.Empty;

        var registeredBands = _metaDb.GetRegisteredBands();
        if (registeredBands.Count == 0)
            return RegisteredBandProcessingResult.Empty;

        var currentSeason = seasonWindows.Count == 0 ? 0 : seasonWindows.Max(static window => window.SeasonNumber);
        var intents = BuildLookupIntents(songIds, currentSeason);
        if (intents.Count == 0)
            return RegisteredBandProcessingResult.Empty;

        var maxBands = _options.RegisteredBandProcessingMaxBandsPerPass;
        if (maxBands > 0)
            registeredBands = registeredBands.Take(maxBands).ToList();

        _progress.SetAdaptiveLimiter(pool.Limiter);
        _progress.BeginPhaseProgress(registeredBands.Count);
        _progress.SetPhaseAccounts(registeredBands.Count);

        var impactedTeams = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        int bandsProcessed = 0;
        int lookupsCheckedTotal = 0;
        int entriesFoundTotal = 0;
        int entriesPersistedTotal = 0;

        foreach (var registeredBand in registeredBands)
        {
            ct.ThrowIfCancellationRequested();

            var bandResult = await ProcessBandAsync(
                registeredBand,
                intents,
                accessToken,
                callerAccountId,
                pool,
                ct);

            bandsProcessed++;
            lookupsCheckedTotal += bandResult.LookupsChecked;
            entriesFoundTotal += bandResult.EntriesFound;
            entriesPersistedTotal += bandResult.EntriesPersisted;

            if (bandResult.EntriesPersisted > 0)
            {
                if (!impactedTeams.TryGetValue(registeredBand.BandType, out var teams))
                {
                    teams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    impactedTeams[registeredBand.BandType] = teams;
                }
                teams.Add(registeredBand.TeamKey);
            }

            _progress.ReportPhaseItemComplete();
        }

        _progress.SetAdaptiveLimiter(null);

        _log.LogInformation(
            "Registered-band targeted processing complete: {Bands} band(s), {Lookups} lookup(s), {Entries} entrie(s), {Persisted} persisted row(s).",
            bandsProcessed, lookupsCheckedTotal, entriesFoundTotal, entriesPersistedTotal);

        return new RegisteredBandProcessingResult
        {
            BandsProcessed = bandsProcessed,
            LookupsChecked = lookupsCheckedTotal,
            EntriesFound = entriesFoundTotal,
            EntriesPersisted = entriesPersistedTotal,
            ImpactedTeamsByBandType = impactedTeams.ToDictionary(
                static kvp => kvp.Key,
                static kvp => (IReadOnlyCollection<string>)kvp.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase),
        };
    }

    private async Task<BandProcessingRunResult> ProcessBandAsync(
        RegisteredBandInfo registeredBand,
        IReadOnlyList<RegisteredBandLookupIntent> allIntents,
        string accessToken,
        string callerAccountId,
        SharedDopPool pool,
        CancellationToken ct)
    {
        _metaDb.EnsureRegisteredBandProcessingStatus(
            registeredBand.SourceId,
            registeredBand.BandType,
            registeredBand.TeamKey,
            allIntents.Count);

        var checkedProgress = _metaDb.GetCheckedRegisteredBandLookups(
            registeredBand.SourceId,
            registeredBand.BandType,
            registeredBand.TeamKey);
        var checkedKeys = checkedProgress
            .Select(static row => (row.SongId, row.Scope, row.Season))
            .ToHashSet();
        var entriesFound = checkedProgress.Count(static row => row.EntryFound);

        var pendingIntents = allIntents
            .Where(intent => !checkedKeys.Contains((intent.SongId, intent.ProgressScope, intent.Season)))
            .ToList();

        var maxLookups = _options.RegisteredBandProcessingMaxLookupsPerBand;
        if (maxLookups > 0)
            pendingIntents = pendingIntents.Take(maxLookups).ToList();

        if (pendingIntents.Count == 0)
        {
            if (checkedProgress.Count >= allIntents.Count)
            {
                _metaDb.CompleteRegisteredBandProcessing(
                    registeredBand.SourceId,
                    registeredBand.BandType,
                    registeredBand.TeamKey,
                    checkedProgress.Count,
                    entriesFound);
            }

            return new BandProcessingRunResult(0, 0, 0);
        }

        _metaDb.StartRegisteredBandProcessing(
            registeredBand.SourceId,
            registeredBand.BandType,
            registeredBand.TeamKey);

        var band = new BandWorkItem
        {
            BandId = registeredBand.BandId,
            BandType = registeredBand.BandType,
            TeamKey = registeredBand.TeamKey,
            MemberAccountIds = registeredBand.TeamKey.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            AllTimeNeeded = true,
            Purposes = WorkPurpose.PostScrape,
            SeasonsNeeded = allIntents.Where(static intent => intent.Scope == RegisteredBandLookupScope.Season)
                .Select(static intent => intent.Season)
                .ToHashSet(),
        };

        int lookupsChecked = 0;
        int entriesPersisted = 0;
        foreach (var intent in pendingIntents)
        {
            ct.ThrowIfCancellationRequested();

            Func<Task<RegisteredBandLookupResult>> work = () =>
            {
                _progress.ReportPhaseRequest();
                return _lookupStrategy.FetchAsync(
                    band,
                    intent,
                    accessToken,
                    callerAccountId,
                    pool.Limiter,
                    ct);
            };

            var lookupResult = await _lookupRunner.TryRunAsync(
                pool,
                isHighPriority: false,
                ct,
                work,
                ex => _log.LogDebug(ex, "Registered-band lookup failed for {BandType}/{TeamKey}/{Song}/{Scope}/{Season}.",
                    registeredBand.BandType, registeredBand.TeamKey, intent.SongId, intent.ProgressScope, intent.Season));

            if (!lookupResult.Succeeded || lookupResult.Value is null)
            {
                _metaDb.FailRegisteredBandProcessing(
                    registeredBand.SourceId,
                    registeredBand.BandType,
                    registeredBand.TeamKey,
                    $"Lookup failed for {intent.SongId}/{intent.ProgressScope}/{intent.Season}.");
                break;
            }

            var entries = lookupResult.Value.Entries;
            var found = entries.Count > 0;
            if (found)
            {
                var persisted = _bandPersistence.UpsertBandEntries(intent.SongId, registeredBand.BandType, entries);
                entriesPersisted += persisted;
                entriesFound += entries.Count;
                if (persisted > 0)
                    _progress.ReportPhaseEntryUpdated(persisted);
            }

            _metaDb.MarkRegisteredBandLookupChecked(
                registeredBand.SourceId,
                registeredBand.BandType,
                registeredBand.TeamKey,
                intent.SongId,
                intent.ProgressScope,
                intent.Season,
                found);

            lookupsChecked++;
            var totalChecked = checkedProgress.Count + lookupsChecked;
            _metaDb.UpdateRegisteredBandProcessingProgress(
                registeredBand.SourceId,
                registeredBand.BandType,
                registeredBand.TeamKey,
                totalChecked,
                entriesFound,
                allIntents.Count);

            if (totalChecked >= allIntents.Count)
            {
                _metaDb.CompleteRegisteredBandProcessing(
                    registeredBand.SourceId,
                    registeredBand.BandType,
                    registeredBand.TeamKey,
                    totalChecked,
                    entriesFound);
            }
        }

        return new BandProcessingRunResult(lookupsChecked, entriesFound, entriesPersisted);
    }

    private static List<RegisteredBandLookupIntent> BuildLookupIntents(IReadOnlyList<string> songIds, int currentSeason)
    {
        var intents = new List<RegisteredBandLookupIntent>(songIds.Count * (currentSeason > 0 ? 2 : 1));
        foreach (var songId in songIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            intents.Add(new RegisteredBandLookupIntent(songId, RegisteredBandLookupScope.AllTime, 0));
            if (currentSeason > 0)
                intents.Add(new RegisteredBandLookupIntent(songId, RegisteredBandLookupScope.Season, currentSeason));
        }
        return intents;
    }

    private sealed record BandProcessingRunResult(int LookupsChecked, int EntriesFound, int EntriesPersisted);
}