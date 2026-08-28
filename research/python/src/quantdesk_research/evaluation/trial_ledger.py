import json
import sqlite3
from datetime import UTC, datetime

from loguru import logger

from quantdesk_research.config import get_research_config


class TrialLedger:
    """
    Records every hypothesis and model variant tried, ensuring repeated
    experimentation is accounted for in statistical significance tests.
    """

    def __init__(self, db_path: str | None = None):
        config = get_research_config()
        self.db_path = db_path or str(config.experiment_db_path)
        self._init_db()

    def _init_db(self):
        with sqlite3.connect(self.db_path) as conn:
            conn.execute("""
                CREATE TABLE IF NOT EXISTS trials (
                    trial_id TEXT PRIMARY KEY,
                    experiment_id TEXT,
                    hypothesis_family_id TEXT,
                    model_family TEXT,
                    feature_family TEXT,
                    parameters TEXT,
                    dataset_hash TEXT,
                    sharpe_ratio REAL,
                    status TEXT,
                    git_commit TEXT,
                    config_hash TEXT,
                    timestamp TEXT
                )
            """)
            conn.commit()

    def record_trial(self, trial_data: dict):
        trial_id = trial_data.get("trial_id") or f"trial_{datetime.now(UTC).timestamp()}"
        with sqlite3.connect(self.db_path) as conn:
            conn.execute(
                """
                INSERT INTO trials (
                    trial_id, experiment_id, hypothesis_family_id, model_family,
                    feature_family, parameters, dataset_hash, sharpe_ratio,
                    status, git_commit, config_hash, timestamp
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
                (
                    trial_id,
                    trial_data.get("experiment_id"),
                    trial_data.get("hypothesis_family_id"),
                    trial_data.get("model_family"),
                    trial_data.get("feature_family"),
                    json.dumps(trial_data.get("parameters", {})),
                    trial_data.get("dataset_hash"),
                    trial_data.get("sharpe_ratio"),
                    trial_data.get("status"),
                    trial_data.get("git_commit"),
                    trial_data.get("config_hash"),
                    datetime.now(UTC).isoformat(),
                ),
            )
            conn.commit()
        logger.info(f"Recorded trial {trial_id} in ledger.")

    def get_trial_count(self, hypothesis_family_id: str) -> int:
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.execute(
                "SELECT COUNT(*) FROM trials WHERE hypothesis_family_id = ?",
                (hypothesis_family_id,),
            )
            return cursor.fetchone()[0]

    def get_all_sharpe_ratios(self, hypothesis_family_id: str) -> list[float]:
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.execute(
                "SELECT sharpe_ratio FROM trials WHERE hypothesis_family_id = ? AND sharpe_ratio IS NOT NULL",
                (hypothesis_family_id,),
            )
            return [row[0] for row in cursor.fetchall()]

    def list_experiments(self) -> list[str]:
        """List all unique experiment IDs in the ledger."""
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.execute(
                "SELECT DISTINCT experiment_id FROM trials WHERE experiment_id IS NOT NULL"
            )
            return [row[0] for row in cursor.fetchall()]
