import polars as pl
import pytest

from quantdesk_research.features.microstructure import (
    calculate_order_book_imbalance,
    calculate_relative_spread,
)


def test_order_book_imbalance_is_bounded_and_directional() -> None:
    frame = pl.DataFrame({"bid_depth": [3.0, 0.0], "ask_depth": [1.0, 4.0]})

    result = calculate_order_book_imbalance(frame, "bid_depth", "ask_depth")

    assert result["obi"].to_list()[0] == pytest.approx(0.5)
    assert result["obi"].to_list()[1] == pytest.approx(-1.0)
    assert all(-1.0 <= value <= 1.0 for value in result["obi"].to_list())


def test_relative_spread_uses_midpoint() -> None:
    frame = pl.DataFrame({"bid": [100.0], "ask": [101.0]})

    result = calculate_relative_spread(frame, "bid", "ask")

    assert result["relative_spread"].item() == pytest.approx(1 / 100.5)
