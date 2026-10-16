using AshkanQuantEdge.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AshkanQuantEdge;

/// <summary>Fail-closed pre-fill risk checks for isolated SQL paper accounts.
/// Call under the account-scoped transaction lock, immediately before writing a fill.</summary>
public static class SqlPreTradeRisk
{
    public const decimal MaxOrderNotionalUsd = 25000m;
    public const decimal MaxGrossExposurePercent = 80m;
    public const decimal MaxDailyTradedNotionalUsd = 100000m;
    public const int MaxDailyFills = 30;
    public sealed record DailyUsage(DateTimeOffset DayStartUtc, decimal TradedNotionalUsd, int FillCount,
        decimal RemainingNotionalUsd, int RemainingFills);

    /// <summary>UTC-based per-account daily fill budget. Must be read under SqlAccountLock
    /// within the same serializable transaction as the eventual execution insert.</summary>
    public static async Task<DailyUsage> GetDailyUsageAsync(TradingDbContext db, Guid accountId,
        DateTimeOffset nowUtc, CancellationToken ct)
    {
        var day = new DateTimeOffset(nowUtc.UtcDateTime.Date, TimeSpan.Zero);
        var next = day.AddDays(1);
        var rows = await db.Executions.AsNoTracking()
            .Where(x => x.AccountId == accountId && x.ExecutedUtc >= day && x.ExecutedUtc < next)
            .Select(x => new { x.Price, x.Quantity }).ToListAsync(ct);
        var notional = rows.Sum(x => checked(x.Price * x.Quantity));
        return new DailyUsage(day, notional, rows.Count,
            Math.Max(0m, MaxDailyTradedNotionalUsd - notional),
            Math.Max(0, MaxDailyFills - rows.Count));
    }

    public sealed record Decision(bool Allowed, string Reason, decimal Equity, decimal GrossExposure,
        decimal ProjectedGrossExposure, decimal ProjectedExposurePercent);

    public static async Task<Decision> EvaluateAsync(TradingDbContext db, TradingEngine market,
        TradingAccount account, TradingOrder order, decimal executionPrice, CancellationToken ct)
    {
        if (order.Quantity <= 0 || executionPrice <= 0 || order.Side is not ("Buy" or "Sell"))
            return Deny("Invalid execution input.");
        decimal notional;
        try { notional = checked(order.Quantity * executionPrice); }
        catch (OverflowException) { return Deny("Execution notional overflow."); }
        if (notional > MaxOrderNotionalUsd) return Deny($"Order exceeds {MaxOrderNotionalUsd:N0} USD execution limit.");
        var daily = await GetDailyUsageAsync(db, account.Id, DateTimeOffset.UtcNow, ct);
        if (daily.FillCount >= MaxDailyFills) return Deny($"Daily limit of {MaxDailyFills} fills reached (UTC).");
        if (notional > daily.RemainingNotionalUsd)
            return Deny($"Daily traded notional budget exceeded. Remaining: {daily.RemainingNotionalUsd:N2} USD (UTC).");

        var fills = await (from e in db.Executions.AsNoTracking()
            join o in db.Orders.AsNoTracking() on e.OrderId equals o.Id
            where e.AccountId == account.Id
            select new { o.Symbol, o.Side, e.Quantity }).ToListAsync(ct);
        var holdings = fills.GroupBy(x => x.Symbol).ToDictionary(g => g.Key,
            g => g.Sum(x => x.Side == "Buy" ? x.Quantity : -x.Quantity));
        var gross = 0m;
        var net = 0m;
        foreach (var (symbol, quantity) in holdings)
        {
            if (quantity == 0) continue;
            if (!market.TryGetPaperPrice(symbol, out var mark) || mark <= 0)
                return Deny($"No valid synthetic mark for {symbol}; execution blocked.");
            var value = checked(quantity * mark);
            gross += Math.Abs(value);
            net += value;
        }
        var equity = account.CashBalance + net;
        if (equity <= 0) return Deny("Non-positive account equity.");
        holdings.TryGetValue(order.Symbol, out var held);
        var delta = order.Side == "Buy" ? order.Quantity : -order.Quantity;
        var projected = held + delta;
        if (projected < 0) return Deny("Short selling is disabled.");
        // Existing symbol is repriced at the same quote used for the execution.
        var projectedGross = gross - Math.Abs(held * executionPrice) + Math.Abs(projected * executionPrice);
        var projectedPct = 100m * projectedGross / equity;
        if (projectedGross > gross && projectedPct > MaxGrossExposurePercent)
            return new(false, $"Projected exposure {projectedPct:N2}% exceeds {MaxGrossExposurePercent}% limit.", equity, gross, projectedGross, projectedPct);
        return new(true, "Within pre-trade limits.", equity, gross, projectedGross, projectedPct);
    }

    private static Decision Deny(string reason) => new(false, reason, 0m, 0m, 0m, 0m);
}
