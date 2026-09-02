using QuantDesk.Runtime.Research;

namespace QuantDesk.Runtime.Tests.Research;

/// <summary>
/// The rung that keeps a stood-down strategy from being stood down forever.
///
/// After the 2026-09-02 re-measurement every rule in both books is known to lose against what the
/// venue actually charges, so the lane opens nothing. On its own that is permanent: a rule that
/// never trades never produces evidence, and without evidence nothing can be re-qualified. Shadow
/// records what each rule would have done, with no order sent, so the loop can close.
/// </summary>
public sealed class ShadowSignalLogTests : IDisposable
{
    private static readonly DateTimeOffset Fired = DateTimeOffset.Parse("2026-09-02T12:00:00Z");

    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"qd-shadow-{Guid.NewGuid():N}.json");

    [Fact]
    public void ASignalIsRecordedAndSurvivesReopeningTheLog()
    {
        Assert.True(Log().TryRecord(Signal("a.trend.v1", "BTC/USD", 100m)));

        ShadowSignal stored = Assert.Single(Log().ListAll());
        Assert.Equal("a.trend.v1", stored.StrategyId);
        Assert.Equal(100m, stored.EntryReferencePrice);
        Assert.False(stored.IsResolved);
    }

    [Fact]
    public void TheSameFiringIsNeverCountedTwice()
    {
        // Two cycles landing on the same bar are one signal. Without that the sample would be
        // weighted by how often the lane happened to run, which is a fact about the scheduler
        // rather than about the rule.
        ShadowSignalLog log = Log();

        Assert.True(log.TryRecord(Signal("a.trend.v1", "BTC/USD", 100m)));
        Assert.False(log.TryRecord(Signal("a.trend.v1", "BTC/USD", 101m)));

        Assert.Single(log.ListAll());
    }

    [Fact]
    public void ASignalResolvesToTheMoveLessTheVenueRoundTrip()
    {
        // 100 to 102 is +200 bps. A crypto round trip costs 60, so the rule would have earned 140.
        ShadowSignalLog log = Log();
        log.TryRecord(Signal("a.trend.v1", "BTC/USD", 100m));

        Assert.Equal(1, log.Resolve(Fired.AddHours(5), _ => 102m));

        ShadowSignal resolved = Assert.Single(log.ListAll());
        Assert.True(resolved.IsResolved);
        Assert.Equal(140d, resolved.NetBps!.Value, precision: 6);
        Assert.Equal(102m, resolved.ExitReferencePrice);
    }

    [Fact]
    public void ASignalInsideItsHoldingPeriodIsLeftAlone()
    {
        ShadowSignalLog log = Log();
        log.TryRecord(Signal("a.trend.v1", "BTC/USD", 100m));

        Assert.Equal(0, log.Resolve(Fired.AddHours(1), _ => 102m));
        Assert.False(log.ListAll()[0].IsResolved);
    }

    [Fact]
    public void ASignalThatCannotBePricedStaysOpenRatherThanResolvingAtAGuess()
    {
        // Late but honest. It resolves on a later pass, and the lateness is visible in the gap
        // between ResolveAt and the exit it eventually gets.
        ShadowSignalLog log = Log();
        log.TryRecord(Signal("a.trend.v1", "BTC/USD", 100m));

        Assert.Equal(0, log.Resolve(Fired.AddHours(5), _ => null));
        Assert.False(log.ListAll()[0].IsResolved);

        Assert.Equal(1, log.Resolve(Fired.AddHours(9), _ => 103m));
        Assert.True(log.ListAll()[0].IsResolved);
    }

    [Fact]
    public void ARuleWithTooFewSignalsIsNotSummarised()
    {
        // The same bar the research scan uses, so a shadow figure and a backtest figure are
        // comparable rather than two different kinds of number.
        ShadowSignalLog log = Log();
        for (int i = 0; i < 5; i++) Record(log, "a.trend.v1", i, exit: 101m);

        Assert.Empty(log.Summarise());
    }

    [Fact]
    public void ARuleWithEnoughSignalsReportsItsMeanAndLowerBound()
    {
        ShadowSignalLog log = Log();

        // Twelve signals alternating +100 and +300 bps of price move against a 60 bps round trip,
        // so net alternates 40 and 240 and the mean is 140.
        for (int i = 0; i < 12; i++)
            Record(log, "a.trend.v1", i, exit: i % 2 == 0 ? 101m : 103m);

        ShadowSummary summary = log.Summarise()["a.trend.v1"];

        Assert.Equal(12, summary.Signals);
        Assert.Equal(140d, summary.MeanNetBps, precision: 6);
        Assert.True(summary.LowerBoundBps < summary.MeanNetBps);
    }

    [Fact]
    public void AnUnresolvedSignalContributesNothingToTheSummary()
    {
        ShadowSignalLog log = Log();
        for (int i = 0; i < 12; i++) Record(log, "a.trend.v1", i, exit: 101m);
        for (int i = 100; i < 120; i++) log.TryRecord(Signal("a.trend.v1", "BTC/USD", 100m, minute: i));

        Assert.Equal(12, log.Summarise()["a.trend.v1"].Signals);
    }

    [Fact]
    public void AnUnreadableLogStartsAgainRatherThanThrowingIntoTheTradingPath()
    {
        // A shadow log is evidence, not money. Losing it is bad; stopping the lane is worse.
        File.WriteAllText(_path, "{ this is not json");

        Assert.Empty(Log().ListAll());
        Assert.True(Log().TryRecord(Signal("a.trend.v1", "BTC/USD", 100m)));
    }

    [Fact]
    public void ASignalWithNoUsablePriceIsRefused()
    {
        Assert.False(Log().TryRecord(Signal("a.trend.v1", "BTC/USD", 0m)));
        Assert.Empty(Log().ListAll());
    }

    [Fact]
    public void ABatchIsWrittenOnceHoweverManySignalsItCarries()
    {
        // The defect this fixes was measured in production: an 83 KB log rewritten every two to
        // four seconds, because every single recorded signal read and rewrote the whole file. One
        // evaluation can fire ninety-one rules, so a cycle's cost was quadratic in the log's size,
        // on a path the trading loop awaits.
        ShadowSignalLog log = Log();

        List<ShadowSignal> batch =
            [.. Enumerable.Range(0, 50).Select(i => Signal($"rule-{i}.v1", "BTC/USD", 100m))];

        Assert.Equal(50, log.TryRecordMany(batch));

        DateTime writtenAt = File.GetLastWriteTimeUtc(_path);
        Assert.Equal(50, log.ListAll().Count);

        // Nothing new in the batch means nothing written at all.
        Assert.Equal(0, log.TryRecordMany(batch));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void ABatchSkipsUnusableSignalsWithoutRejectingTheRest()
    {
        ShadowSignalLog log = Log();

        int added = log.TryRecordMany(
        [
            Signal("a.trend.v1", "BTC/USD", 0m),
            Signal("b.trend.v1", "BTC/USD", 100m),
        ]);

        Assert.Equal(1, added);
        Assert.Equal("b.trend.v1", log.ListAll()[0].StrategyId);
    }

    private void Record(ShadowSignalLog log, string strategyId, int minute, decimal exit)
    {
        log.TryRecord(Signal(strategyId, "BTC/USD", 100m, minute));
        log.Resolve(Fired.AddMinutes(minute).AddHours(5), _ => exit);
    }

    private static ShadowSignal Signal(
        string strategyId, string symbol, decimal entry, int minute = 0) =>
        new(
            SignalId: $"{strategyId}|{symbol}|{Fired.AddMinutes(minute):yyyyMMddTHHmm}",
            Symbol: symbol,
            StrategyId: strategyId,
            FiredAt: Fired.AddMinutes(minute),
            EntryReferencePrice: entry,
            ResolveAt: Fired.AddMinutes(minute).AddHours(4),
            VenueRoundTripBps: 60d);

    private ShadowSignalLog Log() => new(_path);

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
