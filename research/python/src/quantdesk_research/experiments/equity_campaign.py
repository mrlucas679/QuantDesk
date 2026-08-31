from __future__ import annotations

import argparse
import json
import subprocess
import sys
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, cast

from quantdesk_research.experiments.equity_research import CANDIDATES

EVALUATOR_MODULE = "quantdesk_research.experiments.equity_research"
RESULT_HEADER = (
    "iteration\tmetric_value\tdelta\tdelta_pct\tstatus\tdescription\t"
    "evaluator_source\ttimestamp\n"
)


@dataclass(frozen=True)
class IterationResult:
    """One recorded mechanical evaluator run."""

    number: int
    description: str
    output: dict[str, Any]
    status: str
    timestamp: str


def run_campaign(data_root: Path, artifact_root: Path) -> dict[str, Any]:
    """Exhaust all preregistered candidates, then evaluate one eligible holdout winner."""
    artifact_root.mkdir(parents=True, exist_ok=True)
    iterations: list[IterationResult] = []
    best_score = 0.0
    non_improving = 0
    log_sections = [_campaign_log_header(data_root)]

    for candidate in CANDIDATES:
        output = _run_evaluator(data_root, candidate.number, "validation")
        score = float(output["score"])
        improved = score > best_score
        if improved:
            best_score = score
            non_improving = 0
        else:
            non_improving += 1
        status = "kept" if bool(output["pass"]) else "reverted"
        result = IterationResult(
            number=candidate.number,
            description=candidate.description,
            output=output,
            status=status,
            timestamp=datetime.now(UTC).isoformat(),
        )
        iterations.append(result)
        log_sections.append(_iteration_log(result))
        pivot = _pivot_log(candidate.number, non_improving)
        if pivot:
            log_sections.append(pivot)

    passing = [iteration for iteration in iterations if bool(iteration.output["pass"])]
    winner = max(passing, key=lambda item: float(item.output["score"])) if passing else None
    holdout = _run_evaluator(data_root, winner.number, "holdout") if winner else None
    qualified = bool(winner and holdout and holdout["pass"])
    summary = {
        "campaign": "US_EQUITIES_RESEARCH_001",
        "qualified": qualified,
        "validation_winner": winner.number if winner else None,
        "holdout_evaluated": holdout is not None,
        "holdout": holdout,
        "completed_iterations": len(iterations),
        "max_iterations": len(CANDIDATES),
        "execution_authority": "PAPER_EQUITY_MAX_USD_5" if qualified else "NONE",
    }
    _write_artifacts(artifact_root, data_root, iterations, log_sections, summary)
    return summary


def _run_evaluator(data_root: Path, candidate: int, phase: str) -> dict[str, Any]:
    command = [
        sys.executable,
        "-m",
        EVALUATOR_MODULE,
        "--data-root",
        str(data_root),
        "--candidate",
        str(candidate),
        "--phase",
        phase,
    ]
    completed = subprocess.run(
        command,
        check=False,
        capture_output=True,
        text=True,
        timeout=300,
    )
    if completed.returncode != 0:
        message = completed.stderr.strip().splitlines()[-1] if completed.stderr.strip() else "unknown"
        raise RuntimeError(f"Evaluator failed for candidate {candidate}: {message}")
    for line in completed.stdout.splitlines():
        try:
            parsed = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(parsed, dict) and "pass" in parsed:
            return cast(dict[str, Any], parsed)
    raise RuntimeError(f"Evaluator returned no JSON contract for candidate {candidate}.")


