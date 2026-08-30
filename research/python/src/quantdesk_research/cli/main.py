import argparse
import json
import sys
from pathlib import Path

from loguru import logger

from quantdesk_research.resource_governor import get_resource_governor


def main() -> None:
    parser = argparse.ArgumentParser(prog="quantdesk-research")
    subparsers = parser.add_subparsers(dest="command")

    # Preflight
    subparsers.add_parser("preflight")

    # Shadow
    shadow_parser = subparsers.add_parser("shadow")
    shadow_sub = shadow_parser.add_subparsers(dest="subcommand")
    shadow_audit = shadow_sub.add_parser("audit")
    shadow_audit.add_argument("--events", type=str, required=True, help="Path to JSON events file")
    shadow_audit.add_argument(
        "--state", type=str, required=True, help="Path to JSON runtime state file"
    )

    # MCP
    mcp_parser = subparsers.add_parser("mcp")
    mcp_parser.add_argument("--port", type=int, default=8000)

    worker_parser = subparsers.add_parser("worker")
    worker_parser.add_argument("--data-root", type=str, default="/app/data")
    worker_parser.add_argument("--interval-seconds", type=int, default=21_600)

    args = parser.parse_args()

    if args.command == "preflight":
        gov = get_resource_governor()
        if gov.check_limits():
            logger.info("Preflight check passed.")
            sys.exit(0)
        else:
            logger.error("Preflight check failed: insufficient resources.")
            sys.exit(1)

    elif args.command == "shadow" and args.subcommand == "audit":
        from quantdesk_research.shadow.auditor import ShadowAuditor

        try:
            with open(args.events, "r") as f:
                events = json.load(f)
            with open(args.state, "r") as f:
                state = json.load(f)

            auditor = ShadowAuditor()
            result = auditor.audit(events, state)
            print(json.dumps(result, indent=2))
        except Exception as e:  # noqa: BLE001
            logger.error(f"Shadow Audit failed: {e}")
            sys.exit(1)

    elif args.command == "mcp":
        from quantdesk_research.mcp.server import mcp

        logger.info(f"Starting MCP server on port {args.port}...")
        mcp.run(transport="streamable-http", host="0.0.0.0", port=args.port)

    elif args.command == "worker":
        if args.interval_seconds < 60:
            parser.error("--interval-seconds must be at least 60 to prevent uncontrolled retraining.")
        from quantdesk_research.runtime.research_worker import run_forever

        run_forever(Path(args.data_root), args.interval_seconds)

    elif args.command is None:
        parser.print_help()


if __name__ == "__main__":
    main()
