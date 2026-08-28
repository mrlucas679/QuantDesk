from pathlib import Path

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict


class TrainingConfig(BaseSettings):
    default_window_days: int = 252
    validation_window_days: int = 63
    test_window_days: int = 63


class EvaluationConfig(BaseSettings):
    sharpe_threshold: float = 1.0
    deflated_sharpe_threshold: float = 0.5
    pbo_threshold: float = 0.1


class CostsConfig(BaseSettings):
    default_fee_bps: float = 1.0
    default_slippage_bps: float = 1.0


class ResearchConfig(BaseSettings):
    model_config = SettingsConfigDict(toml_file="configs/research.default.toml")

    random_seed: int = 42
    data_root: Path = Path("data")
    artifacts_root: Path = Path("artifacts")
    experiment_db_path: Path = Path("experiments.db")

    training: TrainingConfig = Field(default_factory=TrainingConfig)
    evaluation: EvaluationConfig = Field(default_factory=EvaluationConfig)
    costs: CostsConfig = Field(default_factory=CostsConfig)


class ResourceConfig(BaseSettings):
    model_config = SettingsConfigDict(toml_file="configs/resource.default.toml")

    max_ram_gb: float = 8.0
    duckdb_threads: int = 4
    duckdb_memory_limit_gb: float = 4.0
    parallel_workers: int = 4
    min_free_disk_gb: float = 5.0


class LoggingConfig(BaseSettings):
    model_config = SettingsConfigDict(toml_file="configs/logging.default.toml")

    level: str = "INFO"
    format: str = "{time} | {level} | {message}"
    file_path: Path | None = Path("logs/research.log")
    rotation: str = "10 MB"
    retention: str = "1 week"


def get_research_config() -> ResearchConfig:
    return ResearchConfig()


def get_resource_config() -> ResourceConfig:
    return ResourceConfig()


def get_logging_config() -> LoggingConfig:
    return LoggingConfig()
