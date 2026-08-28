import hashlib
import json
from typing import Any


def hash_dict(d: dict[str, Any]) -> str:
    """Deterministic hash of a dictionary."""
    s = json.dumps(d, sort_keys=True)
    return hashlib.sha256(s.encode()).hexdigest()


def hash_file(path: str) -> str:
    """SHA256 hash of a file."""
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while chunk := f.read(8192):
            h.update(chunk)
    return h.hexdigest()
