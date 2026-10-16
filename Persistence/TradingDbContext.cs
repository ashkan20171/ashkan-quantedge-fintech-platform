using Microsoft.EntityFrameworkCore;
namespace AshkanQuantEdge.Persistence;

// SQL Server persistence foundation. Not yet connected to the legacy shared trading engine.
public sealed class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
    public DbSet<TradingControlState> TradingControls => Set<TradingControlState>();
    public DbSet<TradingAccount> Accounts => Set<TradingAccount>();
    public DbSet<TradingOrder> Orders => Set<TradingOrder>();
    public DbSet<TradeExecution> Executions => Set<TradeExecution>();
    public DbSet<SecurityAuditRecord> SecurityEvents => Set<SecurityAuditRecord>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<TradingControlState>(e => { e.HasKey(x => x.Id); e.Property(x => x.RowVersion).IsRowVersion(); e.Property(x => x.UpdatedBy).HasMaxLength(128); });
        model.Entity<TradingAccount>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.OwnerId).IsUnique(); e.Property(x => x.CashBalance).HasPrecision(19,4); e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.RowVersion).IsRowVersion(); });
        model.Entity<TradingOrder>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.AccountId, x.CreatedUtc }); e.Property(x => x.Symbol).HasMaxLength(32); e.Property(x => x.Side).HasMaxLength(8); e.Property(x => x.Kind).HasMaxLength(12); e.Property(x => x.Status).HasMaxLength(20); e.Property(x => x.Quantity).HasPrecision(19,8); e.Property(x => x.LimitPrice).HasPrecision(19,8); e.Property(x => x.StopPrice).HasPrecision(19,8); e.HasOne<TradingAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<TradeExecution>(e => { e.HasKey(x => x.Id); e.HasIndex(x => new { x.AccountId, x.ExecutedUtc }); e.Property(x => x.Price).HasPrecision(19,8); e.Property(x => x.Quantity).HasPrecision(19,8); e.HasOne<TradingOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<SecurityAuditRecord>(e => { e.HasKey(x => x.Id); e.HasIndex(x => x.AtUtc); e.Property(x => x.Action).HasMaxLength(80); e.Property(x => x.ActorId).HasMaxLength(450); e.Property(x => x.Source).HasMaxLength(128); });
    }
}
public sealed class TradingAccount { public Guid Id {get;set;} public string OwnerId {get;set;} = ""; public string Currency {get;set;} = "USD"; public decimal CashBalance {get;set;} public byte[] RowVersion {get;set;} = []; }
public sealed class TradingOrder { public Guid Id {get;set;} public Guid AccountId {get;set;} public string Symbol {get;set;} = ""; public string Side {get;set;} = ""; public string Kind {get;set;} = ""; public string Status {get;set;} = ""; public decimal Quantity {get;set;} public decimal? LimitPrice {get;set;} public decimal? StopPrice {get;set;} public DateTimeOffset CreatedUtc {get;set;} }
public sealed class TradeExecution { public Guid Id {get;set;} public Guid AccountId {get;set;} public Guid OrderId {get;set;} public decimal Price {get;set;} public decimal Quantity {get;set;} public DateTimeOffset ExecutedUtc {get;set;} }
public sealed class SecurityAuditRecord { public long Id {get;set;} public DateTimeOffset AtUtc {get;set;} public string Action {get;set;} = ""; public string ActorId {get;set;} = ""; public string Source {get;set;} = ""; }

public sealed class TradingControlState { public int Id {get;set;} = 1; public bool Paused {get;set;} = true; public DateTimeOffset UpdatedUtc {get;set;} = DateTimeOffset.UtcNow; public string UpdatedBy {get;set;} = "system"; public byte[] RowVersion {get;set;} = []; }
