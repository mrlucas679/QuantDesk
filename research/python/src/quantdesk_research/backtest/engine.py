from typing import Any

import polars as pl
from loguru import logger

from quantdesk_research.backtest.fill_models import FillModel
from quantdesk_research.backtest.transaction_costs import TransactionCostModel
from quantdesk_research.evaluation.metrics import calculate_max_drawdown, calculate_sharpe_ratio

EventRecord = dict[str, Any]
BacktestReport = dict[str, Any]


class BacktestEngine:
    def __init__(
        self,
        initial_cash: float = 100000.0,
        cost_model: TransactionCostModel | None = None,
        fill_model: FillModel | None = None,
        deterministic: bool = True,
    ) -> None:
        self.initial_cash = initial_cash
        self.cash = initial_cash
        self.positions: dict[str, float] = {}  # instrument -> quantity
        self.average_prices: dict[str, float] = {}  # instrument -> avg_price
        self.current_prices: dict[str, float] = {}  # instrument -> last_price
        self.cost_model = cost_model
        self.fill_model = fill_model
        self.deterministic = deterministic
        self.trades: list[EventRecord] = []
        self.equity_curve: list[EventRecord] = []
        self.realized_pnl = 0.0

    def run(self, events: pl.DataFrame) -> BacktestReport:
        """
        Deterministic event-based simulation.
        events: must contain 'timestamp', 'type', 'symbol', 'price' (or 'bid'/'ask')
        Supported types: 'bar', 'quote', 'signal'
        """
        # Ensure strict ordering: bars/quotes before signals at same timestamp
        # to simulate that signal can use latest market data but execution
        # might need to happen at next available price or with fill model.
        # However, to avoid leakage, we should be careful.

        # We'll use a secondary sort key to ensure deterministic behavior
        event_priority = {"bar": 0, "quote": 1, "signal": 2}

        def event_priority_for(event_type: object) -> int:
            return event_priority.get(str(event_type), 9)

        sorted_events = events.with_columns(
            pl.col("type")
            .map_elements(event_priority_for, return_dtype=pl.Int32)
            .alias("priority")
        ).sort(["timestamp", "priority"])

        for row in sorted_events.to_dicts():
            self._handle_event(row)
            self._record_state(row["timestamp"])

        return self._generate_report()

    def _handle_event(self, event: EventRecord) -> None:
        symbol = event.get("symbol")
        event_type = event.get("type")

        if symbol is None or event_type is None:
            return

        # Update current price for valuation
        if "price" in event and event["price"] is not None:
            self.current_prices[symbol] = event["price"]
        elif "ask" in event and "bid" in event:
            self.current_prices[symbol] = (event["ask"] + event["bid"]) / 2.0

        if event_type == "signal":
            self._execute_signal(event)

    def _execute_signal(self, signal: EventRecord) -> None:
        symbol = signal["symbol"]
        target_qty = signal["quantity"]
        current_qty = self.positions.get(symbol, 0.0)
        trade_qty = target_qty - current_qty

        if trade_qty == 0:
            return

        # Deterministic fill price logic
        bid = signal.get("bid")
        ask = signal.get("ask")
        price = signal.get("price")
        side = "buy" if trade_qty > 0 else "sell"

        fill_price = None
        if self.fill_model and bid is not None and ask is not None:
            fill_price = self.fill_model.get_fill_price(bid, ask, side)
        elif price is not None:
            fill_price = price

        if fill_price is None:
            # Fallback to current market price if signal lacks it
            fill_price = self.current_prices.get(symbol)

        if fill_price is None:
            logger.warning(f"No price available for {symbol} at {signal['timestamp']}")
            return

        # Calculate cost
        cost = 0.0

        # Determine actual fill quantity
        fill_qty = trade_qty
        if self.fill_model:
            fill_qty = self.fill_model.get_fill_quantity(trade_qty, bid=bid, ask=ask, price=price)

        if fill_qty == 0:
            return

        if self.cost_model:
            cost = self.cost_model.calculate_cost(fill_qty, fill_price, symbol=symbol)

        # Update cash and positions
        trade_value = fill_qty * fill_price

        # Realized P&L calculation
        if fill_qty < 0 and current_qty > 0:  # Selling and closing long
            qty_closed = min(abs(fill_qty), current_qty)
            self.realized_pnl += qty_closed * (fill_price - self.average_prices.get(symbol, 0.0))
        elif fill_qty > 0 and current_qty < 0:  # Buying and closing short
            qty_closed = min(fill_qty, abs(current_qty))
            self.realized_pnl += qty_closed * (self.average_prices.get(symbol, 0.0) - fill_price)

        self.cash -= trade_value + cost

        # Update average price
        old_qty = self.positions.get(symbol, 0.0)
        new_qty = old_qty + fill_qty

        if new_qty != 0:
            if (old_qty > 0 and new_qty > 0) or (old_qty < 0 and new_qty < 0):
                # Increasing position
                if abs(new_qty) > abs(old_qty):
                    old_cost = old_qty * self.average_prices.get(symbol, 0.0)
                    self.average_prices[symbol] = (old_cost + trade_value) / new_qty
            elif old_qty == 0:
                self.average_prices[symbol] = fill_price
            # If flipping or decreasing, average price stays same for the remaining portion
        else:
            self.average_prices[symbol] = 0.0

        self.positions[symbol] = new_qty

        self.trades.append(
            {
                "timestamp": signal["timestamp"],
                "symbol": symbol,
                "quantity": fill_qty,
                "price": fill_price,
                "cost": cost,
                "side": side,
                "realized_pnl": self.realized_pnl,
            }
        )

    def _record_state(self, timestamp: object) -> None:
        # Value positions
        unrealized_pnl = 0.0
        for symbol, qty in self.positions.items():
            if qty != 0:
                current_price = self.current_prices.get(symbol)
                if current_price:
                    unrealized_pnl += qty * (current_price - self.average_prices.get(symbol, 0.0))

        total_equity = self.cash + sum(
            qty * self.current_prices.get(symbol, 0.0)
            for symbol, qty in self.positions.items()
            if symbol in self.current_prices
        )

        self.equity_curve.append(
            {
                "timestamp": timestamp,
                "cash": self.cash,
                "equity": total_equity,
                "positions": self.positions.copy(),
                "realized_pnl": self.realized_pnl,
                "unrealized_pnl": unrealized_pnl,
            }
        )

    def _generate_report(self) -> BacktestReport:
        final_equity = self.equity_curve[-1]["equity"] if self.equity_curve else self.initial_cash

        # Calculate metrics
        equity_series = pl.Series([s["equity"] for s in self.equity_curve])
        returns = equity_series.pct_change().fill_null(0.0)

        sharpe = calculate_sharpe_ratio(returns)
        mdd = calculate_max_drawdown(equity_series)

        return {
            "initial_cash": self.initial_cash,
            "final_equity": final_equity,
            "total_pnl": final_equity - self.initial_cash,
            "realized_pnl": self.realized_pnl,
            "sharpe_ratio": sharpe,
            "max_drawdown": mdd,
            "trade_count": len(self.trades),
            "trades": self.trades,
            "equity_curve": self.equity_curve,
        }
