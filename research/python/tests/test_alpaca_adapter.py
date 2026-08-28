import os

import pytest

from quantdesk_research.mcp.alpaca import (
    SAFE_RESEARCH_TOOLSETS,
    get_safe_alpaca_env,
    validate_alpaca_config,
)


def test_case_a_integration_disabled():
    # This is more of a server.py test, but we can verify adapter doesn't interfere
    # if env vars are missing.
    os.environ.pop("ALPACA_PAPER_TRADE", None)
    os.environ.pop("ALPACA_TOOLSETS", None)
    # validate_alpaca_config should return False (fail closed) if these are missing but requested
    assert validate_alpaca_config() is False


def test_case_b_alpaca_enabled_safe():
    os.environ["ALPACA_PAPER_TRADE"] = "true"
    os.environ["ALPACA_TOOLSETS"] = ",".join(list(SAFE_RESEARCH_TOOLSETS))
    os.environ["ALPACA_API_KEY"] = "fake"
    os.environ["ALPACA_SECRET_KEY"] = "fake"
    assert validate_alpaca_config() is True

    env = get_safe_alpaca_env()
    assert env["ALPACA_PAPER_TRADE"] == "true"
    assert "assets" in env["ALPACA_TOOLSETS"]


def test_case_c_toolsets_missing():
    os.environ["ALPACA_PAPER_TRADE"] = "true"
    os.environ.pop("ALPACA_TOOLSETS", None)
    assert validate_alpaca_config() is False


def test_case_d_trading_toolset_requested():
    os.environ["ALPACA_PAPER_TRADE"] = "true"
    os.environ["ALPACA_TOOLSETS"] = "assets,trading"
    assert validate_alpaca_config() is False


def test_case_e_account_toolset_requested():
    os.environ["ALPACA_PAPER_TRADE"] = "true"
    os.environ["ALPACA_TOOLSETS"] = "assets,account"
    assert validate_alpaca_config() is False


def test_case_f_paper_mode_false():
    os.environ["ALPACA_PAPER_TRADE"] = "false"
    os.environ["ALPACA_TOOLSETS"] = "assets"
    assert validate_alpaca_config() is False


def test_case_f_paper_mode_missing():
    os.environ.pop("ALPACA_PAPER_TRADE", None)
    os.environ["ALPACA_TOOLSETS"] = "assets"
    assert validate_alpaca_config() is False


def test_get_safe_alpaca_env_raises_on_invalid():
    os.environ["ALPACA_PAPER_TRADE"] = "false"
    with pytest.raises(RuntimeError, match="Alpaca security validation failed"):
        get_safe_alpaca_env()
