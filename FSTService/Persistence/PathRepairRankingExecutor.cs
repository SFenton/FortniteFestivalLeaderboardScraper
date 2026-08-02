using FortniteFestival.Core;
using FortniteFestival.Core.Services;
using FSTService.Scraping;

namespace FSTService.Persistence;

public interface IPathRepairRankingExecutor
{
    Task RebuildAsync(
        IReadOnlyList<Song> publishedCatalogSongs,
        CancellationToken ct);
}

public sealed class PathRepairRankingExecutor : IPathRepairRankingExecutor
{
    private readonly GlobalLeaderboardPersistence _persistence;
    private readonly RankingsCalculator _rankings;

    public PathRepairRankingExecutor(
        GlobalLeaderboardPersistence persistence,
        RankingsCalculator rankings)
    {
        _persistence = persistence;
        _rankings = rankings;
    }

    public async Task RebuildAsync(
        IReadOnlyList<Song> publishedCatalogSongs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(publishedCatalogSongs);
        if (publishedCatalogSongs.Count == 0)
        {
            throw new InvalidOperationException(
                "Published repair catalog cannot be empty.");
        }

        foreach (var instrument in GlobalLeaderboardScraper.AllInstruments)
            _persistence.GetOrCreateInstrumentDb(instrument);

        var festivalService =
            FestivalService.CreateFromSongCatalogSnapshot(
                publishedCatalogSongs);
        await _rankings.ComputeAllForPathRepairAsync(
            festivalService,
            ct);
    }
}
