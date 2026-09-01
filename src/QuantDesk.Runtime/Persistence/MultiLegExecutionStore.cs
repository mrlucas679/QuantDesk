using System.Text.Json;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;

namespace QuantDesk.Runtime.Persistence;

public enum MultiLegExecutionState
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
    EntryRejected,
    ExitRejected,
    ReconciliationFailed,
    SubmissionUnknown,
    EmergencyFlattening,
    EmergencyFlattened,
    /// <summary>
    /// The broker never exposed an order for an ambiguous submission within the bounded recovery
    /// period. This is deliberately terminal: submitting again could duplicate exposure.
    /// </summary>
    SubmissionUnresolved
}

/// <summary>Durable parent-order lifecycle for one atomic, defined-risk options position.</summary>
public sealed record MultiLegExecutionRecord(
    string ExecutionId,
    string StrategyId,
    MultiLegExecutionState State,
    MultiLegExecutionCommand EntryCommand,
    MultiLegExecutionCommand ExitCommand,
    DateTimeOffset CreatedAt,
    DateTimeOffset EntryReservedAt)
{
    public string ExecutionMode { get; init; } = "PAPER";
    public decimal DefinedMaximumLoss { get; init; }
    public TimeSpan MaximumHoldingPeriod { get; init; }
    public DateTimeOffset? EntrySubmissionAttemptedAt { get; init; }
    public DateTimeOffset? EntrySubmittedAt { get; init; }
    public DateTimeOffset? EntryAcknowledgedAt { get; init; }
    public string? EntryBrokerOrderId { get; init; }
    public decimal EntryFilledQuantity { get; init; }
    public decimal? EntryAverageFillPrice { get; init; }
    public DateTimeOffset? EntryFinalFillAt { get; init; }
    public IReadOnlyList<BrokerOrderLegSnapshot> EntryLegs { get; init; } = [];
    public DateTimeOffset? HoldStartedAt { get; init; }
    public DateTimeOffset? ScheduledExitAt { get; init; }
    public DateTimeOffset? ExitReservedAt { get; init; }
    public DateTimeOffset? ExitSubmissionAttemptedAt { get; init; }
    public DateTimeOffset? ExitSubmittedAt { get; init; }
    public DateTimeOffset? ExitAcknowledgedAt { get; init; }
    public string? ExitBrokerOrderId { get; init; }
    public decimal ExitFilledQuantity { get; init; }
    public decimal? ExitAverageFillPrice { get; init; }
    public DateTimeOffset? ExitFinalFillAt { get; init; }
    public IReadOnlyList<BrokerOrderLegSnapshot> ExitLegs { get; init; } = [];
    public DateTimeOffset? ReconciledAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? FailureReason { get; init; }

    /// <summary>Why the hold ended before its timer, or null when the timer ended it.</summary>
    public string? EarlyExitReason { get; init; }

    /// <summary>The research publication that authorised this position, captured at reservation.</summary>
    public PositionOwnership? Ownership { get; init; }

    public decimal InternalOpenQuantity => Math.Max(0, EntryFilledQuantity - ExitFilledQuantity);

    /// <summary>
    /// Reconstructs the signed option inventory owned by this lifecycle from broker-supplied leg
    /// fills. A missing, duplicate, or directionally inconsistent leg is deliberately not
    /// represented as zero inventory: reconciliation must halt rather than hide exposure.
    /// </summary>
    public bool TryGetInternalOpenLegQuantities(out IReadOnlyDictionary<string, decimal> quantities)
    {
        quantities = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (EntryLegs.Count != EntryCommand.Legs.Count || ExitLegs.Count != ExitCommand.Legs.Count)
            return false;

        var reconstructed = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (MultiLegExecutionLeg entry in EntryCommand.Legs)
        {
            BrokerOrderLegSnapshot? entryFill = FindSingle(EntryLegs, entry.Symbol);
            BrokerOrderLegSnapshot? exitFill = FindSingle(ExitLegs, entry.Symbol);
            if (entryFill is null || exitFill is null || entryFill.FilledQuantity < 0 ||
                exitFill.FilledQuantity < 0 || entryFill.FilledQuantity < exitFill.FilledQuantity)
                return false;

            decimal sign = entry.Side == OrderSide.Buy ? 1m : -1m;
            reconstructed[entry.Symbol] = sign * (entryFill.FilledQuantity - exitFill.FilledQuantity);
        }

        quantities = reconstructed;
        return true;
    }

    private static BrokerOrderLegSnapshot? FindSingle(
        IReadOnlyList<BrokerOrderLegSnapshot> legs,
        string symbol)
    {
        BrokerOrderLegSnapshot[] matches = legs.Where(leg =>
            string.Equals(leg.Symbol, symbol, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}

/// <summary>Atomically persists MLeg records and submission claims across process restarts.</summary>
public sealed class MultiLegExecutionStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = QuantDesk.Domain.Serialization.ContractJson.Web;
    // Lifecycles are constructed independently during recovery and tests. The lock must therefore
    // outlive a store object, or two owners can both observe an empty state and submit the same
    // opportunity. MLeg persistence is low-volume, so a process-wide gate is preferable to a
    // racy per-object lock; separate processes are still fenced by deterministic client IDs.
    private static readonly Lock ProcessGate = new();
    private readonly Lock _gate = ProcessGate;

    public bool IsAvailable()
    {
        lock (_gate)
        {
            try
            {
                Save(Load());
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               System.Security.SecurityException or JsonException or NotSupportedException)
            {
                return false;
            }
        }
    }

    public bool TryCreate(MultiLegExecutionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            State state = Load();
            if (state.Records.Any(item => item.ExecutionId == record.ExecutionId) ||
                state.ClientOrderIds.Contains(record.EntryCommand.ClientOrderId, StringComparer.Ordinal) ||
                state.ClientOrderIds.Contains(record.ExitCommand.ClientOrderId, StringComparer.Ordinal))
                return false;
            state.ClientOrderIds.Add(record.EntryCommand.ClientOrderId);
            state.ClientOrderIds.Add(record.ExitCommand.ClientOrderId);
            state.Records.Add(record);
            Save(state);
            return true;
        }
    }

    public MultiLegExecutionRecord? Find(string executionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        lock (_gate) return Load().Records.Find(item => item.ExecutionId == executionId);
    }

    public IReadOnlyList<MultiLegExecutionRecord> ListNonterminal()
    {
        lock (_gate)
        {
            return Load().Records
                .Where(item => item.State is not (MultiLegExecutionState.Complete or
                    MultiLegExecutionState.EntryRejected or MultiLegExecutionState.ExitRejected or
                    MultiLegExecutionState.ReconciliationFailed or
                    MultiLegExecutionState.EmergencyFlattened or
                    MultiLegExecutionState.SubmissionUnresolved))
                .ToArray();
        }
    }

    public void Update(string executionId, Func<MultiLegExecutionRecord, MultiLegExecutionRecord> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            State state = Load();
            int index = state.Records.FindIndex(item => item.ExecutionId == executionId);
            if (index < 0) throw new KeyNotFoundException($"MLeg execution '{executionId}' was not found.");
            state.Records[index] = mutate(state.Records[index]);
            Save(state);
        }
    }

    public bool TryClaimEntrySubmission(string executionId, DateTimeOffset attemptedAt)
    {
        return TryClaim(executionId, MultiLegExecutionState.EntryReserved,
            item => item.EntrySubmissionAttemptedAt is null,
            item => item with { EntrySubmissionAttemptedAt = attemptedAt });
    }

    public bool TryReserveExit(string executionId, DateTimeOffset reservedAt)
    {
        return TryClaim(executionId, MultiLegExecutionState.ExitDue,
            item => item.ExitReservedAt is null,
            item => item with { State = MultiLegExecutionState.ExitReserved, ExitReservedAt = reservedAt });
    }

    public bool TryClaimExitSubmission(string executionId, DateTimeOffset attemptedAt)
    {
        return TryClaim(executionId, MultiLegExecutionState.ExitReserved,
            item => item.ExitSubmissionAttemptedAt is null,
            item => item with { ExitSubmissionAttemptedAt = attemptedAt });
    }

    private bool TryClaim(
        string executionId,
        MultiLegExecutionState requiredState,
        Func<MultiLegExecutionRecord, bool> predicate,
        Func<MultiLegExecutionRecord, MultiLegExecutionRecord> mutate)
    {
        lock (_gate)
        {
            State state = Load();
            int index = state.Records.FindIndex(item => item.ExecutionId == executionId);
            if (index < 0 || state.Records[index].State != requiredState || !predicate(state.Records[index]))
                return false;
            state.Records[index] = mutate(state.Records[index]);
            Save(state);
            return true;
        }
    }

    private State Load() => File.Exists(path)
        ? JsonSerializer.Deserialize<State>(File.ReadAllText(path), JsonOptions) ?? new State()
        : new State();

    private void Save(State state)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                   FileShare.None, 4_096, FileOptions.WriteThrough))
        {
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, true);
    }

    private sealed class State
    {
        public List<string> ClientOrderIds { get; init; } = [];
        public List<MultiLegExecutionRecord> Records { get; init; } = [];
    }
}
