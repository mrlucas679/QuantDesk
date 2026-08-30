import sqlite3
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from loguru import logger

from quantdesk_research.contracts.model_artifact import ModelArtifact


class ModelRegistry:
    """
    Registry for model artifacts and their promotion states.
    """

    def __init__(self, db_path: str):
        self.db_path = db_path
        Path(self.db_path).parent.mkdir(parents=True, exist_ok=True)
        self._init_db()

    def _init_db(self) -> None:
        with sqlite3.connect(self.db_path) as conn:
            conn.execute("""
                CREATE TABLE IF NOT EXISTS models (
                    artifact_id TEXT PRIMARY KEY,
                    model_id TEXT,
                    model_type TEXT,
                    model_version TEXT,
                    promotion_state TEXT,
                    artifact_path TEXT,
                    manifest_json TEXT,
                    created_at TEXT
                )
            """)
            conn.commit()

    def register_model(self, artifact: ModelArtifact, artifact_path: Path) -> None:
        with sqlite3.connect(self.db_path) as conn:
            conn.execute(
                """
                INSERT INTO models (
                    artifact_id, model_id, model_type, model_version,
                    promotion_state, artifact_path, manifest_json, created_at
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
                (
                    artifact.artifact_id,
                    artifact.model_id,
                    artifact.model_type,
                    artifact.model_version,
                    "EXPERIMENTAL",
                    str(artifact_path),
                    artifact.model_dump_json(),
                    datetime.now(UTC).isoformat(),
                ),
            )
            conn.commit()
        logger.info(
            f"Registered model {artifact.model_id} (version {artifact.model_version}) in registry."
        )

    def update_promotion_state(self, artifact_id: str, new_state: str) -> None:
        valid_states = [
            "EXPERIMENTAL",
            "VALIDATED",
            "SHADOW",
            "EXPLORATION",
            "EXPLOITATION",
            "RETIRED",
        ]
        if new_state not in valid_states:
            raise ValueError(f"Invalid promotion state: {new_state}")

        with sqlite3.connect(self.db_path) as conn:
            conn.execute(
                "UPDATE models SET promotion_state = ? WHERE artifact_id = ?",
                (new_state, artifact_id),
            )
            conn.commit()
        logger.info(f"Updated model {artifact_id} promotion state to {new_state}.")

    def list_models(self, promotion_state: str | None = None) -> list[dict[str, Any]]:
        with sqlite3.connect(self.db_path) as conn:
            if promotion_state:
                cursor = conn.execute(
                    "SELECT * FROM models WHERE promotion_state = ?", (promotion_state,)
                )
            else:
                cursor = conn.execute("SELECT * FROM models")

            if cursor.description is None:
                return []
            columns = [column[0] for column in cursor.description]
            return [dict(zip(columns, row)) for row in cursor.fetchall()]
