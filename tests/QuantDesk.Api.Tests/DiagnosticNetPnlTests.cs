using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Api.Tests;

/// <summary>
/// The difference between gross and net is the fee, and the fee is what decides whether trading pays.
///
/// Measured over 20 live BTC/USD round trips, gross overstated the result by 24.6 bps of notional per
/// trip — Alpaca's taker fee almost exactly — and reported two losing trades as winners. A P&amp;L number
/// that flatters every trade by the cost of trading is the most dangerous kind of wrong here, because
/// it is the number a search would optimise against.
/// </summary>
public sealed class DiagnosticNetPnlTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NetSubtractsTheQuantityBoughtButNeverSold()
    {
        // Crypto commission is deducted in kind: 1.0 bought, 0.99 left to sell. Gross multiplies the
        // price move by the exit quantity and never notices the missing 0.01.
        DiagnosticExecutionRecord record = Record() with
        {
            EntryAverageFillPrice = 100m,
            ExitAverageFillPrice = 100m,
            EntryFilledQuantity = 1.0m,
            ExitFilledQuantity = 0.99m
        };

        Assert.Equal(0m, DiagnosticExecutionMath.GrossPaperPnl(record));
        Assert.Equal(-1m, DiagnosticExecutionMath.NetPaperPnl(record));
    }

    [Fact]
    public void ATradeThatLooksLikeAWinnerOnGrossCanBeALossOnNet()
    {
        // The observed case: a small favourable price move, entirely eaten by the in-kind fee.
        DiagnosticExecutionRecord record = Record() with
        {
            EntryAverageFillPrice = 100m,
            ExitAverageFillPrice = 100.5m,
            EntryFilledQuantity = 1.0m,
            ExitFilledQuantity = 0.99m
        };

        Assert.True(DiagnosticExecutionMath.GrossPaperPnl(record) > 0);
        Assert.True(DiagnosticExecutionMath.NetPaperPnl(record) < 0);
    }

    [Fact]
    public void AGenuineWinnerSurvivesTheFee()
    {
        DiagnosticExecutionRecord record = Record() with
        {
            EntryAverageFillPrice = 100m,
            ExitAverageFillPrice = 110m,
            EntryFilledQuantity = 1.0m,
            ExitFilledQuantity = 0.99m
        };

        Assert.True(DiagnosticExecutionMath.NetPaperPnl(record) > 0);
    }

    [Fact]
    public void AnIncompleteRoundTripReportsNullRatherThanZero()
    {
        Assert.Null(DiagnosticExecutionMath.NetPaperPnl(Record()));
        Assert.Null(DiagnosticExecutionMath.NetPaperPnl(
            Record() with { EntryAverageFillPrice = 100m, EntryFilledQuantity = 1m }));
    }

    private static DiagnosticExecutionRecord Record() =>
        new("EXP-1", nameof(OrderClassification.DiagnosticExecution), "BTC/USD", "Complete",
            RequestedNotional: 10m, HoldingDuration: TimeSpan.FromMinutes(2), CreatedAt: Now,
            EntryClientOrderId: "entry-id", ExitClientOrderId: "exit-id");
}
