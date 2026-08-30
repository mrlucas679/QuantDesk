using QuantDesk.Alpaca.Trading;
using QuantDesk.Domain.Execution;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Supervises the paper-account trade-update stream independently of REST polling.</summary>
public sealed class TradeUpdateRuntimeService(
    AlpacaTradeUpdateStream stream,
    FullSystemReadinessState readiness,
    ILogger<TradeUpdateRuntimeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stream.ConnectivityChanged += OnConnectivityChanged;
        try
        {
            await foreach (BrokerTradeUpdate update in stream.ReadAsync(stoppingToken))
            {
                logger.LogInformation(
                    "Received broker trade update {Kind} for client order {ClientOrderId}.",
                    update.Kind,
                    update.ClientOrderId);
            }
        }
        finally
        {
            stream.ConnectivityChanged -= OnConnectivityChanged;
            OnConnectivityChanged(false);
        }
    }

    private void OnConnectivityChanged(bool healthy) =>
        readiness.RecordStreams(readiness.Snapshot().MarketDataHealthy, healthy);
}
