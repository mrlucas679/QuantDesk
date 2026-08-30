using System.Diagnostics;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Domain.Market;
using QuantDesk.Runtime.Ingestion;
using QuantDesk.Runtime.State;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Owns the live crypto market stream and its single-writer state consumer.</summary>
public sealed class MarketDataRuntimeService(
    AlpacaMarketDataStream stream,
    AlpacaLatestCryptoQuoteClient quoteClient,
    BoundedEventChannel<NormalizedMarketEvent> channel,
    MicrostructureEvidenceBuffer microstructureEvidence,
    MarketStateOwner stateOwner,
    FullSystemReadinessState readiness,
    PaperTradingOptions options,
    ILogger<MarketDataRuntimeService> logger) : BackgroundService
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);
    private long _lastEventTicks;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string[] cryptoSymbols = options.Symbols.Values
            .Where(symbol => symbol.Contains('/', StringComparison.Ordinal))
            .ToArray();
        if (cryptoSymbols.Length == 0)
        {
            logger.LogWarning("No crypto symbols are configured for the Alpaca market-data stream.");
            return;
        }

        stream.ConnectivityChanged += OnConnectivityChanged;
        Task stateOwnerTask = stateOwner.RunAsync(stoppingToken);
        Task watchdogTask = WatchdogAsync(cryptoSymbols[0], options.Symbols.First(item =>
            string.Equals(item.Value, cryptoSymbols[0], StringComparison.Ordinal)).Key, stoppingToken);
        try
        {
            await foreach (NormalizedMarketEvent marketEvent in stream.ReadAsync(cryptoSymbols, stoppingToken))
            {
                if (!channel.TryPublish(marketEvent, Stopwatch.GetTimestamp()))
                {
                    readiness.RecordStreams(false, readiness.Snapshot().TradeUpdatesHealthy);
                    logger.LogError("Market-data channel reached capacity; readiness failed closed.");
                    continue;
                }
                if (!microstructureEvidence.TryPublish(marketEvent, Stopwatch.GetTimestamp()))
                {
                    logger.LogWarning("Microstructure evidence buffer reached capacity; affected windows are marked unusable.");
                }
                Interlocked.Exchange(ref _lastEventTicks, Stopwatch.GetTimestamp());
                readiness.RecordStreams(true, readiness.Snapshot().TradeUpdatesHealthy);
            }
        }
        finally
        {
            stream.ConnectivityChanged -= OnConnectivityChanged;
            readiness.RecordStreams(false, readiness.Snapshot().TradeUpdatesHealthy);
            await Task.WhenAll(stateOwnerTask, watchdogTask);
        }
    }

    private void OnConnectivityChanged(bool connected)
    {
        if (!connected)
        {
            readiness.RecordStreams(false, readiness.Snapshot().TradeUpdatesHealthy);
            microstructureEvidence.MarkGap("stream_disconnected", Stopwatch.GetTimestamp());
        }
        logger.LogInformation("Alpaca crypto market-data subscription {State}.", connected ? "connected" : "disconnected");
    }

    private async Task WatchdogAsync(string symbol, int instrumentSlot, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            long lastEvent = Interlocked.Read(ref _lastEventTicks);
            bool healthy = lastEvent != 0 && Stopwatch.GetElapsedTime(lastEvent) <= StaleAfter;
            if (!healthy)
                healthy = await TryPublishLatestQuoteAsync(symbol, instrumentSlot, cancellationToken);
            readiness.RecordStreams(healthy, readiness.Snapshot().TradeUpdatesHealthy);
        }
    }

    private async Task<bool> TryPublishLatestQuoteAsync(
        string symbol, int instrumentSlot, CancellationToken cancellationToken)
    {
        try
        {
            CryptoQuoteSnapshot quote = await quoteClient.GetLatestQuoteAsync(symbol, cancellationToken);
            long nowTicks = Stopwatch.GetTimestamp();
            long nowNanoseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            var marketEvent = NormalizedMarketEvent.FromQuote(new QuoteEvent(
                nowNanoseconds, instrumentSlot, (double)quote.Bid, (double)quote.Ask,
                0, 0, nowNanoseconds, nowTicks, nowNanoseconds));
            if (!channel.TryPublish(marketEvent, nowTicks))
            {
                logger.LogError("Market-data channel reached capacity during quote fallback; readiness failed closed.");
                return false;
            }
            Interlocked.Exchange(ref _lastEventTicks, nowTicks);
            logger.LogInformation("Refreshed stale crypto stream with an authenticated latest quote for {Symbol}.", symbol);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Latest crypto quote fallback failed closed for {Symbol}.", symbol);
            return false;
        }
    }
}
