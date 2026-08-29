namespace QuantDesk.Alpaca.Mapping;

public interface IInstrumentSymbolResolver
{
    bool TryResolve(int instrumentSlot, out string symbol);

    bool TryResolveBySymbol(string symbol, out int instrumentSlot);
}

public sealed class DictionaryInstrumentSymbolResolver(
    IReadOnlyDictionary<int, string> symbols) : IInstrumentSymbolResolver
{
    public bool TryResolve(int instrumentSlot, out string symbol) =>
        symbols.TryGetValue(instrumentSlot, out symbol!);

    public bool TryResolveBySymbol(string symbol, out int instrumentSlot)
    {
        foreach ((int slot, string candidate) in symbols)
        {
            if (string.Equals(candidate, symbol, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Normalize(candidate), Normalize(symbol), StringComparison.OrdinalIgnoreCase))
            {
                instrumentSlot = slot;
                return true;
            }
        }

        instrumentSlot = -1;
        return false;
    }

    private static string Normalize(string symbol) => symbol.Replace("/", string.Empty, StringComparison.Ordinal);
}
