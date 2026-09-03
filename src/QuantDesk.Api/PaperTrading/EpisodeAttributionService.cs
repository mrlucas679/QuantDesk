using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Scoring;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Scoring;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.PaperTrading;

/// <summary>One completed round trip, decomposed as far as the evidence allows.</summary>
/// <param name="ExecutionId">Which round trip.</param>
/// <param name="Symbol">What it traded.</param>
/// <param name="Score">The decomposition, including its residual.</param>
/// <param name="ResidualShare">Residual as a fraction of what the prices moved.</param>
/// <param name="Trustworthy">Whether the parts explain more than they omit.</param>
public sealed record EpisodeAttributionEntry(
    string ExecutionId,
    string Symbol,
    EpisodeAttributionScore Score,
    decimal ResidualShare,
    bool Trustworthy);

/// <summary>What the attribution currently says about completed round trips.</summary>
/// <param name="Episodes">Most recent first.</param>
/// <param name="UnexplainedShare">Residual over paper P&amp;L, across every episode measured.</param>
/// <param name="TrustworthyCount">How many decompositions explain more than they omit.</param>
/// <param name="UpdatedAt">When this was computed.</param>
public sealed record EpisodeAttributionSnapshot(
    IReadOnlyList<EpisodeAttributionEntry> Episodes,
    decimal UnexplainedShare,
    int TrustworthyCount,
    DateTimeOffset UpdatedAt);

/// <summary>Holds the latest attribution for the status surface.</summary>
public sealed class EpisodeAttributionState
{
    private readonly Lock _gate = new();
    private EpisodeAttributionSnapshot _snapshot = new([], 0m, 0, DateTimeOffset.UnixEpoch);

    public EpisodeAttributionSnapshot Snapshot()
    {
        lock (_gate) return _snapshot;
    }

    public void Update(EpisodeAttributionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate) _snapshot = snapshot;
    }
}

