using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Configuration;

namespace QuantDesk.Alpaca.MarketData;

/// <summary>
/// Supplies the live two-sided quote and recent closes for one US equity, in the same shape the
/// autonomous decision pipeline already consumes for spot crypto.
///
/// This is the piece whose absence made the autonomous lane crypto-only. The pipeline's evidence
/// parameter was satisfied solely by the crypto client, so an equity opportunity had no way to
/// reach the committee, the compiler, or risk — regardless of what research said. Same contract,
/// different venue.
/// </summary>
public sealed class AlpacaLatestEquityQuoteClient(HttpClient httpClient, AlpacaOptions options)
{
    private const int RequiredCloses = 13;
    private static readonly JsonSerializerOptions JsonOptions = QuantDesk.Domain.Serialization.ContractJson.Web;

    /// <summary>Gets the current NBBO quote and the recent 5-minute closes for one symbol.</summary>
    public async Task<DirectionalMarketEvidence> GetEvidenceAsync(
        string symbol, CancellationToken cancellationToken)
    {
        string normalized = Validate(symbol);
        (decimal bid, decimal ask) = await GetQuoteAsync(normalized, cancellationToken);
        IReadOnlyList<decimal> closes = await GetRecentClosesAsync(normalized, cancellationToken);
        return new DirectionalMarketEvidence(bid, ask, closes);
    }

    /// <summary>Gets the current executable NBBO quote without fetching bar history.</summary>
    public async Task<CryptoQuoteSnapshot> GetLatestQuoteAsync(
        string symbol, CancellationToken cancellationToken)
    {
        (decimal bid, decimal ask) = await GetQuoteAsync(Validate(symbol), cancellationToken);
        return new CryptoQuoteSnapshot(bid, ask, 0m, 0m);
    }

    private async Task<(decimal Bid, decimal Ask)> GetQuoteAsync(
        string symbol, CancellationToken cancellationToken)
    {
        string requestUri =
            options.DataUri($"v2/stocks/quotes/latest?symbols={Uri.EscapeDataString(symbol)}&feed=iex");
        using var request = AuthenticatedRequest(requestUri);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await AlpacaMarketDataResponse.EnsureSuccessAsync(
            response, "v2/stocks/quotes/latest", cancellationToken);
        EquityQuoteResponse? payload = await response.Content.ReadFromJsonAsync<EquityQuoteResponse>(
            JsonOptions, cancellationToken);
        if (payload?.Quotes is null ||
            !payload.Quotes.TryGetValue(symbol, out EquityQuote? quote) || quote is null ||
            !TryReadDecimal(quote.BidPrice, out decimal bid) ||
            !TryReadDecimal(quote.AskPrice, out decimal ask) ||
            bid <= 0 || ask < bid)
        {
            throw new InvalidOperationException(
                $"Alpaca latest equity quote for '{symbol}' did not contain a valid two-sided spread.");
        }

        return (bid, ask);
    }

    private async Task<IReadOnlyList<decimal>> GetRecentClosesAsync(
        string symbol, CancellationToken cancellationToken)
    {
        // Reach back further than the required window so an illiquid open or a halt does not
        // silently shorten the series; the gate rejects a short series rather than guessing.
        string start = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-6).ToString("O"));
        string requestUri = options.DataUri("v2/stocks/bars") +
            $"?symbols={Uri.EscapeDataString(symbol)}&timeframe=5Min&start={start}" +
            "&limit=100&sort=asc&feed=iex&adjustment=all";
        using var request = AuthenticatedRequest(requestUri);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await AlpacaMarketDataResponse.EnsureSuccessAsync(response, "v2/stocks/bars", cancellationToken);
        EquityBarsResponse? payload = await response.Content.ReadFromJsonAsync<EquityBarsResponse>(
            JsonOptions, cancellationToken);
        if (payload?.Bars is null || !payload.Bars.TryGetValue(symbol, out IReadOnlyList<EquityBar>? bars))
            return [];

        decimal[] closes = bars
            .Select(bar => TryReadDecimal(bar.Close, out decimal close) ? close : 0m)
            .Where(close => close > 0m)
            .ToArray();
        return closes.Length <= RequiredCloses ? closes : closes[^RequiredCloses..];
    }

    private static string Validate(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        string normalized = symbol.Trim().ToUpperInvariant();
        return normalized.Length is >= 1 and <= 5 && normalized.All(char.IsAsciiLetterUpper)
            ? normalized
            : throw new ArgumentException($"'{symbol}' is not a US equity symbol.", nameof(symbol));
    }

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

    private sealed record EquityQuoteResponse(
        [property: JsonPropertyName("quotes")] IReadOnlyDictionary<string, EquityQuote>? Quotes);

    private sealed record EquityQuote(
        [property: JsonPropertyName("ap")] JsonElement AskPrice,
        [property: JsonPropertyName("bp")] JsonElement BidPrice);

    private sealed record EquityBarsResponse(
        [property: JsonPropertyName("bars")] IReadOnlyDictionary<string, IReadOnlyList<EquityBar>>? Bars);

    private sealed record EquityBar([property: JsonPropertyName("c")] JsonElement Close);
}
