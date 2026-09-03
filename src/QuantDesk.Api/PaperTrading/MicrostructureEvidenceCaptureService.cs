using System.Text.Json;
using QuantDesk.Domain.Market;
using QuantDesk.Runtime.Ingestion;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Drains bounded raw order-book evidence to the shared research volume. This service has no
/// execution authority; any stream or buffer loss is durably recorded for future validation.
/// </summary>
public sealed class MicrostructureEvidenceCaptureService(
    MicrostructureEvidenceBuffer buffer,
    PaperTradingOptions options,
    ILogger<MicrostructureEvidenceCaptureService> logger,
    IRuntimeClock clock) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = QuantDesk.Domain.Serialization.ContractJson.Web;
    private long _recordedGapCount;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PersistNewGapsAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            OrderBookEvent orderBook = await buffer.ReadAsync(stoppingToken);
            await PersistNewGapsAsync(stoppingToken);
            await AppendOrderBookAsync(orderBook, ResolveSymbol(orderBook.InstrumentSlot), stoppingToken);
        }
    }

    private string ResolveSymbol(int instrumentSlot) => options.Symbols.TryGetValue(instrumentSlot, out string? symbol)
        ? symbol
        : throw new InvalidOperationException($"Unknown instrument slot {instrumentSlot} in microstructure evidence.");

    private async Task PersistNewGapsAsync(CancellationToken cancellationToken)
    {
        MicrostructureCaptureSnapshot snapshot = buffer.Snapshot();
        if (snapshot.GapCount <= _recordedGapCount)
            return;

        var gap = new MicrostructureEvidenceGapRecord(
            clock.UtcNow,
            snapshot.GapCount,
            snapshot.LastGapReason ?? "unknown",
            snapshot.LastGapMonotonicTicks);
        await AppendAsync("microstructure-gaps", "capture-gaps", gap, clock.UtcNow, cancellationToken);
        _recordedGapCount = snapshot.GapCount;
        logger.LogWarning("Recorded microstructure evidence gap {GapCount}: {ReasonCode}.", gap.GapCount, gap.ReasonCode);
    }

    private Task AppendOrderBookAsync(OrderBookEvent orderBook, string symbol, CancellationToken cancellationToken)
    {
        var record = new OrderBookEvidenceRecord(
            clock.UtcNow,
            symbol,
            orderBook.EventUnixNanoseconds,
            orderBook.SourceSequence,
            orderBook.BestBid,
            orderBook.BestAsk,
            orderBook.BidDepth,
            orderBook.AskDepth);
        string safeSymbol = new string(symbol.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        return AppendAsync("orderbook-events", safeSymbol, record, clock.UtcNow, cancellationToken);
    }

    private static async Task AppendAsync(
        string directoryName,
        string filePrefix,
        object record,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        string root = Environment.GetEnvironmentVariable("QUANTDESK_RESEARCH_DATA_ROOT") ?? "/app/research-data";
        string directory = Path.Combine(root, directoryName);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{filePrefix}-{capturedAt:yyyyMMdd}.jsonl");
        await File.AppendAllTextAsync(path, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine, cancellationToken);
    }
}

/// <summary>Raw point-in-time aggregate order-book state, captured independently of execution.</summary>
public sealed record OrderBookEvidenceRecord(
    DateTimeOffset CapturedAt,
    string Symbol,
    long EventUnixNanoseconds,
    long SourceSequence,
    double BestBid,
    double BestAsk,
    double BidDepth,
    double AskDepth);

/// <summary>Non-recoverable evidence discontinuity which invalidates affected microstructure windows.</summary>
public sealed record MicrostructureEvidenceGapRecord(
    DateTimeOffset CapturedAt,
    long GapCount,
    string ReasonCode,
    long LastGapMonotonicTicks);
