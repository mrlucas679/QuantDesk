using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Domain.Options;

namespace QuantDesk.Alpaca.MarketData;

/// <summary>Discovers active or expired Alpaca option contracts for research and execution selection.</summary>
public sealed class AlpacaOptionContractClient(HttpClient httpClient, AlpacaOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AlpacaOptionContract>> ListAsync(
        string underlying,
        DateOnly expirationStart,
        DateOnly expirationEnd,
        string status,
        CancellationToken cancellationToken)
    {
        string normalizedUnderlying = underlying.Trim().ToUpperInvariant();
        if (normalizedUnderlying.Length is 0 or > 6 || !normalizedUnderlying.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Underlying must be a valid US symbol.", nameof(underlying));
        if (expirationStart > expirationEnd)
            throw new ArgumentException("Expiration start must not follow expiration end.", nameof(expirationStart));
        if (status is not ("active" or "inactive"))
            throw new ArgumentException("Contract status must be active or inactive.", nameof(status));

        var contracts = new Dictionary<string, AlpacaOptionContract>(StringComparer.Ordinal);
        string? pageToken = null;
        do
        {
            string requestUri = new Uri(options.BaseUrl, "/v2/options/contracts").AbsoluteUri +
                $"?underlying_symbols={Uri.EscapeDataString(normalizedUnderlying)}" +
                $"&status={status}" +
                $"&expiration_date_gte={expirationStart:yyyy-MM-dd}" +
                $"&expiration_date_lte={expirationEnd:yyyy-MM-dd}&limit=1000" +
                (pageToken is null ? string.Empty : $"&page_token={Uri.EscapeDataString(pageToken)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("APCA-API-KEY-ID", options.KeyId);
            request.Headers.Add("APCA-API-SECRET-KEY", options.SecretKey);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            OptionContractsResponse? payload = await response.Content.ReadFromJsonAsync<OptionContractsResponse>(
                JsonOptions, cancellationToken);
            if (payload is null) throw new InvalidDataException("Alpaca option-contract response was empty.");
            foreach (OptionContractWire contract in payload.OptionContracts)
            {
                if (!OccOptionSymbol.TryParse(contract.Symbol, out OccOptionSymbol? parsed) || parsed is null ||
                    !string.Equals(parsed.Underlying, normalizedUnderlying, StringComparison.Ordinal) ||
                    !decimal.TryParse(contract.StrikePrice, NumberStyles.Number, CultureInfo.InvariantCulture,
                        out decimal strike) || strike <= 0)
                    throw new InvalidDataException($"Alpaca returned an invalid option contract '{contract.Symbol}'.");
                contracts[parsed.BrokerSymbol] = new AlpacaOptionContract(
                    contract.Id,
                    parsed.BrokerSymbol,
                    normalizedUnderlying,
                    parsed.Expiration,
                    strike,
                    parsed.Right,
                    contract.Status,
                    contract.Tradable);
            }
            pageToken = string.IsNullOrWhiteSpace(payload.NextPageToken) ? null : payload.NextPageToken;
        } while (pageToken is not null);

        return contracts.Values
            .OrderBy(contract => contract.Expiration)
            .ThenBy(contract => contract.Strike)
            .ThenBy(contract => contract.Right)
            .ToArray();
    }

    private sealed record OptionContractsResponse(
        [property: JsonPropertyName("option_contracts")] IReadOnlyList<OptionContractWire> OptionContracts,
        [property: JsonPropertyName("next_page_token")] string? NextPageToken);

    private sealed record OptionContractWire(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("symbol")] string Symbol,
        [property: JsonPropertyName("strike_price")] string StrikePrice,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("tradable")] bool Tradable);
}

public sealed record AlpacaOptionContract(
    string Id,
    string Symbol,
    string Underlying,
    DateOnly Expiration,
    decimal Strike,
    OptionRight Right,
    string Status,
    bool Tradable);
