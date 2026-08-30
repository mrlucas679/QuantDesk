using System.Text.Json;
using System.Globalization;
using QuantDesk.Domain.Execution;

namespace QuantDesk.Alpaca.Trading;

public static class AlpacaTradeUpdateParser
{
    public static bool TryParse(string json, out BrokerTradeUpdate update)
    {
        update = default;
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return false; }
        using (document)
        {
            return TryParseDocument(document, out update);
        }
    }

    private static bool TryParseDocument(JsonDocument document, out BrokerTradeUpdate update)
    {
        update = default;
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("stream", out JsonElement stream) &&
            string.Equals(stream.GetString(), "trade_updates", StringComparison.Ordinal) &&
            root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object)
        {
            root = data;
        }
        string? eventName = root.TryGetProperty("event", out JsonElement eventElement) ? eventElement.GetString() : null;
        JsonElement order = root.TryGetProperty("order", out JsonElement orderElement) ? orderElement : root;
        string? clientId = Read(order, "client_order_id");
        string? orderId = Read(order, "id");
        if (string.IsNullOrWhiteSpace(eventName) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(orderId))
            return false;
        BrokerTradeUpdateKind kind = eventName switch
        {
            "new" => BrokerTradeUpdateKind.New,
            "fill" => BrokerTradeUpdateKind.Fill,
            "partial_fill" => BrokerTradeUpdateKind.PartialFill,
            "canceled" => BrokerTradeUpdateKind.Canceled,
            "rejected" => BrokerTradeUpdateKind.Rejected,
            "expired" => BrokerTradeUpdateKind.Expired,
            _ => BrokerTradeUpdateKind.Unknown
        };
        decimal quantity = ReadDecimal(order, "filled_qty");
        decimal price = ReadDecimal(order, "filled_avg_price");
        long eventNs = TryUnixNanoseconds(Read(root, "timestamp"));
        update = new BrokerTradeUpdate(kind, clientId, orderId, quantity, price, Read(root, "rejected_at"), eventNs);
        return true;
    }

    private static string? Read(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static decimal ReadDecimal(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal result)) return result;
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out result) ? result : 0;
    }

    private static long TryUnixNanoseconds(string? value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset timestamp))
            return 0;
        long seconds = timestamp.ToUnixTimeSeconds();
        long remainderTicks = timestamp.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks - seconds * TimeSpan.TicksPerSecond;
        return checked(seconds * 1_000_000_000L + remainderTicks * 100L);
    }
}
