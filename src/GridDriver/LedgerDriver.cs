using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Guard;

namespace GridDriver;

public sealed class RowQuery
{
    public string? Ref { get; set; }
    public string? Account { get; set; }
    public long? AmountCents { get; set; }
    public string? Date { get; set; }
    public override string ToString() => string.Join(", ", new[] { Ref != null ? $"ref {Ref}" : null, Account != null ? $"account '{Account}'" : null, AmountCents != null ? $"amount {Ocr.Money(AmountCents.Value)}" : null, Date != null ? $"date {Date}" : null }.Where(s => s != null));
}

public sealed class RowSeen
{
    public int Position { get; set; }
    public Dictionary<string, string> Cells { get; set; } = new();
    public bool Selected { get; set; }
}

public sealed class Selection
{
    public string Tx { get; set; } = "";
    public string Ref { get; set; } = "";
    public string Account { get; set; } = "";
    public string Date { get; set; } = "";
    public long AmountCents { get; set; }
    public string Status { get; set; } = "";
    public string? PaidBy { get; set; }
}

public sealed class Layout
{
    public IntPtr Grid, Scroll, Detail, Search;
    public Rectangle GridRect => Native.Rect(Grid);
    public Rectangle DetailRect => Native.Rect(Detail);
}

public sealed class ScanResult
{
    public List<RowSeen> Rows { get; set; } = new();
    /// <summary>Rows in the grid's current view, from the scroll bar (null when only the visible rows were read without it).</summary>
    public int? Total { get; set; }
    public bool Complete { get; set; }
    public string Coverage { get; set; } = "";
}

public sealed class PaymentOutcome
{
    public string State { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? PaymentTx { get; set; }
}

/// <summary>
/// Locates, selects and writes through the outside of the window only: screen capture, SendInput, Win32, UI Automation.
/// Coordinates are never reused across steps: every step captures and locates again.
/// Adapter assumptions for this witness grid, written down here and in the README: the panes are found by their UIA
/// AutomationId (ledgerGrid, gridScroll, detailPanel, searchBox), and the grid's scroll bar counts rows (SCROLLINFO in row
/// units, nMax + 1 = rows in the current view, nPage = rows on screen).
/// </summary>
public sealed class LedgerDriver : IDisposable
{
    readonly DriverConfig _cfg;
    readonly Evidence _ev;
    readonly GridProfile _prof = new();
    readonly WinRtOcr _win;
    readonly TesseractOcr _tess;
    int _linesPerNotch;
    int _notches;
    public string Readers => $"reader A: {_win.Name} on the whole row{(Faults.Has("ocr-1000-as-100") ? " [fault injected: ocr-1000-as-100]" : "")}; reader B: {_tess.Name} on each cell";

    /// <summary>Test-only fault on reader A, named and logged: a dropped digit group.</summary>
    static string FaultA(string s) => Faults.Has("ocr-1000-as-100") ? s.Replace("1,000.00", "100.00") : s;

    public LedgerDriver(DriverConfig cfg, Evidence ev)
    {
        _cfg = cfg; _ev = ev;
        _win = WinRtOcr.TryCreate(out var why) ?? throw new StopException(Codes.NotAvailable, "Windows.Media.Ocr unavailable: " + why);
        _tess = new TesseractOcr(cfg.TessdataDir);
    }

    public void Dispose() => _tess.Dispose();

    public TargetWindow Attach(Decision d) => TargetWindow.Attach(_cfg, d);

    public Layout GetLayout(TargetWindow t, Decision d) => Uia.Call(t, t.Hwnd, "pane bounds", () =>
    {
        IntPtr H(string id) => Uia.ById(t.Hwnd, id) is { } e ? new IntPtr(e.Current.NativeWindowHandle) : IntPtr.Zero;
        var l = new Layout { Grid = H("ledgerGrid"), Scroll = H("gridScroll"), Detail = H("detailPanel"), Search = H("searchBox") };
        if (l.Grid == IntPtr.Zero || l.Detail == IntPtr.Zero) throw new StopException(Codes.GeometryUnconfirmed, "grid or detail pane not found among UIA panes", d);
        return l;
    }, d);

    (int Pos, int Max, int Page)? ScrollInfo(Layout l)
    {
        if (l.Scroll == IntPtr.Zero) return null;
        var si = new Native.SCROLLINFO { cbSize = (uint)Marshal.SizeOf<Native.SCROLLINFO>(), fMask = 0x17 };
        return Native.GetScrollInfo(l.Scroll, 2, ref si) ? (si.nPos, si.nMax, (int)si.nPage) : null;
    }

    GridGeometry Look(TargetWindow t, Layout l, Decision d, out Bitmap img)
    {
        var r = l.GridRect;
        t.EnsureAreaUnobstructed(r, d);
        img = Evidence.Capture(r);
        return GridGeometry.Detect(img, r, _prof, _win, d, b => _tess.ReadLine(b));
    }

