# QuantDesk execution plan

The working plan, kept here rather than in conversation so it survives a restart.
Last verified 2026-09-03. Baseline commit `9a47d2a`.

## The finding everything rests on

Crypto costs **60 bps** a round trip. The best measured signal is roughly **20 bps gross** at a
five-minute horizon. No ensemble beats a toll three times the edge, so model work before cost work
is decoration. A crypto fund on hour-to-day holds saw fee drag take 40% of gross returns; a 0.1%
round trip needs 0.2% of move to break even.

Second finding: there is exactly one `har` and one `garch` artifact, both fitted on the BTC/USD
5-minute dataset, and both are used for SPY, QQQ, IWM and DIA. `Docs/QuantDesk_42` already names the
missing controls — `ExpertSupportDomain` and `ModelEquivalenceGate` — with the rule that a Level-1
model may not silently claim Level-2/Level-3 evidence.

## Phases, in order

### Phase 0 — cost and horizon (do first)
- Crypto research cost assumption is `ResearchCostAssumptions.Crypto = 33.7`; the venue charges
  `VenueRoundTripCosts.Crypto = 60.0`. Every crypto net figure in the registry is ~26 bps too
  generous, and that gap is the only reason crypto rules clear the committee floor while honest
  equity rules do not.
- Raise the assumption to the measured figure, and extend the crypto holding horizon so edge scales
  with the square root of time while cost stays fixed.
- Expect this to stop most crypto trading. That is the correct outcome, not a regression.

### Phase 1 — direction (half done, in the working tree)
- Done: `SignalDirection` {None, Long, Short}; all 13 rules return it with the bearish half written
  for the first time; `IndicatorSet.DonchianLow` added so breakouts can be symmetric;
  `StrategySelection.Direction` carries it.
- Done: the spot lifecycle carries `Direction`; entry opens `Sell` and exit closes `Buy` for a
  short; the entry fence signs the adverse-move test; `HeldPosition.RealisableProfit` puts the exit
  cost on the buyback rather than negating a long; broker quantities are taken as magnitudes, since
  the venue reports a short as negative; `BrokerAssetSnapshot` reads `shortable` and
  `easy_to_borrow`; `None` is never reserved.
- Remaining, and it is one layer in from where I expected: `CryptoDirectionalStrategyCompiler`
  refuses any forecast with `ExpectedReturnBps <= 0`, so a bearish view produces no candidate at
  all, and `TradeCandidate` has no direction to carry — a candidate that did emerge would reach the
  risk governor and the reservation as a long. Until both change, the pipeline keeps refusing Short
  with `ShortNotYetExecutable`. Spot crypto stays permanently refused: no borrow at the venue.

### Phase 2 — support domain (in-process, not containers)
- Fit and publish per (asset class, symbol, timeframe) instead of one global artifact.
- Enforce at load: a BTC-fitted model is refused for SPY rather than silently used.
- Not separate containers. Newman's *Building Microservices* §Premature Decomposition recommends the
  bounded context as a module inside the monolith first; a container would happily load a BTC model
  and score SPY anyway. The typed scope plus a load-time gate is what prevents it.
- Known cost: per-symbol models see less data, raising estimation error.

### Phase 3 — parallel models: start with three, not seven
- Ridge (the baseline that must be beaten), LightGBM, Random Forest. Simple averaging first.
- Evidence: forecast combination beats complex standalone ML, and across 46 countries plain OLS beat
  eight ML methods on risk-adjusted returns because estimation error dominates. Gu/Kelly/Xiu reject
  OLS in favour of nonlinear methods, but on far larger panels than seven crypto pairs.
- CPU, not GPU: 12 logical cores suit boosted trees; 4 GB VRAM rules out sequence models.

### Phase 4 — selection by measured skill
- `ForecastOutcomeLog` and `MeasuredCalibrationSource` exist and already score by worst regime.
- Weight each model by its own out-of-sample record per asset class and regime. No record, no weight.

### Phase 4.5 — agents read disagreement
- Review and Research run today (5 runs each). Policy is gated on a validated expert and unblocks
  itself at Phase 5.
- Change their input from completed fills to the model bank's disagreements.
- They propose; they never decide. Keep Policy behind the validated-expert gate.

### Phase 5 — promotion
- All ten R-gates already evaluate. Nothing new needed.

## Hardware
i5-13420H, 8 physical / 12 logical cores, 40 GB RAM, RTX 3050 Laptop with 4 GB VRAM.
Installed: lightgbm 4.7, sklearn 1.9, arch 8.0, hmmlearn 0.3.3, statsmodels 0.15.
Missing: torch, xgboost, cupy. Docker on Windows needs WSL2 plus the NVIDIA container toolkit
before a container sees the GPU at all.

## Standing constraints
Alpaca PAPER only; never a live endpoint or fallback. C#/.NET is the sole order-mutation authority.
Do not weaken R-gates, realistic costs, risk limits, reservation-before-POST, deterministic client
IDs, lookup-before-retry, recovery, automatic exit, or reconciliation. NO_TRADE, UNCERTAIN and
ABSTAIN are valid outcomes. Never commit `.env`, keys, account identifiers or raw broker responses.
Never push to `main`; use a `codex/` branch.

## Open gaps not yet scheduled
- No operator endpoint closes a single spot position on demand.
- A rule re-enters seconds after a losing exit (LINK: three trips, -$4.30, all cost).
- Unused: `GaussianHmmFilter`, `GradientBoostedTreeModel`, `OrderBookImbalanceExpert`,
  `ExpertCatalog`.
