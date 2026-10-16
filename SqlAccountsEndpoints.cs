using System.Security.Claims;
using AshkanQuantEdge.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AshkanQuantEdge;

// Separate SQL-backed paper-account pilot. Legacy /api/orders still uses the shared JSON engine.
public static class SqlAccountsEndpoints
{
    private static string? Owner(ClaimsPrincipal user) => user.Identity?.Name;
    public sealed record NewSqlOrder(string Symbol, string Side, decimal Quantity, decimal LimitPrice);

    public static IEndpointRouteBuilder MapSqlAccounts(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sql-paper").RequireAuthorization();
        group.MapGet("/account", async (ClaimsPrincipal user, TradingDbContext db, CancellationToken ct) =>
        {
            var owner = Owner(user);
            if (string.IsNullOrWhiteSpace(owner)) return Results.Unauthorized();
            var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.OwnerId == owner, ct);
            return account is null ? Results.NotFound(new { error = "Create your SQL paper account first." })
                : Results.Ok(new { account.Id, account.Currency, account.CashBalance });
        });
        group.MapPost("/account", async (ClaimsPrincipal user, TradingDbContext db, CancellationToken ct) =>
        {
            if (!user.IsInRole("Trader") && !user.IsInRole("Admin")) return Results.Forbid();
            var owner = Owner(user);
            if (string.IsNullOrWhiteSpace(owner)) return Results.Unauthorized();
            var existing = await db.Accounts.SingleOrDefaultAsync(a => a.OwnerId == owner, ct);
            if (existing is not null) return Results.Conflict(new { error = "Account already exists", existing.Id });
            var account = new TradingAccount { Id = Guid.NewGuid(), OwnerId = owner, Currency = "USD", CashBalance = 100000m };
            db.Accounts.Add(account);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "Account provisioning conflict; retry GET." }); }
            return Results.Created("/api/sql-paper/account", new { account.Id, account.Currency, account.CashBalance });
        });
        group.MapGet("/orders", async (ClaimsPrincipal user, TradingDbContext db, CancellationToken ct) =>
        {
            var owner = Owner(user);
            if (string.IsNullOrWhiteSpace(owner)) return Results.Unauthorized();
            var orders = await (from o in db.Orders.AsNoTracking()
                                join a in db.Accounts.AsNoTracking() on o.AccountId equals a.Id
                                where a.OwnerId == owner
                                orderby o.CreatedUtc descending
                                select new { o.Id, o.Symbol, o.Side, o.Kind, o.Status, o.Quantity, o.LimitPrice, o.CreatedUtc })
                               .Take(200).ToListAsync(ct);
            return Results.Ok(orders);
        });
        group.MapPost("/orders/limit", async (NewSqlOrder request, ClaimsPrincipal user, TradingDbContext db, CancellationToken ct) =>
        {
            if (!user.IsInRole("Trader") && !user.IsInRole("Admin")) return Results.Forbid();
            var owner = Owner(user);
            if (string.IsNullOrWhiteSpace(owner)) return Results.Unauthorized();
            var symbol = request.Symbol?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(symbol) || symbol.Length > 32 || !symbol.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_') ||
                request.Side is not ("Buy" or "Sell") || request.Quantity <= 0 || request.Quantity > 10000 ||
                request.LimitPrice <= 0 || request.LimitPrice > 10000000 || request.Quantity * request.LimitPrice > 1000000m)
                return Results.BadRequest(new { error = "Invalid symbol, side, quantity, price or order notional." });
            var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.OwnerId == owner, ct);
            if (account is null) return Results.BadRequest(new { error = "Create a SQL paper account first." });
            var order = new TradingOrder { Id = Guid.NewGuid(), AccountId = account.Id, Symbol = symbol,
                Side = request.Side, Kind = "Limit", Status = "Pending", Quantity = request.Quantity,
                LimitPrice = request.LimitPrice, CreatedUtc = DateTimeOffset.UtcNow };
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/sql-paper/orders/{order.Id}", new { order.Id, order.Status });
        });
        group.MapDelete("/orders/{id:guid}", async (Guid id, ClaimsPrincipal user, TradingDbContext db, CancellationToken ct) =>
        {
            if (!user.IsInRole("Trader") && !user.IsInRole("Admin")) return Results.Forbid();
            var owner = Owner(user);
            if (string.IsNullOrWhiteSpace(owner)) return Results.Unauthorized();
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            // Match the automatic worker account lock to serialize manual and automatic fills.
            var accountId = await (from o in db.Orders join a in db.Accounts on o.AccountId equals a.Id
                                   where o.Id == id && a.OwnerId == owner select (Guid?)o.AccountId).SingleOrDefaultAsync(ct);
            if (accountId is null) return Results.NotFound();
            if (!await SqlAccountLock.AcquireAsync(db, accountId.Value, ct))
                return Results.Conflict(new { error = "Account is busy; retry execution." });
            // Cancelling pending orders remains available even while trading is paused.
            var order = await (from o in db.Orders join a in db.Accounts on o.AccountId equals a.Id
                               where a.OwnerId == owner && o.Id == id select o).SingleOrDefaultAsync(ct);
            if (order is null) return Results.NotFound();
            if (order.Status != "Pending") return Results.Conflict(new { error = "Only pending orders can be cancelled." });
            order.Status = "Cancelled";
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { order.Id, order.Status });
        });
        // Explicit simulated fill: executes a pending SQL limit order only when the
        // current synthetic quote crosses its limit. No live broker is contacted.
        group.MapPost("/orders/{id:guid}/execute", async (Guid id, ClaimsPrincipal user, TradingDbContext db, TradingEngine market, TradingSafety safety, SqlTradingGate gate, CancellationToken ct) =>
        {
            if (!user.IsInRole("Trader") && !user.IsInRole("Admin")) return Results.Forbid();
            if (await gate.IsPausedAsync(ct)) return Results.Conflict(new { error = "SQL trading is paused by the shared safety switch." });
            var owner = Owner(user);
            if (string.IsNullOrWhiteSpace(owner)) return Results.Unauthorized();
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            // Match the automatic worker account lock to serialize manual and automatic fills.
            var accountId = await (from o in db.Orders join a in db.Accounts on o.AccountId equals a.Id
                                   where o.Id == id && a.OwnerId == owner select (Guid?)o.AccountId).SingleOrDefaultAsync(ct);
            if (accountId is null) return Results.NotFound();
            if (!await SqlAccountLock.AcquireAsync(db, accountId.Value, ct))
                return Results.Conflict(new { error = "Account is busy; retry execution." });
            if (await gate.IsPausedAsync(ct)) return Results.Conflict(new { error = "Trading paused." });
            var order = await (from o in db.Orders join a in db.Accounts on o.AccountId equals a.Id
                               where o.Id == id && a.OwnerId == owner select o).SingleOrDefaultAsync(ct);
            if (order is null) return Results.NotFound();
            if (order.Status != "Pending" || order.Kind != "Limit") return Results.Conflict(new { error = "Order is not pending limit." });
            if (!market.TryGetPaperPrice(order.Symbol, out var price)) return Results.BadRequest(new { error = "Unsupported market symbol." });
            if (order.LimitPrice is not decimal limit || (order.Side == "Buy" && price > limit) || (order.Side == "Sell" && price < limit))
                return Results.Conflict(new { error = "Synthetic quote has not reached limit price.", price, limit });
            var account = await db.Accounts.SingleAsync(a => a.Id == order.AccountId, ct);
            var executions = await (from e in db.Executions join o in db.Orders on e.OrderId equals o.Id
                                    where e.AccountId == account.Id && o.Symbol == order.Symbol
                                    select new { o.Side, e.Quantity }).ToListAsync(ct);
            var held = executions.Sum(x => x.Side == "Buy" ? x.Quantity : -x.Quantity);
            var notional = checked(price * order.Quantity);
            if (order.Side == "Buy" && account.CashBalance < notional) return Results.Conflict(new { error = "Insufficient cash at execution." });
            if (order.Side == "Sell" && held < order.Quantity) return Results.Conflict(new { error = "Insufficient holdings; short selling disabled." });
            var risk = await SqlPreTradeRisk.EvaluateAsync(db, market, account, order, price, ct);
            if (!risk.Allowed) return Results.Conflict(new { error = risk.Reason, risk.ProjectedExposurePercent });
            account.CashBalance += order.Side == "Buy" ? -notional : notional;
            order.Status = "Filled";
            var execution = new TradeExecution { Id = Guid.NewGuid(), AccountId = account.Id, OrderId = order.Id,
                Price = price, Quantity = order.Quantity, ExecutedUtc = DateTimeOffset.UtcNow };
            db.Executions.Add(execution);
            try { if (await gate.IsPausedAsync(ct)) return Results.Conflict(new { error = "Trading paused before commit." }); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Concurrent account update; retry." }); }
            return Results.Ok(new { order.Id, order.Status, execution.Id, execution.Price, execution.Quantity, account.CashBalance });
        });
        group.MapGet("/executions", async (ClaimsPrincipal user, TradingDbContext db, CancellationToken ct) =>
        {
            var owner = Owner(user);
            if (string.IsNullOrWhiteSpace(owner)) return Results.Unauthorized();
            return Results.Ok(await (from e in db.Executions.AsNoTracking() join a in db.Accounts.AsNoTracking() on e.AccountId equals a.Id
                join o in db.Orders.AsNoTracking() on e.OrderId equals o.Id where a.OwnerId == owner
                orderby e.ExecutedUtc descending select new { e.Id, e.OrderId, o.Symbol, o.Side, e.Quantity, e.Price, e.ExecutedUtc })
                .Take(200).ToListAsync(ct));
        });
        // SQL-only, owner-scoped execution analytics. Does not mix with legacy shared paper data.
        group.MapGet("/analytics", async (ClaimsPrincipal user, TradingDbContext db, CancellationToken ct) =>
        {
            var owner = Owner(user);
            if (string.IsNullOrWhiteSpace(owner)) return Results.Unauthorized();
            var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.OwnerId == owner, ct);
            if (account is null) return Results.NotFound(new { error = "SQL paper account not found." });
            var orders = await db.Orders.AsNoTracking().Where(o => o.AccountId == account.Id)
                .Select(o => new { o.Status, o.Side }).ToListAsync(ct);
            var fills = await (from e in db.Executions.AsNoTracking()
                               join o in db.Orders.AsNoTracking() on e.OrderId equals o.Id
                               where e.AccountId == account.Id
                               select new { o.Symbol, o.Side, e.Quantity, e.Price }).ToListAsync(ct);
            return Results.Ok(new {
                mode = "synthetic-paper", currency = account.Currency,
                orders = new { total = orders.Count, pending = orders.Count(x => x.Status == "Pending"),
                    filled = orders.Count(x => x.Status == "Filled"), cancelled = orders.Count(x => x.Status == "Cancelled") },
                executions = fills.Count,
                buyNotional = fills.Where(x => x.Side == "Buy").Sum(x => x.Quantity * x.Price),
                sellNotional = fills.Where(x => x.Side == "Sell").Sum(x => x.Quantity * x.Price),
                symbols = fills.GroupBy(x => x.Symbol).Select(g => new { symbol = g.Key,
                    fills = g.Count(), notional = g.Sum(x => x.Quantity * x.Price) }).OrderByDescending(x => x.notional).ToArray()
            });
        });
        group.MapGet("/executions.csv", async (ClaimsPrincipal user, TradingDbContext db, CancellationToken ct) =>
        {
            var owner = Owner(user);
            if (string.IsNullOrWhiteSpace(owner)) return Results.Unauthorized();
            var fills = await (from e in db.Executions.AsNoTracking()
                               join a in db.Accounts.AsNoTracking() on e.AccountId equals a.Id
                               join o in db.Orders.AsNoTracking() on e.OrderId equals o.Id
                               where a.OwnerId == owner
                               orderby e.ExecutedUtc descending
                               select new { e.Id, o.Symbol, o.Side, e.Quantity, e.Price, e.ExecutedUtc })
                              .Take(10000).ToListAsync(ct);
            static string Csv(string value) => "\"" + value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
            var rows = new List<string> { "Id,Symbol,Side,Quantity,Price,ExecutedUtc" };
            foreach (var x in fills)
                rows.Add(string.Join(",", Csv(x.Id.ToString()), Csv(x.Symbol), Csv(x.Side),
                    x.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    x.Price.ToString(System.Globalization.CultureInfo.InvariantCulture), Csv(x.ExecutedUtc.ToString("O"))));
            var content = System.Text.Encoding.UTF8.GetBytes("\uFEFF" + string.Join("\r\n", rows));
            return Results.File(content, "text/csv; charset=utf-8", "quantedge-sql-executions.csv");
        });
        // v4.1: FIFO realized profit/loss analytics; informational only, not a loss-limit gate.
        group.MapGet("/realized-pnl", async (ClaimsPrincipal user, TradingDbContext db, CancellationToken ct) =>
        {
            var owner = Owner(user);
            if (string.IsNullOrWhiteSpace(owner)) return Results.Unauthorized();
            var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.OwnerId == owner, ct);
            if (account is null) return Results.NotFound(new { error = "SQL paper account not found." });
            try
            {
                var summary = await SqlRealizedPnl.GetAsync(db, account.Id, DateTimeOffset.UtcNow, ct);
                return Results.Ok(new { mode = "synthetic-paper", costBasisMethod = "FIFO", currency = account.Currency,
                    summary.RealizedTodayUsd, summary.RealizedAllTimeUsd, summary.MatchedSellFills,
                    summary.DayStartUtc, summary.NextResetUtc, summary.Symbols,
                    note = "Read-only realized P&L from synthetic fills; excludes fees and taxes. Not a daily loss guard." });
            }
            catch (InvalidOperationException)
            {
                return Results.Conflict(new { error = "Historical executions are inconsistent; FIFO P&L unavailable." });
            }
        });
        group.MapGet("/daily-risk-budget", async (ClaimsPrincipal user, TradingDbContext db, CancellationToken ct) =>
        {
            var owner = Owner(user);
            if (string.IsNullOrWhiteSpace(owner)) return Results.Unauthorized();
            var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.OwnerId == owner, ct);
            if (account is null) return Results.NotFound(new { error = "SQL paper account not found." });
            var usage = await SqlPreTradeRisk.GetDailyUsageAsync(db, account.Id, DateTimeOffset.UtcNow, ct);
            return Results.Ok(new {
                mode = "synthetic-paper", resetAtUtc = usage.DayStartUtc.AddDays(1),
                usage.DayStartUtc, usage.TradedNotionalUsd, usage.FillCount,
                usage.RemainingNotionalUsd, usage.RemainingFills,
                maxDailyTradedNotionalUsd = SqlPreTradeRisk.MaxDailyTradedNotionalUsd,
                maxDailyFills = SqlPreTradeRisk.MaxDailyFills,
                note = "UTC daily limits enforced at fill; SQL paper accounts only."
            });
        });
        group.MapGet("/risk-policy", () => Results.Ok(new {
            mode = "synthetic-paper", maxOrderNotionalUsd = SqlPreTradeRisk.MaxOrderNotionalUsd,
            maxDailyTradedNotionalUsd = SqlPreTradeRisk.MaxDailyTradedNotionalUsd,
            maxDailyFills = SqlPreTradeRisk.MaxDailyFills,
            maxGrossExposurePercent = SqlPreTradeRisk.MaxGrossExposurePercent,
            enforcement = "at-fill", appliesTo = "SQL paper accounts only",
            note = "Existing positions can remain above the limit; risk-increasing fills are rejected."
        }));
        // v3.8: owner-scoped concentration and exposure analytics. Read-only; synthetic prices.
        group.MapGet("/risk-exposure", async (ClaimsPrincipal user, TradingDbContext db, TradingEngine market, CancellationToken ct) =>
        {
            var owner = Owner(user);
            if (string.IsNullOrWhiteSpace(owner)) return Results.Unauthorized();
            var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.OwnerId == owner, ct);
            if (account is null) return Results.NotFound(new { error = "SQL paper account not found." });
            var fills = await (from e in db.Executions.AsNoTracking()
                join o in db.Orders.AsNoTracking() on e.OrderId equals o.Id
                where e.AccountId == account.Id
                select new { o.Symbol, o.Side, e.Quantity }).ToListAsync(ct);
            var positions = fills.GroupBy(x => x.Symbol).Select(g => {
                var qty = g.Sum(x => x.Side == "Buy" ? x.Quantity : -x.Quantity);
                var hasPrice = market.TryGetPaperPrice(g.Key, out var price);
                return new { symbol = g.Key, quantity = qty, markPrice = hasPrice ? (decimal?)price : null,
                    marketValue = hasPrice ? (decimal?)(qty * price) : null };
            }).Where(x => x.quantity != 0).OrderBy(x => x.symbol).ToArray();
            var unpriced = positions.Count(x => x.marketValue is null);
            var grossExposure = positions.Sum(x => Math.Abs(x.marketValue ?? 0m));
            var netExposure = positions.Sum(x => x.marketValue ?? 0m);
            var equity = account.CashBalance + netExposure;
            var largestPosition = positions.OrderByDescending(x => Math.Abs(x.marketValue ?? 0m)).FirstOrDefault();
            return Results.Ok(new {
                mode = "synthetic-paper", accountId = account.Id, currency = account.Currency,
                cashBalance = account.CashBalance, estimatedEquity = unpriced == 0 ? (decimal?)equity : null,
                grossExposure = unpriced == 0 ? (decimal?)grossExposure : null,
                grossExposurePct = unpriced == 0 && equity > 0 ? (decimal?)(100m * grossExposure / equity) : null,
                largestPositionSymbol = largestPosition?.symbol,
                largestPositionPct = unpriced == 0 && grossExposure > 0 && largestPosition is not null
                    ? (decimal?)(100m * Math.Abs(largestPosition.marketValue ?? 0m) / grossExposure) : null,
                unpricedPositions = unpriced, positions, generatedUtc = DateTimeOffset.UtcNow
            });
        });
        group.MapGet("/positions", async (ClaimsPrincipal user, TradingDbContext db, TradingEngine market, TradingSafety safety, SqlTradingGate gate, CancellationToken ct) =>
        {
            var owner = Owner(user);
            if (string.IsNullOrWhiteSpace(owner)) return Results.Unauthorized();
            var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.OwnerId == owner, ct);
            if (account is null) return Results.NotFound();
            var rows = await (from e in db.Executions.AsNoTracking() join o in db.Orders.AsNoTracking() on e.OrderId equals o.Id
                where e.AccountId == account.Id select new { o.Symbol, o.Side, e.Quantity }).ToListAsync(ct);
            var positions = rows.GroupBy(x => x.Symbol).Select(g => {
                var quantity = g.Sum(x => x.Side == "Buy" ? x.Quantity : -x.Quantity);
                var quoted = market.TryGetPaperPrice(g.Key, out var price);
                return new { symbol = g.Key, quantity, markPrice = quoted ? (decimal?)price : null,
                    marketValue = quoted ? (decimal?)(quantity * price) : null };
            }).Where(x => x.quantity != 0).ToArray();
            return Results.Ok(new { account.Id, account.CashBalance, positions,
                estimatedEquity = account.CashBalance + positions.Sum(x => x.marketValue ?? 0m),
                mode = "synthetic-paper" });
        });
        return app;
    }
}
