using QuantDesk.Api.PaperTrading;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

/// <summary>
/// The lane state, once it holds more than one instrument.
///
/// A single snapshot was accurate only while the lane traded one symbol. With several, the last
/// instrument evaluated overwrites the rest every cycle, so an operator watching the endpoint would
/// see a live position vanish from view the moment another symbol was assessed -- and could not
/// tell "flat" from "not the most recent".
/// </summary>
public sealed class MultiSymbolLaneStateTests
{
    [Fact]
    public void EachInstrumentKeepsItsOwnState()
    {
        var state = new AutonomousTradingState(new LiveRuntimeClock());

        state.UpdateSymbol("BTC/USD", "holding", filledQuantity: 0.01m);
        state.UpdateSymbol("ETH/USD", "abstained", reason: "EXPECTED_EDGE_BELOW_COSTS");

        IReadOnlyList<AutonomousTradingSnapshot> all = state.SnapshotAll();
        Assert.Equal(2, all.Count);
        Assert.Equal("holding", all.Single(item => item.Symbol == "BTC/USD").State);
        Assert.Equal("abstained", all.Single(item => item.Symbol == "ETH/USD").State);
    }

    [Fact]
    public void AHeldPositionIsNotHiddenByAnotherSymbolsAbstention()
    {
        // The failure a single slot produced: evaluating ETH after BTC opened a position would have
        // reported the lane as flat while it was holding.
        var state = new AutonomousTradingState(new LiveRuntimeClock());

        state.UpdateSymbol("BTC/USD", "holding", filledQuantity: 0.01m);
        state.UpdateSymbol("ETH/USD", "abstained", reason: "MomentumNotAligned");

        Assert.Equal("holding", state.Snapshot().State);
        Assert.Equal("BTC/USD", state.Snapshot().Symbol);
    }

    [Fact]
    public void WithNothingHeldTheMostRecentEvaluationIsReported()
    {
        // And never the lane record, which carries only states no instrument owns -- reporting
        // "disabled" over a running lane that had simply abstained everywhere.
        var state = new AutonomousTradingState(new LiveRuntimeClock());

        state.UpdateSymbol("BTC/USD", "abstained", reason: "MomentumNotAligned");

        Assert.Equal("abstained", state.Snapshot().State);
    }

    [Fact]
    public void AnEmptyLaneStillReportsSomething()
    {
        Assert.Single(new AutonomousTradingState(new LiveRuntimeClock()).SnapshotAll());
    }
}

/// <summary>Parsing the lane's symbol list.</summary>
public sealed class MultiSymbolOptionsTests
{
    [Fact]
    public void ACommaSeparatedListBecomesSeveralSymbols()
    {
        Assert.Equal(["BTC/USD", "ETH/USD", "LTC/USD"], Parse("BTC/USD, ETH/USD ,LTC/USD"));
    }

    [Fact]
    public void ASingleSymbolStillMeansExactlyWhatItDid()
    {
        Assert.Equal(["BTC/USD"], Parse("BTC/USD"));
    }

    [Fact]
    public void DuplicatesCollapseSoOneInstrumentIsNotEvaluatedTwicePerCycle()
    {
        Assert.Equal(["BTC/USD", "ETH/USD"], Parse("BTC/USD,ETH/USD,BTC/USD"));
    }

    [Fact]
    public void SymbolsAreNormalisedSoCasingCannotSplitOneInstrumentIntoTwo()
    {
        Assert.Equal(["BTC/USD"], Parse("btc/usd, BTC/USD"));
    }

    /// <summary>Mirrors the parsing in AutonomousPaperTradingOptions.FromEnvironment.</summary>
    private static string[] Parse(string configured) =>
    [
        .. configured
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal),
    ];
}
