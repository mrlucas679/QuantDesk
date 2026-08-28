using QuantDesk.Runtime.Options;

namespace QuantDesk.Runtime.Tests.Options;

public sealed class OptionsTests
{
    [Fact]
    public void BullCallDebitSpread_MatchesGoldenDefinedRiskCase()
    {
        var result = DefinedRiskPayoff.BullCallDebitSpread(100, 110, 6, 2, 100);

        Assert.Equal(400, result.MaxLoss.Value);
        Assert.Equal(600, result.MaxProfit.Value);
        Assert.Equal(104, result.Breakeven);
    }

    [Fact]
    public void BlackScholes_IsOnlyAReferenceSanityPrice()
    {
        double price = BlackScholes.EuropeanCall(100, 100, 1, 0, 0.2);

        Assert.InRange(price, 7, 9);
    }

    [Fact]
    public void BearPutDebitSpread_MatchesDefinedRiskMath()
    {
        var result = DefinedRiskPayoff.BearPutDebitSpread(90, 100, 7, 3, 100);
        Assert.Equal(400, result.MaxLoss.Value);
        Assert.Equal(600, result.MaxProfit.Value);
        Assert.Equal(96, result.Breakeven);
    }
}
