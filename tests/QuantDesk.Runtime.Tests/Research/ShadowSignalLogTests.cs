using QuantDesk.Domain.Trading;
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

    /// <summary>
    /// A minute past a signal's due time, which is inside the resolver's lateness tolerance.
    ///
    /// These tests used to resolve an hour late and assert the resulting figure, which is exactly
    /// the behaviour that made shadow score a market rally as a rule's skill: the exit price
    /// available at resolution is the mid *now*, so a signal scored long after its horizon is
    /// scored against a hold nobody took.
    /// </summary>
    private static readonly DateTimeOffset OnTime = Fired.AddHours(4).AddMinutes(1);

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

        Assert.Equal(1, log.Resolve(OnTime, _ => 102m));

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
    public void ASignalThatCannotBePricedStaysOpenAndIsAbandonedRatherThanScoredLate()
    {
        // This test used to assert "late but honest": a signal that could not be priced on time
        // resolved on a later pass, at whatever the mid happened to be by then. That is the defect.
        // The exit price available at resolution is the mid *now*, so scoring a signal five hours
        // past its horizon measures a hold nobody took -- and on a day when the market rallied, it
        // recorded the rally as the rule's skill.
        ShadowSignalLog log = Log();
        log.TryRecord(Signal("a.trend.v1", "BTC/USD", 100m));

        // Still open while it is merely unpriced and not yet late.
        Assert.Equal(0, log.Resolve(OnTime, _ => null));
        Assert.False(log.ListAll()[0].IsResolved);

        // Past the tolerance it is abandoned, not scored. An observation that was never made is
        // not a losing observation; counting it either way invents evidence.
        Assert.Equal(1, log.Resolve(Fired.AddHours(9), _ => 103m));
        Assert.True(log.ListAll()[0].Abandoned);
        Assert.Null(log.ListAll()[0].NetBps);
        Assert.Empty(log.Summarise(minimumSignals: 1));
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
    public void ACorruptLogRaisesAnExplicitPersistenceFailure()
    {
        // Treating this as an empty ledger makes evidence loss look like a rule that never fired.
        // The caller may degrade/abstain, but it must be told that the evidence is unavailable.
        File.WriteAllText(_path, "{ this is not json");

        ShadowSignalPersistenceException failure =
            Assert.Throws<ShadowSignalPersistenceException>(() => Log().ListAll());

        Assert.Equal("load", failure.Operation);
        Assert.Equal(_path, failure.EvidencePath);
        Assert.IsType<System.Text.Json.JsonException>(failure.InnerException);
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

        // Resolved at its own due time. Resolving a batch at one later instant is what the
        // production bug did, and a test that does it cannot detect the bug.
        log.Resolve(Fired.AddMinutes(minute).AddHours(4).AddMinutes(1), _ => exit);
    }

    // ------------------------------------------------------- the two books share rule identifiers

    [Fact]
    public void OneBookIsNotSummarisedFromTheOtherBooksEvidence()
    {
        // reversion.vwap.v1 is defined in both books, as are the bollinger and rsi reversion rules.
        // They are different rules held to costs an order of magnitude apart, and this summary is
        // what decides whether a stood-down rule may trade again.
        //
        // Crypto here is strongly positive and equities strongly negative. Pooled, the equity rule
        // reads as a promotion candidate on evidence it did not produce.
        ShadowSignalLog log = Log();
        RecordResolved(log, "reversion.vwap.v1", "BTC/USD", net: 40d, count: 40);
        RecordResolved(log, "reversion.vwap.v1", "SPY", net: -30d, count: 40);

        ShadowSummary crypto = log.Summarise(TradedAssetClass.SpotCrypto)["reversion.vwap.v1"];
        ShadowSummary equity = log.Summarise(TradedAssetClass.UsEquity)["reversion.vwap.v1"];

        Assert.Equal(40, crypto.Signals);
        Assert.Equal(40, equity.Signals);
        Assert.True(crypto.LowerBoundBps > 0d);
        Assert.True(equity.LowerBoundBps < 0d);
    }

    [Fact]
    public void ASignalRecordedBeforeTheBookWasStoredIsReadFromItsSymbol()
    {
        // Six thousand signals predate the field. A slash is a crypto pair; everything else in this
        // universe is an equity. Reading them as one book would keep the pooling bug alive in the
        // history even after new signals carry the route's answer.
        Assert.Equal(
            TradedAssetClass.SpotCrypto,
            Signal("a.trend.v1", "BTC/USD", 100m).AssetClass);
        Assert.Equal(
            TradedAssetClass.UsEquity,
            Signal("a.trend.v1", "SPY", 100m).AssetClass);
    }

    [Fact]
    public void SummarisingWithoutABookStillSeesEverything()
    {
        // The status surface reports the whole log, and that reading stays available.
        ShadowSignalLog log = Log();
        RecordResolved(log, "reversion.vwap.v1", "BTC/USD", net: 40d, count: 20);
        RecordResolved(log, "reversion.vwap.v1", "SPY", net: -30d, count: 20);

        Assert.Equal(40, log.Summarise()["reversion.vwap.v1"].Signals);
    }

    /// <summary>Records and resolves a run of signals for one rule on one symbol.</summary>
    private static void RecordResolved(
        ShadowSignalLog log, string strategyId, string symbol, double net, int count)
    {
        // Entry 100, so an exit of 100 * (1 + (net + venue) / 10_000) lands on the wanted net.
        decimal exit = 100m * (1m + ((decimal)(net + 60d) / 10_000m));

        // Each signal resolved at its own deadline rather than the whole run at one later instant.
        // The batch form is precisely the production defect: it prices every signal at whatever the
        // clock says when the resolver runs, so a run recorded over forty minutes gets scored
        // against one price up to forty minutes past the earliest signal's horizon.
        for (int minute = 0; minute < count; minute++)
        {
            log.TryRecord(Signal(strategyId, symbol, 100m, minute));
            log.Resolve(Fired.AddMinutes(minute).AddHours(4).AddMinutes(1), _ => exit);
        }
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
