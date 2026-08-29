using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Configuration;

namespace QuantDesk.Alpaca.MarketData;

public sealed class AlpacaLatestCryptoQuoteClient(HttpClient httpClient, AlpacaOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<decimal> GetAskAsync(string symbol, CancellationToken cancellationToken)
    {
        (decimal _, decimal ask) = await GetQuoteAsync(symbol, cancellationToken);
        return ask;
    }

    public async Task<CryptoMarketEvidence> GetEvidenceAsync(
        string symbol, CancellationToken cancellationToken)
    {
        (decimal bid, decimal ask) = await GetQuoteAsync(symbol, cancellationToken);
        IReadOnlyList<decimal> closes = await GetRecentClosesAsync(symbol, cancellationToken);
        return new CryptoMarketEvidence(bid, ask, closes);
    }

    private async Task<(decimal Bid, decimal Ask)> GetQuoteAsync(
        string symbol, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        string requestUri =
            $"https://data.alpaca.markets/v1beta3/crypto/us/latest/quotes?symbols={Uri.EscapeDataString(symbol)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("APCA-API-KEY-ID", options.KeyId);
        request.Headers.Add("APCA-API-SECRET-KEY", options.SecretKey);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        CryptoQuoteResponse? payload = await response.Content.ReadFromJsonAsync<CryptoQuoteResponse>(
            JsonOptions, cancellationToken);
        CryptoQuote? quote = payload?.Quotes.FirstOrDefault(pair =>
            string.Equals(Normalize(pair.Key), Normalize(symbol), StringComparison.OrdinalIgnoreCase)).Value;
        if (quote is null || !TryReadDecimal(quote.AskPrice, out decimal ask) ||
            !TryReadDecimal(quote.BidPrice, out decimal bid) || bid <= 0 || ask < bid)
            throw new InvalidOperationException("Alpaca latest crypto quote did not contain a valid spread.");

        return (bid, ask);
    }

    private async Task<IReadOnlyList<decimal>> GetRecentClosesAsync(
        string symbol, CancellationToken cancellationToken)
    {
        string start = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-2).ToString("O"));
        string requestUri = "https://data.alpaca.markets/v1beta3/crypto/us/bars" +
            $"?symbols={Uri.EscapeDataString(symbol)}&timeframe=5Min&start={start}&limit=30&sort=asc";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("APCA-API-KEY-ID", options.KeyId);
        request.Headers.Add("APCA-API-SECRET-KEY", options.SecretKey);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        CryptoBarsResponse? payload = await response.Content.ReadFromJsonAsync<CryptoBarsResponse>(
            JsonOptions, cancellationToken);
        IReadOnlyList<CryptoBar>? bars = payload?.Bars.FirstOrDefault(pair =>
            string.Equals(Normalize(pair.Key), Normalize(symbol), StringComparison.OrdinalIgnoreCase)).Value;
        return bars?.Select(bar => TryReadDecimal(bar.Close, out decimal close) ? close : 0)
            .Where(close => close > 0)
            .ToArray() ?? [];
    }

    private static string Normalize(string symbol) => symbol.Replace("/", string.Empty, StringComparison.Ordinal);

    private static bool TryReadDecimal(JsonElement element, out decimal value) =>
        element.ValueKind == JsonValueKind.Number
            ? element.TryGetDecimal(out value)
            : decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private sealed record CryptoQuoteResponse(
        [property: JsonPropertyName("quotes")] IReadOnlyDictionary<string, CryptoQuote> Quotes);

    private sealed record CryptoQuote(
        [property: JsonPropertyName("ap")] JsonElement AskPrice,
        [property: JsonPropertyName("bp")] JsonElement BidPrice);

    private sealed record CryptoBarsResponse(
        [property: JsonPropertyName("bars")] IReadOnlyDictionary<string, IReadOnlyList<CryptoBar>> Bars);

    private sealed record CryptoBar([property: JsonPropertyName("c")] JsonElement Close);
}

public sealed record CryptoMarketEvidence(decimal Bid, decimal Ask, IReadOnlyList<decimal> Closes);
