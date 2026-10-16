using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
namespace AshkanQuantEdge;
public record OrderRequest(string Symbol,string Side,int Quantity);
public record LimitOrderRequest(string Symbol,string Side,int Quantity,decimal LimitPrice);
public record PriceAlertRequest(string Symbol,string Direction,decimal TargetPrice);
public record PriceAlert(Guid Id,DateTimeOffset CreatedAt,string Symbol,string Direction,decimal TargetPrice,bool Triggered=false,DateTimeOffset? TriggeredAt=null);
public record StopOrderRequest(string Symbol,string Side,int Quantity,decimal StopPrice);
public record StopOrder(Guid Id,DateTimeOffset CreatedAt,string Symbol,string Side,int Quantity,decimal StopPrice);
public record PendingOrder(Guid Id,DateTimeOffset CreatedAt,string Symbol,string Side,int Quantity,decimal LimitPrice);
public record RiskSettings(decimal MaxOrderNotional=50000m,int MaxOrderQuantity=1000,decimal MaxExposurePct=80m);
public record Order(Guid Id,DateTimeOffset Time,string Symbol,string Side,int Quantity,decimal Price,decimal Notional,string Status);
public record OrderResult(Order? Order,string? Error);
public sealed class MarketHub:Microsoft.AspNetCore.SignalR.Hub { }
public sealed class TradingEngine {
 private readonly object gate=new();
 private readonly Random random=new();
 private readonly Dictionary<string,decimal> prices=new(){{"BTCUSD",67280m},{"ETHUSD",3540m},{"SOLUSD",172m},{"AAPL",228m},{"NVDA",142m},{"EURUSD",1.0842m}};
 private readonly Dictionary<string,Queue<object>> history=new();
 private readonly List<Order> orders=new();
 private readonly List<PendingOrder> pending=new();
 private readonly List<StopOrder> stops=new();
 private readonly List<PriceAlert> alerts=new();
 private readonly List<AuditEntry> audit=new();
 private readonly Dictionary<string,Queue<Candle>> candles=new();
 private readonly Dictionary<string,decimal> initialPrices=new();
 public record Candle(DateTimeOffset Time,decimal Open,decimal High,decimal Low,decimal Close);
 public record AuditEntry(DateTimeOffset Time,string Action,string Detail);
 private readonly Dictionary<string,int> positions=new();
 private decimal cash=100000m;private decimal realized=0;
 private RiskSettings riskSettings=new();
 private readonly string statePath=Path.Combine(AppContext.BaseDirectory,"data","paper-account.json");
 private sealed record AccountSnapshot(decimal Cash,List<Order> Orders,Dictionary<string,int> Positions,List<AuditEntry>? Audit=null,RiskSettings? Risk=null,List<PendingOrder>? Pending=null,List<StopOrder>? Stops=null,List<PriceAlert>? Alerts=null);
 private void SaveState(){var folder=Path.GetDirectoryName(statePath)!;Directory.CreateDirectory(folder);var temp=statePath+".tmp";File.WriteAllText(temp,System.Text.Json.JsonSerializer.Serialize(new AccountSnapshot(cash,orders,positions,audit,riskSettings,pending,stops,alerts)));File.Move(temp,statePath,true);}
 private void LoadState(){if(!File.Exists(statePath))return;try{var snapshot=System.Text.Json.JsonSerializer.Deserialize<AccountSnapshot>(File.ReadAllText(statePath));if(snapshot is null||snapshot.Cash<0||snapshot.Orders is null||snapshot.Positions is null)return;cash=snapshot.Cash;if(snapshot.Risk is { } r && IsValidRisk(r))riskSettings=r;orders.Clear();orders.AddRange(snapshot.Orders);pending.Clear();if(snapshot.Pending is not null)pending.AddRange(snapshot.Pending.Where(x=>prices.ContainsKey(x.Symbol)&&x.Quantity>0&&x.LimitPrice>0));stops.Clear();if(snapshot.Stops is not null)stops.AddRange(snapshot.Stops.Where(x=>prices.ContainsKey(x.Symbol)&&x.Quantity>0&&x.StopPrice>0));alerts.Clear();if(snapshot.Alerts is not null)alerts.AddRange(snapshot.Alerts.Where(x=>prices.ContainsKey(x.Symbol)&&x.TargetPrice>0).TakeLast(200));audit.Clear();if(snapshot.Audit is not null)audit.AddRange(snapshot.Audit.TakeLast(500));positions.Clear();foreach(var pair in snapshot.Positions)if(prices.ContainsKey(pair.Key)&&pair.Value>=0)positions[pair.Key]=pair.Value;}catch(System.Text.Json.JsonException){/* Invalid state: start a new paper session. */}}
 private static bool IsValidRisk(RiskSettings r)=>r.MaxOrderNotional>=100m&&r.MaxOrderNotional<=1000000m&&r.MaxOrderQuantity>=1&&r.MaxOrderQuantity<=100000&&r.MaxExposurePct>=1m&&r.MaxExposurePct<=100m;
 public RiskSettings GetRiskSettings(){lock(gate)return riskSettings;}
 public (RiskSettings? Value,string? Error) UpdateRiskSettings(RiskSettings settings){lock(gate){if(!IsValidRisk(settings))return (null,"Invalid risk settings: notional 100-1000000, quantity 1-100000, exposure 1-100%");riskSettings=settings;audit.Add(new AuditEntry(DateTimeOffset.UtcNow,"RISK_UPDATED",$"Notional {settings.MaxOrderNotional}; Quantity {settings.MaxOrderQuantity}; Exposure {settings.MaxExposurePct}%"));if(audit.Count>500)audit.RemoveAt(0);SaveState();return (riskSettings,null);}}
 public object GetAnalytics(){lock(gate){var equity=cash+positions.Sum(p=>p.Value*prices[p.Key]);var gross=positions.Sum(p=>Math.Abs(p.Value*prices[p.Key]));return new {equity,cash,grossExposure=gross,exposurePct=equity>0?Math.Round(gross/equity*100,2):0,unrealizedPnl=positions.Sum(p=>p.Value*prices[p.Key])-orders.Sum(o=>o.Side=="BUY"?o.Notional:-o.Notional),filledOrders=orders.Count,buyOrders=orders.Count(o=>o.Side=="BUY"),sellOrders=orders.Count(o=>o.Side=="SELL"),positions=positions.Where(p=>p.Value>0).Select(p=>new {symbol=p.Key,quantity=p.Value,marketValue=p.Value*prices[p.Key],allocationPct=equity>0?Math.Round(p.Value*prices[p.Key]/equity*100,2):0}).ToArray()};}}
 public TradingEngine(){foreach(var (s,p) in prices){var q=new Queue<object>();for(int i=59;i>=0;i--)q.Enqueue(new {time=DateTimeOffset.UtcNow.AddSeconds(-i*2),price=p});history[s]=q;initialPrices[s]=p;var bars=new Queue<Candle>();for(var i=59;i>=0;i--){var t=DateTimeOffset.UtcNow.AddMinutes(-i);bars.Enqueue(new Candle(t,p,p,p,p));}candles[s]=bars;}LoadState();}
 public object GetCandles(string symbol,int limit=60){lock(gate){return candles.TryGetValue(symbol.ToUpperInvariant(),out var q)?q.TakeLast(Math.Clamp(limit,1,120)).ToArray():Array.Empty<Candle>();}}
 public object GetAudit(){lock(gate)return audit.TakeLast(100).Reverse().ToArray();}
 public bool TryGetPaperPrice(string symbol, out decimal price){lock(gate)return prices.TryGetValue(symbol.ToUpperInvariant(),out price);}
 public object GetMarket(){lock(gate)return prices.Select(x=>new {symbol=x.Key,price=x.Value,change=0m}).ToArray();}
 public decimal GetEquity(){lock(gate){return cash+positions.Sum(p=>p.Value*prices[p.Key]);}}
 public object GetAccount(){lock(gate){var exposure=positions.Sum(p=>p.Value*prices[p.Key]);return new {cash,equity=cash+exposure,exposure,realized,positions=positions.Select(p=>new {symbol=p.Key,quantity=p.Value,price=prices[p.Key],marketValue=p.Value*prices[p.Key]}).ToArray()};}}
 public object GetOrders(){lock(gate)return orders.AsEnumerable().Reverse().Take(100).ToArray();}
 public object GetTrades()=>GetOrders();
 public object GetRiskSummary(){lock(gate){var exposure=positions.Sum(p=>Math.Abs(p.Value*prices[p.Key]));var equity=cash+positions.Sum(p=>p.Value*prices[p.Key]);return new {mode="PAPER",currency="USD",equity,cash,grossExposure=exposure,exposureRatio=equity>0?Math.Round(exposure/equity,4):0m,maxOrderNotional=riskSettings.MaxOrderNotional,maxOrderQuantity=riskSettings.MaxOrderQuantity,maxExposurePct=riskSettings.MaxExposurePct,shortSellingEnabled=false,openPositions=positions.Count(p=>p.Value!=0),filledOrders=orders.Count};}}
 public object GetHistory(string symbol){lock(gate)return history.TryGetValue(symbol.ToUpperInvariant(),out var q)?q.ToArray():Array.Empty<object>();}
 public OrderResult PlaceOrder(OrderRequest req){lock(gate){var sym=req.Symbol?.ToUpperInvariant()??"";var side=req.Side?.ToUpperInvariant()??"";
 if(!prices.TryGetValue(sym,out var price))return new(null,"Unsupported symbol");
 if(side is not ("BUY" or "SELL"))return new(null,"Side must be BUY or SELL");
 if(req.Quantity<1||req.Quantity>riskSettings.MaxOrderQuantity)return new(null,$"Quantity must be between 1 and {riskSettings.MaxOrderQuantity}");
 var notional=price*req.Quantity;if(notional>riskSettings.MaxOrderNotional)return new(null,$"Risk limit: maximum order notional is ${riskSettings.MaxOrderNotional}");
 positions.TryGetValue(sym,out var held);
 if(side=="BUY"&&cash<notional)return new(null,"Insufficient paper cash");
 if(side=="BUY") {var exposure=positions.Sum(p=>p.Value*prices[p.Key]);var equity=cash+exposure;if(equity<=0||((exposure+notional)/equity*100m)>riskSettings.MaxExposurePct)return new(null,$"Risk limit: portfolio exposure exceeds {riskSettings.MaxExposurePct}%");}
 if(side=="SELL"&&held<req.Quantity)return new(null,"Insufficient position (short selling disabled)");
 cash+=side=="BUY"?-notional:notional;positions[sym]=held+(side=="BUY"?req.Quantity:-req.Quantity);
 var order=new Order(Guid.NewGuid(),DateTimeOffset.UtcNow,sym,side,req.Quantity,price,notional,"FILLED");orders.Add(order);audit.Add(new AuditEntry(DateTimeOffset.UtcNow,"ORDER_FILLED",$"{side} {req.Quantity} {sym} @ {price}"));if(audit.Count>500)audit.RemoveAt(0);SaveState();return new(order,null);
 }}
 public object GetStopOrders(){lock(gate)return stops.OrderByDescending(x=>x.CreatedAt).ToArray();}
 public (StopOrder? Order,string? Error) PlaceStopOrder(StopOrderRequest req){lock(gate){
 var sym=req.Symbol?.Trim().ToUpperInvariant()??"";var side=req.Side?.Trim().ToUpperInvariant()??"";
 if(!prices.TryGetValue(sym,out var market))return(null,"Unsupported symbol");
 if(side is not ("BUY" or "SELL"))return(null,"Invalid side");
 if(req.Quantity<1||req.Quantity>riskSettings.MaxOrderQuantity)return(null,"Quantity exceeds risk limits");
 if(req.StopPrice<=0||req.StopPrice>100000000m)return(null,"Invalid stop price");
 if(req.Quantity*Math.Max(market,req.StopPrice)>riskSettings.MaxOrderNotional)return(null,"Stop order exceeds notional risk limit");
 if(side=="BUY"&&req.StopPrice<=market)return(null,"Buy stop must be above market price");
 if(side=="SELL"&&req.StopPrice>=market)return(null,"Sell stop must be below market price");
 if(stops.Count>=200)return(null,"Maximum stop orders reached");
 var order=new StopOrder(Guid.NewGuid(),DateTimeOffset.UtcNow,sym,side,req.Quantity,req.StopPrice);
 stops.Add(order);audit.Add(new AuditEntry(DateTimeOffset.UtcNow,"STOP_CREATED",$"{side} {req.Quantity} {sym} @ {req.StopPrice}"));TrimAudit();SaveState();return(order,null);
 }}
 public bool CancelStop(Guid id){lock(gate){var index=stops.FindIndex(x=>x.Id==id);if(index<0)return false;stops.RemoveAt(index);audit.Add(new AuditEntry(DateTimeOffset.UtcNow,"STOP_CANCELLED",id.ToString()));TrimAudit();SaveState();return true;}}
 private void ExecuteStops(){foreach(var order in stops.ToArray()){
 var price=prices[order.Symbol];if(order.Side=="BUY"&&price<order.StopPrice||order.Side=="SELL"&&price>order.StopPrice)continue;
 // A triggered stop is a market order; fill-time risk checks may reject it.
 var result=PlaceOrder(new OrderRequest(order.Symbol,order.Side,order.Quantity));
 stops.Remove(order);
 audit.Add(new AuditEntry(DateTimeOffset.UtcNow,result.Error is null?"STOP_FILLED":"STOP_REJECTED",result.Error??order.Id.ToString()));TrimAudit();SaveState();
 }}
 public object GetPendingOrders(){lock(gate)return pending.OrderByDescending(x=>x.CreatedAt).ToArray();}
 public (PendingOrder? Order,string? Error) PlaceLimitOrder(LimitOrderRequest req){lock(gate){
 var sym=req.Symbol?.Trim().ToUpperInvariant()??"";var side=req.Side?.Trim().ToUpperInvariant()??"";
 if(!prices.ContainsKey(sym))return (null,"Unsupported symbol");
 if(side is not ("BUY" or "SELL"))return (null,"Invalid side");
 if(req.Quantity<1||req.Quantity>riskSettings.MaxOrderQuantity)return (null,"Quantity exceeds risk limits");
 if(req.LimitPrice<=0||req.LimitPrice>100000000m)return (null,"Invalid limit price");
 if(req.Quantity*req.LimitPrice>riskSettings.MaxOrderNotional)return (null,"Limit notional exceeds risk limit");
 if(pending.Count>=200)return (null,"Maximum pending orders reached");
 var order=new PendingOrder(Guid.NewGuid(),DateTimeOffset.UtcNow,sym,side,req.Quantity,req.LimitPrice);
 pending.Add(order);audit.Add(new AuditEntry(DateTimeOffset.UtcNow,"LIMIT_CREATED",$"{side} {req.Quantity} {sym} @ {req.LimitPrice}"));TrimAudit();SaveState();return(order,null);
 }}
 public bool CancelPending(Guid id){lock(gate){var index=pending.FindIndex(x=>x.Id==id);if(index<0)return false;var order=pending[index];pending.RemoveAt(index);audit.Add(new AuditEntry(DateTimeOffset.UtcNow,"LIMIT_CANCELLED",order.Id.ToString()));TrimAudit();SaveState();return true;}}
 private void TrimAudit(){if(audit.Count>500)audit.RemoveRange(0,audit.Count-500);}
 private void ExecutePending(){
 var changed=false;
 foreach(var order in pending.ToArray()){
  var price=prices[order.Symbol];
  if(order.Side=="BUY"&&price>order.LimitPrice||order.Side=="SELL"&&price<order.LimitPrice)continue;
  // Reuse the market execution path so cash, position and exposure rules are enforced at fill time.
  var result=PlaceOrder(new OrderRequest(order.Symbol,order.Side,order.Quantity));
  if(result.Error is not null)continue;
  pending.Remove(order);audit.Add(new AuditEntry(DateTimeOffset.UtcNow,"LIMIT_FILLED",order.Id.ToString()));changed=true;
 }
 if(changed){TrimAudit();SaveState();}
 }

