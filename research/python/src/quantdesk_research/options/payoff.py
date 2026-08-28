def calculate_payoff(S, K, option_type="call", side="long"):
    if option_type == "call":
        payoff = max(S - K, 0)
    else:
        payoff = max(K - S, 0)

    return payoff if side == "long" else -payoff


def bull_call_spread_payoff(S, lower_K, upper_K, debit):
    long_call = calculate_payoff(S, lower_K, "call", "long")
    short_call = calculate_payoff(S, upper_K, "call", "short")
    return long_call + short_call - debit


def bear_put_spread_payoff(S, upper_K, lower_K, debit):
    long_put = calculate_payoff(S, upper_K, "put", "long")
    short_put = calculate_payoff(S, lower_K, "put", "short")
    return long_put + short_put - debit
