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
        CancellationToken ct)
        => RebuildCoreAsync(
            persistence,
            pathDataStore,
            accountIds,
            log,
            maintenanceLease: null,
            ct: ct);

    internal static Task<PlayerStatsTierRebuildResult>
        RebuildForMaxScoreMaintenanceAsync(
            GlobalLeaderboardPersistence persistence,
            IPathDataStore pathDataStore,
            IReadOnlyCollection<string> accountIds,
            ILogger log,
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
            ct: ct);

    private static async Task<PlayerStatsTierRebuildResult>
        RebuildCoreAsync(
            GlobalLeaderboardPersistence persistence,
            IPathDataStore pathDataStore,
            IReadOnlyCollection<string> accountIds,
            ILogger log,
            IMaxScoreMaintenanceLease? maintenanceLease,
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

        var normalizedAccountIds = accountIds
            .Where(accountId => !string.IsNullOrWhiteSpace(accountId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(accountId => accountId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var allMaxScores = pathDataStore.GetAllMaxScores();
        var metaDb = persistence.Meta;
        var instrumentKeys = persistence.GetInstrumentKeys();
        var totalSongs = persistence.GetTotalSongCount();
        var population = metaDb.GetAllLeaderboardPopulation();
        var rebuiltAccounts = 0;
        var writtenRows = 0;

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
                    fallbacks);
                if (accountRows.Count == 0)
                    continue;

                rows.AddRange(accountRows);
                rebuiltAccounts++;
            }

            if (rows.Count > 0)
            {
                if (maintenanceLease is null)
                {
                    metaDb.UpsertPlayerStatsTiersBatch(rows);
                }
                else
                {
                    await maintenanceLease.ExecuteTransactionAsync(
                        $"derived-player-stats:{accountChunk[0]}",
                        requireSourceLocks: true,
                        (connection, transaction, _) =>
                        {
                            metaDb.UpsertPlayerStatsTiersBatch(
                                rows,
                                connection,
                                transaction);
                            return Task.CompletedTask;
                        },
                        ct: ct);
                }
                writtenRows += rows.Count;
            }
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
