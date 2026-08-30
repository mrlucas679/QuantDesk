using System.Security.Cryptography;
using System.Text.Json;
using QuantDesk.Alpaca.MarketData;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Publishes immutable SPY bars so equity research stays independent from execution credentials.</summary>
public sealed class HistoricalEquityDatasetService(
    AlpacaHistoricalStockBarClient client,
    ILogger<HistoricalEquityDatasetService> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

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
        string root = Environment.GetEnvironmentVariable("QUANTDESK_RESEARCH_DATA_ROOT") ?? "/app/research-data";
        int lookbackDays = int.TryParse(Environment.GetEnvironmentVariable("QUANTDESK_EQUITY_LOOKBACK_DAYS"), out int configured) && configured > 0
            ? configured
            : 730;
        DateTimeOffset intradayEnd = DateTimeOffset.UtcNow.AddMinutes(-20);
        DateTimeOffset intradayStart = intradayEnd.AddDays(-lookbackDays);
        try
        {
            await PublishAsync(root, "5Min", intradayStart, intradayEnd, "latest-spy-manifest.json", 1_000, stoppingToken);
            await PublishAsync(root, "1Day", intradayEnd.AddDays(-3_650), intradayEnd,
                "latest-spy-daily-manifest.json", 500, stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "SPY research dataset publication failed closed.");
        }
    }

    private async Task PublishAsync(
        string root, string timeframe, DateTimeOffset start, DateTimeOffset end,
        string latestManifestFile, int minimumRows, CancellationToken cancellationToken)
    {
        IReadOnlyList<HistoricalStockBar> bars = await client.GetBarsAsync("SPY", start, end, timeframe, cancellationToken);
        if (bars.Count < minimumRows)
        {
            logger.LogWarning("SPY {Timeframe} research dataset contained only {RowCount} rows and was not published.", timeframe, bars.Count);
            return;
        }

        Directory.CreateDirectory(root);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(bars, JsonOptions);
        string hash = Convert.ToHexStringLower(SHA256.HashData(data));
        string datasetId = $"spy-{timeframe.ToLowerInvariant()}-{hash[..16]}";
        string dataFile = $"{datasetId}.json";
        await WriteAsync(Path.Combine(root, dataFile), data, cancellationToken);
        var manifest = new HistoricalDatasetManifest(
            datasetId, "SPY", timeframe, bars[0].Timestamp, bars[^1].Timestamp,
            bars.Count, $"sha256:{hash}", DateTimeOffset.UtcNow, dataFile);
        await WriteAsync(Path.Combine(root, latestManifestFile),
            JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions), cancellationToken);
        logger.LogInformation("Published SPY {Timeframe} research dataset {DatasetId} with {RowCount} bars.",
            timeframe, datasetId, bars.Count);
    }

    private static async Task WriteAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporary, content, cancellationToken);
        File.Move(temporary, path, true);
    }
}
