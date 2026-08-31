using System.Text.Json;
using QuantDesk.Domain.Execution;

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
    SubmissionUnknown
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
    public DateTimeOffset? ReconciledAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? FailureReason { get; init; }

    public decimal InternalOpenQuantity => Math.Max(0, EntryFilledQuantity - ExitFilledQuantity);
}

/// <summary>Atomically persists MLeg records and submission claims across process restarts.</summary>
public sealed class MultiLegExecutionStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();

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
                    MultiLegExecutionState.ReconciliationFailed))
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
