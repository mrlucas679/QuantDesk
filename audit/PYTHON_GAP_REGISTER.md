# Python Implementation Gap Register (Audit)

| ID | Severity | Domain | Requirement | Current behavior | Expected behavior | Status |
| -- | -------- | ------ | ----------- | ---------------- | ----------------- | ------ |
| G-P0-01 | P0 | AB | Full BacktestEngine | Basic signal execution, no event-based bar/quote handling. | Deterministic chronological replay with cost/fill models, partial fills, and latency. | OPEN |
| G-P0-02 | P0 | AN | Enhanced Shadow Auditor | Holdings quantity check only. | Independent P&L, Risk, Greeks, and order reconciliation reconstruction. | OPEN |
| G-P0-03 | P0 | AR | MCP Security | Self-audit tool exists but no automated verification tests. | Automated verification of registered tool set (no trading) in test suite. | OPEN |
| G-P1-01 | P1 | R | CPCV | Fallback to simple chronological folds. | True combinatorial purged CV behavior. | OPEN |
| G-P1-02 | P1 | AT | Complete CLI commands | Parser exists but most logic is not linked. | End-to-end research pipeline via CLI (all commands functional). | OPEN |
| G-P0-04 | P0 | S/T | PBO and Deflated Sharpe | Simplified implementation, lacks golden test cases. | Real calculation integrated with Trial Ledger and verified by golden fixtures. | OPEN |
| G-P0-05 | P0 | AU | Resource enforcement | Enforced in DuckDB/LGBM but lacks global enforcement in all models/data paths. | Actually obeys DuckDB memory/thread caps, worker limits globally. | OPEN |
| G-P0-06 | P0 | AJ/AK | C# artifact compatibility | JSON export exists but compatibility not proven. | Proven C# consumability via cross-language golden fixtures. | OPEN |
| G-P0-07 | P0 | C | PIT Correctness | No adversarial leakage tests or TimeFrontier. | Proved 0 leakage via TimeFrontier and synthetic leakage fixtures. | OPEN |
| G-P1-03 | P1 | O | Expert Support Domain | Models lack validity metadata. | Metadata and checks for liquidity, volatility, and regime validity. | OPEN |
| G-P1-04 | P1 | L/M | Calibration & Uncertainty | Missing in all models. | Calibration windows, reliability metrics, and prediction intervals. | OPEN |
| G-P1-05 | P1 | Z/AA | Actionability & Utility | Basic cost check only. | Evaluation includes liquidity, capacity, fill probability, and net edge. | OPEN |
| G-P1-06 | P1 | F | Baseline Models | Only Mean and Naive present. | Includes Trend, Reversal, Logistic, and EWMA baselines. | OPEN |
| G-P1-07 | P1 | G/K | Model Diagnostics | Minimal diagnostics for HMM/GARCH. | State occupancy, transition validation, stability checks, and fit rejection. | OPEN |
| G-P1-08 | P1 | BC | Smoke Pipeline | Individual components tested separately. | Integrated end-to-end research-to-artifact pipeline. | OPEN |
