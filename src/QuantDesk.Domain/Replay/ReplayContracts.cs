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
/// <para><paramref name="ReceiveOffsetNanoseconds"/> plus the event time is when this runtime saw
/// it. That timeline is monotonic by construction -- it is one process's own clock read in ingress
/// order -- which is why it, and not venue time, is what a replay advances.</para>
/// </summary>
/// <param name="ReceiveOffsetNanoseconds">
/// How much later this runtime saw the event than the venue stamped it. May be negative: a venue
/// clock running ahead of ours produces a genuinely negative offset, and clamping it to zero would
/// silently move the receive time this record exists to preserve.
/// </param>
public sealed record ReplayEnvelope(
    int SchemaVersion,
    long IngressSequence,
    string Source,
    long EventUnixNanoseconds,
    long ReceiveOffsetNanoseconds,
    string EventType,
    byte[] Payload)
{
    /// <summary>When this runtime saw the event -- the timeline a replay advances along.</summary>
    public long ReceiveUnixNanoseconds => EventUnixNanoseconds + ReceiveOffsetNanoseconds;

    public bool IsValid() => SchemaVersion > 0
        && IngressSequence > 0
        && !string.IsNullOrWhiteSpace(Source)
        && EventUnixNanoseconds > 0
        && ReceiveUnixNanoseconds > 0
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
