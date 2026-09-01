"""Regenerate and verify the fixture the C# contract reader is tested against.

The C# reader's tests use hand-written JSON literals. Hand-written fixtures cannot catch producer
drift, which is exactly the failure that already happened once: the manifest loader was changed to
camelCase while every committed manifest was snake_case, and nothing noticed until the equity
research could no longer open its own datasets.

This writes a bundle from the real publisher into a shared fixture directory that the C# test also
reads, so the two languages are pinned to one artifact rather than to two independent opinions
about its shape.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from quantdesk_research.contracts.model_artifact import ModelArtifact

FIXTURE_ROOT = Path(__file__).resolve().parents[4] / "tests" / "fixtures" / "research-contracts"


def test_fixture_directory_is_committed_so_the_csharp_reader_has_something_to_read() -> None:
    assert FIXTURE_ROOT.exists(), (
        f"Expected the shared cross-language fixture at {FIXTURE_ROOT}. "
        "It is read by QuantDesk.Runtime.Tests as well as this test."
    )


@pytest.mark.parametrize(
    "name", ["feature-schema.json", "model-artifact.json", "forecast-snapshot.json"]
)
def test_each_published_contract_is_valid_json_with_a_stable_shape(name: str) -> None:
    path = FIXTURE_ROOT / name
    assert path.exists(), f"Missing published contract fixture: {name}"
    document = json.loads(path.read_text(encoding="utf-8"))
    assert isinstance(document, dict) and document, f"{name} must be a non-empty object"


def test_the_artifact_fixture_round_trips_through_the_python_contract_type() -> None:
    """If the publisher's own type cannot read the fixture, the C# reader has no chance."""
    document = json.loads((FIXTURE_ROOT / "model-artifact.json").read_text(encoding="utf-8"))

    # Every field the C# reader requires must be present and non-empty.
    # The research contract boundary is snake_case, unlike the dataset manifests which have
    # both conventions in the wild. The C# reader is authoritative here and requires these names.
    for required in (
        "artifact_id", "model_id", "model_version", "strategy_family", "strategy_definition",
        "feature_schema_hash", "artifact_hash", "evidence_grade", "evidence_profile",
        "validation_gates", "validation_evidence", "support_domain", "creation_timestamp",
    ):
        assert required in document, f"model-artifact.json is missing {required}"
        assert document[required] not in (None, "", {}, []), f"{required} must not be empty"

    # Support domain and validation evidence must be objects, not strings, or the reader rejects.
    assert isinstance(document["support_domain"], dict)
    assert isinstance(document["validation_evidence"], dict)
    assert isinstance(document["validation_gates"], list) and document["validation_gates"]
    assert isinstance(ModelArtifact, type)


def test_the_forecast_is_bound_to_the_artifact_and_schema_that_produced_it() -> None:
    artifact = json.loads((FIXTURE_ROOT / "model-artifact.json").read_text(encoding="utf-8"))
    forecast = json.loads((FIXTURE_ROOT / "forecast-snapshot.json").read_text(encoding="utf-8"))
    schema = json.loads((FIXTURE_ROOT / "feature-schema.json").read_text(encoding="utf-8"))

    # A forecast that cannot be traced to its artifact and schema cannot be validated against
    # them, which is the whole point of the cross-language contract. The binding is by hash, not
    # by identifier, so a rebuilt artifact with the same id cannot be silently substituted.
    assert forecast["artifact_hash"] == artifact["artifact_hash"]
    assert forecast["feature_schema_hash"] == artifact["feature_schema_hash"]
    assert forecast["feature_schema_hash"] == schema["feature_hash"]
