"""Read dataset-manifest fields written by either producer.

Two things publish immutable dataset manifests and they do not agree on casing. The Python
downloader writes snake_case (``data_file``, ``row_count``); the C# exporters serialise with web
JSON defaults and write camelCase (``dataFile``, ``rowCount``). Neither is wrong, but a reader that
assumes one silently fails on datasets produced by the other — and the failure mode is a
``KeyError`` at load time, or worse, a ``None`` that skips a hash check.

Accepting both is the interoperable fix. Picking a side would strand every dataset already on disk.
"""

from __future__ import annotations

import re
from typing import Any

JsonObject = dict[str, Any]


def _camel(snake: str) -> str:
    head, *rest = snake.split("_")
    return head + "".join(part.title() for part in rest)


def _snake(camel: str) -> str:
    return re.sub(r"(?<!^)(?=[A-Z])", "_", camel).lower()


def manifest_value(manifest: JsonObject, key: str, default: Any = None) -> Any:
    """Return ``key`` from a manifest regardless of the producer's casing convention."""
    for candidate in (key, _camel(key), _snake(key)):
        if candidate in manifest:
            return manifest[candidate]
    return default


def require_manifest_value(manifest: JsonObject, key: str) -> Any:
    """Return ``key`` or fail loudly naming what was actually present.

    A missing manifest field must never degrade to ``None``: the fields this reads are the data
    file, the row count, and the hash, and a silent ``None`` on any of them turns an integrity
    check into a no-op.
    """
    value = manifest_value(manifest, key)
    if value is None:
        raise KeyError(
            f"Dataset manifest has no '{key}' in any supported casing. "
            f"Present keys: {sorted(manifest)}"
        )
    return value
