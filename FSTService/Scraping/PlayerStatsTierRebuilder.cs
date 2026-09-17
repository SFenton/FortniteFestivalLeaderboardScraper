using FSTService.Persistence;

namespace FSTService.Scraping;

public sealed record PlayerStatsTierRebuildResult(
    int RequestedAccounts,
    int RebuiltAccounts,
    int WrittenRows);

public static class PlayerStatsTierRebuilder
{
    private const int AccountChunkSize = 512;

    public static Task<PlayerStatsTierRebuildResult> RebuildAsync(
        GlobalLeaderboardPersistence persistence,
        IPathDataStore pathDataStore,
        IReadOnlyCollection<string> accountIds,
        ILogger log,
        CancellationToken ct,
        Action<int, int>? onProgress = null)
        => RebuildCoreAsync(
            persistence,
            pathDataStore,
            accountIds,
            log,
            maintenanceLease: null,
            maxScoresOverride: null,
            populationOverride: null,
            onProgress: onProgress,
            ct: ct);

    internal static Task<PlayerStatsTierRebuildResult>
        RebuildForMaxScoreMaintenanceAsync(
            GlobalLeaderboardPersistence persistence,
            IPathDataStore pathDataStore,
            IReadOnlyCollection<string> accountIds,
            ILogger log,
            IReadOnlyDictionary<string, SongMaxScores>
                publicationMaxScores,
            IReadOnlyDictionary<
                (string SongId, string Instrument),
                long> publicationPopulation,
            IMaxScoreMaintenanceLease maintenanceLease,
            CancellationToken ct)
        => RebuildCoreAsync(
            persistence,
            pathDataStore,
            accountIds,
            log,
            maintenanceLease
                ?? throw new ArgumentNullException(
                    nameof(maintenanceLease)),
            publicationMaxScores
                ?? throw new ArgumentNullException(
                    nameof(publicationMaxScores)),
            publicationPopulation
                ?? throw new ArgumentNullException(
                    nameof(publicationPopulation)),
            onProgress: null,
            ct: ct);

    private static async Task<PlayerStatsTierRebuildResult>
        RebuildCoreAsync(
            GlobalLeaderboardPersistence persistence,
            IPathDataStore pathDataStore,
            IReadOnlyCollection<string> accountIds,
            ILogger log,
            IMaxScoreMaintenanceLease? maintenanceLease,
            IReadOnlyDictionary<string, SongMaxScores>?
                maxScoresOverride,
            IReadOnlyDictionary<
                (string SongId, string Instrument),
                long>? populationOverride,
            Action<int, int>? onProgress,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(pathDataStore);
        ArgumentNullException.ThrowIfNull(accountIds);
        ArgumentNullException.ThrowIfNull(log);

        if (accountIds.Count == 0)
        {
            return new PlayerStatsTierRebuildResult(0, 0, 0);
        }

        var normalizedAccountIds =
            MaxScoreMaintenanceAccountIdPolicy
                .NormalizeSet(accountIds);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var allMaxScores = maintenanceLease is null
            ? pathDataStore.GetAllMaxScores()
            : maxScoresOverride?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase)
              ?? throw new InvalidOperationException(
                  "Max-score player stats require publication-owned maximum scores.");
        var metaDb = persistence.Meta;
        var instrumentKeys = maintenanceLease is null
            ? persistence.GetInstrumentKeys()
            : GlobalLeaderboardScraper.AllInstruments
                .Where(instrument =>
                    populationOverride!.Keys.Any(scope =>
                        string.Equals(
                            scope.Instrument,
                            instrument,
                            StringComparison.Ordinal)))
                .ToArray();
        var totalSongs = maintenanceLease is null
            ? persistence.GetTotalSongCount()
            : populationOverride!.Keys
                .Select(scope => scope.SongId)
                .Distinct(StringComparer.Ordinal)
                .Count();
        var population = maintenanceLease is null
            ? metaDb.GetAllLeaderboardPopulation()
            : populationOverride
              ?? throw new InvalidOperationException(
                  "Max-score player stats require an immutable publication population snapshot.");
        var maintenanceScopes = maintenanceLease is null
            ? null
            : population.Keys.ToHashSet();
        var totalSongsByInstrument = maintenanceLease is null
            ? null
            : population.Keys
                .GroupBy(
                    scope => scope.Instrument,
                    StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(scope => scope.SongId)
                        .Distinct(StringComparer.Ordinal)
                        .Count(),
                    StringComparer.Ordinal);
        var overallTotalSongs = maintenanceLease is null
            ? (int?)null
            : population.Count;
        var rebuiltAccounts = 0;
        var writtenRows = 0;
        var processedAccounts = 0;

        foreach (var accountChunk in normalizedAccountIds.Chunk(
                     AccountChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            var profilesByAccount =
                persistence.GetCurrentStatePlayerProfiles(accountChunk);
            var rows = new List<PlayerStatsTiersRow>();
            foreach (var accountId in accountChunk)
            {
                ct.ThrowIfCancellationRequested();
                if (!profilesByAccount.TryGetValue(
                        accountId,
                        out var allScores)
                    || allScores.Count == 0)
                {
                    continue;
                }
                if (maintenanceScopes is not null)
                {
                    allScores = allScores
                        .Where(score => maintenanceScopes.Contains(
                            (score.SongId, score.Instrument)))
                        .ToList();
                    if (allScores.Count == 0)
                        continue;
                }

                Dictionary<
                    (string SongId, string Instrument),
                    List<ValidScoreFallback>>? fallbacks = null;
                var maxThresholds =
                    PlayerStatsTierRowBuilder.BuildAboveMaxThresholds(
                        allScores,
                        allMaxScores);
                if (maxThresholds.Count > 0)
                {
                    fallbacks = metaDb.GetAllValidScoreTiers(
                        accountId,
                        maxThresholds);
                }

                var accountRows = PlayerStatsTierRowBuilder.BuildRows(
                    accountId,
                    allScores,
                    instrumentKeys,
                    totalSongs,
                    allMaxScores,
                    population,
                    fallbacks,
                    totalSongsByInstrument,
                    overallTotalSongs);
                if (accountRows.Count == 0)
                    continue;

                rows.AddRange(accountRows);
                rebuiltAccounts++;
            }

            if (maintenanceLease is null)
            {
                if (rows.Count > 0)
                    metaDb.UpsertPlayerStatsTiersBatch(rows);
            }
            else
            {
                await maintenanceLease.ExecuteTransactionAsync(
                    $"derived-player-stats:{accountChunk[0]}",
                    requireSourceLocks: true,
                    (connection, transaction, _) =>
                    {
                        metaDb
                            .ReplacePlayerStatsTiersForMaxScoreMaintenance(
                                accountChunk,
                                instrumentKeys,
                                rows,
                                connection,
                                transaction);
                        return Task.CompletedTask;
                    },
                    ct: ct);
            }
            if (rows.Count > 0)
            {
                writtenRows += rows.Count;
            }
            processedAccounts += accountChunk.Length;
            onProgress?.Invoke(
                Math.Min(processedAccounts, normalizedAccountIds.Length),
                normalizedAccountIds.Length);
        }

        sw.Stop();
        log.LogInformation(
            "Rebuilt player stats tiers for {Rebuilt:N0}/{Requested:N0} accounts with {Rows:N0} rows in {Elapsed:F1}s.",
            rebuiltAccounts,
            normalizedAccountIds.Length,
            writtenRows,
            sw.Elapsed.TotalSeconds);
        return new PlayerStatsTierRebuildResult(
            normalizedAccountIds.Length,
            rebuiltAccounts,
            writtenRows);
    }
}
