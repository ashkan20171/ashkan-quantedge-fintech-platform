using System.Collections.Concurrent;
namespace AshkanQuantEdge;
public sealed record SecurityEvent(DateTimeOffset At, string Action, string Actor, string Source);
public sealed class SecurityAuditStore {
 private readonly object gate = new();
 private readonly string path;
 private const long MaxAuditFileBytes = 5 * 1024 * 1024;
 private const int MaxArchives = 5;
 private readonly ConcurrentQueue<SecurityEvent> events = new();
 public SecurityAuditStore(IHostEnvironment environment) {
  var folder = Path.Combine(environment.ContentRootPath, "App_Data");
  Directory.CreateDirectory(folder);
  path = Path.Combine(folder, "security-audit.jsonl");
  if (File.Exists(path)) {
   foreach (var line in File.ReadLines(path).TakeLast(1000)) {
    try { var evt = System.Text.Json.JsonSerializer.Deserialize<SecurityEvent>(line); if (evt is not null) events.Enqueue(evt); }
    catch (System.Text.Json.JsonException) { /* Skip malformed historical entries. */ }
   }
  }
 }
 public void Add(string action, string actor, string source) {
  var evt = new SecurityEvent(DateTimeOffset.UtcNow, action, actor, source);
  lock (gate) {
   RotateIfNeeded();
   File.AppendAllText(path, System.Text.Json.JsonSerializer.Serialize(evt) + Environment.NewLine);
   events.Enqueue(evt);
   while (events.Count > 1000) events.TryDequeue(out _);
  }
 }
 private void RotateIfNeeded() {
  if (!File.Exists(path) || new FileInfo(path).Length < MaxAuditFileBytes) return;
  var oldest = path + "." + MaxArchives;
  if (File.Exists(oldest)) File.Delete(oldest);
  for (int i = MaxArchives - 1; i >= 1; i--) {
   var from = path + "." + i;
   if (File.Exists(from)) File.Move(from, path + "." + (i + 1));
  }
  File.Move(path, path + ".1");
 }
 public SecurityEvent[] Recent(int limit = 200, string? action = null, string? actor = null) {
  limit = Math.Clamp(limit, 1, 1000);
  lock (gate) return events.Reverse()
   .Where(e => (string.IsNullOrWhiteSpace(action) || string.Equals(e.Action, action, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(actor) || string.Equals(e.Actor, actor, StringComparison.OrdinalIgnoreCase)))
   .Take(limit).ToArray();
 }
 public object Statistics() {
  lock (gate) {
   var snapshot = events.ToArray();
   return new { totalInMemory = snapshot.Length, archiveCount = Enumerable.Range(1, MaxArchives).Count(i => File.Exists(path + "." + i)),
    currentFileBytes = File.Exists(path) ? new FileInfo(path).Length : 0,
    byAction = snapshot.GroupBy(e => e.Action).Select(g => new { action = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ToArray(),
    latest = snapshot.Length == 0 ? (DateTimeOffset?)null : snapshot[^1].At };
  }
 }
}
public sealed class LoginAttemptGuard {
 private sealed class Entry { public int Count; public DateTimeOffset Until; public readonly object Gate=new(); }
 private readonly ConcurrentDictionary<string,Entry> entries=new();
 public bool Allow(string ip){var entry=entries.GetOrAdd(ip,_=>new());lock(entry.Gate){if(entry.Until<=DateTimeOffset.UtcNow){entry.Count=0;entry.Until=DateTimeOffset.UtcNow.AddMinutes(15);}return entry.Count<8;}}
 public void Fail(string ip){var entry=entries.GetOrAdd(ip,_=>new());lock(entry.Gate){if(entry.Until<=DateTimeOffset.UtcNow){entry.Count=0;entry.Until=DateTimeOffset.UtcNow.AddMinutes(15);}entry.Count++;}}
 public void Success(string ip)=>entries.TryRemove(ip,out _);
}
