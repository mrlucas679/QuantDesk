namespace QuantDesk.Domain.Replay;

public enum ReplayEvidenceClass
{
    PassiveHistoricalReplay,
    CounterfactualOrderBook,
    BrokerPaper
}

/// <summary>
/// One recorded event, carrying both of section 8.2's timelines.
///
/// <para><paramref name="EventUnixNanoseconds"/> is when the venue says the thing happened. It is a
/// per-instrument quantity: two symbols are two order books on two venue clocks, so across a
/// multi-instrument feed these legitimately interleave and go backwards. Only within one source is
/// it required to advance.</para>
///
/// <para><paramref name="ReceiveMonotonicNanoseconds"/> is how long into the session this runtime
/// saw it, from a reading that only moves forward. That is the timeline a replay advances along.</para>
///
/// <para>It is deliberately not the wall clock. Wall time is not monotonic -- an NTP correction
/// steps it, and this system has already had uptime come back negative from exactly that. A live
/// 31,695-event session was refused by the replay gate because receive time, then derived from
/// <c>UtcNow</c>, went backwards at event 6,447: the gate was right and the timeline was wrong.</para>
/// </summary>
/// <param name="ReceiveOffsetNanoseconds">
/// How much later this runtime saw the event than the venue stamped it, by wall clock. Kept because
/// it is the only measure of feed latency, and used for nothing that must be ordered. May be
/// negative: a venue clock running ahead of ours produces a genuinely negative offset, and clamping
/// it to zero would silently move the receive time it describes.
/// </param>
/// <param name="ReceiveMonotonicNanoseconds">
/// Nanoseconds since this recording session began, from the monotonic clock. Zero on a log written
/// before the field existed, which the runner treats as an unusable timeline rather than as
/// simultaneity.
/// </param>
public sealed record ReplayEnvelope(
    int SchemaVersion,
    long IngressSequence,
    string Source,
    long EventUnixNanoseconds,
    long ReceiveOffsetNanoseconds,
    string EventType,
    byte[] Payload,
    long ReceiveMonotonicNanoseconds = 0L)
{
    /// <summary>When this runtime saw the event by wall clock. Informational; never an ordering.</summary>
    public long ReceiveUnixNanoseconds => EventUnixNanoseconds + ReceiveOffsetNanoseconds;

    /// <summary>Whether this envelope carries the monotonic timeline a replay can advance along.</summary>
    public bool HasMonotonicReceiveTime => ReceiveMonotonicNanoseconds > 0
        || (ReceiveMonotonicNanoseconds == 0 && IngressSequence == 1);

    public bool IsValid() => SchemaVersion > 0
        && IngressSequence > 0
        && !string.IsNullOrWhiteSpace(Source)
        && EventUnixNanoseconds > 0
        && ReceiveUnixNanoseconds > 0
        && ReceiveMonotonicNanoseconds >= 0
        && !string.IsNullOrWhiteSpace(EventType)
        && Payload is not null;
}

public sealed record ReplayManifest(
    string ConfigurationHash,
    string ModelArtifactsHash,
    string PolicyVersion,
    long RandomSeed,
    ReplayEvidenceClass EvidenceClass)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(ConfigurationHash)
        && !string.IsNullOrWhiteSpace(ModelArtifactsHash)
        && !string.IsNullOrWhiteSpace(PolicyVersion)
        && Enum.IsDefined(EvidenceClass);
}

public sealed record ReplayDecision(
    long IngressSequence,
    string DecisionCode,
    string DeterministicPayloadHash)
{
    public bool IsValid() => IngressSequence > 0
        && !string.IsNullOrWhiteSpace(DecisionCode)
        && !string.IsNullOrWhiteSpace(DeterministicPayloadHash);
}

public sealed record ReplayRunResult(
    ReplayEvidenceClass EvidenceClass,
    int EventCount,
    string DeterministicTraceHash,
    IReadOnlyList<ReplayDecision> Decisions,
    DateTimeOffset FinalVirtualTime,
    long FinalMonotonicTimestamp);
