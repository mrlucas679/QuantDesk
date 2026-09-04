using QuantDesk.Domain.Instruments;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Options;
using QuantDesk.Domain.Runtime;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Options;

namespace QuantDesk.Runtime.Tests.Options;

public sealed class DefinedRiskVerticalCompilerTests
{
    private static readonly DateOnly Today = new(2026, 8, 31);
    private static readonly DateOnly Expiry = new(2026, 9, 18);
    private const decimal Underlying = 600m;

    // The 600/605 chain below costs a 3.20 debit per share, so $320 per spread on a 100
    // multiplier. The default budget sits just above that; the budget test drops it deliberately.
    private static DefinedRiskVerticalCompiler Compiler(
        decimal riskBudget = 400m,
        double maximumRelativeSpread = 0.10,
        double minimumRewardToRisk = 0.5) =>
        new(new Usd(riskBudget), maximumRelativeSpread, minimumRewardToRisk, 7, 60);

    [Fact]
    public void BullishForecastCompilesABullCallSpreadWithDefinedMaximumLoss()
    {
        VerticalCompilation result = Compiler().Compile(
            candidateId: 42, Underlying, expectedReturnBps: 200,
            Chain(), Quotes(), Today, costBps: 30m, Plan());

        Assert.True(result.Admitted);
        Assert.Equal("spy-bull-call-vertical-v1", result.Candidate!.StrategyId);
        Assert.Equal(2, result.Candidate.Legs.Count);
        Assert.Equal(OrderSide.Buy, result.Candidate.Legs[0].Side);
        Assert.Equal(OrderSide.Sell, result.Candidate.Legs[1].Side);
        // The debit paid is the entire downside, and it is carried on the candidate.
        Assert.Equal(result.DefinedMaximumLoss, result.Candidate.DefinedMaximumLoss);
        Assert.True(result.DefinedMaximumLoss.Value > 0);
        Assert.Equal(result.DefinedMaximumLoss, result.Candidate.ManagementPlan.MaximumAdverseLoss);
    }

    [Fact]
    public void BearishForecastCompilesABearPutSpread()
    {
        VerticalCompilation result = Compiler().Compile(
            42, Underlying, expectedReturnBps: -200, Chain(OptionRight.Put), Quotes(OptionRight.Put),
            Today, 30m, Plan());

        Assert.True(result.Admitted);
        Assert.Equal("spy-bear-put-vertical-v1", result.Candidate!.StrategyId);
    }

    [Fact]
    public void MaximumLossNeverExceedsTheRiskBudget()
    {
        // A one-dollar budget cannot fund any spread in this chain.
        VerticalCompilation result = Compiler(riskBudget: 1m).Compile(
            42, Underlying, 200, Chain(), Quotes(), Today, 30m, Plan());

        Assert.False(result.Admitted);
        Assert.Equal(VerticalRejection.DebitExceedsRiskBudget, result.Rejection);
    }

    [Fact]
    public void ADebitAtOrAboveTheSpreadWidthIsRefusedBecauseItCannotProfit()
    {
        // Long leg offered at the full width above the short leg's bid: max profit would be <= 0.
        var quotes = new Dictionary<int, OptionQuoteSnapshot>
        {
            [Slot(600)] = Quote(Slot(600), bid: 10.0, ask: 10.5),
            [Slot(605)] = Quote(Slot(605), bid: 5.4, ask: 5.6)
        };

        VerticalCompilation result = Compiler().Compile(
            42, Underlying, 200, Chain(), quotes, Today, 30m, Plan());

        Assert.False(result.Admitted);
        Assert.Equal(VerticalRejection.DebitExceedsSpreadWidth, result.Rejection);
    }

    [Fact]
    public void AnUnhealthyOrCrossedQuoteIsNeverTraded()
    {
        var quotes = new Dictionary<int, OptionQuoteSnapshot>
        {
            [Slot(600)] = Quote(Slot(600), 8.0, 8.2) with { Quality = DataQuality.Stale },
            [Slot(605)] = Quote(Slot(605), 5.0, 5.2)
        };

        VerticalCompilation result = Compiler().Compile(
            42, Underlying, 200, Chain(), quotes, Today, 30m, Plan());

        Assert.False(result.Admitted);
        Assert.Equal(VerticalRejection.QuoteUnhealthy, result.Rejection);
    }

    [Fact]
    public void AnIlliquidWideQuoteIsRefused()
    {
        var quotes = new Dictionary<int, OptionQuoteSnapshot>
        {
            [Slot(600)] = Quote(Slot(600), 8.0, 8.2, relativeSpread: 0.40),
            [Slot(605)] = Quote(Slot(605), 5.0, 5.2, relativeSpread: 0.40)
        };

        VerticalCompilation result = Compiler().Compile(
            42, Underlying, 200, Chain(), quotes, Today, 30m, Plan());

        Assert.False(result.Admitted);
        Assert.Equal(VerticalRejection.SpreadTooWide, result.Rejection);
    }

    [Fact]
    public void AForecastThatCannotPayTheRoundTripIsRefused()
    {
        // A forecast of one basis point leaves the spread far out of the money at the target.
        VerticalCompilation result = Compiler().Compile(
            42, Underlying, expectedReturnBps: 1, Chain(), Quotes(), Today, 30m, Plan());

        Assert.False(result.Admitted);
        Assert.Equal(VerticalRejection.ExpectedValueBelowCosts, result.Rejection);
    }

    [Fact]
    public void NoDirectionalConvictionProducesNoCandidate()
    {
        VerticalCompilation result = Compiler().Compile(
            42, Underlying, 0, Chain(), Quotes(), Today, 30m, Plan());

        Assert.False(result.Admitted);
        Assert.Equal(VerticalRejection.NoDirectionalConviction, result.Rejection);
    }

