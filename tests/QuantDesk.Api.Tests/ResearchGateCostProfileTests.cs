using QuantDesk.Alpaca.MarketData;
using QuantDesk.Api.PaperTrading;

namespace QuantDesk.Api.Tests;

/// <summary>
/// The gate charges whatever cost profile it is given, and it was given none.
///
/// Registered without one it fell back to the spot-crypto taker default whatever the lane traded.
/// Pointed at SPY that charges an ~80 bps round-trip hurdle against an instrument whose real cost
/// is nearer 9 -- and SPY does not move 80 bps in an hour, so the lane would have abstained with
/// EXPECTED_EDGE_BELOW_COSTS at every cycle, indefinitely, while looking like it was working.
/// </summary>
public sealed class ResearchGateCostProfileTests
{
    [Fact]
    public void AnEquityMoveThatClearsEquityCostsIsRefusedByTheCryptoHurdle()
    {
        // 60 bps over the window, which is a 15 bps move on the shorter horizon the gate actually
        // charges. Comfortably profitable for SPY at an ~8 bps hurdle; nowhere near a crypto round
        // trip, which has to clear roughly seventy.
        DirectionalMarketEvidence evidence = Rising(60m);

        CryptoResearchDecision crypto = new CryptoResearchGate().Evaluate(evidence);

        Assert.False(crypto.Approved);
        Assert.Equal("EXPECTED_EDGE_BELOW_COSTS", crypto.Reason);
    }

    [Fact]
    public void TheSameMoveIsAdmittedOnTheEquityProfile()
    {
        DirectionalMarketEvidence evidence = Rising(60m);

        CryptoResearchDecision equity =
            new CryptoResearchGate(ExecutionCostProfile.UsEquity).Evaluate(evidence);

        Assert.True(equity.Approved);
        Assert.True(equity.HurdleBps < 15m);
    }

    [Fact]
    public void AMoveTooSmallForEquityCostsIsStillRefused()
    {
        // The profile is not a licence to trade noise -- it just charges the right number.
        CryptoResearchDecision equity =
            new CryptoResearchGate(ExecutionCostProfile.UsEquity).Evaluate(Rising(2m));

        Assert.False(equity.Approved);
    }

    /// <summary>Thirteen closes rising by <paramref name="totalBps"/>, with a one-cent SPY spread.</summary>
    private static DirectionalMarketEvidence Rising(decimal totalBps)
    {
        const decimal start = 650m;
        decimal end = start * (1m + (totalBps / 10_000m));
        decimal step = (end - start) / 12m;
        return new DirectionalMarketEvidence(
            end - 0.005m, end + 0.005m,
            [.. Enumerable.Range(0, 13).Select(index => start + (step * index))]);
    }
}