    void RequireResponsive(TargetWindow t, Decision d)
    {
        if (!t.IsResponsive()) throw new StopException(Codes.AppNotResponding, "the window did not answer WM_NULL within 500 ms; no input is sent to a window that does not respond", d);
    }

    Point GridCenter(Layout l) { var r = l.GridRect; return new Point(r.X + r.Width / 2, r.Y + r.Height / 2); }

    /// <summary>Positive notches scroll up, negative down. Real wheel input at the grid's centre.</summary>
    void Wheel(TargetWindow t, Layout l, int notches, Decision d)
    {
        RequireResponsive(t, d);
        t.EnsureForeground(t.Hwnd, d);
        var c = GridCenter(l);
        t.EnsureUnobstructed(c, t.Hwnd, d);
        Native.Wheel(c.X, c.Y, notches * 120);
        _notches += Math.Abs(notches);
        Thread.Sleep(220);
    }

    static Dictionary<string, string> Named(GridGeometry g, string[] cells) =>
        g.ColumnIndex.ToDictionary(kv => kv.Key, kv => cells[kv.Value] ?? "");

    public void ClearSearch(TargetWindow t, Layout l, Decision d)
    {
        if (l.Search == IntPtr.Zero) return;
        Uia.Call(t, t.Hwnd, "clear search box", () =>
        {
            var e = System.Windows.Automation.AutomationElement.FromHandle(l.Search);
            var was = Uia.GetValue(e);
            if (was != "") { Uia.SetValue(e, ""); d.Obs($"search box cleared through UIA ValuePattern (it read '{was}')"); }
            return 0;
        }, d);
    }

    public string SearchText(TargetWindow t, Layout l, Decision d) =>
        l.Search == IntPtr.Zero ? "" : Uia.Call(t, t.Hwnd, "read search box", () => Uia.GetValue(System.Windows.Automation.AutomationElement.FromHandle(l.Search)), d);

    public void SetSearch(TargetWindow t, Layout l, string text, Decision d)
    {
        Uia.Call(t, t.Hwnd, "set search box", () =>
        {
            var e = System.Windows.Automation.AutomationElement.FromHandle(l.Search);
            Uia.SetValue(e, text);
            var back = Uia.GetValue(e);
            if (back != text) throw new StopException(Codes.DialogMismatch, $"search box reads '{back}' after writing '{text}'", d);
            d.Obs($"search box set to '{text}' through UIA ValuePattern and read back").Met("semantic path used where the control exposes it");
            return 0;
        }, d);
        Thread.Sleep(200);
    }

    /// <summary>Reader A's scan of one view, with the named test fault that misreads one row's Account cell.</summary>
    List<string[]> ScanView(GridGeometry g, Bitmap img, Decision d)
    {
        var cells = g.Scan(img, _win);
        if (Faults.Arg("garble-scan") is { Length: > 0 } refToGarble && g.ColumnIndex.TryGetValue("Ref #", out int rc) && g.ColumnIndex.TryGetValue("Account", out int ac))
            foreach (var row in cells)
                if (Ocr.Fold(row[rc] ?? "") == Ocr.Fold(refToGarble) && row[ac] is { Length: > 5 } acc)
                {
                    row[ac] = acc.Remove(4, 1).Remove(1, 1);
                    d.Obs($"FAULT garble-scan: reader A's scan of {refToGarble} reads Account '{row[ac]}' instead of '{acc}'");
                }
        return cells;
    }

