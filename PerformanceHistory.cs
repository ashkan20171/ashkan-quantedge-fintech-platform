using System.Text.Json;
namespace AshkanQuantEdge;

public sealed record EquityPoint(DateTimeOffset Time, decimal Equity);

// Local, single-process paper-trading telemetry. Not a transactional financial ledger.
public sealed class PerformanceHistory : BackgroundService
{
    private readonly TradingEngine engine;
    private readonly ILogger<PerformanceHistory> logger;
    private readonly object sync = new();
    private readonly List<EquityPoint> points = new();
    private readonly string path = Path.Combine(AppContext.BaseDirectory, "data", "equity-history.json");
    public PerformanceHistory(TradingEngine engine, ILogger<PerformanceHistory> logger)
    {
        this.engine = engine; this.logger = logger;
        try
        {
            if (File.Exists(path))
                points.AddRange(JsonSerializer.Deserialize<List<EquityPoint>>(File.ReadAllText(path)) ?? new());
            if (points.Count > 2000) points.RemoveRange(0, points.Count - 2000);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Unable to read paper equity history"); }
    }
    public object GetSummary()
    {
        lock(sync)
        {
            var copy = points.ToArray();
            if(copy.Length == 0) return new { mode="paper", points=copy, returnPct=0m, maxDrawdownPct=0m, highWatermark=0m };
            var initial = copy[0].Equity;
            var peak = initial;
            decimal maxDrawdown = 0;
            foreach (var p in copy)
            {
                peak = Math.Max(peak,p.Equity);
                if (peak > 0) maxDrawdown = Math.Max(maxDrawdown, (peak-p.Equity)/peak*100);
            }
            return new { mode="paper", points=copy, returnPct=initial>0?Math.Round((copy[^1].Equity-initial)/initial*100,2):0m,
                maxDrawdownPct=Math.Round(maxDrawdown,2), highWatermark=peak };
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                lock(sync)
                {
                    points.Add(new EquityPoint(DateTimeOffset.UtcNow, engine.GetEquity()));
                    if(points.Count>2000) points.RemoveRange(0,points.Count-2000);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var temp = path+".tmp";
                    File.WriteAllText(temp,JsonSerializer.Serialize(points));
                    File.Move(temp,path,true);
                }
            }
            catch(Exception ex) { logger.LogWarning(ex,"Unable to persist equity history"); }
        } while(await timer.WaitForNextTickAsync(stoppingToken));
    }
}
