using QuantDesk.Runtime.Persistence;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using QuantDesk.Alpaca.MarketData;

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

/// <summary>Exports immutable, hashed Alpaca bars for the asynchronous Python research plane.</summary>
public sealed class HistoricalCryptoDatasetService(
    AlpacaLatestCryptoQuoteClient client,
    AutonomousPaperTradingOptions trading,
    ILogger<HistoricalCryptoDatasetService> logger) : BackgroundService
{
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

    private async Task RefreshAsync(CancellationToken stoppingToken)
    {
        string outputRoot = Environment.GetEnvironmentVariable("QUANTDESK_RESEARCH_DATA_ROOT")
            ?? "/app/research-data";
        int lookbackDays = ReadPositiveInt("QUANTDESK_RESEARCH_LOOKBACK_DAYS", 180);
        DateTimeOffset end = DateTimeOffset.UtcNow.AddMinutes(-1);
        try
        {
            await PublishDatasetAsync(outputRoot, trading.Symbol, end.AddDays(-lookbackDays), end, "5Min", "latest-manifest.json", stoppingToken);
            await PublishDatasetAsync(outputRoot, "ETH/USD", end.AddDays(-lookbackDays), end, "5Min", "latest-eth-manifest.json", stoppingToken);
            await PublishDatasetAsync(outputRoot, trading.Symbol, end.AddDays(-3_000), end, "1Day", "latest-daily-manifest.json", stoppingToken);
            await PublishIndependentValidationDatasetAsync(outputRoot, stoppingToken);
            await PublishFinalValidationDatasetAsync(outputRoot, stoppingToken);
            await PublishEthTransferValidationDatasetAsync(outputRoot, stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
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
        if (ManifestCoversWindow(manifestPath, trading.Symbol, FinalValidationStart, FinalValidationEnd))
            return;
        await PublishDatasetAsync(
            outputRoot,
            trading.Symbol,
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
            trading.Symbol,
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
            bars.Count, $"sha256:{hash}", DateTimeOffset.UtcNow, dataFile);
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