    /// <summary>
    /// Reads every row of the grid's current view before any decision: back to the first row (scroll position 0), then
    /// down with real wheel input until the last row has been read, checking at each step that no row was skipped.
    /// Any gap, a scroll position that cannot be read, or a wheel that does not move gives SCAN_INCOMPLETE.
    /// With scroll = false only the rows on screen are read, and the result says so.
    /// </summary>
    public ScanResult FullScan(TargetWindow t, Layout l, bool scroll, Decision d)
    {
        RequireResponsive(t, d);
        _notches = 0;
        var seen = new SortedDictionary<int, RowSeen>();
        var si = ScrollInfo(l);
        if (!scroll)
        {
            var g0 = Look(t, l, d, out var img0);
            using (img0)
            {
                var cells = ScanView(g0, img0, d);
                int pos0 = si?.Pos ?? 0;
                for (int i = 0; i < g0.Rows.Count; i++) seen[pos0 + i] = new RowSeen { Position = pos0 + i, Cells = Named(g0, cells[i]), Selected = g0.SelectedRow == i };
                d.Captures.Add(_ev.Shot("scan-first-view", img0));
                string cov = si is { } s0 ? $"visible rows only: rows {pos0}..{pos0 + g0.Rows.Count - 1} of {s0.Max + 1}" : $"visible rows only: {g0.Rows.Count} rows (scroll position unavailable)";
                d.Via("OCR scan (Windows.Media.Ocr)").Obs(cov);
                return new ScanResult { Rows = seen.Values.ToList(), Total = si is { } s1 ? s1.Max + 1 : null, Complete = si is { } s2 && pos0 == 0 && g0.Rows.Count >= s2.Max + 1, Coverage = cov };
            }
        }
        if (si == null) throw new StopException(Codes.ScanIncomplete, "the grid's scroll position cannot be read (GetScrollInfo failed); without it the rows cannot be counted, so nothing is decided", d);
        for (int i = 0; si.Value.Pos > 0; i++)
        {
            if (i >= 40) throw new StopException(Codes.ScanIncomplete, $"could not reach the first row: scroll position still {si.Value.Pos}", d);
            int before = si.Value.Pos;
            Wheel(t, l, 40, d);
            si = ScrollInfo(l) ?? throw new StopException(Codes.ScanIncomplete, "the scroll position became unreadable while scrolling up", d);
            if (si.Value.Pos >= before) throw new StopException(Codes.ScanIncomplete, $"the wheel did not move the grid up from row {before}", d);
        }
        int total = si.Value.Max + 1, views = 0;
        while (true)
        {
            views++;
            int pos = si!.Value.Pos;
            int expect = Math.Min(Math.Max(1, si.Value.Page), total - pos);
            var g = Look(t, l, d, out var img);
            using (img)
            {
                if (g.Rows.Count < expect)
                    throw new StopException(Codes.ScanIncomplete, $"view at row {pos}: {g.Rows.Count} full rows found in the image, {expect} expected from the scroll bar", d);
                var cells = ScanView(g, img, d);
                for (int i = 0; i < expect; i++) seen[pos + i] = new RowSeen { Position = pos + i, Cells = Named(g, cells[i]), Selected = g.SelectedRow == i };
                if (views == 1) d.Captures.Add(_ev.Shot("scan-first-view", img));
            }
            if (pos + expect >= total) break;
            if (views > total + 2) throw new StopException(Codes.ScanIncomplete, $"no end of grid after {views} views", d);
            int want = Math.Max(1, expect - 1);
            int notches = _linesPerNotch > 0 ? Math.Max(1, want / _linesPerNotch) : 1;
            Wheel(t, l, -notches, d);
            si = ScrollInfo(l) ?? throw new StopException(Codes.ScanIncomplete, "the scroll position became unreadable while scrolling down", d);
            int moved = si.Value.Pos - pos;
            if (moved <= 0) throw new StopException(Codes.ScanIncomplete, $"the wheel did not move the grid down from row {pos}", d);
            if (moved > expect) throw new StopException(Codes.ScanIncomplete, $"the grid moved {moved} rows after {expect} were read: rows would be skipped", d);
            if (_linesPerNotch == 0) { _linesPerNotch = Math.Max(1, moved / notches); d.Obs($"one wheel notch moves {_linesPerNotch} row(s) in this grid (measured)"); }
            if (si.Value.Max + 1 != total) throw new StopException(Codes.ScanIncomplete, $"the grid changed size during the scan ({total} rows, now {si.Value.Max + 1})", d);
        }
        var missing = Enumerable.Range(0, total).Where(i => !seen.ContainsKey(i)).ToList();
        if (missing.Count > 0) throw new StopException(Codes.ScanIncomplete, $"{missing.Count} of {total} rows were not read (first: {missing[0]})", d);
        string coverage = $"all {total} rows read in {views} view(s), from row 0 to row {total - 1}{(views > 1 ? ", scrolled with real wheel input" : "")}";
        d.Via("OCR scan (Windows.Media.Ocr)").Obs(coverage);
        return new ScanResult { Rows = seen.Values.ToList(), Total = total, Complete = true, Coverage = coverage };
    }

    static bool Matches(RowSeen r, RowQuery q)
    {
        string C(string n) => r.Cells.TryGetValue(n, out var v) ? v : "";
        if (q.Ref != null && Ocr.Fold(C("Ref #")) != Ocr.Fold(q.Ref)) return false;
        if (q.Account != null && Ocr.Fold(C("Account")) != Ocr.Fold(q.Account)) return false;
        if (q.AmountCents != null && Ocr.Fold(C("Amount")) != Ocr.Fold(Ocr.Money(q.AmountCents.Value))) return false;
        if (q.Date != null && Ocr.Fold(C("Date")) != Ocr.Fold(q.Date)) return false;
        return true;
    }

    static int Distance(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return dp[a.Length, b.Length];
    }

