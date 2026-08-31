namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// The venue cost a momentum opportunity must clear before it is admissible, per asset class.
///
/// These were previously three <c>private const</c> fields inside the crypto research gate, which
/// hardcoded Alpaca's spot-crypto fee schedule into the only autonomous lane and made the gate
/// unusable for any other asset class. The numbers themselves were correct; being unable to change
/// them was the defect.
///
/// Why this matters for whether the system ever trades: the gate admits an opportunity only when
/// the expected move exceeds <c>spread + fees + slippage + minimum edge</c>. For spot crypto that
/// sum is roughly 61 bps, so a short-horizon signal has to predict a 0.7% move before the
/// application will place an order. For US equities the same sum is under 8 bps, because Alpaca
/// charges no commission and these instruments quote a penny spread. The hurdle differs by roughly
/// an order of magnitude, and that difference — not the signal logic — is what decides whether any
/// trade is ever admissible.
/// </summary>
/// <param name="AssetClass">Identifier recorded on the decision for audit.</param>
/// <param name="RoundTripFeeBps">Venue fees for a complete round trip, excluding spread.</param>
/// <param name="SlippageAllowanceBps">Allowance for fills worse than the quoted touch.</param>
/// <param name="MinimumNetEdgeBps">Margin the expected move must clear beyond modelled cost.</param>
public sealed record ExecutionCostProfile(
    string AssetClass,
    decimal RoundTripFeeBps,
    decimal SlippageAllowanceBps,
    decimal MinimumNetEdgeBps)
{
    /// <summary>
    /// Alpaca spot crypto at tier 1: 0.25% taker per side, so 50 bps for a round trip before
    /// spread. Verified against https://docs.alpaca.markets/us/docs/crypto-fees.
    /// </summary>
    public static readonly ExecutionCostProfile SpotCryptoTaker =
        new("spot-crypto-taker", 50m, 10m, 10m);

    /// <summary>
    /// Alpaca spot crypto at tier 1 paying the maker rate: 0.15% per side, 30 bps round trip.
    /// Only valid for a lane that actually rests limit orders; a marketable order pays taker.
    /// </summary>
    public static readonly ExecutionCostProfile SpotCryptoMaker =
        new("spot-crypto-maker", 30m, 5m, 10m);

    /// <summary>
    /// US equities and ETFs on Alpaca: commission-free, with SEC and FINRA pass-through fees on
    /// sells only totalling well under a basis point. Verified against
    /// https://alpaca.markets/support/commission-clearing-fees and .../regulatory-fees. The
    /// quoted spread is measured live and added on top of this profile, not assumed here.
    /// </summary>
    public static readonly ExecutionCostProfile UsEquity =
        new("us-equity", 1m, 2m, 5m);

    /// <summary>
    /// US equity options on Alpaca: no per-contract commission, but OCC and regulatory pass-through
    /// fees of roughly $0.05 per contract, and option spreads are far wider than the underlying's.
    /// A two-leg vertical crosses two spreads on entry and two on exit, so the allowance is much
    /// larger than the equity profile. The live quoted spread is measured and added on top; this
    /// covers fees and the slippage a multi-leg fill typically suffers.
    /// </summary>
    public static readonly ExecutionCostProfile UsEquityOption =
        new("us-equity-option", 6m, 15m, 10m);

    /// <summary>Total hurdle for a given live spread, in basis points.</summary>
    public decimal HurdleBps(decimal spreadBps) =>
        spreadBps + RoundTripFeeBps + SlippageAllowanceBps + MinimumNetEdgeBps;
}
