using QuantDesk.Alpaca.Mapping;
using QuantDesk.Domain.Runtime;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.State;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Marks held positions from the live market state.
///
/// Returns null on anything less than a healthy two-sided quote. A stop that fires on missing data
/// would liquidate during a feed outage — the moment the account is least able to judge what price
/// it would actually get — so the absence of a quote leaves the scheduled exit as the only bound,
/// which is what it was before this existed.
/// </summary>
public sealed class MarketStateHeldPositionMarker(
    MarketStateStore market,
    IInstrumentSymbolResolver symbols) : IHeldPositionMarker
{
    public decimal? CurrentMid(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        if (!symbols.TryResolveBySymbol(symbol, out int slot)) return null;

        InstrumentSnapshot snapshot = market.Snapshot(slot);
        if (snapshot.QuoteQuality != DataQuality.Healthy) return null;
        if (snapshot.Bid <= 0d || snapshot.Ask <= 0d) return null;

        return (decimal)snapshot.Mid;
    }

    public decimal? CurrentRelativeSpread(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        if (!symbols.TryResolveBySymbol(symbol, out int slot)) return null;

        InstrumentSnapshot snapshot = market.Snapshot(slot);
        if (snapshot.QuoteQuality != DataQuality.Healthy) return null;
        if (snapshot.Bid <= 0d || snapshot.Ask <= 0d) return null;

        return (decimal)snapshot.RelativeSpread;
    }
}
