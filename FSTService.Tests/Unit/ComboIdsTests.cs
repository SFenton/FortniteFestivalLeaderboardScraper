namespace FSTService.Tests.Unit;

public sealed class ComboIdsTests
{
    [Theory]
    [InlineData(new[] { "Solo_Guitar", "Solo_Bass" }, "03")]
    [InlineData(new[] { "Solo_Guitar", "Solo_PeripheralGuitar" }, "11")]
    [InlineData(new[] { "Solo_Guitar", "Solo_Bass", "Solo_Drums", "Solo_Vocals" }, "0f")]
    [InlineData(new[] { "Solo_PeripheralGuitar", "Solo_PeripheralBass" }, "30")]
    [InlineData(new[] { "Solo_Guitar", "Solo_Bass", "Solo_Drums", "Solo_Vocals", "Solo_PeripheralGuitar", "Solo_PeripheralBass" }, "3f")]
    public void FromInstruments_produces_expected_combo_id(string[] instruments, string expected)
    {
        Assert.Equal(expected, ComboIds.FromInstruments(instruments));
    }

    [Fact]
    public void FromInstruments_is_order_independent()
    {
        Assert.Equal(
            ComboIds.FromInstruments(["Solo_Bass", "Solo_Guitar"]),
            ComboIds.FromInstruments(["Solo_Guitar", "Solo_Bass"]));
    }

    [Fact]
    public void FromInstruments_throws_for_unknown()
    {
        Assert.Throws<ArgumentException>(() => ComboIds.FromInstruments(["NotReal"]));
    }

    [Theory]
    [InlineData("03", new[] { "Solo_Guitar", "Solo_Bass" })]
    [InlineData("0f", new[] { "Solo_Guitar", "Solo_Bass", "Solo_Drums", "Solo_Vocals" })]
    [InlineData("3f", new[] { "Solo_Guitar", "Solo_Bass", "Solo_Drums", "Solo_Vocals", "Solo_PeripheralGuitar", "Solo_PeripheralBass" })]
    public void ToInstruments_reverses_combo_id(string comboId, string[] expected)
    {
        Assert.Equal(expected, ComboIds.ToInstruments(comboId));
    }

    [Fact]
    public void ToInstruments_throws_for_invalid()
    {
        Assert.Throws<FormatException>(() => ComboIds.ToInstruments("zz"));
    }

    [Theory]
    [InlineData("03", "07", "0f", "11", "30", "3f")]
    public void RoundTrip_all_combo_ids(params string[] ids)
    {
        foreach (var id in ids)
        {
            var instruments = ComboIds.ToInstruments(id);
            Assert.Equal(id, ComboIds.FromInstruments(instruments));
        }
    }

    [Fact]
    public void FromMask_produces_hex()
    {
        Assert.Equal("03", ComboIds.FromMask(3));
        Assert.Equal("3f", ComboIds.FromMask(63));
        Assert.Equal("0f", ComboIds.FromMask(15));
    }

    // ── NormalizeComboParam ──

    [Theory]
    [InlineData("03", "03")]
    [InlineData("3f", "3f")]
    [InlineData("0f", "0f")]
    public void NormalizeComboParam_accepts_hex_ids(string input, string expected)
    {
        Assert.Equal(expected, ComboIds.NormalizeComboParam(input));
    }

