"""HAR realised-variance fitting, and the export that lets C# evaluate the same dot product.

HAR is the least likely of the four bridged models to be ported wrong -- it is an intercept and
three lag weights -- which is not a reason to hold it to a weaker standard. The errors parity
catches do not care how simple the arithmetic is: a coefficient read under the wrong name, a
feature order that drifted, a variance meant as a volatility.

The coefficient names were, in fact, already wrong. This module exported ``const``, ``beta_d``,
``beta_w`` and ``beta_m``; the runtime reads ``intercept``, ``beta_short``, ``beta_medium`` and
``beta_long`` by name and refuses an artifact missing any of them. Nothing had ever carried an
artifact between the two, so the mismatch sat there costing nothing until the first real one moved.
The names here are now the runtime's.
"""

from datetime import datetime

import numpy as np
from numpy.typing import NDArray

from quantdesk_research.models.runtime_artifact import (
    SupportDomain,
    FeatureSemantics,
    ParityCase,
    ParitySuite,
    ProducerIdentity,
    RuntimeInferenceArtifact,
    feature_schema_of,
    utc_now,
)


class HARModel:
    """
    Heterogeneous AutoRegressive (HAR) model for volatility.
    RV_t = c + beta_d * RV_{t-1} + beta_w * RV_{t-5:t-1} + beta_m * RV_{t-22:t-1} + epsilon_t
    """

    def __init__(self) -> None:
        self.coefficients: NDArray[np.float64] | None = None
        self.is_fitted = False

    def _prepare_features(
        self, rv: NDArray[np.float64]
    ) -> tuple[NDArray[np.float64], NDArray[np.float64]]:
        n = len(rv)
        # rv_d: RV_{t-1}
        # rv_w: average of RV over last 5 days
        # rv_m: average of RV over last 22 days

        rv_d = rv[21:-1]

        rv_w = np.array([np.mean(rv[i - 5 : i]) for i in range(22, n)])
        rv_m = np.array([np.mean(rv[i - 22 : i]) for i in range(22, n)])

        y = rv[22:]
        X = np.column_stack([np.ones(len(y)), rv_d, rv_w, rv_m])
        return np.asarray(X, dtype=np.float64), np.asarray(y, dtype=np.float64)

    def fit(self, rv: NDArray[np.float64]) -> None:
        if len(rv) < 23:
            raise ValueError("Insufficient history for HAR model")

        X, y = self._prepare_features(rv)
        # The lagged HAR features can be collinear for legitimate low-variation
        # windows. Use the deterministic minimum-norm least-squares solution.
        coefficients, _, _, _ = np.linalg.lstsq(X, y, rcond=None)
        self.coefficients = np.asarray(coefficients, dtype=np.float64)
        self.is_fitted = True

    def fit_matrix(self, design: NDArray[np.float64], target: NDArray[np.float64]) -> None:
        """Fit from an explicit design matrix, for windows other than the daily 1 / 5 / 22.

        ``fit`` builds its own features at the daily convention. The runtime serves 12 / 60 / 288
        bar windows, so a model fitted by ``fit`` and served there would have had coefficients
        matched to different quantities than the features multiplying them -- no error, no throw,
        just a wrong forecast that the schema hash cannot catch because both sides call the columns
        rv_short, rv_medium and rv_long.

        The caller supplies the design so the windows are its decision and travel in the artifact.
        """
        if design.ndim != 2 or design.shape[1] != 3:
            raise ValueError("HAR design must have three feature columns")
        if design.shape[0] != target.shape[0]:
            raise ValueError("HAR design and target differ in length")
        if design.shape[0] < 4:
            raise ValueError("Insufficient rows to identify four coefficients")

        with_intercept = np.column_stack([np.ones(design.shape[0]), design])
        coefficients, _, _, _ = np.linalg.lstsq(with_intercept, target, rcond=None)
        self.coefficients = np.asarray(coefficients, dtype=np.float64)
        self.is_fitted = True

    def predict(self, rv_d: float, rv_w: float, rv_m: float) -> float:
        if not self.is_fitted or self.coefficients is None:
            raise ValueError("Model not fitted")
        return float(
            self.coefficients[0]
            + self.coefficients[1] * rv_d
            + self.coefficients[2] * rv_w
            + self.coefficients[3] * rv_m
        )


