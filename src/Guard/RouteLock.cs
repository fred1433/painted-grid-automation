using System;
using System.Threading;

namespace Guard;

/// <summary>
/// Serialises the whole locate-verify-write route across threads and processes of the same session: every MCP tool call,
/// the recovery of IN_FLIGHT operations, and the operator's reconciliation.
/// </summary>
public static class RouteLock
{
    public const string LedgerRoute = "pga-ledger-route";

    public static T Run<T>(string name, TimeSpan wait, Func<T> body, Action<string>? log = null)
    {
        using var m = new Mutex(false, @"Local\" + name);
        bool owned;
        try { owned = m.WaitOne(wait); }
        catch (AbandonedMutexException) { owned = true; log?.Invoke("previous holder of the route lock ended without releasing it"); }
        if (!owned) throw new Refusal("ROUTE_BUSY", "another call holds the route lock");
        try { return body(); }
        finally { m.ReleaseMutex(); }
    }
}
