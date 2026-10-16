using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace AshkanQuantEdge.Persistence;
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<TradingDbContext>
{
    public TradingDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("QE_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Set QE_SQL_CONNECTION before running EF migrations.");
        return new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlServer(connection).Options);
    }
}