    /// <summary>
    /// Without a reference, a row that matches some fields exactly and misses the others only by what a misreading could
    /// explain (a close account, an amount or date that is not readable as one) may be the same kind of row: it counts
    /// against uniqueness, so one misread row cannot make the other look unique.
    /// </summary>
    static bool NearMatch(RowSeen r, RowQuery q)
    {
        if (q.Ref != null || Matches(r, q)) return false;
        string C(string n) => r.Cells.TryGetValue(n, out var v) ? v : "";
        int exact = 0;
        bool Field(string? want, string cell, Func<string, bool> unreadable)
        {
            if (want == null) return true;
            if (Ocr.Fold(cell) == Ocr.Fold(want)) { exact++; return true; }
            return unreadable(cell);
        }
        bool ok = Field(q.Account, C("Account"), c => { string a = Ocr.Fold(c), b = Ocr.Fold(q.Account!); return a.Length == 0 || Distance(a, b) <= Math.Max(2, b.Length / 5); })
               && Field(q.AmountCents is long ct ? Ocr.Money(ct) : null, C("Amount"), c => !Ocr.StrictAmount.IsMatch(c))
               && Field(q.Date, C("Date"), c => !Ocr.StrictDate.IsMatch(c));
        return ok && exact > 0;
    }

    public (List<RowSeen> Rows, List<RowSeen> Near, ScanResult Scan) Find(TargetWindow t, Layout l, RowQuery q, bool scroll, Decision d)
    {
        var scan = FullScan(t, l, scroll, d);
        var c = scan.Rows.Where(r => Matches(r, q)).ToList();
        var near = scan.Rows.Where(r => NearMatch(r, q)).ToList();
        d.Candidates = c.Count;
        d.Obs($"query {q}: {c.Count} candidate row(s){(near.Count > 0 ? $", {near.Count} more that a misreading could explain" : "")}");
        if (c.Count > 1)
        {
            var differ = c[0].Cells.Keys.Where(k => c.Select(r => r.Cells[k]).Distinct().Count() > 1).ToList();
            d.Discriminant = differ.Count > 0 ? $"columns that tell them apart: {string.Join(", ", differ)}" : "no visible column tells them apart";
        }
        return (c, near, scan);
    }

    (string A, string B) DoubleRead(string fromRowA, Bitmap img, Rectangle r, Func<string, string>? constrain, Decision d, string field)
    {
        using var prep = Ocr.Prepare(img, r, 3, 12, out _);
        string a = FaultA(Ocr.Norm(fromRowA)), b = _tess.ReadLine(prep);
        if (constrain != null)
        {
            string ca = constrain(Canon(a)), cb = constrain(Canon(b));
            if (ca != Canon(a) || cb != Canon(b)) d.Obs($"{field}: raw reads '{a}' / '{b}', digits-only positions taken as '{ca}' / '{cb}' (to be confirmed against the pixels)");
            return (ca, cb);
        }
        return (a, b);
    }

    string Pixels(Bitmap img, GridGeometry g, int row, string col, string value, bool right, Decision d)
    {
        var glyph = Glyphs.Verify(img, g.FullCell(row, g.Col(col)), value, right, _prof, g.Scale(_prof), out var detail);
        if (glyph != null) throw new StopException(Codes.AmbiguousReading, $"{col}: both readers say '{value}', but the pixels do not match it re-drawn in the grid font ({glyph}); look-alike glyphs suspected", d);
        d.Met($"{col} pixels match '{value}' re-drawn in the grid font ({detail}; any differing pixel refuses)");
        return value;
    }

    static string Canon(string s) => Regex.Replace(s.Replace('\u2014', '-').Replace('\u2013', '-'), @"\s+", "");

    string Agree(string field, (string A, string B) r, Regex? format, Decision d)
    {
        if (r.A != r.B) throw new StopException(Codes.AmbiguousReading, $"{field}: the two readers disagree ('{r.A}' vs '{r.B}'); refusing rather than guessing", d);
        if (format != null && !format.IsMatch(r.A)) throw new StopException(Codes.AmbiguousReading, $"{field}: read '{r.A}' by both readers, which is not a strict {field} format", d);
        d.Met($"{field} read '{r.A}' by both readers");
        return r.A;
    }

    static readonly Regex DetailTx = new(@"\bTX-\d{4}\b"), DetailRef = new(@"\b(INV|PMT)-\d{4}\b"), DetailAmt = new(@"(-?[0-9][0-9,]*\.\d{2})\s?USD"), DetailStatus = new(@"\b(Open|Paid|Posted)\b"), DetailPaid = new(@"paid by (TX-\d{4})");

