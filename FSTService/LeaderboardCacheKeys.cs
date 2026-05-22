using System.Globalization;

namespace FSTService;

public static class LeaderboardCacheKeys
{
    public const int SongDetailPreviewTop = 10;

    public static string LeaderboardAll(string songId, int top, double? leeway)
        => leeway.HasValue
            ? $"lb-rank-offsets-v1:{songId}:{top}:{FormatLeeway(leeway)}"
            : $"lb:{songId}:{top}:";

    public static string SongBandLeaderboardsAll(string songId, int top)
        => $"song-bands-all:{songId}:{top}";

    public static string LeaderboardRankOffsets(string songId, string instrument)
        => $"lb-rank-offsets:{songId}:{instrument}";

    private static string FormatLeeway(double? leeway)
        => leeway.HasValue
            ? leeway.Value.ToString("0.########", CultureInfo.InvariantCulture)
            : string.Empty;
    }
