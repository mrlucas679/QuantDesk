using QuantDesk.Domain.Trading;
using System.Text.Json;
using QuantDesk.Alpaca.MarketData;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Appends authenticated, point-in-time crypto quotes to the research volume for future
/// spread and microstructure studies. It has no execution authority.
///
/// It captures the autonomous lane's symbol when that symbol is crypto, and only then. It used to
/// capture it unconditionally, which meant configuring the lane for an equity sent SPY to
/// <c>v1beta3/crypto/us/latest/quotes</c> and produced a 400 from the venue on every tick --
/// "invalid symbol: SPY does not match ^[A-Z]+x?/[A-Z]+$" -- roughly twelve times a minute,
/// forever. Nothing broke, which is what made it easy to miss: the failure was logged as a warning
/// and the lane carried on, so the only symptom was a research volume quietly missing the data it
/// was supposed to be accumulating.
/// </summary>
public sealed class CryptoQuoteCaptureService(
    AlpacaLatestCryptoQuoteClient quoteClient,
    AutonomousPaperTradingOptions trading,
    OpportunityRouter router,
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
        foreach (string symbol in CaptureSymbols())
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

    /// <summary>
    /// The crypto symbols worth capturing: the lane's own, when it is crypto, plus a fixed
    /// reference pair.
    ///
    /// Asked of the same router that decides where an order goes, so capture and execution cannot
    /// disagree about what an instrument is. A symbol that does not route to spot crypto is skipped
    /// rather than sent to a crypto endpoint that can only reject it.
    /// </summary>
    private IEnumerable<string> CaptureSymbols()
    {
        var symbols = new List<string> { "ETH/USD" };
        if (router.TryRoute(trading.Symbol, out OpportunityRoute? route, out _)
            && route?.AssetClass is TradedAssetClass.SpotCrypto)
        {
            symbols.Insert(0, trading.Symbol);
        }

        return symbols.Distinct(StringComparer.OrdinalIgnoreCase);
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
