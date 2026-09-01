using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Configuration;

namespace QuantDesk.Alpaca.MarketData;

public sealed class AlpacaLatestCryptoQuoteClient(HttpClient httpClient, AlpacaOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = QuantDesk.Domain.Serialization.ContractJson.Web;

    public async Task<decimal> GetAskAsync(string symbol, CancellationToken cancellationToken)
    {
        (decimal _, decimal ask, decimal _, decimal _) = await GetQuoteAsync(symbol, cancellationToken);
        return ask;
    }

    /// <summary>Gets the current executable two-sided crypto quote without fetching bar history.</summary>
    public async Task<CryptoQuoteSnapshot> GetLatestQuoteAsync(string symbol, CancellationToken cancellationToken)
    {
        (decimal bid, decimal ask, decimal bidSize, decimal askSize) = await GetQuoteAsync(symbol, cancellationToken);
        return new CryptoQuoteSnapshot(bid, ask, bidSize, askSize);
    }

    public async Task<DirectionalMarketEvidence> GetEvidenceAsync(
        string symbol, CancellationToken cancellationToken)
    {
        (decimal bid, decimal ask, decimal _, decimal _) = await GetQuoteAsync(symbol, cancellationToken);
        IReadOnlyList<decimal> closes = await GetRecentClosesAsync(symbol, cancellationToken);
        return new DirectionalMarketEvidence(bid, ask, closes);
    }

    public async Task<IReadOnlyList<HistoricalCryptoBar>> GetHistoricalBarsAsync(
        string symbol,
        DateTimeOffset start,
        DateTimeOffset end,
        string timeframe,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeframe);
        if (start >= end) throw new ArgumentException("Historical bar start must precede end.", nameof(start));

        List<HistoricalCryptoBar> result = [];
        string? pageToken = null;
        do
        {
            string requestUri = options.DataUri("v1beta3/crypto/us/bars") +
                $"?symbols={Uri.EscapeDataString(symbol)}&timeframe={Uri.EscapeDataString(timeframe)}" +
                $"&start={Uri.EscapeDataString(start.ToString("O"))}&end={Uri.EscapeDataString(end.ToString("O"))}" +
                "&limit=10000&sort=asc" +
                (pageToken is null ? string.Empty : $"&page_token={Uri.EscapeDataString(pageToken)}");
            using var request = AuthenticatedRequest(requestUri);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            await AlpacaMarketDataResponse.EnsureSuccessAsync(
                response, "v1beta3/crypto/us/bars", cancellationToken);
            HistoricalCryptoBarsResponse? payload =
                await response.Content.ReadFromJsonAsync<HistoricalCryptoBarsResponse>(JsonOptions, cancellationToken);
            IReadOnlyList<HistoricalCryptoBar>? bars = payload?.Bars.FirstOrDefault(pair =>
                string.Equals(Normalize(pair.Key), Normalize(symbol), StringComparison.OrdinalIgnoreCase)).Value;
            if (bars is not null) result.AddRange(bars);
            pageToken = string.IsNullOrWhiteSpace(payload?.NextPageToken) ? null : payload.NextPageToken;
        } while (pageToken is not null);

        return result
            .OrderBy(bar => bar.Timestamp)
            .DistinctBy(bar => bar.Timestamp)
            .ToArray();
    }

    private async Task<(decimal Bid, decimal Ask, decimal BidSize, decimal AskSize)> GetQuoteAsync(
        string symbol, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        string requestUri =
            options.DataUri($"v1beta3/crypto/us/latest/quotes?symbols={Uri.EscapeDataString(symbol)}");
        using var request = AuthenticatedRequest(requestUri);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await AlpacaMarketDataResponse.EnsureSuccessAsync(
            response, "v1beta3/crypto/us/latest/quotes", cancellationToken);
        CryptoQuoteResponse? payload = await response.Content.ReadFromJsonAsync<CryptoQuoteResponse>(
            JsonOptions, cancellationToken);
        CryptoQuote? quote = payload?.Quotes.FirstOrDefault(pair =>
            string.Equals(Normalize(pair.Key), Normalize(symbol), StringComparison.OrdinalIgnoreCase)).Value;
        if (quote is null || !TryReadDecimal(quote.AskPrice, out decimal ask) ||
            !TryReadDecimal(quote.BidPrice, out decimal bid) || bid <= 0 || ask < bid ||
            !TryReadNonNegativeDecimal(quote.BidSize, out decimal bidSize) ||
            !TryReadNonNegativeDecimal(quote.AskSize, out decimal askSize))
            throw new InvalidOperationException("Alpaca latest crypto quote did not contain a valid spread.");

        return (bid, ask, bidSize, askSize);
    }

    private async Task<IReadOnlyList<decimal>> GetRecentClosesAsync(
        string symbol, CancellationToken cancellationToken)
    {
        string start = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-2).ToString("O"));
        string requestUri = options.DataUri("v1beta3/crypto/us/bars") +
            $"?symbols={Uri.EscapeDataString(symbol)}&timeframe=5Min&start={start}&limit=30&sort=asc";
        using var request = AuthenticatedRequest(requestUri);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await AlpacaMarketDataResponse.EnsureSuccessAsync(
            response, "v1beta3/crypto/us/bars", cancellationToken);
        CryptoBarsResponse? payload = await response.Content.ReadFromJsonAsync<CryptoBarsResponse>(
            JsonOptions, cancellationToken);
        IReadOnlyList<CryptoBar>? bars = payload?.Bars.FirstOrDefault(pair =>
            string.Equals(Normalize(pair.Key), Normalize(symbol), StringComparison.OrdinalIgnoreCase)).Value;
        return bars?.Select(bar => TryReadDecimal(bar.Close, out decimal close) ? close : 0)
            .Where(close => close > 0)
            .ToArray() ?? [];
    }

    private static string Normalize(string symbol) => symbol.Replace("/", string.Empty, StringComparison.Ordinal);

    private HttpRequestMessage AuthenticatedRequest(string requestUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("APCA-API-KEY-ID", options.KeyId);
        request.Headers.Add("APCA-API-SECRET-KEY", options.SecretKey);
        return request;
    }

    /// <summary>
    /// Reads a value the venue may send as a JSON number or a string. Every other kind is refused
    /// up front: an absent property deserializes to a default <see cref="JsonElement"/> of kind
    /// <see cref="JsonValueKind.Undefined"/>, and <c>GetString</c> throws on that rather than
    /// returning null, so an unsent field would fail the read instead of being treated as missing.
    /// </summary>
    private static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(
                element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private static bool TryReadNonNegativeDecimal(JsonElement element, out decimal value)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            value = 0;
            return true;
        }
        return TryReadDecimal(element, out value) && value >= 0;
    }

    private sealed record CryptoQuoteResponse(
        [property: JsonPropertyName("quotes")] IReadOnlyDictionary<string, CryptoQuote> Quotes);

    private sealed record CryptoQuote(
        [property: JsonPropertyName("ap")] JsonElement AskPrice,
        [property: JsonPropertyName("bp")] JsonElement BidPrice,
        [property: JsonPropertyName("bs")] JsonElement BidSize,
        [property: JsonPropertyName("as")] JsonElement AskSize);

    private sealed record CryptoBarsResponse(
        [property: JsonPropertyName("bars")] IReadOnlyDictionary<string, IReadOnlyList<CryptoBar>> Bars);

    private sealed record HistoricalCryptoBarsResponse(
        [property: JsonPropertyName("bars")] IReadOnlyDictionary<string, IReadOnlyList<HistoricalCryptoBar>> Bars,
        [property: JsonPropertyName("next_page_token")] string? NextPageToken);

    private sealed record CryptoBar([property: JsonPropertyName("c")] JsonElement Close);
}

public sealed record CryptoQuoteSnapshot(decimal Bid, decimal Ask, decimal BidSize, decimal AskSize);

public sealed record HistoricalCryptoBar(
    [property: JsonPropertyName("t")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("o")] decimal Open,
    [property: JsonPropertyName("h")] decimal High,
    [property: JsonPropertyName("l")] decimal Low,
    [property: JsonPropertyName("c")] decimal Close,
    [property: JsonPropertyName("v")] decimal Volume,
    [property: JsonPropertyName("n")] long TradeCount,
    [property: JsonPropertyName("vw")] decimal Vwap);
