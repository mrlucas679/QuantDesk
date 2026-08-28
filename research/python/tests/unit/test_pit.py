from datetime import UTC, datetime

import polars as pl
import pytest

from quantdesk_research.data.point_in_time import validate_no_lookahead


def test_validate_no_lookahead_success():
    df = pl.DataFrame(
        {
            "event_time": [
                datetime(2023, 1, 1, 10, 0, tzinfo=UTC),
                datetime(2023, 1, 1, 10, 5, tzinfo=UTC),
            ],
            "available_time": [
                datetime(2023, 1, 1, 10, 1, tzinfo=UTC),
                datetime(2023, 1, 1, 10, 6, tzinfo=UTC),
            ],
        }
    )
    assert validate_no_lookahead(df, "event_time", "available_time") is True


def test_validate_no_lookahead_failure():
    df = pl.DataFrame(
        {
            "event_time": [
                datetime(2023, 1, 1, 10, 0, tzinfo=UTC),
                datetime(2023, 1, 1, 10, 10, tzinfo=UTC),
            ],
            "available_time": [
                datetime(2023, 1, 1, 10, 1, tzinfo=UTC),
                datetime(2023, 1, 1, 10, 5, tzinfo=UTC),
            ],
        }
    )
    with pytest.raises(ValueError, match="LOOKAHEAD_DETECTED"):
        validate_no_lookahead(df, "event_time", "available_time")
