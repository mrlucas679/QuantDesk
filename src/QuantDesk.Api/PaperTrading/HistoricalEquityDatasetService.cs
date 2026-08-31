using System.Security.Cryptography;
using System.Text.Json;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Publishes immutable equity bars so research stays independent from execution credentials.
///
/// This service was hardcoded to SPY. That was a direct cause of the research universe being stuck
/// at four correlated ETFs: the application could only ever publish one equity dataset, so QQQ,
/// IWM, and DIA had to be side-loaded by hand and the cross-sectional strategies had almost no
/// dispersion to trade. The universe is now configuration, because widening it is the
/// highest-value research change available.
/// </summary>
public sealed class HistoricalEquityDatasetService(
    AlpacaHistoricalStockBarClient client,
    ILogger<HistoricalEquityDatasetService> logger) : BackgroundService
{
    private static readonly string[] DefaultSymbols = ["SPY", "QQQ", "IWM", "DIA"];
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

    /// <summary>Parses the configured research universe, falling back to the four index ETFs.</summary>
    public static IReadOnlyList<string> ResolveUniverse(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return DefaultSymbols;
        string[] symbols = configured
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(symbol => symbol.ToUpperInvariant())
            .Where(symbol => symbol.Length is >= 1 and <= 5 && symbol.All(char.IsAsciiLetterUpper))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return symbols.Length is 0 ? DefaultSymbols : symbols;
    }

    private async Task RefreshAsync(CancellationToken stoppingToken)
    {
        string root = Environment.GetEnvironmentVariable("QUANTDESK_RESEARCH_DATA_ROOT") ?? "/app/research-data";
        int lookbackDays = int.TryParse(
            Environment.GetEnvironmentVariable("QUANTDESK_EQUITY_LOOKBACK_DAYS"), out int configured) && configured > 0
            ? configured
            : 730;
        IReadOnlyList<string> universe = ResolveUniverse(
            Environment.GetEnvironmentVariable("QUANTDESK_EQUITY_RESEARCH_SYMBOLS"));
        // The research plane requires SIP consolidated bars and rejects anything else. IEX remains
        // the default because SIP needs a paid subscription; an operator with one sets this.
        string feed = Environment.GetEnvironmentVariable("QUANTDESK_EQUITY_RESEARCH_FEED")?.Trim()
            .ToLowerInvariant() is "sip" ? "sip" : "iex";

        DateTimeOffset intradayEnd = DateTimeOffset.UtcNow.AddMinutes(-20);
        DateTimeOffset intradayStart = intradayEnd.AddDays(-lookbackDays);
        foreach (string symbol in universe)
        {
            try
            {
                await PublishAsync(root, symbol, "5Min", intradayStart, intradayEnd, 1_000, feed, stoppingToken);
                await PublishAsync(
                    root, symbol, "1Day", intradayEnd.AddDays(-3_650), intradayEnd, 500, feed, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One symbol failing must not abandon the rest of the universe; a partial universe
                // is still usable research input, and the gap is visible in the published set.
                logger.LogError(
                    exception, "{Symbol} research dataset publication failed closed.", symbol);
            }
        }
    }

    private async Task PublishAsync(
        string root, string symbol, string timeframe, DateTimeOffset start, DateTimeOffset end,
        int minimumRows, string feed, CancellationToken cancellationToken)
    {
        IReadOnlyList<HistoricalStockBar> bars = await client.GetBarsAsync(
            symbol, start, end, timeframe, cancellationToken, feed, adjustment: "all");
        if (bars.Count < minimumRows)
        {
            logger.LogWarning(
                "{Symbol} {Timeframe} research dataset contained only {RowCount} rows and was not published.",
                symbol, timeframe, bars.Count);
            return;
        }

        Directory.CreateDirectory(root);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(bars, JsonOptions);
        string hash = Convert.ToHexStringLower(SHA256.HashData(data));
        string slug = symbol.ToLowerInvariant();
        string datasetId = $"{slug}-{timeframe.ToLowerInvariant()}-{feed}-{hash[..16]}";
        string dataFile = $"{datasetId}.json";
        await AtomicFile.WriteAllBytesAsync(Path.Combine(root, dataFile), data, cancellationToken);
        var manifest = new HistoricalDatasetManifest(
            datasetId, symbol, timeframe, bars[0].Timestamp, bars[^1].Timestamp,
            bars.Count, $"sha256:{hash}", DateTimeOffset.UtcNow, dataFile, feed, "all");
        await AtomicFile.WriteAllBytesAsync(
            Path.Combine(root, LatestManifestName(slug, timeframe, feed)),
            JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions), cancellationToken);
        logger.LogInformation(
            "Published {Symbol} {Timeframe} research dataset {DatasetId} with {RowCount} bars.",
            symbol, timeframe, datasetId, bars.Count);
    }

    /// <summary>
    /// Per-symbol, per-feed manifest name, matching exactly what the Python research plane reads
    /// (<c>latest-spy-1day-sip.manifest.json</c>).
    ///
    /// The previous names were <c>latest-spy-manifest.json</c> and
    /// <c>latest-spy-daily-manifest.json</c> — one symbol only, no feed, and a shape the research
    /// loader could not open. This service was publishing datasets nothing could read.
    /// </summary>
    public static string LatestManifestName(string slug, string timeframe, string feed) =>
        $"latest-{slug}-{timeframe.ToLowerInvariant()}-{feed}.manifest.json";
}
