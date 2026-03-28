namespace FSTService;

/// <summary>
/// Deterministic bitmask-based combo IDs for instrument combinations.
///
/// Each of the 6 instruments occupies a fixed bit position (0–5) in the canonical order
/// defined by <see cref="CanonicalOrder"/>. A combo ID is the zero-padded 2-digit lowercase
/// hex representation of the bitmask. For example:
///   Lead + Bass         → bits 0+1 → 0x03 → "03"
///   All Pad (G+B+D+V)  → bits 0-3 → 0x0f → "0f"
///   All 6               → all bits → 0x3f → "3f"
///
/// This MUST stay in sync with packages/core/src/combos.ts.
/// </summary>
public static class ComboIds
{
    /// <summary>
    /// Canonical instrument order — the index of each key is its bit position.
    /// Must match SERVER_INSTRUMENT_KEYS / COMBO_INSTRUMENTS in @festival/core.
    /// </summary>
    public static readonly IReadOnlyList<string> CanonicalOrder = new[]
    {
        "Solo_Guitar",
        "Solo_Bass",
        "Solo_Drums",
        "Solo_Vocals",
        "Solo_PeripheralGuitar",
        "Solo_PeripheralBass",
    };

    /// <summary>Compute the combo ID (2-digit hex) for a set of instrument keys.</summary>
    public static string FromInstruments(IEnumerable<string> instruments)
    {
        int mask = 0;
        foreach (var key in instruments)
        {
            int bit = IndexOf(key);
            if (bit < 0) throw new ArgumentException($"Unknown instrument: {key}");
            mask |= 1 << bit;
        }
        return mask.ToString("x2");
    }

    /// <summary>Compute the combo ID directly from a bitmask (used by ComputeAllCombos).</summary>
    public static string FromMask(int mask) => mask.ToString("x2");

    /// <summary>Recover the instrument list from a combo ID. Returns instruments in canonical order.</summary>
    public static List<string> ToInstruments(string comboId)
    {
        int mask = Convert.ToInt32(comboId, 16);
        if (mask < 0 || mask > 0x3F) throw new ArgumentException($"Invalid combo ID: {comboId}");
        var result = new List<string>();
        for (int bit = 0; bit < CanonicalOrder.Count; bit++)
        {
            if ((mask & (1 << bit)) != 0)
                result.Add(CanonicalOrder[bit]);
        }
        return result;
    }

    /// <summary>
    /// Normalize a combo parameter — accepts EITHER an old-style "Solo_Bass+Solo_Guitar"
    /// key string OR a hex combo ID like "03". Returns the combo ID.
    /// Returns null if fewer than 2 instruments.
    /// </summary>
    public static string? NormalizeComboParam(string? param)
    {
        if (string.IsNullOrWhiteSpace(param)) return null;

        // If it looks like a hex combo ID (1-2 hex chars), validate and return
        if (param.Length <= 2 && int.TryParse(param, System.Globalization.NumberStyles.HexNumber, null, out int mask))
        {
            if (mask >= 0 && mask <= 0x3F && BitCount(mask) >= 2)
                return mask.ToString("x2");
        }

        // Otherwise treat as "Instrument+Instrument+..." legacy format
        var parts = param.Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (parts.Count < 2) return null;

        // Validate all instruments exist
        foreach (var p in parts)
        {
            if (IndexOf(p) < 0) return null;
        }

        return FromInstruments(parts);
    }

    private static int IndexOf(string instrument)
    {
        for (int i = 0; i < CanonicalOrder.Count; i++)
        {
            if (CanonicalOrder[i].Equals(instrument, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static int BitCount(int value)
    {
        int count = 0;
        while (value != 0) { count += value & 1; value >>= 1; }
        return count;
    }
}
