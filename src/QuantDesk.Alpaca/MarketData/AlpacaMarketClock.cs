using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Configuration;

namespace QuantDesk.Alpaca.MarketData;

/// <summary>Whether the venue says the session is open, and when it next changes.</summary>
/// <param name="IsOpen">True while regular trading hours are in effect.</param>
/// <param name="NextOpen">When the next session begins.</param>
/// <param name="NextClose">When the current or next session ends.</param>
public sealed record MarketSession(bool IsOpen, DateTimeOffset NextOpen, DateTimeOffset NextClose);

/// <summary>
/// Asks the venue whether the equity session is open.
///
/// Why the lane needs this
/// ----------------------
/// Without it the lane could not tell a closed market from a broken feed. Both surfaced as the same
/// thing: a quote with no two-sided spread, caught as a generic exception, reported as
/// "EvidenceUnavailable", and logged as a warning with a full stack trace once per cycle. On an
/// equity that is roughly sixteen hours of identical stack traces every day, which trains an
/// operator to ignore exactly the log line that would matter when the feed really did break.
///
/// A closed session is not a fault. It is the single most predictable state the system can be in,
/// and the venue will simply tell us. Inferring it from the shape of a failed quote would be
/// guessing at something we can ask.
///
/// Crypto never closes, so the lane only consults this for instruments that have a session.
/// </summary>
public sealed class AlpacaMarketClock(HttpClient httpClient, AlpacaOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = QuantDesk.Domain.Serialization.ContractJson.Web;

    public async Task<MarketSession> GetSessionAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri(options.BaseUrl, "v2/clock").AbsoluteUri);
        request.Headers.Add("APCA-API-KEY-ID", options.KeyId);
        request.Headers.Add("APCA-API-SECRET-KEY", options.SecretKey);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await AlpacaMarketDataResponse.EnsureSuccessAsync(response, "v2/clock", cancellationToken);

        ClockPayload? payload = JsonSerializer.Deserialize<ClockPayload>(
            await response.Content.ReadAsStringAsync(cancellationToken), JsonOptions);

        return payload is null
            ? throw new InvalidOperationException("Alpaca v2/clock returned an empty body.")
            : new MarketSession(payload.IsOpen, payload.NextOpen, payload.NextClose);
    }

    private sealed record ClockPayload(
        [property: JsonPropertyName("is_open")] bool IsOpen,
        [property: JsonPropertyName("next_open")] DateTimeOffset NextOpen,
        [property: JsonPropertyName("next_close")] DateTimeOffset NextClose);
}
