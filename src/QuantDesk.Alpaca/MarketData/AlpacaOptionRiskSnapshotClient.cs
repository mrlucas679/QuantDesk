using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Domain.Options;
using QuantDesk.Domain.Runtime;

namespace QuantDesk.Alpaca.MarketData;

/// <summary>
/// Reads authenticated Alpaca option snapshots with implied volatility and Greeks for risk admission.
///
/// The payload shape here is Alpaca's, not a convenient approximation of it: implied volatility is a
/// sibling of <c>greeks</c> named <c>impliedVolatility</c>, not a member of it named
/// <c>implied_volatility</c>. Reading it from inside <c>greeks</c> found nothing on every real
/// response, and the absent value then threw rather than degrading — a lane that had never been run
/// against the venue failing on its first live call.
/// </summary>
public sealed class AlpacaOptionRiskSnapshotClient(HttpClient httpClient, AlpacaOptions options)
{
    private const string Endpoint = "v1beta1/options/snapshots";
    private static readonly JsonSerializerOptions JsonOptions = QuantDesk.Domain.Serialization.ContractJson.Web;

    public async Task<IReadOnlyDictionary<int, OptionRiskSnapshot>> GetSnapshotsAsync(
        IReadOnlyDictionary<string, int> slotsBySymbol,
        DateTimeOffset asOf,
        TimeSpan maximumAge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slotsBySymbol);
        if (slotsBySymbol.Count is 0 or > 100) throw new ArgumentOutOfRangeException(nameof(slotsBySymbol));
        var normalized = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((string symbol, int slot) in slotsBySymbol)
        {
            if (!OccOptionSymbol.TryParse(symbol, out OccOptionSymbol? parsed) || parsed is null)
                throw new ArgumentException($"'{symbol}' is not an OCC option symbol.", nameof(slotsBySymbol));
            normalized[parsed.BrokerSymbol] = slot;
        }

        string uri = options.DataUri(Endpoint) +
            $"?symbols={Uri.EscapeDataString(string.Join(',', normalized.Keys))}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("APCA-API-KEY-ID", options.KeyId);
        request.Headers.Add("APCA-API-SECRET-KEY", options.SecretKey);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        OptionSnapshotsResponse payload = await AlpacaMarketDataResponse.ReadAsync<OptionSnapshotsResponse>(
            response, Endpoint, JsonOptions, cancellationToken);
        if (payload.Snapshots is null) throw new InvalidDataException("Alpaca option snapshot response omitted its snapshots payload.");
        foreach (string returned in payload.Snapshots.Keys)
            if (!normalized.ContainsKey(returned))
                throw new InvalidDataException($"Alpaca returned an unrequested option symbol '{returned}'.");

        return normalized.ToDictionary(
            item => item.Value,
            item => ToSnapshot(item.Value, payload.Snapshots.GetValueOrDefault(item.Key), asOf, maximumAge));
    }

    /// <summary>
    /// Maps one snapshot, degrading to <see cref="DataQuality.Stale"/> whenever any input needed to
    /// size option risk is missing, non-finite, or older than the caller allows. Greeks are only
    /// meaningful together, so a partial set is refused rather than filled with zeros that would
    /// read as a genuine flat delta.
    /// </summary>
    private static OptionRiskSnapshot ToSnapshot(int slot, OptionSnapshotWire? wire, DateTimeOffset asOf, TimeSpan maximumAge)
    {
        if (wire?.Greeks is null || wire.LatestQuote is null ||
            !TryFinite(wire.ImpliedVolatility, out double iv) ||
            !TryFinite(wire.Greeks.Delta, out double delta) ||
            !TryFinite(wire.Greeks.Gamma, out double gamma) ||
            !TryFinite(wire.Greeks.Vega, out double vega) ||
            !TryFinite(wire.Greeks.Theta, out double theta) ||
            !DateTimeOffset.TryParse(wire.LatestQuote.Timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp) ||
            !QuoteFreshness.IsFresh(timestamp, asOf, maximumAge))
            return new OptionRiskSnapshot(slot, 0, 0, 0, 0, 0, 0, DataQuality.Stale);
        return new OptionRiskSnapshot(slot, iv, delta, gamma, vega, theta,
            timestamp.ToUnixTimeMilliseconds() * 1_000_000L, DataQuality.Healthy);
    }

    /// <summary>
    /// Reads a finite number that the venue may send as a JSON number or a string.
    ///
    /// An absent property deserializes to a default <see cref="JsonElement"/> whose kind is
    /// <see cref="JsonValueKind.Undefined"/>, and calling <c>GetString</c> on that throws rather
    /// than returning null. Every kind other than number and string is therefore rejected up front,
    /// so a field the venue simply did not send degrades the snapshot instead of failing the read.
    /// </summary>
    private static bool TryFinite(JsonElement value, out double result)
    {
        result = 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out result) && double.IsFinite(result),
            JsonValueKind.String => double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
                double.IsFinite(result),
            _ => false
        };
    }

    private sealed record OptionSnapshotsResponse(
        [property: JsonPropertyName("snapshots")] IReadOnlyDictionary<string, OptionSnapshotWire>? Snapshots);

    private sealed record OptionSnapshotWire(
        [property: JsonPropertyName("latestQuote")] LatestQuoteWire? LatestQuote,
        [property: JsonPropertyName("greeks")] GreeksWire? Greeks,
        [property: JsonPropertyName("impliedVolatility")] JsonElement ImpliedVolatility);

    private sealed record LatestQuoteWire([property: JsonPropertyName("t")] string? Timestamp);

    private sealed record GreeksWire(
        [property: JsonPropertyName("delta")] JsonElement Delta,
        [property: JsonPropertyName("gamma")] JsonElement Gamma,
        [property: JsonPropertyName("vega")] JsonElement Vega,
        [property: JsonPropertyName("theta")] JsonElement Theta);
}
