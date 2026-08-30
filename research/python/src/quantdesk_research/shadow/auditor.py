from datetime import UTC, datetime
from typing import Any

from loguru import logger

from quantdesk_research.contracts.shadow_audit import ShadowAudit
from quantdesk_research.shadow.exposure import ExposureCalculator
from quantdesk_research.shadow.portfolio import PortfolioReconstructor


class ShadowAuditor:
    def __init__(self) -> None:
        self.reconstructor = PortfolioReconstructor()
        self.exposure_calc = ExposureCalculator()

    def audit(
        self, recorded_events: list[dict[str, Any]], runtime_state: dict[str, Any]
    ) -> ShadowAudit:
        """
        Independently reconstruct state from immutable events and compare with runtime_state.
        Purpose: reduce common-mode defects and provide independent verification.
        """
        start_time = datetime.now(UTC)
        # Reconstruct holdings and average prices
        reconstructed = self.reconstructor.reconstruct_detailed_from_events(recorded_events)
        reconstructed_holdings = reconstructed["holdings"]
        reconstructed_realized_pnl = reconstructed["realized_pnl"]

        runtime_holdings = runtime_state.get("holdings", {})
        runtime_pnl = runtime_state.get("realized_pnl", 0.0)

        mismatches = []

        # Check holdings
        all_symbols = set(reconstructed_holdings.keys()) | set(runtime_holdings.keys())
        for symbol in all_symbols:
            rec_q = reconstructed_holdings.get(symbol, 0.0)
            run_q = runtime_holdings.get(symbol, 0.0)
            if abs(rec_q - run_q) > 1e-8:
                mismatches.append(
                    {
                        "field": f"holdings.{symbol}",
                        "reconstructed": rec_q,
                        "runtime": run_q,
                        "diff": rec_q - run_q,
                    }
                )

        # Check P&L (if provided)
        if abs(reconstructed_realized_pnl - runtime_pnl) > 0.01:
            mismatches.append(
                {
                    "field": "realized_pnl",
                    "reconstructed": reconstructed_realized_pnl,
                    "runtime": runtime_pnl,
                    "diff": reconstructed_realized_pnl - runtime_pnl,
                }
            )

        # Independent Exposure Calculation
        exposure = self.exposure_calc.calculate_exposure(
            reconstructed_holdings, runtime_state.get("market_prices", {})
        )
        risk = self.exposure_calc.calculate_portfolio_risk(exposure)

        status = "PASS" if not mismatches else "FAIL"

        if status == "FAIL":
            logger.error(f"Shadow Audit FAILED with {len(mismatches)} mismatches.")

        end_time = datetime.now(UTC)

        return ShadowAudit(
            audit_id=f"audit_{start_time.timestamp()}",
            start_time=start_time,
            end_time=end_time,
            reconstructed_portfolio={
                "holdings": reconstructed_holdings,
                "pnl": reconstructed_realized_pnl,
            },
            reconstructed_risk=risk,
            runtime_portfolio={"holdings": runtime_holdings, "pnl": runtime_pnl},
            runtime_risk=runtime_state.get("risk", {}),
            mismatches=mismatches,
            status=status,
            diff_report=f"Found {len(mismatches)} mismatches"
            if mismatches
            else "No mismatches found",
            timestamp=end_time,
        )
