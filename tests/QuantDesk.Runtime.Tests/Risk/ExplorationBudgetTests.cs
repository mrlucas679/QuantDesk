using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Risk;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Indicators;
using QuantDesk.Runtime.Risk;
using QuantDesk.Runtime.Tests.TestData;

namespace QuantDesk.Runtime.Tests.Risk;

/// <summary>
/// Paying a known price for evidence, without pretending it is an edge.
///
/// After the 2026-09-02 re-measurement no rule in either book has a positive expected edge at the
/// sixty basis points the venue charges, so the desk stands down. That is correct and, left alone,
/// permanent: a rule that never trades never produces evidence. An exploration budget is the
/// deliberate decision to buy some anyway -- bounded, ordered by which rule is closest to viable,
/// and recorded as what it is.
/// </summary>
public sealed class ExplorationBudgetTests
{
    [Fact]
    public void WithoutABudgetANegativeEdgeCandidateIsRefused()
    {
        RiskDecision decision = Evaluate(explorationBudgetAvailable: false);

        Assert.False(decision.Approved);
        Assert.Equal(RiskReason.NegativeNetEdge, decision.Reason);
    }

    [Fact]
    public void WithABudgetItIsAdmittedUnderItsOwnReason()
    {
        // Never folded into Approved. A position opened this way is buying information at a price
        // known in advance, and anything reporting performance has to tell the two apart.
        RiskDecision decision = Evaluate(explorationBudgetAvailable: true);

        Assert.True(decision.Approved);
        Assert.Equal(RiskReason.ApprovedAsExploration, decision.Reason);
    }

    [Fact]
    public void APositiveEdgeCandidateIsStillApprovedNormallyEvenWithABudget()
    {
        // The budget widens what may be admitted; it does not relabel what already qualified.
        RiskDecision decision = Evaluate(
            explorationBudgetAvailable: true, grossExpectedPnl: 500m, cost: 1m);

        Assert.True(decision.Approved);
        Assert.Equal(RiskReason.Approved, decision.Reason);
    }

    [Fact]
    public void ABudgetDoesNotExcuseAnyOtherLimit()
    {
        // Exploration buys evidence about a rule, not permission to exceed the risk envelope. The
        // day's losses still stop the lane, budget or no budget.
        PortfolioSnapshot drawn = FinancialTestData.Portfolio() with { DailyPnl = new Usd(-5_000m) };

        RiskDecision decision = new RiskGovernor(FinancialTestData.Limits()).Evaluate(
            FinancialTestData.Candidate(),
            new CostEstimate(Usd.Zero, Usd.Zero, new Usd(500m), Usd.Zero, Usd.Zero),
            FinancialTestData.HealthyMarket(),
            drawn,
            brokerHealthy: true,
            portfolioReconciled: true,
            nowTicks: 0,
            projectedCorrelatedExposure: default,
            explorationBudgetAvailable: true);

        Assert.False(decision.Approved);
        Assert.Equal(RiskReason.DailyLossLimit, decision.Reason);
    }

    [Fact]
    public void TheExplorableBookIsOrderedByWhichRuleIsClosestToViable()
    {
        // The budget is small, so it should be spent on the candidates with the most to prove
        // rather than on whichever happens to fire first. Ordering is a property of the book
        // whichever book survives the filters, so this asks the equity one, which still has
        // members -- the crypto book no longer does, and for a reason the next test states.
        IReadOnlyList<SignalStrategy> explorable =
            SignalStrategies.Explorable(TradedAssetClass.UsEquity);

        for (int i = 1; i < explorable.Count; i++)
        {
            Assert.True(
                explorable[i - 1].ResearchMeanGrossBps >= explorable[i].ResearchMeanGrossBps,
                $"{explorable[i - 1].Id} should not rank below {explorable[i].Id}");
        }
    }

    [Fact]
    public void NoCryptoRuleIsWorthExploringAtSixtyBasisPointsARoundTrip()
    {
        // The change that stopped the bleed of 2026-09-03, and the argument has to be exactly
        // right because it overrules this budget's whole purpose.
        //
        // Exploration exists to buy what shadow and backtest cannot see: fills, spread, slippage.
        // That is a real gap, and it is why a rule the research record condemns was deliberately
        // still explorable -- the record might be pricing the wrong cost.
        //
        // But every one of those unseen effects is a *cost*. Execution reality can only ever make
        // a rule look worse than its frictionless record, never better. So a rule whose gross edge
        // already sits more than a standard error below what the venue charges cannot be redeemed
        // by anything exploration is able to discover, and the sixty basis points buy nothing.
        //
        // volume.obv-confirmed-trend and volatility.atr-expansion were explored on exactly this
        // reasoning gap and were the two positions bleeding when it was found.
        Assert.Empty(SignalStrategies.Explorable(TradedAssetClass.SpotCrypto));
    }

    [Fact]
    public void ARuleWhoseEvidenceDescribesSomethingElseIsNotExplorable()
    {
        // Stale means the figures were produced by a different rule or a different cost, so there
        // is no sense in which such a rule is "closest to viable" -- it is unranked, not near.
        Assert.DoesNotContain(
            SignalStrategies.Explorable(TradedAssetClass.SpotCrypto),
            strategy => strategy.Qualification is StrategyQualification.Stale);
    }

    [Fact]
    public void ExplorationStatesItsOwnPriceRatherThanLeavingItToBeInferred()
    {
        // A budget that does not know its own price is not a budget. The best crypto rule is
        // measured at about 35 bps gross against the 60 the venue charges, so each round trip
        // spent on it costs roughly 25 bps of evidence -- which is precisely why none of them is
        // explorable any more. The pricing itself still has to be right, because the equity book
        // uses it, so it is asked of the rule directly rather than through a filter that now
        // excludes it.
        SignalStrategy best = SignalStrategies.ForCrypto
            .OrderByDescending(strategy => strategy.ResearchMeanGrossBps)
            .First();

        double price = best.ExpectedExplorationCostBps(VenueRoundTripCosts.Crypto);

        Assert.InRange(price, 20d, 30d);
    }

    [Fact]
    public void ARuleThatBeatsTheVenueCostsNothingToExplore()
    {
        SignalStrategy winner = SignalStrategies.ForCrypto[0] with
        {
            ResearchMeanNetBps = 100,
            ResearchCostAssumptionBps = 0,
        };

        Assert.Equal(0d, winner.ExpectedExplorationCostBps(VenueRoundTripCosts.Crypto));
    }

    private static RiskDecision Evaluate(
        bool explorationBudgetAvailable,
        decimal grossExpectedPnl = 1m,
        decimal cost = 500m)
    {
        TradeCandidate candidate = FinancialTestData.Candidate() with
        {
            GrossExpectedPnl = new Usd(grossExpectedPnl),
        };

        return new RiskGovernor(FinancialTestData.Limits()).Evaluate(
            candidate,
            new CostEstimate(Usd.Zero, Usd.Zero, new Usd(cost), Usd.Zero, Usd.Zero),
            FinancialTestData.HealthyMarket(),
            FinancialTestData.Portfolio(),
            brokerHealthy: true,
            portfolioReconciled: true,
            nowTicks: 0,
            projectedCorrelatedExposure: default,
            explorationBudgetAvailable: explorationBudgetAvailable);
    }
}
