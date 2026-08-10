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
        app.MapFeatureEndpoints();
        app.MapAccountEndpoints();
        app.MapSongEndpoints();
        app.MapLeaderboardEndpoints();
        app.MapPlayerEndpoints();
        app.MapExportEndpoints();
        app.MapBandSyncEndpoints();
        app.MapRivalsEndpoints();
        app.MapLeaderboardRivalsEndpoints();
        app.MapRankingsEndpoints();
        app.MapImprovementNotificationEndpoints();
        app.MapAdminEndpoints();
        app.MapDiagEndpoints();
        app.MapWebSocketEndpoints();
    }
}