    Selection ReadDetail(TargetWindow t, Layout l, Decision d)
    {
        var r = l.DetailRect;
        using var img = Evidence.Capture(r);
        d.Captures.Add(_ev.Shot("detail-panel", img));
        using var prep = Ocr.Prepare(img, new Rectangle(0, 0, img.Width, img.Height), 2, 12, out _);
        string a = Ocr.ConstrainLine(FaultA(_win.ReadLine(prep)).Replace('\u2014', '-').Replace('\u2013', '-')), b = Ocr.ConstrainLine(_tess.ReadLine(prep).Replace('\u2014', '-').Replace('\u2013', '-'));
        d.Obs($"detail panel: reader A '{a}' | reader B '{b}'");
        string? F(Regex re, string s, int grp = 0) { var m = re.Match(s); return m.Success ? m.Groups[grp].Value : null; }
        var fields = new (string Name, Regex Re, int Grp)[] { ("transaction id", DetailTx, 0), ("reference", DetailRef, 0), ("amount", DetailAmt, 1), ("status", DetailStatus, 0) };
        var sel = new Selection();
        foreach (var (name, re, grp) in fields)
        {
            var va = F(re, a, grp); var vb = F(re, b, grp);
            if (va == null || vb == null || va != vb)
                throw new StopException(Codes.AmbiguousReading, $"detail panel {name}: reader A '{va ?? "none"}', reader B '{vb ?? "none"}'", d);
            if (name == "amount" && Ocr.Cents(va) == null) throw new StopException(Codes.AmbiguousReading, $"detail panel amount '{va}' is not a strict amount", d);
            switch (name) { case "transaction id": sel.Tx = va; break; case "reference": sel.Ref = va; break; case "amount": sel.AmountCents = Ocr.Cents(va)!.Value; break; default: sel.Status = va; break; }
        }
        var pa = F(DetailPaid, a, 1); var pb = F(DetailPaid, b, 1);
        if (pa != pb) throw new StopException(Codes.AmbiguousReading, $"detail panel 'paid by': reader A '{pa ?? "none"}', reader B '{pb ?? "none"}'", d);
        sel.PaidBy = pa;
        d.Met($"detail panel read by both readers: {sel.Tx} {sel.Ref} {Ocr.Money(sel.AmountCents)} {sel.Status}{(sel.PaidBy != null ? " paid by " + sel.PaidBy : "")}");
        return sel;
    }