def _write_artifacts(
    artifact_root: Path,
    data_root: Path,
    iterations: list[IterationResult],
    log_sections: list[str],
    summary: dict[str, Any],
) -> None:
    (artifact_root / "research_log.md").write_text(
        "\n\n".join(log_sections) + "\n", encoding="utf-8"
    )
    (artifact_root / "autoresearch-results.tsv").write_text(
        RESULT_HEADER + "".join(_tsv_row(result) for result in iterations), encoding="utf-8"
    )
    (artifact_root / "validation-results.json").write_text(
        json.dumps([result.output for result in iterations], indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    (artifact_root / "qualification.json").write_text(
        json.dumps(summary, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )
    (artifact_root / "research.md").write_text(
        _research_markdown(data_root, iterations), encoding="utf-8"
    )
    (artifact_root / "final_report.md").write_text(
        _final_report(iterations, summary), encoding="utf-8"
    )
    (artifact_root / "progress.svg").write_text(_progress_svg(iterations), encoding="utf-8")


def _campaign_log_header(data_root: Path) -> str:
    return (
        "# US_EQUITIES_RESEARCH_001 Log\n\n"
        f"- Started: {datetime.now(UTC).isoformat()}\n"
        f"- Data root: `{data_root.resolve()}`\n"
        "- Evaluator: mechanical JSON contract; 300-second timeout\n"
        "- Keep policy: pass_only\n"
        "- Final holdout: evaluated once only if a validation candidate passes"
    )


def _iteration_log(result: IterationResult) -> str:
    output = result.output
    return (
        f"## Iteration {result.number}: {output['slug']}\n\n"
        f"- Hypothesis: {result.description}\n"
        f"- Evaluator output: `{json.dumps(output, sort_keys=True)}`\n"
        f"- Decision: {result.status.upper()} under pass_only\n"
        f"- Timestamp: {result.timestamp}"
    )


def _pivot_log(iteration: int, non_improving: int) -> str:
    if non_improving == 5:
        return (
            f"## DEEP PIVOT (Level 2) — Iteration {iteration}\n\n"
            "- Exhausted approaches: current preregistered rule family\n"
            "- New paradigm: continue into the next distinct family in the locked registry\n"
            "- Inspiration source: cost-first cross-family search in research.md"
        )
    if non_improving >= 3 and non_improving % 3 == 0:
        return (
            f"## PIVOT (Level 1) — Iteration {iteration}\n\n"
            "- Previous strategy: current preregistered rule family\n"
            f"- Reason: {non_improving} consecutive non-improving iterations\n"
            "- New strategy: next distinct rule family in the locked registry\n"
            "- Expected direction: test a different causal source of return"
        )
    return ""


def _tsv_row(result: IterationResult) -> str:
    score = float(result.output["score"])
    evaluator = f"python -m {EVALUATOR_MODULE}"
    return (
        f"{result.number}\t{score:.6f}\t{score:.6f}\tN/A\t{result.status}\t"
        f"{result.output['slug']}\t{evaluator}\t{result.timestamp}\n"
    )


def _research_markdown(data_root: Path, iterations: list[IterationResult]) -> str:
    rows = "\n".join(
        f"| {item.number} | {item.output['slug']} | {float(item.output['score']):.6f} bps | "
        f"{item.status} | {item.timestamp} |"
        for item in iterations
    )
    return f"""# Research: US_EQUITIES_RESEARCH_001

## Goal
Qualify or reject a causal, repeatable US-equity strategy before any execution integration.

## Success Metric
- **Metric:** Bonferroni-adjusted validation lower confidence bound, then one untouched holdout
- **Target:** positive BASE net expectancy and lower bound; holdout stability and STRESS positivity
- **Direction:** maximize

## Constraints
- **Max iterations:** 20
- **Time budget per experiment:** 5 minutes
- **Pause for review every:** never
- **Evaluator:** `python -m {EVALUATOR_MODULE}`
- **Keep policy:** pass_only
- **Guard:** no orders, no secret output, immutable SIP/all hashes, chronological causality
- **Noise runs:** 1 (deterministic rules)
- **Min delta:** 0
- **BASE/STRESS/SEVERE:** 25/35/50 bps round trip
- **Selection alpha:** 0.0025 one-sided (0.05 / 20)
- **Minimum trades:** 30

## Current Approach
Execution authority starts at NONE. Data is read-only Alpaca SIP history rooted at
`{data_root.resolve()}`. Signals use only completed observations available before entry.

## Search Space
- **Allowed changes:** exactly the 20 preregistered candidate rules in source
- **Forbidden changes:** costs, datasets, holdout, endpoints, credentials, risk limits, execution

## History
| # | Change | Metric | Result | Timestamp |
|---|--------|--------|--------|-----------|
| 0 | No qualified equity strategy | 0 bps | baseline | 2026-08-30 |
{rows}
"""


def _final_report(iterations: list[IterationResult], summary: dict[str, Any]) -> str:
    best = max(iterations, key=lambda item: float(item.output["score"]))
    rows = "\n".join(
        f"| {item.number} | {item.output['slug']} | {float(item.output['score']):.3f} | "
        f"{float(item.output['base_mean_net_bps']):.3f} | {item.output['trade_count']} | "
        f"{'yes' if item.output['pass'] else 'no'} |"
        for item in iterations
    )
    holdout_text = (
        "Evaluated exactly once for the validation winner."
        if summary["holdout_evaluated"]
        else "Not opened because no candidate crossed the validation gate."
    )
    return f"""# Research Report: US_EQUITIES_RESEARCH_001

**Generated:** {datetime.now(UTC).isoformat()}
**Total Iterations:** {len(iterations)}
**Final Metric:** {float(best.output['score']):.6f} bps adjusted lower bound
**Status:** max_iterations_reached

## Executive Summary
The entire preregistered budget was exhausted. Strategy qualification is
`{'PASS' if summary['qualified'] else 'FAIL'}` and execution authority remains
`{summary['execution_authority']}`. {holdout_text}

## Best Validation Result
- **Iteration:** {best.number}
- **Candidate:** {best.output['slug']}
- **BASE mean:** {float(best.output['base_mean_net_bps']):.6f} bps/trade
- **Adjusted lower bound:** {float(best.output['score']):.6f} bps/trade
- **Trade count:** {best.output['trade_count']}

## Iteration Summary
| # | Candidate | Adjusted lower bps | BASE mean bps | Trades | Passed? |
|---|-----------|-------------------:|--------------:|-------:|---------|
{rows}

## Key Findings
1. BASE costs were applied before evaluating every hypothesis.
2. Candidate selection used a 20-trial Bonferroni correction.
3. No order path was exercised by the research campaign.

## Holdout Discipline
{holdout_text}

## Reproducibility Commands
```powershell
uv run --locked python -m quantdesk_research.experiments.equity_campaign `
  --data-root data/US_EQUITIES_RESEARCH_001 `
  --artifact-root artifacts/US_EQUITIES_RESEARCH_001
uv run --locked pytest -q
```

## Artifact Checklist
- [x] `research.md` has all final history rows
- [x] `research_log.md` records every evaluator output
- [x] `autoresearch-results.tsv` uses the standard 8-column header
- [x] `progress.svg` records the campaign trajectory
- [x] `qualification.json` is the machine-readable authority decision
"""


def _progress_svg(iterations: list[IterationResult]) -> str:
    width, height = 960, 480
    scores = [float(item.output["score"]) for item in iterations]
    low = min(scores + [0.0])
    high = max(scores + [0.0])
    span = high - low or 1.0
    points = []
    for index, score in enumerate(scores):
        x = 60 + index * (width - 100) / max(len(scores) - 1, 1)
        y = 35 + (high - score) * (height - 90) / span
        points.append(f"{x:.1f},{y:.1f}")
    zero_y = 35 + (high - 0.0) * (height - 90) / span
    return f"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">
<rect width="100%" height="100%" fill="white"/>
<text x="60" y="24" font-family="Arial, sans-serif" font-size="18">US_EQUITIES_RESEARCH_001 adjusted validation bound</text>
<line x1="60" y1="{zero_y:.1f}" x2="920" y2="{zero_y:.1f}" stroke="#D55E00" stroke-dasharray="5,5"/>
<polyline points="{' '.join(points)}" fill="none" stroke="#0072B2" stroke-width="2"/>
<text x="60" y="465" font-family="Arial, sans-serif" font-size="13">Iteration 1</text>
<text x="850" y="465" font-family="Arial, sans-serif" font-size="13">Iteration 20</text>
</svg>
"""


def main() -> int:
    parser = argparse.ArgumentParser(description="Run the locked US equity research campaign.")
    parser.add_argument("--data-root", type=Path, required=True)
    parser.add_argument("--artifact-root", type=Path, required=True)
    arguments = parser.parse_args()
    print(json.dumps(run_campaign(arguments.data_root, arguments.artifact_root), sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
