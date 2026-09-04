using QuantDesk.Domain.Options;

namespace QuantDesk.Domain.Tests.Options;

public sealed class OccOptionSymbolTests
{
    [Fact]
    public void ParsesStrictOccContractIdentity()
    {
        Assert.True(OccOptionSymbol.TryParse("SPY260904C00650000", out OccOptionSymbol? symbol));
        Assert.NotNull(symbol);
        Assert.Equal("SPY", symbol.Underlying);
        Assert.Equal(new DateOnly(2026, 9, 4), symbol.Expiration);
        Assert.Equal(OptionRight.Call, symbol.Right);
        Assert.Equal(650m, symbol.Strike);
    }

    [Theory]
    [InlineData("SPY1261016C00600000", "SPY1")]
    [InlineData("AAPL1261016P00150000", "AAPL1")]
    public void ParsesTheNumberedRootOfAnAdjustedContract(string value, string expectedRoot)
    {
        // How a corporate action is encoded. Rejecting these as invalid symbols conflated "this
        // contract is adjusted" with "this feed is corrupt", and callers act on that difference.
        Assert.True(OccOptionSymbol.TryParse(value, out OccOptionSymbol? symbol));
        Assert.NotNull(symbol);
        Assert.Equal(expectedRoot, symbol.Underlying);
        Assert.Equal(new DateOnly(2026, 10, 16), symbol.Expiration);
    }

    [Fact]
    public void ADigitBearingRootStillResolvesTheDateAndStrikeUnambiguously()
    {
        Assert.True(OccOptionSymbol.TryParse("SPY1261016C00600000", out OccOptionSymbol? symbol));
        Assert.Equal(OptionRight.Call, symbol!.Right);
        Assert.Equal(600m, symbol.Strike);
    }

    [Theory]
    [InlineData("SPY")]
    [InlineData("1SPY261016C00600000")]
    [InlineData("SPY260904X00650000")]
    [InlineData("SPY260932C00650000")]
    [InlineData("SPY260904C00000000")]
    public void RejectsMalformedOrNonOptionSymbols(string value)
    {
        Assert.False(OccOptionSymbol.TryParse(value, out _));
    }
}
