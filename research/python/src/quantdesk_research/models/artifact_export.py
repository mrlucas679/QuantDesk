import hashlib
import json
from pathlib import Path
from typing import Any

from quantdesk_research.contracts.model_artifact import ModelArtifact


class ArtifactExporter:
    def __init__(self, artifacts_root: Path) -> None:
        self.artifacts_root = artifacts_root
        self.artifacts_root.mkdir(parents=True, exist_ok=True)

    def export(self, artifact: ModelArtifact, model_payload: dict[str, Any]) -> Path:
        """
        Export model and manifest in a C#-consumable format.
        Manifest contains provenance and schema information.
        """
        # 1. Save model payload
        model_filename = f"{artifact.artifact_id}_model.json"
        model_path = self.artifacts_root / model_filename

        # Ensure model payload is portable (no pickles)
        with open(model_path, "w") as f:
            json.dump(model_payload, f, indent=2)

        # Calculate model hash for manifest
        with open(model_path, "rb") as f:
            model_hash = hashlib.sha256(f.read()).hexdigest()

        # 2. Update artifact manifest with model hash and file reference
        artifact.artifact_hash = model_hash

        # 3. Save manifest
        manifest_path = self.artifacts_root / f"{artifact.artifact_id}_manifest.json"
        with open(manifest_path, "w") as f:
            f.write(artifact.model_dump_json(indent=2))

        return manifest_path

    def validate_artifact(self, manifest_path: Path) -> bool:
        """Verify artifact integrity before C# ingestion."""
        try:
            with open(manifest_path, "r") as f:
                manifest = json.load(f)

            # Basic structural validation
            required = ["artifact_id", "feature_schema_hash", "dataset_hash"]
            return all(k in manifest for k in required)
        except Exception:  # noqa: BLE001
            return False
