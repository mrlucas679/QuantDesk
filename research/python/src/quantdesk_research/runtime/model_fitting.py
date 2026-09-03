"""Fits the models the runtime can load, and publishes them where it looks.

Why this did not exist
---------------------
Both ends of the model bridge were built and never joined. ``ContractPublisher`` gained a
``publish_model``; nothing called it. The runtime watches a directory that is a read-only mount of
the volume this worker writes to; nothing put a fitted model in it. So a verified two-language
inference path could not affect a single decision, and the loop that runs every few minutes fitted
nothing at all.

The feature definition has to match, exactly
--------------------------------------------
The runtime's volatility expert computes realised variance as the mean squared log return over the
last N bars, at N = 12, 60 and 288. The HAR implementation here fitted 1 / 5 / 22 -- the daily
convention -- so a model fitted by it and served by the runtime would have had coefficients matched
to different quantities than the features they multiply. Nothing would have thrown. The forecast
would simply have been wrong, and the schema hash would not have caught it because both sides call
the features rv_short, rv_medium and rv_long.

``realised_variance`` below is a transcription of the runtime's own definition, and the windows
travel in the artifact so a change on either side stops the load rather than moving the answer.

Where a fresh fit enters the ladder
-----------------------------------
SHADOW, never VALIDATED. A model fitted five minutes ago has no out-of-sample record; what it has
is coefficients. Section 20.4's ladder exists so that something can inform a decision while it
earns the right to drive one, and publishing straight to VALIDATED would skip the only rung that
distinguishes the two. The exporters default to VALIDATED for a human running them deliberately;
an automatic loop does not get that default.
"""

from __future__ import annotations

import hashlib
import json
import math
import os
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import numpy as np
from numpy.typing import NDArray

from quantdesk_research.models.garch import GarchFitRejected, export_garch_artifact, fit_garch
from quantdesk_research.models.har import HARModel, export_har_artifact
from quantdesk_research.models.runtime_artifact import RuntimeInferenceArtifact

#: The windows the runtime's volatility expert computes, in bars. Changing one here without
#: changing it there produces coefficients matched to the wrong quantity.
SHORT_BARS = 12
MEDIUM_BARS = 60
LONG_BARS = 288

#: What the HAR fit predicts: realised variance over the next SHORT_BARS bars.
FORECAST_BARS = SHORT_BARS

#: Bars held back from every fit, so the diagnostics are measured on data the fit never saw.
HELD_OUT_BARS = 2_016

#: Below this there is not enough history for the long window plus a held-out tail to mean anything.
MINIMUM_BARS = LONG_BARS + HELD_OUT_BARS + 1_000

#: Where the runtime looks. A pointer file rather than a directory listing, so a half-written set
#: of artifacts is never picked up as a complete one.
POINTER_NAME = "current-fitted-models.json"

MODELS_DIRECTORY = "fitted-models"


class ModelFittingSkipped(Exception):
    """This cycle produced no artifact, for a reason worth stating rather than swallowing."""


@dataclass(frozen=True)
class FittedModelPublication:
    """What one cycle wrote, so the caller can report it rather than guess."""

    written: list[str]
    skipped: dict[str, str]
    dataset_hash: str


def realised_variance(closes: NDArray[np.float64], end: int, bars: int) -> float:
    """Mean squared log return over the ``bars`` observations ending at ``end``.

    A transcription of the runtime's own definition, including its handling of non-positive and
    non-finite values, because a fit computed any other way produces coefficients for a quantity the
    runtime does not compute.
    """
    first = end - bars + 1
    if first <= 0:
        return float("nan")

    total = 0.0
    counted = 0
    for index in range(first, end + 1):
        previous = closes[index - 1]
        current = closes[index]
        if previous <= 0.0 or current <= 0.0:
            continue
        log_return = math.log(current / previous)
        if not math.isfinite(log_return):
            continue
        total += log_return * log_return
        counted += 1

    return total / counted if counted > 0 else float("nan")


