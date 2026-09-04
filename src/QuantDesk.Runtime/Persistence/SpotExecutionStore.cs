using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;
using System.Text.Json;
using QuantDesk.Domain.Serialization;

namespace QuantDesk.Runtime.Persistence;

/// <summary>Lifecycle states of one durable single-leg spot execution.</summary>
public enum SpotExecutionState
{
    EntryReserved,
    EntrySubmitted,
    EntryAccepted,
    EntryPartiallyFilled,
    EntryFilled,
    Holding,
    ExitDue,
    ExitReserved,
    ExitSubmitted,
    ExitAccepted,
    ExitPartiallyFilled,
    ExitFilled,
    Reconciling,
    Complete,
    Failed
}

/// <summary>
/// One spot opportunity, durable across restarts.
///
/// The autonomous spot lane previously held all of this in memory, so a restart between reserving
/// and filling lost the fact that an order existed at all. Every identity here is derived from the
/// opportunity rather than generated, so a record can be reconstructed and an ambiguous submission
/// resolved by asking the broker for the exact client order ID it would have used.
/// </summary>
/// <summary>One position's market value at an instant: what it was, how much, and at what price.</summary>
/// <param name="Symbol">The instrument, in the venue's spelling.</param>
/// <param name="Quantity">Units held.</param>
/// <param name="Mid">The mid used to mark it, or zero when no healthy quote existed.</param>
public readonly record struct PositionMark(string Symbol, decimal Quantity, decimal Mid)
{
    public decimal MarketValue => Quantity * Mid;
}

