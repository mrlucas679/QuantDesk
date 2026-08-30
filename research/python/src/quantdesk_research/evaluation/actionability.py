from loguru import logger


class ActionabilityGate:
    def __init__(self, fee_bps: float = 1.0, slippage_bps: float = 1.0) -> None:
        self.fee_bps = fee_bps
        self.slippage_bps = slippage_bps

    def is_actionable(self, forecast_value_bps: float, turnover: float) -> bool:
        """
        Check if the forecast is actionable after costs.
        turnover: expected turnover per trade (e.g., 2.0 for a full roundtrip)
        """
        total_cost_bps = (self.fee_bps + self.slippage_bps) * turnover
        net_value = forecast_value_bps - total_cost_bps

        if net_value <= 0:
            logger.warning(
                f"Forecast not actionable: value={forecast_value_bps}bps, cost={total_cost_bps}bps"
            )
            return False
        return True


class EconomicUtility:
    @staticmethod
    def calculate_utility(pnl: float, max_drawdown: float, turnover: float) -> dict[str, float]:
        return {
            "pnl": pnl,
            "max_drawdown": max_drawdown,
            "turnover": turnover,
            "return_on_turnover": pnl / turnover if turnover > 0 else 0,
        }
