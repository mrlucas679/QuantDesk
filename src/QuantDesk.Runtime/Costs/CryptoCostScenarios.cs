namespace QuantDesk.Runtime.Costs;

/// <summary>Explicit economic assumptions for crypto research and paper decisions.</summary>
public enum CryptoCostScenario
{
    FeeFloor,
    Base,
    Conservative,
    Stress
}

/// <summary>Calculates transparent round-trip bps without hiding assumption provenance.</summary>
public static class CryptoCostScenarios
{
    public static decimal RoundTripBps(CryptoCostScenario scenario, decimal observedSpreadBps) =>
        scenario switch
        {
            CryptoCostScenario.FeeFloor => 50m + observedSpreadBps,
            CryptoCostScenario.Base => 50m + observedSpreadBps + 10m,
            CryptoCostScenario.Conservative => 50m + observedSpreadBps + 20m,
            CryptoCostScenario.Stress => 50m + observedSpreadBps + 40m,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown crypto cost scenario.")
        };
}
