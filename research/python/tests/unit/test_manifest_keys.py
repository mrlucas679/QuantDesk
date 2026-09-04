from __future__ import annotations

import pytest

from quantdesk_research.data.manifest_keys import manifest_value, require_manifest_value

SNAKE = {"data_file": "spy.json", "row_count": 1965, "sha256": "sha256:abc"}
CAMEL = {"dataFile": "spy.json", "rowCount": 1965, "sha256": "sha256:abc"}


@pytest.mark.parametrize("manifest", [SNAKE, CAMEL], ids=["python-producer", "csharp-producer"])
def test_either_producers_casing_is_readable(manifest: dict[str, object]) -> None:
    """The Python downloader writes snake_case; the C# exporters write camelCase."""
    assert require_manifest_value(manifest, "data_file") == "spy.json"
    assert require_manifest_value(manifest, "row_count") == 1965
    assert require_manifest_value(manifest, "sha256") == "sha256:abc"


def test_a_missing_field_fails_loudly_rather_than_returning_none() -> None:
    # The fields this reads gate an integrity check. A silent None would turn a hash comparison
    # into a no-op, which is worse than crashing.
    with pytest.raises(KeyError, match="data_file"):
        require_manifest_value({"unrelated": 1}, "data_file")


def test_the_error_names_what_was_actually_present() -> None:
    with pytest.raises(KeyError, match="unrelated"):
        require_manifest_value({"unrelated": 1}, "data_file")


def test_optional_lookup_returns_the_default_without_raising() -> None:
    assert manifest_value({}, "feed") is None
    assert manifest_value({}, "feed", "iex") == "iex"


def test_an_exact_key_wins_over_a_converted_one() -> None:
    assert manifest_value({"data_file": "exact", "dataFile": "converted"}, "data_file") == "exact"


def test_a_single_word_key_needs_no_conversion() -> None:
    assert require_manifest_value({"sha256": "x"}, "sha256") == "x"
    assert require_manifest_value({"feed": "sip"}, "feed") == "sip"
