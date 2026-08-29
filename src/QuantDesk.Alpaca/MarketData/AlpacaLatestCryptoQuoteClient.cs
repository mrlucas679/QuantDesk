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
        if (quote is null || !TryReadDecimal(quote.AskPrice, out decimal ask) || ask <= 0)
            throw new InvalidOperationException("Alpaca latest crypto quote did not contain a valid ask price.");
        return ask;
    }

    private static string Normalize(string symbol) => symbol.Replace("/", string.Empty, StringComparison.Ordinal);

    private static bool TryReadDecimal(JsonElement element, out decimal value) =>
        element.ValueKind == JsonValueKind.Number
            ? element.TryGetDecimal(out value)
            : decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private sealed record CryptoQuoteResponse(
        [property: JsonPropertyName("quotes")] IReadOnlyDictionary<string, CryptoQuote> Quotes);

    private sealed record CryptoQuote(
        [property: JsonPropertyName("ap")] JsonElement AskPrice);
}
