# Python Requirement Traceability Matrix (Audit)

This matrix tracks every Python-owned requirement against the actual implementation and verification status.

## Audit Domains

| Domain | Description | Status | Evidence |
| --- | --- | --- | --- |
| A | Project / foundation | PARTIAL | Python 3.12, pyproject.toml, typed config verified. Resource gov not fully enforced. |
| B | Data plane | PARTIAL | Parquet/DuckDB present. Missing lineage, versioning, manifests. |
| C | Point-in-time correctness (P0) | PARTIAL | `as_of_join` and simple validation present. Missing `TimeFrontier` and adversarial tests. |
| D | Feature schema | PARTIAL | `FeatureSchema` exists but lacks hard-failure for reordering and versioning logic. |
| E | Feature research | PARTIAL | Returns, Trend, Volatility present. Missing HARQ, spreads, liquidity. |
| F | Baselines | PARTIAL | Mean and Naive present. Missing Trend, Reversal, Logistic, EWMA. |
| G | HMM | PARTIAL | Basic fit/predict. Missing diagnostics, stability checks. |
| H | LightGBM | PARTIAL | Training works. CPU enforced. Missing calibration, diagnostics. |
| I | HAR | VERIFIED | Basic fit/predict/export present. |
| J | HARQ | MISSING | Not implemented. |
| K | GARCH | PARTIAL | Basic fit/predict. Missing unstable-fit rejection. |
| L | Calibration | MISSING | Not implemented in models. |
| M | Forecast intervals / uncertainty | MISSING | Not implemented. |
| N | Typed forecast contract | PARTIAL | Basic `Forecast` contract. Missing metadata fields (calibration, support-domain). |
| O | Expert Support Domain | MISSING | Not implemented. |
| P | Walk-forward | PARTIAL | Simple loop exists. Missing fold timestamp retention. |
| Q | Purged CV | VERIFIED | Basic purge/embargo logic present. |
| R | CPCV | MISSING | Skeleton only, falls back to chronological folds. |
| S | PBO | PARTIAL | Simplified calculation. Missing relative rank/logit treatment. |
| T | Deflated Sharpe Ratio | PARTIAL | Implementation present but lacks mathematical golden tests. |
| U | Trial Ledger | VERIFIED | SQLite persistence present. |
| V | Model Equivalence Gate | MISSING | Not implemented. |
| W | Robustness / sensitivity | MISSING | Not implemented. |
| X | Transfer testing | MISSING | Not implemented. |
| Y | Ablation | MISSING | Not implemented. |
| Z | Actionability Gate | PARTIAL | Simple cost check. Missing liquidity, capacity, fill probability. |
| AA | Economic utility | PARTIAL | Minimal metrics. Missing net edge, concentration, tail behavior. |
| AB | Full BacktestEngine | PARTIAL | Skeleton exists. Missing event handling, partial fills, latency. |
| AC | Asset-specific cost models | PARTIAL | `EquityCostModel` exists. Missing Options/Crypto. |
| AD | Fill models | PARTIAL | `SpreadFillModel` exists. Missing maker/adverse selection. |
| AE | Execution simulator model risk | MISSING | Not implemented. |
| AF | Options reference engine | VERIFIED | Black-Scholes, Payoff present. |
| AG | Option-surface diagnostics | PARTIAL | Minimal sanity checks. |
| AH | Delta-hedged option evaluation | MISSING | Not implemented. |
| AI | Artifact governance | PARTIAL | JSON export exists. Missing provenance validation. |
| AJ | C# compatibility | PARTIAL | JSON output exists but compatibility not proven with fixtures. |
| AK | Cross-language golden fixtures | MISSING | Not implemented. |
| AL | Experiment Registry | VERIFIED | SQLite registry present. |
| AM | Promotion evidence | MISSING | Not implemented. |
| AN | Enhanced Shadow Auditor | PARTIAL | Holdings audit only. Missing P&L, Risk, Greeks. |
| AO | Shadow Auditor result format | PARTIAL | Simple status/mismatch. |
| AP | Warm Python worker | MISSING | Not implemented. |
| AQ | QuantDesk MCP | PARTIAL | Server exists with basic tools. |
| AR | MCP security | PARTIAL | `security_audit` tool exists but no automated verification. |
| AS | MCP live/read integration | MISSING | Not implemented. |
| AT | CLI | PARTIAL | Parser exists. Most commands are stubs. |
| AU | Resource enforcement | PARTIAL | Applied to DuckDB/LGBM. Not global. |
| AV | Logging / error semantics | VERIFIED | Loguru structured logging present. |
| AW | Security / secrets | VERIFIED | No obvious leaks. Redaction patterns in docs. |
| AX | Dependency audit | VERIFIED | Minimal dependencies, Python 3.12 compatible. |
| AY | Windows compatibility | VERIFIED | Working on Windows dev machine. |
| AZ | Determinism | PARTIAL | Seeds used in models. Not verified across runs. |
| BA | Test quality | PARTIAL | Tests exist for some areas but assertions are often weak. |
| BB | Required test layers | PARTIAL | Unit/Golden exist. Missing Property/Security/Integration. |
| BC | Smoke pipeline | MISSING | Not implemented as integrated pipeline. |
| BD | Model smoke workflows | PARTIAL | Individual models have basic tests. |
| BE | MCP smoke | MISSING | Not implemented. |
| BF | Persistence/restart smoke | MISSING | Not implemented. |
| BG | Historical failure scenarios | MISSING | Not implemented. |
| BH | V5 harness evidence | MISSING | Not implemented. |
| BI | Test discovery | VERIFIED | Pytest discovery working. |
| BJ | Coverage diagnostic | MISSING | Not configured. |
| BK | Dead-code audit | PENDING | To be performed. |
| BL | Documentation-vs-code audit | PENDING | To be performed. |
| BM | REQUIREMENT TRACEABILITY EXIT CONDITION | PENDING | |
