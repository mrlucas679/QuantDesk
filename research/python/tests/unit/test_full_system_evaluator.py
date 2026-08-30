from quantdesk_research.evaluation.full_system import READINESS_GATES, evaluate


def test_evaluator_fails_closed_and_reports_missing_gates():
    def fetch(url: str) -> dict:
        if url.endswith("/readiness") and "api/system" not in url:
            return {"ready": False, "validated_model_count": 0, "reason": "no_validated_models"}
        return {gate: gate != "expertsReady" for gate in READINESS_GATES} | {"ready": False}

    result = evaluate("http://api", "http://research", fetch)

    assert result["pass"] is False
    assert result["failed_gates"] == ["expertsReady"]
    assert result["validated_model_count"] == 0


def test_evaluator_passes_only_with_every_gate_and_validated_model():
    def fetch(url: str) -> dict:
        if "api/system" in url:
            return {gate: True for gate in READINESS_GATES} | {"ready": True}
        return {"ready": True, "validated_model_count": 1, "reason": "validated_models_available"}

    result = evaluate("http://api", "http://research", fetch)

    assert result["pass"] is True
    assert result["score"] == 1.0
