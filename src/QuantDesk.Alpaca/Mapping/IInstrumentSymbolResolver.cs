using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using QuantDesk.Domain.Options;

namespace QuantDesk.Alpaca.Mapping;

public interface IInstrumentSymbolResolver
{
    bool TryResolve(int instrumentSlot, out string symbol);

    bool TryResolveBySymbol(string symbol, out int instrumentSlot);

    bool TryRegisterOptionSymbol(string symbol, out int instrumentSlot)
    {
        instrumentSlot = -1;
        return false;
    }
}

public sealed class DictionaryInstrumentSymbolResolver(
    IReadOnlyDictionary<int, string> symbols) : IInstrumentSymbolResolver
{
    private readonly ConcurrentDictionary<int, string> _bySlot = new(symbols);
    private readonly ConcurrentDictionary<string, int> _bySymbol = new(
        symbols.Select(item => new KeyValuePair<string, int>(Normalize(item.Value), item.Key)),
        StringComparer.OrdinalIgnoreCase);

    public bool TryResolve(int instrumentSlot, out string symbol) =>
        _bySlot.TryGetValue(instrumentSlot, out symbol!);

    public bool TryResolveBySymbol(string symbol, out int instrumentSlot)
    {
        return _bySymbol.TryGetValue(Normalize(symbol), out instrumentSlot);
    }

    public bool TryRegisterOptionSymbol(string symbol, out int instrumentSlot)
    {
        if (!OccOptionSymbol.TryParse(symbol, out OccOptionSymbol? parsed) || parsed is null)
        {
            instrumentSlot = -1;
            return false;
        }
        string normalized = Normalize(parsed.BrokerSymbol);
        instrumentSlot = _bySymbol.GetOrAdd(normalized, static value => StableOptionSlot(value));
        if (!_bySlot.TryAdd(instrumentSlot, parsed.BrokerSymbol) &&
            (!_bySlot.TryGetValue(instrumentSlot, out string? existing) ||
             !string.Equals(Normalize(existing), normalized, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Deterministic option instrument-slot collision.");
        return true;
    }

    private static string Normalize(string symbol) =>
        symbol.Replace("/", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();

    private static int StableOptionSlot(string symbol)
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(symbol));
        return 1_000_000 + BinaryPrimitives.ReadUInt16BigEndian(hash);
    }
}
