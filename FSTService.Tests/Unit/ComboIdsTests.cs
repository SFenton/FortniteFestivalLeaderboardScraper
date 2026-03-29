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
}
