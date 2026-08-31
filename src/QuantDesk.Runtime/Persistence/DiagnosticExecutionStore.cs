using System.Text.Json;

namespace QuantDesk.Runtime.Persistence;

/// <summary>Durably records one diagnostic lifecycle and reserves its client IDs.</summary>
public sealed class DiagnosticExecutionStore(string path)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly Lock gate = new();

    /// <summary>Verifies that the configured store can be read and durably replaced.</summary>
    public bool IsAvailable()
    {
        lock (gate)
        {
            try
            {
                State state = LoadOrEmpty();
                Save(state);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               System.Security.SecurityException or JsonException or NotSupportedException)
            {
                return false;
            }
        }
    }

    public bool TryReserve(string experimentId, string clientOrderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        lock (gate)
        {
            var state = LoadOrEmpty();
            if (state.Reservations.Contains(clientOrderId, StringComparer.Ordinal)) return false;
            state.Reservations.Add(clientOrderId);
            Save(state);
            return true;
        }
    }

    /// <summary>Atomically persists a lifecycle record and all deterministic client-order reservations.</summary>
    public bool TryCreateReservation(DiagnosticExecutionRecord record, params string[] clientOrderIds)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(clientOrderIds);
        if (clientOrderIds.Length == 0 || clientOrderIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one client-order ID is required.", nameof(clientOrderIds));

        lock (gate)
        {
            State state = LoadOrEmpty();
            if (state.Records.Any(item => item.ExperimentId == record.ExperimentId) ||
                clientOrderIds.Any(id => state.Reservations.Contains(id, StringComparer.Ordinal)))
                return false;

            state.Reservations.AddRange(clientOrderIds);
            state.Records.Add(record);
            Save(state);
            return true;
        }
    }

    public void Record(DiagnosticExecutionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (gate)
        {
            var state = LoadOrEmpty();
            state.Records.RemoveAll(item => item.ExperimentId == record.ExperimentId);
            state.Records.Add(record);
            Save(state);
        }
    }

    public DiagnosticExecutionRecord? Find(string experimentId)
    {
        lock (gate) return LoadOrEmpty().Records.Find(item => item.ExperimentId == experimentId);
    }

    /// <summary>Returns a stable snapshot of all diagnostic records that still require lifecycle work.</summary>
    public IReadOnlyList<DiagnosticExecutionRecord> ListNonterminal()
    {
        lock (gate)
        {
            return LoadOrEmpty().Records
                .Where(record => record.State is not (
                    "Complete" or "ReconciliationFailed" or "EmergencyFlattenFailed" or
                    "EntryCanceled" or "EntryRejected" or "EntryExpired" or
                    "ExitCanceled" or "ExitRejected" or "ExitExpired"))
                .ToArray();
        }
    }

    public void Update(string experimentId, Func<DiagnosticExecutionRecord, DiagnosticExecutionRecord> mutate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        ArgumentNullException.ThrowIfNull(mutate);
        lock (gate)
        {
            State state = LoadOrEmpty();
            int index = state.Records.FindIndex(item => item.ExperimentId == experimentId);
            if (index < 0)
                throw new KeyNotFoundException($"Diagnostic experiment '{experimentId}' was not found.");
            DiagnosticExecutionRecord record = state.Records[index];
            state.Records[index] = mutate(record);
            Save(state);
        }
    }

    /// <summary>
    /// Claims the sole entry submission attempt while keeping the durable lifecycle state at EntryReserved
    /// until a broker result is known.
    /// </summary>
    public bool TryClaimEntrySubmission(
        string experimentId,
        decimal requestedQuantity,
        DateTimeOffset attemptedAt,
        out DiagnosticExecutionRecord? claimed)
    {
        lock (gate)
        {
            State state = LoadOrEmpty();
            int index = state.Records.FindIndex(item => item.ExperimentId == experimentId);
            if (index < 0 || state.Records[index].State != "EntryReserved" ||
                state.Records[index].EntrySubmissionAttemptedAt is not null)
            {
                claimed = index < 0 ? null : state.Records[index];
                return false;
            }

            claimed = state.Records[index] with
            {
                RequestedQuantity = requestedQuantity,
                EntrySubmissionAttemptedAt = attemptedAt
            };
            state.Records[index] = claimed;
            Save(state);
            return true;
        }
    }

    /// <summary>Persists the broker-derived flatten quantity before any exit submission can be claimed.</summary>
    public bool TryReserveExit(
        string experimentId,
        decimal exitQuantity,
        DateTimeOffset reservedAt,
        out DiagnosticExecutionRecord? reserved)
    {
        if (exitQuantity <= 0) throw new ArgumentOutOfRangeException(nameof(exitQuantity));
        lock (gate)
        {
            State state = LoadOrEmpty();
            int index = state.Records.FindIndex(item => item.ExperimentId == experimentId);
            if (index < 0 || state.Records[index].State != "ExitDue" ||
                state.Records[index].ExitReservedAt is not null)
            {
                reserved = index < 0 ? null : state.Records[index];
                return false;
            }

            reserved = state.Records[index] with
            {
                State = "ExitReserved",
                ExitQuantity = exitQuantity,
                ExitReservedAt = reservedAt
            };
            state.Records[index] = reserved;
            Save(state);
            return true;
        }
    }

    /// <summary>Claims the sole exit POST while leaving the durable state at ExitReserved.</summary>
    public bool TryClaimExitSubmission(
        string experimentId,
        DateTimeOffset attemptedAt,
        out DiagnosticExecutionRecord? claimed)
    {
        lock (gate)
        {
            State state = LoadOrEmpty();
            int index = state.Records.FindIndex(item => item.ExperimentId == experimentId);
            if (index < 0 || state.Records[index].State != "ExitReserved" ||
                state.Records[index].ExitSubmissionAttemptedAt is not null)
            {
                claimed = index < 0 ? null : state.Records[index];
                return false;
            }

            claimed = state.Records[index] with { ExitSubmissionAttemptedAt = attemptedAt };
            state.Records[index] = claimed;
            Save(state);
            return true;
        }
    }

    /// <summary>Claims the sole emergency flatten POST across workers and process restarts.</summary>
    public bool TryClaimEmergencySubmission(
        string experimentId,
        decimal quantity,
        DateTimeOffset attemptedAt,
        out DiagnosticExecutionRecord? claimed)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        lock (gate)
        {
            State state = LoadOrEmpty();
            int index = state.Records.FindIndex(item => item.ExperimentId == experimentId);
            if (index < 0 || state.Records[index].EmergencySubmissionAttemptedAt is not null)
            {
                claimed = index < 0 ? null : state.Records[index];
                return false;
            }

            claimed = state.Records[index] with
            {
                State = "EmergencyFlattenReserved",
                EmergencyFlattenQuantity = quantity,
                EmergencyReservedAt = attemptedAt,
                EmergencySubmissionAttemptedAt = attemptedAt
            };
            state.Records[index] = claimed;
            Save(state);
            return true;
        }
    }

    private State LoadOrEmpty() => File.Exists(path)
        ? JsonSerializer.Deserialize<State>(File.ReadAllText(path), Options) ?? new State()
        : new State();

    private void Save(State state)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(state, Options);
        using (var stream = new FileStream(
                   temporary,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 4_096,
                   FileOptions.WriteThrough))
        {
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, true);
    }

    private sealed class State
    {
        public List<string> Reservations { get; init; } = [];
        public List<DiagnosticExecutionRecord> Records { get; init; } = [];
    }
}

