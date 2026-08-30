import argparse
import json
import sys
from collections.abc import Callable
from typing import Any, cast
from urllib.error import HTTPError, URLError
from urllib.request import urlopen

READINESS_GATES = (
    "marketDataHealthy",
    "tradeUpdatesHealthy",
    "brokerReconciled",
    "portfolioKnown",
    "featuresReady",
    "expertsReady",
    "committeesReady",
    "riskReady",
    "reservationReady",
    "executionReady",
    "exitEngineReady",
    "paperEndpointVerified",
)


def _fetch_json(url: str) -> dict[str, Any]:
    payload: object
    try:
        with urlopen(url, timeout=5) as response:
            payload = json.load(response)
    except HTTPError as error:
        payload = json.loads(error.read().decode("utf-8"))

    if not isinstance(payload, dict):
        raise TypeError("Readiness endpoint returned a non-object JSON payload.")
    return cast(dict[str, Any], payload)


def evaluate(
    api_base_url: str,
    research_base_url: str,
    fetch_json: Callable[[str], dict[str, Any]] = _fetch_json,
) -> dict[str, Any]:
    """Return a higher-is-better readiness score and explicit blocking evidence."""
    api_readiness = fetch_json(f"{api_base_url.rstrip('/')}/api/system/readiness")
    research_readiness = fetch_json(f"{research_base_url.rstrip('/')}/readiness")
    passed_gates = [gate for gate in READINESS_GATES if api_readiness.get(gate) is True]
    failed_gates = [gate for gate in READINESS_GATES if gate not in passed_gates]
    validated_models = int(research_readiness.get("validated_model_count", 0))
    score = (len(passed_gates) + min(validated_models, 1)) / (len(READINESS_GATES) + 1)
    passed = not failed_gates and validated_models > 0 and api_readiness.get("ready") is True
    return {
        "pass": passed,
        "score": round(score, 6),
        "passed_gates": passed_gates,
        "failed_gates": failed_gates,
        "validated_model_count": validated_models,
        "research_reason": research_readiness.get("reason", "unknown"),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Evaluate live QuantDesk full-system readiness.")
    parser.add_argument("--api", default="http://localhost:8080")
    parser.add_argument("--research", default="http://localhost:8000")
    arguments = parser.parse_args()
    try:
        result = evaluate(arguments.api, arguments.research)
    except (OSError, URLError, ValueError, json.JSONDecodeError) as error:
        result = {"pass": False, "score": 0.0, "error": type(error).__name__}
    print(json.dumps(result, sort_keys=True))
    return 0 if result["pass"] else 1


if __name__ == "__main__":
    sys.exit(main())
