import os

from loguru import logger

# Safe toolsets for QuantDesk research agent
SAFE_RESEARCH_TOOLSETS = {
    "assets",
    "stock-data",
    "options-data",
    "crypto-data",
    "index-data",
    "corporate-actions",
}

# Forbidden patterns to prevent broker execution authority in Python plane.
# Protects against order submission, mutation, account changes, and credential access.
FORBIDDEN_PATTERNS = [
    r"place_.*_order",
    r"submit_.*_order",
    r"cancel_.*",
    r"replace_.*",
    r"close_.*",
    r"liquidate_.*",
    r"exercise_.*",
    r"do_not_exercise_.*",
    r"update_account_config",
    r"create_watchlist",
    r"delete_watchlist.*",
    r"update_watchlist.*",
    r"add_asset_to_watchlist.*",
    r"remove_asset_from_watchlist.*",
    r"create_locate",
    r"trade_.*",
    r".*reservation.*",
    r".*modify_.*risk.*",
    r".*change_.*risk.*",
    r".*hard_risk.*",
    r".*disable_.*risk.*",
    r".*policy.*",
    r".*credential.*",
    r".*secret.*",
    r".*account.*mutation.*",
]


def validate_alpaca_config():
    """
    Validates the Alpaca MCP configuration for security invariants.
    Specifically:
    - ALPACA_PAPER_TRADE must be 'true'
    - ALPACA_TOOLSETS must be explicitly set and NOT empty
    - Only safe toolsets are allowed
    - Credentials must be present (without exposing values)
    """
    # Criterion 5: Paper mode must be explicit
    paper_trade = os.environ.get("ALPACA_PAPER_TRADE", "").lower()
    if paper_trade != "true":
        logger.error(f"SECURITY FAILURE: ALPACA_PAPER_TRADE must be 'true', found '{paper_trade}'")
        return False

    # Criterion 4: Explicit safe toolset allowlist, fail closed if unset
    toolsets_str = os.environ.get("ALPACA_TOOLSETS")
    if not toolsets_str:
        logger.error("SECURITY FAILURE: ALPACA_TOOLSETS is missing or empty. FAIL CLOSED.")
        return False

    requested = {t.strip() for t in toolsets_str.split(",") if t.strip()}
    if not requested:
        logger.error("SECURITY FAILURE: No toolsets specified in ALPACA_TOOLSETS. FAIL CLOSED.")
        return False

    # Check against allowlist
    unauthorized = requested - SAFE_RESEARCH_TOOLSETS
    if unauthorized:
        logger.error(f"SECURITY FAILURE: Unauthorized toolsets requested: {unauthorized}")
        return False

    # Paper mode validation - ensure it's not trying to bypass via env mixup
    if os.environ.get("ALPACA_PAPER_TRADE", "").lower() == "false":
        logger.error("SECURITY FAILURE: Live trading explicitly forbidden.")
        return False

    if not os.environ.get("ALPACA_API_KEY") and not os.environ.get("APCA_API_KEY_ID"):
        logger.warning("Alpaca API Key is missing - server may fail to start.")

    return True


def get_safe_alpaca_env():
    """
    Returns a dictionary of environment variables for a safe Alpaca MCP server.
    Refuses to generate environment if validation fails.
    """
    if not validate_alpaca_config():
        raise RuntimeError("Alpaca security validation failed. Refusing to generate environment.")

    env = os.environ.copy()

    # Enforce paper trade explicitly
    env["ALPACA_PAPER_TRADE"] = "true"

    # Map old keys if needed for compatibility
    if not env.get("ALPACA_API_KEY") and env.get("APCA_API_KEY_ID"):
        env["ALPACA_API_KEY"] = env["APCA_API_KEY_ID"]
    if not env.get("ALPACA_SECRET_KEY") and env.get("APCA_API_SECRET_KEY"):
        env["ALPACA_SECRET_KEY"] = env["APCA_API_SECRET_KEY"]

    return env


def get_alpaca_mcp_command():
    """
    Returns the command to run the restricted Alpaca MCP server via uvx.
    """
    return ["uvx", "alpaca-mcp-server"]


# We no longer provide get_alpaca_mcp_server() returning a FastMCP instance
# to avoid runtime dependency on alpaca-mcp-server and encourage separate servers.
