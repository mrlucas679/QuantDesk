from typing import Literal

OptionType = Literal["call", "put"]
PositionSide = Literal["long", "short"]


def calculate_payoff(
    spot_price: float,
    strike_price: float,
    option_type: OptionType = "call",
    side: PositionSide = "long",
) -> float:
    if option_type == "call":
        payoff = max(spot_price - strike_price, 0)
    else:
        payoff = max(strike_price - spot_price, 0)

    return payoff if side == "long" else -payoff


def bull_call_spread_payoff(
    spot_price: float, lower_strike: float, upper_strike: float, debit: float
) -> float:
    long_call = calculate_payoff(spot_price, lower_strike, "call", "long")
    short_call = calculate_payoff(spot_price, upper_strike, "call", "short")
    return long_call + short_call - debit


def bear_put_spread_payoff(
    spot_price: float, upper_strike: float, lower_strike: float, debit: float
) -> float:
    long_put = calculate_payoff(spot_price, upper_strike, "put", "long")
    short_put = calculate_payoff(spot_price, lower_strike, "put", "short")
    return long_put + short_put - debit
