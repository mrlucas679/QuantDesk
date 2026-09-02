using QuantDesk.Domain.Execution;
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

    public decimal DefinedMaximumLoss { get; init; }
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