public sealed record SpotExecutionRecord(
    string ExecutionId,
    string StrategyId,
    string Symbol,
    int InstrumentSlot,
    SpotExecutionState State,
    string EntryClientOrderId,
    string ExitClientOrderId,
    decimal Quantity,
    DateTimeOffset CreatedAt,
    DateTimeOffset EntryReservedAt)
{
    /// <summary>Always PAPER. A record that says otherwise is refused on load.</summary>
    public string ExecutionMode { get; init; } = "PAPER";

    /// <summary>
    /// Which way this position is held, which decides the side of both orders.
    ///
    /// Long by default, and deliberately not <see cref="SignalDirection.None"/>: every record
    /// written before this field existed is a long, so a record loaded without it must read as one.
    /// The enum's own default is None precisely so that a *signal* deserialised without a direction
    /// cannot read as an instruction to take exposure; here the exposure already exists and the
    /// question is only which way it points.
    /// </summary>
    public SignalDirection Direction { get; init; } = SignalDirection.Long;

    public decimal DefinedMaximumLoss { get; init; }

    /// <summary>
    /// The unrealised gain at which this position has earned what its thesis predicted.
    ///
    /// Zero means no target, which is how every record written before this existed loads.
    /// </summary>
    public decimal ProfitTarget { get; init; }

    public TimeSpan MaximumHoldingPeriod { get; init; }

    /// <summary>
    /// The research publication that authorised this position, captured at reservation.
    ///
    /// Null for an execution opened without a verified artifact — the experimental mode, which never
    /// claims research licensed it. A null binding is therefore not a missing field to be repaired;
    /// it is the honest record of a position no artifact stands behind, and the artifact-retraction
    /// interrupt correctly declines to act on one.
    /// </summary>
    public PositionOwnership? Ownership { get; init; }
    public decimal? EntryLimitPrice { get; init; }
    public decimal? ExitLimitPrice { get; init; }

    public DateTimeOffset? EntrySubmissionAttemptedAt { get; init; }
    public DateTimeOffset? EntrySubmittedAt { get; init; }
    public string? EntryBrokerOrderId { get; init; }
    public decimal EntryFilledQuantity { get; init; }
    public decimal? EntryAverageFillPrice { get; init; }
    public DateTimeOffset? EntryFinalFillAt { get; init; }

    public DateTimeOffset? HoldStartedAt { get; init; }
    public DateTimeOffset? ScheduledExitAt { get; init; }

    public DateTimeOffset? ExitReservedAt { get; init; }
    public DateTimeOffset? ExitSubmissionAttemptedAt { get; init; }
    public DateTimeOffset? ExitSubmittedAt { get; init; }
    public string? ExitBrokerOrderId { get; init; }
    public decimal ExitFilledQuantity { get; init; }
    public decimal? ExitAverageFillPrice { get; init; }
    public DateTimeOffset? ExitFinalFillAt { get; init; }

    public DateTimeOffset? ReconciledAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? FailureReason { get; init; }

    /// <summary>Why the hold ended before its timer, or null when the timer ended it.</summary>
    public string? EarlyExitReason { get; init; }

    /// <summary>
    /// The quote mid when this opportunity was decided, and again when it reconciled flat.
    ///
    /// These are the decision prices, and without them a round trip cannot say what it cost. The
    /// measure that matters is implementation shortfall -- what a frictionless execution at the
    /// decision price would have earned, less what the account actually earned -- and every term of
    /// it except these two was already recorded.
    ///
    /// Fill prices are not a substitute. They already have the spread and the slippage baked in, so
    /// a cost derived from them sees only the fee and reports roughly half the true figure.
    /// </summary>
    public decimal? EntryReferencePrice { get; init; }

    /// <inheritdoc cref="EntryReferencePrice"/>
    public decimal? ExitReferencePrice { get; init; }

    /// <summary>Account equity immediately before the entry was reserved.</summary>
    public decimal? AccountEquityBefore { get; init; }

    /// <summary>Account equity once this execution reconciled flat.</summary>
    public decimal? AccountEquityAfter { get; init; }

    /// <summary>
    /// What every position the account held was worth, at each of the two equity readings.
    ///
    /// Account equity is a property of the whole portfolio, so a round trip that shared the account
    /// with another has no equity delta of its own -- which is why the cost estimator refuses those
    /// rather than reporting a figure inflated by the concurrency factor, and why the cost dataset
    /// could only ever fill from serialised trading. A lane that holds several positions by design
    /// would never measure its own costs.
    ///
    /// Recording the marks makes the arithmetic possible: a sibling that was merely held across the
    /// window contributes exactly its own change in market value to the equity delta, and that can
    /// be subtracted. A sibling that opened or closed inside the window also moved cash, and its
    /// contribution cannot be separated without the fills -- so those still refuse.
    /// </summary>
    public IReadOnlyList<PositionMark> PositionMarksBefore { get; init; } = [];

    /// <inheritdoc cref="PositionMarksBefore"/>
    public IReadOnlyList<PositionMark> PositionMarksAfter { get; init; } = [];

    /// <summary>
    /// True when this entry was admitted by the exploration budget rather than by having an edge.
    ///
    /// Recorded because two safety mechanisms were cancelling each other out without either being
    /// wrong on its own. The budget deliberately admits a rule the evidence has stood down, to buy
    /// information at a price fixed in advance; the entry fence deliberately refuses a stood-down
    /// rule at submission, because a reservation must not outlive the decision behind it. Together
    /// they produced an exploration entry that reserved, reached the fence, and died -- so the
    /// budget could never actually buy anything, and the only visible trace was a Failed record
    /// saying the strategy was stood down, which was true and beside the point.
    ///
    /// The fence still applies every other check to these. Exploration buys evidence about a rule;
    /// it is not permission to trade into a price that has run away or a book that has widened.
    /// </summary>
    public bool AdmittedAsExploration { get; init; }

    /// <summary>
    /// The quoted spread at the moment the entry was decided, as a fraction of the mid.
    ///
    /// Captured because it cannot be recovered afterwards. The realism adjustment and the
    /// simulation grade both turn on what the book looked like when the decision was made, and a
    /// spread read at reconciliation describes a different market.
    /// </summary>
    public double? DecisionRelativeSpread { get; init; }

    /// <summary>
    /// How far the reported result can be trusted, and every reason it cannot be trusted further.
    ///
    /// A grade is not a measure of profit. A grade A loss is better evidence than a grade D gain,
    /// because the question is whether the number describes trading or describes the simulator.
    /// Every result this system has reported so far carried one fill assumption and no grade.
    /// </summary>
    public string? SimulationGrade { get; init; }

    /// <inheritdoc cref="SimulationGrade"/>
    public IReadOnlyList<string> SimulationGradeReasons { get; init; } = [];

    /// <summary>
    /// What a fill at the far touch on both legs would have cost beyond what the paper engine
    /// charged, in USD.
    ///
    /// Kept beside the paper result rather than replacing it, per section 14.2: the paper number
    /// proves the system behaved and the adjusted number is the only one that says anything about
    /// money, so merging them would lose both claims.
    /// </summary>
    public decimal? AdditionalRealismCost { get; init; }

    /// <summary>
    /// What the round trip actually did to the account.
    ///
    /// The only figure here that owes nothing to a fee model. Alpaca charges a "Coin Pair
    /// Transaction Fee (USD)" that appears in neither the fill price nor the filled quantity, so a
    /// cost derived from fills is not merely less precise -- it is systematically low. Measured
    /// across 59 live round trips, fills reported 36 bps where the account had lost 68.
    /// </summary>
    public decimal? RealisedAccountPnl =>
        AccountEquityBefore is { } before && AccountEquityAfter is { } after ? after - before : null;

    /// <summary>Quantity the application believes it still holds.</summary>
    public decimal InternalOpenQuantity => Math.Max(0m, EntryFilledQuantity - ExitFilledQuantity);

    public bool IsTerminal => State is SpotExecutionState.Complete or SpotExecutionState.Failed;
}

