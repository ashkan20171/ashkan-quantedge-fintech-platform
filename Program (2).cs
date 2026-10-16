using Microsoft.EntityFrameworkCore;
using AshkanQuantEdge.Persistence;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using AshkanQuantEdge;
var builder=WebApplication.CreateBuilder(args);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
 .AddCookie(options => { options.Cookie.Name="QuantEdge.Session"; options.Cookie.HttpOnly=true;
 options.Cookie.SecurePolicy=CookieSecurePolicy.SameAsRequest;
 options.Cookie.SameSite=SameSiteMode.Strict;
 options.Cookie.IsEssential=true; options.LoginPath="/login.html";
 options.Events.OnRedirectToLogin=ctx=>{ctx.Response.StatusCode=401;return Task.CompletedTask;};
 options.Events.OnRedirectToAccessDenied=ctx=>{ctx.Response.StatusCode=403;return Task.CompletedTask;};
 options.ExpireTimeSpan=TimeSpan.FromHours(8); });
builder.Services.AddAuthorization();
builder.Services.AddSingleton<SecurityAuditStore>();
// SQL persistence is opt-in. Existing paper trading remains file-backed.
var sqlConnection = Environment.GetEnvironmentVariable("QE_SQL_CONNECTION");
if (!string.IsNullOrWhiteSpace(sqlConnection))
    builder.Services.AddDbContext<TradingDbContext>(options => options.UseSqlServer(sqlConnection));

builder.Services.AddSingleton<LoginAttemptGuard>();
builder.Services.AddSingleton<TradingSafety>();
builder.Services.AddSingleton<SqlTradingGate>();
builder.Services.AddAntiforgery();
builder.Services.AddSignalR();
builder.Services.AddSingleton<TradingEngine>();
builder.Services.AddHostedService<MarketWorker>();
builder.Services.AddHostedService<SqlAutoExecutionWorker>();
builder.Services.AddSingleton<PerformanceHistory>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PerformanceHistory>());
builder.Services.AddCors(options=>options.AddPolicy("local",p=>p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));
var app=builder.Build();
app.Use(async (ctx, next) => {
 ctx.Response.Headers.XContentTypeOptions = "nosniff";
 ctx.Response.Headers.XFrameOptions = "DENY";
 ctx.Response.Headers.ReferrerPolicy = "no-referrer";
 ctx.Response.Headers.CacheControl = "no-store";
 await next();
});
app.UseDefaultFiles();app.UseStaticFiles();
app.UseAuthentication();app.UseAuthorization();
// Require an authenticated session and same-origin requests for every state-changing API call.
app.Use(async (ctx,next)=>{
 if(ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/api/auth") && !ctx.Request.Path.StartsWithSegments("/api/health")) {
  if(ctx.User.Identity?.IsAuthenticated!=true){ctx.Response.StatusCode=401;return;}
 }
 if(ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Method is not ("GET" or "HEAD" or "OPTIONS") && !ctx.Request.Path.StartsWithSegments("/api/auth")) {
  if(ctx.User.Identity?.IsAuthenticated!=true){ctx.Response.StatusCode=401;return;}
  var origin=ctx.Request.Headers.Origin.ToString();
  var referer=ctx.Request.Headers.Referer.ToString();
  var expected=$"{ctx.Request.Scheme}://{ctx.Request.Host}";
  if(!string.Equals(origin,expected,StringComparison.OrdinalIgnoreCase) &&
     !(string.IsNullOrEmpty(origin) && Uri.TryCreate(referer,UriKind.Absolute,out var refUri) &&
       string.Equals(refUri.GetLeftPart(UriPartial.Authority),expected,StringComparison.OrdinalIgnoreCase))){ctx.Response.StatusCode=403;return;}
  if(ctx.Request.Path.StartsWithSegments("/api/reset") || ctx.Request.Path.StartsWithSegments("/api/risk/settings")){
   if(!ctx.User.IsInRole("Admin")){ctx.Response.StatusCode=403;return;}
  } else if(!ctx.User.IsInRole("Admin") && !ctx.User.IsInRole("Trader")){ctx.Response.StatusCode=403;return;}
 }
 await next();
});
// Emergency safety control: paused on startup; Admin must explicitly resume.
app.MapGet("/api/admin/trading-safety", async (SqlTradingGate gate, CancellationToken ct) => Results.Ok(await gate.StatusAsync(ct))).RequireAuthorization(policy => policy.RequireRole("Admin"));
app.MapPost("/api/admin/trading-safety/pause", async (SqlTradingGate gate, TradingSafety safety, SecurityAuditStore audit, ClaimsPrincipal user, CancellationToken ct) => {
 safety.SetPaused(true);
 try { if (!await gate.SetPausedAsync(true, user.Identity?.Name ?? "unknown", ct)) return Results.Conflict(new { error = "SQL safety row not initialized. Apply migration and seed." }); }
 catch (Exception) { return Results.StatusCode(503); }
 audit.Add("SQL_TRADING_PAUSED", user.Identity?.Name ?? "unknown", "api");
 return Results.Ok(new { paused = true, scope = "sql-shared" });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));
