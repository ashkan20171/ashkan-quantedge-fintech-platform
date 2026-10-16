using AshkanQuantEdge.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AshkanQuantEdge;

// A durable, fail-closed coordination flag. NOT an atomic barrier against in-flight fills.
public sealed class SqlTradingGate(IServiceScopeFactory scopes, ILogger<SqlTradingGate> logger)
{
    public async Task<bool> IsPausedAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetService<TradingDbContext>();
            if (db is null) return true;
            var state = await db.TradingControls.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);
            return state?.Paused != false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Trading gate unavailable; failing closed");
            return true;
        }
    }

    public async Task<bool> SetPausedAsync(bool paused, string actor, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var state = await db.TradingControls.SingleOrDefaultAsync(x => x.Id == 1, ct);
        if (state is null) return false; // migration/seed required; never implicitly resume
        state.Paused = paused;
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        state.UpdatedBy = actor;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<object> StatusAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<TradingDbContext>();
        if (db is null) return new { configured = false, paused = true, reason = "SQL not configured" };
        try
        {
            var state = await db.TradingControls.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);
            return new { configured = true, paused = state?.Paused ?? true, initialized = state is not null, updatedUtc = state?.UpdatedUtc, updatedBy = state?.UpdatedBy };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Trading gate status unavailable");
            return new { configured = true, paused = true, reason = "Database unavailable or migration pending" };
        }
    }
}