/// <summary>
/// Decomposes completed round trips, and reports how much of each one nothing explains.
///
/// Section 17.3 asks five questions of an episode: was the forecast right, was the expression of it
/// appropriate, what did execution cost, was the sizing sensible, and what remains unexplained. The
/// scorer that answers them was written, tested and called by nothing.
///
/// What can honestly be attributed today, and what cannot
/// ------------------------------------------------------
/// A completed spot execution records the reference price at entry and exit and the account equity
/// either side. That gives two numbers whose difference is the whole of section 14's implementation
/// shortfall: what the prices did, and what the account actually did.
///
/// It does not give the split. Nothing records the spread paid, the slippage taken, or the venue's
/// fee per round trip separately, so this attributes none of them -- and the entire shortfall lands
/// in the residual.
///
/// That is deliberate and is the useful part. The scorer's own warning is that an attribution which
/// always adds up has explained nothing: a bucket that absorbs the remainder hides a systematic
/// error indefinitely. Splitting the shortfall across named components on no evidence would produce
/// exactly that -- a tidy decomposition asserting knowledge nobody has. A large residual is the
/// honest reading, and the number it reports is the one worth acting on: on the measured crypto
/// round trips it should be most of the loss, which says the costs are real and unmodelled rather
/// than a mystery.
///
/// Why the account delta is the truth and fills are not
/// ----------------------------------------------------
/// Crypto fees are charged in kind, as a deduction from the quantity received, plus a separate USD
/// charge. A P&amp;L reconstructed from fill prices misses both and reports a profit where the
/// account lost money. Only the equity delta sees them.
/// </summary>
public sealed class EpisodeAttributionService(
    SpotExecutionStore executions,
    EpisodeAttributionState state,
    IRuntimeClock clock,
    ILogger<EpisodeAttributionService> logger) : BackgroundService
{
    /// <summary>How many recent round trips are decomposed.</summary>
    private const int MaximumEpisodes = 50;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Refresh();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Episode attribution could not be computed.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    /// <summary>Decomposes every completed round trip that carries the evidence to be decomposed.</summary>
    internal void Refresh()
    {
        var entries = new List<EpisodeAttributionEntry>();
        decimal paperTotal = 0m;
        decimal residualTotal = 0m;

        foreach (SpotExecutionRecord record in executions.ListCompleted()
            .OrderByDescending(item => item.CompletedAt ?? item.CreatedAt)
            .Take(MaximumEpisodes))
        {
            if (Attribute(record) is not { } entry) continue;

            entries.Add(entry);
            paperTotal += Math.Abs(entry.Score.PaperPnl.Value);
            residualTotal += Math.Abs(entry.Score.Residual.Value);
        }

        state.Update(new EpisodeAttributionSnapshot(
            entries,
            paperTotal > 0m ? residualTotal / paperTotal : 0m,
            entries.Count(entry => entry.Trustworthy),
            clock.UtcNow));
    }

    /// <summary>
    /// One episode, or nothing when the record cannot support a decomposition.
    ///
    /// Both reference prices and both equity readings are required. A round trip missing any of
    /// them cannot say what the prices did or what the account did, and inventing either would
    /// make the residual meaningless in the direction that looks best.
    /// </summary>
    private static EpisodeAttributionEntry? Attribute(SpotExecutionRecord record)
    {
        if (record.EntryReferencePrice is not { } entryPrice) return null;
        if (record.ExitReferencePrice is not { } exitPrice) return null;
        if (record.RealisedAccountPnl is not { } realised) return null;
        if (record.Quantity <= 0m) return null;

        // What the prices did over the holding period, which is the most any strategy could have
        // earned before it paid to get in and out.
        decimal paperPnl = (exitPrice - entryPrice) * record.Quantity;

        var input = new EpisodeAttributionInput(
            EpisodeId: EpisodeIdOf(record.ExecutionId),

            // The episode's P&L is what the account did, not what the prices did. A paper account's
            // equity delta is the only figure that saw the in-kind quantity deduction and the
            // separate USD charge, and the residual is measured against this -- so anchoring it to
            // the price move instead would make the decomposition balance against a number the
            // account never earned.
            PaperPnl: new Usd(realised),

            // What the expression achieved before anything was paid to get in or out. Credited to
            // strategy expression rather than to a forecast because the entry came from a rule
            // firing and no forecast is recorded per execution; claiming a forecast contribution
            // nothing measured would be the fabrication the residual exists to prevent.
            AlphaOrForecastContribution: Usd.Zero,
            StrategyExpressionContribution: new Usd(paperPnl),

            // None of these are measured per round trip. Filling them in on no evidence would make
            // the decomposition add up, and the scorer's own warning is that an attribution which
            // always adds up has explained nothing -- a bucket absorbing the remainder hides a
            // systematic error indefinitely. The gap between what the prices did and what the
            // account did therefore lands in the residual, which is where an unmodelled cost
            // belongs and where it stays visible.
            SpreadCost: Usd.Zero,
            SlippageCost: Usd.Zero,
            FeeCost: Usd.Zero,
            TimingCost: Usd.Zero,
            SizingRiskContribution: Usd.Zero,
            FactorStyleContribution: Usd.Zero,
            TailRiskContribution: Usd.Zero,
            CrowdingContribution: Usd.Zero,

            // Zero, because the account figure above already includes every real cost. Charging a
            // realism adjustment on top would deduct the same money twice.
            AdditionalRealismCost: Usd.Zero);

        EpisodeAttributionScore score = EpisodeAttributionScorer.Score(input);
        decimal magnitude = Math.Abs(score.PaperPnl.Value);

        return new EpisodeAttributionEntry(
            record.ExecutionId,
            record.Symbol,
            score,
            magnitude > 0m ? Math.Abs(score.Residual.Value) / magnitude : 0m,
            EpisodeAttributionScorer.IsTrustworthy(score));
    }

    /// <summary>A stable numeric episode id from the execution's own identifier.</summary>
    private static long EpisodeIdOf(string executionId)
    {
        long id = (long)(BitConverter.ToUInt64(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(executionId)), 0) & long.MaxValue);

        return id == 0 ? 1 : id;
    }
}
