using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Domain.Market;

namespace QuantDesk.Alpaca.MarketData;

/// <summary>
/// Reads the resting crypto order book, which is the only evidence this system has that is not
/// derived from price.
///
/// Every one of the thirteen entry rules reads the same OHLCV series, which is a structural reason
/// they correlate: measured on 2026-09-02 the seven traded pairs had a mean pairwise correlation of
/// 0.709, about 1.33 independent bets held as if they were seven. Depth is the one signal in the
/// handbook that looks at something else -- what is resting, rather than what has traded.
///
/// Bounded on purpose. The venue answers with everything it has, up to 85 levels a side, and this
/// keeps only what is needed to measure pressure near the touch.
/// </summary>
public sealed class AlpacaCryptoOrderBookClient(HttpClient httpClient, AlpacaOptions options)
{
    /// <summary>Levels retained per side. Far more than any band needs, and still bounded.</summary>
    private const int RetainedLevels = 100;

    private static readonly JsonSerializerOptions JsonOptions =
        QuantDesk.Domain.Serialization.ContractJson.Web;

    /// <summary>
    /// The current depth imbalance for one symbol, or an unmeasurable result when the venue
    /// returned nothing usable.
    ///
    /// Never throws for an absent or one-sided book. Depth is a candidate predictor, so a failure
    /// to read it must degrade the evidence rather than stop the lane -- exactly as a missing
    /// volume series does.
    /// </summary>
    public async Task<BookImbalance> GetImbalanceAsync(
        string symbol,
        CancellationToken cancellationToken,
        double bandBps = OrderBookImbalanceCalculator.DefaultBandBps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        string requestUri = options.DataUri("v1beta3/crypto/us/latest/orderbooks") +
            $"?symbols={Uri.EscapeDataString(symbol)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("APCA-API-KEY-ID", options.KeyId);
        request.Headers.TryAddWithoutValidation("APCA-API-SECRET-KEY", options.SecretKey);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return default;

        OrderBooksResponse? payload =
            await response.Content.ReadFromJsonAsync<OrderBooksResponse>(JsonOptions, cancellationToken);

        OrderBookPayload? book = payload?.OrderBooks
            .FirstOrDefault(pair => string.Equals(
                Normalize(pair.Key), Normalize(symbol), StringComparison.OrdinalIgnoreCase)).Value;
        if (book is null) return default;

        return OrderBookImbalanceCalculator.Calculate(
            Levels(book.Bids), Levels(book.Asks), bandBps);
    }

    private static IReadOnlyList<BookLevel> Levels(IReadOnlyList<BookLevelPayload>? levels)
    {
        if (levels is null || levels.Count == 0) return [];

        List<BookLevel> result = new(Math.Min(levels.Count, RetainedLevels));
        foreach (BookLevelPayload level in levels)
        {
            if (result.Count >= RetainedLevels) break;
            if (!TryReadDecimal(level.Price, out decimal price) || price <= 0m) continue;
            if (!TryReadDecimal(level.Size, out decimal size) || size <= 0m) continue;
            result.Add(new BookLevel(price, size));
        }

        return result;
    }

    /// <summary>Alpaca sends numbers as JSON numbers or strings depending on the endpoint.</summary>
    private static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetDecimal(out value);
            case JsonValueKind.String:
                return decimal.TryParse(
                    element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            default:
                value = 0m;
                return false;
        }
    }

    private static string Normalize(string symbol) => symbol.Replace("/", string.Empty);

    private sealed record OrderBooksResponse(
        [property: JsonPropertyName("orderbooks")]
        IReadOnlyDictionary<string, OrderBookPayload> OrderBooks);

    private sealed record OrderBookPayload(
        [property: JsonPropertyName("b")] IReadOnlyList<BookLevelPayload> Bids,
        [property: JsonPropertyName("a")] IReadOnlyList<BookLevelPayload> Asks);

    private sealed record BookLevelPayload(
        [property: JsonPropertyName("p")] JsonElement Price,
        [property: JsonPropertyName("s")] JsonElement Size);
}