def har_design(
    closes: NDArray[np.float64],
) -> tuple[NDArray[np.float64], NDArray[np.float64], list[int]]:
    """The HAR design matrix, its target, and the bar index each row was taken at.

    One row per bar that has both a full long window behind it and a full forecast window ahead of
    it. The target is the realised variance of the *next* forecast window, which is what the expert
    is asked for -- fitting to the contemporaneous window instead would produce a model that
    describes the present and is scored on the future.
    """
    rows: list[list[float]] = []
    targets: list[float] = []
    positions: list[int] = []

    for end in range(LONG_BARS, len(closes) - FORECAST_BARS):
        features = [
            realised_variance(closes, end, SHORT_BARS),
            realised_variance(closes, end, MEDIUM_BARS),
            realised_variance(closes, end, LONG_BARS),
        ]
        target = realised_variance(closes, end + FORECAST_BARS, FORECAST_BARS)
        if not all(math.isfinite(value) for value in [*features, target]):
            continue

        rows.append(features)
        targets.append(target)
        positions.append(end)

    return (
        np.asarray(rows, dtype=np.float64),
        np.asarray(targets, dtype=np.float64),
        positions,
    )


def _log_returns_percent(closes: NDArray[np.float64]) -> NDArray[np.float64]:
    """Log returns in percent, which is the scale GARCH is fitted and served on.

    Percent rather than decimal because the optimiser behaves badly on variances near 1e-9, and
    because the artifact records the choice -- a model fitted on one and fed the other is wrong by
    four orders of magnitude in omega with nothing about the number to say so.
    """
    usable = closes[closes > 0.0]
    return np.diff(np.log(usable)) * 100.0


def _closes_from(dataset: Path) -> NDArray[np.float64]:
    bars = json.loads(dataset.read_text(encoding="utf-8"))
    closes = np.asarray([float(bar["c"]) for bar in bars], dtype=np.float64)
    if closes.size < MINIMUM_BARS:
        raise ModelFittingSkipped(
            f"{dataset.name} has {closes.size} bars; {MINIMUM_BARS} are needed for a "
            f"{LONG_BARS}-bar window plus a {HELD_OUT_BARS}-bar held-out tail"
        )
    return closes


def _git_commit() -> str:
    """The commit that produced the fit, which the contract requires and will not invent."""
    commit = os.environ.get("QUANTDESK_GIT_COMMIT", "").strip()
    if not commit:
        raise ModelFittingSkipped(
            "QUANTDESK_GIT_COMMIT is not set. An artifact that cannot name the code that produced "
            "it cannot be traced back from a live decision, which is most of what the manifest is "
            "for."
        )
    return commit


def fit_har(
    closes: NDArray[np.float64],
    *,
    dataset_hash: str,
    short_hash: str,
    as_of: datetime,
    git_commit: str,
) -> RuntimeInferenceArtifact:
    """Fit HAR on everything but the tail, and probe it on the tail."""
    design, target, _ = har_design(closes)
    if design.shape[0] <= HELD_OUT_BARS:
        raise ModelFittingSkipped("not enough usable HAR rows after the warm-up")

    split = design.shape[0] - HELD_OUT_BARS
    model = HARModel()
    model.fit_matrix(design[:split], target[:split])

    # Probes drawn from the held-out tail rather than invented. A parity case built from a made-up
    # feature vector proves the arithmetic; one taken from data the fit never saw also exercises the
    # range the model will actually meet.
    probes = [
        (float(row[0]), float(row[1]), float(row[2]))
        for row in design[split : split + 8]
    ]

    return export_har_artifact(
        model,
        probes=probes,
        artifact_id=f"har-{short_hash}",
        model_id="crypto-realised-variance",
        model_version="1.0.0",
        dataset_hash=dataset_hash,
        git_commit=git_commit,
        random_seed=0,
        as_of=as_of,
        bar_duration_minutes=5,
        short_bars=SHORT_BARS,
        medium_bars=MEDIUM_BARS,
        long_bars=LONG_BARS,
        variance_units="mean_squared_log_return",
        evidence_grade="C",
        promotion_state="SHADOW",
    )


