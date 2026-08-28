using QuantDesk.Domain.Options;
using QuantDesk.Domain.Runtime;

namespace QuantDesk.Runtime.Options;

public static class OptionChainValidator
{
    public static bool TryValidate(
        IReadOnlyList<OptionContractDefinition> contracts,
        IReadOnlyDictionary<int, OptionQuoteSnapshot> quotes,
        DateOnly asOf,
        out string reason)
    {
        if (contracts.Count == 0)
        {
            reason = "empty_chain";
            return false;
        }
        HashSet<int> slots = [];
        foreach (OptionContractDefinition contract in contracts)
        {
            if (contract.Expiration < asOf || contract.Strike <= 0 || contract.Multiplier <= 0 ||
                string.IsNullOrWhiteSpace(contract.BrokerSymbol) || !slots.Add(contract.Id.Value))
            {
                reason = "invalid_contract";
                return false;
            }
            if (!quotes.TryGetValue(contract.Id.Value, out OptionQuoteSnapshot quote) ||
                quote.Quality != DataQuality.Healthy || !double.IsFinite(quote.Bid) || !double.IsFinite(quote.Ask) ||
                quote.Bid < 0 || quote.Ask <= 0 || quote.Bid > quote.Ask)
            {
                reason = "invalid_quote";
                return false;
            }
        }
        reason = "valid";
        return true;
    }
}
