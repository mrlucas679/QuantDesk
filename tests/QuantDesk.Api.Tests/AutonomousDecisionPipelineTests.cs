using QuantDesk.Alpaca.MarketData;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Capabilities;
using QuantDesk.Domain.Contracts;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Risk;
using QuantDesk.Runtime.Actionability;
using QuantDesk.Runtime.Costs;
using QuantDesk.Runtime.Indicators;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Risk;
using QuantDesk.Runtime.State;
using QuantDesk.Runtime.Strategies;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

public sealed class AutonomousDecisionPipelineTests
{
    [Fact]
    public void AlignedExpertsWithNetEdgeProduceManagedRiskApprovedCandidate()
    {
        AutonomousPipelineDecision result = CreatePipeline().Evaluate(
            0, CryptoRoute, Evidence(100m, 100.01m, 100m, 104m), Portfolio(), true, true, CryptoEnabled);

        Assert.True(result.Approved);
        // Named for the rule that actually fired, not a generic family. The lane now asks several
        // strategies and credits the trade to one of them, so the record says which mechanism
        // opened the position -- without that the live evidence cannot be attributed to anything.
        Assert.Equal("trend.momentum-dual-horizon.v1", result.Candidate?.StrategyId);
        Assert.Equal("crypto-long-managed-v1", result.Candidate?.ManagementPlan.ExitPolicyVersion);
        Assert.True(result.Risk?.Approved);
        Assert.Equal(2, result.Committee?.SupportingExperts.Count);
    }

    [Fact]
    public void PositiveForecastThatCannotPayCostsIsRejected()
    {
        AutonomousPipelineDecision result = CreatePipeline().Evaluate(
            0, CryptoRoute, Evidence(100m, 100.40m, 100m, 100.5m), Portfolio(), true, true, CryptoEnabled);

        Assert.False(result.Approved);
        // The gate now asks whether the instrument moves enough to pay a round trip, rather than
        // whether trailing momentum is large enough. The old question was one strategy's entry
        // condition and made every mean-reversion rule unreachable.
        Assert.Equal("EXPECTED_MOVE_BELOW_COSTS", result.Reason);
        Assert.Null(result.Risk);
    }

    [Fact]
    public void ResearchGateRejectionCannotReachActionabilityOrRisk()
    {
        AutonomousPipelineDecision result = CreatePipeline().Evaluate(
            0, CryptoRoute, Evidence(100m, 100.01m, 100m, 100.3m), Portfolio(), true, true, CryptoEnabled);

        Assert.False(result.Approved);
        // The gate now asks whether the instrument moves enough to pay a round trip, rather than
        // whether trailing momentum is large enough. The old question was one strategy's entry
        // condition and made every mean-reversion rule unreachable.
        Assert.Equal("EXPECTED_MOVE_BELOW_COSTS", result.Reason);
        Assert.Null(result.Candidate);
        Assert.Null(result.Risk);
    }

    [Fact]
    public void BrokerHealthFailureCannotReachApproval()
    {
        AutonomousPipelineDecision result = CreatePipeline().Evaluate(
            0, CryptoRoute, Evidence(100m, 100.01m, 100m, 104m), Portfolio(), false, true, CryptoEnabled);

        Assert.False(result.Approved);
        Assert.Equal(RiskReason.BrokerUnhealthy.ToString(), result.Reason);
    }

    [Fact]
    public void AnAccountWithoutCryptoPermissionCannotProduceACryptoCandidate()
    {
        // The reason the capability argument is mandatory. This pipeline previously defaulted to a
        // permissive set when none was supplied -- crypto and equity granted, options withheld --
        // so an account that had never been asked what it could trade was assumed able to trade
        // the exact thing this compiler emits. The venue would then reject the order after the
        // reservation was taken, which is the expensive place to discover a permission.
        var withoutCrypto = new AccountCapabilities(
            PaperEnvironment: true,
            EquityTrading: true,
            CryptoTrading: false,
            OptionsTrading: false,
            OptionsTradingLevel: null);

        AutonomousPipelineDecision result = CreatePipeline().Evaluate(
            0, CryptoRoute, Evidence(100m, 100.01m, 100m, 104m), Portfolio(), true, true, withoutCrypto);

        Assert.False(result.Approved);
        Assert.Equal("NoOpportunity", result.Reason);
        Assert.Null(result.Candidate);
        Assert.Null(result.Risk);
    }

    [Fact]
    public void CapabilitiesAreRequiredRatherThanDefaultedWhenAbsent()
    {
        // A null set is a question that was never answered, not permission. Failing closed here is
        // what keeps the absence of an answer from reading as a yes.
        Assert.Throws<ArgumentNullException>(() => CreatePipeline().Evaluate(
            0, CryptoRoute, Evidence(100m, 100.01m, 100m, 104m), Portfolio(), true, true, null!));
    }

    [Fact]
    public void AVerifiedForecastWithoutAStatedErrorBarIsRefusedOnceACostBoundExists()
    {
        // The point forecast used to flow straight into the net-edge comparison as though it were a
        // fact. Once a measured cost bound is available the fuller test applies, and a forecast that
        // never stated how wrong it could be cannot pass it -- silence is not a claim of precision.
        AutonomousPipelineDecision result = CreatePipeline().Evaluate(
            0, CryptoRoute, Evidence(100m, 100.01m, 100m, 104m), Portfolio(), true, true, CryptoEnabled,
            verifiedForecastBps: 400d,
            verifiedStrategyFamily: null,
            verifiedStrategyDefinition: null,
            forecastUncertainty: null,
            allInCostUpperBoundBps: 70d);

        Assert.False(result.Approved);
        Assert.Equal("ForecastUncertaintyNotPublished", result.Reason);
    }