def fit_garch_model(
    closes: NDArray[np.float64],
    *,
    dataset_hash: str,
    short_hash: str,
    as_of: datetime,
    git_commit: str,
) -> RuntimeInferenceArtifact:
    """Fit GARCH(1,1) on percent log returns, holding back the same tail."""
    returns = _log_returns_percent(closes)
    if returns.size <= HELD_OUT_BARS:
        raise ModelFittingSkipped("not enough returns after the held-out tail")

    fit = fit_garch(returns[: returns.size - HELD_OUT_BARS], return_units="percent")

    return export_garch_artifact(
        fit,
        artifact_id=f"garch-{short_hash}",
        model_id="crypto-conditional-variance",
        model_version="1.0.0",
        dataset_hash=dataset_hash,
        git_commit=git_commit,
        random_seed=0,
        as_of=as_of,
        bar_duration_minutes=5,
        evidence_grade="C",
        promotion_state="SHADOW",
    )


def publish_fitted_models(data_root: Path, artifacts_root: Path) -> FittedModelPublication:
    """Fit what this cycle can from the latest dataset and publish it, or say why not.

    Idempotent on the dataset. Refitting the same bars every few minutes would churn the artifact
    hash without changing what the model knows, and the runtime would reload on every cycle for no
    reason -- so a dataset already published is skipped.
    """
    manifest_path = data_root / "latest-manifest.json"
    if not manifest_path.exists():
        raise ModelFittingSkipped(f"no dataset manifest at {manifest_path}")

    manifest: dict[str, Any] = json.loads(manifest_path.read_text(encoding="utf-8"))
    dataset_hash = str(manifest["sha256"])
    dataset = data_root / str(manifest["dataFile"])
    if not dataset.exists():
        raise ModelFittingSkipped(f"manifest names {dataset.name}, which is not present")

    destination = artifacts_root / MODELS_DIRECTORY
    pointer_path = destination / POINTER_NAME
    if pointer_path.exists():
        published = json.loads(pointer_path.read_text(encoding="utf-8"))
        if published.get("dataset_hash") == dataset_hash:
            return FittedModelPublication([], {"all": "dataset already published"}, dataset_hash)

    git_commit = _git_commit()
    closes = _closes_from(dataset)
    # The manifest hash arrives prefixed "sha256:", and a colon is not a filename on every platform
    # this repository is cloned onto. The full hash still travels inside the artifact.
    short_hash = dataset_hash.split(":")[-1][:16]
    as_of = datetime.now(UTC)

    written: list[str] = []
    skipped: dict[str, str] = {}
    pointer: dict[str, str] = {}

    for family, fit in (("har", fit_har), ("garch", fit_garch_model)):
        try:
            artifact = fit(
                closes, dataset_hash=dataset_hash, short_hash=short_hash,
                as_of=as_of, git_commit=git_commit)
        except (ModelFittingSkipped, GarchFitRejected, ValueError) as refusal:
            # A refused fit is a result. The gates it failed -- non-convergence, a persistence at or
            # above one, too little history -- are the ones that keep an unusable model out of the
            # runtime, so recording the reason is more useful than an empty directory.
            skipped[family] = str(refusal)
            continue

        name = f"{artifact.artifact_id}.json"
        artifact.write(destination / name)
        written.append(name)
        pointer[family] = name

    if not written:
        raise ModelFittingSkipped(
            "no family produced a usable fit: "
            + "; ".join(f"{family}: {reason}" for family, reason in skipped.items())
        )

    # Written last, so a reader never sees a pointer to a file that is still being written.
    _write_atomic(
        pointer_path,
        {
            "dataset_hash": dataset_hash,
            "generated_at": as_of.isoformat(),
            "models": pointer,
            "skipped": skipped,
        },
    )

    return FittedModelPublication(written, skipped, dataset_hash)


def _write_atomic(path: Path, document: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(document, sort_keys=True, indent=1), encoding="utf-8")
    temporary.replace(path)


def dataset_fingerprint(closes: NDArray[np.float64]) -> str:
    """A hash of the bars themselves, for a caller that has no manifest."""
    return hashlib.sha256(closes.tobytes()).hexdigest()
