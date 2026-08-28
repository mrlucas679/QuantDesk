using QuantDesk.Domain.Market;
using QuantDesk.Runtime.Ingestion;

namespace QuantDesk.Runtime.State;

public sealed class MarketStateOwner(
    MarketStateStore stateStore,
    BoundedEventChannel<NormalizedMarketEvent> input)
{
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NormalizedMarketEvent marketEvent = await input.ReadAsync(stoppingToken);
            Apply(marketEvent);
        }
    }

    public ValidationResult Apply(in NormalizedMarketEvent marketEvent) => marketEvent.Kind switch
    {
        MarketEventKind.Quote => stateStore.Apply(marketEvent.Quote),
        MarketEventKind.Trade => stateStore.Apply(marketEvent.Trade),
        MarketEventKind.OrderBook => stateStore.Apply(marketEvent.OrderBook),
        _ => throw new ArgumentOutOfRangeException(nameof(marketEvent), "Unsupported normalized market event kind.")
    };

}