 public object GetOrderBook(string symbol,int depth=10){lock(gate){
  symbol=(symbol??"").Trim().ToUpperInvariant();if(!prices.TryGetValue(symbol,out var mid))return null!;
  depth=Math.Clamp(depth,1,20);var tick=mid<2m?0.0001m:mid<100m?0.01m:0.1m;
  var spread=Math.Max(tick,Math.Round(mid*0.0002m/tick,0,MidpointRounding.AwayFromZero)*tick);
  var bid=mid-spread/2;var ask=mid+spread/2;
  var bids=Enumerable.Range(0,depth).Select(i=>new {price=Math.Max(tick,Math.Round((bid-i*tick)/tick,0)*tick),quantity=10+(i*7+symbol.Length*13)%85}).ToArray();
  var asks=Enumerable.Range(0,depth).Select(i=>new {price=Math.Round((ask+i*tick)/tick,0)*tick,quantity=10+(i*11+symbol.Length*17)%85}).ToArray();
  return new {symbol,mid,spread,mode="SYNTHETIC",bids,asks,updatedAt=DateTimeOffset.UtcNow};
 }}
 public object GetAlerts(){lock(gate)return alerts.OrderByDescending(x=>x.CreatedAt).ToArray();}
 public (PriceAlert? Alert,string? Error) CreateAlert(PriceAlertRequest req){lock(gate){
  var symbol=(req.Symbol??"").Trim().ToUpperInvariant();var direction=(req.Direction??"").Trim().ToUpperInvariant();
  if(!prices.ContainsKey(symbol))return(null,"Unsupported symbol");
  if(direction is not ("ABOVE" or "BELOW"))return(null,"Direction must be ABOVE or BELOW");
  if(req.TargetPrice<=0||req.TargetPrice>100000000m)return(null,"Invalid target price");
  if(alerts.Count(x=>!x.Triggered)>=100)return(null,"Maximum active alerts reached");
  var alert=new PriceAlert(Guid.NewGuid(),DateTimeOffset.UtcNow,symbol,direction,req.TargetPrice);
  alerts.Add(alert);audit.Add(new AuditEntry(DateTimeOffset.UtcNow,"ALERT_CREATED",$"{symbol} {direction} {req.TargetPrice}"));TrimAudit();SaveState();return(alert,null);
 }}
 public bool DeleteAlert(Guid id){lock(gate){var a=alerts.FirstOrDefault(x=>x.Id==id);if(a is null)return false;alerts.Remove(a);audit.Add(new AuditEntry(DateTimeOffset.UtcNow,"ALERT_REMOVED",id.ToString()));TrimAudit();SaveState();return true;}}
 private void EvaluateAlerts(){var changed=false;for(var i=0;i<alerts.Count;i++){var a=alerts[i];if(a.Triggered)continue;var p=prices[a.Symbol];if(a.Direction=="ABOVE"?p>=a.TargetPrice:p<=a.TargetPrice){alerts[i]=a with {Triggered=true,TriggeredAt=DateTimeOffset.UtcNow};audit.Add(new AuditEntry(DateTimeOffset.UtcNow,"ALERT_TRIGGERED",$"{a.Symbol} {a.Direction} {a.TargetPrice}; market {p}"));changed=true;}}if(changed){TrimAudit();SaveState();}}
 public void Reset(){lock(gate){cash=100000m;realized=0;orders.Clear();pending.Clear();stops.Clear();positions.Clear();audit.Add(new AuditEntry(DateTimeOffset.UtcNow,"ACCOUNT_RESET","Paper portfolio reset"));if(audit.Count>500)audit.RemoveAt(0);SaveState();}}
 public object Tick(){lock(gate){foreach(var sym in prices.Keys.ToArray()){var p=prices[sym];var move=(decimal)(random.NextDouble()-.5)*.002m;var next=Math.Max(.0001m,Math.Round(p*(1+move),sym=="EURUSD"?5:2));prices[sym]=next;var bars=candles[sym];var last=bars.Last();var now=DateTimeOffset.UtcNow; if(last.Time.Year==now.Year&&last.Time.DayOfYear==now.DayOfYear&&last.Time.Hour==now.Hour&&last.Time.Minute==now.Minute){var values=bars.ToArray();bars.Clear();for(int i=0;i<values.Length-1;i++)bars.Enqueue(values[i]);bars.Enqueue(last with {High=Math.Max(last.High,next),Low=Math.Min(last.Low,next),Close=next});}else{bars.Enqueue(new Candle(now,next,next,next,next));if(bars.Count>120)bars.Dequeue();}var q=history[sym];q.Enqueue(new {time=DateTimeOffset.UtcNow,price=next});while(q.Count>120)q.Dequeue();}ExecutePending();ExecuteStops();EvaluateAlerts();return GetMarket();}}
}
public sealed class MarketWorker:BackgroundService {
 private readonly TradingEngine engine;private readonly Microsoft.AspNetCore.SignalR.IHubContext<MarketHub> hub;
 public MarketWorker(TradingEngine engine,Microsoft.AspNetCore.SignalR.IHubContext<MarketHub> hub){this.engine=engine;this.hub=hub;}
 protected override async Task ExecuteAsync(CancellationToken token){using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));try{while(await timer.WaitForNextTickAsync(token)){var data=engine.Tick();await hub.Clients.All.SendAsync("marketUpdate",data,token);}}catch(OperationCanceledException){} }
}
