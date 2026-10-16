# Ashkan QuantEdge — FinTech Paper-Trading Platform

[![.NET](https://img.shields.io/badge/.NET-8-512BD4)](https://dotnet.microsoft.com/) ![Mode](https://img.shields.io/badge/Trading-Simulation%20Only-287c8e) ![Database](https://img.shields.io/badge/Database-SQL%20Server-316192)

> A portfolio project showcasing ASP.NET Core, SQL Server persistence, real-time market simulation, transaction coordination, and pre-trade risk engineering. **Not a live brokerage or production trading service.**

## What it demonstrates

- **Backend engineering:** ASP.NET Core minimal APIs, dependency injection, background services, EF Core, SQL Server.
- **Concurrency:** SQL Server account-scoped application locks, serializable fill transactions, coordinated manual and automatic execution.
- **Risk controls:** simulated price validation, max order notional, gross-exposure ceiling, daily traded-notional and fill-count budgets.
- **Operational safeguards:** SQL-backed pause switch, admin operations console, security audit trail, read-only portfolio risk metrics.
- **Frontend:** responsive trading dashboard, SignalR updates, English-first UI with Persian RTL option.
- **Reporting:** owner-scoped SQL executions, analytics and CSV export.

## Stack

C# · .NET 8 · ASP.NET Core · EF Core · Microsoft SQL Server · SignalR · JavaScript · HTML · CSS

## Architecture and execution path

1. A signed-in trader creates an isolated SQL paper account and submits a limit order.
2. A manual action or opt-in background worker observes a simulated quote crossing the limit.
3. The executor begins a serializable SQL transaction and acquires an account-scoped SQL application lock.
4. The shared SQL trading safety gate and account funds/holdings are checked.
5. **Pre-fill risk policy** evaluates order size, exposure and the UTC daily turnover/fill budget.
6. A permitted fill atomically updates the order, cash balance and execution history in the transaction.

The legacy shared-account demo engine is separate from the isolated SQL account subsystem.

## Getting started

**Requirements:** .NET 8 SDK, SQL Server, EF Core CLI (`dotnet tool install --global dotnet-ef`), and a local development environment.

1. Set `QE_SQL_CONNECTION` to a SQL Server connection string for a dedicated development database.
2. Set `QE_ADMIN_PASSWORD`, `QE_TRADER_PASSWORD`, and `QE_ANALYST_PASSWORD` to distinct strong passwords.
3. Restore dependencies and review the existing EF Core database setup / migrations before running.
4. Optionally set `QE_SQL_AUTO_EXECUTION=true` to enable the simulated background fill worker.
5. Start the web app:

```bash
dotnet restore
dotnet build
dotnet run --urls http://localhost:5080
```

The shared SQL safety gate starts in paused mode until an administrator explicitly resumes trading. The exact SQL schema must be provisioned before SQL-backed endpoints are used; see the existing persistence project and historical changelogs.

## Realized P&L analytics (v4.1)

The SQL paper account workspace now includes a bilingual **FIFO realized profit/loss** panel. `GET /api/sql-paper/realized-pnl` returns UTC-day and all-time realized P&L, matched sell-fill count, and a per-symbol cost-basis summary. The calculator matches synthetic buy lots against sells in chronological FIFO order and rejects inconsistent inventory history instead of reporting unreliable numbers. This endpoint is authenticated and scoped to the current account owner. No database migration is required.

**Not a daily loss guard:** These figures are informational only. They exclude fees, taxes, splits, and external deposits or withdrawals. A realized-loss kill switch, automated tests, and ASP.NET Core Identity remain future work.

## Risk policy (v4.0)

| Policy | Default | Enforcement |
|---|---:|---|
| Maximum single fill | $25,000 | Before SQL fill |
| Maximum gross exposure | 80% of estimated equity | Risk-increasing SQL fills |
| Maximum daily traded notional | $100,000 | Per account, UTC calendar day |
| Maximum daily fills | 30 | Per account, UTC calendar day |

**Daily turnover is not daily loss.** Both buys and sells consume the turnover budget. Daily budget resets at 00:00 UTC. Read your account's consumption at `GET /api/sql-paper/daily-risk-budget`; inspect static policy at `GET /api/sql-paper/risk-policy`. No new migration is required for v4.0.

## Security and engineering limitations

- Authentication is a development-only environment-password / cookie system, **not ASP.NET Core Identity**. Do not expose publicly as-is.
- The SQL pause mechanism is not a fully atomic global kill switch; in-flight executions require additional hardening.
- Simulated prices are not market data; no exchange or broker connectivity is provided.
- SQL and legacy shared-account modes are separate; SQL risk policies do not protect the legacy mode.
- SQL Server concurrency and deployment behavior require real integration tests; a successful archive check does not establish production readiness.
- This repository is an engineering portfolio demonstration, not investment advice.

## Roadmap

ASP.NET Core Identity and granular RBAC · automated SQL Server integration tests · daily loss / realized P&L controls · unified SQL-only trading engine · observability and OpenTelemetry · CI/CD · accessibility audit.

## Recruiter notes

Relevant to **Backend Software Engineer / C# .NET Developer / FinTech Software Engineer** roles across Germany and Europe. The project highlights deliberate risk boundaries, transactional data integrity, clear technical limitations and maintainable incremental delivery.

## Changelog

See `CHANGELOG_v4.1.md`, `CHANGELOG_v4.0.md` and prior version changelogs for development history.
