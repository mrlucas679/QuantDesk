using QuantDesk.Api.PaperTrading;

namespace QuantDesk.Api.Tests;

/// <summary>
/// This publisher was hardcoded to SPY and emitted manifests the research plane could not open,
/// so it ran every six hours in production producing datasets nothing read. These tests pin both
/// halves of the repair.
/// </summary>
public sealed class HistoricalEquityDatasetServiceTests
{
    [Fact]
    public void TheUniverseDefaultsToTheFourIndexEtfsWhenUnconfigured()
    {
        Assert.Equal(
            ["SPY", "QQQ", "IWM", "DIA"],
            HistoricalEquityDatasetService.ResolveUniverse(null));
    }

    [Fact]
    public void AConfiguredUniverseIsNormalisedAndDeduplicated()
    {
        IReadOnlyList<string> universe =
            HistoricalEquityDatasetService.ResolveUniverse(" spy , xlk,XLK , xlf ");

        Assert.Equal(["SPY", "XLK", "XLF"], universe);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("BTC/USD, TOOLONG, 123")]
    public void AnEmptyOrWhollyInvalidUniverseFallsBackRatherThanPublishingNothing(string configured)
    {
        // Silently publishing an empty universe would starve research without any signal.
        Assert.Equal(
            ["SPY", "QQQ", "IWM", "DIA"],
            HistoricalEquityDatasetService.ResolveUniverse(configured));
    }

    [Fact]
    public void InvalidSymbolsAreDroppedButValidOnesSurvive()
    {
        IReadOnlyList<string> universe =
            HistoricalEquityDatasetService.ResolveUniverse("SPY,BTC/USD,XLK,TOOLONG");

        Assert.Equal(["SPY", "XLK"], universe);
    }

    [Fact]
    public void ManifestNamesMatchExactlyWhatTheResearchPlaneReads()
    {
        // research/python .../equity_portfolio_strategies.py opens this precise filename.
        Assert.Equal(
            "latest-spy-1day-sip.manifest.json",
            HistoricalEquityDatasetService.LatestManifestName("spy", "1Day", "sip"));
        Assert.Equal(
            "latest-qqq-5min-iex.manifest.json",
            HistoricalEquityDatasetService.LatestManifestName("qqq", "5Min", "iex"));
    }

    [Fact]
    public void EachSymbolAndFeedGetsADistinctManifestSoNoDatasetOverwritesAnother()
    {
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            HistoricalEquityDatasetService.LatestManifestName("spy", "1Day", "sip"),
            HistoricalEquityDatasetService.LatestManifestName("spy", "1Day", "iex"),
            HistoricalEquityDatasetService.LatestManifestName("spy", "5Min", "sip"),
            HistoricalEquityDatasetService.LatestManifestName("qqq", "1Day", "sip")
        };

        Assert.Equal(4, names.Count);
    }
}
