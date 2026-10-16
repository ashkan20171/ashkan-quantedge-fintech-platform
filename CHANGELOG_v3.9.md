# v3.9 — SQL Pre-Trade Risk Guard

- Enforce a maximum synthetic fill notional of USD 25,000 for SQL paper accounts.
- Block risk-increasing fills projected above 80% gross exposure/equity.
- Share the same pre-fill guard between manual and automatic execution.
- Fail closed if an existing holding lacks a valid synthetic mark.
- Fix cancellation: use a serializable transaction and account lock, and permit cancellation while trading is paused.
- Publish authenticated GET `/api/sql-paper/risk-policy` and a bilingual limits notice in SQL workspace.

## Limitations

- Fixed illustrative risk parameters; no per-user policy editor or daily loss limits yet.
- Synthetic marks and estimated equity; no market-data feed, broker, or live trading.
- The pre-fill guard does not atomically synchronize with global pause toggles; see existing safety documentation.
- Legacy shared JSON trading engine is not governed by these SQL limits.
- Requires integration testing against SQL Server before deployment.
