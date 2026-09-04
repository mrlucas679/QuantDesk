using Microsoft.Extensions.Logging.Abstractions;
using QuantDesk.Api.PaperTrading;

namespace QuantDesk.Api.Tests;

/// <summary>
/// A cycle that cannot evaluate must abstain, not end the lane.
///
/// Regression test for a live failure: outside regular hours SPY has no two-sided quote, the evidence
/// provider threw, and because the try wrapped the whole loop the autonomous trader stopped
/// permanently and degraded the runtime. That is roughly nineteen hours of every day, plus weekends.
/// </summary>
public sealed class AutonomousCycleResilienceTests
{
    [Fact]
    public void AnUnavailableMarketIsAFaultToAbsorbNotAShutdown()
    {
        // The classifier the cycle guard uses. A market-data failure is a fault to log and continue
        // from; only a signalled stopping token means the lane should end.
        var running = CancellationToken.None;
        var evidenceFailure = new InvalidOperationException(
            "Alpaca latest equity quote for 'SPY' did not contain a valid two-sided spread.");

        Assert.True(HostedServiceFaults.IsFault(evidenceFailure, running));
        Assert.True(HostedServiceFaults.IsFault(new HttpRequestException("venue unreachable"), running));
        Assert.True(HostedServiceFaults.IsFault(new TaskCanceledException("http timeout"), running));
    }

    [Fact]
    public void ShutdownIsNotTreatedAsAFaultToAbsorb()
    {
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();

        Assert.False(HostedServiceFaults.IsFault(new OperationCanceledException(), stopping.Token));
    }
}
