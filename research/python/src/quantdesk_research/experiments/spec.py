from collections.abc import Mapping
from dataclasses import dataclass


@dataclass(frozen=True)
class ExperimentSpec:
    experiment_id: str
    hypothesis: str
    asset_class: str
    horizon_seconds: int
    train_start: str
    train_end: str
    calibration_start: str
    calibration_end: str
    test_start: str
    test_end: str
    feature_schema: tuple[str, ...]
    parameters: Mapping[str, object]
    random_seed: int
