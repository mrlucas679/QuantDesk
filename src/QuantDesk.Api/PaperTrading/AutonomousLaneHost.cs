namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Runs every configured autonomous lane side by side.
///
/// Each lane is a full <see cref="AutonomousPaperTradingService"/> with its own symbols, order
/// size, and holding period, so a lane's settings belong to the instruments it trades rather than
/// being averaged across all of them.
///
/// They are started together and stopped together, but they do not share a cycle: a lane that
/// throws on startup must not prevent the others from running, because one asset class being
/// misconfigured or its venue being unreachable says nothing about the rest.
/// </summary>
public sealed class AutonomousLaneHost(IReadOnlyList<AutonomousPaperTradingService> lanes)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) =>
        await Task.WhenAll(lanes.Select(lane => lane.StartAsync(stoppingToken)));

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(lanes.Select(lane => lane.StopAsync(cancellationToken)));
        await base.StopAsync(cancellationToken);
    }
}
