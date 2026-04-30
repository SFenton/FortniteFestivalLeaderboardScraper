using System.Globalization;

namespace FSTService;

public static class LeaderboardCacheKeys
{
    public const int SongDetailPreviewTop = 10;

    public static string LeaderboardAll(string songId, int top, double? leeway)
        => $"lb:{songId}:{top}:{FormatLeeway(leeway)}";

    public static string SongBandLeaderboardsAll(string songId, int top)
        => $"song-bands-all:{songId}:{top}";

    private static string FormatLeeway(double? leeway)
        => leeway.HasValue
            ? leeway.Value.ToString("0.########", CultureInfo.InvariantCulture)
            : string.Empty;
    }
