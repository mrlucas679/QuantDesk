using System.Text.Json;
using QuantDesk.Domain.Contracts;
using QuantDesk.Runtime.Costs;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Publishes what trading actually cost to the volume the research plane reads.
///
/// The missing half of a two-way exchange
/// --------------------------------------
/// Research publishes artifacts to a volume execution watches, and that direction worked. The
/// reverse direction did not exist: execution is the only side that can measure a round trip —
/// account equity before and after is the only thing that sees the venue's separate USD cash
/// charge — and it had no way to tell research. So research went on assuming a cost, execution went
/// on charging a different one, and neither matched the 68 bps the account was actually paying.
///
/// The <c>/api/costs/realised</c> endpoint exposes the same dataset, but an endpoint only answers
/// when asked, and a research run is a batch process that should not need a live API to know what
/// trading costs. A file on a shared volume is the same shape of contract research already
/// publishes in the other direction.
///
/// Writes are atomic — temp file then move — because a research run reading a half-written dataset
/// would get a parse error at best and a truncated cost at worst.
/// </summary>
public sealed class RealisedCostPublisherService(
    IRealisedCostSource costs,
    ILogger<RealisedCostPublisherService> logger) : BackgroundService
{
    /// <summary>
    /// Slow on purpose. The dataset changes only when a round trip completes, and a research run
    /// reads it once at startup, so there is nothing to gain from republishing more often.
    /// </summary>
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _dataRoot =
        Environment.GetEnvironmentVariable("QUANTDESK_RESEARCH_DATA_ROOT") ?? "/app/research-data";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Publish();
            await Task.Delay(PublishInterval, stoppingToken);
        }
    }

    private void Publish()
    {
        try
        {
            RealisedCostContract? contract = costs.Current();

            // Nothing is written when too few round trips have completed. That matters: the research
            // reader treats a missing file as "no measurement" and refuses to run rather than
            // assuming a cost, and writing an empty or zeroed dataset would turn that honest refusal
            // into a silent zero.
            if (contract is null) return;

            Directory.CreateDirectory(_dataRoot);
            string destination = Path.Combine(_dataRoot, "realised-costs.json");
            string temporary = destination + ".tmp";

            File.WriteAllText(temporary, JsonSerializer.Serialize(contract, Wire));
            File.Move(temporary, destination, overwrite: true);

            logger.LogDebug(
                "Published realised costs from {Count} round trips to {Path}.",
                contract.ObservationCount, destination);
        }
        catch (Exception exception) when (HostedServiceFaults.IsFault(exception, CancellationToken.None))
        {
            // A failed publish must not stop the host. Research keeps reading the previous dataset,
            // which is stale rather than wrong, and the next tick tries again.
            logger.LogWarning(exception, "Could not publish the realised-cost dataset.");
        }
    }
}
