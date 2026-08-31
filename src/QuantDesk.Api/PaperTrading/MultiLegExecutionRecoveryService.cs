using QuantDesk.Runtime.Execution;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Continuously resumes every durable, nonterminal MLeg lifecycle after startup.</summary>
public sealed class MultiLegExecutionRecoveryService(
    MultiLegExecutionLifecycle lifecycle,
    ILogger<MultiLegExecutionRecoveryService> logger) : BackgroundService
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
            LastError = exception.GetType().Name;
            logger.LogError(exception, "Unable to recover durable MLeg executions.");
        }
    }
}
