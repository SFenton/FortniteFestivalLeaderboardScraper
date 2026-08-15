using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FSTService.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace FSTService.Scraping;

public sealed record MaxScoreMaintenanceDerivedStateResult(
    int RebuiltInstrumentCount,
    int BandThresholdRowsUpdated,
    int BandProjectionScopeCount,
    int AffectedPlayerStatsAccountCount,
    int RebuiltLeaderboardRivalsAccountCount);

public sealed class MaxScoreMaintenanceDerivedStateService
{
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly IPathDataStore _pathDataStore;
    private readonly RankingsCalculator _rankings;
    private readonly BandRankingRepairService _bandRankingRepair;
    private readonly BandCurrentProjectionBuilder _bandProjection;
    private readonly LeaderboardRivalsCalculator _leaderboardRivals;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<MaxScoreMaintenanceDerivedStateService> _log;

    public MaxScoreMaintenanceDerivedStateService(
        GlobalLeaderboardPersistence persistence,
        IPathDataStore pathDataStore,
        RankingsCalculator rankings,
        BandRankingRepairService bandRankingRepair,
        BandCurrentProjectionBuilder bandProjection,
        LeaderboardRivalsCalculator leaderboardRivals,
        NpgsqlDataSource dataSource,
        IOptions<ScraperOptions> options,
        ILogger<MaxScoreMaintenanceDerivedStateService> log)
    {
        _persistence = persistence;
        _pathDataStore = pathDataStore;
        _rankings = rankings;
        _bandRankingRepair = bandRankingRepair;
        _bandProjection = bandProjection;
        _leaderboardRivals = leaderboardRivals;
        _dataSource = dataSource;
        _log = log;
    }