app.MapPost("/api/admin/trading-safety/resume", async (SqlTradingGate gate, TradingSafety safety, SecurityAuditStore audit, ClaimsPrincipal user, CancellationToken ct) => {
 try { if (!await gate.SetPausedAsync(false, user.Identity?.Name ?? "unknown", ct)) return Results.Conflict(new { error = "SQL safety row not initialized. Apply migration and seed." }); }
 catch (Exception) { return Results.StatusCode(503); }
 safety.SetPaused(false);
 audit.Add("SQL_TRADING_RESUMED", user.Identity?.Name ?? "unknown", "api");
 return Results.Ok(new { paused = false, scope = "sql-shared" });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));
// Operational diagnostics are intentionally administrator-only and contain no secrets.
app.MapGet("/api/admin/operations/summary", async (IServiceProvider services, SqlTradingGate gate, CancellationToken ct) => {
    var safety = await gate.StatusAsync(ct);
    if (string.IsNullOrWhiteSpace(sqlConnection))
        return Results.Ok(new { mode = "synthetic-paper-only", sqlConfigured = false, sqlConnected = false,
            safety, autoExecutionConfigured = false, pendingOrders = (int?)null, executedTrades = (int?)null,
            accounts = (int?)null, generatedUtc = DateTimeOffset.UtcNow });
    try {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var connected = await db.Database.CanConnectAsync(ct);
        if (!connected) return Results.Ok(new { mode = "synthetic-paper-only", sqlConfigured = true,
            sqlConnected = false, safety, autoExecutionConfigured = false,
            pendingOrders = (int?)null, executedTrades = (int?)null, accounts = (int?)null,
            generatedUtc = DateTimeOffset.UtcNow });
        return Results.Ok(new { mode = "synthetic-paper-only", sqlConfigured = true, sqlConnected = true,
            safety, autoExecutionConfigured = Environment.GetEnvironmentVariable("QE_SQL_AUTO_EXECUTION") == "true",
            pendingOrders = (int?)await db.Orders.CountAsync(x => x.Status == "Pending", ct),
            executedTrades = (int?)await db.Executions.CountAsync(ct),
            accounts = (int?)await db.Accounts.CountAsync(ct), generatedUtc = DateTimeOffset.UtcNow });
    } catch (Exception) {
        return Results.Ok(new { mode = "synthetic-paper-only", sqlConfigured = true, sqlConnected = false,
            safety, autoExecutionConfigured = false, pendingOrders = (int?)null,
            executedTrades = (int?)null, accounts = (int?)null, generatedUtc = DateTimeOffset.UtcNow });
    }
}).RequireAuthorization(policy => policy.RequireRole("Admin"));
// v3.7: read-only, administrator-scoped SQL risk observability. No order mutation.
app.MapGet("/api/admin/risk/overview", async (IServiceProvider services, SqlTradingGate gate, CancellationToken ct) => {
    var safety = await gate.StatusAsync(ct);
    if (string.IsNullOrWhiteSpace(sqlConnection))
        return Results.Ok(new { configured = false, connected = false, safety, accounts = 0, pendingOrders = 0, executedTrades = 0, totalCash = 0m, generatedUtc = DateTimeOffset.UtcNow });
    try {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        if (!await db.Database.CanConnectAsync(ct))
            return Results.Ok(new { configured = true, connected = false, safety, accounts = 0, pendingOrders = 0, executedTrades = 0, totalCash = 0m, generatedUtc = DateTimeOffset.UtcNow });
        return Results.Ok(new { configured = true, connected = true, safety,
            accounts = await db.Accounts.CountAsync(ct),
            pendingOrders = await db.Orders.CountAsync(o => o.Status == "Pending", ct),
            executedTrades = await db.Executions.CountAsync(ct),
            totalCash = await db.Accounts.SumAsync(a => (decimal?)a.CashBalance, ct) ?? 0m,
            generatedUtc = DateTimeOffset.UtcNow });
    } catch (Exception) { return Results.Problem("Risk overview unavailable", statusCode: 503); }
}).RequireAuthorization(policy => policy.RequireRole("Admin"));
app.MapGet("/api/sql-paper/auto-execution/status", (ClaimsPrincipal user) => Results.Ok(new { enabled = Environment.GetEnvironmentVariable("QE_SQL_AUTO_EXECUTION") == "true", mode = "synthetic-paper-only", intervalSeconds = 3 })).RequireAuthorization();
app.MapGet("/api/auth/me",(ClaimsPrincipal user)=>Results.Ok(new {authenticated=user.Identity?.IsAuthenticated==true,username=user.Identity?.Name,role=user.FindFirstValue(ClaimTypes.Role)}));
app.MapPost("/api/auth/login",async (HttpContext ctx, LoginRequest request, LoginAttemptGuard guard, SecurityAuditStore securityAudit)=>{
 var ip=ctx.Connection.RemoteIpAddress?.ToString()??"unknown";
 if(!guard.Allow(ip))return Results.StatusCode(429);
 var username=request.Username?.Trim().ToLowerInvariant();
 if(username is not ("admin" or "trader" or "analyst") || string.IsNullOrWhiteSpace(request.Password)){guard.Fail(ip);securityAudit.Add("LOGIN_FAILED", "unknown", ip);return Results.Unauthorized();}
 var configured=Environment.GetEnvironmentVariable("QE_"+username.ToUpperInvariant()+"_PASSWORD");
 if(string.IsNullOrWhiteSpace(configured)){guard.Fail(ip);securityAudit.Add("LOGIN_FAILED",username,ip);return Results.Unauthorized();}
 var a=SHA256.HashData(Encoding.UTF8.GetBytes(configured));var b=SHA256.HashData(Encoding.UTF8.GetBytes(request.Password));
 if(!CryptographicOperations.FixedTimeEquals(a,b)){guard.Fail(ip);securityAudit.Add("LOGIN_FAILED",username,ip);return Results.Unauthorized();}
 guard.Success(ip);securityAudit.Add("LOGIN_SUCCESS",username,ip);
 var role=char.ToUpperInvariant(username[0])+username[1..];
 var principal=new ClaimsPrincipal(new ClaimsIdentity(new[]{new Claim(ClaimTypes.Name,username),new Claim(ClaimTypes.Role,role)},CookieAuthenticationDefaults.AuthenticationScheme));
 await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,principal);
 return Results.Ok(new {username,role});
});
app.MapPost("/api/auth/logout",async (HttpContext ctx, SecurityAuditStore securityAudit)=>{
 var origin=ctx.Request.Headers.Origin.ToString();
 var expected=$"{ctx.Request.Scheme}://{ctx.Request.Host}";
 if(!string.Equals(origin,expected,StringComparison.OrdinalIgnoreCase))return Results.StatusCode(403);
 securityAudit.Add("LOGOUT",ctx.User.Identity?.Name??"unknown",ctx.Connection.RemoteIpAddress?.ToString()??"unknown");
 await ctx.SignOutAsync();return Results.Ok(new {message="Signed out"});
});
app.MapHub<MarketHub>("/hubs/market").RequireAuthorization();
app.MapGet("/api/performance", (PerformanceHistory h) => Results.Ok(h.GetSummary()));
app.MapGet("/api/health",()=>Results.Ok(new {status="healthy",mode="paper",utc=DateTimeOffset.UtcNow}));
app.MapGet("/api/security/audit",(SecurityAuditStore store, int? limit, string? action, string? actor)=>Results.Ok(store.Recent(limit ?? 200, action, actor))).RequireAuthorization(policy=>policy.RequireRole("Admin"));
app.MapGet("/api/security/audit/stats", (SecurityAuditStore store) => Results.Ok(store.Statistics())).RequireAuthorization(policy => policy.RequireRole("Admin"));
app.MapGet("/api/security/audit.csv", (SecurityAuditStore store) => {
 var csv = new System.Text.StringBuilder("At,Action,Actor,Source\r\n");
 static string Q(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
 foreach (var evt in store.Recent()) csv.Append(Q(evt.At.ToString("O"))).Append(',').Append(Q(evt.Action)).Append(',').Append(Q(evt.Actor)).Append(',').Append(Q(evt.Source)).Append("\r\n");
 return Results.File(new System.Text.UTF8Encoding(true).GetBytes(csv.ToString()), "text/csv; charset=utf-8", "security-audit.csv");
}).RequireAuthorization(policy => policy.RequireRole("Admin"));
// Administrative SQL audit bridge: explicitly migrate the database before importing.
app.MapGet("/api/admin/sql/status", async (IServiceProvider services, CancellationToken ct) => {
    if (string.IsNullOrWhiteSpace(sqlConnection)) return Results.Ok(new { configured = false, connected = false });
    try {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var connected = await db.Database.CanConnectAsync(ct);
        return Results.Ok(new { configured = true, connected });
    } catch (Exception) { return Results.Ok(new { configured = true, connected = false }); }
}).RequireAuthorization(policy => policy.RequireRole("Admin"));
app.MapPost("/api/admin/sql/import-audit", async (IServiceProvider services, SecurityAuditStore audit, CancellationToken ct) => {
    if (string.IsNullOrWhiteSpace(sqlConnection)) return Results.BadRequest(new { error = "Set QE_SQL_CONNECTION first" });
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
    if (!await db.Database.CanConnectAsync(ct)) return Results.Problem("SQL Server unavailable or database not initialized", statusCode: 503);
    // Bounded import; idempotent by the composite event identity. Historical archives are not imported.
    var items = audit.Recent(1000).Reverse().ToArray();
    var inserted = 0;
    foreach (var item in items) {
        var exists = await db.SecurityEvents.AnyAsync(x => x.AtUtc == item.At && x.Action == item.Action && x.ActorId == item.Actor && x.Source == item.Source, ct);
        if (exists) continue;
        db.SecurityEvents.Add(new SecurityAuditRecord { AtUtc = item.At, Action = item.Action, ActorId = item.Actor, Source = item.Source });
        inserted++;
    }
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { imported = inserted, inspected = items.Length, source = "latest in-memory audit events" });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));
app.MapGet("/api/admin/sql/audit", async (IServiceProvider services, int? limit, CancellationToken ct) => {
    if (string.IsNullOrWhiteSpace(sqlConnection)) return Results.BadRequest(new { error = "SQL not configured" });
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
    var rows = await db.SecurityEvents.AsNoTracking().OrderByDescending(x => x.AtUtc).Take(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync(ct);
    return Results.Ok(rows);
}).RequireAuthorization(policy => policy.RequireRole("Admin"));
app.MapGet("/api/security/session",(ClaimsPrincipal user)=>Results.Ok(new {user=user.Identity?.Name,role=user.FindFirstValue(ClaimTypes.Role),mode="paper",accountScope="shared"})).RequireAuthorization();
app.MapGet("/api/audit",(TradingEngine e)=>Results.Ok(e.GetAudit()));
app.MapGet("/api/candles/{symbol}",(string symbol,int? limit,TradingEngine e)=>Results.Ok(e.GetCandles(symbol,limit??60)));
app.MapGet("/api/analytics",(TradingEngine e)=>Results.Ok(e.GetAnalytics()));
app.MapGet("/api/risk/settings",(TradingEngine e)=>Results.Ok(e.GetRiskSettings()));
app.MapPut("/api/risk/settings",(RiskSettings settings,TradingEngine e)=>{var result=e.UpdateRiskSettings(settings);return result.Error is null?Results.Ok(result.Value):Results.BadRequest(new {error=result.Error});});
app.MapGet("/api/risk",(TradingEngine e)=>Results.Ok(e.GetRiskSummary()));
app.MapGet("/api/market",(TradingEngine e)=>Results.Ok(e.GetMarket()));
app.MapGet("/api/account",(TradingEngine e)=>Results.Ok(e.GetAccount()));
app.MapGet("/api/orders",(TradingEngine e)=>Results.Ok(e.GetOrders()));
app.MapGet("/api/trades",(TradingEngine e)=>Results.Ok(e.GetTrades()));
app.MapGet("/api/history/{symbol}",(string symbol,TradingEngine e)=>Results.Ok(e.GetHistory(symbol)));
app.MapPost("/api/orders",(OrderRequest request,TradingEngine e)=>{var result=e.PlaceOrder(request);return result.Error is null?Results.Ok(result.Order):Results.BadRequest(new {error=result.Error});});
app.MapGet("/api/orders/pending",(TradingEngine e)=>Results.Ok(e.GetPendingOrders()));
app.MapPost("/api/orders/limit",(LimitOrderRequest request,TradingEngine e)=>{var result=e.PlaceLimitOrder(request);return result.Error is null?Results.Ok(result.Order):Results.BadRequest(new {error=result.Error});});
app.MapDelete("/api/orders/pending/{id:guid}",(Guid id,TradingEngine e)=>e.CancelPending(id)?Results.NoContent():Results.NotFound());
app.MapGet("/api/orders/stops",(TradingEngine e)=>Results.Ok(e.GetStopOrders()));
app.MapPost("/api/orders/stops",(StopOrderRequest request,TradingEngine e)=>{var result=e.PlaceStopOrder(request);return result.Error is null?Results.Ok(result.Order):Results.BadRequest(new {error=result.Error});});
app.MapDelete("/api/orders/stops/{id:guid}",(Guid id,TradingEngine e)=>e.CancelStop(id)?Results.NoContent():Results.NotFound());
app.MapGet("/api/orderbook/{symbol}",(string symbol,int? depth,TradingEngine e)=>e.GetOrderBook(symbol,depth??10) is { } book?Results.Ok(book):Results.NotFound(new {error="Unsupported symbol"}));
app.MapGet("/api/alerts",(TradingEngine e)=>Results.Ok(e.GetAlerts()));
app.MapPost("/api/alerts",(PriceAlertRequest request,TradingEngine e)=>{var result=e.CreateAlert(request);return result.Error is null?Results.Ok(result.Alert):Results.BadRequest(new {error=result.Error});});
app.MapDelete("/api/alerts/{id:guid}",(Guid id,TradingEngine e)=>e.DeleteAlert(id)?Results.NoContent():Results.NotFound());
// Reporting endpoints use immutable snapshots returned by the existing trading engine.
app.MapGet("/api/reports/summary",(TradingEngine e)=> {
    var orders=(Order[])e.GetOrders();
    var buys=orders.Where(o=>o.Side=="BUY").ToArray();
    var sells=orders.Where(o=>o.Side=="SELL").ToArray();
    return Results.Ok(new {generatedAt=DateTimeOffset.UtcNow,mode="paper",totalOrders=orders.Length,
        buyOrders=buys.Length,sellOrders=sells.Length,buyNotional=buys.Sum(o=>o.Notional),
        sellNotional=sells.Sum(o=>o.Notional),totalTradedNotional=orders.Sum(o=>o.Notional),
        instruments=orders.GroupBy(o=>o.Symbol).Select(g=>new {symbol=g.Key,orders=g.Count(),notional=g.Sum(o=>o.Notional)}).OrderByDescending(x=>x.notional).ToArray()});
});
app.MapGet("/api/reports/orders.csv",(TradingEngine e)=> {
    var orders=(Order[])e.GetOrders();
    static string Csv(string? value)=>"\""+(value??"").Replace("\"","\"\"")+"\"";
    var rows=new List<string>{"Id,Time,Symbol,Side,Quantity,Price,Notional,Status"};
    foreach(var o in orders) rows.Add(string.Join(",",Csv(o.Id.ToString()),Csv(o.Time.ToString("O")),Csv(o.Symbol),Csv(o.Side),o.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),o.Price.ToString(System.Globalization.CultureInfo.InvariantCulture),o.Notional.ToString(System.Globalization.CultureInfo.InvariantCulture),Csv(o.Status)));
    return Results.File(System.Text.Encoding.UTF8.GetBytes("\uFEFF"+string.Join("\r\n",rows)),"text/csv; charset=utf-8","quantedge-orders.csv");
});
app.MapPost("/api/reset",(TradingEngine e, ClaimsPrincipal user, SecurityAuditStore audit)=>{e.Reset();audit.Add("ACCOUNT_RESET",user.Identity?.Name??"unknown","api");return Results.Ok(new {message="Paper account reset"});});
if (!string.IsNullOrWhiteSpace(sqlConnection))
    app.MapSqlAccounts();
app.Run();

public record LoginRequest(string Username,string Password);
