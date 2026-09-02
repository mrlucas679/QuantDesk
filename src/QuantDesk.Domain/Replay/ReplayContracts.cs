namespace QuantDesk.Domain.Replay;

public enum ReplayEvidenceClass
{
    PassiveHistoricalReplay,
    CounterfactualOrderBook,
    BrokerPaper
}

public sealed record ReplayEnvelope(
    int SchemaVersion,
    long IngressSequence,
    string Source,
    long EventUnixNanoseconds,
    long ReceiveOffsetNanoseconds,
    string EventType,
    byte[] Payload)
{
    public bool IsValid() => SchemaVersion > 0
        && IngressSequence > 0
        && !string.IsNullOrWhiteSpace(Source)
        && EventUnixNanoseconds > 0
        && ReceiveOffsetNanoseconds >= 0
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