    public async Task<MaxScoreMaintenanceDerivedStateResult> RebuildAsync(
        MaxScoreMaintenanceManifest manifest,
        IReadOnlyList<Song> publishedCatalogSongs,
        IReadOnlyDictionary<
            (string SongId, string Instrument),
            long> publicationPopulation,
        IReadOnlyCollection<string> affectedStatsAccounts,
        IMaxScoreMaintenanceLease maintenanceLease,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(publishedCatalogSongs);
        ArgumentNullException.ThrowIfNull(publicationPopulation);
        ArgumentNullException.ThrowIfNull(affectedStatsAccounts);
        ArgumentNullException.ThrowIfNull(maintenanceLease);
        if (publishedCatalogSongs.Count != manifest.CatalogSongCount)
        {
            throw new InvalidOperationException(
                "Max-score derived rebuild requires the exact manifest-bound catalog.");
        }

        var affectedInstruments =
            manifest.Scope.ExpectedChangedInstruments.ToArray();
        if (affectedInstruments.Length == 0)
        {
            throw new InvalidOperationException(
                "Max-score derived rebuild has no affected instruments.");
        }

        if (!_persistence
                .IsMaxScoreMaintenancePublishedReadPassActive)
        {
            throw new InvalidOperationException(
                "Max-score derived rebuild requires the strict published-source read context.");
        }

        var festivalService =
            FestivalService.CreateFromSongCatalogSnapshot(
                publishedCatalogSongs);
        var bandThresholdRowsUpdated =
            await maintenanceLease.ExecuteTransactionAsync(
                "derived-band-thresholds",
                requireSourceLocks: true,
                (connection, transaction, _) =>
                    Task.FromResult(
                        _bandRankingRepair
                            .RecomputeOverThresholdFlagsForSongs(
                                manifest.Songs
                                    .Select(song => song.SongId)
                                    .ToArray(),
                                connection,
                                transaction)),
                ct: ct);
        var targetSongIds = manifest.Songs
            .Select(song => song.SongId)
            .ToHashSet(StringComparer.Ordinal);
        var currentScopes = (await _bandProjection.LoadCurrentScopesAsync(
                ct: ct))
            .Where(scope => targetSongIds.Contains(scope.SongId))
            .ToArray();
        var priorScopes = await LoadPublishedBandScopesAsync(
            targetSongIds,
            ct);
        var bandScopes = currentScopes
            .Concat(priorScopes)
            .Distinct()
            .OrderBy(scope => scope.BandType, StringComparer.Ordinal)
            .ThenBy(scope => scope.RankingScope, StringComparer.Ordinal)
            .ThenBy(scope => scope.ScopeComboId, StringComparer.Ordinal)
            .ThenBy(scope => scope.SongId, StringComparer.Ordinal)
            .ToArray();
        if (bandScopes.Length > 0)
        {
            var projection = await _bandProjection
                .RefreshScopesForMaxScoreMaintenanceAsync(
                bandScopes,
                maintenanceLease,
                ct: ct);
            if (projection.FailedScopes > 0
                || projection.ScopeCount > 0
                && !projection.PublishResult.Published)
            {
                throw new InvalidOperationException(
                    $"Band current projection refresh failed for {projection.FailedScopes:N0}/{projection.ScopeCount:N0} affected scope(s).");
            }
        }
        await _rankings.ComputeForMaxScoreMaintenanceAsync(
            festivalService,
            affectedInstruments,
            publicationPopulation,
            maintenanceLease,
            ct);

        await PlayerStatsTierRebuilder
            .RebuildForMaxScoreMaintenanceAsync(
            _persistence,
            _pathDataStore,
            affectedStatsAccounts,
            _log,
            publicationPopulation,
            maintenanceLease,
            ct);

        var registeredAccounts = _persistence.Meta
            .GetRegisteredAccountIds()
            .OrderBy(accountId => accountId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var rivalsRebuilt = 0;
        foreach (var accountId in registeredAccounts)
        {
            ct.ThrowIfCancellationRequested();
            await _leaderboardRivals
                .ComputeForUserForMaxScoreMaintenanceAsync(
                    accountId,
                    maintenanceLease,
                    rankingsAuthoritative: true,
                    ct);
            rivalsRebuilt++;
        }

        return new MaxScoreMaintenanceDerivedStateResult(
            affectedInstruments.Length,
            bandThresholdRowsUpdated,
            bandScopes.Length,
            affectedStatsAccounts.Count,
            rivalsRebuilt);
    }

    private async Task<IReadOnlyList<BandCurrentProjectionScopeKey>>
        LoadPublishedBandScopesAsync(
            IReadOnlySet<string> songIds,
            CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT song_id,
                   band_type,
                   ranking_scope,
                   scope_combo_id
            FROM band_current_projection_scope
            WHERE song_id = ANY(@songIds)
            ORDER BY band_type,
                     ranking_scope,
                     scope_combo_id,
                     song_id
            """;
        cmd.Parameters.Add(
            "songIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            songIds.ToArray();
        var result = new List<BandCurrentProjectionScopeKey>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new BandCurrentProjectionScopeKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }
        return result;
    }

    internal static async Task<IReadOnlyList<string>>
        LoadAffectedPlayerStatsAccountsAsync(
            MaxScoreMaintenanceManifest manifest,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(
                transaction.Connection,
                connection))
        {
            throw new ArgumentException(
                "The affected-account transaction must belong to the supplied connection.",
                nameof(transaction));
        }

        var songIds = new List<string>();
        var instruments = new List<string>();
        foreach (var song in manifest.Songs)
        {
            foreach (var instrument in song.ChangedInstruments)
            {
                songIds.Add(song.SongId);
                instruments.Add(instrument);
            }
        }

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = 600;
        cmd.CommandText = $"""
            WITH affected(song_id, instrument) AS (
                SELECT *
                FROM unnest(
                    @songIds::TEXT[],
                    @instruments::TEXT[])
            ),
            {PublishedSoloScopeSql.CurrentResolvedAffectedEntriesCte}
            SELECT DISTINCT resolved.account_id
            FROM resolved_rows resolved
            ORDER BY resolved.account_id
            """;
        cmd.Parameters.Add(
            "songIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            songIds.ToArray();
        cmd.Parameters.Add(
            "instruments",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            instruments.ToArray();
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    internal static async Task<long>
        CountMissingPlayerStatsAccountsAsync(
            IReadOnlyCollection<string> accountIds,
            DateTime minimumUpdatedAtUtc,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(
                transaction.Connection,
                connection))
        {
            throw new ArgumentException(
                "The player-stats validation transaction must belong to the supplied connection.",
                nameof(transaction));
        }
        if (accountIds.Count == 0)
            return 0;
        if (minimumUpdatedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "The player-stats validation timestamp must be UTC.",
                nameof(minimumUpdatedAtUtc));
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)::BIGINT
            FROM unnest(@accountIds::TEXT[])
                affected(account_id)
            WHERE NOT EXISTS (
                SELECT 1
                FROM player_stats_tiers stats
                WHERE stats.account_id =
                    affected.account_id
                  AND stats.updated_at >=
                      @minimumUpdatedAt
            )
            """;
        command.Parameters.Add(
            "accountIds",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            accountIds.ToArray();
        command.Parameters.AddWithValue(
            "minimumUpdatedAt",
            minimumUpdatedAtUtc);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(ct));
    }
}
