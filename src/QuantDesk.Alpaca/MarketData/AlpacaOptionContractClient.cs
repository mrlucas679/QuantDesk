using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Domain.Options;

namespace QuantDesk.Alpaca.MarketData;

/// <summary>
/// Discovers active or expired Alpaca option contracts for research acquisition and execution selection.
/// Every published contract is cross-validated against its OCC symbol and the requested filter, so an
/// unrequested, adjusted, non-standard, or self-inconsistent contract fails the acquisition instead of
/// silently entering a dataset or a selection universe.
/// </summary>
public sealed class AlpacaOptionContractClient(HttpClient httpClient, AlpacaOptions options)
{
    private const int ContractsPerPage = 1000;
    private const int MaximumPages = 1000;
    private const int StandardMultiplier = 100;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OptionContractQuery> ListAsync(
        string underlying,
        DateOnly expirationStart,
        DateOnly expirationEnd,
        string status,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(underlying);
        string normalizedUnderlying = underlying.Trim().ToUpperInvariant();
        if (normalizedUnderlying.Length > 6 || !normalizedUnderlying.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Underlying must be a valid US symbol.", nameof(underlying));
        if (expirationStart > expirationEnd)
            throw new ArgumentException("Expiration start must not follow expiration end.", nameof(expirationStart));
        if (status is not ("active" or "inactive"))
            throw new ArgumentException("Contract status must be active or inactive.", nameof(status));

        var contracts = new Dictionary<string, AlpacaOptionContract>(StringComparer.Ordinal);
        var requestUris = new List<string>();
        var cursor = new AlpacaPageCursor(MaximumPages, "option-contract");
        while (cursor.HasMorePages)
        {
            string requestUri = new Uri(options.BaseUrl, "/v2/options/contracts").AbsoluteUri +
                $"?underlying_symbols={Uri.EscapeDataString(normalizedUnderlying)}" +
                $"&status={status}" +
                $"&expiration_date_gte={expirationStart:yyyy-MM-dd}" +
                $"&expiration_date_lte={expirationEnd:yyyy-MM-dd}&limit={ContractsPerPage}" +
                cursor.PageTokenQuery;
            requestUris.Add(requestUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Add("APCA-API-KEY-ID", options.KeyId);
            request.Headers.Add("APCA-API-SECRET-KEY", options.SecretKey);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            OptionContractsResponse? payload = await response.Content.ReadFromJsonAsync<OptionContractsResponse>(
                JsonOptions, cancellationToken);
            if (payload?.OptionContracts is null)
                throw new InvalidDataException("Alpaca option-contract response was empty.");

            foreach (OptionContractWire wire in payload.OptionContracts)
            {
                AlpacaOptionContract contract = Validate(
                    wire, normalizedUnderlying, expirationStart, expirationEnd, status);
                if (contracts.TryGetValue(contract.Symbol, out AlpacaOptionContract? existing) && existing != contract)
                {
                    throw new InvalidDataException(
                        $"Alpaca returned conflicting definitions for option contract '{contract.Symbol}'.");
                }

                contracts[contract.Symbol] = contract;
            }

            cursor.Advance(payload.NextPageToken);
        }

        return new OptionContractQuery(
            normalizedUnderlying,
            expirationStart,
            expirationEnd,
            status,
            contracts.Values
                .OrderBy(contract => contract.Expiration)
                .ThenBy(contract => contract.Strike)
                .ThenBy(contract => contract.Right)
                .ToArray(),
            requestUris);
    }

    /// <summary>Rejects any contract that disagrees with its OCC symbol or falls outside the request.</summary>
    private static AlpacaOptionContract Validate(
        OptionContractWire wire,
        string requestedUnderlying,
        DateOnly expirationStart,
        DateOnly expirationEnd,
        string requestedStatus)
    {
        if (!OccOptionSymbol.TryParse(wire.Symbol ?? string.Empty, out OccOptionSymbol? occ) || occ is null)
            throw new InvalidDataException($"Alpaca returned an invalid option symbol '{wire.Symbol}'.");

        string symbol = occ.BrokerSymbol;
        if (string.IsNullOrWhiteSpace(wire.Id))
            throw new InvalidDataException($"Option contract '{symbol}' has no broker identifier.");
        if (!string.Equals(wire.Status, requestedStatus, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Option contract '{symbol}' reported status '{wire.Status}' for a '{requestedStatus}' request.");
        }

        string underlyingSymbol = Normalize(wire.UnderlyingSymbol);
        string rootSymbol = Normalize(wire.RootSymbol);
        if (!string.Equals(underlyingSymbol, requestedUnderlying, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Option contract '{symbol}' has underlying '{underlyingSymbol}', not '{requestedUnderlying}'.");
        }

        if (!string.Equals(rootSymbol, requestedUnderlying, StringComparison.Ordinal) ||
            !string.Equals(occ.Underlying, requestedUnderlying, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Option contract '{symbol}' is adjusted or non-standard: root '{rootSymbol}' does not " +
                $"match underlying '{requestedUnderlying}'.");
        }

        DateOnly expiration = ParseExpiration(wire.ExpirationDate, symbol);
        if (expiration != occ.Expiration)
        {
            throw new InvalidDataException(
                $"Option contract '{symbol}' reports expiration {expiration:yyyy-MM-dd} but its OCC " +
                $"symbol encodes {occ.Expiration:yyyy-MM-dd}.");
        }

        if (expiration < expirationStart || expiration > expirationEnd)
        {
            throw new InvalidDataException(
                $"Option contract '{symbol}' expires {expiration:yyyy-MM-dd}, outside the requested window " +
                $"{expirationStart:yyyy-MM-dd}..{expirationEnd:yyyy-MM-dd}.");
        }

        OptionRight right = ParseRight(wire.Type, symbol);
        if (right != occ.Right)
        {
            throw new InvalidDataException(
                $"Option contract '{symbol}' reports type '{wire.Type}' but its OCC symbol encodes {occ.Right}.");
        }

        decimal strike = ParsePositiveDecimal(wire.StrikePrice, symbol, "strike price");
        if (strike != occ.Strike)
        {
            throw new InvalidDataException(
                $"Option contract '{symbol}' reports strike {strike} but its OCC symbol encodes {occ.Strike}.");
        }

        return new AlpacaOptionContract(
            wire.Id,
            symbol,
            requestedUnderlying,
            rootSymbol,
            expiration,
            strike,
            right,
            ParseStyle(wire.Style, symbol),
            ParseStandardSize(wire.Multiplier, symbol, "multiplier"),
            ParseStandardSize(wire.Size, symbol, "size"),
            requestedStatus,
            wire.Tradable);
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static DateOnly ParseExpiration(string? value, string symbol) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
            out DateOnly parsed)
            ? parsed
            : throw new InvalidDataException($"Option contract '{symbol}' has unparseable expiration '{value}'.");

    private static OptionRight ParseRight(string? value, string symbol) => Normalize(value) switch
    {
        "CALL" => OptionRight.Call,
        "PUT" => OptionRight.Put,
        _ => throw new InvalidDataException($"Option contract '{symbol}' has unsupported type '{value}'.")
    };

    private static OptionExerciseStyle ParseStyle(string? value, string symbol) => Normalize(value) switch
    {
        "AMERICAN" => OptionExerciseStyle.American,
        "EUROPEAN" => OptionExerciseStyle.European,
        _ => throw new InvalidDataException($"Option contract '{symbol}' has unsupported style '{value}'.")
    };

    private static decimal ParsePositiveDecimal(string? value, string symbol, string field) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed) && parsed > 0
            ? parsed
            : throw new InvalidDataException($"Option contract '{symbol}' has an invalid {field} '{value}'.");

    /// <summary>
    /// Requires the standard 100-share multiplier and deliverable size. A non-standard value signals an
    /// adjusted contract whose defined maximum loss and per-contract economics would be miscomputed.
    /// </summary>
    private static int ParseStandardSize(string? value, string symbol, string field)
    {
        decimal parsed = ParsePositiveDecimal(value, symbol, field);
        return parsed == StandardMultiplier
            ? StandardMultiplier
            : throw new InvalidDataException(
                $"Option contract '{symbol}' has non-standard {field} '{value}'; " +
                $"only {StandardMultiplier} is supported.");
    }

    private sealed record OptionContractsResponse(
        [property: JsonPropertyName("option_contracts")] IReadOnlyList<OptionContractWire>? OptionContracts,
        [property: JsonPropertyName("next_page_token")] string? NextPageToken);

    private sealed record OptionContractWire(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("symbol")] string? Symbol,
        [property: JsonPropertyName("underlying_symbol")] string? UnderlyingSymbol,
        [property: JsonPropertyName("root_symbol")] string? RootSymbol,
        [property: JsonPropertyName("expiration_date")] string? ExpirationDate,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("style")] string? Style,
        [property: JsonPropertyName("strike_price")] string? StrikePrice,
        [property: JsonPropertyName("multiplier")] string? Multiplier,
        [property: JsonPropertyName("size")] string? Size,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("tradable")] bool Tradable);
}

/// <summary>
/// A completed contract discovery together with the exact request it answered. The request URIs carry no
/// credentials — Alpaca keys travel in headers — so they can be persisted as reproducible provenance.
/// </summary>
public sealed record OptionContractQuery(
    string Underlying,
    DateOnly ExpirationStart,
    DateOnly ExpirationEnd,
    string Status,
    IReadOnlyList<AlpacaOptionContract> Contracts,
    IReadOnlyList<string> RequestUris);

/// <summary>An Alpaca option contract whose broker payload agrees with its OCC symbol.</summary>
public sealed record AlpacaOptionContract(
    string Id,
    string Symbol,
    string Underlying,
    string RootSymbol,
    DateOnly Expiration,
    decimal Strike,
    OptionRight Right,
    OptionExerciseStyle Style,
    int Multiplier,
    int ContractSize,
    string Status,
    bool Tradable);
