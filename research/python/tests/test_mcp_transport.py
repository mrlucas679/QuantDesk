from quantdesk_research.cli import main as cli


class RecordingMcp:
    """Record transport options without opening a socket or touching stdio."""

    def __init__(self) -> None:
        self.arguments: dict[str, object] = {}

    def run(self, **arguments: object) -> None:
        self.arguments = arguments


def test_http_transport_defaults_to_localhost(monkeypatch) -> None:
    recorder = RecordingMcp()
    monkeypatch.setattr("quantdesk_research.mcp.server.mcp", recorder)
    monkeypatch.setattr("sys.argv", ["quantdesk-research", "mcp"])

    cli.main()

    assert recorder.arguments == {
        "transport": "streamable-http",
        "host": "127.0.0.1",
        "port": 8000,
        "show_banner": False,
    }


def test_stdio_transport_does_not_receive_http_arguments(monkeypatch) -> None:
    recorder = RecordingMcp()
    monkeypatch.setattr("quantdesk_research.mcp.server.mcp", recorder)
    monkeypatch.setattr(
        "sys.argv", ["quantdesk-research", "mcp", "--transport", "stdio"]
    )

    cli.main()

    assert recorder.arguments == {"transport": "stdio", "show_banner": False}
