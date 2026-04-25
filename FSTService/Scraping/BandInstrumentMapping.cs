namespace FSTService.Scraping;

/// <summary>
/// Maps between Epic's numeric instrument IDs used in band <c>trackedStats</c>
/// (<c>M_{i}_INSTRUMENT</c>, <c>INSTRUMENT_{i}</c>) and the leaderboard type
/// strings used for CHOpt lookups and solo leaderboards.
///
/// Verified empirically via V1 API <c>M_0_INSTRUMENT</c> extraction:
///   0 = Solo_Guitar, 1 = Solo_Bass, 2 = Solo_Vocals, 3 = Solo_Drums,
///   4 = Solo_PeripheralGuitar, 5 = Solo_PeripheralBass,
///   6 = Solo_PeripheralDrums (Pro Drums),
///   7 = Solo_PeripheralVocals (Karaoke),
///   8 = Solo_PeripheralCymbals (Pro Drums + Cymbals).
///
/// WARNING: This does NOT match the C# InstrumentType enum ordering
/// (Lead=0, Drums=1, Vocals=2, Bass=3, ProLead=4, ProBass=5, ...).
/// It also does NOT match ComboIds.CanonicalOrder — the canonical-order
/// 6/7/8 slots are Vocals/Cymbals/Drums while Epic's IDs 6/7/8 are
/// Drums/Vocals/Cymbals. This mapping is the indirection that reconciles them.
/// </summary>
public static class BandInstrumentMapping
{
    private static readonly string[] IdToLeaderboard =
    [
        "Solo_Guitar",            // 0
        "Solo_Bass",              // 1
        "Solo_Vocals",            // 2
        "Solo_Drums",             // 3
        "Solo_PeripheralGuitar",  // 4
        "Solo_PeripheralBass",    // 5
        "Solo_PeripheralDrums",   // 6 - Pro Drums
        "Solo_PeripheralVocals",  // 7 - Karaoke
        "Solo_PeripheralCymbals", // 8 - Pro Drums + Cymbals
    ];

    /// <summary>
    /// The 3 band leaderboard type strings used in the alltime URL pattern.
    /// </summary>
    public static readonly IReadOnlyList<string> AllBandTypes = new[]
    {
        "Band_Duets",
        "Band_Trios",
        "Band_Quad",
    };

    /// <summary>
    /// Convert an Epic numeric instrument ID (0–5) to a Solo leaderboard type string.
    /// Returns null if the ID is out of range.
    /// </summary>
    public static string? ToLeaderboardType(int instrumentId) =>
        instrumentId >= 0 && instrumentId < IdToLeaderboard.Length
            ? IdToLeaderboard[instrumentId]
            : null;

    /// <summary>
    /// Expected number of team members for a given band type.
    /// </summary>
    public static int ExpectedMemberCount(string bandType) => bandType switch
    {
        "Band_Duets" => 2,
        "Band_Trios" => 3,
        "Band_Quad" => 4,
        _ => 0,
    };
}
