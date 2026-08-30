using QuantDesk.Runtime.Costs;

namespace QuantDesk.Runtime.Tests.Costs;

public sealed class CryptoCostScenariosTests
{
    [Fact]
    public void ScenariosKeepObservedSpreadSeparateFromFeeFloor()
    {
        Assert.Equal(52.5m, CryptoCostScenarios.RoundTripBps(CryptoCostScenario.FeeFloor, 2.5m));
        Assert.Equal(62.5m, CryptoCostScenarios.RoundTripBps(CryptoCostScenario.Base, 2.5m));
        Assert.Equal(72.5m, CryptoCostScenarios.RoundTripBps(CryptoCostScenario.Conservative, 2.5m));
        Assert.Equal(92.5m, CryptoCostScenarios.RoundTripBps(CryptoCostScenario.Stress, 2.5m));
    }
}
