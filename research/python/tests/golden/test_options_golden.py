from quantdesk_research.options.black_scholes import black_scholes_call, black_scholes_put
from quantdesk_research.options.payoff import bull_call_spread_payoff


def test_black_scholes_golden():
    # S=100, K=100, T=1, r=0.05, sigma=0.2
    # Known values from standard calculators
    call = black_scholes_call(100, 100, 1, 0.05, 0.2)
    put = black_scholes_put(100, 100, 1, 0.05, 0.2)

    assert abs(call - 10.4505) < 1e-4
    assert abs(put - 5.5735) < 1e-4


def test_bull_call_spread_payoff_golden():
    # Long 100C @ 6, Short 110C @ 2
    # Debit = 4, Max Loss = 4, Max Profit = 6
    # Breakeven = 104

    # At S=90 (below lower strike)
    assert bull_call_spread_payoff(90, 100, 110, 4) == -4.0

    # At S=104 (breakeven)
    assert bull_call_spread_payoff(104, 100, 110, 4) == 0.0

    # At S=120 (above upper strike)
    assert bull_call_spread_payoff(120, 100, 110, 4) == 6.0
