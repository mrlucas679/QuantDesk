using System.Text.Json;
using QuantDesk.Domain.Market;

namespace QuantDesk.Alpaca.MarketData;

public interface IAlpacaMarketDataParser
{
    bool TryParse(string json, long receiveMonotonicTicks, out NormalizedMarketEvent marketEvent);
    IReadOnlyList<NormalizedMarketEvent> ParseMany(string json, long receiveMonotonicTicks);
}

public sealed class AlpacaMarketDataParser(IReadOnlyDictionary<string, int> symbolSlots) : IAlpacaMarketDataParser
{
    public bool TryParse(string json, long receiveMonotonicTicks, out NormalizedMarketEvent marketEvent)
    {
        marketEvent = default;
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return false; }
        using (document)
        {
            return TryParseDocument(document, receiveMonotonicTicks, out marketEvent);
        }
    }

    private bool TryParseDocument(JsonDocument document, long receiveMonotonicTicks, out NormalizedMarketEvent marketEvent)
    {
        marketEvent = default;
        JsonElement root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0) return false;
            root = root[0];
        }
        if (!root.TryGetProperty("T", out JsonElement type) || type.GetString() is not { } kind)
            return false;
        if (kind is not ("q" or "t"))
            return false;
        if (!root.TryGetProperty("S", out JsonElement symbol) || !symbolSlots.TryGetValue(symbol.GetString() ?? "", out int slot))
            return false;
        long timestamp = ParseTimestamp(root);
        long sequence = root.TryGetProperty("i", out JsonElement id) && id.TryGetInt64(out long sourceId) ? sourceId : 0;
        long eventId = HashCode.Combine(slot, timestamp, sequence);
        if (kind == "q")
        {
            if (!TryGetPositive(root, "bp", out double bid) || !TryGetPositive(root, "ap", out double ask) || bid > ask)
                return false;
            double bidSize = TryGetNonNegative(root, "bs");
            double askSize = TryGetNonNegative(root, "as");
            marketEvent = NormalizedMarketEvent.FromQuote(new QuoteEvent(eventId, slot, bid, ask, bidSize, askSize, timestamp, receiveMonotonicTicks, sequence));
            return true;
        }
        if (!TryGetPositive(root, "p", out double price) || !TryGetPositive(root, "s", out double size))
            return false;
        marketEvent = NormalizedMarketEvent.FromTrade(new TradeEvent(eventId, slot, price, size, timestamp, receiveMonotonicTicks, sequence));
        return true;
    }

    public IReadOnlyList<NormalizedMarketEvent> ParseMany(string json, long receiveMonotonicTicks)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        List<NormalizedMarketEvent> result = [];
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in root.EnumerateArray())
                if (TryParse(element.GetRawText(), receiveMonotonicTicks, out NormalizedMarketEvent value)) result.Add(value);
        }
        else if (TryParse(json, receiveMonotonicTicks, out NormalizedMarketEvent single)) result.Add(single);
        return result;
    }

    private static long ParseTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("t", out JsonElement value) || value.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(value.GetString(), out DateTimeOffset timestamp))
            return 0;
        return timestamp.ToUnixTimeMilliseconds() * 1_000_000;
    }

    private static bool TryGetPositive(JsonElement root, string name, out double value)
    {
        value = 0;
        return root.TryGetProperty(name, out JsonElement element)
            && element.TryGetDouble(out value)
            && double.IsFinite(value)
            && value > 0;
    }

    private static double TryGetNonNegative(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.TryGetDouble(out double value) && double.IsFinite(value) && value >= 0 ? value : 0;
}
