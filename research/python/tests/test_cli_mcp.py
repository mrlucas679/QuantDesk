from unittest.mock import Mock, patch

from quantdesk_research.cli.main import main


def test_mcp_cli_honors_network_port(monkeypatch):
    monkeypatch.setattr("sys.argv", ["quantdesk-research", "mcp", "--port", "8123"])
    fake_mcp = Mock()

    with patch("quantdesk_research.mcp.server.mcp", fake_mcp):
        main()

    fake_mcp.run.assert_called_once_with(
        transport="streamable-http", host="0.0.0.0", port=8123
    )
