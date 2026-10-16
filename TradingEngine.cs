using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
namespace AshkanQuantEdge;
public record OrderRequest(string Symbol,string Side,int Quantity);
public record Order(Guid Id,DateTimeOffset Time,string Symbol,string Side,int Quantity,decimal Price,decimal Notional,string Status);
public record OrderResult(Order? Order,string? Error);
public sealed class MarketHub:Microsoft.AspNetCore.SignalR.Hub { }
public sealed class TradingEngine {
 private readonly object gate=new();
 private readonly Random random=new();
 private readonly Dictionary<string,decimal> prices=new(){{"BTCUSD",67280m},{"ETHUSD",3540m},{"SOLUSD",172m},{"AAPL",228m},{"NVDA",142m},{"EURUSD",1.0842m}};
 private readonly Dictionary<string,Queue<object>> history=new();
 private readonly List<Order> orders=new();
 private readonly Dictionary<string,int> positions=new();
 private decimal cash=100000m;private decimal realized=0;
 public TradingEngine(){foreach(var (s,p) in prices){var q=new Queue<object>();for(int i=59;i>=0;i--)q.Enqueue(new {time=DateTimeOffset.UtcNow.AddSeconds(-i*2),price=p});history[s]=q;}}
 public object GetMarket(){lock(gate)return prices.Select(x=>new {symbol=x.Key,price=x.Value,change=0m}).ToArray();}
 public object GetAccount(){lock(gate){var exposure=positions.Sum(p=>p.Value*prices[p.Key]);return new {cash,equity=cash+exposure,exposure,realized,positions=positions.Select(p=>new {symbol=p.Key,quantity=p.Value,price=prices[p.Key],marketValue=p.Value*prices[p.Key]}).ToArray()};}}
 public object GetOrders(){lock(gate)return orders.AsEnumerable().Reverse().Take(100).ToArray();}
 public object GetTrades()=>GetOrders();
 public object GetRiskSummary(){lock(gate){var exposure=positions.Sum(p=>Math.Abs(p.Value*prices[p.Key]));var equity=cash+positions.Sum(p=>p.Value*prices[p.Key]);return new {mode="PAPER",currency="USD",equity,cash,grossExposure=exposure,exposureRatio=equity>0?Math.Round(exposure/equity,4):0m,maxOrderNotional=50000m,maxOrderQuantity=1000,shortSellingEnabled=false,openPositions=positions.Count(p=>p.Value!=0),filledOrders=orders.Count};}}
 public object GetHistory(string symbol){lock(gate)return history.TryGetValue(symbol.ToUpperInvariant(),out var q)?q.ToArray():Array.Empty<object>();}
 public OrderResult PlaceOrder(OrderRequest req){lock(gate){var sym=req.Symbol?.ToUpperInvariant()??"";var side=req.Side?.ToUpperInvariant()??"";
 if(!prices.TryGetValue(sym,out var price))return new(null,"Unsupported symbol");
 if(side is not ("BUY" or "SELL"))return new(null,"Side must be BUY or SELL");
 if(req.Quantity<1||req.Quantity>1000)return new(null,"Quantity must be between 1 and 1000");
 var notional=price*req.Quantity;if(notional>50000m)return new(null,"Risk limit: maximum order notional is $50,000");
 positions.TryGetValue(sym,out var held);
 if(side=="BUY"&&cash<notional)return new(null,"Insufficient paper cash");
 if(side=="SELL"&&held<req.Quantity)return new(null,"Insufficient position (short selling disabled)");
 cash+=side=="BUY"?-notional:notional;positions[sym]=held+(side=="BUY"?req.Quantity:-req.Quantity);
 var order=new Order(Guid.NewGuid(),DateTimeOffset.UtcNow,sym,side,req.Quantity,price,notional,"FILLED");orders.Add(order);return new(order,null);
 }}
 public void Reset(){lock(gate){cash=100000m;realized=0;orders.Clear();positions.Clear();}}
 public object Tick(){lock(gate){foreach(var sym in prices.Keys.ToArray()){var p=prices[sym];var move=(decimal)(random.NextDouble()-.5)*.002m;var next=Math.Max(.0001m,Math.Round(p*(1+move),sym=="EURUSD"?5:2));prices[sym]=next;var q=history[sym];q.Enqueue(new {time=DateTimeOffset.UtcNow,price=next});while(q.Count>120)q.Dequeue();}return GetMarket();}}
}
public sealed class MarketWorker:BackgroundService {
 private readonly TradingEngine engine;private readonly Microsoft.AspNetCore.SignalR.IHubContext<MarketHub> hub;
 public MarketWorker(TradingEngine engine,Microsoft.AspNetCore.SignalR.IHubContext<MarketHub> hub){this.engine=engine;this.hub=hub;}
 protected override async Task ExecuteAsync(CancellationToken token){using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));try{while(await timer.WaitForNextTickAsync(token)){var data=engine.Tick();await hub.Clients.All.SendAsync("marketUpdate",data,token);}}catch(OperationCanceledException){} }
}