    /// <summary>
    /// The proven click: locate on a complete scan, click with real input, then accept only if the highlight moved to that
    /// row, both readers agree on its cells, and the detail panel shows the expected business identity. A click that lands
    /// elsewhere stops the route; no corrective click is sent.
    /// </summary>
    public Selection SelectVerified(TargetWindow t, Layout l, RowQuery q, string? expectTx, Decision d)
    {
        var scan = FullScan(t, l, true, d);
        var rows = scan.Rows;
        var cands = rows.Where(r => Matches(r, q)).ToList();
        d.Candidates = cands.Count;
        if (cands.Count == 0) throw new StopException(Codes.NotFound, $"no row matches {q} ({scan.Coverage})", d);
        if (cands.Count > 1)
        {
            var differ = cands[0].Cells.Keys.Where(k => cands.Select(r => r.Cells[k]).Distinct().Count() > 1).ToList();
            throw new StopException(Codes.AmbiguousTarget, $"{cands.Count} rows match {q} (rows {string.Join(", ", cands.Select(c => c.Position))}); they differ only in {string.Join(", ", differ)}; no click sent", d);
        }
        var near = rows.Where(r => NearMatch(r, q)).ToList();
        if (near.Count > 0)
            throw new StopException(Codes.AmbiguousTarget, $"1 row matches {q} exactly, and {near.Count} more could be the same kind of row misread: {string.Join("; ", near.Select(r => $"row {r.Position} reads '{r.Cells.GetValueOrDefault("Account", "")}' {r.Cells.GetValueOrDefault("Amount", "")}"))}; no click sent", d);
        var target = cands[0];
        var look = rows.Where(r => Ocr.Fold(r.Cells.GetValueOrDefault("Account", "")) == Ocr.Fold(target.Cells.GetValueOrDefault("Account", ""))
                                && Ocr.Fold(r.Cells.GetValueOrDefault("Amount", "")) == Ocr.Fold(target.Cells.GetValueOrDefault("Amount", ""))
                                && r.Cells.GetValueOrDefault("Type", "") == target.Cells.GetValueOrDefault("Type", "")).ToList();
        if (look.Count > 1)
        {
            d.Discriminant = $"Ref # ({string.Join(" vs ", look.Select(r => r.Cells.GetValueOrDefault("Ref #", "?")))}) and Date; Account, Type and Amount are identical";
            d.Obs($"{look.Count} rows share Account, Type and Amount with the target");
        }

        // Navigate: real wheel notches until the target row is on screen, then locate it again on a new capture.
        var si = ScrollInfo(l) ?? throw new StopException(Codes.ScanIncomplete, "scroll position unreadable before navigating", d);
        int page = Math.Max(1, si.Page), total = si.Max + 1;
        int desired = Math.Clamp(target.Position - page / 2, 0, Math.Max(0, total - page));
        int lpn = _linesPerNotch > 0 ? _linesPerNotch : 3;
        bool InView((int Pos, int Max, int Page) v) => target.Position >= v.Pos && target.Position < v.Pos + page;
        for (int i = 0; i < 40 && !InView(si); i++)
        {
            int delta = desired - si.Pos;
            int notches = Math.Clamp(delta / lpn, -50, 50);
            if (notches == 0) notches = Math.Sign(delta);
            Wheel(t, l, -notches, d);
            si = ScrollInfo(l) ?? throw new StopException(Codes.ScanIncomplete, "scroll position unreadable while navigating", d);
        }
        if (!InView(si)) throw new StopException(Codes.TargetChanged, $"row {target.Position} could not be brought on screen", d);
        d.Obs($"target is row {target.Position} of {total}: on screen at rows {si.Pos}..{si.Pos + page - 1} after {_notches} wheel notches in all, then located again");
        var g = Look(t, l, d, out var img);
        int idx;
        using (img)
        {
            var cells = g.Scan(img, _win);
            var here = Enumerable.Range(0, g.Rows.Count).Where(i => Matches(new RowSeen { Cells = Named(g, cells[i]) }, q)).ToList();
            if (here.Count != 1) throw new StopException(here.Count == 0 ? Codes.TargetChanged : Codes.AmbiguousTarget, $"after scrolling, {here.Count} visible rows match {q}", d);
            idx = here[0];
        }
        int clickRow = idx;
        if (Faults.Has("wrong-click"))
        {
            clickRow = idx + 1 < g.Rows.Count ? idx + 1 : idx - 1;
            d.Obs($"FAULT wrong-click: the click is sent to view row {clickRow} instead of {idx}");
        }
        var point = g.ScreenCenter(clickRow, g.Col("Ref #"));
        RequireResponsive(t, d);
        t.EnsureForeground(t.Hwnd, d);
        t.EnsureUnobstructed(point, t.Hwnd, d);
        using (var before = Evidence.Capture(t.WindowRect)) d.Captures.Add(_ev.Shot("before-click", before));
        Native.Click(point.X, point.Y);
        d.Via("SendInput click").Obs($"clicked {point.X},{point.Y} (row {clickRow} of the view, Ref # cell)");

        GridGeometry? g2 = null; Bitmap? img2 = null;
        for (int i = 0; i < 8; i++)
        {
            Thread.Sleep(150);
            img2?.Dispose();
            g2 = Look(t, l, d, out img2);
            if (g2.SelectedRow == idx && g2.Rows.Count > idx && Math.Abs(g2.Rows[idx].Y0 - g.Rows[idx].Y0) <= 1) break;
        }
        using (img2)
        {
            d.Captures.Add(_ev.Shot("after-click", img2!));
            if (g2!.SelectedRow != idx)
            {
                string got = "no row";
                if (g2.SelectedRow is int s && s < g2.Rows.Count)
                {
                    var hl = g2.ReadRow(img2!, _win, s, 3);
                    got = $"view row {s}, which reads {hl[g2.Col("Ref #")] ?? "?"} '{hl[g2.Col("Account")] ?? "?"}' {hl[g2.Col("Amount")] ?? "?"}";
                }
                throw new StopException(Codes.SelectionNotObserved, $"after the click the highlight is on {got}; the target is view row {idx} ({q}); stopped, no second click is sent", d);
            }
            d.Met("highlight is on the clicked row in a new capture");
            var rowA = g2.ReadRow(img2!, _win, idx, 3);
            string A(string col) => rowA[g2.Col(col)] ?? "";
            var sel = new Selection
            {
                Ref = Pixels(img2!, g2, idx, "Ref #", Agree("Ref #", DoubleRead(A("Ref #"), img2!, g2.Cell(idx, g2.Col("Ref #")), Ocr.ConstrainRef, d, "Ref #"), Ocr.StrictRef, d), false, d),
                Date = Pixels(img2!, g2, idx, "Date", Agree("Date", DoubleRead(A("Date"), img2!, g2.Cell(idx, g2.Col("Date")), Ocr.ConstrainNumeric, d, "Date"), Ocr.StrictDate, d), false, d),
                Account = Agree("Account", DoubleRead(A("Account"), img2!, g2.Cell(idx, g2.Col("Account")), null, d, "Account"), null, d),
            };
            string amt = Pixels(img2!, g2, idx, "Amount", Agree("Amount", DoubleRead(A("Amount"), img2!, g2.Cell(idx, g2.Col("Amount")), Ocr.ConstrainNumeric, d, "Amount"), Ocr.StrictAmount, d), true, d);
            sel.AmountCents = Ocr.Cents(amt)!.Value;
            if (q.Ref != null && sel.Ref != q.Ref) throw new StopException(Codes.TargetChanged, $"selected row reads {sel.Ref}, expected {q.Ref}", d);
            if (q.Account != null && Ocr.Fold(sel.Account) != Ocr.Fold(q.Account)) throw new StopException(Codes.TargetChanged, $"selected row account reads '{sel.Account}', expected '{q.Account}'", d);
            if (q.AmountCents != null && sel.AmountCents != q.AmountCents) throw new StopException(Codes.AmountMismatch, $"selected row amount reads {Ocr.Money(sel.AmountCents)}, expected {Ocr.Money(q.AmountCents.Value)}", d);
            var det = ReadDetail(t, l, d);
            if (det.Ref != sel.Ref || det.AmountCents != sel.AmountCents) throw new StopException(Codes.TargetChanged, $"detail panel shows {det.Ref} {Ocr.Money(det.AmountCents)}, the grid row shows {sel.Ref} {Ocr.Money(sel.AmountCents)}", d);
            if (expectTx != null && det.Tx != expectTx) throw new StopException(Codes.TargetChanged, $"detail panel shows {det.Tx}, the approved operation targets {expectTx}", d);
            sel.Tx = det.Tx; sel.Status = det.Status; sel.PaidBy = det.PaidBy;
            d.Met($"business identity {sel.Tx} read on the detail panel{(expectTx != null ? " and equal to the approved row" : "")}");
            return sel;
        }
    }

