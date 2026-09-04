"""One model per instrument, instead of one model for all of them.

The datasets were always there. SPY, QQQ, IWM and DIA have had their own five-minute manifests on
the research volume for as long as the equity lane has existed -- 41,840 SPY bars over two years,
sitting beside the BTC manifest. The fitting loop read exactly one of them, fitted one HAR and one
GARCH from it, and those two artifacts were then consulted for all five instruments.

These tests pin the discovery rule and the per-instrument idempotence, which is the part a single
global publish key got wrong: one symbol's fresh dataset must not suppress another's fit, and one
symbol's unchanged dataset must not unpublish the model it already has.
"""

from __future__ import annotations

import json
import os
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import numpy as np
import pytest

from quantdesk_research.runtime.model_fitting import (
    ModelFittingSkipped,
    _five_minute_manifests,
    publish_fitted_models,
    support_domain_of,
)

COMMIT = "abc1234"


def _bars(seed: int, count: int = 4_000) -> list[dict[str, Any]]:
    """A price series with enough history for the long HAR window."""
    rng = np.random.default_rng(seed)
    closes = 100.0 * np.exp(np.cumsum(rng.normal(0.0, 0.0015, size=count)))
    start = datetime(2026, 1, 1, tzinfo=UTC)
    return [
        {
            "t": start.isoformat(),
            "o": float(close),
            "h": float(close),
            "l": float(close),
            "c": float(close),
            "v": 1_000.0,
        }
        for close in closes
    ]


def _dataset(root: Path, symbol: str, timeframe: str, seed: int, name: str) -> str:
    """Writes bars plus the manifest naming them, the way the C# dataset service does."""
    slug = "".join(character for character in symbol.lower() if character.isalnum())
    data_file = f"{slug}-{timeframe.lower()}-{seed}.json"
    (root / data_file).write_text(json.dumps(_bars(seed)), encoding="utf-8")
    digest = f"sha256:{seed:064x}"
    (root / name).write_text(
        json.dumps(
            {
                "datasetId": f"{slug}-{seed}",
                "symbol": symbol,
                "timeframe": timeframe,
                "rowCount": 4_000,
                "sha256": digest,
                "generatedAt": datetime.now(UTC).isoformat(),
                "dataFile": data_file,
            }
        ),
        encoding="utf-8",
    )
    return digest


