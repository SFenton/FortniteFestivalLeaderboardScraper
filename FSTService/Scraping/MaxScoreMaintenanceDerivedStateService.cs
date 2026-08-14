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
        IMaxScoreMaintenanceLease maintenanceLease,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(publishedCatalogSongs);
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

        _persistence.InitializeReadOnly();
        foreach (var instrument in GlobalLeaderboardScraper.AllInstruments)
            _persistence.GetOrCreateInstrumentDb(instrument);

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
            maintenanceLease,
            ct);

        var affectedStatsAccounts =
            await LoadAffectedPlayerStatsAccountsAsync(
                manifest,
                ct);
        await PlayerStatsTierRebuilder
            .RebuildForMaxScoreMaintenanceAsync(
            _persistence,
            _pathDataStore,
            affectedStatsAccounts,
            _log,
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

    private async Task<IReadOnlyList<string>>
        LoadAffectedPlayerStatsAccountsAsync(
            MaxScoreMaintenanceManifest manifest,
            CancellationToken ct)
    {
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

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 600;
        cmd.CommandText = """
            WITH affected(song_id, instrument) AS (
                SELECT *
                FROM unnest(
                    @songIds::TEXT[],
                    @instruments::TEXT[])
            )
            SELECT DISTINCT current.account_id
            FROM current_leaderboard_entries current
            JOIN affected
              ON affected.song_id = current.song_id
             AND affected.instrument = current.instrument
            ORDER BY current.account_id
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
}
