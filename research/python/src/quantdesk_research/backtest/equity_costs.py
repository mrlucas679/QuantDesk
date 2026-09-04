"""Round-trip cost scenarios for US equities and for Alpaca spot crypto.

The equity numbers were corrected on 2026-08-31. The previous BASE scenario charged 25 bps
round trip (20 bps spread-and-slippage plus 5 bps regulatory) to SPY, QQQ, IWM, and DIA. That
is roughly eight times the achievable cost for those four names and it silently rejected every
candidate whose real edge was smaller than the modelling error.

Derivation, from Alpaca's published schedule (see references below):

* Commission: **zero**. Alpaca charges no commission for US listed equities and ETFs traded
  through the API.
* Regulatory: SEC Section 31 and FINRA TAF are pass-through and apply to **sells only**. At the
  current SEC rate the Section 31 fee is on the order of 0.3 bps of sell principal, and the TAF
  is $0.000166 per share — under 0.01 bps on a $500 share. Together with the CAT fee they round
  to well under 1 bp per round trip.
* Spread: SPY, QQQ, IWM, and DIA quote a one-cent spread almost continuously. On share prices
  in the $240-$650 range one cent is 0.15-0.4 bps, so crossing the full spread on both legs of a
  round trip costs roughly 0.3-0.8 bps.
* Slippage: for the small notional this system trades, a marketable order in these four names
  fills at or inside the touch. One to two bps round trip is already a pessimistic allowance.

An honest BASE round trip is therefore near 2 bps. The scenarios below deliberately sit above
that so qualification stays conservative: BASE charges 5 bps, roughly double the achievable
cost; STRESS charges 10 bps; SEVERE charges 20 bps, which still exceeds anything these four
names have plausibly cost. Overstating cost is not conservatism — it is a modelling error that
rejects real edges, exactly as understating it would accept false ones.

These scenarios apply to large, penny-spread US ETFs. Do not reuse them for thin single names
without re-deriving the spread term.

References:
* https://alpaca.markets/support/commission-clearing-fees
* https://alpaca.markets/support/regulatory-fees
* https://docs.alpaca.markets/us/docs/crypto-fees
"""

from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class EquityCostScenario:
    """Round-trip assumptions for small orders in penny-spread US equity ETFs."""

    name: str
    spread_and_slippage_bps: float
    regulatory_and_rounding_bps: float
    commission_bps: float

    @property
    def round_trip_bps(self) -> float:
        """Return the all-in round-trip cost in basis points."""
        return (
            self.spread_and_slippage_bps
            + self.regulatory_and_rounding_bps
            + self.commission_bps
        )

    @property
    def one_way_bps(self) -> float:
        """Return the cost of a single side, for turnover-weighted portfolio charging."""
        return self.round_trip_bps / 2.0

    def net_return(self, gross_return: float) -> float:
        """Deduct one modeled round trip from a decimal gross return."""
        return gross_return - self.round_trip_bps / 10_000


BASE_COST = EquityCostScenario(
    name="BASE",
    spread_and_slippage_bps=4.0,
    regulatory_and_rounding_bps=1.0,
    commission_bps=0.0,
)
STRESS_COST = EquityCostScenario(
    name="STRESS",
    spread_and_slippage_bps=9.0,
    regulatory_and_rounding_bps=1.0,
    commission_bps=0.0,
)
SEVERE_COST = EquityCostScenario(
    name="SEVERE",
    spread_and_slippage_bps=19.0,
    regulatory_and_rounding_bps=1.0,
    commission_bps=0.0,
)

COST_SCENARIOS = (BASE_COST, STRESS_COST, SEVERE_COST)


# Alpaca spot crypto, tier 1 (under $100k of 30-day volume): 0.25% taker and 0.15% maker per
# side. A taker round trip therefore costs 50 bps in fees alone before any spread. This is why
# every short-horizon BTC and ETH campaign in this repository's failure ledger lost roughly the
# cost allowance: the venue fee, not the signal, dominated the result. Any crypto hypothesis has
# to clear a hurdle an order of magnitude above the equity one, so it needs either a much larger
# per-trade edge or a holding period long enough to amortise 50 bps.
CRYPTO_TAKER_ROUND_TRIP_BPS = 50.0
CRYPTO_MAKER_ROUND_TRIP_BPS = 30.0

# Measured, not modelled. On 2026-09-01, 59 bounded BTC/USD round trips were executed against the
# Alpaca paper venue and the cost was read from account equity before and after each one, so it owes
# nothing to a fee schedule:
#
#   fill-price P&L ("gross")            11 bps   -- omits the fee entirely
#   fill-derived P&L net of in-kind fee 36 bps   -- misses the separate USD cash charge
#   account equity delta                68 bps   -- ground truth
#
# The taker constant above understates the realised cost by 18 bps because the venue charges both an
# in-kind quantity deduction *and* a "Coin Pair Transaction Fee (USD)" cash charge, and neither the
# fill price nor the filled quantity reveals the second one.
#
# Use this for any crypto hypothesis that intends to trade. The 50 bps figure remains as the
# documented schedule rate; this is what the schedule actually costs in practice.
CRYPTO_TAKER_ROUND_TRIP_BPS_MEASURED = 68.0
CRYPTO_MEASURED_PROVENANCE = (
    "59 BTC/USD paper round trips, 2026-09-01, cost taken from Alpaca account equity per trip"
)