    [Theory]
    [InlineData("Solo_Guitar+Solo_Bass", "03")]
    [InlineData("Solo_Bass+Solo_Guitar", "03")]
    [InlineData("Solo_PeripheralGuitar+Solo_PeripheralBass", "30")]
    public void NormalizeComboParam_accepts_legacy_instrument_strings(string input, string expected)
    {
        Assert.Equal(expected, ComboIds.NormalizeComboParam(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Solo_Guitar")]       // single instrument = null
    [InlineData("01")]                // single instrument hex = null
    public void NormalizeComboParam_returns_null_for_invalid(string? input)
    {
        Assert.Null(ComboIds.NormalizeComboParam(input));
    }

    // ── NormalizeAnyComboParam ──

    [Theory]
    [InlineData("01", "01")]
    [InlineData("03", "03")]
    [InlineData("3f", "3f")]
    public void NormalizeAnyComboParam_accepts_hex_ids(string input, string expected)
    {
        Assert.Equal(expected, ComboIds.NormalizeAnyComboParam(input));
    }

    [Theory]
    [InlineData("Solo_Guitar", "01")]
    [InlineData("Solo_Bass", "02")]
    [InlineData("Solo_Drums", "04")]
    [InlineData("Solo_Vocals", "08")]
    [InlineData("Solo_PeripheralGuitar", "10")]
    [InlineData("Solo_PeripheralBass", "20")]
    public void NormalizeAnyComboParam_accepts_single_instrument_names(string input, string expected)
    {
        Assert.Equal(expected, ComboIds.NormalizeAnyComboParam(input));
    }

    [Theory]
    [InlineData("Solo_Guitar+Solo_Bass", "03")]
    [InlineData("Solo_Bass+Solo_Guitar", "03")]
    public void NormalizeAnyComboParam_accepts_multi_instrument_strings(string input, string expected)
    {
        Assert.Equal(expected, ComboIds.NormalizeAnyComboParam(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("NotAnInstrument")]
    [InlineData("00")]
    public void NormalizeAnyComboParam_returns_null_for_invalid(string? input)
    {
        Assert.Null(ComboIds.NormalizeAnyComboParam(input));
    }

    // ── Instrument groups & within-group combos ──

    [Fact]
    public void WithinGroupComboMasks_contains_12_combos()
    {
        Assert.Equal(12, ComboIds.WithinGroupComboMasks.Count);
    }

    [Fact]
    public void WithinGroupComboMasks_all_have_2_or_more_bits()
    {
        foreach (var mask in ComboIds.WithinGroupComboMasks)
            Assert.True(BitCount(mask) >= 2, $"Mask 0x{mask:x2} has fewer than 2 bits set.");
    }

    [Theory]
    [InlineData(0x03, true)]  // Lead + Bass (OG Band)
    [InlineData(0x05, true)]  // Lead + Drums (OG Band)
    [InlineData(0x0F, true)]  // All OG Band
    [InlineData(0x30, true)]  // Pro Lead + Pro Bass
    [InlineData(0x11, false)] // Lead + Pro Lead (cross-group)
    [InlineData(0x3F, false)] // All 6 (cross-group)
    [InlineData(0x31, false)] // Lead + Pro Strings (cross-group)
    [InlineData(0x01, false)] // Single instrument
    public void IsWithinGroupCombo_mask_validates_correctly(int mask, bool expected)
    {
        Assert.Equal(expected, ComboIds.IsWithinGroupCombo(mask));
    }

    [Theory]
    [InlineData("03", true)]
    [InlineData("0f", true)]
    [InlineData("30", true)]
    [InlineData("11", false)]
    [InlineData("3f", false)]
    public void IsWithinGroupCombo_string_validates_correctly(string comboId, bool expected)
    {
        Assert.Equal(expected, ComboIds.IsWithinGroupCombo(comboId));
    }

    [Fact]
    public void InstrumentGroups_has_2_groups()
    {
        Assert.Equal(2, ComboIds.InstrumentGroups.Count);
    }

    [Fact]
    public void InstrumentGroups_og_band_covers_bits_0_to_3()
    {
        Assert.Equal(0x0F, ComboIds.InstrumentGroups[0]);
    }

    [Fact]
    public void InstrumentGroups_pro_strings_covers_bits_4_to_5()
    {
        Assert.Equal(0x30, ComboIds.InstrumentGroups[1]);
    }

    private static int BitCount(int value)
    {
        int count = 0;
        while (value != 0) { count += value & 1; value >>= 1; }
        return count;
    }
}
