# v4.1 — FIFO Realized P&L Observability

- Added a deterministic FIFO realized profit/loss calculator for SQL paper executions.
- Owner-scoped `GET /api/sql-paper/realized-pnl` with today (UTC), all-time, matched sell fills, and symbol breakdown.
- Added bilingual read-only dashboard cards with refresh and explicit error states.
- Fail-closed analytics: inconsistent historical inventory produces a conflict instead of fabricated P&L.
- No database migration; no external dependencies.

**Important:** This is analytics, not a daily loss-limit enforcement system. No fees, taxes, splits or external cash flows are modeled. Real market trading is not supported.