/// <summary>
/// Durable store for spot executions, written atomically so a crash never leaves a partial record.
///
/// Mirrors the guarantees the diagnostic and multi-leg lanes already had. The spot lane was the
/// only money path without them, which meant deterministic client order IDs alone could not
/// deliver restart recovery: recomputing an ID is useless if nothing recorded that the opportunity
/// existed.
/// </summary>
public sealed class SpotExecutionStore(string path)
{
    private readonly Lock _gate = new();

    /// <summary>True when the store's directory is writable, checked before any reservation.</summary>
    public bool IsAvailable()
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Creates a record, refusing a duplicate execution or client order ID.</summary>
    public bool TryCreate(SpotExecutionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!string.Equals(record.ExecutionMode, "PAPER", StringComparison.Ordinal)) return false;

        lock (_gate)
        {
            Dictionary<string, SpotExecutionRecord> records = ReadUnsafe();
            if (records.ContainsKey(record.ExecutionId)) return false;
            // A duplicate client order ID across executions would make broker lookup ambiguous,
            // which is the one thing recovery cannot tolerate.
            if (records.Values.Any(existing =>
                    existing.EntryClientOrderId == record.EntryClientOrderId ||
                    existing.ExitClientOrderId == record.ExitClientOrderId ||
                    existing.EntryClientOrderId == record.ExitClientOrderId ||
                    existing.ExitClientOrderId == record.EntryClientOrderId))
                return false;

            records[record.ExecutionId] = record;
            WriteUnsafe(records);
            return true;
        }
    }

    /// <summary>Replaces a record wholesale; the caller owns transition validity.</summary>
    public void Update(SpotExecutionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            Dictionary<string, SpotExecutionRecord> records = ReadUnsafe();
            records[record.ExecutionId] = record;
            WriteUnsafe(records);
        }
    }

    /// <summary>
    /// Atomically claims the right to submit, so two racing callers cannot both POST. Returns the
    /// claimed record only to the winner.
    /// </summary>
    public bool TryClaimEntrySubmission(
        string executionId, DateTimeOffset attemptedAt, out SpotExecutionRecord? claimed)
    {
        claimed = null;
        lock (_gate)
        {
            Dictionary<string, SpotExecutionRecord> records = ReadUnsafe();
            if (!records.TryGetValue(executionId, out SpotExecutionRecord? record) ||
                record.State != SpotExecutionState.EntryReserved ||
                record.EntrySubmissionAttemptedAt is not null)
                return false;

            claimed = record with
            {
                State = SpotExecutionState.EntrySubmitted,
                EntrySubmissionAttemptedAt = attemptedAt
            };
            records[executionId] = claimed;
            WriteUnsafe(records);
            return true;
        }
    }

    /// <summary>Atomically claims the right to submit the exit.</summary>
    public bool TryClaimExitSubmission(
        string executionId, DateTimeOffset attemptedAt, out SpotExecutionRecord? claimed)
    {
        claimed = null;
        lock (_gate)
        {
            Dictionary<string, SpotExecutionRecord> records = ReadUnsafe();
            if (!records.TryGetValue(executionId, out SpotExecutionRecord? record) ||
                record.State is not (SpotExecutionState.ExitDue or SpotExecutionState.ExitReserved) ||
                record.ExitSubmissionAttemptedAt is not null)
                return false;

            claimed = record with
            {
                State = SpotExecutionState.ExitSubmitted,
                ExitSubmissionAttemptedAt = attemptedAt
            };
            records[executionId] = claimed;
            WriteUnsafe(records);
            return true;
        }
    }

    public SpotExecutionRecord? Find(string executionId)
    {
        lock (_gate) return ReadUnsafe().GetValueOrDefault(executionId);
    }

    /// <summary>Every record a restart must resume.</summary>
    public IReadOnlyList<SpotExecutionRecord> ListNonterminal()
    {
        lock (_gate) return [.. ReadUnsafe().Values.Where(record => !record.IsTerminal)];
    }

    /// <summary>
    /// Every round trip that completed cleanly, for measuring what trading actually cost.
    ///
    /// Only Complete, and deliberately not the failure states. A trip that was rejected, cancelled,
    /// or emergency-flattened either paid no cost or paid an exceptional one, and averaging those
    /// into a cost curve would describe a kind of trading this system does not do on purpose.
    /// </summary>
    public IReadOnlyList<SpotExecutionRecord> ListCompleted()
    {
        lock (_gate)
        {
            return [.. ReadUnsafe().Values.Where(record => record.State is SpotExecutionState.Complete)];
        }
    }

    public IReadOnlyList<SpotExecutionRecord> ListAll()
    {
        lock (_gate) return [.. ReadUnsafe().Values];
    }

    private Dictionary<string, SpotExecutionRecord> ReadUnsafe()
    {
        if (!File.Exists(path)) return new Dictionary<string, SpotExecutionRecord>(StringComparer.Ordinal);
        string content = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(content))
            return new Dictionary<string, SpotExecutionRecord>(StringComparer.Ordinal);

        Dictionary<string, SpotExecutionRecord>? records =
            JsonSerializer.Deserialize<Dictionary<string, SpotExecutionRecord>>(content, ContractJson.Web)
            ?? throw new InvalidDataException("Spot execution store is not a record map.");

        // A record claiming any mode other than PAPER is corrupt or tampered with; refusing to load
        // is the only safe response, because the alternative is resuming an unknown lane.
        foreach (SpotExecutionRecord record in records.Values)
        {
            if (!string.Equals(record.ExecutionMode, "PAPER", StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Spot execution '{record.ExecutionId}' is not a PAPER record.");
        }

        return new Dictionary<string, SpotExecutionRecord>(records, StringComparer.Ordinal);
    }

    private void WriteUnsafe(Dictionary<string, SpotExecutionRecord> records) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(records, ContractJson.Web));
}
