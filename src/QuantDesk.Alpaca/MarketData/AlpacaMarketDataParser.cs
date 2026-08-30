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
    private readonly Dictionary<int, SortedDictionary<double, double>> _bids = [];
    private readonly Dictionary<int, SortedDictionary<double, double>> _asks = [];
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
        if (kind is not ("q" or "t" or "o"))
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
        if (kind == "o")
            return TryParseOrderBook(root, slot, eventId, timestamp, receiveMonotonicTicks, sequence, out marketEvent);
        if (!TryGetPositive(root, "p", out double price) || !TryGetPositive(root, "s", out double size))
            return false;
        marketEvent = NormalizedMarketEvent.FromTrade(new TradeEvent(eventId, slot, price, size, timestamp, receiveMonotonicTicks, sequence));
        return true;
    }

    private bool TryParseOrderBook(
        JsonElement root,
        int slot,
        long eventId,
        long timestamp,
        long receiveMonotonicTicks,
        long sequence,
        out NormalizedMarketEvent marketEvent)
    {
        marketEvent = default;
        if (!TryReadLevels(root, "b", out IReadOnlyList<BookLevel> bidUpdates) ||
            !TryReadLevels(root, "a", out IReadOnlyList<BookLevel> askUpdates))
            return false;
        SortedDictionary<double, double> bids = GetBook(_bids, slot);
        SortedDictionary<double, double> asks = GetBook(_asks, slot);
        if (root.TryGetProperty("r", out JsonElement reset) && reset.ValueKind == JsonValueKind.True)
        {
            bids.Clear();
            asks.Clear();
        }
        ApplyUpdates(bids, bidUpdates);
        ApplyUpdates(asks, askUpdates);
        if (bids.Count == 0 || asks.Count == 0 || timestamp <= 0)
            return false;

        double bestBid = bids.Last().Key;
        double bestAsk = asks.First().Key;
        marketEvent = NormalizedMarketEvent.FromOrderBook(new OrderBookEvent(
            eventId,
            slot,
            bestBid,
            bestAsk,
            bids.Values.Sum(),
            asks.Values.Sum(),
            timestamp,
            receiveMonotonicTicks,
            sequence));
        return bestBid <= bestAsk;
    }

    private static SortedDictionary<double, double> GetBook(
        IDictionary<int, SortedDictionary<double, double>> books,
        int slot) => books.TryGetValue(slot, out SortedDictionary<double, double>? book)
            ? book
            : books[slot] = [];

    private static bool TryReadLevels(JsonElement root, string property, out IReadOnlyList<BookLevel> levels)
    {
        levels = [];
        if (!root.TryGetProperty(property, out JsonElement payload) || payload.ValueKind != JsonValueKind.Array)
            return false;
        var parsed = new List<BookLevel>();
        foreach (JsonElement level in payload.EnumerateArray())
        {
            if (!TryGetPositive(level, "p", out double price) ||
                !level.TryGetProperty("s", out JsonElement sizeElement) ||
                !sizeElement.TryGetDouble(out double size) || !double.IsFinite(size) || size < 0)
                return false;
            parsed.Add(new BookLevel(price, size));
        }
        levels = parsed;
        return true;
    }

    private static void ApplyUpdates(IDictionary<double, double> book, IReadOnlyList<BookLevel> updates)
    {
        foreach (BookLevel update in updates)
        {
            if (update.Size == 0) book.Remove(update.Price);
            else book[update.Price] = update.Size;
        }
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

    private readonly record struct BookLevel(double Price, double Size);
}
