# Ashkan QuantEdge

**Real-Time Paper Trading & Analytics Terminal** built with **C# / ASP.NET Core 8 / SignalR / vanilla JavaScript**.

## Run

Requirements: .NET 8 SDK (Windows, macOS, or Linux).

```bash
dotnet restore
dotnet run --urls http://localhost:5080
```

Open **http://localhost:5080**.

## Included
- Responsive premium dark trading dashboard
- Simulated live price streaming (2-second ticks), 6 instruments
- SignalR hub at `/hubs/market`
- Live chart, market watch, account equity, positions and recent orders
- Market BUY/SELL paper order execution
- Risk checks: max quantity 1000, max notional $50k, no short sales, cash limits
- In-memory order book/history and resettable $100,000 virtual balance
- REST endpoints for market, history, account, orders, trades and reset

## API
`GET /api/market`, `GET /api/history/{symbol}`, `GET /api/account`, `GET /api/orders`, `GET /api/trades`

`POST /api/orders` body: `{"symbol":"AAPL","side":"BUY","quantity":2}`

`POST /api/reset` resets the paper account.

## Architecture
`TradingEngine.cs` owns state and enforces execution/risk constraints behind a lock; `MarketWorker` publishes simulated ticks via SignalR; `wwwroot` contains the responsive client. This is a self-contained demonstration, **not production trading infrastructure**.

## Production roadmap
Separate domain/application/infrastructure layers; SQL Server + EF Core migrations; authentication and authorization; durable event journal; idempotency keys; decimal precision and instrument metadata; fee/slippage modeling; order types and matching; reconciliation; market-data provider adapters; backtesting; observability; automated tests; fault tolerance and regulatory controls.

## Important limitations
All balances, orders and prices are **simulated and in-memory**, and reset when the process restarts. No real brokerage connection, market feed, historical market data, P&L accounting, authentication, or production-grade audit log is included. Do not expose this demo to the public internet or use it for real funds.

## GitHub
Repository: `ashkan-quantedge-realtime-trading-platform`

Description: `C#/.NET 8 real-time paper trading terminal with SignalR market streaming, portfolio analytics, risk controls, REST APIs and a premium responsive FinTech dashboard.`


## Update 1.1 — SignalR fix and risk telemetry
- Added `using Microsoft.AspNetCore.SignalR;` to `TradingEngine.cs` to make `IClientProxy.SendAsync` available.
- Added `GET /api/health` and `GET /api/risk` for operational and paper-trading risk visibility.
- Existing UI, SignalR event name (`marketUpdate`), and endpoints remain compatible.
- This is a simulated educational trading environment, not a production exchange or brokerage.
"# ashkan-quantedge-fintech-platform" 
