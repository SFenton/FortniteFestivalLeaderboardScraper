using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

internal enum RegisteredBandLookupScope
{
    AllTime,
    Season,
}

internal static class RegisteredBandLookupIdentity
{
    public static string ResolveWindowId(
        string scope,
        int season,
        string? windowId)
    {
        if (!string.IsNullOrWhiteSpace(windowId))
            return windowId;

        return string.Equals(scope, "alltime", StringComparison.Ordinal)
            ? "alltime"
            : HistoryReconstructor.GetSeasonPrefix(season);
    }
}

internal sealed record RegisteredBandLookupIntent(
    string SongId,
    RegisteredBandLookupScope Scope,
    int Season,
    string WindowId)
{
    public string ProgressScope => Scope == RegisteredBandLookupScope.AllTime ? "alltime" : "season";
    public (string SongId, string Scope, int Season, string WindowId) ProgressKey =>
        (SongId, ProgressScope, Season, WindowId);
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
    public IReadOnlyCollection<BandCurrentProjectionScopeKey> ImpactedCurrentProjectionScopes { get; init; } = [];

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

        var intents = BuildLookupIntents(songIds, seasonWindows);
        if (intents.Count == 0)
            return RegisteredBandProcessingResult.Empty;

        _progress.SetAdaptiveLimiter(pool.Limiter);
        var maxBands = _options.RegisteredBandProcessingMaxBandsPerPass;
        var plannedBandCount = maxBands > 0
            ? Math.Min(maxBands, registeredBands.Count)
            : registeredBands.Count;
        _progress.BeginPhaseProgress(plannedBandCount);
        _progress.SetPhaseAccounts(plannedBandCount);

        var impactedTeams = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var impactedCurrentProjectionScopes = new HashSet<BandCurrentProjectionScopeKey>();
        int bandsProcessed = 0;
        int lookupsCheckedTotal = 0;
        int entriesFoundTotal = 0;
        int entriesPersistedTotal = 0;
        var maxLookupsPerPass = _options.RegisteredBandProcessingMaxLookupsPerPass;

        foreach (var registeredBand in registeredBands)
        {
            ct.ThrowIfCancellationRequested();
            if (maxBands > 0 && bandsProcessed >= maxBands)
                break;
            if (maxLookupsPerPass > 0 && lookupsCheckedTotal >= maxLookupsPerPass)
                break;

            var remainingLookups = maxLookupsPerPass > 0
                ? maxLookupsPerPass - lookupsCheckedTotal
                : 0;

            var bandResult = await ProcessBandAsync(
                registeredBand,
                intents,
                accessToken,
                callerAccountId,
                pool,
                remainingLookups,
                ct);

            if (bandResult.LookupsChecked == 0)
                continue;

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

                foreach (var scope in bandResult.ImpactedCurrentProjectionScopes)
                    impactedCurrentProjectionScopes.Add(scope);
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
            ImpactedCurrentProjectionScopes = BandCurrentProjectionScopeTracker.OrderedDistinct(impactedCurrentProjectionScopes),
        };
    }

    private async Task<BandProcessingRunResult> ProcessBandAsync(
        RegisteredBandInfo registeredBand,
        IReadOnlyList<RegisteredBandLookupIntent> allIntents,
        string accessToken,
        string callerAccountId,
        SharedDopPool pool,
        int remainingPassLookups,
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
            .Select(static row => (
                row.SongId,
                row.Scope,
                row.Season,
                WindowId: RegisteredBandLookupIdentity.ResolveWindowId(
                    row.Scope,
                    row.Season,
                    row.WindowId)))
            .ToHashSet();
        var currentIntentKeys = allIntents
            .Select(static intent => intent.ProgressKey)
            .ToHashSet();
        var matchedProgress = checkedProgress
            .Where(row => currentIntentKeys.Contains((
                row.SongId,
                row.Scope,
                row.Season,
                RegisteredBandLookupIdentity.ResolveWindowId(
                    row.Scope,
                    row.Season,
                    row.WindowId))))
            .ToList();
        var matchedCheckedCount = matchedProgress.Count;
        var entriesFound = matchedProgress.Count(static row => row.EntryFound);

        var pendingIntents = allIntents
            .Where(intent => !checkedKeys.Contains(intent.ProgressKey))
            .ToList();

        var maxLookups = _options.RegisteredBandProcessingMaxLookupsPerBand;
        if (maxLookups > 0)
            pendingIntents = pendingIntents.Take(maxLookups).ToList();
        if (remainingPassLookups > 0)
            pendingIntents = pendingIntents.Take(remainingPassLookups).ToList();

        if (pendingIntents.Count == 0)
        {
            if (matchedCheckedCount >= allIntents.Count)
            {
                _metaDb.CompleteRegisteredBandProcessing(
                    registeredBand.SourceId,
                    registeredBand.BandType,
                    registeredBand.TeamKey,
                    matchedCheckedCount,
                    entriesFound);
            }

            return new BandProcessingRunResult(0, 0, 0, []);
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
        var impactedCurrentProjectionScopes = new HashSet<BandCurrentProjectionScopeKey>();
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
                EpicTrafficKind.Background,
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
                {
                    _progress.ReportPhaseEntryUpdated(persisted);
                    foreach (var entry in entries)
                        BandCurrentProjectionScopeTracker.AddScopes(impactedCurrentProjectionScopes, intent.SongId, registeredBand.BandType, entry.InstrumentCombo);
                }
            }

            _metaDb.MarkRegisteredBandLookupChecked(
                registeredBand.SourceId,
                registeredBand.BandType,
                registeredBand.TeamKey,
                intent.SongId,
                intent.ProgressScope,
                intent.Season,
                found,
                intent.WindowId);

            lookupsChecked++;
            var totalChecked = matchedCheckedCount + lookupsChecked;
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

        return new BandProcessingRunResult(
            lookupsChecked,
            entriesFound,
            entriesPersisted,
            BandCurrentProjectionScopeTracker.OrderedDistinct(impactedCurrentProjectionScopes));
    }

    internal static List<RegisteredBandLookupIntent> BuildLookupIntents(
        IReadOnlyList<string> songIds,
        IReadOnlyList<SeasonWindowInfo> seasonWindows)
    {
        var distinctSongIds = songIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var windows = seasonWindows
            .Where(static window => window.SeasonNumber > 0)
            .GroupBy(static window => window.SeasonNumber)
            .Select(static group =>
                group.LastOrDefault(static window => !string.IsNullOrWhiteSpace(window.WindowId))
                ?? group.Last())
            .OrderByDescending(static window => window.SeasonNumber)
            .ToList();

        var intents = new List<RegisteredBandLookupIntent>(distinctSongIds.Count * (1 + windows.Count));
        foreach (var songId in distinctSongIds)
            intents.Add(new RegisteredBandLookupIntent(
                songId,
                RegisteredBandLookupScope.AllTime,
                0,
                "alltime"));

        foreach (var window in windows)
        foreach (var songId in distinctSongIds)
            intents.Add(new RegisteredBandLookupIntent(
                songId,
                RegisteredBandLookupScope.Season,
                window.SeasonNumber,
                HistoryReconstructor.GetSeasonLookupId(window)));

        return intents;
    }

    private sealed record BandProcessingRunResult(
        int LookupsChecked,
        int EntriesFound,
        int EntriesPersisted,
        IReadOnlyCollection<BandCurrentProjectionScopeKey> ImpactedCurrentProjectionScopes);
}