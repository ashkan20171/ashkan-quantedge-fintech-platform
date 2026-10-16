using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using AshkanQuantEdge.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AshkanQuantEdge;

// Opt-in, synthetic-only SQL order execution. Never routes orders to a broker.
public sealed class SqlAutoExecutionWorker(IServiceScopeFactory scopes, TradingEngine market, TradingSafety safety, SqlTradingGate gate, ILogger<SqlAutoExecutionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Environment.GetEnvironmentVariable("QE_SQL_AUTO_EXECUTION") != "true")
        {
            logger.LogInformation("SQL auto-execution disabled. Set QE_SQL_AUTO_EXECUTION=true to opt in.");
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (await gate.IsPausedAsync(stoppingToken)) continue;
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetService<TradingDbContext>();
                if (db is null) continue;
                var ids = await db.Orders.AsNoTracking().Where(x => x.Status == "Pending" && x.Kind == "Limit")
                    .OrderBy(x => x.CreatedUtc).Select(x => x.Id).Take(100).ToListAsync(stoppingToken);
                foreach (var id in ids)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    if (await gate.IsPausedAsync(stoppingToken)) break;
                    try { await TryFillAsync(db, id, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogWarning(ex, "SQL paper fill skipped for {OrderId}", id); }
                    db.ChangeTracker.Clear();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "SQL auto-execution cycle failed"); }
        }
    }

    private async Task TryFillAsync(TradingDbContext db, Guid id, CancellationToken ct)
    {
        if (await gate.IsPausedAsync(ct)) return;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        // Transaction-scoped lock serializes balance/position updates for each account,
        // including multiple worker instances using this implementation.
        var candidate = await db.Orders.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.AccountId }).SingleOrDefaultAsync(ct);
        if (candidate is null) return;
        if (!await SqlAccountLock.AcquireAsync(db, candidate.AccountId, ct)) return;
        if (await gate.IsPausedAsync(ct)) return;
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (order is null || order.Status != "Pending" || order.Kind != "Limit" || order.LimitPrice is null) return;
        if (!market.TryGetPaperPrice(order.Symbol, out var price)) return;
        if (order.Side == "Buy" && price > order.LimitPrice || order.Side == "Sell" && price < order.LimitPrice) return;
        if (order.Side is not ("Buy" or "Sell") || order.Quantity <= 0) return;
        var account = await db.Accounts.SingleAsync(x => x.Id == order.AccountId, ct);
        var history = await (from e in db.Executions join o in db.Orders on e.OrderId equals o.Id
            where e.AccountId == account.Id && o.Symbol == order.Symbol select new { o.Side, e.Quantity }).ToListAsync(ct);
        var held = history.Sum(x => x.Side == "Buy" ? x.Quantity : -x.Quantity);
        var notional = checked(price * order.Quantity);
        if (order.Side == "Buy" && account.CashBalance < notional || order.Side == "Sell" && held < order.Quantity) return;
        var risk = await SqlPreTradeRisk.EvaluateAsync(db, market, account, order, price, ct);
        if (!risk.Allowed) { logger.LogInformation("SQL order {OrderId} risk-blocked: {Reason}", id, risk.Reason); return; }
        account.CashBalance += order.Side == "Buy" ? -notional : notional;
        order.Status = "Filled";
        db.Executions.Add(new TradeExecution { Id = Guid.NewGuid(), AccountId = account.Id, OrderId = order.Id,
            Price = price, Quantity = order.Quantity, ExecutedUtc = DateTimeOffset.UtcNow });
        if (await gate.IsPausedAsync(ct)) return;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Synthetic SQL order {OrderId} filled at {Price}", id, price);
    }
}
