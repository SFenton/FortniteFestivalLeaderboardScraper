using FSTService.Api;

namespace FSTService.Tests.Unit;

public sealed class RivalsComboResolverTests
{
    [Fact]
    public void TryResolveRivalCombo_WithThreeDigitHexCombo_ReturnsCanonicalComboAndInstruments()
    {
        var resolved = ApiEndpoints.TryResolveRivalCombo("1ff");

        Assert.NotNull(resolved);
        Assert.Equal("1ff", resolved.Value.CanonicalCombo);
        Assert.Equal(ComboIds.ToInstruments("1ff"), resolved.Value.Instruments);
    }

    [Fact]
    public void TryResolveRivalCombo_WithSingleInstrument_ReturnsCanonicalComboAndInstrument()
    {
        var resolved = ApiEndpoints.TryResolveRivalCombo("Solo_Guitar");

        Assert.NotNull(resolved);
        Assert.Equal("01", resolved.Value.CanonicalCombo);
        Assert.Equal(["Solo_Guitar"], resolved.Value.Instruments);
    }

    [Fact]
    public void TryResolveRivalCombo_WithLegacyComboString_ReturnsCanonicalComboAndInstruments()
    {
        var resolved = ApiEndpoints.TryResolveRivalCombo("Solo_Guitar+Solo_Bass");

        Assert.NotNull(resolved);
        Assert.Equal("03", resolved.Value.CanonicalCombo);
        Assert.Equal(["Solo_Guitar", "Solo_Bass"], resolved.Value.Instruments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("InvalidInstrument")]
    [InlineData("00")]
    public void TryResolveRivalCombo_WithInvalidInput_ReturnsNull(string? combo)
    {
        Assert.Null(ApiEndpoints.TryResolveRivalCombo(combo));
    }
}