using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace GridDriver;

/// <summary>
/// UI Automation and MSAA calls. Each one is checked first with WM_NULL (SMTO_ABORTIFHUNG, 500 ms), then runs on a worker
/// with a time limit. A call that exceeds the limit is abandoned, not cancelled: a COM call already sent may still complete
/// (one SetValue may land), but the worker checks a cancellation flag before each further write, no other UIA call starts
/// while it is still running, and the route stops, so no Post click follows.
/// Errors are mapped to codes: access denied -> ACCESS_DENIED, anything else -> PATH_FAILED. Both stop the route.
/// </summary>
public static class Uia
{
    static readonly List<(string What, Task Work)> Abandoned = new();
    static readonly object Gate = new();
    const int E_ACCESSDENIED = unchecked((int)0x80070005);

    public static T Call<T>(TargetWindow t, IntPtr window, string what, Func<T> f, Decision d, int timeoutMs = 4000) =>
        Call(t, window, what, _ => f(), d, timeoutMs);

    public static T Call<T>(TargetWindow t, IntPtr window, string what, Func<CancellationToken, T> f, Decision d, int timeoutMs = 4000)
    {
        lock (Gate)
        {
            Abandoned.RemoveAll(a => a.Work.IsCompleted);
            if (Abandoned.Count > 0)
                throw new StopException(Codes.AppNotResponding, $"UIA call '{what}' not started: the call '{Abandoned[0].What}', abandoned after its time limit, has not returned yet", d);
        }
        if (!t.IsResponsive(window))
            throw new StopException(Codes.AppNotResponding, $"UIA call '{what}' not made: the window did not answer WM_NULL within 500 ms; it is read by screen capture only", d);
        var cts = new CancellationTokenSource();
        var task = Task.Run(() => f(cts.Token));
        if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(timeoutMs))
        {
            cts.Cancel();
            lock (Gate) Abandoned.Add((what, task));
            task.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new StopException(Codes.AppNotResponding, $"UIA call '{what}' did not return within {timeoutMs} ms; it is abandoned (a COM call already sent may still complete), it writes no further field, and no other UIA call starts until it returns", d);
        }
        d.Via("UI Automation");
        try { return task.GetAwaiter().GetResult(); }
        catch (StopException) { throw; }
        catch (Exception ex) { throw Map(ex, what, d); }
    }

    /// <summary>ACCESS_DENIED for E_ACCESSDENIED, PATH_FAILED for anything else. Neither lets the route fall back to pixels.</summary>
    public static StopException Map(Exception ex, string what, Decision d)
    {
        if (ex is AggregateException ag && ag.InnerExceptions.Count == 1) ex = ag.InnerExceptions[0];
        if (ex is StopException s) return s;
        bool denied = ex is UnauthorizedAccessException || (ex is COMException c && c.HResult == E_ACCESSDENIED) || ex.HResult == E_ACCESSDENIED;
        return new StopException(denied ? Codes.AccessDenied : Codes.PathFailed, $"{what}: {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}", d);
    }

    public static AutomationElement? ById(IntPtr root, string id) =>
        AutomationElement.FromHandle(root).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    public static Rectangle Rect(AutomationElement e)
    {
        var r = e.Current.BoundingRectangle;
        return new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
    }

    public static string GetValue(AutomationElement e) => ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
    public static void SetValue(AutomationElement e, string v) => ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).SetValue(v);
}
