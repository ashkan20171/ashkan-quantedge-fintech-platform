# v4.0 — Daily SQL Risk Budget

- Enforce maximum 100,000 USD executed notional per SQL paper account per UTC day.
- Enforce maximum 30 executed fills per SQL paper account per UTC day.
- Both controls evaluated at fill time under the existing SQL account lock and serializable transaction, for manual and automatic execution.
- New owner-scoped GET `/api/sql-paper/daily-risk-budget` returns usage and remaining capacity.
- Extended GET `/api/sql-paper/risk-policy` with daily limits.
- Added bilingual daily risk budget panel with manual refresh.
- No new schema migration required.

**Scope:** Synthetic SQL paper accounts only. This is a daily turnover budget, not a daily loss or realized P&L limit. Legacy shared-account routes remain outside these controls. Integration/build verification pending.
