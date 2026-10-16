using System.Diagnostics;

namespace Bimtech.RevitMcp.DevTools;

// Stops a run_revit_query snippet that runs too long or recurses too deep. Revit runs the snippet on its only API
// thread and .NET cannot abort a thread, so the snippet must stop itself: QueryGuardInserter puts Tick() first in
// every loop body and wraps every local function and lambda in Enter()/Exit().
//   - Tick throws once the time budget is spent or the client gave up waiting.
//   - Enter also counts call depth: endless recursion would overflow the stack, which .NET cannot catch and
//     which would take Revit down with it, so it is stopped at MaxDepth instead.
// One snippet runs at a time on Revit's thread, so the state is static.
public static class QueryGuard
{
    public const int MaxDepth = 1000;

    private static Stopwatch? _clock;
    private static TimeSpan _budget;
    private static CancellationToken _cancellation;
    private static int _depth;

    internal static void Begin(TimeSpan budget, CancellationToken cancellation)
    {
        _clock = Stopwatch.StartNew();
        _budget = budget;
        _cancellation = cancellation;
        _depth = 0;
    }

    internal static void End() => _clock = null;

    public static void Tick()
    {
        if (_clock is null) return;
        _cancellation.ThrowIfCancellationRequested();
        if (_clock.Elapsed > _budget) throw new QueryTimeoutException(_budget);
    }

    public static void Enter()
    {
        Tick();
        if (_clock is not null && ++_depth > MaxDepth) throw new QueryDepthException();
    }

    public static void Exit()
    {
        if (_depth > 0) _depth--;
    }
}

public sealed class QueryTimeoutException(TimeSpan budget)
    : Exception($"The query ran longer than {budget.TotalSeconds:0} s and was stopped.");

public sealed class QueryDepthException()
    : Exception($"The query nested function calls more than {QueryGuard.MaxDepth} deep (endless recursion?) and was stopped.");
