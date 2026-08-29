# QuantDesk implementation status

QuantDesk is a paper-trading system. This page records implementation and
verification evidence; it is not a declaration of live-trading readiness.

| Area | Implementation | Verification evidence |
| --- | --- | --- |
| Alpaca market data | Authenticated WebSocket client with connection/auth acknowledgements, bounded reconnecting stream, quote/trade normalization | Unit parser/handshake tests and the read-only `stream-test` command against Alpaca's `FAKEPACA` feed |
| Alpaca paper execution | Paper-only account, submit, lookup, open-order, position, cancel, and replace adapter | Adapter tests and `paper-order-smoke`: submit, lookup, cancel, terminal cancellation poll |
| Portfolio | Idempotent normalized fills, strategy attribution, virtual lots, cash, realized/unrealized P&L | Ledger and lifecycle tests |
| Recovery and audit | Atomic portfolio snapshot writes, append-only execution journal, contiguous replay validation | Persistence and journal replay tests |
| Research contracts | Strict Python snake_case schema/artifact/forecast reader with model, artifact, and schema hash validation | Contract-reader tests using Python-compatible payloads |
| Strategy/risk/options | Directional strategy compiler, costs, actionability/risk gates, committee allocation, options validation and defined-risk payoff calculations | Domain/runtime unit tests |
| Control API | Runtime status/readiness, authenticated entry halt/risk-reduction, paper limit-order submission/list/cancel, symbol allowlist, buying-power and notional gates | Docker/Alpaca check: readiness after reconciliation, 401 without operator key, accepted non-marketable SPY order, successful cancellation, and absence from open orders |
| Autonomous research execution | Hourly live spread and two-hour bar research, aligned-momentum requirement, explicit round-trip fee/slippage hurdle, bounded entry, fee-adjusted reconciliation, exact-quantity exit, liquidation fallback, and flat-account verification | Live Docker/Alpaca check correctly abstained at -7.0 bps expected return versus 62.9 bps estimated cost; zero orders and positions |
| Docker | Multi-stage .NET API image and compose health check | Image build, host `/health` 200, and container health `healthy` |
| Concurrency | Bounded event channel with synchronized queue/timestamp bookkeeping | 12,000-event producer/consumer stress test |
| Performance | Tagged market-pipeline throughput regression gate | 50,000 normalized quotes through the bounded channel and state owner within a 10-second CI budget |

## Safe local verification

```powershell
dotnet test QuantDesk.slnx --no-restore
dotnet run --project src/QuantDesk.Cli -- capabilities
dotnet run --project src/QuantDesk.Cli -- stream-test
dotnet run --project src/QuantDesk.Cli -- paper-order-smoke
```

`paper-order-smoke` is paper-account only. It creates an intentionally
non-marketable order and cancels it immediately; never direct it at a live
account.

Private specifications, credentials, generated research artifacts, and runtime
state are excluded from the public repository. The tested Python research source
is included, but it has no broker execution authority.
