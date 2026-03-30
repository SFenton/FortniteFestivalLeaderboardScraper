using FortniteFestival.Core;
using FortniteFestival.Core.Persistence;
using Npgsql;

namespace FSTService.Persistence.Pg;

/// <summary>
/// PostgreSQL implementation of <see cref="IFestivalPersistence"/> for the Core library's
/// FestivalService. Reads/writes the <c>songs</c> table in PostgreSQL.
/// The Scores table is not migrated — leaderboard data lives in leaderboard_entries.
/// </summary>
public sealed class PgFestivalPersistence : IFestivalPersistence
{
    private readonly NpgsqlDataSource _ds;

    public PgFestivalPersistence(NpgsqlDataSource dataSource)
    {
        _ds = dataSource;
    }

    public Task<IList<Song>> LoadSongsAsync()
    {
        // TODO: Port from SqlitePersistence — read from PG songs table and map to Song objects
        throw new NotImplementedException();
    }

    public Task SaveSongsAsync(IEnumerable<Song> songs)
    {
        // TODO: Port from SqlitePersistence — upsert into PG songs table
        throw new NotImplementedException();
    }

    public Task<IList<LeaderboardData>> LoadScoresAsync()
    {
        // The per-user Scores table is deprecated in PG — data lives in leaderboard_entries
        return Task.FromResult<IList<LeaderboardData>>(new List<LeaderboardData>());
    }

    public Task SaveScoresAsync(IEnumerable<LeaderboardData> scores)
    {
        // No-op: scores are managed by GlobalLeaderboardPersistence via leaderboard_entries
        return Task.CompletedTask;
    }
}
