using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Domain.Options;

namespace QuantDesk.Alpaca.MarketData;

/// <summary>
/// Retrieves paginated option bars for explicitly identified OCC contracts. An unrequested symbol, a bar
/// outside the requested window, a self-inconsistent bar, or a conflicting duplicate fails the acquisition
/// so a research dataset can never absorb data the caller did not ask for and cannot reproduce.
/// </summary>
public sealed class AlpacaHistoricalOptionBarClient(HttpClient httpClient, AlpacaOptions options)
{
    private const int MaximumSymbolsPerRequest = 100;
    private const int MaximumPages = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<HistoricalOptionBar>> EmptyBars =
        new Dictionary<string, IReadOnlyList<HistoricalOptionBar>>(StringComparer.Ordinal);

    public async Task<OptionBarQuery> GetBarsAsync(
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
        var requestUris = new List<string>();
        var cursor = new AlpacaPageCursor(MaximumPages, "option-bar");
        string querySymbols = string.Join(',', normalized);
        while (cursor.HasMorePages)
        {
            string requestUri = options.DataUri("v1beta1/options/bars") +
                $"?symbols={Uri.EscapeDataString(querySymbols)}&timeframe={Uri.EscapeDataString(timeframe)}" +
                $"&start={Uri.EscapeDataString(start.ToString("O"))}&end={Uri.EscapeDataString(end.ToString("O"))}" +
                "&limit=10000&sort=asc" +
                cursor.PageTokenQuery;
            requestUris.Add(requestUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("APCA-API-KEY-ID", options.KeyId);
            request.Headers.Add("APCA-API-SECRET-KEY", options.SecretKey);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            OptionBarsResponse? payload = await response.Content.ReadFromJsonAsync<OptionBarsResponse>(
                JsonOptions, cancellationToken);
            if (payload is null) throw new InvalidDataException("Alpaca option-bars response was empty.");
            foreach ((string symbol, IReadOnlyList<HistoricalOptionBar> bars) in
                payload.Bars ?? EmptyBars)
            {
                if (!barsBySymbol.TryGetValue(symbol, out Dictionary<DateTimeOffset, HistoricalOptionBar>? target))
                    throw new InvalidDataException($"Alpaca returned an unrequested option symbol '{symbol}'.");
                foreach (HistoricalOptionBar bar in bars)
                {
                    ValidateBar(bar, symbol, start, end);
                    if (!target.TryAdd(bar.Timestamp, bar) && target[bar.Timestamp] != bar)
                        throw new InvalidDataException($"Conflicting option bars share timestamp {bar.Timestamp:O}.");
                }
            }

            cursor.Advance(payload.NextPageToken);
        }

        return new OptionBarQuery(
            start,
            end,
            timeframe,
            barsBySymbol.ToDictionary(
                item => item.Key,
                item => (IReadOnlyList<HistoricalOptionBar>)item.Value.Values.OrderBy(bar => bar.Timestamp).ToArray(),
                StringComparer.Ordinal),
            requestUris);
    }

    private static string ValidateSymbol(string value)
    {
        if (!OccOptionSymbol.TryParse(value, out OccOptionSymbol? parsed) || parsed is null)
            throw new ArgumentException($"'{value}' is not a valid OCC option symbol.", nameof(value));
        return parsed.BrokerSymbol;
    }

    /// <summary>Rejects a bar outside the requested window or one whose own OHLC values contradict.</summary>
    private static void ValidateBar(
        HistoricalOptionBar bar, string symbol, DateTimeOffset start, DateTimeOffset end)
    {
        if (bar.Timestamp < start || bar.Timestamp > end)
        {
            throw new InvalidDataException(
                $"Option bar for '{symbol}' at {bar.Timestamp:O} falls outside the requested window " +
                $"{start:O}..{end:O}.");
        }

        if (bar.Open <= 0 || bar.High <= 0 || bar.Low <= 0 || bar.Close <= 0 || bar.Vwap <= 0)
            throw new InvalidDataException($"Option bar for '{symbol}' at {bar.Timestamp:O} has a non-positive price.");
        if (bar.Volume < 0 || bar.TradeCount < 0)
            throw new InvalidDataException($"Option bar for '{symbol}' at {bar.Timestamp:O} has negative activity.");
        if (bar.Low > bar.High || bar.Open > bar.High || bar.Close > bar.High ||
            bar.Open < bar.Low || bar.Close < bar.Low)
        {
            throw new InvalidDataException(
                $"Option bar for '{symbol}' at {bar.Timestamp:O} has inconsistent OHLC values.");
        }
    }

    private sealed record OptionBarsResponse(
        [property: JsonPropertyName("bars")] IReadOnlyDictionary<string, IReadOnlyList<HistoricalOptionBar>> Bars,
        [property: JsonPropertyName("next_page_token")] string? NextPageToken);
}

/// <summary>
/// A completed option-bar acquisition together with the exact requests it answered. The request URIs carry
/// no credentials — Alpaca keys travel in headers — so they can be persisted as reproducible provenance.
/// </summary>
public sealed record OptionBarQuery(
    DateTimeOffset Start,
    DateTimeOffset End,
    string Timeframe,
    IReadOnlyDictionary<string, IReadOnlyList<HistoricalOptionBar>> Bars,
    IReadOnlyList<string> RequestUris);

public sealed record HistoricalOptionBar(
    [property: JsonPropertyName("t")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("o")] decimal Open,
    [property: JsonPropertyName("h")] decimal High,
    [property: JsonPropertyName("l")] decimal Low,
    [property: JsonPropertyName("c")] decimal Close,
    [property: JsonPropertyName("v")] decimal Volume,
    [property: JsonPropertyName("n")] long TradeCount,
    [property: JsonPropertyName("vw")] decimal Vwap);