    [Fact]
    public void ALargeSignalFromAFamilyWithNoDemonstratedEdgeIsRefused()
    {
        AutonomousPipelineDecision result = CreatePipeline().Evaluate(
            0, CryptoRoute, Evidence(100m, 100.01m, 100m, 104m), Portfolio(), true, true, CryptoEnabled,
            verifiedForecastBps: 400d,
            verifiedStrategyFamily: null,
            verifiedStrategyDefinition: null,
            forecastUncertainty: new ForecastUncertaintyContract(
                StandardErrorBps: 5d,
                HistoricalNetEdgeBps: -3d,
                HistoricalNetEdgeStandardErrorBps: 1d,
                HistoricalObservations: 400,
                AssumedRoundTripCostBps: 0d),
            allInCostUpperBoundBps: 70d);

        Assert.False(result.Approved);
        Assert.Equal("NoDemonstratedHistoricalEdge", result.Reason);
    }

    [Fact]
    public void ANoisySignalIsRefusedEvenThoughItsPointEstimateClearsCost()
    {
        // 100 bps against a 70 bps cost passes a point comparison. At a standard error of 60 the
        // lower bound is about +1 bps, and the trade is a coin flip wearing a forecast.
        AutonomousPipelineDecision result = CreatePipeline().Evaluate(
            0, CryptoRoute, Evidence(100m, 100.01m, 100m, 104m), Portfolio(), true, true, CryptoEnabled,
            verifiedForecastBps: 100d,
            verifiedStrategyFamily: null,
            verifiedStrategyDefinition: null,
            forecastUncertainty: new ForecastUncertaintyContract(60d, 40d, 5d, 400, 0d),
            allInCostUpperBoundBps: 70d);

        Assert.False(result.Approved);
        Assert.Equal("SignalBelowCostAtLowerBound", result.Reason);
    }

    [Fact]
    public void WithoutAMeasuredCostBoundTheOlderComparisonStillGoverns()
    {
        // An unmeasured cost is not a licence to assume one, and it is also not a reason to stop
        // trading entirely -- the gate falls back rather than failing closed on absent measurement.
        AutonomousPipelineDecision result = CreatePipeline().Evaluate(
            0, CryptoRoute, Evidence(100m, 100.01m, 100m, 104m), Portfolio(), true, true, CryptoEnabled,
            verifiedForecastBps: 400d,
            verifiedStrategyFamily: null,
            verifiedStrategyDefinition: null,
            forecastUncertainty: null,
            allInCostUpperBoundBps: null);

        Assert.NotEqual("ForecastUncertaintyNotPublished", result.Reason);
    }

    /// <summary>
    /// No measured dataset, so the modelled cost stands.
    ///
    /// That is the designed fallback: an unmeasured cost is not a licence to assume one, and it is
    /// also not a reason to stop, so the model governs until real round trips say otherwise.
    /// </summary>
    private sealed class NoRealisedCosts : IRealisedCostSource
    {
        public RealisedCostContract? Current() => null;
    }

    /// <summary>The route the pipeline prices against; supplied per call now, not resolved at startup.</summary>
    private static readonly OpportunityRoute CryptoRoute = Route("BTC/USD");

    private static OpportunityRoute Route(string symbol)
    {
        Assert.True(new OpportunityRouter().TryRoute(symbol, out OpportunityRoute? route, out _));
        return route!;
    }

    private static readonly AccountCapabilities CryptoEnabled = new(
        PaperEnvironment: true,
        EquityTrading: true,
        CryptoTrading: true,
        OptionsTrading: false,
        OptionsTradingLevel: null);

    private static AutonomousDecisionPipeline CreatePipeline()
    {
        var clock = new VirtualRuntimeClock(DateTimeOffset.Parse("2026-08-29T00:00:00Z"));
        return new AutonomousDecisionPipeline(
            new MarketStateStore(1),
            new ExpertCommittee(0.6, 1),
            new CryptoDirectionalStrategyCompiler(new Usd(20), 0.05,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)),
            new AssetClassPricing(new NoRealisedCosts(), holdingBars: 12),
            new StrategyRotation(),
            new ActionabilityGate(0.01, new Usd(0.01m)),
            new RiskGovernor(new RiskLimits(new Usd(5), new Usd(25), new Usd(100),
                new Usd(250), 1, 100_000, 100_000, 100_000, 0.01, 1)),
            clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AutonomousDecisionPipeline>.Instance);
    }

    private static DirectionalMarketEvidence Evidence(
        decimal bid, decimal ask, decimal first, decimal last)
    {
        decimal step = (last - first) / 12m;
        decimal[] closes = Enumerable.Range(0, 13).Select(index => first + step * index).ToArray();
        return new DirectionalMarketEvidence(bid, ask, closes);
    }

    private static PortfolioSnapshot Portfolio() => new(
        0, new Usd(100_000), new Usd(100_000), new Usd(100_000),
        Usd.Zero, Usd.Zero, Usd.Zero, Usd.Zero, 0, 0, 0, 0, 0, 0, 0, []);
}