FEATURE_NAMES = ["rv_short", "rv_medium", "rv_long"]

#: A fitted variance below this is reported as this. Least squares does not know a variance cannot
#: be negative, and an intercept going slightly negative on a quiet window is ordinary rather than a
#: fault -- the model is saying "as close to zero as I can express". Declared in the artifact rather
#: than applied privately on one side, because two implementations that clamp differently agree on
#: every case except the ones where it matters.
OUTPUT_FLOOR = 0.0


def _floored(value: float) -> float:
    return max(value, OUTPUT_FLOOR)


def har_parity_cases(model: HARModel, probes: list[tuple[float, float, float]]) -> list[ParityCase]:
    """Probe inputs, with what ``HARModel.predict`` returns for each, floored as the artifact says."""
    return [
        ParityCase(
            inputs=[[float(short), float(medium), float(long_run)]],
            expected=[_floored(model.predict(short, medium, long_run))],
        )
        for short, medium, long_run in probes
    ]


def export_har_artifact(
    model: HARModel,
    *,
    probes: list[tuple[float, float, float]],
    artifact_id: str,
    model_id: str,
    model_version: str,
    dataset_hash: str,
    git_commit: str,
    random_seed: int,
    as_of: datetime,
    bar_duration_minutes: int,
    support_domain: SupportDomain,
    short_bars: int,
    medium_bars: int,
    long_bars: int,
    variance_units: str,
    evidence_grade: str = "B",
    promotion_state: str = "VALIDATED",
) -> RuntimeInferenceArtifact:
    """Seal a fitted HAR into the artifact the runtime loads.

    The aggregation windows and the units travel with it. A dot product can be perfectly
    implemented while being fed the wrong quantity, and "RV" names a variance, a volatility, a
    5-minute realisation and a daily one equally well.
    """
    if not model.is_fitted or model.coefficients is None:
        raise ValueError("Model not fitted")
    if not probes:
        raise ValueError("HAR export needs probe inputs; an artifact with no parity cannot refuse")

    schema = feature_schema_of(
        schema_version="har-realised-variance-v1",
        feature_names=FEATURE_NAMES,
        dtypes=dict.fromkeys(FEATURE_NAMES, "float64"),
        normalization={},
        lookback_periods=long_bars,
        source_requirements=["alpaca_ohlcv"],
    )

    artifact = RuntimeInferenceArtifact(
        artifact_id=artifact_id,
        model_id=model_id,
        model_family="har",
        model_version=model_version,
        producer=ProducerIdentity(
            library="quantdesk_research.models.har",
            library_version="1",
            numpy_version=np.__version__,
        ),
        feature_schema=schema,
        feature_schema_hash=schema.feature_hash,
        support_domain=support_domain,
        feature_semantics=FeatureSemantics(
            units=dict.fromkeys(FEATURE_NAMES, variance_units),
            missing_policy="refuse",
            lookback_periods=long_bars,
            bar_duration_minutes=bar_duration_minutes,
        ),
        dataset_hash=dataset_hash,
        parameters={
            "intercept": float(model.coefficients[0]),
            "beta_short": float(model.coefficients[1]),
            "beta_medium": float(model.coefficients[2]),
            "beta_long": float(model.coefficients[3]),
        },
        variant={
            "short_bars": str(short_bars),
            "medium_bars": str(medium_bars),
            "long_bars": str(long_bars),
            "variance_units": variance_units,
            "output_floor": repr(OUTPUT_FLOOR),
        },
        random_seed=random_seed,
        evidence_grade=evidence_grade,
        promotion_state=promotion_state,
        diagnostics={
            "short_bars": short_bars,
            "medium_bars": medium_bars,
            "long_bars": long_bars,
            "coefficients": [float(value) for value in model.coefficients],
        },
        git_commit=git_commit,
        created_at=utc_now(),
        as_of=as_of,
        parity=ParitySuite(
            kind="vector_to_scalar",
            absolute_tolerance=1e-18,
            relative_tolerance=1e-9,
            cases=har_parity_cases(model, probes),
        ),
    )
    return artifact.sealed()
