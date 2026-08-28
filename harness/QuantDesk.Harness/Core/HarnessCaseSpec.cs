namespace QuantDesk.Harness.Core;

public enum HarnessCriticality
{
    Informational,
    Quality,
    Reliability,
    Security,
    FinancialSafety
}

public sealed record HarnessEnvironmentSpec(
    string DataMode,
    string BrokerMode,
    string ClockMode,
    string RandomMode,
    string ConfigFingerprint,
    string ModelRegistryVersion,
    string PolicyVersion,
    string ExecutionEvidenceVersion);

public sealed record HarnessRunBudget(
    TimeSpan MaximumWallTime,
    long MaximumMarketEvents,
    int MaximumOrders,
    int MaximumAgentSteps,
    long MaximumAgentTokens,
    long MaximumAllocatedBytes)
{
    public void Validate()
    {
        if (MaximumWallTime <= TimeSpan.Zero ||
            MaximumMarketEvents <= 0 ||
            MaximumOrders <= 0 ||
            MaximumAgentSteps <= 0 ||
            MaximumAgentTokens <= 0 ||
            MaximumAllocatedBytes <= 0)
        {
            throw new InvalidOperationException("Every harness budget must be positive and bounded.");
        }
    }
}

public sealed record HarnessCaseSpec(
    string Id,
    string Suite,
    string CriticalSlice,
    HarnessCriticality Criticality,
    string ScenarioVersion,
    long Seed,
    HarnessEnvironmentSpec Environment,
    HarnessRunBudget Budget,
    IReadOnlyList<string> OracleIds,
    IReadOnlyList<string> ScorerIds,
    IReadOnlyList<string> ScannerIds,
    IReadOnlyList<string> FaultIds,
    IReadOnlyDictionary<string, string> Metadata);

