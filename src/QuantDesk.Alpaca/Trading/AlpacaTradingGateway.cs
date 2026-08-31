using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.Mapping;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;

namespace QuantDesk.Alpaca.Trading;

public sealed class AlpacaTradingGateway(
    HttpClient httpClient,
    AlpacaOptions options,
    IInstrumentSymbolResolver symbols) : IBrokerExecutionGateway, IMultiLegBrokerExecutionGateway
{
    private readonly Uri _paperBaseUrl = ValidatePaperBaseUrl(options.BaseUrl);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsPaperEnvironment => true;

    /// <summary>Reads paper-account state for the execution preflight without exposing credentials.</summary>
    public async Task<BrokerAccountSnapshot?> GetAccountAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint("/v2/account"));
        AddCredentials(request);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        AlpacaAccount? account = await response.Content.ReadFromJsonAsync<AlpacaAccount>(JsonOptions, cancellationToken);
        return account is null || string.IsNullOrWhiteSpace(account.Id)
            ? null
            : new BrokerAccountSnapshot(
                account.Id,
                account.Status ?? "unknown",
                ParseDecimal(account.Equity),
                ParseDecimal(account.BuyingPower),
                account.TradingBlocked,
                account.AccountBlocked)
            {
                CryptoTradingStatus = account.CryptoStatus
            };
    }

    /// <summary>Reads the broker's current asset eligibility for a configured symbol.</summary>
    public async Task<BrokerAssetSnapshot?> GetAssetAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        string brokerSymbol = NormalizeBrokerSymbol(symbol);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            Endpoint($"/v2/assets/{Uri.EscapeDataString(brokerSymbol)}"));
        AddCredentials(request);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        AlpacaAsset? asset = await response.Content.ReadFromJsonAsync<AlpacaAsset>(JsonOptions, cancellationToken);
        return asset is null || string.IsNullOrWhiteSpace(asset.Symbol)
            ? null
            : new BrokerAssetSnapshot(
                asset.Symbol,
                asset.Status ?? "unknown",
                asset.AssetClass ?? "unknown",
                asset.Tradable);
    }

    public async Task<BrokerSubmitResult> SubmitAsync(ExecutionCommand command, CancellationToken cancellationToken)
    {
        if (!symbols.TryResolve(command.InstrumentSlot, out string? symbol))
            return new BrokerSubmitResult(BrokerSubmitState.Rejected, null, "UNKNOWN_INSTRUMENT_SLOT", null);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("/v2/orders"))
        {
            Content = JsonContent.Create(CreateOrder(command, symbol), options: JsonOptions)
        };
        AddCredentials(request);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string? requestId = ReadRequestId(response);

        if (!response.IsSuccessStatusCode)
        {
            string reason = await ReadBrokerRejectionAsync(response, cancellationToken);
            return new BrokerSubmitResult(BrokerSubmitState.Rejected, null, reason, requestId);
        }

        AlpacaOrder? order = await response.Content.ReadFromJsonAsync<AlpacaOrder>(JsonOptions, cancellationToken);
        if (order is null || string.IsNullOrWhiteSpace(order.Id))
            return new BrokerSubmitResult(BrokerSubmitState.Unknown, null, "BROKER_RESPONSE_INVALID", requestId);

        return new BrokerSubmitResult(BrokerSubmitState.Acknowledged, order.Id, null, requestId);
    }

    /// <summary>Submits one atomic PAPER options strategy with explicit parent and leg semantics.</summary>
    public async Task<BrokerSubmitResult> SubmitMultiLegAsync(
        MultiLegExecutionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!command.IsValid())
            return new BrokerSubmitResult(BrokerSubmitState.Rejected, null, "INVALID_MLEG_COMMAND", null);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("/v2/orders"))
        {
            Content = JsonContent.Create(CreateMultiLegOrder(command), options: JsonOptions)
        };
        AddCredentials(request);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string? requestId = ReadRequestId(response);
        if (!response.IsSuccessStatusCode)
        {
            string reason = await ReadBrokerRejectionAsync(response, cancellationToken);
            return new BrokerSubmitResult(BrokerSubmitState.Rejected, null, reason, requestId);
        }
        AlpacaOrder? order = await response.Content.ReadFromJsonAsync<AlpacaOrder>(
            JsonOptions, cancellationToken);
        return string.IsNullOrWhiteSpace(order?.Id)
            ? new BrokerSubmitResult(BrokerSubmitState.Unknown, null, "BROKER_RESPONSE_INVALID", requestId)
            : new BrokerSubmitResult(BrokerSubmitState.Acknowledged, order.Id, null, requestId);
    }

    public async Task<BrokerOrderSnapshot?> FindByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            Endpoint($"/v2/orders:by_client_order_id?client_order_id={Uri.EscapeDataString(clientOrderId)}"));
        AddCredentials(request);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        AlpacaOrder? order = await response.Content.ReadFromJsonAsync<AlpacaOrder>(JsonOptions, cancellationToken);
        return order is null ? null : ToSnapshot(order);
    }

    public async Task<IReadOnlyList<BrokerOrderSnapshot>> ListOpenOrdersAsync(CancellationToken cancellationToken)
    {
        return await ListOpenOrdersAsync("/v2/orders?status=open", cancellationToken);
    }

    /// <summary>Queries only open orders relevant to one broker symbol.</summary>
    public async Task<IReadOnlyList<BrokerOrderSnapshot>> ListOpenOrdersForSymbolAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        string brokerSymbol = NormalizeBrokerSymbol(symbol);
        string path = $"/v2/orders?status=open&symbols={Uri.EscapeDataString(brokerSymbol)}";
        return await ListOpenOrdersAsync(path, cancellationToken);
    }

    private async Task<IReadOnlyList<BrokerOrderSnapshot>> ListOpenOrdersAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(path));
        AddCredentials(request);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        List<AlpacaOrder>? orders = await response.Content.ReadFromJsonAsync<List<AlpacaOrder>>(JsonOptions, cancellationToken);
        return orders?.Select(ToSnapshot).ToArray() ?? [];
    }

    public async Task<IReadOnlyList<BrokerPositionSnapshot>> ListPositionsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint("/v2/positions"));
        AddCredentials(request);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        List<AlpacaPosition>? positions = await response.Content.ReadFromJsonAsync<List<AlpacaPosition>>(JsonOptions, cancellationToken);
        return positions?
            .Select(position =>
            {
                if (!symbols.TryResolveBySymbol(position.Symbol, out int slot))
                    symbols.TryRegisterOptionSymbol(position.Symbol, out slot);
                return new BrokerPositionSnapshot(position.Symbol, slot, ParseDecimal(position.Quantity), ParseDecimal(position.AverageEntryPrice));
            })
            .ToArray() ?? [];
    }

    /// <summary>Cancels an existing paper order and preserves the broker request id.</summary>
    public async Task<BrokerSubmitResult> CancelAsync(string brokerOrderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerOrderId);
        using var request = new HttpRequestMessage(HttpMethod.Delete, Endpoint($"/v2/orders/{Uri.EscapeDataString(brokerOrderId)}"));
        AddCredentials(request);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string? requestId = ReadRequestId(response);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
            return new BrokerSubmitResult(BrokerSubmitState.Acknowledged, brokerOrderId, null, requestId);
        return new BrokerSubmitResult(BrokerSubmitState.Rejected, brokerOrderId, "BROKER_CANCEL_REJECTED", requestId);
    }

    /// <summary>Replaces quantity and/or limit price on an existing paper order.</summary>
    public async Task<BrokerSubmitResult> ReplaceAsync(
        string brokerOrderId, decimal? quantity, decimal? limitPrice, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerOrderId);
        if (quantity is <= 0 || limitPrice is <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Replacement values must be positive when supplied.");
        var payload = new Dictionary<string, string>(StringComparer.Ordinal);
        if (quantity.HasValue) payload["qty"] = quantity.Value.ToString(CultureInfo.InvariantCulture);
        if (limitPrice.HasValue) payload["limit_price"] = limitPrice.Value.ToString(CultureInfo.InvariantCulture);
        if (payload.Count == 0) throw new ArgumentException("At least one replacement value is required.");
        using var request = new HttpRequestMessage(HttpMethod.Patch, Endpoint($"/v2/orders/{Uri.EscapeDataString(brokerOrderId)}"))
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        AddCredentials(request);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string? requestId = ReadRequestId(response);
        if (!response.IsSuccessStatusCode)
            return new BrokerSubmitResult(BrokerSubmitState.Rejected, brokerOrderId, "BROKER_REPLACE_REJECTED", requestId);
        AlpacaOrder? order = await response.Content.ReadFromJsonAsync<AlpacaOrder>(JsonOptions, cancellationToken);
        return string.IsNullOrWhiteSpace(order?.Id)
            ? new BrokerSubmitResult(BrokerSubmitState.Unknown, brokerOrderId, "BROKER_RESPONSE_INVALID", requestId)
            : new BrokerSubmitResult(BrokerSubmitState.Acknowledged, order.Id, null, requestId);
    }

    public async Task<BrokerSubmitResult> ClosePositionAsync(
        int instrumentSlot, CancellationToken cancellationToken)
    {
        if (!symbols.TryResolve(instrumentSlot, out string? symbol))
            return new BrokerSubmitResult(BrokerSubmitState.Rejected, null, "UNKNOWN_INSTRUMENT_SLOT", null);
        string brokerSymbol = symbol.Replace("/", string.Empty, StringComparison.Ordinal);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            Endpoint($"/v2/positions/{Uri.EscapeDataString(brokerSymbol)}"));
        AddCredentials(request);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string? requestId = ReadRequestId(response);
        if (!response.IsSuccessStatusCode)
            return new BrokerSubmitResult(BrokerSubmitState.Rejected, null, "BROKER_CLOSE_POSITION_REJECTED", requestId);
        AlpacaOrder? order = await response.Content.ReadFromJsonAsync<AlpacaOrder>(JsonOptions, cancellationToken);
        return string.IsNullOrWhiteSpace(order?.Id)
            ? new BrokerSubmitResult(BrokerSubmitState.Unknown, null, "BROKER_RESPONSE_INVALID", requestId)
            : new BrokerSubmitResult(BrokerSubmitState.Acknowledged, order.Id, null, requestId);
    }

    private static object CreateOrder(ExecutionCommand command, string symbol)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["symbol"] = symbol,
            ["side"] = command.Side == OrderSide.Buy ? "buy" : "sell",
            ["type"] = ToBrokerOrderType(command.OrderType),
            ["time_in_force"] = ToBrokerTimeInForce(command.TimeInForce),
            ["client_order_id"] = command.ClientOrderId,
            ["limit_price"] = command.LimitPrice?.ToString(CultureInfo.InvariantCulture)
        };
        if (command.Notional is decimal notional)
            payload["notional"] = notional.ToString(CultureInfo.InvariantCulture);
        else
            payload["qty"] = command.Quantity.ToString(CultureInfo.InvariantCulture);
        string? positionIntent = ToBrokerPositionIntent(command.PositionIntent);
        if (positionIntent is not null) payload["position_intent"] = positionIntent;
        return payload;
    }

    private static object CreateMultiLegOrder(MultiLegExecutionCommand command) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["order_class"] = "mleg",
            ["client_order_id"] = command.ClientOrderId,
            ["qty"] = command.Quantity.ToString(CultureInfo.InvariantCulture),
            ["type"] = ToBrokerOrderType(command.OrderType),
            ["time_in_force"] = ToBrokerTimeInForce(command.TimeInForce),
            ["limit_price"] = command.LimitPrice?.ToString(CultureInfo.InvariantCulture),
            ["legs"] = command.Legs.Select(leg => new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["symbol"] = leg.Symbol,
                ["ratio_qty"] = leg.RatioQuantity.ToString(CultureInfo.InvariantCulture),
                ["side"] = leg.Side == OrderSide.Buy ? "buy" : "sell",
                ["position_intent"] = ToBrokerPositionIntent(leg.PositionIntent)
                    ?? throw new InvalidOperationException("MLeg position intent is required.")
            }).ToArray()
        };

    private Uri Endpoint(string path) => new(_paperBaseUrl, path);

    private static Uri ValidatePaperBaseUrl(Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (!string.Equals(baseUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(baseUrl.Host, "paper-api.alpaca.markets", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Trading gateway is restricted to Alpaca paper trading.", nameof(baseUrl));
        return baseUrl;
    }

    private void AddCredentials(HttpRequestMessage request)
    {
        request.Headers.Add("APCA-API-KEY-ID", options.KeyId);
        request.Headers.Add("APCA-API-SECRET-KEY", options.SecretKey);
    }

    private static string? ReadRequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Request-ID", out IEnumerable<string>? values) ? values.FirstOrDefault() : null;

    private static async Task<string> ReadBrokerRejectionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string prefix = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "BROKER_UNAUTHORIZED"
            : "BROKER_ORDER_REJECTED";
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            string code = document.RootElement.TryGetProperty("code", out JsonElement codeElement)
                ? codeElement.ToString()
                : ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            string message = document.RootElement.TryGetProperty("message", out JsonElement messageElement)
                ? messageElement.GetString() ?? "unknown"
                : "unknown";
            return $"{prefix}:{code}:{message}";
        }
        catch (JsonException)
        {
            return $"{prefix}:HTTP_{(int)response.StatusCode}";
        }
    }

    private static BrokerOrderSnapshot ToSnapshot(AlpacaOrder order) => new(
        order.Id, order.ClientOrderId ?? string.Empty, order.Status ?? "unknown", ParseDecimal(order.FilledQuantity),
        order.FilledAveragePrice is null ? null : ParseDecimal(order.FilledAveragePrice))
    {
        Symbol = order.Symbol,
        CreatedAt = order.CreatedAt,
        SubmittedAt = order.SubmittedAt,
        UpdatedAt = order.UpdatedAt,
        FilledAt = order.FilledAt,
        CanceledAt = order.CanceledAt,
        ExpiredAt = order.ExpiredAt,
        RejectedAt = order.FailedAt
    };

    private static string NormalizeBrokerSymbol(string symbol) =>
        symbol.Trim().Replace("/", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static decimal ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed) ? parsed : 0;

    private static string ToBrokerOrderType(ExecutionOrderType type) => type switch
    {
        ExecutionOrderType.Market => "market",
        ExecutionOrderType.Limit => "limit",
        ExecutionOrderType.StopLimit => "stop_limit",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static string ToBrokerTimeInForce(ExecutionTimeInForce timeInForce) => timeInForce switch
    {
        ExecutionTimeInForce.Day => "day",
        ExecutionTimeInForce.Gtc => "gtc",
        ExecutionTimeInForce.Ioc => "ioc",
        _ => throw new ArgumentOutOfRangeException(nameof(timeInForce))
    };

    private static string? ToBrokerPositionIntent(PositionIntent intent) => intent switch
    {
        PositionIntent.BuyToOpen => "buy_to_open",
        PositionIntent.BuyToClose => "buy_to_close",
        PositionIntent.SellToOpen => "sell_to_open",
        PositionIntent.SellToClose => "sell_to_close",
        _ => null
    };

    private sealed record AlpacaOrder(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("client_order_id")] string? ClientOrderId,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("filled_qty")] string? FilledQuantity,
        [property: JsonPropertyName("filled_avg_price")] string? FilledAveragePrice,
        [property: JsonPropertyName("symbol")] string? Symbol = null,
        [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt = null,
        [property: JsonPropertyName("submitted_at")] DateTimeOffset? SubmittedAt = null,
        [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt = null,
        [property: JsonPropertyName("filled_at")] DateTimeOffset? FilledAt = null,
        [property: JsonPropertyName("canceled_at")] DateTimeOffset? CanceledAt = null,
        [property: JsonPropertyName("expired_at")] DateTimeOffset? ExpiredAt = null,
        [property: JsonPropertyName("failed_at")] DateTimeOffset? FailedAt = null);

    private sealed record AlpacaAsset(
        [property: JsonPropertyName("symbol")] string Symbol,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("class")] string? AssetClass,
        [property: JsonPropertyName("tradable")] bool Tradable);

    private sealed record AlpacaPosition(
        [property: JsonPropertyName("symbol")] string Symbol,
        [property: JsonPropertyName("qty")] string Quantity,
        [property: JsonPropertyName("avg_entry_price")] string AverageEntryPrice);

    private sealed record AlpacaAccount(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("equity")] string? Equity,
        [property: JsonPropertyName("buying_power")] string? BuyingPower,
        [property: JsonPropertyName("trading_blocked")] bool TradingBlocked,
        [property: JsonPropertyName("account_blocked")] bool AccountBlocked,
        [property: JsonPropertyName("crypto_status")] string? CryptoStatus = null);
}
