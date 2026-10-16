using AshkanQuantEdge.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AshkanQuantEdge;

/// <summary>Read-only FIFO realized P&L from synthetic SQL paper fills.
/// Fees, taxes, corporate actions and external cash flows are not modeled.</summary>
public static class SqlRealizedPnl
{
    public sealed record Fill(string Symbol, string Side, decimal Quantity, decimal Price, DateTimeOffset ExecutedUtc, Guid Id);
    public sealed record SymbolPnl(string Symbol, decimal RealizedUsd, decimal OpenQuantity, decimal OpenCostBasisUsd);
    public sealed record Summary(decimal RealizedTodayUsd, decimal RealizedAllTimeUsd, int MatchedSellFills,
        DateTimeOffset DayStartUtc, DateTimeOffset NextResetUtc, IReadOnlyList<SymbolPnl> Symbols);

    public static async Task<Summary> GetAsync(TradingDbContext db, Guid accountId, DateTimeOffset now, CancellationToken ct)
    {
        var fills = await (from execution in db.Executions.AsNoTracking()
            join order in db.Orders.AsNoTracking() on execution.OrderId equals order.Id
            where execution.AccountId == accountId
            orderby execution.ExecutedUtc, execution.Id
            select new Fill(order.Symbol, order.Side, execution.Quantity, execution.Price, execution.ExecutedUtc, execution.Id))
            .ToListAsync(ct);
        return Calculate(fills, now);
    }

    // Pure deterministic calculation, independently testable without SQL Server.
    public static Summary Calculate(IEnumerable<Fill> fills, DateTimeOffset now)
    {
        var start = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var next = start.AddDays(1);
        var lots = new Dictionary<string, Queue<(decimal Qty, decimal UnitCost)>>(StringComparer.Ordinal);
        var realizedBySymbol = new Dictionary<string, decimal>(StringComparer.Ordinal);
        decimal today = 0m, all = 0m;
        int sellFills = 0;
        foreach (var fill in fills.OrderBy(x => x.ExecutedUtc).ThenBy(x => x.Id))
        {
            if (fill.Quantity <= 0 || fill.Price <= 0 || string.IsNullOrWhiteSpace(fill.Symbol))
                throw new InvalidOperationException("Invalid historical fill; cannot calculate reliable FIFO P&L.");
            if (!lots.TryGetValue(fill.Symbol, out var queue))
                lots[fill.Symbol] = queue = new Queue<(decimal Qty, decimal UnitCost)>();
            if (fill.Side == "Buy") { queue.Enqueue((fill.Quantity, fill.Price)); continue; }
            if (fill.Side != "Sell") throw new InvalidOperationException("Unknown historical fill side.");
            var available = queue.Sum(x => x.Qty);
            if (available < fill.Quantity)
                throw new InvalidOperationException("Historical sell exceeds FIFO inventory; cannot calculate reliable P&L.");
            sellFills++;
            var remaining = fill.Quantity;
            while (remaining > 0)
            {
                var lot = queue.Dequeue();
                var matched = Math.Min(remaining, lot.Qty);
                var profit = checked(matched * (fill.Price - lot.UnitCost));
                all = checked(all + profit);
                if (fill.ExecutedUtc >= start && fill.ExecutedUtc < next) today = checked(today + profit);
                realizedBySymbol[fill.Symbol] = checked(realizedBySymbol.GetValueOrDefault(fill.Symbol) + profit);
                remaining -= matched;
                if (lot.Qty > matched)
                {
                    var rest = queue.ToArray(); queue.Clear();
                    queue.Enqueue((lot.Qty - matched, lot.UnitCost));
                    foreach (var item in rest) queue.Enqueue(item);
                }
            }
        }
        var symbols = lots.Keys.Union(realizedBySymbol.Keys).OrderBy(x => x, StringComparer.Ordinal)
            .Select(symbol => new SymbolPnl(symbol, realizedBySymbol.GetValueOrDefault(symbol),
                lots.TryGetValue(symbol, out var q) ? q.Sum(x => x.Qty) : 0m,
                lots.TryGetValue(symbol, out var costs) ? costs.Sum(x => checked(x.Qty * x.UnitCost)) : 0m))
            .ToArray();
        return new Summary(today, all, sellFills, start, next, symbols);
    }
}
