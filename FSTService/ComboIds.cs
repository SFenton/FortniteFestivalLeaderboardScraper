namespace FSTService;

/// <summary>
/// Deterministic bitmask-based combo IDs for instrument combinations.
///
/// Each of the 9 instruments occupies a fixed bit position (0–8) in the canonical order
/// defined by <see cref="CanonicalOrder"/>. A combo ID is the lowercase hex representation
/// of the bitmask, padded to a minimum of 2 digits for backwards compatibility with stored
/// 6-instrument rows. Masks ≥ 0x100 naturally widen to 3 digits. For example:
///   Lead + Bass         → bits 0+1   → 0x03  → "03"
///   All Pad (G+B+D+V)  → bits 0-3   → 0x0f  → "0f"
///   OG Band + Pro       → all low 6  → 0x3f  → "3f"
///   Peripheral Vocals   → bit 6      → 0x40  → "40"
///   Peripheral Drums    → bit 8      → 0x100 → "100"
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
        "Solo_PeripheralVocals",
        "Solo_PeripheralCymbals",
        "Solo_PeripheralDrums",
    };

    /// <summary>
    /// Instrument groups for combo ranking computation.
    /// Only within-group combos are computed (no cross-group).
    /// Each group is a bitmask of the instruments it contains.
    /// </summary>
    public static readonly IReadOnlyList<int> InstrumentGroups = new[]
    {
        0x0F,  // OG Band: Lead(0) + Bass(1) + Drums(2) + Vocals(3) = bits 0-3
        0x30,  // Pro Strings: Pro Lead(4) + Pro Bass(5) = bits 4-5
    };

    /// <summary>
    /// All valid within-group combo bitmasks (2+ instruments, all from same group).
    /// OG Band: C(4,2)+C(4,3)+C(4,4) = 6+4+1 = 11 combos.
    /// Pro Strings: C(2,2) = 1 combo.
    /// Total: 12 combos.
    /// </summary>
    public static readonly IReadOnlyList<int> WithinGroupComboMasks = BuildWithinGroupMasks();

    /// <summary>Returns true if the bitmask represents a within-group combo (2+ instruments, all from the same group).</summary>
    public static bool IsWithinGroupCombo(int mask)
    {
        if (BitCount(mask) < 2) return false;
        foreach (var group in InstrumentGroups)
        {
            if ((mask & ~group) == 0) return true;
        }
        return false;
    }

    /// <summary>Returns true if the combo ID (hex string) represents a within-group combo.</summary>
    public static bool IsWithinGroupCombo(string comboId)
    {
        int mask = Convert.ToInt32(comboId, 16);
        return IsWithinGroupCombo(mask);
    }

    private static int[] BuildWithinGroupMasks()
    {
        var result = new List<int>();
        foreach (var group in InstrumentGroups)
        {
            // Enumerate all subsets of this group with 2+ bits
            int n = CanonicalOrder.Count; // total bit positions
            for (int mask = 3; mask < (1 << n); mask++)
            {
                if (BitCount(mask) < 2) continue;
                if ((mask & ~group) == 0) // all bits within this group
                    result.Add(mask);
            }
        }
        result.Sort();
        return result.ToArray();
    }

    /// <summary>Compute the combo ID (2-digit hex, wider when mask ≥ 0x100) for a set of instrument keys.</summary>
    public static string FromInstruments(IEnumerable<string> instruments)
    {
        int mask = 0;
        foreach (var key in instruments)
        {
            int bit = IndexOf(key);
            if (bit < 0) throw new ArgumentException($"Unknown instrument: {key}");
            mask |= 1 << bit;
        }
        return FormatMask(mask);
    }

    /// <summary>Compute the combo ID directly from a bitmask (used by ComputeAllCombos).</summary>
    public static string FromMask(int mask) => FormatMask(mask);

    /// <summary>
    /// Format a bitmask as a hex combo ID. Minimum 2-digit padding preserves
    /// backwards compatibility with stored 6-instrument rows; masks ≥ 0x100
    /// naturally widen to 3 digits when the 7th–9th instruments are involved.
    /// </summary>
    private static string FormatMask(int mask) => mask.ToString("x").PadLeft(2, '0');

    /// <summary>Recover the instrument list from a combo ID. Returns instruments in canonical order.</summary>
    public static List<string> ToInstruments(string comboId)
    {
        int mask = Convert.ToInt32(comboId, 16);
        int maxMask = (1 << CanonicalOrder.Count) - 1;
        if (mask < 0 || mask > maxMask) throw new ArgumentException($"Invalid combo ID: {comboId}");
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
    /// key string OR a hex combo ID like "03" or "1c0". Returns the combo ID.
    /// Returns null if fewer than 2 instruments.
    /// </summary>
    public static string? NormalizeComboParam(string? param)
    {
        if (string.IsNullOrWhiteSpace(param)) return null;

        int maxMask = (1 << CanonicalOrder.Count) - 1;

        // If it looks like a hex combo ID (1-3 hex chars), validate and return
        if (param.Length <= 3 && int.TryParse(param, System.Globalization.NumberStyles.HexNumber, null, out int mask))
        {
            if (mask >= 0 && mask <= maxMask && BitCount(mask) >= 2)
                return FormatMask(mask);
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

    /// <summary>
    /// Normalize a combo parameter to a hex combo ID — accepts a hex ID, a single instrument
    /// name, or a "+" delimited instrument list. Unlike <see cref="NormalizeComboParam"/>,
    /// this allows single-instrument values. Returns null if invalid.
    /// </summary>
    public static string? NormalizeAnyComboParam(string? param)
    {
        if (string.IsNullOrWhiteSpace(param)) return null;

        int maxMask = (1 << CanonicalOrder.Count) - 1;

        // If it looks like a hex combo ID (1-3 hex chars), validate and return
        if (param.Length <= 3 && int.TryParse(param, System.Globalization.NumberStyles.HexNumber, null, out int mask))
        {
            if (mask > 0 && mask <= maxMask)
                return FormatMask(mask);
        }

        // Otherwise treat as "Instrument+Instrument+..." format (including single)
        var parts = param.Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (parts.Count == 0) return null;

        foreach (var p in parts)
        {
            if (IndexOf(p) < 0) return null;
        }

        return FromInstruments(parts);
    }

    /// <summary>
    /// Normalize a rival combo parameter to a supported stored form.
    /// Supported rival combos are single instruments plus valid within-group combos.
    /// Returns null for unsupported multi-instrument combinations.
    /// </summary>
    public static string? NormalizeSupportedRivalComboParam(string? param)
    {
        var normalized = NormalizeAnyComboParam(param);
        if (normalized is null) return null;

        int mask = Convert.ToInt32(normalized, 16);
        return BitCount(mask) == 1 || IsWithinGroupCombo(mask)
            ? normalized
            : null;
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
