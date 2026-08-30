using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Configuration;

namespace QuantDesk.Alpaca.MarketData;

/// <summary>Retrieves paginated IEX stock bars for offline, point-in-time research only.</summary>
public sealed class AlpacaHistoricalStockBarClient(HttpClient httpClient, AlpacaOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<HistoricalStockBar>> GetBarsAsync(
        string symbol, DateTimeOffset start, DateTimeOffset end, string timeframe,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (start >= end) throw new ArgumentException("Start must precede end.", nameof(start));

        List<HistoricalStockBar> result = [];
        string? pageToken = null;
        do
        {
            string requestUri = "https://data.alpaca.markets/v2/stocks/bars" +
                $"?symbols={Uri.EscapeDataString(symbol)}&timeframe={Uri.EscapeDataString(timeframe)}" +
                $"&start={Uri.EscapeDataString(start.ToString("O"))}&end={Uri.EscapeDataString(end.ToString("O"))}" +
                "&feed=iex&limit=10000&sort=asc" +
                (pageToken is null ? string.Empty : $"&page_token={Uri.EscapeDataString(pageToken)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("APCA-API-KEY-ID", options.KeyId);
            request.Headers.Add("APCA-API-SECRET-KEY", options.SecretKey);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            StockBarsResponse? payload = await response.Content.ReadFromJsonAsync<StockBarsResponse>(JsonOptions, cancellationToken);
            if (payload?.Bars.TryGetValue(symbol, out IReadOnlyList<HistoricalStockBar>? bars) == true)
                result.AddRange(bars);
            pageToken = string.IsNullOrWhiteSpace(payload?.NextPageToken) ? null : payload.NextPageToken;
        } while (pageToken is not null);

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
