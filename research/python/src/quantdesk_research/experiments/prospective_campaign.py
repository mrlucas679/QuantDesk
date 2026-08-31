from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any


@dataclass(frozen=True)
class ProspectiveCampaign:
    campaign_id: str
    instrument: str
    timeframe: str
    registered_at: datetime
    holdout_start_exclusive: datetime
    source_dataset_hash: str
    minimum_unseen_bars: int
    round_trip_cost_bps: float
    minimum_trades: int
    minimum_sharpe: float
    required_lower_confidence_bps: float
    strategy_families: tuple[str, ...]
    holding_horizons_bars: tuple[int, ...]

    @classmethod
    def load(cls, path: Path) -> ProspectiveCampaign:
        """Load and validate a fixed campaign definition before reading its holdout."""
        payload: dict[str, Any] = json.loads(path.read_text(encoding="utf-8"))
        campaign = cls(
            campaign_id=str(payload["campaign_id"]),
            instrument=str(payload["instrument"]),
            timeframe=str(payload["timeframe"]),
            registered_at=datetime.fromisoformat(str(payload["registered_at"])),
            holdout_start_exclusive=datetime.fromisoformat(str(payload["holdout_start_exclusive"])),
            source_dataset_hash=str(payload["source_dataset_hash"]),
            minimum_unseen_bars=int(payload["minimum_unseen_bars"]),
            round_trip_cost_bps=float(payload["round_trip_cost_bps"]),
            minimum_trades=int(payload["minimum_trades"]),
            minimum_sharpe=float(payload["minimum_sharpe"]),
            required_lower_confidence_bps=float(payload["required_lower_confidence_bps"]),
            strategy_families=tuple(map(str, payload["strategy_families"])),
            holding_horizons_bars=tuple(map(int, payload["holding_horizons_bars"])),
        )
        campaign.validate()
        return campaign

    def validate(self) -> None:
        """Reject campaign definitions that could weaken prospective qualification."""
        if not self.campaign_id or self.instrument != "BTC/USD" or self.timeframe != "5Min":
            raise ValueError("Campaign identity or support domain is invalid.")
        if self.registered_at <= self.holdout_start_exclusive:
            raise ValueError("Campaign registration must occur after the immutable holdout cutoff.")
        if not self.source_dataset_hash.startswith("sha256:"):
            raise ValueError("Campaign must bind to a source dataset hash.")
        if self.minimum_unseen_bars < 8_640 or self.minimum_trades < 60:
            raise ValueError("Prospective evidence minimums cannot be weakened.")
        if self.round_trip_cost_bps < 60 or self.minimum_sharpe < 0.5:
            raise ValueError("Economic qualification gates cannot be weakened.")
        if self.required_lower_confidence_bps < 0:
            raise ValueError("The conservative net confidence bound must be non-negative.")
        if len(set(self.strategy_families)) != len(self.strategy_families):
            raise ValueError("Strategy families must be unique.")
        if not self.strategy_families or not self.holding_horizons_bars:
            raise ValueError("Campaign search space cannot be empty.")

    def fingerprint(self) -> str:
        """Return a stable fingerprint for audit and artifact binding."""
        canonical = json.dumps(
            {
                "campaign_id": self.campaign_id,
                "holding_horizons_bars": self.holding_horizons_bars,
                "holdout_start_exclusive": self.holdout_start_exclusive.isoformat(),
                "instrument": self.instrument,
                "minimum_sharpe": self.minimum_sharpe,
                "minimum_trades": self.minimum_trades,
                "minimum_unseen_bars": self.minimum_unseen_bars,
                "required_lower_confidence_bps": self.required_lower_confidence_bps,
                "round_trip_cost_bps": self.round_trip_cost_bps,
                "source_dataset_hash": self.source_dataset_hash,
                "strategy_families": self.strategy_families,
                "timeframe": self.timeframe,
            },
            separators=(",", ":"),
            sort_keys=True,
        ).encode("utf-8")
        return hashlib.sha256(canonical).hexdigest()

    def unseen_bar_count(self, bars: list[dict[str, Any]]) -> int:
        """Count only observations strictly after the preregistered cutoff."""
        return sum(
            datetime.fromisoformat(str(bar["t"])) > self.holdout_start_exclusive for bar in bars
        )

    def require_sufficient_unseen_data(self, bars: list[dict[str, Any]]) -> int:
        """Fail closed until the prospective sample reaches its fixed minimum."""
        count = self.unseen_bar_count(bars)
        if count < self.minimum_unseen_bars:
            raise ValueError(f"PROSPECTIVE_HOLDOUT_INSUFFICIENT:{count}/{self.minimum_unseen_bars}")
        return count


