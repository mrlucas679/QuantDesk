using Microsoft.Extensions.Logging.Abstractions;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Scoring;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

/// <summary>
/// Completed round trips decomposed, and the part nothing explains reported rather than hidden.
///
/// The scorer answering section 17.3 was written, tested and called by nothing. These connect it to
/// the durable executions and pin the decision that makes the connection worth anything: the
/// unmeasured cost lands in the residual instead of being spread across named components on no
/// evidence.
/// </summary>
public sealed class EpisodeAttributionServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"quantdesk-exec-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void ACompletedRoundTripIsDecomposed()
    {
        Store(Completed("exec-1", entry: 100m, exit: 104m, quantity: 2m, realised: 7.4m));
        var state = new EpisodeAttributionState();

        Service(state).Refresh();

        EpisodeAttributionEntry entry = Assert.Single(state.Snapshot().Episodes);
        Assert.Equal("exec-1", entry.ExecutionId);

        // The episode's P&L is what the account did, not what the prices did.
        Assert.Equal(7.4m, entry.Score.PaperPnl.Value);
        Assert.Equal(8m, entry.Score.StrategyExpressionContribution.Value);
    }

    [Fact]
    public void TheGapBetweenWhatPricesDidAndWhatTheAccountDidIsCarriedNotHidden()
    {
        // Eight dollars of price move, 7.40 in the account: sixty cents went somewhere. Nothing
        // records where, so nothing claims to know -- it lands in the residual and stays visible
        // rather than being spread across named components that would make the sum balance.
        Store(Completed("exec-1", entry: 100m, exit: 104m, quantity: 2m, realised: 7.4m));
        var state = new EpisodeAttributionState();

        Service(state).Refresh();

        EpisodeAttributionEntry entry = Assert.Single(state.Snapshot().Episodes);
        Assert.Equal(-0.6m, entry.Score.Residual.Value, precision: 6);
        Assert.Equal(7.4m, entry.Score.RealismAdjustedPnl.Value, precision: 6);
    }

    [Fact]
    public void ARoundTripWhoseCostSwampsItsMoveIsMarkedUntrustworthy()
    {
        // The measured crypto case: the price moved the rule's way and the account still lost. The
        // decomposition explains less than it omits, and saying so is the point of the residual.
        Store(Completed("exec-1", entry: 100m, exit: 100.5m, quantity: 1m, realised: -0.7m));
        var state = new EpisodeAttributionState();

        Service(state).Refresh();

        EpisodeAttributionEntry entry = Assert.Single(state.Snapshot().Episodes);
        Assert.False(entry.Trustworthy);
        Assert.True(entry.ResidualShare > EpisodeAttributionScorer.MaximumTrustedResidualShare);
    }

    [Fact]
    public void ARoundTripMissingItsEvidenceIsSkippedRatherThanGuessed()
    {
        // Without both reference prices and both equity readings there is no way to say what the
        // prices did or what the account did. Inventing either would move the residual in whichever
        // direction looked best.
        Store(Completed("exec-1", entry: 100m, exit: 104m, quantity: 2m, realised: 7.4m)
            with { EntryReferencePrice = null });
        var state = new EpisodeAttributionState();

        Service(state).Refresh();

        Assert.Empty(state.Snapshot().Episodes);
    }

    [Fact]
    public void TheUnexplainedShareIsReportedAcrossEveryEpisode()
    {
        Store(
            Completed("exec-1", entry: 100m, exit: 104m, quantity: 1m, realised: 3.6m),
            Completed("exec-2", entry: 200m, exit: 196m, quantity: 1m, realised: -4.4m));
        var state = new EpisodeAttributionState();

        Service(state).Refresh();

        EpisodeAttributionSnapshot snapshot = state.Snapshot();
        Assert.Equal(2, snapshot.Episodes.Count);

        // Eight dollars of absolute move, eighty cents unexplained.
        Assert.Equal(0.1m, snapshot.UnexplainedShare, precision: 6);
    }

    [Fact]
    public void NoCompletedRoundTripsMeansNoEpisodesRatherThanAZeroResidual()
    {
        // An empty decomposition that reported perfect explanation would be the worst possible
        // reading of having measured nothing.
        var state = new EpisodeAttributionState();

        Service(state).Refresh();

        Assert.Empty(state.Snapshot().Episodes);
        Assert.Equal(0m, state.Snapshot().UnexplainedShare);
        Assert.Equal(0, state.Snapshot().TrustworthyCount);
    }

    // ------------------------------------------------------------------------------- fixtures

    private EpisodeAttributionService Service(EpisodeAttributionState state) =>
        new(new SpotExecutionStore(_path), state, new LiveRuntimeClock(),
            NullLogger<EpisodeAttributionService>.Instance);

    private void Store(params SpotExecutionRecord[] records)
    {
        var store = new SpotExecutionStore(_path);
        foreach (SpotExecutionRecord record in records)
        {
            store.TryCreate(record with { State = SpotExecutionState.EntryReserved });
            store.Update(record);
        }
    }

    private static SpotExecutionRecord Completed(
        string id, decimal entry, decimal exit, decimal quantity, decimal realised) =>
        new(
            ExecutionId: id,
            StrategyId: "test-strategy",
            Symbol: "BTC/USD",
            InstrumentSlot: 0,
            State: SpotExecutionState.Complete,
            EntryClientOrderId: $"{id}-entry",
            ExitClientOrderId: $"{id}-exit",
            Quantity: quantity,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-30),
            EntryReservedAt: DateTimeOffset.UtcNow.AddMinutes(-30))
        {
            CompletedAt = DateTimeOffset.UtcNow,
            EntryReferencePrice = entry,
            ExitReferencePrice = exit,
            AccountEquityBefore = 1_000m,
            AccountEquityAfter = 1_000m + realised,
        };
}
