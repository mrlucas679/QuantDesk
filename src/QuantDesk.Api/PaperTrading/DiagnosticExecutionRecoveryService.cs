using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Resumes durable diagnostic lifecycles after startup and on each worker interval.</summary>
public sealed class DiagnosticExecutionRecoveryService(
    DiagnosticExecutionStore store,
    CryptoDiagnosticExecutionService diagnostics,
    ILogger<DiagnosticExecutionRecoveryService> logger,
    IRuntimeClock clock) : BackgroundService
{
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(1);

    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? LastCycleAt { get; private set; }
    public string? LastError { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartedAt = clock.UtcNow;
        await ResumeAllAsync(stoppingToken);
        using var timer = new PeriodicTimer(RecoveryInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await ResumeAllAsync(stoppingToken);
    }

    internal async Task ResumeAllAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DiagnosticExecutionRecord> records;
        try
        {
            records = store.ListNonterminal();
        }
        catch (Exception exception)
        {
            LastError = exception.GetType().Name;
            logger.LogError(exception, "Unable to load diagnostic executions for recovery.");
            return;
        }

        foreach (DiagnosticExecutionRecord record in records)
        {
            try
            {
                await diagnostics.AdvanceAsync(
                    record.ExperimentId,
                    record.RequestedQuantity,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                LastError = exception.GetType().Name;
                logger.LogError(
                    exception,
                    "Diagnostic recovery failed for {ExperimentId} in state {State}.",
                    record.ExperimentId,
                    record.State);
            }
        }

        LastCycleAt = clock.UtcNow;
        LastError = null;
    }
}
