using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Configuration;

namespace QuantDesk.Alpaca.MarketData;

/// <summary>Retrieves paginated IEX stock bars for offline, point-in-time research only.</summary>
public sealed class AlpacaHistoricalStockBarClient(HttpClient httpClient, AlpacaOptions options)
{
    private const int MaximumPages = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = QuantDesk.Domain.Serialization.ContractJson.Web;

    /// <summary>
    /// Fetches adjusted bars for one symbol.
    /// </summary>
    /// <param name="feed">
    /// <c>iex</c> or <c>sip</c>. The research plane requires SIP consolidated bars, because IEX
    /// covers only a few percent of consolidated volume and its bars are not a faithful record of
    /// the session. SIP needs a paid data subscription, so the caller chooses and the manifest
    /// records which was used — a dataset must never be silently mistaken for the other feed.
    /// </param>
    /// <param name="adjustment">
    /// <c>raw</c>, <c>split</c>, <c>dividend</c>, or <c>all</c>. Research requires <c>all</c>;
    /// unadjusted history fabricates returns across splits and dividends.
    /// </param>
    public async Task<IReadOnlyList<HistoricalStockBar>> GetBarsAsync(
        string symbol, DateTimeOffset start, DateTimeOffset end, string timeframe,
        CancellationToken cancellationToken, string feed = "iex", string adjustment = "all")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (start >= end) throw new ArgumentException("Start must precede end.", nameof(start));
        if (feed is not ("iex" or "sip"))
            throw new ArgumentException("Feed must be iex or sip.", nameof(feed));
        if (adjustment is not ("raw" or "split" or "dividend" or "all"))
            throw new ArgumentException("Adjustment must be raw, split, dividend, or all.", nameof(adjustment));
        string requestedSymbol = symbol.Trim().ToUpperInvariant();

        List<HistoricalStockBar> result = [];
        var cursor = new AlpacaPageCursor(MaximumPages, "stock-bar");
        while (cursor.HasMorePages)
        {
            string requestUri = options.DataUri("v2/stocks/bars") +
                $"?symbols={Uri.EscapeDataString(requestedSymbol)}&timeframe={Uri.EscapeDataString(timeframe)}" +
                $"&start={Uri.EscapeDataString(start.ToString("O"))}&end={Uri.EscapeDataString(end.ToString("O"))}" +
                $"&feed={feed}&adjustment={adjustment}&limit=10000&sort=asc" +
                cursor.PageTokenQuery;
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("APCA-API-KEY-ID", options.KeyId);
            request.Headers.Add("APCA-API-SECRET-KEY", options.SecretKey);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            StockBarsResponse? payload = await response.Content.ReadFromJsonAsync<StockBarsResponse>(JsonOptions, cancellationToken);
            if (payload?.Bars is null)
                throw new InvalidOperationException("Alpaca stock-bars response omitted its bars payload.");
            string[] unexpected = payload.Bars.Keys
                .Where(returned => !string.Equals(returned, requestedSymbol, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (unexpected.Length != 0)
                throw new InvalidOperationException("Alpaca stock-bars response contained an unrequested symbol.");
            if (!payload.Bars.TryGetValue(requestedSymbol, out IReadOnlyList<HistoricalStockBar>? bars) || bars is null)
                throw new InvalidOperationException($"Alpaca stock-bars response omitted '{requestedSymbol}'.");
            result.AddRange(bars);
            cursor.Advance(payload.NextPageToken);
        }

        return result.OrderBy(bar => bar.Timestamp).DistinctBy(bar => bar.Timestamp).ToArray();
    }

    private sealed record StockBarsResponse(
        [property: JsonPropertyName("bars")] IReadOnlyDictionary<string, IReadOnlyList<HistoricalStockBar>> Bars,
        [property: JsonPropertyName("next_page_token")] string? NextPageToken);
}

public sealed record HistoricalStockBar(
    [property: JsonPropertyName("t")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("o")] decimal Open,
    [property: JsonPropertyName("h")] decimal High,
    [property: JsonPropertyName("l")] decimal Low,
    [property: JsonPropertyName("c")] decimal Close,
    [property: JsonPropertyName("v")] decimal Volume,
    [property: JsonPropertyName("n")] long TradeCount,
    [property: JsonPropertyName("vw")] decimal Vwap);
