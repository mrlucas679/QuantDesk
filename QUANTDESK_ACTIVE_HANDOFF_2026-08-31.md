# QuantDesk Active Handoff — 2026-08-31

## Objective

Continue from the current working tree until at least one strategy genuinely qualifies under unchanged evidence, realistic after-cost economics, PAPER-only risk controls, durable recovery, managed exit, and broker/internal reconciliation. Then—and only then—execute exactly one bounded autonomous PAPER opportunity through the QuantDesk application.

This handoff separates two questions:

- **Technical lens:** can a genuinely good strategy traverse the entire system without an implementation defect?
- **Critical lens:** do the hypothesis, research design, evidence standard, and decision rule make economic and statistical sense?

Removing an artificial software blocker is required. Removing a valid research or risk rejection is forbidden. `NO_TRADE` remains a correct result when no strategy has positive robust expectancy after realistic costs.

## Safety and execution invariants

- Alpaca PAPER only; never add a live-money fallback.
- C#/.NET is the sole order-mutation authority. Python, MCP, CLI, and direct HTTP remain research or diagnostic surfaces.
- Do not weaken R-gates, costs, risk, reservation-before-POST, deterministic identity, ambiguous-submit recovery, managed exit, or reconciliation.
- Autonomous execution remains disabled until actual runtime checks and a genuinely qualified artifact pass.
- Do not treat the completed BTC diagnostic as alpha evidence or authorization for a second experiment.
- Never retune against a frozen validation result.

## Repository and runtime baseline

- Repository: `C:\Users\Admin\OneDrive\Documents\New folder\QuantDesk`
- Working branch observed: `build/csharp-foundation`
- HEAD observed when this handoff was refreshed: `9660c65 Strengthen autonomous paper-trading safeguards`
- The working tree contains intentional uncommitted research, contract, runtime, Alpaca, reconciliation, and test changes. Preserve them.
- The last verified runtime had autonomous execution disabled and a positively identified Alpaca PAPER account with crypto, equities, and options level 3 capability. Recheck runtime state because this evidence can drift.
- `CRYPTO-DIAGNOSTIC-2026-08-31-001` completed entry, worker-owned exit, and zero/zero reconciliation. It proved infrastructure only.
- No strategy is currently qualified. No autonomous strategy order is authorized by the present evidence.

## Evidence already obtained

- Broad frozen BTC validation: all 32 comparisons failed.
- ETH/USD transfer validation: 107 trades; mean net `+14.320408 bps`; adjusted lower bound `-369.440078 bps`; Sharpe `0.084252`; rejected.
- Published one-to-eight-week BTC momentum confirmation: all comparisons failed; best positive mean did not clear Sharpe or adjusted lower-bound gates.
- Preregistered 4h/12h/24h/48h trend-state campaign: all comparisons failed; gross expectancy was approximately zero and net expectancy remained near the conservative 60 bps cost.
- Historical BTC final-validation export was corrected to a real 2022–2024 window and stale manifests with the wrong window are rejected.

These failures are research knowledge. They are not reasons to lower the gate or repeat the same parameter neighborhood.

## Technical lens status

### Implemented in the working tree; verification still required

1. Typed per-gate validation evidence in Python artifacts and publication.
2. Matching C# contract/runtime enforcement for complete evidence.
3. Normal research-worker registration and immutable persistence for independent validation campaigns.
4. Rule-based publication into the common feature-schema/artifact/forecast/evidence bundle.
5. Exact strategy-definition transport from research to runtime: symbol, horizon, parameters, entry rule, signal type, and exit policy.
6. Event/state signal taxonomy for registered directional families.
7. Typed rejected-hypothesis memory and next-action routing.
8. Strict OCC parsing, deterministic dynamic option-symbol recognition, and fail-closed unknown-instrument reconciliation.
9. PAPER preflight now requires the broker to positively identify itself as PAPER and only marks reconciliation clean when open orders and positions are zero.

Do not mark these items complete until focused and full verification passes on the current tree.

### Partially implemented

