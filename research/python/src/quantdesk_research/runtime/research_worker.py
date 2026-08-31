"""Container-owned, fail-closed validation worker for the research plane."""

import time
from dataclasses import dataclass, replace
from pathlib import Path

from loguru import logger

from quantdesk_research.contracts.model_artifact import EvidenceProfile
from quantdesk_research.data.orderbook_evidence import load_orderbook_evidence
from quantdesk_research.experiments.crypto_direction import (
    publish_validated_directional_forecast,
    run_rolling_experiment,
    run_rolling_persistence_baseline,
)
from quantdesk_research.experiments.strategy_ensemble import run_prospective_campaign


@dataclass(frozen=True)
class ValidationCandidate:
    """A bounded, evidence-declared research branch with its own immutable dataset."""

    manifest_name: str
    experiment_name: str
    horizon_bars: int
    strategy_family: str
    evidence_profile: EvidenceProfile
    round_trip_cost_bps: float = 60.0


VALIDATION_CANDIDATES = (
    ValidationCandidate(
        "latest-manifest.json",
        "crypto-btcusd-5min-direction",
        12,
        "price_volume_directional",
        EvidenceProfile(
            evidence_id="BTC-5MIN-UNSUPPORTED-001",
            economic_hypothesis="Short-horizon BTC price-volume features predict net returns.",
            counter_hypothesis="Short-horizon BTC price moves are noise after execution costs.",
            primary_evidence_ids=["local-alpaca-btcusd-5min"],
            transfer_grade="NotTransferable",
            transfer_reason="No primary evidence directly supports this venue and horizon.",
        ),
    ),
    ValidationCandidate(
        "latest-daily-manifest.json",
        "crypto-btcusd-daily-trend",
        1,
        "moving_average_trend",
        EvidenceProfile(
            evidence_id="BTC-DAILY-MOMENTUM-001",
            economic_hypothesis="BTC daily momentum can exceed local round-trip costs.",
            counter_hypothesis="Daily BTC momentum is regime-dependent and costs erase it.",
            primary_evidence_ids=["NBER-w24877"],
            transfer_grade="C_Weak",
            transfer_reason="Published daily evidence is not direct Alpaca execution evidence.",
        ),
    ),
)


def run_forever(data_root: Path, interval_seconds: int) -> None:
    """Continuously validate declared BTC/USD branches without promoting failures."""
    while True:
        validate_microstructure_evidence(data_root)
        for candidate in VALIDATION_CANDIDATES:
            validate_candidate(data_root, candidate)
        validate_prospective_campaign(data_root)
        time.sleep(interval_seconds)


def validate_prospective_campaign(data_root: Path) -> None:
    """Monitor the fixed multi-strategy cohort and fail closed until unseen evidence matures."""
    campaign_path = Path("/app/configs/prospective_strategy_campaign.json")
    try:
        results = run_prospective_campaign(data_root, campaign_path)
    except ValueError as error:
        logger.info("Prospective strategy campaign is not eligible: {}", error)
        return
    passed = [result for result in results if result.passed]
    if not passed:
        best = max(results, key=lambda result: result.score)
        logger.warning(
            "Prospective strategy campaign rejected: best={}, adjusted lower bound={} bps, trades={}.",
            best.name,
            best.lower_confidence_net_bps,
            best.trade_count,
        )
        return
    winner = max(passed, key=lambda result: result.score)
    logger.warning(
        "Prospective candidate {} passed statistical gates but remains unpromoted until its executable artifact bundle is built.",
        winner.name,
    )


def validate_microstructure_evidence(data_root: Path) -> None:
    """Keep the order-book branch visible while refusing to promote insufficient evidence."""
    try:
        records = load_orderbook_evidence(data_root, "BTC/USD", minimum_records=100_000)
    except ValueError as error:
        logger.warning("Microstructure branch remains SHADOW_ONLY: {}", error)
        return
    logger.info(
        "Microstructure evidence is complete enough for a bounded experiment: records={}",
        len(records),
    )


def validate_candidate(data_root: Path, candidate: ValidationCandidate) -> None:
    """Run one hypothesis against its matching baseline and promote only a verified winner."""
    manifest = data_root / candidate.manifest_name
    if not manifest.exists():
        logger.warning(
            "Research dataset {} is not available; retaining execution halt.",
            candidate.manifest_name,
        )
        return
    try:
        result = run_rolling_experiment(
            data_root=data_root,
            manifest_name=candidate.manifest_name,
            experiment_name=candidate.experiment_name,
            round_trip_cost_bps=candidate.round_trip_cost_bps,
            horizon_bars=candidate.horizon_bars,
        )
        baseline = run_rolling_persistence_baseline(
            data_root,
            candidate.round_trip_cost_bps,
            candidate.horizon_bars,
            candidate.manifest_name,
            candidate.experiment_name,
        )
        if (
            result.passed
            and result.test_lower_confidence_net_bps <= baseline.test_lower_confidence_net_bps
        ):
            logger.warning("Validation rejected: complex model did not beat the causal baseline.")
            result = replace(result, passed=False)
        if result.passed:
            if candidate.evidence_profile.transfer_grade not in {"A_Direct", "B_Close"}:
                logger.warning(
                    "Validation passed for {}, but evidence grade {} permits shadow research only.",
                    candidate.experiment_name,
                    candidate.evidence_profile.transfer_grade,
                )
                return
            publish_validated_directional_forecast(
                data_root,
                Path("/app/artifacts"),
                candidate.manifest_name,
                candidate.experiment_name,
                candidate.horizon_bars,
                result,
                candidate.evidence_profile,
                candidate.strategy_family,
            )
            logger.info("Validation passed and the verified contract bundle was promoted.")
        else:
            logger.warning(
                "Validation rejected for {}: lower confidence bound={} bps, trades={}.",
                candidate.experiment_name,
                result.test_lower_confidence_net_bps,
                result.test_trade_count,
            )
    # This is the process boundary: any unexpected validation failure must be
    # logged and fail closed so it cannot promote a model or submit an order.
    except Exception:  # noqa: BLE001
        logger.exception("Research validation failed closed; no model was promoted.")
