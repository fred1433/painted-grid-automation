using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;

namespace GridDriver;

public sealed class DriverConfig
{
    public string AllowedExe { get; set; } = "";
    public string TitlePrefix { get; set; } = "Payables Ledger";
    public string DialogTitle { get; set; } = "Record payment";
    public string EvidenceDir { get; set; } = "evidence";
    public string TessdataDir { get; set; } = "tessdata";
}

/// <summary>
/// The one window the driver may touch: allowlisted by full executable path, then pinned by PID and process start time.
/// A window with the same title from another executable is rejected and reported.
/// </summary>
public sealed class TargetWindow
{
    public IntPtr Hwnd { get; }
    public int Pid { get; }
    public string ExePath { get; }
    public string ProcessStart { get; }
    public string Title { get; }

    TargetWindow(IntPtr h, int pid, string exe, string start, string title) { Hwnd = h; Pid = pid; ExePath = exe; ProcessStart = start; Title = title; }

    public static TargetWindow Attach(DriverConfig cfg, Decision d)
    {
        d.Via("Win32 window enumeration");
        var windows = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            if (Native.IsWindowVisible(h) && Native.GetWindow(h, Native.GW_OWNER) == IntPtr.Zero && Native.Text(h).StartsWith(cfg.TitlePrefix, StringComparison.Ordinal)) windows.Add(h);
            return true;
        }, IntPtr.Zero);
        var allowed = new List<TargetWindow>();
        string want = Path.GetFullPath(cfg.AllowedExe);
        int? mine = Native.IntegrityLevel(Environment.ProcessId, out _);
        foreach (var h in windows)
        {
            int pid = Native.Pid(h);
            string? exe = Native.ImagePath(pid, out var why);
            if (exe == null) { d.Obs($"window '{Native.Text(h)}' pid {pid}: executable path unreadable ({why}); not considered"); continue; }
            if (!string.Equals(Path.GetFullPath(exe), want, StringComparison.OrdinalIgnoreCase))
            {
                d.Obs($"rejected window '{Native.Text(h)}' pid {pid}: executable {exe} is not on the allowlist");
                continue;
            }
            // UIPI: input and messages from a lower integrity level are dropped without an error. Refuse instead of clicking into nothing.
            int? theirs = Native.IntegrityLevel(pid, out var whyIl);
            if (theirs == null || mine == null || theirs > mine)
                throw new StopException(Codes.AccessDenied, theirs == null
                    ? $"pid {pid} is allowlisted but its integrity level cannot be read ({whyIl}); if it runs elevated, Windows would drop this server's input without an error; no input sent"
                    : $"pid {pid} runs at integrity {Native.LevelName(theirs)}, this server at {Native.LevelName(mine)}: Windows drops input and messages sent to a higher level (UIPI) without an error; no input sent", d);
            string start;
            try { using var p = Process.GetProcessById(pid); start = p.StartTime.ToUniversalTime().ToString("O"); }
            catch (Exception ex) { d.Obs($"window pid {pid}: start time unreadable ({ex.GetType().Name})"); continue; }
            d.Obs($"pid {pid} integrity {Native.LevelName(theirs)}, this server {Native.LevelName(mine)}");
            allowed.Add(new TargetWindow(h, pid, exe, start, Native.Text(h)));
        }
        if (allowed.Count == 0) throw new StopException(Codes.WindowNotFound, $"no visible window titled '{cfg.TitlePrefix}*' from {want}", d);
        if (allowed.Count > 1) throw new StopException(Codes.AmbiguousTarget, $"{allowed.Count} allowlisted instances are open; refusing to pick one", d);
        var t = allowed[0];
        d.Obs($"attached to '{t.Title}' pid {t.Pid} started {t.ProcessStart}, dpi {Native.GetDpiForWindow(t.Hwnd)}");
        return t;
    }

    public void VerifyIdentity(int pid, string start, Decision d)
    {
        if (pid != Pid || start != ProcessStart)
            throw new StopException(Codes.IdentityChanged, $"operation is bound to pid {pid} started {start}; the window now belongs to pid {Pid} started {ProcessStart}", d);
        d.Met($"same process: pid {Pid}, start time {ProcessStart}");
    }

    /// <summary>A3: called before any UIA or MSAA call. A window that does not answer WM_NULL in 500 ms is read by screen capture only.</summary>
    public bool IsResponsive(IntPtr? h = null) =>
        Native.SendMessageTimeout(h ?? Hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 500, out _) != IntPtr.Zero;

    public Rectangle WindowRect => Native.Rect(Hwnd);

    public IntPtr FindDialog(string title)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            if (Native.IsWindowVisible(h) && Native.Pid(h) == Pid && Native.Text(h) == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public void EnsureUnobstructed(Point p, IntPtr expectedRoot, Decision d)
    {
        var hit = Native.WindowFromPoint(new Native.POINT { X = p.X, Y = p.Y });
        var root = Native.GetAncestor(hit, Native.GA_ROOT);
        if (root != expectedRoot)
        {
            int pid = Native.Pid(root);
            throw new StopException(Codes.WindowBlocked, $"point {p.X},{p.Y} is covered by window '{Native.Text(root)}' class {Native.Class(root)} pid {pid}; no input sent", d);
        }
        d.Met($"point {p.X},{p.Y} belongs to the target window");
    }

    /// <summary>A capture of a covered grid is not evidence: every sampled point of the area must belong to the target window.</summary>
    public void EnsureAreaUnobstructed(Rectangle r, Decision d)
    {
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
            {
                var p = new Point(r.Left + 4 + (r.Width - 8) * i / 4, r.Top + 4 + (r.Height - 8) * j / 4);
                var root = Native.GetAncestor(Native.WindowFromPoint(new Native.POINT { X = p.X, Y = p.Y }), Native.GA_ROOT);
                if (root != Hwnd)
                    throw new StopException(Codes.WindowBlocked, $"the grid is partly covered at {p.X},{p.Y} by window '{Native.Text(root)}' class {Native.Class(root)} pid {Native.Pid(root)}; its capture is not used and no input is sent", d);
            }
    }

    /// <summary>Brings the window to the foreground with a real click on its title bar, or stops with FOCUS_LOST.</summary>
    public void EnsureForeground(IntPtr want, Decision d)
    {
        if (Native.GetForegroundWindow() == want) { d.Met("target window has the focus"); return; }
        var r = Native.Rect(want);
        var title = new Point(r.Left + Math.Min(90, r.Width / 3), r.Top + Math.Max(8, (int)(10 * Native.GetDpiForWindow(want) / 96.0)));
        EnsureUnobstructed(title, want, d);
        Native.Click(title.X, title.Y);
        Thread.Sleep(250);
        if (Native.GetForegroundWindow() != want)
        {
            var fg = Native.GetForegroundWindow();
            throw new StopException(Codes.FocusLost, $"focus is held by '{Native.Text(fg)}' pid {Native.Pid(fg)}; no input sent", d);
        }
        d.Obs("focus restored with a click on the title bar").Met("target window has the focus");
    }
}
