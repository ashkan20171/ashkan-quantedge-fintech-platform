using System.Threading;

namespace AshkanQuantEdge;

// Process-local emergency pause for synthetic SQL execution.
// Not a distributed kill switch: multi-instance deployments need a shared durable state.
public sealed class TradingSafety
{
    private int _paused = 1; // fail closed on every startup
    public bool IsPaused => Volatile.Read(ref _paused) == 1;
    public void SetPaused(bool paused) => Interlocked.Exchange(ref _paused, paused ? 1 : 0);
}