@pytest.fixture(autouse=True)
def _commit(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("QUANTDESK_GIT_COMMIT", COMMIT)


def test_every_five_minute_instrument_is_discovered(tmp_path: Path) -> None:
    _dataset(tmp_path, "BTC/USD", "5Min", 1, "latest-manifest.json")
    _dataset(tmp_path, "SPY", "5Min", 2, "latest-spy-5min-iex.manifest.json")
    _dataset(tmp_path, "QQQ", "5Min", 3, "latest-qqq-5min-iex.manifest.json")

    found = {manifest["symbol"] for manifest in _five_minute_manifests(tmp_path)}

    assert found == {"BTC/USD", "SPY", "QQQ"}


def test_a_daily_dataset_is_not_mistaken_for_a_five_minute_one(tmp_path: Path) -> None:
    """Filtered on the timeframe the manifest states, not on how the file happens to be named.

    The filenames are inconsistent -- ``latest-manifest.json``, ``latest-spy-5min-iex.manifest.json``,
    ``latest-spy-daily-manifest.json`` -- and a naming convention is not a fact about the data. A
    daily series fed to a model fitted on five-minute bars is a different model with the same name.
    """
    _dataset(tmp_path, "SPY", "1Day", 4, "latest-spy-daily-manifest.json")

    assert _five_minute_manifests(tmp_path) == []


def test_one_manifest_per_symbol_even_when_several_name_it(tmp_path: Path) -> None:
    _dataset(tmp_path, "SPY", "5Min", 5, "latest-spy-manifest.json")
    _dataset(tmp_path, "SPY", "5Min", 6, "latest-spy-5min-iex.manifest.json")

    assert len(_five_minute_manifests(tmp_path)) == 1


def test_the_asset_class_is_read_from_the_symbols_shape() -> None:
    """Nothing records the asset class, so it is derived -- and the rule is stated rather than buried.

    Alpaca's crypto pairs are slash-separated and its equities are not.
    """
    crypto = support_domain_of({"symbol": "BTC/USD", "timeframe": "5Min"})
    equity = support_domain_of({"symbol": "SPY", "timeframe": "5Min"})

    assert crypto.asset_class == "spot_crypto"
    assert equity.asset_class == "us_equity"
    assert equity.symbols == ["SPY"]
    assert equity.bar_duration_minutes == 5


def test_each_instrument_gets_its_own_artifact(tmp_path: Path) -> None:
    """The substantive one. Two instruments, two HARs -- not one HAR used twice."""
    data = tmp_path / "data"
    data.mkdir()
    _dataset(data, "BTC/USD", "5Min", 7, "latest-manifest.json")
    _dataset(data, "SPY", "5Min", 8, "latest-spy-5min-iex.manifest.json")

    publish_fitted_models(data, tmp_path / "artifacts")

    pointer = json.loads(
        (tmp_path / "artifacts" / "fitted-models" / "current-fitted-models.json").read_text(
            encoding="utf-8"
        )
    )
    har = {entry["symbol"]: entry["artifact"] for entry in pointer["models"] if entry["family"] == "har"}

    assert set(har) == {"BTC/USD", "SPY"}
    assert har["BTC/USD"] != har["SPY"]

    # And each artifact says what it was fitted on, which is what lets the runtime refuse it
    # elsewhere.
    spy = json.loads(
        (tmp_path / "artifacts" / "fitted-models" / har["SPY"]).read_text(encoding="utf-8")
    )
    assert spy["support_domain"]["symbols"] == ["SPY"]
    assert spy["support_domain"]["asset_class"] == "us_equity"


def test_an_unchanged_instrument_keeps_the_model_it_already_has(tmp_path: Path) -> None:
    """A single global publish key made this impossible in both directions.

    One symbol's fresh dataset forced a refit of every other; one symbol's unchanged dataset
    skipped the whole cycle. Here the second run refits only SPY, and BTC/USD stays published
    rather than being dropped from the pointer.
    """
    data = tmp_path / "data"
    data.mkdir()
    artifacts = tmp_path / "artifacts"
    _dataset(data, "BTC/USD", "5Min", 9, "latest-manifest.json")
    _dataset(data, "SPY", "5Min", 10, "latest-spy-5min-iex.manifest.json")
    publish_fitted_models(data, artifacts)

    # SPY gets new bars; BTC/USD does not.
    _dataset(data, "SPY", "5Min", 11, "latest-spy-5min-iex.manifest.json")
    second = publish_fitted_models(data, artifacts)

    assert second.skipped["BTC/USD"] == "dataset and code already published"
    assert any(name.startswith("har-spy") for name in second.written)

    pointer = json.loads(
        (artifacts / "fitted-models" / "current-fitted-models.json").read_text(encoding="utf-8")
    )
    assert {entry["symbol"] for entry in pointer["models"]} == {"BTC/USD", "SPY"}


def test_no_five_minute_dataset_is_a_refusal_rather_than_an_empty_publish(tmp_path: Path) -> None:
    with pytest.raises(ModelFittingSkipped):
        publish_fitted_models(tmp_path, tmp_path / "artifacts")


def test_an_artifact_will_not_be_written_without_the_commit_that_produced_it(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """An artifact that cannot name the code behind it cannot be traced back from a live decision.

    The compose file used to supply a hardcoded fallback here, so every artifact claimed a commit
    that had not produced it -- and, since the republish key includes the commit, a change to the
    fitting code never republished.
    """
    data = tmp_path / "data"
    data.mkdir()
    _dataset(data, "SPY", "5Min", 12, "latest-spy-5min-iex.manifest.json")
    monkeypatch.setenv("QUANTDESK_GIT_COMMIT", "")

    with pytest.raises(ModelFittingSkipped):
        publish_fitted_models(data, tmp_path / "artifacts")


def test_a_manifest_naming_a_missing_file_skips_that_instrument_only(tmp_path: Path) -> None:
    """One broken dataset must not cost every other instrument its fit."""
    data = tmp_path / "data"
    data.mkdir()
    _dataset(data, "BTC/USD", "5Min", 13, "latest-manifest.json")
    _dataset(data, "SPY", "5Min", 14, "latest-spy-5min-iex.manifest.json")
    os.remove(data / "spy-5min-14.json")

    result = publish_fitted_models(data, tmp_path / "artifacts")

    assert "SPY" in result.skipped
    assert any(name.startswith("har-btcusd") for name in result.written)
