using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Domain.Options;

namespace QuantDesk.Alpaca.MarketData;

/// <summary>Retrieves paginated option bars for explicitly identified OCC contracts.</summary>
public sealed class AlpacaHistoricalOptionBarClient(HttpClient httpClient, AlpacaOptions options)
{
    private const int MaximumSymbolsPerRequest = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<HistoricalOptionBar>>> GetBarsAsync(
        IReadOnlyCollection<string> symbols,
        DateTimeOffset start,
        DateTimeOffset end,
        string timeframe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeframe);
        if (symbols.Count is 0 or > MaximumSymbolsPerRequest)
            throw new ArgumentOutOfRangeException(nameof(symbols), "One to 100 OCC symbols are required.");
        if (start >= end) throw new ArgumentException("Start must precede end.", nameof(start));
        string[] normalized = symbols.Select(ValidateSymbol).Distinct(StringComparer.Ordinal).ToArray();

        var barsBySymbol = normalized.ToDictionary(
            symbol => symbol,
            _ => new Dictionary<DateTimeOffset, HistoricalOptionBar>(),
            StringComparer.Ordinal);
        string? pageToken = null;
        do
        {
            string querySymbols = string.Join(',', normalized);
            string requestUri = "https://data.alpaca.markets/v1beta1/options/bars" +
                $"?symbols={Uri.EscapeDataString(querySymbols)}&timeframe={Uri.EscapeDataString(timeframe)}" +
                $"&start={Uri.EscapeDataString(start.ToString("O"))}&end={Uri.EscapeDataString(end.ToString("O"))}" +
                "&limit=10000&sort=asc" +
                (pageToken is null ? string.Empty : $"&page_token={Uri.EscapeDataString(pageToken)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("APCA-API-KEY-ID", options.KeyId);
            request.Headers.Add("APCA-API-SECRET-KEY", options.SecretKey);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            OptionBarsResponse? payload = await response.Content.ReadFromJsonAsync<OptionBarsResponse>(
                JsonOptions, cancellationToken);
            if (payload is null) throw new InvalidDataException("Alpaca option-bars response was empty.");
            foreach ((string symbol, IReadOnlyList<HistoricalOptionBar> bars) in payload.Bars)
            {
                if (!barsBySymbol.TryGetValue(symbol, out Dictionary<DateTimeOffset, HistoricalOptionBar>? target))
                    throw new InvalidDataException($"Alpaca returned an unrequested option symbol '{symbol}'.");
                foreach (HistoricalOptionBar bar in bars)
                {
                    if (!target.TryAdd(bar.Timestamp, bar) && target[bar.Timestamp] != bar)
                        throw new InvalidDataException($"Conflicting option bars share timestamp {bar.Timestamp:O}.");
                }
            }
            pageToken = string.IsNullOrWhiteSpace(payload.NextPageToken) ? null : payload.NextPageToken;
        } while (pageToken is not null);

        return barsBySymbol.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<HistoricalOptionBar>)item.Value.Values.OrderBy(bar => bar.Timestamp).ToArray(),
            StringComparer.Ordinal);
    }

    private static string ValidateSymbol(string value)
    {
        if (!OccOptionSymbol.TryParse(value, out OccOptionSymbol? parsed) || parsed is null)
            throw new ArgumentException($"'{value}' is not a valid OCC option symbol.", nameof(value));
        return parsed.BrokerSymbol;
    }

    private sealed record OptionBarsResponse(
        [property: JsonPropertyName("bars")] IReadOnlyDictionary<string, IReadOnlyList<HistoricalOptionBar>> Bars,
        [property: JsonPropertyName("next_page_token")] string? NextPageToken);
}

public sealed record HistoricalOptionBar(
    [property: JsonPropertyName("t")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("o")] decimal Open,
    [property: JsonPropertyName("h")] decimal High,
    [property: JsonPropertyName("l")] decimal Low,
    [property: JsonPropertyName("c")] decimal Close,
    [property: JsonPropertyName("v")] decimal Volume,
    [property: JsonPropertyName("n")] long TradeCount,
    [property: JsonPropertyName("vw")] decimal Vwap);
