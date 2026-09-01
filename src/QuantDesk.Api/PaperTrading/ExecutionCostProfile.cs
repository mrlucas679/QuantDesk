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
/// <param name="MinimumVenueNotionalUsd">
/// Smallest order the venue will accept, kept clear of the exact minimum so quantity rounding
/// cannot push an order under it. This is a separate constraint from economic viability: one is
/// what the venue will physically accept, the other is what is worth trading once fees are paid.
/// An order must clear both.
/// </param>
/// <param name="FixedCostPerRoundTripUsd">
/// Costs that do not shrink with order size: per-contract option fees, and regulatory fees the
/// venue rounds up to the cent. Modelling these as basis points is wrong, because a fixed charge
/// is a larger and larger share of a smaller and smaller order until it exceeds the entire
/// expected edge. That is the mechanism by which a small trade becomes a guaranteed loss.
/// </param>
public sealed record ExecutionCostProfile(
    string AssetClass,
    decimal RoundTripFeeBps,
    decimal SlippageAllowanceBps,
    decimal MinimumNetEdgeBps,
    decimal FixedCostPerRoundTripUsd = 0m,
    decimal MinimumVenueNotionalUsd = 0m)
{
    /// <summary>
    /// <summary>
    /// The largest share of the expected gross edge that fixed costs may consume before an
    /// opportunity is refused as uneconomic. At 25% a trade still keeps three quarters of its
    /// edge; above that the order is mostly paying the broker.
    /// </summary>
    public const decimal MaximumFixedCostShareOfEdge = 0.25m;

    /// <summary>
    /// Alpaca spot crypto at tier 1: 0.25% taker per side, so 50 bps for a round trip before
    /// spread. Verified against https://docs.alpaca.markets/us/docs/crypto-fees.
    /// </summary>
    public static readonly ExecutionCostProfile SpotCryptoTaker =
        new("spot-crypto-taker", 50m, 10m, 10m, MinimumVenueNotionalUsd: 10m);

    /// <summary>
    /// Alpaca spot crypto at tier 1 paying the maker rate: 0.15% per side, 30 bps round trip.
    /// Only valid for a lane that actually rests limit orders; a marketable order pays taker.
    /// </summary>
    public static readonly ExecutionCostProfile SpotCryptoMaker =
        new("spot-crypto-maker", 30m, 5m, 10m, MinimumVenueNotionalUsd: 10m);

    /// <summary>
    /// Conservative qualification scenario. It deliberately exceeds the expected taker cost and
    /// is the only crypto profile that can qualify a strategy; maker and realised profiles are
    /// execution observations, never substitutes for this stress evidence.
    /// </summary>
    public static readonly ExecutionCostProfile SpotCryptoConservativeStress =
        new("spot-crypto-conservative-stress", 50m, 20m, 15m, MinimumVenueNotionalUsd: 10m);

    /// <summary>
    /// Builds a separately labelled realised-cost observation from completed PAPER fills. The
    /// caller must retain the evidence identifier; this profile is reporting evidence only and
    /// must not be used to relax conservative strategy qualification.
    /// </summary>
    public static ExecutionCostProfile ObservedRealisedCrypto(
        decimal roundTripFeeBps, decimal realisedSlippageBps, string evidenceId)
    {
        if (roundTripFeeBps < 0 || realisedSlippageBps < 0 || string.IsNullOrWhiteSpace(evidenceId))
            throw new ArgumentException("Observed crypto costs require non-negative values and evidence identity.");
        return new($"spot-crypto-observed-realised:{evidenceId.Trim()}",
            roundTripFeeBps, realisedSlippageBps, 0m);
    }

    /// <summary>
    /// US equities and ETFs on Alpaca: commission-free, with SEC and FINRA pass-through fees on
    /// sells only totalling well under a basis point. Verified against
    /// https://alpaca.markets/support/commission-clearing-fees and .../regulatory-fees. The
    /// quoted spread is measured live and added on top of this profile, not assumed here.
    /// </summary>
    /// <summary>
    /// Commission-free, but the SEC fee is rounded up to the cent on the sell, which is a floor
    /// rather than a rate. On a $20 order that rounding alone is 5 bps.
    /// </summary>
    public static readonly ExecutionCostProfile UsEquity =
        new("us-equity", 1m, 2m, 5m, FixedCostPerRoundTripUsd: 0.01m, MinimumVenueNotionalUsd: 1m);

    /// <summary>
    /// US equity options on Alpaca: no per-contract commission, but OCC and regulatory pass-through
    /// fees of roughly $0.05 per contract, and option spreads are far wider than the underlying's.
    /// A two-leg vertical crosses two spreads on entry and two on exit, so the allowance is much
    /// larger than the equity profile. The live quoted spread is measured and added on top; this
    /// covers fees and the slippage a multi-leg fill typically suffers.
    /// </summary>
    /// <summary>
    /// A two-leg vertical pays OCC clearing and regulatory fees per contract per side. At roughly
    /// five cents a contract that is about twenty cents for a one-lot round trip across both legs
    /// — small in absolute terms, and decisive for small orders: on a $50 spread it is 40 bps,
    /// against 6 bps on a $327 one.
    /// </summary>
    public static readonly ExecutionCostProfile UsEquityOption =
        new("us-equity-option", 6m, 15m, 10m, FixedCostPerRoundTripUsd: 0.20m, MinimumVenueNotionalUsd: 25m);

    /// <summary>Proportional hurdle for a given live spread, in basis points.</summary>
    public decimal HurdleBps(decimal spreadBps) =>
        spreadBps + RoundTripFeeBps + SlippageAllowanceBps + MinimumNetEdgeBps;

    /// <summary>Total modelled round-trip cost in dollars at a given order size.</summary>
    public decimal RoundTripCostUsd(decimal notional, decimal spreadBps) =>
        notional * (spreadBps + RoundTripFeeBps + SlippageAllowanceBps) / 10_000m
        + FixedCostPerRoundTripUsd;

    /// <summary>
    /// The smallest order size at which fixed costs stay within
    /// <see cref="MaximumFixedCostShareOfEdge"/> of the expected gross edge.
    ///
    /// Below this size the trade pays the broker more than it can plausibly win, whatever the
    /// direction. Returns zero when the profile carries no fixed cost, because a purely
    /// proportional cost is scale-invariant and no minimum applies.
    /// </summary>
    public decimal MinimumViableNotionalUsd(decimal expectedGrossEdgeBps)
    {
        if (FixedCostPerRoundTripUsd <= 0m) return 0m;
        if (expectedGrossEdgeBps <= 0m) return decimal.MaxValue;
        decimal edgeFraction = expectedGrossEdgeBps / 10_000m;
        return decimal.Round(
            FixedCostPerRoundTripUsd / (MaximumFixedCostShareOfEdge * edgeFraction),
            2, MidpointRounding.ToPositiveInfinity);
    }

    /// <summary>
    /// Whether an order of this size can pay its own costs and still keep most of its edge.
    /// </summary>
    public bool IsEconomicallyViable(
        decimal notional, decimal expectedGrossEdgeBps, decimal spreadBps, out string reason)
    {
        if (notional <= 0m)
        {
            reason = "NotionalNotPositive";
            return false;
        }

        if (expectedGrossEdgeBps <= 0m)
        {
            reason = "NoExpectedEdge";
            return false;
        }

        decimal grossEdgeUsd = notional * expectedGrossEdgeBps / 10_000m;
        decimal costUsd = RoundTripCostUsd(notional, spreadBps);
        if (costUsd >= grossEdgeUsd)
        {
            reason = "CostExceedsExpectedEdge";
            return false;
        }

        if (notional < MinimumVenueNotionalUsd)
        {
            reason = "NotionalBelowVenueMinimum";
            return false;
        }

        decimal minimum = MinimumViableNotionalUsd(expectedGrossEdgeBps);
        if (notional < minimum)
        {
            reason = "NotionalBelowMinimumViable";
            return false;
        }

        reason = "Viable";
        return true;
    }
}
