using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Domain.Options;

namespace QuantDesk.Alpaca.MarketData;

/// <summary>
/// Discovers active or expired Alpaca option contracts for research acquisition and execution selection.
/// Every published contract is cross-validated against its OCC symbol and the requested filter, so nothing
/// enters a dataset or a selection universe that the caller did not ask for and cannot reproduce.
///
/// Two kinds of disagreement are answered differently, because they mean different things.
///
/// A contract that contradicts itself or the request — an unparseable symbol, a strike or expiration that
/// disagrees with its own OCC encoding, a status or underlying the caller did not ask for — means the feed
/// or the filter is wrong, and the whole acquisition fails. Publishing part of a broken response would put
/// data of unknown provenance into a research dataset.
///
/// A contract that is internally consistent but not standard-form tradable — adjusted after a corporate
/// action, or carrying a non-standard deliverable — is excluded and reported on
/// <see cref="OptionContractQuery.Excluded"/>. These occur normally in a real chain, and failing the whole
/// query over one of them would have meant a single adjusted contract silently costing the caller the
/// other several hundred. Excluded is never the same as unnoticed: every exclusion is named with its reason.
/// </summary>
public sealed class AlpacaOptionContractClient(HttpClient httpClient, AlpacaOptions options)
{
    private const int ContractsPerPage = 1000;
    private const int MaximumPages = 1000;
    private const int StandardMultiplier = 100;
    private const string Endpoint = "/v2/options/contracts";
    private static readonly JsonSerializerOptions JsonOptions = QuantDesk.Domain.Serialization.ContractJson.Web;

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
        var excluded = new Dictionary<string, OptionContractExclusion>(StringComparer.Ordinal);
        var requestUris = new List<string>();
        var cursor = new AlpacaPageCursor(MaximumPages, "option-contract");
        while (cursor.HasMorePages)
        {
            string requestUri = new Uri(options.BaseUrl, Endpoint).AbsoluteUri +
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
            OptionContractsResponse payload = await AlpacaMarketDataResponse.ReadAsync<OptionContractsResponse>(
                response, Endpoint, JsonOptions, cancellationToken);
            if (payload.OptionContracts is null)
                throw new InvalidDataException("Alpaca option-contract response omitted its contracts payload.");

            foreach (OptionContractWire wire in payload.OptionContracts)
            {
                ContractAdmission admission = Admit(
                    wire, normalizedUnderlying, expirationStart, expirationEnd, status);
                if (admission.Exclusion is OptionContractExclusion exclusion)
                {
                    excluded[exclusion.Symbol] = exclusion;
                    continue;
                }

                AlpacaOptionContract contract = admission.Contract!;
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
            requestUris,
            excluded.Values.OrderBy(exclusion => exclusion.Symbol, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Decides whether one wire contract can be published, excluded, or must fail the acquisition.
    ///
    /// Checks run in a deliberate order: everything that would mean the response itself is untrustworthy
    /// is settled first and throws, and only then are the standard-form questions asked, whose answer is
    /// an exclusion. That ordering matters — a contract has to be established as internally consistent
    /// before "we simply cannot trade this one" is a safe thing to conclude about it.
    /// </summary>
    private static ContractAdmission Admit(
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
        if (!string.Equals(underlyingSymbol, requestedUnderlying, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Option contract '{symbol}' has underlying '{underlyingSymbol}', not '{requestedUnderlying}'.");
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

        // The response is trustworthy from here on. What remains is whether this particular contract is
        // one whose economics the defined-risk arithmetic can express.
        string rootSymbol = Normalize(wire.RootSymbol);
        if (!string.Equals(rootSymbol, requestedUnderlying, StringComparison.Ordinal) ||
            !string.Equals(occ.Underlying, requestedUnderlying, StringComparison.Ordinal))
        {
            return ContractAdmission.Exclude(
                symbol,
                $"adjusted or non-standard: root '{rootSymbol}' does not match underlying '{requestedUnderlying}'");
        }

        if (!TryParseStyle(wire.Style, out OptionExerciseStyle style))
            return ContractAdmission.Exclude(symbol, $"unsupported exercise style '{wire.Style}'");
        if (!TryParseStandardSize(wire.Multiplier, out int multiplier))
            return ContractAdmission.Exclude(symbol, NonStandard("multiplier", wire.Multiplier));
        if (!TryParseStandardSize(wire.Size, out int contractSize))
            return ContractAdmission.Exclude(symbol, NonStandard("deliverable size", wire.Size));

        return ContractAdmission.Publish(new AlpacaOptionContract(
            wire.Id,
            symbol,
            requestedUnderlying,
            rootSymbol,
            expiration,
            strike,
            right,
            style,
            multiplier,
            contractSize,
            requestedStatus,
            wire.Tradable));
    }

    private static string NonStandard(string field, string? value) =>
        $"non-standard {field} '{value}'; only {StandardMultiplier} can be priced as a defined-risk spread";

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

    private static bool TryParseStyle(string? value, out OptionExerciseStyle style)
    {
        switch (Normalize(value))
        {
            case "AMERICAN": style = OptionExerciseStyle.American; return true;
            case "EUROPEAN": style = OptionExerciseStyle.European; return true;
            default: style = default; return false;
        }
    }

    private static decimal ParsePositiveDecimal(string? value, string symbol, string field) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed) && parsed > 0
            ? parsed
            : throw new InvalidDataException($"Option contract '{symbol}' has an invalid {field} '{value}'.");

    /// <summary>
    /// Accepts only the standard 100-share multiplier and deliverable size. A non-standard value signals a
    /// contract adjusted by a corporate action, whose maximum loss and per-contract economics this system
    /// would compute wrongly — so it is excluded rather than priced.
    /// </summary>
    private static bool TryParseStandardSize(string? value, out int size)
    {
        size = 0;
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed) ||
            parsed != StandardMultiplier)
            return false;
        size = StandardMultiplier;
        return true;
    }

    /// <summary>One contract's fate: published, or excluded with a stated reason.</summary>
    private readonly record struct ContractAdmission(
        AlpacaOptionContract? Contract,
        OptionContractExclusion? Exclusion)
    {
        public static ContractAdmission Publish(AlpacaOptionContract contract) => new(contract, null);
        public static ContractAdmission Exclude(string symbol, string reason) =>
            new(null, new OptionContractExclusion(symbol, reason));
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
    IReadOnlyList<string> RequestUris,
    IReadOnlyList<OptionContractExclusion> Excluded);

/// <summary>
/// A contract the venue returned that this system will not trade, with the reason it was set aside.
///
/// Carried on the query rather than logged and forgotten: if a chain comes back entirely excluded, the
/// difference between "the venue returned nothing" and "the venue returned only adjusted contracts" is
/// the whole diagnosis, and it is only available here.
/// </summary>
public sealed record OptionContractExclusion(string Symbol, string Reason);

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
