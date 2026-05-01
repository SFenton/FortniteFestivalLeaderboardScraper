namespace FSTService.Scraping;

/// <summary>
/// Helpers for normalized band combo identifiers.
/// Unlike <see cref="ComboIds"/>, band combos must preserve repeated instruments
/// (for example, <c>Solo_Guitar+Solo_Guitar</c> for Lead/Lead duos).
/// </summary>
public static class BandComboIds
{
    private static readonly string[] CanonicalOrder =
    [
        "Solo_Guitar",
        "Solo_Bass",
        "Solo_Drums",
        "Solo_Vocals",
        "Solo_PeripheralGuitar",
        "Solo_PeripheralBass",
        "Solo_PeripheralVocals",
        "Solo_PeripheralCymbals",
        "Solo_PeripheralDrums",
    ];

    private static readonly Dictionary<string, int> SortOrder = CanonicalOrder
        .Select((instrument, index) => new { instrument, index })
        .ToDictionary(x => x.instrument, x => x.index, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> LeaderboardToEpicId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Solo_Guitar"] = "0",
        ["Solo_Bass"] = "1",
        ["Solo_Vocals"] = "2",
        ["Solo_Drums"] = "3",
        ["Solo_PeripheralGuitar"] = "4",
        ["Solo_PeripheralBass"] = "5",
        ["Solo_PeripheralDrums"] = "6",
        ["Solo_PeripheralVocals"] = "7",
        ["Solo_PeripheralCymbals"] = "8",
    };

    public static bool IsValidBandType(string bandType) =>
        BandInstrumentMapping.AllBandTypes.Contains(bandType, StringComparer.OrdinalIgnoreCase);

    public static string FromEpicRawCombo(string? rawCombo)
    {
        if (string.IsNullOrWhiteSpace(rawCombo))
            return string.Empty;

        var instruments = new List<string>();
        foreach (var part in rawCombo.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, out var instrumentId))
                continue;

            var mapped = BandInstrumentMapping.ToLeaderboardType(instrumentId);
            if (mapped is not null)
                instruments.Add(mapped);
        }

        return FromInstruments(instruments);
    }

    public static string FromInstruments(IEnumerable<string> instruments)
    {
        var normalized = instruments
            .Where(static instrument => !string.IsNullOrWhiteSpace(instrument))
            .Select(static instrument => instrument.Trim())
            .OrderBy(static instrument => SortOrder.GetValueOrDefault(instrument, int.MaxValue))
            .ThenBy(static instrument => instrument, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length == 0 ? string.Empty : string.Join('+', normalized);
    }

    public static IReadOnlyList<string> ToInstruments(string? comboId)
    {
        if (string.IsNullOrWhiteSpace(comboId))
            return [];

        return comboId
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static instrument => instrument.Trim())
            .ToArray();
    }

    public static bool TryNormalizeComboParam(string? comboParam, out string comboId)
    {
        comboId = string.Empty;

        if (string.IsNullOrWhiteSpace(comboParam))
            return false;

        var value = comboParam.Trim().Replace(' ', '+');

        if (value.Contains(':'))
        {
            comboId = FromEpicRawCombo(value);
            return !string.IsNullOrEmpty(comboId);
        }

        var instruments = value
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static instrument => instrument.Trim())
            .ToArray();

        if (instruments.Length == 0 || instruments.Any(static instrument => !SortOrder.ContainsKey(instrument)))
            return false;

        comboId = FromInstruments(instruments);
        return !string.IsNullOrEmpty(comboId);
    }

    public static (string? ComboId, string? Error) TryNormalizeForBandType(string bandType, string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo))
            return (null, null);

        if (!TryNormalizeComboParam(combo, out var comboId))
            return (null, "Invalid band combo. Use a normalized server-instrument list such as Solo_Guitar+Solo_Bass.");

        if (ToInstruments(comboId).Count != BandInstrumentMapping.ExpectedMemberCount(bandType))
            return (null, $"Combo size does not match band type {bandType}.");

        return (comboId, null);
    }

    public static IReadOnlyList<string> ToEpicRawComboCandidates(string? comboId)
    {
        var instruments = ToInstruments(comboId);
        if (instruments.Count == 0)
            return [];

        var ids = new string[instruments.Count];
        for (var i = 0; i < instruments.Count; i++)
        {
            if (!LeaderboardToEpicId.TryGetValue(instruments[i], out var id))
                return [];
            ids[i] = id;
        }

        var results = new HashSet<string>(StringComparer.Ordinal);
        PermuteRawComboIds(ids, 0, results);
        return results.Order(StringComparer.Ordinal).ToArray();
    }

    private static void PermuteRawComboIds(string[] ids, int index, HashSet<string> results)
    {
        if (index == ids.Length)
        {
            results.Add(string.Join(':', ids));
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = index; i < ids.Length; i++)
        {
            if (!seen.Add(ids[i]))
                continue;
            (ids[index], ids[i]) = (ids[i], ids[index]);
            PermuteRawComboIds(ids, index + 1, results);
            (ids[index], ids[i]) = (ids[i], ids[index]);
        }
    }
}
