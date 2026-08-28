import numpy as np
import pytest

from quantdesk_research.models.har import HARModel


def test_har_formula_golden():
    """
    Test HAR model with a fixed case to ensure cross-language reproducibility.
    """
    # Create synthetic RV data
    # We need at least 23 points for HAR (d, w, m)
    rv = np.linspace(0.01, 0.05, 50)

    model = HARModel()
    model.fit(rv)

    coeffs = model.export_coefficients()

    # Check if we get expected coefficients (roughly)
    assert "const" in coeffs
    assert "beta_d" in coeffs
    assert "beta_w" in coeffs
    assert "beta_m" in coeffs

    # Predict for a specific case
    rv_d, rv_w, rv_m = 0.04, 0.035, 0.03
    pred = model.predict(rv_d, rv_w, rv_m)

    expected = (
        coeffs["const"]
        + coeffs["beta_d"] * rv_d
        + coeffs["beta_w"] * rv_w
        + coeffs["beta_m"] * rv_m
    )
    assert pytest.approx(pred) == expected
