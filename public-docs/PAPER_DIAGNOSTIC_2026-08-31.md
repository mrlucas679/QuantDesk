# Alpaca PAPER BTC/USD diagnostic evidence

Date: 2026-08-31

Experiment: `CRYPTO-DIAGNOSTIC-2026-08-30-001`

Classification: `DiagnosticExecution`

## Scope and safety

This was one bounded broker-path diagnostic, not a strategy-qualified trade.
Autonomous strategy execution was disabled. The runtime positively verified
`https://paper-api.alpaca.markets`, an active authenticated account with crypto
permission, an active/tradable BTC/USD asset, durable diagnostic storage, a
healthy recovery heartbeat, and zero BTC/USD orders/positions before entry.
No credentials are recorded here.

## Durable lifecycle evidence

| Field | Evidence |
|---|---|
| Entry client ID | `qd-diag-7d0abf3cd79f-entry` |
| Entry broker ID | `25f62cb5-d146-48a1-b789-43875d2fb474` |
| Entry final fill | `2026-08-31T04:39:37.294651+02:00` |
| Entry average price | `77786.731` |
| Entry filled quantity | `0.000126063 BTC` |
| Hold started | `2026-08-31T04:39:37.294651+02:00` |
| Scheduled exit | `2026-08-31T04:41:37.294651+02:00` |
| Exit reserved | `2026-08-31T04:41:40.333243+02:00` |
| Exit client ID | `qd-diag-7d0abf3cd79f-exit` |
| Exit broker ID | `25d96819-a752-4090-b978-5747dc066310` |
| Exit final fill | `2026-08-31T04:41:40.544770+02:00` |
| Exit average price | `77695.700` |
| Exit filled quantity | `0.000125747 BTC` |
| Final broker quantity | `0 BTC` |
| Final internal quantity | `0 BTC` |
| Reconciliation | `Flat` |

The durable deadline was exactly entry final fill plus two minutes. The worker
observed `ExitDue` approximately 458 ms after that deadline and persisted
`ExitReserved` 3.039 seconds after it while completing broker pre-exit queries.
The actual hold to durable exit reservation was `00:02:03.038592`; no exit was
submitted early and no user message was required.

## Runtime defects found and corrected

1. Diagnostic submission used slot 0 (`SPY`) instead of resolving the configured
   BTC/USD instrument slot. Diagnostic commands now resolve BTC/USD through the
   runtime symbol map.
2. The prior $5 diagnostic default was below Alpaca's observed $10 crypto order
   minimum. Alpaca returned code `40310000`; the bounded default is now $10.
3. Final internal reconciliation treated BTC-denominated crypto fees as residual
   exposure. Internal flatten accounting now uses the durable broker-derived
   exit quantity.
4. The runtime lacked an operator path for starting/inspecting diagnostics and
   recovery heartbeat evidence. Protected start, status, and recovery endpoints
   now expose the existing durable service without weakening PAPER controls.

All rejected attempts were associated with the same deterministic experiment
and client ID, and Alpaca lookup confirmed no broker order existed before each
safe resume. Exactly one entry order and one exit order were ultimately created.

## Measurements

- Entry broker request RTT: `229.135 ms`
- Entry submission-attempt to fill: `231.100 ms`
- Broker acknowledgement to entry fill: `1.965 ms`
- Exit reservation to fill: `211.527 ms`
- Entry submission-attempt to exit fill: `00:02:03.481219`
- Estimated realized cash-flow P&L: `-$0.036027`
- Comparable-quantity price P&L: `-$0.011447`

The final live Alpaca queries returned `[]` for both open BTC/USD orders and
positions. The complete .NET solution passed 165 tests with zero failures after
the runtime fixes, and the production API image built successfully.

## Readiness boundary

This evidence qualifies the bounded PAPER diagnostic and recovery path. It does
not authorize autonomous strategy execution. Strategy entries must still pass
their independent research, expert, committee, actionability, risk, reservation,
portfolio, and exit-management gates.
