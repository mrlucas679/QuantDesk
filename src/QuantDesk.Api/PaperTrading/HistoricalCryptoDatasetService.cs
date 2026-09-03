using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Persistence;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.PaperTrading;

public sealed record HistoricalDatasetManifest(
    string DatasetId,
    string Symbol,
    string Timeframe,
    DateTimeOffset Start,
    DateTimeOffset End,
    int RowCount,
    string Sha256,
    DateTimeOffset GeneratedAt,
    string DataFile,
    // Feed and adjustment are provenance, not decoration: the research plane refuses any dataset
    // that is not SIP/all, and without these fields a consumer cannot tell the feeds apart.
    string Feed = "iex",
    string Adjustment = "all");

/// <summary>
/// Exports immutable, hashed Alpaca bars for the asynchronous Python research plane.
///
/// Every endpoint this service calls is a crypto endpoint, so every symbol it publishes has to be a
/// crypto symbol. It used to take the autonomous lane's execution symbol directly, which conflated
/// two different things: what the system *trades* and what it *studies*. They coincided while the
/// lane traded BTC/USD and came apart the moment it was pointed at an equity -- SPY went to
/// <c>v1beta3/crypto/us/bars</c> and the venue rejected it, so the research plane's primary dataset
/// silently stopped refreshing while the lane itself ran on unaffected.
/// </summary>
public sealed class HistoricalCryptoDatasetService(
    AlpacaLatestCryptoQuoteClient client,
    AutonomousPaperTradingOptions trading,
    OpportunityRouter router,
    ILogger<HistoricalCryptoDatasetService> logger,
    IRuntimeClock clock) : BackgroundService
{
    /// <summary>
    /// The crypto instrument studied when the lane is not itself trading crypto.
    ///
    /// BTC/USD because it is the pair the committed research datasets and the failure ledger are
    /// built on, so a lane pointed elsewhere leaves that history continuous rather than truncated.
    /// </summary>
    private const string DefaultResearchSymbol = "BTC/USD";
    private static readonly DateTimeOffset IndependentValidationStart =
        DateTimeOffset.Parse("2022-01-01T00:00:00Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset IndependentValidationEnd =
        DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture);
    private const string IndependentValidationManifest = "independent-validation-manifest.json";
    private static readonly DateTimeOffset FinalValidationStart =
        DateTimeOffset.Parse("2022-01-01T00:00:00Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset FinalValidationEnd =
        DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture);
    private const string FinalValidationManifest = "final-validation-manifest.json";
    private const string EthTransferValidationManifest = "eth-transfer-validation-manifest.json";
    // The shortest production forecast is one hour (12 five-minute bars).  Refreshing
    // no slower than half that horizon prevents a valid forecast becoming stale while
    // the worker is waiting on an unchanged dataset.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = QuantDesk.Domain.Serialization.ContractJson.Indented;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(stoppingToken);
            await Task.Delay(RefreshInterval, stoppingToken);
        }
    }

    /// <summary>
    /// The crypto symbol to study: the lane's own when it trades crypto, BTC/USD otherwise.
    ///
    /// Asked of the same router that decides where an order goes, so the study symbol and the
    /// execution symbol cannot disagree about what an instrument is.
    /// </summary>
    private string ResearchSymbol() =>
        router.TryRoute(trading.Symbol, out OpportunityRoute? route, out _)
        && route?.AssetClass is TradedAssetClass.SpotCrypto
            ? trading.Symbol
            : DefaultResearchSymbol;

    private async Task RefreshAsync(CancellationToken stoppingToken)
    {
        string outputRoot = Environment.GetEnvironmentVariable("QUANTDESK_RESEARCH_DATA_ROOT")
            ?? "/app/research-data";
        int lookbackDays = ReadPositiveInt("QUANTDESK_RESEARCH_LOOKBACK_DAYS", 180);
        DateTimeOffset end = clock.UtcNow.AddMinutes(-1);
        try
        {
            string symbol = ResearchSymbol();
            await PublishDatasetAsync(outputRoot, symbol, end.AddDays(-lookbackDays), end, "5Min", "latest-manifest.json", stoppingToken);
            await PublishDatasetAsync(outputRoot, "ETH/USD", end.AddDays(-lookbackDays), end, "5Min", "latest-eth-manifest.json", stoppingToken);
            await PublishDatasetAsync(outputRoot, symbol, end.AddDays(-3_000), end, "1Day", "latest-daily-manifest.json", stoppingToken);
            await PublishIndependentValidationDatasetAsync(outputRoot, stoppingToken);
            await PublishFinalValidationDatasetAsync(outputRoot, stoppingToken);
            await PublishEthTransferValidationDatasetAsync(outputRoot, stoppingToken);
        }
        catch (Exception exception) when (HostedServiceFaults.IsFault(exception, stoppingToken))
        {
            logger.LogError(exception, "Historical research dataset publication failed closed.");
        }
    }

    private async Task PublishEthTransferValidationDatasetAsync(
        string outputRoot,
        CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(outputRoot, EthTransferValidationManifest);
        if (File.Exists(manifestPath)) return;
        await PublishDatasetAsync(
            outputRoot,
            "ETH/USD",
            DateTimeOffset.Parse("2021-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2024-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            "5Min",
            EthTransferValidationManifest,
            cancellationToken);
    }

    private async Task PublishFinalValidationDatasetAsync(
        string outputRoot,
        CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(outputRoot, FinalValidationManifest);
        if (ManifestCoversWindow(manifestPath, ResearchSymbol(), FinalValidationStart, FinalValidationEnd))
            return;
        await PublishDatasetAsync(
            outputRoot,
            ResearchSymbol(),
            FinalValidationStart,
            FinalValidationEnd,
            "5Min",
            FinalValidationManifest,
            cancellationToken);
    }

    private static bool ManifestCoversWindow(
        string manifestPath,
        string symbol,
        DateTimeOffset requestedStart,
        DateTimeOffset requestedEnd)
    {
        if (!File.Exists(manifestPath)) return false;
        try
        {
            HistoricalDatasetManifest? manifest = JsonSerializer.Deserialize<HistoricalDatasetManifest>(
                File.ReadAllBytes(manifestPath), JsonOptions);
            return manifest is not null &&
                string.Equals(manifest.Symbol, symbol, StringComparison.OrdinalIgnoreCase) &&
                manifest.Start >= requestedStart && manifest.Start < requestedStart.AddDays(1) &&
                manifest.End > requestedEnd.AddDays(-1) && manifest.End <= requestedEnd;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task PublishIndependentValidationDatasetAsync(
        string outputRoot,
        CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(outputRoot, IndependentValidationManifest);
        if (File.Exists(manifestPath))
            return;

        await PublishDatasetAsync(
            outputRoot,
            ResearchSymbol(),
            IndependentValidationStart,
            IndependentValidationEnd,
            "5Min",
            IndependentValidationManifest,
            cancellationToken);
    }

    private async Task PublishDatasetAsync(
        string outputRoot,
        string symbol,
        DateTimeOffset start,
        DateTimeOffset end,
        string timeframe,
        string latestManifestName,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<HistoricalCryptoBar> bars = await client.GetHistoricalBarsAsync(
            symbol, start, end, timeframe, cancellationToken);
        if (bars.Count < 1_000)
        {
            logger.LogWarning(
                "Historical {Timeframe} dataset contained only {RowCount} rows and was not published.",
                timeframe,
                bars.Count);
            return;
        }

        Directory.CreateDirectory(outputRoot);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(bars, JsonOptions);
        string hash = Convert.ToHexStringLower(SHA256.HashData(data));
        string datasetId = $"{Normalize(symbol)}-{timeframe.ToLowerInvariant()}-{hash[..16]}";
        string dataFile = $"{datasetId}.json";
        await AtomicFile.WriteAllBytesAsync(Path.Combine(outputRoot, dataFile), data, cancellationToken);
        var manifest = new HistoricalDatasetManifest(
            datasetId, symbol, timeframe, bars[0].Timestamp, bars[^1].Timestamp,
            bars.Count, $"sha256:{hash}", clock.UtcNow, dataFile);
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        await AtomicFile.WriteAllBytesAsync(Path.Combine(outputRoot, latestManifestName), manifestBytes, cancellationToken);
        logger.LogInformation(
            "Published point-in-time {Timeframe} research dataset {DatasetId} with {RowCount} chronological bars.",
            timeframe,
            datasetId,
            bars.Count);
    }

    private static int ReadPositiveInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0 ? value : fallback;

    private static string Normalize(string symbol) =>
        new(symbol.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