@dataclass(frozen=True)
class IndependentValidationCampaign:
    """A fixed historical cohort disjoint from every dataset used during strategy search."""

    campaign_id: str
    instrument: str
    timeframe: str
    registered_at: datetime
    validation_start_inclusive: datetime
    validation_end_exclusive: datetime
    prior_search_data_start: datetime
    minimum_validation_bars: int
    round_trip_cost_bps: float
    minimum_trades: int
    minimum_sharpe: float
    required_lower_confidence_bps: float
    strategy_families: tuple[str, ...]
    holding_horizons_bars: tuple[int, ...]
    prior_comparisons: int = 0

    @classmethod
    def load(cls, path: Path) -> IndependentValidationCampaign:
        """Load and validate the cohort declaration before its broker data is evaluated."""
        payload: dict[str, Any] = json.loads(path.read_text(encoding="utf-8"))
        campaign = cls(
            campaign_id=str(payload["campaign_id"]),
            instrument=str(payload["instrument"]),
            timeframe=str(payload["timeframe"]),
            registered_at=datetime.fromisoformat(str(payload["registered_at"])),
            validation_start_inclusive=datetime.fromisoformat(
                str(payload["validation_start_inclusive"])
            ),
            validation_end_exclusive=datetime.fromisoformat(
                str(payload["validation_end_exclusive"])
            ),
            prior_search_data_start=datetime.fromisoformat(
                str(payload["prior_search_data_start"])
            ),
            minimum_validation_bars=int(payload["minimum_validation_bars"]),
            round_trip_cost_bps=float(payload["round_trip_cost_bps"]),
            minimum_trades=int(payload["minimum_trades"]),
            minimum_sharpe=float(payload["minimum_sharpe"]),
            required_lower_confidence_bps=float(payload["required_lower_confidence_bps"]),
            strategy_families=tuple(map(str, payload["strategy_families"])),
            holding_horizons_bars=tuple(map(int, payload["holding_horizons_bars"])),
            prior_comparisons=int(payload.get("prior_comparisons", 0)),
        )
        campaign.validate()
        return campaign

    def validate(self) -> None:
        """Reject overlap, underpowered samples, or weakened economic gates."""
        supported_instruments = {"BTC/USD", "ETH/USD"}
        if (
            not self.campaign_id
            or self.instrument not in supported_instruments
            or self.timeframe != "5Min"
        ):
            raise ValueError("Independent campaign support domain is invalid.")
        if not (
            self.validation_start_inclusive
            < self.validation_end_exclusive
            <= self.prior_search_data_start
            < self.registered_at
        ):
            raise ValueError("Independent validation cohort overlaps searched evidence.")
        if self.minimum_validation_bars < 150_000 or self.minimum_trades < 60:
            raise ValueError("Independent validation evidence minimums cannot be weakened.")
        if self.round_trip_cost_bps < 60 or self.minimum_sharpe < 0.5:
            raise ValueError("Independent economic qualification gates cannot be weakened.")
        if self.required_lower_confidence_bps < 0:
            raise ValueError("Independent conservative confidence bound must be non-negative.")
        if not self.strategy_families or not self.holding_horizons_bars:
            raise ValueError("Independent campaign search space cannot be empty.")
        if len(set(self.strategy_families)) != len(self.strategy_families):
            raise ValueError("Independent strategy families must be unique.")
        if self.prior_comparisons < 0:
            raise ValueError("Prior comparison count cannot be negative.")

    def fingerprint(self) -> str:
        """Return a stable fingerprint binding the cohort and unchanged qualification gates."""
        return hashlib.sha256(
            json.dumps(
                {
                    key: value.isoformat() if isinstance(value, datetime) else value
                    for key, value in self.__dict__.items()
                },
                separators=(",", ":"),
                sort_keys=True,
            ).encode("utf-8")
        ).hexdigest()
