using QuantDesk.Domain.Market;
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

    /// <summary>Bars requested, and bars retained for indicator warm-up.</summary>
    /// <summary>
    /// Bars requested per call, and bars kept.
    ///
    /// Sized for the time-of-day volume baseline, which needs five prior days at the same time of
    /// day and had none: the window was thirty hours and the retention 240 bars, so every
    /// time-of-day bucket held one observation and the feature was NaN for every bar in production
    /// while the sixty-day research scan computed it happily.
    ///
    /// A regular equity session is six and a half hours, so a week of five-minute bars is about
    /// 470 -- one request, and roughly 30 KB of doubles per symbol. Crypto is not widened to match,
    /// because there the feed reports no volume on 65.6% of bars and no amount of history fixes
    /// that; the coverage check refuses those series instead.
    /// </summary>
    private const int BarLimit = 1_200;
    private const int RetainedBars = 800;
    private static readonly JsonSerializerOptions JsonOptions = QuantDesk.Domain.Serialization.ContractJson.Web;

    /// <summary>Gets the current NBBO quote and the recent 5-minute closes for one symbol.</summary>
    public async Task<DirectionalMarketEvidence> GetEvidenceAsync(
        string symbol, CancellationToken cancellationToken)
    {
        string normalized = Validate(symbol);
        (decimal bid, decimal ask) = await GetQuoteAsync(normalized, cancellationToken);
        return await GetRecentBarsAsync(normalized, bid, ask, cancellationToken);
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

    /// <summary>
    /// The recent bar history, as full bars rather than closes alone.
    ///
    /// Reaches back further than the longest indicator window rather than the shortest usable one:
    /// a recursive indicator seeded on too little history produces a number that looks valid and is
    /// wrong for its first few dozen bars, which is worse than declining to produce one.
    /// </summary>
    private async Task<DirectionalMarketEvidence> GetRecentBarsAsync(
        string symbol, decimal bid, decimal ask, CancellationToken cancellationToken)
    {
        // Ninety days, because the window is counted in bars and the bar got six times longer.
        // A regular session holds thirteen thirty-minute bars, so the 288-bar indicator window needs
        // about twenty-two trading days -- nine calendar days would deliver roughly 117 bars and
        // every long-window indicator would read NaN forever, which looks exactly like a quiet
        // market.
        string start = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-90).ToString("O"));
        string requestUri = options.DataUri("v2/stocks/bars") +
            $"?symbols={Uri.EscapeDataString(symbol)}&timeframe={Timeframe}&start={start}" +
            $"&limit={BarLimit}&sort=asc&feed=iex&adjustment=all";
        using var request = AuthenticatedRequest(requestUri);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await AlpacaMarketDataResponse.EnsureSuccessAsync(response, "v2/stocks/bars", cancellationToken);
        EquityBarsResponse? payload = await response.Content.ReadFromJsonAsync<EquityBarsResponse>(
            JsonOptions, cancellationToken);
        if (payload?.Bars is null || !payload.Bars.TryGetValue(symbol, out IReadOnlyList<EquityBar>? bars))
            return new DirectionalMarketEvidence(bid, ask, []);

        List<decimal> closes = [], highs = [], lows = [], volumes = [];
        List<DateTimeOffset> timestamps = [];
        bool complete = true;
        foreach (EquityBar bar in bars)
        {
            // The close is what makes a bar usable at all; a bar without one is dropped. A bar
            // missing its high, low, or volume is kept for its close, and the extra series are
            // abandoned wholesale rather than left ragged -- windowed indicators read these by
            // index, so a series with holes in it is worse than no series. The consumer then sees
            // closes only, which is an honest description of what arrived.
            if (!TryReadDecimal(bar.Close, out decimal close) || close <= 0) continue;
            closes.Add(close);
            timestamps.Add(bar.Timestamp);

            if (!complete) continue;
            if (!TryReadDecimal(bar.High, out decimal high) || high <= 0 ||
                !TryReadDecimal(bar.Low, out decimal low) || low <= 0 ||
                !TryReadDecimal(bar.Volume, out decimal volume))
            {
                complete = false;
                continue;
            }

            highs.Add(high);
            lows.Add(low);
            volumes.Add(volume);
        }

        // Drop the bar that has not finished forming. See AlpacaLatestCryptoQuoteClient for the
        // full account: the venue returns the in-progress bar, everything here used it, and because
        // the lane re-evaluates every few seconds the same candle produced a different answer on
        // each pass -- so a rule could fire, stop firing and fire again inside one bar.
        int closed = ClosedBars.CompletedCount(timestamps, BarDuration, DateTimeOffset.UtcNow);
        if (closed < timestamps.Count)
        {
            timestamps.RemoveRange(closed, timestamps.Count - closed);
            closes.RemoveRange(closed, closes.Count - closed);
            if (highs.Count > closed) highs.RemoveRange(closed, highs.Count - closed);
            if (lows.Count > closed) lows.RemoveRange(closed, lows.Count - closed);
            if (volumes.Count > closed) volumes.RemoveRange(closed, volumes.Count - closed);
        }

        if (!complete)
        {
            highs.Clear();
            lows.Clear();
            volumes.Clear();
        }

        return new DirectionalMarketEvidence(bid, ask, Tail(closes))
        {
            Highs = Tail(highs),
            Lows = Tail(lows),
            Volumes = Tail(volumes),
            Timestamps = Tail(timestamps),
        };
    }

    /// <summary>
    /// The bar this client requests, and therefore the clock the equity lane thinks on.
    ///
    /// Thirty minutes, and the change is evidence-led rather than a preference. Every equity figure
    /// in the rule registry was measured on five-minute bars and every one is negative, so the
    /// equity book has been empty since it was written and the lane has never opened an equity
    /// position. Rescanned on thirty-minute bars, three families clear their cost at the 95% lower
    /// bound.
    ///
    /// It also fixes something nothing else could. Crypto was the only thing trading, crypto has no
    /// borrow at this venue, and so every position this system has ever taken has been long --
    /// 262 fills, not one of them a short. Equities can be shorted; giving them a clock on which
    /// they actually signal is what makes the short path reachable at all.
    /// </summary>
    private const string Timeframe = "30Min";

    static AlpacaLatestEquityQuoteClient() =>
        System.Diagnostics.Debug.Assert(
            Timeframe == $"{LaneBars.UsEquityMinutes}Min",
            "the equity client's timeframe and LaneBars must agree");

    /// <inheritdoc cref="Timeframe"/>
    private static readonly TimeSpan BarDuration = TimeSpan.FromMinutes(30);

    private static IReadOnlyList<T> Tail<T>(List<T> values) =>
        values.Count <= RetainedBars ? values : values[^RetainedBars..];

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

    private sealed record EquityBar(
        [property: JsonPropertyName("t")] DateTimeOffset Timestamp,
        [property: JsonPropertyName("c")] JsonElement Close,
        [property: JsonPropertyName("h")] JsonElement High,
        [property: JsonPropertyName("l")] JsonElement Low,
        [property: JsonPropertyName("v")] JsonElement Volume);
}
