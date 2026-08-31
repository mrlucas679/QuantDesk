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
    [InlineData("SPY")]
    [InlineData("SPY260904X00650000")]
    [InlineData("SPY260932C00650000")]
    [InlineData("SPY260904C00000000")]
    public void RejectsMalformedOrNonOptionSymbols(string value)
    {
        Assert.False(OccOptionSymbol.TryParse(value, out _));
    }
}
