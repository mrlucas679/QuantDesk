using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Domain.Options;
using QuantDesk.Domain.Runtime;

namespace QuantDesk.Alpaca.MarketData;

/// <summary>Reads authenticated Alpaca option snapshots with IV and Greeks for risk admission.</summary>
public sealed class AlpacaOptionRiskSnapshotClient(HttpClient httpClient, AlpacaOptions options)
{
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

        string uri = options.DataUri("v1beta1/options/snapshots") +
            $"?symbols={Uri.EscapeDataString(string.Join(',', normalized.Keys))}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("APCA-API-KEY-ID", options.KeyId);
        request.Headers.Add("APCA-API-SECRET-KEY", options.SecretKey);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        OptionSnapshotsResponse? payload = await response.Content.ReadFromJsonAsync<OptionSnapshotsResponse>(
            JsonOptions, cancellationToken);
        if (payload?.Snapshots is null) throw new InvalidDataException("Alpaca option snapshot response was empty.");
        foreach (string returned in payload.Snapshots.Keys)
            if (!normalized.ContainsKey(returned))
                throw new InvalidDataException($"Alpaca returned an unrequested option symbol '{returned}'.");

        return normalized.ToDictionary(
            item => item.Value,
            item => ToSnapshot(item.Value, payload.Snapshots.GetValueOrDefault(item.Key), asOf, maximumAge));
    }

    private static OptionRiskSnapshot ToSnapshot(int slot, OptionSnapshotWire? wire, DateTimeOffset asOf, TimeSpan maximumAge)
    {
        if (wire?.Greeks is null || wire.LatestQuote is null ||
            !TryFinite(wire.Greeks.ImpliedVolatility, out double iv) ||
            !TryFinite(wire.Greeks.Delta, out double delta) ||
            !TryFinite(wire.Greeks.Gamma, out double gamma) ||
            !TryFinite(wire.Greeks.Vega, out double vega) ||
            !TryFinite(wire.Greeks.Theta, out double theta) ||
            !DateTimeOffset.TryParse(wire.LatestQuote.Timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp) || asOf < timestamp ||
            asOf - timestamp > maximumAge)
            return new OptionRiskSnapshot(slot, 0, 0, 0, 0, 0, 0, DataQuality.Stale);
        return new OptionRiskSnapshot(slot, iv, delta, gamma, vega, theta,
            timestamp.ToUnixTimeMilliseconds() * 1_000_000L, DataQuality.Healthy);
    }

    private static bool TryFinite(JsonElement value, out double result) =>
        (value.ValueKind == JsonValueKind.Number ? value.TryGetDouble(out result) :
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result)) &&
        double.IsFinite(result);

    private sealed record OptionSnapshotsResponse(
        [property: JsonPropertyName("snapshots")] IReadOnlyDictionary<string, OptionSnapshotWire>? Snapshots);
    private sealed record OptionSnapshotWire(
        [property: JsonPropertyName("latestQuote")] LatestQuoteWire? LatestQuote,
        [property: JsonPropertyName("greeks")] GreeksWire? Greeks);
    private sealed record LatestQuoteWire([property: JsonPropertyName("t")] string? Timestamp);
    private sealed record GreeksWire(
        [property: JsonPropertyName("implied_volatility")] JsonElement ImpliedVolatility,
        [property: JsonPropertyName("delta")] JsonElement Delta,
        [property: JsonPropertyName("gamma")] JsonElement Gamma,
        [property: JsonPropertyName("vega")] JsonElement Vega,
        [property: JsonPropertyName("theta")] JsonElement Theta);
}