    /// <summary>Only before the Post click. After it, nothing is sent to the dialog.</summary>
    void CancelDialogBeforePost(TargetWindow t, IntPtr dlg, Decision d)
    {
        if (dlg == IntPtr.Zero || !Native.IsWindow(dlg) || !t.IsResponsive(dlg)) return;
        try { t.EnsureForeground(dlg, d); Native.Chord(Native.VK_ESCAPE); Thread.Sleep(300); d.Obs("payment dialog cancelled with Escape, before any Post click"); }
        catch (StopException) { d.Obs("could not cancel the dialog"); }
    }

    /// <summary>
    /// Opens the dialog by keyboard, fills it through UIA, reads it back, then sends the one write input. After the Post
    /// click no key, click or UIA write is sent to the dialog: a slow save is left alone and the outcome is UNKNOWN.
    /// </summary>
    public PaymentOutcome Submit(TargetWindow t, Layout l, Operation op, Action markWriteSent, Decision d)
    {
        RequireResponsive(t, d);
        t.EnsureForeground(t.Hwnd, d);
        Native.Chord(Native.VK_CONTROL, Native.VK_P);
        d.Via("keyboard").Obs("sent Ctrl+P (the adapter's keyboard path to the painted toolbar's New payment command)");
        IntPtr dlg = IntPtr.Zero;
        for (int i = 0; i < 30 && dlg == IntPtr.Zero; i++) { Thread.Sleep(100); dlg = t.FindDialog(_cfg.DialogTitle); }
        if (dlg == IntPtr.Zero) throw new StopException(Codes.DialogNotOpened, "the payment dialog did not open within 3 s", d);
        Thread.Sleep(250);
        Point post;
        try
        {
            Uia.Call(t, dlg, "fill payment dialog", ct =>
            {
                System.Windows.Automation.AutomationElement E(string id) => Uia.ById(dlg, id) ?? throw new StopException(Codes.DialogMismatch, $"dialog control '{id}' not found", d);
                string inv = Uia.GetValue(E("invoiceBox"));
                if (!inv.StartsWith(op.InvoiceRef + " ", StringComparison.Ordinal)) throw new StopException(Codes.DialogMismatch, $"dialog is bound to '{inv}', expected {op.InvoiceRef}", d);
                d.Met($"dialog invoice field reads '{inv}'");
                string label = E("amountLabel").Current.Name;
                if (!label.Contains("(" + op.Currency + ")", StringComparison.Ordinal)) throw new StopException(Codes.DialogMismatch, $"the amount field is labelled '{label}', the operation is in {op.Currency}", d);
                d.Met($"amount field labelled '{label}'");
                var want = new[] { ("amountBox", Ocr.Money(op.AmountCents)), ("dateBox", op.PayDate), ("memoBox", op.OpId) };
                foreach (var (id, v) in want)
                {
                    ct.ThrowIfCancellationRequested();
                    Uia.SetValue(E(id), v);
                }
                foreach (var (id, v) in want)
                {
                    var back = Uia.GetValue(E(id));
                    if (back != v) throw new StopException(Codes.DialogMismatch, $"{id} reads '{back}' after writing '{v}'", d);
                }
                d.Met($"dialog fields read back: amount {Ocr.Money(op.AmountCents)}, date {op.PayDate}, memo {op.OpId}");
                return 0;
            }, d);
            post = Uia.Call(t, dlg, "locate post button", () =>
            {
                var b = Uia.ById(dlg, "postButton") ?? throw new StopException(Codes.DialogMismatch, "post button not found", d);
                var r = Uia.Rect(b);
                return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            }, d);
            t.EnsureForeground(dlg, d);
            t.EnsureUnobstructed(post, dlg, d);
            using (var img = Evidence.Capture(Native.Rect(dlg))) d.Captures.Add(_ev.Shot("before-submit", img));
            Faults.ThrowIf("throw-before-submit", "just before the Post click");
            markWriteSent();
        }
        catch (Exception) { CancelDialogBeforePost(t, dlg, d); throw; }

        Native.Click(post.X, post.Y);
        d.Via("SendInput click").Obs("write input sent: click on Post payment");
        _ev.Log("write_input_sent", new { op = op.OpId });
        if (Faults.Has("crash-after-submit"))
        {
            _ev.Log("fault", new { fault = "crash-after-submit" });
            Environment.FailFast("fault injected: server crash between submit and verification");
        }
        Faults.ThrowIf("throw-after-submit", "just after the Post click");

        bool responsive = true, open = true;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(300);
            responsive = t.IsResponsive();
            open = Native.IsWindow(dlg) && Native.IsWindowVisible(dlg);
            if (responsive && !open) break;
        }
        if (!responsive)
        {
            using var img = Evidence.Capture(t.WindowRect);
            d.Captures.Add(_ev.Shot("not-responding", img));
            return new PaymentOutcome { State = States.Unknown, Reason = $"the application stopped answering after the submit input (WM_NULL unanswered within 500 ms); dialog {(open ? "still on screen" : "gone")}; read by screen capture only, no UIA call made" };
        }
        if (open)
        {
            using (var img = Evidence.Capture(Native.Rect(dlg))) d.Captures.Add(_ev.Shot("dialog-still-open", img));
            string msg = "";
            try { msg = Uia.Call(t, dlg, "read dialog error", () => Uia.ById(dlg, "errorLabel")?.Current.Name ?? "", d); } catch (StopException) { }
            return new PaymentOutcome { State = States.Unknown, Reason = $"the dialog was still open {waited.Elapsed.TotalSeconds:0} s after the Post click (message: '{msg}'); it is left as it is: no key or click is sent after Post" };
        }
        try
        {
            var sel = SelectVerified(t, l, new RowQuery { Ref = op.InvoiceRef, Account = op.Account, AmountCents = op.AmountCents }, op.TxId, d);
            if (sel.Status == "Paid" && sel.PaidBy != null)
                return new PaymentOutcome { State = States.Confirmed, Reason = $"postcondition read back: {sel.Tx} {sel.Ref} is Paid, paid by {sel.PaidBy}", PaymentTx = sel.PaidBy };
            return new PaymentOutcome { State = States.Unknown, Reason = $"dialog closed but {sel.Ref} reads '{sel.Status}'" };
        }
        catch (StopException ex)
        {
            return new PaymentOutcome { State = States.Unknown, Reason = $"dialog closed but the postcondition could not be read ({ex.Code}: {ex.Message})" };
        }
    }

