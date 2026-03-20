namespace FSTService.Api;

/// <summary>
/// Maps all HTTP API endpoints onto the <see cref="WebApplication"/>.
/// Endpoints are organized into domain-specific extension methods for navigability.
/// </summary>
public static partial class ApiEndpoints
{
    public static void MapApiEndpoints(this WebApplication app)
    {
        app.MapHealthEndpoints();
        app.MapAccountEndpoints();
        app.MapSongEndpoints();
        app.MapLeaderboardEndpoints();
        app.MapPlayerEndpoints();
        app.MapRivalsEndpoints();
        app.MapAdminEndpoints();
        app.MapSyncEndpoints();
        app.MapDiagEndpoints();
    }
}

/// <summary>Request body for POST /api/register.</summary>
public sealed class RegisterRequest
{
    public string DeviceId { get; set; } = "";
    public string Username { get; set; } = "";
}

/// <summary>Request body item for POST /api/leaderboard-population.</summary>
public sealed class LeaderboardPopulationRequest
{
    public string SongId { get; set; } = "";
    public string Instrument { get; set; } = "";
    public long TotalEntries { get; set; }
}
