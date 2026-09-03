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
- Done: `TradeCandidate.Direction`; the compiler accepts a negative expected return and emits a
  Short, keeping expected profit and notional as magnitudes and signing only the betas; the pipeline
  signs the vote by the rule's direction; the service refuses a short unless the venue reports both
  `shortable` and `easy_to_borrow`.
- Remaining: the verified-forecast path still requires a positive point forecast
  (`VerifiedForecastNotPositive`), so a *model*-driven short is still refused — only the rule path
  can short today. Spot crypto stays permanently refused: no borrow at the venue.

### Phase 2 — support domain (in-process, not containers)
- Done: `ExpertSupportDomain` exists in code for the first time (the blueprint has named it since
  §44). Every runtime-inference artifact declares `support_domain` — asset class, symbols, bar —
  under the artifact seal; the reader parses it; `FittedModelStore` is keyed by it and holds several
  instruments at once; `IFittedModelSource` cannot be asked a question without naming an instrument.
  An artifact declaring no domain is refused rather than adopted globally.
- Done: the fitting loop reads `symbol` and `timeframe` from the dataset manifest, which carried
  both all along. That is what makes the previous behaviour hard to defend — the file naming the
  instrument sat unread beside the artifact.
- Effect today: the BTC-fitted HAR and GARCH stop serving SPY/QQQ/IWM/DIA. Those instruments have
  no fitted model and now say so, instead of using Bitcoin's coefficients.
- Done: the fitting loop discovers every five-minute manifest on the volume and fits one HAR and
  one GARCH per instrument. Idempotence is per instrument, so one symbol's fresh dataset no longer
  forces a refit of every other and one symbol's unchanged dataset no longer skips the cycle. The
  pointer is an array of (family, symbol, artifact) rather than an object keyed by family, which
  could only ever name one model per family.
- Not separate containers. Newman's *Building Microservices* §Premature Decomposition recommends the
  bounded context as a module inside the monolith first; a container would happily load a BTC model
  and score SPY anyway. The typed scope plus a load-time gate is what prevents it.
- Known cost: per-symbol models see less data, raising estimation error.

### Phase 3 — parallel models: measured, and the answer is no
- Done: Ridge, LightGBM and a random forest, plus their simple average, scored per instrument on the
  same purged rolling out-of-sample windows, net of each venue's real round trip.
- **0 of 24 model/instrument pairs clear their costs at 15 minutes.** Gross edge is about +1 bps
  everywhere against a toll of 8 bps (equity) or 60 bps (crypto). The three families are not
  distinguishable from one another; they are all measuring the same near-zero signal. An ensemble
  of models with no edge has no edge.
- The horizon sweep is the real finding. Equity mean net edge turns positive as the hold lengthens,
  exactly as √t scaling predicts against a fixed cost — IWM +17.1 bps at 4 hours, +20.4 at 12;
  SPY +15.7 at 12 hours. Crypto never approaches its 60 bps toll at any horizon tested.
- **No lower confidence bound clears zero at any horizon.** Trade counts collapse as the hold grows
  (81, 67, 53 in a test window), so the error bar widens faster than the mean rises. Promising, not
  established, and the distinction is the whole discipline.
- What follows is more evidence, not more models: longer equity history, or SHADOW-rung paper
  trading at 4–12 hour holds to accumulate live observations. Crypto at these costs is not a
  modelling problem.

### Phase 4 — selection by measured skill
- Done: skill is measured **per asset class**. `ExpertForecastOutcome` and `ExpertForecastScore`
  carry the book; the scorer groups by it; `MeasuredCalibrationSource` keys on it. The worst-regime
  minimum is taken across regimes but never across books — a bad crypto record no longer condemns a
  good equity one.
- Done: the directional vote's weight and calibration were literals — 0.75 and 0.5, on every vote,
  by every expert, for every instrument. The committee's 0.60 agreement floor was therefore tested
  against a constant, and the scorer reached no decision at all. Weight is now
  `MeasuredEdgeConfidence` = P(true net edge > 0) = Φ(mean / standard error), which is a real
  probability, equals 0.5 at zero edge (deriving the unmeasured default rather than asserting it),
  and makes the 0.60 floor readable as "about a quarter of a standard error of positive net edge".
- Live shadow overrules the research record once it has 12 signals, matching the tradable filter.

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
