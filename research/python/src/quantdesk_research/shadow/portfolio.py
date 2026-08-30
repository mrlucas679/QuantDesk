from typing import Any, TypedDict


class ReconstructedPortfolio(TypedDict):
    holdings: dict[str, float]
    average_prices: dict[str, float]
    realized_pnl: float


class PortfolioReconstructor:
    def __init__(self) -> None:
        self.holdings: dict[str, float] = {}
        self.average_prices: dict[str, float] = {}
        self.realized_pnl = 0.0

    def apply_trade(self, symbol: str, quantity: float, price: float) -> None:
        old_qty = self.holdings.get(symbol, 0.0)
        new_qty = old_qty + quantity

        if quantity > 0:  # Buying
            if old_qty >= 0:  # Increasing long or opening long
                total_cost = old_qty * self.average_prices.get(symbol, 0.0) + quantity * price
                self.average_prices[symbol] = total_cost / new_qty
            else:  # Covering short
                covered_qty = min(abs(old_qty), quantity)
                pnl = covered_qty * (self.average_prices.get(symbol, 0.0) - price)
                self.realized_pnl += pnl

                if quantity > abs(old_qty):  # Reversing to long
                    self.average_prices[symbol] = price
                elif new_qty == 0:
                    self.average_prices[symbol] = 0.0
        else:  # Selling
            if old_qty <= 0:  # Increasing short or opening short
                total_proceeds = (
                    abs(old_qty) * self.average_prices.get(symbol, 0.0) + abs(quantity) * price
                )
                self.average_prices[symbol] = total_proceeds / abs(new_qty)
            else:  # Closing long
                closed_qty = min(old_qty, abs(quantity))
                pnl = closed_qty * (price - self.average_prices.get(symbol, 0.0))
                self.realized_pnl += pnl

                if abs(quantity) > old_qty:  # Reversing to short
                    self.average_prices[symbol] = price
                elif new_qty == 0:
                    self.average_prices[symbol] = 0.0

        self.holdings[symbol] = new_qty
        if abs(self.holdings[symbol]) < 1e-10:
            self.holdings[symbol] = 0.0
            self.average_prices[symbol] = 0.0

    def reconstruct_from_events(self, events: list[dict[str, Any]]) -> dict[str, float]:
        for event in events:
            if event["type"] == "trade":
                self.apply_trade(event["symbol"], event["quantity"], event.get("price", 0.0))
        return self.holdings

    def reconstruct_detailed_from_events(
        self, events: list[dict[str, Any]]
    ) -> ReconstructedPortfolio:
        self.holdings = {}
        self.average_prices = {}
        self.realized_pnl = 0.0

        for event in events:
            if event["type"] == "trade":
                self.apply_trade(event["symbol"], event["quantity"], event.get("price", 0.0))

        return {
            "holdings": self.holdings.copy(),
            "average_prices": self.average_prices.copy(),
            "realized_pnl": self.realized_pnl,
        }
