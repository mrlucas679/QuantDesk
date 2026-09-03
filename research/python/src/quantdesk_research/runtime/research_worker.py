"""Container-owned, fail-closed validation worker for the research plane."""

import json
import os
import time
from dataclasses import asdict, dataclass, replace
from datetime import UTC, datetime
from pathlib import Path
from typing import Any
from uuid import uuid4

from loguru import logger

from quantdesk_research.contracts.model_artifact import EvidenceProfile
from quantdesk_research.data.orderbook_evidence import load_orderbook_evidence
from quantdesk_research.evaluation.hypothesis_memory import (
    HypothesisMemory,
    RejectedHypothesis,
    classify_failure,
)
from quantdesk_research.experiments.crypto_direction import (
    publish_validated_directional_forecast,
    run_rolling_experiment,
    run_rolling_persistence_baseline,
)
from quantdesk_research.experiments.prospective_campaign import IndependentValidationCampaign
from quantdesk_research.experiments.strategy_ensemble import (
    run_independent_validation_campaign,
    run_prospective_campaign,
)
from quantdesk_research.runtime.model_fitting import (
    ModelFittingSkipped,
    publish_fitted_models,
)


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


@dataclass(frozen=True)
class IndependentValidationRegistration:
    """Binds a frozen campaign to its one permitted untouched broker cohort."""

    campaign_file: str
    manifest_name: str


INDEPENDENT_VALIDATIONS = (
    IndependentValidationRegistration(
        "independent_strategy_validation_campaign.json",
        "independent-validation-manifest.json",
    ),
    IndependentValidationRegistration(
        "literature_momentum_confirmation_campaign.json",
        "final-validation-manifest.json",
    ),
    IndependentValidationRegistration(
        "eth_transfer_validation_campaign.json",
        "eth-transfer-validation-manifest.json",
    ),
    IndependentValidationRegistration(
        "mechanism_state_validation_campaign.json",
        "independent-validation-manifest.json",
    ),
)


def run_forever(data_root: Path, interval_seconds: int) -> None:
    """Continuously validate declared BTC/USD branches without promoting failures."""
    while True:
        run_cycle(data_root, Path("/app/configs"), Path("/app/artifacts"))
        time.sleep(interval_seconds)


def run_cycle(data_root: Path, configs_root: Path, artifacts_root: Path) -> None:
    """Run one bounded worker cycle for deterministic orchestration and verification."""
    fit_models(data_root, artifacts_root)
    validate_microstructure_evidence(data_root)
    for candidate in VALIDATION_CANDIDATES:
        validate_candidate(data_root, candidate)
    validate_prospective_campaign(data_root)
    for registration in INDEPENDENT_VALIDATIONS:
        validate_independent_campaign(data_root, configs_root, artifacts_root, registration)


def fit_models(data_root: Path, artifacts_root: Path) -> None:
    """Fit the models the runtime can load, and put them where it looks.

    Before this, the loop validated rule-based campaigns and fitted nothing, so the directory the
    execution plane watches for models stayed empty however complete the bridge on either side of
    it was.

    A refusal here is not a failure of the cycle. The gates a fit can miss -- non-convergence, a
    GARCH persistence at or above one, too little history, a dataset already published -- are the
    ones that keep an unusable model out of the runtime, and a cycle that stopped on one would stop
    the campaign validation that has nothing to do with it.
    """
    try:
        published = publish_fitted_models(data_root, artifacts_root)
    except ModelFittingSkipped as skipped:
        logger.info("Model fitting produced nothing this cycle: {}", skipped)
        return

    logger.info(
        "Published {} fitted model(s) for dataset {}: {}",
        len(published.written), published.dataset_hash[:16], ", ".join(published.written))
    for family, reason in published.skipped.items():
        logger.info("Model family {} was not published: {}", family, reason)


