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
    /// <summary>Test-only fault for one reader, named and logged. Null in normal runs.</summary>
    public string? OcrFault { get; set; }
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
        foreach (var h in windows)
        {
            int pid = Native.Pid(h);
            string exe, start;
            try
            {
                using var p = Process.GetProcessById(pid);
                exe = p.MainModule?.FileName ?? "";
                start = p.StartTime.ToUniversalTime().ToString("O");
            }
            catch (Exception ex) { d.Obs($"window pid {pid}: cannot read its executable ({ex.GetType().Name})"); continue; }
            if (!string.Equals(Path.GetFullPath(exe), want, StringComparison.OrdinalIgnoreCase))
            {
                d.Obs($"rejected window '{Native.Text(h)}' pid {pid}: executable {exe} is not on the allowlist");
                continue;
            }
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