public enum DiagnosticExecutionFailure
{
    None, InvalidRequest, InfrastructureNotReady, PaperAccountUnavailable,
    AssetNotTradable, RiskEnvelopeExceeded, ReconciliationMismatch, SubmissionUnknown,
    EntryCanceled, EntryRejected, EntryExpired, FillTimeout, ExitFailed,
    ExitCanceled, ExitRejected, ExitExpired, ReconciliationFailed, EmergencyFlattenFailed,
    PersistenceFailed
}

public sealed record DiagnosticExecutionRecord(
    string ExperimentId,
    string Classification,
    string Symbol,
    string State,
    decimal RequestedNotional,
    TimeSpan HoldingDuration,
    DateTimeOffset CreatedAt,
    string? EntryClientOrderId,
    string? ExitClientOrderId)
{
    public decimal RequestedQuantity { get; init; }
    public string? EntryBrokerOrderId { get; init; }
    public DateTimeOffset? EntryReservedAt { get; init; }
    public DateTimeOffset? EntrySubmissionAttemptedAt { get; init; }
    public DateTimeOffset? EntrySubmittedAt { get; init; }
    public DateTimeOffset? EntryBrokerCreatedAt { get; init; }
    public DateTimeOffset? EntryBrokerUpdatedAt { get; init; }
    public DateTimeOffset? EntryBrokerCanceledAt { get; init; }
    public DateTimeOffset? EntryBrokerExpiredAt { get; init; }
    public DateTimeOffset? EntryBrokerRejectedAt { get; init; }
    public DateTimeOffset? FirstEntryFillAt { get; init; }
    public DateTimeOffset? FinalEntryFillAt { get; init; }
    public decimal EntryFilledQuantity { get; init; }
    public decimal? EntryAverageFillPrice { get; init; }
    public DateTimeOffset? HoldStartedAt { get; init; }
    public DateTimeOffset? ScheduledExitAt { get; init; }
    public string? ExitBrokerOrderId { get; init; }
    public DateTimeOffset? ExitReservedAt { get; init; }
    public DateTimeOffset? ExitSubmissionAttemptedAt { get; init; }
    public DateTimeOffset? ExitSubmittedAt { get; init; }
    public DateTimeOffset? ExitBrokerCreatedAt { get; init; }
    public DateTimeOffset? ExitBrokerUpdatedAt { get; init; }
    public DateTimeOffset? ExitBrokerCanceledAt { get; init; }
    public DateTimeOffset? ExitBrokerExpiredAt { get; init; }
    public DateTimeOffset? ExitBrokerRejectedAt { get; init; }
    public DateTimeOffset? FirstExitFillAt { get; init; }
    public DateTimeOffset? FinalExitFillAt { get; init; }
    public decimal ExitQuantity { get; init; }
    public decimal ExitFilledQuantity { get; init; }
    public decimal? ExitAverageFillPrice { get; init; }
    public decimal FinalBrokerQuantity { get; init; }
    public decimal FinalInternalQuantity { get; init; }
    public string? ReconciliationResult { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? EmergencyClientOrderId { get; init; }
    public string? EmergencyBrokerOrderId { get; init; }
    public DateTimeOffset? EmergencyReservedAt { get; init; }
    public DateTimeOffset? EmergencySubmissionAttemptedAt { get; init; }
    public decimal EmergencyFlattenQuantity { get; init; }
    public decimal EmergencyFilledQuantity { get; init; }
    public DiagnosticExecutionFailure Failure { get; init; }
    public string? FailureReason { get; init; }
}
