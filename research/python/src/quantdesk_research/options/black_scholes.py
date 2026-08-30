from typing import Literal

import numpy as np
from scipy.stats import norm  # type: ignore[import-untyped]  # scipy-stubs is not installed


def black_scholes_call(spot_price: float, strike_price: float, maturity_years: float, rate: float, volatility: float) -> float:
    d1 = (np.log(spot_price / strike_price) + (rate + 0.5 * volatility**2) * maturity_years) / (volatility * np.sqrt(maturity_years))
    d2 = d1 - volatility * np.sqrt(maturity_years)
    return float(spot_price * norm.cdf(d1) - strike_price * np.exp(-rate * maturity_years) * norm.cdf(d2))


def black_scholes_put(spot_price: float, strike_price: float, maturity_years: float, rate: float, volatility: float) -> float:
    d1 = (np.log(spot_price / strike_price) + (rate + 0.5 * volatility**2) * maturity_years) / (volatility * np.sqrt(maturity_years))
    d2 = d1 - volatility * np.sqrt(maturity_years)
    return float(strike_price * np.exp(-rate * maturity_years) * norm.cdf(-d2) - spot_price * norm.cdf(-d1))


def black_scholes_greeks(
    spot_price: float,
    strike_price: float,
    maturity_years: float,
    rate: float,
    volatility: float,
    option_type: Literal["call", "put"] = "call",
) -> dict[str, float]:
    d1 = (np.log(spot_price / strike_price) + (rate + 0.5 * volatility**2) * maturity_years) / (volatility * np.sqrt(maturity_years))
    d2 = d1 - volatility * np.sqrt(maturity_years)

    if option_type == "call":
        delta = norm.cdf(d1)
        theta = -(spot_price * norm.pdf(d1) * volatility) / (2 * np.sqrt(maturity_years)) - rate * strike_price * np.exp(-rate * maturity_years) * norm.cdf(
            d2
        )
        rho = strike_price * maturity_years * np.exp(-rate * maturity_years) * norm.cdf(d2)
    else:
        delta = norm.cdf(d1) - 1
        theta = -(spot_price * norm.pdf(d1) * volatility) / (2 * np.sqrt(maturity_years)) + rate * strike_price * np.exp(-rate * maturity_years) * norm.cdf(
            -d2
        )
        rho = -strike_price * maturity_years * np.exp(-rate * maturity_years) * norm.cdf(-d2)

    gamma = norm.pdf(d1) / (spot_price * volatility * np.sqrt(maturity_years))
    vega = spot_price * norm.pdf(d1) * np.sqrt(maturity_years)

    return {
        "delta": float(delta),
        "gamma": float(gamma),
        "vega": float(vega),
        "theta": float(theta),
        "rho": float(rho),
    }
