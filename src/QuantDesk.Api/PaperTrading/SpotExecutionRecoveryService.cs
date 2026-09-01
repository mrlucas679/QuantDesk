using QuantDesk.Runtime.Execution;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Continuously resumes every durable, nonterminal spot execution after startup.
///
/// Without this the durable store would record an opportunity that nothing advances: a process
/// restarted mid-hold would leave the position open past its scheduled exit, and an ambiguous
/// submission would never be resolved. This is what turns persistence into recovery.
/// </summary>
public sealed class SpotExecutionRecoveryService(
    SpotExecutionLifecycle lifecycle,
    ILogger<SpotExecutionRecoveryService> logger) : BackgroundService
{
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(1);

    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? LastCycleAt { get; private set; }
    public string? LastError { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartedAt = DateTimeOffset.UtcNow;
        await ResumeAllAsync(stoppingToken);
        using var timer = new PeriodicTimer(RecoveryInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await ResumeAllAsync(stoppingToken);
    }

    internal async Task ResumeAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            await lifecycle.RecoverAllAsync(cancellationToken);
            LastCycleAt = DateTimeOffset.UtcNow;
            LastError = null;
        }
        catch (Exception exception)
        {
            // Recording the failure and continuing is deliberate: one bad record must not stop the
            // worker that is the only thing able to close the others.
            LastError = exception.GetType().Name;
            logger.LogError(exception, "Unable to recover durable spot executions.");
        }
    }
}
