import json
import sqlite3

from loguru import logger

from quantdesk_research.contracts.experiment import Experiment


class SQLiteExperimentRegistry:
    def __init__(self, db_path: str):
        self.db_path = db_path
        self._init_db()

    def _init_db(self):
        with sqlite3.connect(self.db_path) as conn:
            conn.execute("""
                CREATE TABLE IF NOT EXISTS experiments (
                    experiment_id TEXT PRIMARY KEY,
                    hypothesis TEXT,
                    dataset_hash TEXT,
                    feature_schema_hash TEXT,
                    model_family TEXT,
                    parameters TEXT,
                    metrics TEXT,
                    status TEXT,
                    artifact_ids TEXT,
                    git_commit TEXT,
                    config_hash TEXT,
                    timestamp TEXT
                )
            """)

    def record_experiment(self, exp: Experiment):
        with sqlite3.connect(self.db_path) as conn:
            conn.execute(
                """
                INSERT INTO experiments
                (experiment_id, hypothesis, dataset_hash, feature_schema_hash, model_family, parameters, metrics, status, artifact_ids, git_commit, config_hash, timestamp)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
                (
                    exp.experiment_id,
                    exp.hypothesis,
                    exp.dataset_hash,
                    exp.feature_schema_hash,
                    exp.model_family,
                    json.dumps(exp.parameters),
                    json.dumps(exp.metrics),
                    exp.status,
                    json.dumps(exp.artifact_ids),
                    exp.git_commit,
                    exp.config_hash,
                    exp.timestamp.isoformat(),
                ),
            )
        logger.info(f"Experiment {exp.experiment_id} recorded.")
