using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Domain.Options;
using QuantDesk.Domain.Runtime;

namespace QuantDesk.Alpaca.MarketData;

/// <summary>
/// Reads the live two-sided quote for a set of OCC option contracts.
///
/// This was the hard blocker on the options lane. Contracts could be discovered
/// (<see cref="AlpacaOptionContractClient"/>) but not priced, and a defined-risk vertical cannot
/// be compiled without a bid and an offer on both legs — the net debit *is* the maximum loss, so
/// it has to come from real quotes rather than an estimate.
///
/// A quote that is missing, crossed, one-sided, or stale is marked
/// <see cref="DataQuality.Stale"/> rather than dropped, so the compiler sees an unusable leg and
/// refuses the spread instead of silently pricing off whatever remains.
/// </summary>
public sealed class AlpacaLatestOptionQuoteClient(HttpClient httpClient, AlpacaOptions options)
{
    private const int MaximumSymbolsPerRequest = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Returns one snapshot per requested contract, keyed by the caller's instrument slot.
    /// </summary>
    /// <param name="slotsBySymbol">
    /// The exact OCC symbols to price, mapped to the instrument slot the runtime uses for each.
    /// </param>
    /// <param name="maximumQuoteAge">
    /// A quote older than this is downgraded to <see cref="DataQuality.Stale"/>. Option quotes go
    /// stale quickly outside regular hours, and a stale mark would misprice the debit.
    /// </param>
    public async Task<IReadOnlyDictionary<int, OptionQuoteSnapshot>> GetQuotesAsync(
        IReadOnlyDictionary<string, int> slotsBySymbol,
        DateTimeOffset asOf,
        TimeSpan maximumQuoteAge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slotsBySymbol);
        if (slotsBySymbol.Count is 0 or > MaximumSymbolsPerRequest)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slotsBySymbol), $"One to {MaximumSymbolsPerRequest} option symbols are required.");
        }

        Dictionary<string, int> normalized = new(StringComparer.Ordinal);
        foreach ((string symbol, int slot) in slotsBySymbol)
        {
            if (!OccOptionSymbol.TryParse(symbol, out OccOptionSymbol? parsed) || parsed is null)
                throw new ArgumentException($"'{symbol}' is not a valid OCC option symbol.", nameof(slotsBySymbol));
            normalized[parsed.BrokerSymbol] = slot;
        }

        string requestUri = options.DataUri("v1beta1/options/quotes/latest") +
            $"?symbols={Uri.EscapeDataString(string.Join(',', normalized.Keys))}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("APCA-API-KEY-ID", options.KeyId);
        request.Headers.Add("APCA-API-SECRET-KEY", options.SecretKey);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        OptionQuotesResponse? payload = await response.Content.ReadFromJsonAsync<OptionQuotesResponse>(
            JsonOptions, cancellationToken);
        if (payload is null) throw new InvalidDataException("Alpaca option-quote response was empty.");

        var snapshots = new Dictionary<int, OptionQuoteSnapshot>();
        foreach ((string symbol, int slot) in normalized)
        {
            OptionQuoteWire? wire = null;
            if (payload.Quotes?.TryGetValue(symbol, out OptionQuoteWire? found) == true) wire = found;
            snapshots[slot] = ToSnapshot(slot, wire, asOf, maximumQuoteAge);
        }

        foreach (string returned in payload.Quotes?.Keys ?? [])
        {
            if (!normalized.ContainsKey(returned))
                throw new InvalidDataException($"Alpaca returned an unrequested option symbol '{returned}'.");
        }

        return snapshots;
    }

    /// <summary>Converts one wire quote into a snapshot, marking anything unusable as stale.</summary>
    private static OptionQuoteSnapshot ToSnapshot(
        int slot, OptionQuoteWire? wire, DateTimeOffset asOf, TimeSpan maximumQuoteAge)
    {
        if (wire is null ||
            !TryReadDouble(wire.BidPrice, out double bid) ||
            !TryReadDouble(wire.AskPrice, out double ask) ||
            !DateTimeOffset.TryParse(
                wire.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestamp))
            return Unusable(slot);

        // A zero or negative offer, a crossed book, or a one-sided market cannot price a spread.
        bool usable = ask > 0 && bid >= 0 && bid <= ask;
        TimeSpan age = asOf - timestamp;
        bool fresh = age >= TimeSpan.Zero && age <= maximumQuoteAge;
        double mid = usable ? (bid + ask) / 2d : 0d;
        double relativeSpread = usable && mid > 0 ? (ask - bid) / mid : double.PositiveInfinity;

        return new OptionQuoteSnapshot(
            slot, bid, ask, mid, relativeSpread,
            ImpliedVolatility: null, Delta: null, Gamma: null, Vega: null, Theta: null,
            EventNs: timestamp.ToUnixTimeMilliseconds() * 1_000_000L,
            Quality: usable && fresh ? DataQuality.Healthy : DataQuality.Stale);
    }

    private static OptionQuoteSnapshot Unusable(int slot) =>
        new(slot, 0d, 0d, 0d, double.PositiveInfinity, null, null, null, null, null, 0L, DataQuality.Stale);

    private static bool TryReadDouble(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetDouble(out value);
        return double.TryParse(
            element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private sealed record OptionQuotesResponse(
        [property: JsonPropertyName("quotes")] IReadOnlyDictionary<string, OptionQuoteWire>? Quotes);

    private sealed record OptionQuoteWire(
        [property: JsonPropertyName("bp")] JsonElement BidPrice,
        [property: JsonPropertyName("ap")] JsonElement AskPrice,
        [property: JsonPropertyName("t")] string? Timestamp);
}