    public List<PathResult> Inspect(TargetWindow t, Layout l, Decision d)
    {
        var res = Diagnostics.Run(t, _ev, d);
        var img = new PathResult { Path = "Image", Surface = "grid" };
        var ocr = new PathResult { Path = "OCR", Surface = "grid" };
        var g = Look(t, l, d, out var cap);
        using (cap)
        {
            d.Captures.Add(_ev.Shot("inspect-grid", cap));
            img.Result = g.ColumnIndex.Count == _prof.Columns.Length ? "AVAILABLE" : "PARTIAL";
            img.Observations.Add($"{g.Spans.Count} column separators, {g.Rows.Count} full rows of {g.RowHeight}px, header bottom {g.HeaderBottom}px; columns named from the header: {string.Join(", ", g.ColumnIndex.OrderBy(k => k.Value).Select(k => k.Key))}");
            using var head = Ocr.Prepare(cap, new Rectangle(0, 0, g.Spans[^1].X1, g.HeaderBottom), 2, 12, out _);
            string a = _win.ReadLine(head), b = _tess.ReadLine(head);
            ocr.Result = _prof.Columns.All(c => Ocr.Fold(a).Contains(Ocr.Fold(c))) && _prof.Columns.All(c => Ocr.Fold(b).Contains(Ocr.Fold(c))) ? "AVAILABLE" : "PARTIAL";
            ocr.Observations.Add($"header, reader A ({_win.Name}): '{a}'");
            ocr.Observations.Add($"header, reader B ({_tess.Name}): '{b}'");
        }
        res.Add(img); res.Add(ocr);
        int aw = Native.GetAwarenessFromDpiAwarenessContext(Native.GetWindowDpiAwarenessContext(t.Hwnd));
        d.Obs($"target window: GetDpiForWindow = {Native.GetDpiForWindow(t.Hwnd)}, DPI awareness = {(aw switch { 0 => "unaware", 1 => "system", 2 => "per-monitor", _ => aw.ToString() })}; driver is per-monitor v2");
        return res;
    }
}