def validate_independent_campaign(
    data_root: Path,
    configs_root: Path,
    artifacts_root: Path,
    registration: IndependentValidationRegistration,
) -> dict[str, Any] | None:
    """Evaluate an untouched campaign once and durably reuse its immutable outcome."""
    campaign_path = configs_root / registration.campaign_file
    manifest_path = data_root / registration.manifest_name
    if not campaign_path.exists():
        logger.info(
            "Independent validation is waiting for campaign={}.",
            registration.campaign_file,
        )
        return None
    try:
        campaign = IndependentValidationCampaign.load(campaign_path)
        fingerprint = campaign.fingerprint()
        outcome_path = (
            artifacts_root
            / "independent-validation"
            / f"{campaign.campaign_id}-{fingerprint}.json"
        )
        if outcome_path.exists():
            loaded_outcome: Any = json.loads(outcome_path.read_text(encoding="utf-8"))
            if not isinstance(loaded_outcome, dict):
                raise ValueError("INDEPENDENT_OUTCOME_INVALID")
            outcome: dict[str, Any] = {
                str(key): value for key, value in loaded_outcome.items()
            }
            if outcome.get("campaign_fingerprint") != fingerprint:
                raise ValueError("INDEPENDENT_OUTCOME_FINGERPRINT_MISMATCH")
            logger.info(
                "Independent validation outcome already frozen: campaign={} passed={}.",
                campaign.campaign_id,
                outcome.get("passed"),
            )
            _remember_rejections(data_root, campaign, outcome)
            return outcome

        if not manifest_path.exists():
            logger.info(
                "Independent validation is waiting for manifest={}.",
                registration.manifest_name,
            )
            return None

        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        results = run_independent_validation_campaign(
            data_root,
            campaign_path,
            registration.manifest_name,
        )
        outcome = {
            "campaign_id": campaign.campaign_id,
            "campaign_fingerprint": fingerprint,
            "dataset_hash": manifest.get("sha256"),
            "evaluated_at": datetime.now(UTC).isoformat(),
            "passed": any(result.passed for result in results),
            "prior_comparisons": campaign.prior_comparisons,
            "results": [asdict(result) for result in results],
        }
        _write_json_once(outcome_path, outcome)
        _remember_rejections(data_root, campaign, outcome)
        logger.warning(
            "Independent validation frozen: campaign={} passed={}; publication requires a complete executable evidence bundle.",
            campaign.campaign_id,
            outcome["passed"],
        )
        return outcome
    except ValueError as error:
        logger.warning(
            "Independent validation failed closed for {}: {}",
            registration.campaign_file,
            error,
        )
        return None


def _remember_rejections(
    data_root: Path,
    campaign: IndependentValidationCampaign,
    outcome: dict[str, Any],
) -> None:
    """Turn each frozen failure into typed input for the next research action."""
    memory = HypothesisMemory(data_root / "experiments.db")
    results = outcome.get("results")
    if not isinstance(results, list):
        raise TypeError("INDEPENDENT_OUTCOME_RESULTS_INVALID")
    for item in results:
        if not isinstance(item, dict) or item.get("passed") is True:
            continue
        name = str(item.get("name", "unknown"))
        trade_count = int(item.get("trade_count", 0))
        net_mean = float(item.get("mean_net_bps", -1_000_000))
        reason = classify_failure(
            gross_mean_bps=net_mean + campaign.round_trip_cost_bps,
            net_mean_bps=net_mean,
            trade_count=trade_count,
            minimum_trades=campaign.minimum_trades,
        )
        memory.record(
            RejectedHypothesis(
                hypothesis_id=f"{campaign.campaign_id}:{name}",
                mechanism=f"Preregistered {name} mechanism",
                reason=reason,
                dataset_hash=str(outcome.get("dataset_hash", "unknown")),
                parameters={
                    "strategy": name,
                    "round_trip_cost_bps": campaign.round_trip_cost_bps,
                    "prior_comparisons": campaign.prior_comparisons,
                },
                evidence={str(key): value for key, value in item.items()},
                regime="all",
                cost_scenario=f"conservative-{campaign.round_trip_cost_bps:g}bps",
            )
        )


def _write_json_once(path: Path, document: dict[str, Any]) -> None:
    """Commit a validation outcome atomically without replacing prior evidence."""
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.parent / f".{path.name}.{uuid4().hex}.tmp"
    temporary.write_text(json.dumps(document, sort_keys=True), encoding="utf-8")
    try:
        os.link(temporary, path)
    except FileExistsError:
        pass
    finally:
        temporary.unlink(missing_ok=True)


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
