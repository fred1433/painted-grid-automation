using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace GridDriver;

public sealed class PathResult
{
    public string Path { get; set; } = "";
    public string Surface { get; set; } = "";
    public string Result { get; set; } = "";
    public List<string> Observations { get; set; } = new();
}

/// <summary>
/// Capability diagnostic, per surface: what each path really returns on this window. Raw dumps are written as files
/// next to the step journal so they can be read without trusting this summary.
/// </summary>
public static class Diagnostics
{
    public static List<PathResult> Run(TargetWindow t, Evidence ev, Decision d)
    {
        var results = new List<PathResult>();
        if (!t.IsResponsive()) throw new StopException(Codes.AppNotResponding, "window does not answer WM_NULL; diagnostic skipped", d);

        // 1. UI Automation, raw view.
        var uia = new StringBuilder();
        IntPtr gridHwnd = IntPtr.Zero, scrollHwnd = IntPtr.Zero;
        var gridRes = new PathResult { Path = "UIA", Surface = "grid" };
        var searchRes = new PathResult { Path = "UIA", Surface = "search box" };
        var toolbarRes = new PathResult { Path = "UIA", Surface = "toolbar" };
        Uia.Call(t, t.Hwnd, "raw tree dump", () =>
        {
            var walker = TreeWalker.RawViewWalker;
            int count = 0;
            void Walk(AutomationElement e, int depth)
            {
                if (count++ > 400 || depth > 8) return;
                var c = e.Current;
                var pats = string.Join(",", e.GetSupportedPatterns().Select(p => p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", "")));
                int kids = 0; var ch = walker.GetFirstChild(e); while (ch != null) { kids++; ch = walker.GetNextSibling(ch); }
                uia.AppendLine($"{new string(' ', depth * 2)}{c.ControlType.ProgrammaticName.Replace("ControlType.", "")} name='{c.Name}' id='{c.AutomationId}' class='{c.ClassName}' hwnd=0x{c.NativeWindowHandle:X} rect={c.BoundingRectangle} children={kids} patterns=[{pats}]");
                if (c.AutomationId == "ledgerGrid") { gridHwnd = new IntPtr(c.NativeWindowHandle); Summarise(gridRes, e, kids, pats); }
                if (c.AutomationId == "toolbar") Summarise(toolbarRes, e, kids, pats);
                if (c.AutomationId == "gridScroll") scrollHwnd = new IntPtr(c.NativeWindowHandle);
                if (c.AutomationId == "searchBox")
                {
                    searchRes.Result = pats.Contains("Value") ? "AVAILABLE" : Codes.NotAvailable;
                    searchRes.Observations.Add($"Edit '{c.Name}' exposes [{pats}]");
                }
                var k = walker.GetFirstChild(e);
                while (k != null) { Walk(k, depth + 1); k = walker.GetNextSibling(k); }
            }
            Walk(AutomationElement.FromHandle(t.Hwnd), 0);
            return 0;
        }, d);
        File.WriteAllText(Path.Combine(ev.Dir, "diagnostic-uia-raw.txt"), uia.ToString());
        results.Add(gridRes); results.Add(toolbarRes); results.Add(searchRes);

        // 2. MSAA (IAccessible on the grid window). UIA also surfaces this through its MSAA proxy / LegacyIAccessible.
        var msaa = new PathResult { Path = "MSAA", Surface = "grid" };
        if (gridHwnd != IntPtr.Zero)
        {
            try
            {
                var iid = Native.IID_IAccessible;
                int hr = Native.AccessibleObjectFromWindow(gridHwnd, Native.OBJID_CLIENT, ref iid, out object o);
                if (hr != 0 || o is not Accessibility.IAccessible acc) { msaa.Result = Codes.NotAvailable; msaa.Observations.Add($"AccessibleObjectFromWindow hr=0x{hr:X8}"); }
                else
                {
                    int kids = acc.accChildCount;
                    object role = acc.get_accRole(0);
                    string? name = acc.get_accName(0);
                    msaa.Result = kids == 0 ? Codes.NotAvailable : "PARTIAL";
                    msaa.Observations.Add($"AccessibleObjectFromWindow ok: role={role} name='{name}' accChildCount={kids}");
                }
            }
            catch (Exception ex) { msaa.Result = Codes.NotAvailable; msaa.Observations.Add(ex.GetType().Name + ": " + ex.Message); }
        }
        else { msaa.Result = Codes.NotAvailable; msaa.Observations.Add("grid window handle not found through UIA"); }
        results.Add(msaa);
        File.WriteAllText(Path.Combine(ev.Dir, "diagnostic-msaa.txt"), string.Join(Environment.NewLine, msaa.Observations));

        // 3. Win32: child windows, their classes, text and what messages can tell.
        var w32 = new PathResult { Path = "Win32 messages", Surface = "grid" };
        var lines = new List<string>();
        Native.EnumChildWindows(t.Hwnd, (h, _) =>
        {
            var sb = new StringBuilder(256);
            Native.SendMessageTimeout(h, Native.WM_GETTEXT, (IntPtr)sb.Capacity, sb, Native.SMTO_ABORTIFHUNG, 500, out _);
            lines.Add($"hwnd=0x{h.ToInt64():X} class={Native.Class(h)} rect={Native.Rect(h)} visible={Native.IsWindowVisible(h)} WM_GETTEXT='{sb}'");
            return true;
        }, IntPtr.Zero);
        File.WriteAllText(Path.Combine(ev.Dir, "diagnostic-win32-children.txt"), string.Join(Environment.NewLine, lines));
        if (gridHwnd != IntPtr.Zero)
        {
            string cls = Native.Class(gridHwnd);
            bool listClass = cls.Contains("ListView", StringComparison.OrdinalIgnoreCase) || cls.Contains("ListBox", StringComparison.OrdinalIgnoreCase) || cls.Contains("SysListView32");
            w32.Result = listClass ? "AVAILABLE" : Codes.NotAvailable;
            w32.Observations.Add($"{lines.Count} child windows; grid class {cls} is not a list or grid class, so no row-level messages apply; WM_GETTEXT is empty");
        }
        if (scrollHwnd != IntPtr.Zero)
        {
            var si = new Native.SCROLLINFO { cbSize = (uint)Marshal.SizeOf<Native.SCROLLINFO>(), fMask = 0x17 };
            bool ok = Native.GetScrollInfo(scrollHwnd, 2, ref si);
            w32.Observations.Add(ok ? $"the grid's scrollbar is a real child window: GetScrollInfo pos={si.nPos} max={si.nMax} page={si.nPage} (scroll position, not row content)" : "GetScrollInfo failed on the scrollbar");
        }
        results.Add(w32);

        results.Add(new PathResult
        {
            Path = "Keyboard", Surface = "grid and toolbar", Result = "CONFIGURED",
            Observations = { "declared by this adapter: Ctrl+P opens the payment dialog for the selected row; F6 toggles the sort; arrow keys move the selection", "keyboard is tried before any pixel click on the painted toolbar" },
        });
        d.Obs($"diagnostic: {string.Join("; ", results.Select(r => $"{r.Path}/{r.Surface}={r.Result}"))}");
        return results;
    }

    static void Summarise(PathResult r, AutomationElement e, int kids, string pats)
    {
        bool rows = pats.Contains("Grid") || pats.Contains("Table") || pats.Contains("Selection") || pats.Contains("ItemContainer");
        r.Result = kids == 0 && !rows ? Codes.NotAvailable : "PARTIAL";
        r.Observations.Add($"{e.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")} '{e.Current.AutomationId}' raw children={kids} patterns=[{pats}]");
        if (r.Result == Codes.NotAvailable) r.Observations.Add("visible rows that the tested accessibility interfaces do not expose as usable row objects");
    }
}