    [Fact]
    public void ExpiriesOutsideTheWindowAreNotConsidered()
    {
        OptionContractDefinition[] tooSoon =
        [
            Contract(600, OptionRight.Call, Today.AddDays(1)),
            Contract(605, OptionRight.Call, Today.AddDays(1))
        ];

        VerticalCompilation result = Compiler().Compile(
            42, Underlying, 200, tooSoon, Quotes(), Today, 30m, Plan());

        Assert.False(result.Admitted);
        Assert.Equal(VerticalRejection.NoExpiryInWindow, result.Rejection);
    }

    [Fact]
    public void MaximumProfitPlusMaximumLossEqualsTheSpreadWidth()
    {
        VerticalCompilation result = Compiler().Compile(
            42, Underlying, 200, Chain(), Quotes(), Today, 30m, Plan());

        Assert.True(result.Admitted);
        // Width of a 600/605 spread on a 100 multiplier is $500, by definition of a vertical.
        Assert.Equal(500m, result.DefinedMaximumLoss.Value + result.MaximumProfit.Value);
    }

    private static PositionManagementPlan Plan() =>
        new(TimeSpan.FromDays(5), true, true, null, 2, "vertical-managed-v1");

    private static int Slot(int strike) => 900_000 + strike;

    private static OptionContractDefinition Contract(
        int strike, OptionRight right, DateOnly? expiry = null) =>
        new(new InstrumentId(Slot(strike)), new InstrumentId(1),
            $"SPY{(expiry ?? Expiry):yyMMdd}{(right == OptionRight.Call ? 'C' : 'P')}{strike * 1000:00000000}",
            expiry ?? Expiry, strike, right, 100);

    private static OptionContractDefinition[] Chain(OptionRight right = OptionRight.Call) =>
        [Contract(600, right), Contract(605, right)];

    private static Dictionary<int, OptionQuoteSnapshot> Quotes(OptionRight right = OptionRight.Call) =>
        right == OptionRight.Call
            ? new Dictionary<int, OptionQuoteSnapshot>
            {
                [Slot(600)] = Quote(Slot(600), 8.0, 8.2),
                [Slot(605)] = Quote(Slot(605), 5.0, 5.2)
            }
            : new Dictionary<int, OptionQuoteSnapshot>
            {
                [Slot(600)] = Quote(Slot(600), 5.0, 5.2),
                [Slot(605)] = Quote(Slot(605), 8.0, 8.2)
            };

    private static OptionQuoteSnapshot Quote(
        int slot, double bid, double ask, double relativeSpread = 0.02) =>
        new(slot, bid, ask, (bid + ask) / 2, relativeSpread, 1_000, DataQuality.Healthy);

    [Fact]
    public void AmongAdmissibleSpreadsTheOneWithMoreExpectedValueWins()
    {
        // Two admissible verticals in one chain. The 600/605 pair comes first in strike order but
        // pays a wider spread; the 605/610 pair is cheaper to get into for the same forecast. Taking
        // the first admissible pair rather than the best one is an arbitrary choice dressed as a
        // decision, and on a real chain it costs several times the necessary execution cost.
        // Both 600/605 and 605/610 clear every gate, and 600/605 is reached first in strike order.
        //   600/605: pay 8.20, receive 5.00 -> debit 3.20, max loss 320, payoff at target 500
        //   605/610: pay 5.20, receive 3.00 -> debit 2.20, max loss 220, payoff at target 500
        // Same payoff, smaller outlay, so the second is worth ~$100 more after costs. Returning the
        // first admissible pair would take the more expensive one and report nothing unusual.
        OptionContractDefinition[] chain =
            [Contract(600, OptionRight.Call), Contract(605, OptionRight.Call), Contract(610, OptionRight.Call)];
        var quotes = new Dictionary<int, OptionQuoteSnapshot>
        {
            [Slot(600)] = Quote(Slot(600), 8.00, 8.20),
            [Slot(605)] = Quote(Slot(605), 5.00, 5.20),
            [Slot(610)] = Quote(Slot(610), 3.00, 3.20)
        };

        VerticalCompilation result = Compiler().Compile(
            42, Underlying, expectedReturnBps: 300, chain, quotes, Today, 30m, Plan());

        Assert.True(result.Admitted);
        Assert.Equal(2.20m, result.NetDebitPerSpread);
        Assert.Equal(220m, result.DefinedMaximumLoss.Value);
        Assert.True(
            result.NetExpectedValue > 270m,
            $"expected the cheaper spread's net value, got {result.NetExpectedValue}");
    }

    [Fact]
    public void TheChosenSpreadReportsItsWidestLegSpread()
    {
        VerticalCompilation result = Compiler().Compile(
            42, Underlying, 200, Chain(), Quotes(), Today, 30m, Plan());

        Assert.True(result.Admitted);
        Assert.Equal(0.02, result.WidestLegRelativeSpread, 6);
    }

    [Fact]
    public void AWideLegIsStillRefusedRatherThanRankedLast()
    {
        // Ranking must not become a way in for a spread the width gate would have refused.
        var quotes = new Dictionary<int, OptionQuoteSnapshot>
        {
            [Slot(600)] = Quote(Slot(600), 8.0, 8.2, relativeSpread: 0.50),
            [Slot(605)] = Quote(Slot(605), 5.0, 5.2, relativeSpread: 0.50)
        };

        VerticalCompilation result = Compiler().Compile(
            42, Underlying, 200, Chain(), quotes, Today, 30m, Plan());

        Assert.False(result.Admitted);
        Assert.Equal(VerticalRejection.SpreadTooWide, result.Rejection);
    }
}