1. **Alpaca MLeg adapter:** multi-leg domain validation and PAPER MLeg request construction exist. Exact JSON tests, broker-response tests, lifecycle ownership, and end-to-end proof remain.
2. **Mechanism research:** trend-state and cross-asset campaigns exist, and failures are typed, but the catalogue, regime evaluator, disagreement admission, and ranking are incomplete.

### Missing technical work, in dependency order

1. Run Ruff, strict mypy, and focused Python tests on the current evidence/worker/publication/failure-memory changes; fix every failure without weakening semantics.
2. Run Domain, Runtime, Alpaca, and API tests with the required .NET SDK; add exact MLeg payload and invalid-request/no-POST tests.
3. Finish the native MLeg lifecycle: candidate and leg-aware risk, durable reservation committed before POST, deterministic client-order ID, lookup-before-retry, parent and leg tracking, partial/final fills, managed close, restart recovery, and final reconciliation.
4. Make the autonomous strategy lifecycle durable. Generated IDs and in-memory reservation/portfolio ownership are not restart recovery.
5. Add execution-mode-aware cost scenarios: conservative stress, aggressive/taker, passive/maker, and observed-realized. Keep their provenance and qualification meanings separate.
6. Add one shared asset-class opportunity router for crypto, equities, and options using common evidence, risk, identity, recovery, and reconciliation contracts.
7. Add production option market-data/history ingestion needed for reproducible SPY research, preserving timestamps, source, pagination, and dataset identity.
8. Run full Python and .NET suites, full solution build, Docker production build, and actual runtime reconciliation before enabling anything.

## Critical lens status and missing work

1. **Mechanism catalogue:** formalize trend persistence, liquidity-shock mean reversion, volatility risk premium, cross-asset information, and microstructure pressure. Each entry needs an economic cause, actor, expected regime, disappearance condition, and falsification criterion.
2. **Regime conditioning:** calculate conditional net expectancy by preregistered regime. Independently validate the regime filter and charge its selection to multiplicity.
3. **Disagreement:** return typed `UNCERTAIN`/`ABSTAIN` when independently motivated mechanisms disagree. Do not average contradiction into false confidence.
4. **Failure-directed search:** costs imply longer horizon/lower turnover/passive-execution research; insufficient trades imply more data/assets; instability implies preregistered regime work; fragility or no raw edge rejects the neighborhood/family; transfer failure is asset-specific evidence.
5. **Ranking:** use expected net edge divided by risk consumed as the primary objective. Opportunity frequency is secondary.
6. **SPY directional options:** research a defined-risk debit vertical only after the underlying directional signal independently qualifies.
7. **Volatility risk premium:** research implied versus expected realized volatility as a separate hypothesis with option spreads, fees, assignment/exercise behavior, and defined maximum loss. Do not infer this edge from directional research.
8. **AI role:** the model proposes mechanism hypotheses and falsification tests from the failure ledger. Deterministic evaluators decide pass/fail. Parameter fishing is prohibited.

## Required verification before one PAPER opportunity

1. Focused Python tests, Ruff, and strict mypy pass.
2. Focused .NET contract, compiler, Alpaca, risk, reservation, idempotency, restart, exit, and reconciliation tests pass.
3. Full Python and .NET suites pass.
4. Full solution and Docker production builds pass.
5. Actual API and worker health pass.
6. Alpaca endpoint is positively PAPER; account, permission, and selected asset/contracts are healthy and tradable.
7. A fresh artifact genuinely clears all required gates and exact runtime semantics match it.
8. No unexplained order or position exists and broker/internal reconciliation passes.
9. Durable automatic exit and recovery are active for the chosen asset class.
10. Enable/resume exactly one bounded application-owned opportunity, observe it through final exit, and prove no unintended broker/internal exposure remains.

## Definition of done

The goal is complete only when a genuinely qualified strategy publishes complete evidence, the runtime executes exactly its tested semantics, one bounded Alpaca PAPER trade completes its durable managed lifecycle, and final broker/internal reconciliation passes. If no strategy qualifies, remain disabled and report the lowest-level research or runtime blocker accurately.
