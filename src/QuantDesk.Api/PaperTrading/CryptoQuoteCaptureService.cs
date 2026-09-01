using System.Text.Json;
using QuantDesk.Alpaca.MarketData;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Appends authenticated, point-in-time crypto quotes to the research volume for future
/// spread and microstructure studies. It has no execution authority.
/// </summary>
public sealed class CryptoQuoteCaptureService(
    AlpacaLatestCryptoQuoteClient quoteClient,
    AutonomousPaperTradingOptions trading,
    ILogger<CryptoQuoteCaptureService> logger) : BackgroundService
{
    private static readonly TimeSpan MinimumCaptureInterval = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = QuantDesk.Domain.Serialization.ContractJson.Web;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ReadCaptureInterval());
        do
        {
            await CaptureConfiguredSymbolsAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CaptureConfiguredSymbolsAsync(CancellationToken cancellationToken)
    {
        foreach (string symbol in new[] { trading.Symbol, "ETH/USD" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                CryptoQuoteSnapshot quote = await quoteClient.GetLatestQuoteAsync(symbol, cancellationToken);
                await AppendSnapshotAsync(symbol, quote, cancellationToken);
            }
            catch (Exception exception) when (HostedServiceFaults.IsFault(exception, cancellationToken))
            {
                logger.LogWarning(exception, "Point-in-time quote capture failed for {Symbol}.", symbol);
            }
        }
    }

    private static TimeSpan ReadCaptureInterval()
    {
        const string name = "QUANTDESK_QUOTE_CAPTURE_INTERVAL_SECONDS";
        if (int.TryParse(Environment.GetEnvironmentVariable(name), out int seconds) && seconds >= 5)
            return TimeSpan.FromSeconds(seconds);
        return MinimumCaptureInterval;
    }

    private static async Task AppendSnapshotAsync(
        string symbol,
        CryptoQuoteSnapshot quote,
        CancellationToken cancellationToken)
    {
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        decimal midpoint = (quote.Bid + quote.Ask) / 2m;
        var snapshot = new CryptoQuoteSnapshotRecord(
            capturedAt,
            symbol,
            quote.Bid,
            quote.Ask,
            quote.BidSize,
            quote.AskSize,
            midpoint,
            (quote.Ask - quote.Bid) / midpoint * 10_000m);
        string root = Environment.GetEnvironmentVariable("QUANTDESK_RESEARCH_DATA_ROOT") ?? "/app/research-data";
        string directory = Path.Combine(root, "quote-snapshots");
        Directory.CreateDirectory(directory);
        string safeSymbol = new string(symbol.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        string path = Path.Combine(directory, $"{safeSymbol}-{capturedAt:yyyyMMdd}.jsonl");
        string jsonLine = JsonSerializer.Serialize(snapshot, JsonOptions) + Environment.NewLine;
        await File.AppendAllTextAsync(path, jsonLine, cancellationToken);
    }
}

/// <summary>Immutable, executable two-sided quote evidence captured by the C# runtime.</summary>
public sealed record CryptoQuoteSnapshotRecord(
    DateTimeOffset CapturedAt,
    string Symbol,
    decimal Bid,
    decimal Ask,
    decimal BidSize,
    decimal AskSize,
    decimal Midpoint,
    decimal SpreadBps);
