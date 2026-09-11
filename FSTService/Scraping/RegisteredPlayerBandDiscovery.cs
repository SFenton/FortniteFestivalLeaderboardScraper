using FSTService.Persistence;
using Microsoft.Extensions.Options;

namespace FSTService.Scraping;

internal sealed record RegisteredPlayerBandDiscoveryIntent(
    string SongId,
    string BandType,
    RegisteredBandLookupScope Scope,
    int Season,
    string WindowId)
{
    public string ProgressScope => Scope == RegisteredBandLookupScope.AllTime ? "alltime" : "season";
    public (string SongId, string BandType, string Scope, int Season, string WindowId) ProgressKey =>
        (SongId, BandType, ProgressScope, Season, WindowId);
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
    public int LookupsAttempted { get; init; }
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
    private readonly RegistrationMutationCoordinator
        _registrationMutations;

    internal RegisteredPlayerBandDiscoveryOrchestrator(
        IMetaDatabase metaDb,
        BandLeaderboardPersistence bandPersistence,
        IRegisteredPlayerBandDiscoveryStrategy lookupStrategy,
        ScrapeProgressTracker progress,
        IOptions<ScraperOptions> options,
        ILogger<RegisteredPlayerBandDiscoveryOrchestrator> log,
        RegistrationMutationCoordinator registrationMutations,
        ResilientHttpExecutor? executor = null)
    {
        _metaDb = metaDb;
        _bandPersistence = bandPersistence;
        _lookupStrategy = lookupStrategy;
        _progress = progress;
        _options = options.Value;
        _log = log;
        _lookupRunner = new SongMachineApiLookupRunner(executor, progress);
        _registrationMutations = registrationMutations;
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

        await using var registrationLease =
            await _registrationMutations
                .AcquireLeaseAsync(ct);
        await registrationLease.VerifyHeldAsync(ct);
        var accounts = _metaDb.GetRegisteredAccountIdsForBandDiscovery();
        if (accounts.Count == 0)
            return RegisteredPlayerBandDiscoveryResult.Empty;

        var intents = BuildLookupIntents(songIds, seasonWindows);
        if (intents.Count == 0)
            return RegisteredPlayerBandDiscoveryResult.Empty;

        var impactedTeams = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var impactedCurrentProjectionScopes = new HashSet<BandCurrentProjectionScopeKey>();
        var accountsAttempted = 0;
        var lookupsAttemptedTotal = 0;
        var lookupsCheckedTotal = 0;
        var entriesFoundTotal = 0;
        var entriesPersistedTotal = 0;

        var maxAccounts = _options.RegisteredPlayerBandDiscoveryMaxAccountsPerPass;
        var maxLookupsPerPass = _options.RegisteredPlayerBandDiscoveryMaxLookupsPerPass;
        var admittedAccounts = maxAccounts > 0
            ? accounts.Take(maxAccounts).ToArray()
            : accounts.ToArray();
        var admittedLookups = CountAdmittedLookups(
            admittedAccounts,
            intents,
            maxLookupsPerPass);
        _progress.SetAdaptiveLimiter(pool.Limiter);
        _progress.BeginPhaseProgress(admittedLookups);
        _progress.SetPhaseAccounts(admittedAccounts.Length);

        RegisteredPlayerBandDiscoveryResult BuildResult() => new()
        {
            AccountsProcessed = accountsAttempted,
            LookupsAttempted = lookupsAttemptedTotal,
            LookupsChecked = lookupsCheckedTotal,
            EntriesFound = entriesFoundTotal,
            EntriesPersisted = entriesPersistedTotal,
            ImpactedTeamsByBandType = impactedTeams.ToDictionary(
                static kvp => kvp.Key,
                static kvp => (IReadOnlyCollection<string>)kvp.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase),
            ImpactedCurrentProjectionScopes =
                BandCurrentProjectionScopeTracker.OrderedDistinct(
                    impactedCurrentProjectionScopes),
        };

        void MergeAccountResult(AccountDiscoveryRunResult accountResult)
        {
            lookupsAttemptedTotal += accountResult.LookupsAttempted;
            lookupsCheckedTotal += accountResult.LookupsChecked;
            entriesFoundTotal += accountResult.EntriesFound;
            entriesPersistedTotal += accountResult.EntriesPersisted;

            foreach (var (bandType, teamKeys) in accountResult.ImpactedTeamsByBandType)
            {
                if (!impactedTeams.TryGetValue(bandType, out var teams))
                {
                    teams = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    impactedTeams[bandType] = teams;
                }

                foreach (var teamKey in teamKeys)
                    teams.Add(teamKey);
            }

            foreach (var scope in accountResult.ImpactedCurrentProjectionScopes)
                impactedCurrentProjectionScopes.Add(scope);
        }

        try
        {
            foreach (var accountId in admittedAccounts)
            {
                ct.ThrowIfCancellationRequested();
                if (maxLookupsPerPass > 0
                    && lookupsAttemptedTotal >= maxLookupsPerPass)
                {
                    break;
                }

                accountsAttempted++;
                var remainingLookups = maxLookupsPerPass > 0
                    ? maxLookupsPerPass - lookupsAttemptedTotal
                    : 0;

                try
                {
                    var accountResult = await ProcessAccountAsync(
                        accountId,
                        intents,
                        accessToken,
                        callerAccountId,
                        pool,
                        remainingLookups,
                        ct);
                    MergeAccountResult(accountResult);
                }
                catch (PartialResultOperationCanceledException<AccountDiscoveryRunResult> ex)
                {
                    MergeAccountResult(ex.PartialResult);
                    throw;
                }
                catch (PartialResultFailureException<AccountDiscoveryRunResult> ex)
                {
                    MergeAccountResult(ex.PartialResultValue);
                    throw;
                }

                _progress.ReportPhaseAccountComplete();
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new PartialResultOperationCanceledException<RegisteredPlayerBandDiscoveryResult>(
                BuildResult(),
                ex);
        }
        catch (Exception ex)
        {
            throw new PartialResultFailureException<RegisteredPlayerBandDiscoveryResult>(
                BuildResult(),
                ex);
        }
        finally
        {
            _progress.SetAdaptiveLimiter(null);
        }

        _log.LogInformation(
            "Registered-player band discovery complete: {Accounts} account(s), {Lookups} lookup(s), {Entries} entry(s), {Persisted} persisted row(s).",
            accountsAttempted, lookupsCheckedTotal, entriesFoundTotal, entriesPersistedTotal);

        return BuildResult();
    }

    private int CountAdmittedLookups(
        IReadOnlyList<string> accounts,
        IReadOnlyList<RegisteredPlayerBandDiscoveryIntent> intents,
        int maxLookupsPerPass)
    {
        var total = 0;
        foreach (var accountId in accounts)
        {
            var checkedKeys = _metaDb
                .GetCheckedRegisteredPlayerBandDiscoveryLookups(accountId)
                .Select(static row => (
                    row.SongId,
                    row.BandType,
                    row.Scope,
                    row.Season,
                    WindowId: RegisteredBandLookupIdentity.ResolveWindowId(
                        row.Scope,
                        row.Season,
                        row.WindowId)))
                .ToHashSet();
            var pending = intents.Count(intent =>
                !checkedKeys.Contains(intent.ProgressKey));
            var perAccount =
                _options.RegisteredPlayerBandDiscoveryMaxLookupsPerAccount;
            if (perAccount > 0)
                pending = Math.Min(pending, perAccount);
            total += pending;
            if (maxLookupsPerPass > 0 && total >= maxLookupsPerPass)
                return maxLookupsPerPass;
        }

        return total;
    }

    internal static List<RegisteredPlayerBandDiscoveryIntent> BuildLookupIntents(
        IReadOnlyList<string> songIds,
        IReadOnlyList<SeasonWindowInfo> seasonWindows)
    {
        var distinctSongIds = songIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var windows = HistoryReconstructor.MergeSeasonWindows(seasonWindows)
            .OrderByDescending(static window => window.SeasonNumber)
            .ToList();

        var intents = new List<RegisteredPlayerBandDiscoveryIntent>(distinctSongIds.Count * BandTypes.Length * (1 + windows.Count));
        foreach (var bandType in BandTypes)
            foreach (var songId in distinctSongIds)
                intents.Add(new RegisteredPlayerBandDiscoveryIntent(
                    songId,
                    bandType,
                    RegisteredBandLookupScope.AllTime,
                    0,
                    "alltime"));

        foreach (var window in windows)
            foreach (var bandType in BandTypes)
                foreach (var songId in distinctSongIds)
                    intents.Add(new RegisteredPlayerBandDiscoveryIntent(
                        songId,
                        bandType,
                        RegisteredBandLookupScope.Season,
                        window.SeasonNumber,
                        HistoryReconstructor.GetSeasonLookupId(window)));

        return intents;
    }

    private async Task<AccountDiscoveryRunResult> ProcessAccountAsync(
        string accountId,
        IReadOnlyList<RegisteredPlayerBandDiscoveryIntent> allIntents,
        string accessToken,
        string callerAccountId,
        SharedDopPool pool,
        int remainingPassLookups,
        CancellationToken ct)
    {
        var checkedProgress = _metaDb.GetCheckedRegisteredPlayerBandDiscoveryLookups(accountId);
        var checkedKeys = checkedProgress
            .Select(static row => (
                row.SongId,
                row.BandType,
                row.Scope,
                row.Season,
                WindowId: RegisteredBandLookupIdentity.ResolveWindowId(
                    row.Scope,
                    row.Season,
                    row.WindowId)))
            .ToHashSet();

        var pendingIntents = allIntents
            .Where(intent => !checkedKeys.Contains(intent.ProgressKey))
            .ToList();

        var maxLookups = _options.RegisteredPlayerBandDiscoveryMaxLookupsPerAccount;
        if (maxLookups > 0)
            pendingIntents = pendingIntents.Take(maxLookups).ToList();
        if (remainingPassLookups > 0)
            pendingIntents = pendingIntents.Take(remainingPassLookups).ToList();

        if (pendingIntents.Count == 0)
            return new AccountDiscoveryRunResult(0, 0, 0, 0, new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase), []);

        var lookupsChecked = 0;
        var lookupsAttempted = 0;
        var entriesFound = 0;
        var entriesPersisted = 0;
        var impactedTeams = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var impactedCurrentProjectionScopes = new HashSet<BandCurrentProjectionScopeKey>();

        AccountDiscoveryRunResult BuildResult() => new(
            lookupsAttempted,
            lookupsChecked,
            entriesFound,
            entriesPersisted,
            impactedTeams,
            BandCurrentProjectionScopeTracker.OrderedDistinct(
                impactedCurrentProjectionScopes));

        try
        {
            foreach (var intent in pendingIntents)
            {
                ct.ThrowIfCancellationRequested();
                lookupsAttempted++;
                _progress.ReportPhaseAttempt();

                Func<Task<RegisteredPlayerBandDiscoveryLookupResult>> work = () =>
                {
                    _progress.ReportPhaseRequest();
                    return _lookupStrategy.FetchAsync(accountId, intent, accessToken, callerAccountId, pool.Limiter, ct);
                };

                var stopwatch = RegisteredLookupInstrumentation.Start();
                SongMachineLookupResult<RegisteredPlayerBandDiscoveryLookupResult> lookupResult;
                try
                {
                    lookupResult = await _lookupRunner.TryRunAsync(
                        pool,
                        isHighPriority: false,
                        EpicTrafficKind.Background,
                        ct,
                        work,
                        ex => _log.LogDebug(
                            ex,
                            "Registered-player band discovery lookup failed."));
                }
                catch (OperationCanceledException)
                {
                    RegisteredLookupInstrumentation.Record(
                        stopwatch,
                        "discovery",
                        RegisteredLookupOutcome.Cancelled);
                    throw;
                }

                if (!lookupResult.Succeeded || lookupResult.Value is null)
                {
                    var outcome = RegisteredLookupInstrumentation.ClassifyFailure(
                        lookupResult.Exception);
                    RegisteredLookupInstrumentation.Record(
                        stopwatch,
                        "discovery",
                        outcome);
                    if (outcome == RegisteredLookupOutcome.InvalidLeaderboard)
                    {
                        _progress.ReportPhaseRetryableUnavailable();
                        _metaDb.MarkRegisteredPlayerBandDiscoveryAttempted(
                            accountId,
                            intent.SongId,
                            intent.BandType,
                            intent.ProgressScope,
                            intent.Season,
                            intent.WindowId);
                    }
                    break;
                }

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
                    {
                        _progress.ReportPhaseEntryUpdated(persisted);
                        foreach (var entry in entries)
                        {
                            if (!impactedTeams.TryGetValue(intent.BandType, out var teams))
                            {
                                teams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                impactedTeams[intent.BandType] = teams;
                            }

                            teams.Add(entry.TeamKey);
                            BandCurrentProjectionScopeTracker.AddScopes(
                                impactedCurrentProjectionScopes,
                                intent.SongId,
                                intent.BandType,
                                entry.InstrumentCombo);
                        }
                    }

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
                            true,
                            intent.WindowId);
                    }
                }

                _metaDb.MarkRegisteredPlayerBandDiscoveryChecked(
                    accountId,
                    intent.SongId,
                    intent.BandType,
                    intent.ProgressScope,
                    intent.Season,
                    found,
                    intent.WindowId);

                lookupsChecked++;
                _progress.ReportPhaseItemComplete();
                RegisteredLookupInstrumentation.Record(
                    stopwatch,
                    "discovery",
                    found
                        ? RegisteredLookupOutcome.Success
                        : RegisteredLookupOutcome.NotFound);
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new PartialResultOperationCanceledException<AccountDiscoveryRunResult>(
                BuildResult(),
                ex);
        }
        catch (Exception ex)
        {
            throw new PartialResultFailureException<AccountDiscoveryRunResult>(
                BuildResult(),
                ex);
        }

        return BuildResult();
    }

    private sealed record AccountDiscoveryRunResult(
        int LookupsAttempted,
        int LookupsChecked,
        int EntriesFound,
        int EntriesPersisted,
        IReadOnlyDictionary<string, HashSet<string>> ImpactedTeamsByBandType,
        IReadOnlyCollection<BandCurrentProjectionScopeKey> ImpactedCurrentProjectionScopes);
}
