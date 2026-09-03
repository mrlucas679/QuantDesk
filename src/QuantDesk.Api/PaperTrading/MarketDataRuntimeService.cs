using System.Diagnostics;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Domain.Market;
using QuantDesk.Runtime.Ingestion;
using QuantDesk.Runtime.State;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Owns the live crypto market stream and its single-writer state consumer.</summary>
public sealed class MarketDataRuntimeService(
    AlpacaMarketDataStream stream,
    AlpacaLatestCryptoQuoteClient quoteClient,
    BoundedEventChannel<NormalizedMarketEvent> channel,
    MicrostructureEvidenceBuffer microstructureEvidence,
    MarketStateOwner stateOwner,
    MarketStateStore marketState,
    StreamConnectionTracker connections,
    FullSystemReadinessState readiness,
    PaperTradingOptions options,
    ILogger<MarketDataRuntimeService> logger,
    IRuntimeClock clock,
    MarketDataSessionRecorder recorder) : BackgroundService
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
                // Recorded before anything is decided about it, including before the capacity
                // check below. A session that dropped an event because a channel was full still
                // saw that event, and a replay reconstructing the day needs to know it arrived --
                // otherwise the log describes a quieter market than the one that caused the drop.
                recorder.Record(marketEvent);

                if (!channel.TryPublish(marketEvent, clock.MonotonicTimestamp))
                {
                    readiness.RecordStreams(false, readiness.Snapshot().TradeUpdatesHealthy);
                    logger.LogError("Market-data channel reached capacity; readiness failed closed.");
                    continue;
                }
                if (!microstructureEvidence.TryPublish(marketEvent, clock.MonotonicTimestamp))
                {
                    logger.LogWarning("Microstructure evidence buffer reached capacity; affected windows are marked unusable.");
                }
                Interlocked.Exchange(ref _lastEventTicks, clock.MonotonicTimestamp);
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
        connections.Record(MarketDataStreamName, connected, clock.UtcNow);

        if (!connected)
        {
            readiness.RecordStreams(false, readiness.Snapshot().TradeUpdatesHealthy);
            microstructureEvidence.MarkGap("stream_disconnected", clock.MonotonicTimestamp);

            // The book on hand is not the venue's book any more, and this feed publishes no
            // sequence number that would reveal how far apart they have drifted. Marking the state
            // gapped stops anything trading on it until a fresh event arrives per instrument.
            // Without this the snapshots carried the pre-disconnect book across the reconnect
            // still reporting Healthy.
            marketState.MarkStreamInterrupted();
        }

        logger.LogInformation("Alpaca crypto market-data subscription {State}.", connected ? "connected" : "disconnected");
    }

    /// <summary>Names this stream in the connection tracker; the trade-update stream is separate.</summary>
    internal const string MarketDataStreamName = "alpaca-crypto-market-data";

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
            long nowTicks = clock.MonotonicTimestamp;
            long nowNanoseconds = clock.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
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
        catch (Exception exception) when (HostedServiceFaults.IsFault(exception, cancellationToken))
        {
            logger.LogWarning(exception, "Latest crypto quote fallback failed closed for {Symbol}.", symbol);
            return false;
        }
    }
}
