from typing import Any


def check_option_surface_sanity(quotes: list[dict[str, Any]]) -> dict[str, bool | list[str]]:
    """
    Static sanity and arbitrage diagnostics for an options surface.
    quotes: list of dictionaries with strike, call_price, put_price, type, etc.
    """
    alerts = []

    # 1. Monotonicity check
    # Call prices should decrease as strike increases
    # Put prices should increase as strike increases
    calls = sorted([q for q in quotes if q.get("type") == "call"], key=lambda x: x["strike"])
    puts = sorted([q for q in quotes if q.get("type") == "put"], key=lambda x: x["strike"])

    for i in range(1, len(calls)):
        if calls[i]["price"] >= calls[i - 1]["price"]:
            alerts.append(f"Call monotonicity violation at strike {calls[i]['strike']}")

    for i in range(1, len(puts)):
        if puts[i]["price"] <= puts[i - 1]["price"]:
            alerts.append(f"Put monotonicity violation at strike {puts[i]['strike']}")

    # 2. Convexity check (Butterfly spread must have non-negative value)
    for i in range(1, len(calls) - 1):
        strike_mid = calls[i]["strike"]
        strike_low = calls[i - 1]["strike"]
        strike_high = calls[i + 1]["strike"]

        # If strikes are equidistant
        if abs((strike_high - strike_mid) - (strike_mid - strike_low)) < 1e-6:
            butterfly_val = calls[i - 1]["price"] - 2 * calls[i]["price"] + calls[i + 1]["price"]
            if butterfly_val < -1e-6:
                alerts.append(f"Call convexity violation at strike {strike_mid}")

    return {"is_sane": len(alerts) == 0, "alerts": alerts}


def delta_hedged_scoring(option_pnl: float, underlying_pnl: float, delta: float) -> float:
    """
    Score the model based on delta-hedged performance to isolate surface edge
    from underlying direction.
    """
    return option_pnl - (delta * underlying_pnl)
