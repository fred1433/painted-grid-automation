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

public sealed class PaymentOutcome
{
    public string State { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? PaymentTx { get; set; }
}

/// <summary>
/// Locates, selects and writes through the outside of the window only: screen capture, SendInput, Win32, UI Automation.
/// Coordinates are never reused across steps: every step captures and locates again.
/// </summary>
public sealed class LedgerDriver : IDisposable
{
    readonly DriverConfig _cfg;
    readonly Evidence _ev;
    readonly GridProfile _prof = new();
    readonly WinRtOcr _win;
    readonly TesseractOcr _tess;
    public string Readers => $"reader A: {_win.Name} on the whole row{(_cfg.OcrFault != null ? " [fault injected: " + _cfg.OcrFault + "]" : "")}; reader B: {_tess.Name} on each cell";

    /// <summary>Test-only fault on reader A, named and logged: a dropped digit group.</summary>
    string FaultA(string s) => _cfg.OcrFault == "1000-as-100" ? s.Replace("1,000.00", "100.00") : s;

    public LedgerDriver(DriverConfig cfg, Evidence ev)
    {
        _cfg = cfg; _ev = ev;
        _win = WinRtOcr.TryCreate(out var why) ?? throw new StopException(Codes.NotAvailable, "Windows.Media.Ocr unavailable: " + why);
        _tess = new TesseractOcr(cfg.TessdataDir);
        if (cfg.OcrFault != null) ev.Log("fault_active", new { fault = cfg.OcrFault, reader = "A" });
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

    GridGeometry Look(Layout l, Decision d, out Bitmap img)
    {
        var r = l.GridRect;
        img = Evidence.Capture(r);
        return GridGeometry.Detect(img, r, _prof, _win, d, b => _tess.ReadLine(b));
    }

    void RequireResponsive(TargetWindow t, Decision d)
    {
        if (!t.IsResponsive()) throw new StopException(Codes.AppNotResponding, "the window did not answer WM_NULL within 500 ms; no input is sent to a window that does not respond", d);
    }

    Point GridCenter(Layout l) { var r = l.GridRect; return new Point(r.X + r.Width / 2, r.Y + r.Height / 2); }

    void Wheel(TargetWindow t, Layout l, int notches, Decision d)
    {
        RequireResponsive(t, d);
        t.EnsureForeground(t.Hwnd, d);
        var c = GridCenter(l);
        t.EnsureUnobstructed(c, t.Hwnd, d);
        Native.Wheel(c.X, c.Y, notches * 120);
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
            if (Uia.GetValue(e) != "") { Uia.SetValue(e, ""); d.Obs("search box cleared through UIA ValuePattern"); }
            return 0;
        }, d);
    }

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

    /// <summary>Reads every row, scrolling through the whole grid, so a duplicate further down is never missed.</summary>
    public List<RowSeen> FullScan(TargetWindow t, Layout l, bool scroll, Decision d)
    {
        RequireResponsive(t, d);
        var seen = new List<RowSeen>();
        var keys = new HashSet<int>();
        if (scroll) Wheel(t, l, 40, d);
        int views = 0;
        for (; views < 20; views++)
        {
            var g = Look(l, d, out var img);
            using (img)
            {
                var cells = g.Scan(img, _win);
                var si = ScrollInfo(l);
                int pos = si?.Pos ?? 0;
                for (int i = 0; i < g.Rows.Count; i++)
                    if (keys.Add(pos + i)) seen.Add(new RowSeen { Position = pos + i, Cells = Named(g, cells[i]), Selected = g.SelectedRow == i });
                if (views == 0) d.Captures.Add(_ev.Shot("scan-first-view", img));
                if (!scroll) break;
                if (si == null) { d.Obs("scroll position unavailable: only the visible rows were read"); break; }
                if (pos + g.Rows.Count >= si.Value.Max + 1) break;
                Wheel(t, l, -Math.Max(1, (g.Rows.Count - 1) / 3), d);
                var after = ScrollInfo(l);
                if (after == null || after.Value.Pos == pos) break;
            }
        }
        d.Via("OCR scan (Windows.Media.Ocr)").Obs($"read {seen.Count} rows in {views + 1} view(s){(scroll ? " (scrolled with real wheel input)" : "")}");
        return seen;
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

    public List<RowSeen> Find(TargetWindow t, Layout l, RowQuery q, bool scroll, Decision d)
    {
        var all = FullScan(t, l, scroll, d);
        var c = all.Where(r => Matches(r, q)).ToList();
        d.Candidates = c.Count;
        d.Obs($"query {q}: {c.Count} candidate row(s)");
        if (c.Count > 1)
        {
            var differ = c[0].Cells.Keys.Where(k => c.Select(r => r.Cells[k]).Distinct().Count() > 1).ToList();
            d.Discriminant = differ.Count > 0 ? $"columns that tell them apart: {string.Join(", ", differ)}" : "no visible column tells them apart";
        }
        return c;
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
        d.Met($"{col} pixels match '{value}' re-drawn in the grid font ({detail}; refused above 1 in 4)");
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
    /// The proven click: locate on a fresh full scan, click with real input, then accept only if the highlight moved to that
    /// row, both readers agree on its cells, and the detail panel shows the expected business identity.
    /// </summary>
    public Selection SelectVerified(TargetWindow t, Layout l, RowQuery q, string? expectTx, Decision d)
    {
        var rows = FullScan(t, l, true, d);
        var cands = rows.Where(r => Matches(r, q)).ToList();
        d.Candidates = cands.Count;
        if (cands.Count == 0) throw new StopException(Codes.NotFound, $"no row matches {q}", d);
        if (cands.Count > 1)
        {
            var differ = cands[0].Cells.Keys.Where(k => cands.Select(r => r.Cells[k]).Distinct().Count() > 1).ToList();
            throw new StopException(Codes.AmbiguousTarget, $"{cands.Count} rows match {q}; they differ only in {string.Join(", ", differ)}; no click sent", d);
        }
        var target = cands[0];
        var look = rows.Where(r => Ocr.Fold(r.Cells.GetValueOrDefault("Account", "")) == Ocr.Fold(target.Cells.GetValueOrDefault("Account", ""))
                                && Ocr.Fold(r.Cells.GetValueOrDefault("Amount", "")) == Ocr.Fold(target.Cells.GetValueOrDefault("Amount", ""))
                                && r.Cells.GetValueOrDefault("Type", "") == target.Cells.GetValueOrDefault("Type", "")).ToList();
        if (look.Count > 1)
        {
            d.Discriminant = $"Ref # ({string.Join(" vs ", look.Select(r => r.Cells.GetValueOrDefault("Ref #", "?")))}) and Date; Account, Type and Amount are identical";
            d.Obs($"{look.Count} rows share Account, Type and Amount with the target");
        }

        // Navigate: back to the top, then real wheel notches towards the target, then relocate on a new capture.
        Wheel(t, l, 40, d);
        var si = ScrollInfo(l);
        int page = si?.Page ?? 10;
        int notches = Math.Max(0, (target.Position - page / 2) / 3);
        if (notches > 0) { Wheel(t, l, -notches, d); d.Obs($"target is row {target.Position}: scrolled {notches} wheel notches, then located again"); }
        var g = Look(l, d, out var img);
        int idx;
        using (img)
        {
            var cells = g.Scan(img, _win);
            var here = Enumerable.Range(0, g.Rows.Count).Where(i => Matches(new RowSeen { Cells = Named(g, cells[i]) }, q)).ToList();
            if (here.Count != 1) throw new StopException(here.Count == 0 ? Codes.TargetChanged : Codes.AmbiguousTarget, $"after scrolling, {here.Count} visible rows match {q}", d);
            idx = here[0];
        }
        var point = g.ScreenCenter(idx, g.Col("Ref #"));
        RequireResponsive(t, d);
        t.EnsureForeground(t.Hwnd, d);
        t.EnsureUnobstructed(point, t.Hwnd, d);
        using (var before = Evidence.Capture(t.WindowRect)) d.Captures.Add(_ev.Shot("before-click", before));
        Native.Click(point.X, point.Y);
        d.Via("SendInput click").Obs($"clicked {point.X},{point.Y} (row {idx} of the view, Ref # cell)");

        GridGeometry? g2 = null; Bitmap? img2 = null;
        for (int i = 0; i < 8; i++)
        {
            Thread.Sleep(150);
            img2?.Dispose();
            g2 = Look(l, d, out img2);
            if (g2.SelectedRow == idx && g2.Rows.Count > idx && Math.Abs(g2.Rows[idx].Y0 - g.Rows[idx].Y0) <= 1) break;
        }
        using (img2)
        {
            if (g2!.SelectedRow != idx) throw new StopException(Codes.SelectionNotObserved, $"after the click the highlighted row is {(g2.SelectedRow?.ToString() ?? "none")}, expected {idx}", d);
            d.Met("highlight is on the clicked row in a new capture");
            d.Captures.Add(_ev.Shot("after-click", img2));
            var rowA = g2.ReadRow(img2, _win, idx, 3);
            string A(string col) => rowA[g2.Col(col)] ?? "";
            var sel = new Selection
            {
                Ref = Pixels(img2, g2, idx, "Ref #", Agree("Ref #", DoubleRead(A("Ref #"), img2, g2.Cell(idx, g2.Col("Ref #")), Ocr.ConstrainRef, d, "Ref #"), Ocr.StrictRef, d), false, d),
                Date = Pixels(img2, g2, idx, "Date", Agree("Date", DoubleRead(A("Date"), img2, g2.Cell(idx, g2.Col("Date")), Ocr.ConstrainNumeric, d, "Date"), Ocr.StrictDate, d), false, d),
                Account = Agree("Account", DoubleRead(A("Account"), img2, g2.Cell(idx, g2.Col("Account")), null, d, "Account"), null, d),
            };
            string amt = Pixels(img2, g2, idx, "Amount", Agree("Amount", DoubleRead(A("Amount"), img2, g2.Cell(idx, g2.Col("Amount")), Ocr.ConstrainNumeric, d, "Amount"), Ocr.StrictAmount, d), true, d);
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

    public void CancelDialog(TargetWindow t, IntPtr dlg, Decision d)
    {
        if (dlg == IntPtr.Zero || !Native.IsWindow(dlg) || !t.IsResponsive(dlg)) return;
        try { t.EnsureForeground(dlg, d); Native.Chord(Native.VK_ESCAPE); Thread.Sleep(300); d.Obs("payment dialog cancelled with Escape"); }
        catch (StopException) { d.Obs("could not cancel the dialog"); }
    }

    /// <summary>Opens the dialog by keyboard, fills it through UIA, reads it back, then sends the one write input.</summary>
    public PaymentOutcome Submit(TargetWindow t, Layout l, Operation op, Action markWriteSent, Decision d)
    {
        RequireResponsive(t, d);
        t.EnsureForeground(t.Hwnd, d);
        Native.Chord(Native.VK_CONTROL, Native.VK_P);
        d.Via("keyboard").Obs("sent Ctrl+P (keyboard path for the painted toolbar's New payment button)");
        IntPtr dlg = IntPtr.Zero;
        for (int i = 0; i < 30 && dlg == IntPtr.Zero; i++) { Thread.Sleep(100); dlg = t.FindDialog(_cfg.DialogTitle); }
        if (dlg == IntPtr.Zero) throw new StopException(Codes.DialogNotOpened, "the payment dialog did not open within 3 s", d);
        Thread.Sleep(250);
        try
        {
            Uia.Call(t, dlg, "fill payment dialog", () =>
            {
                var root = System.Windows.Automation.AutomationElement.FromHandle(dlg);
                System.Windows.Automation.AutomationElement E(string id) => Uia.ById(dlg, id) ?? throw new StopException(Codes.DialogMismatch, $"dialog control '{id}' not found", d);
                string inv = Uia.GetValue(E("invoiceBox"));
                if (!inv.Contains(op.InvoiceRef, StringComparison.Ordinal)) throw new StopException(Codes.DialogMismatch, $"dialog is bound to '{inv}', expected {op.InvoiceRef}", d);
                d.Met($"dialog invoice field reads '{inv}'");
                var want = new[] { ("amountBox", Ocr.Money(op.AmountCents)), ("dateBox", op.PayDate), ("memoBox", op.OpId) };
                foreach (var (id, v) in want) Uia.SetValue(E(id), v);
                foreach (var (id, v) in want)
                {
                    var back = Uia.GetValue(E(id));
                    if (back != v) throw new StopException(Codes.DialogMismatch, $"{id} reads '{back}' after writing '{v}'", d);
                }
                d.Met($"dialog fields read back: amount {Ocr.Money(op.AmountCents)}, date {op.PayDate}, memo {op.OpId}");
                return 0;
            }, d);
        }
        catch (StopException) { CancelDialog(t, dlg, d); throw; }

        Point post;
        try
        {
            post = Uia.Call(t, dlg, "locate post button", () =>
            {
                var b = Uia.ById(dlg, "postButton") ?? throw new StopException(Codes.DialogMismatch, "post button not found", d);
                var r = Uia.Rect(b);
                return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            }, d);
            t.EnsureForeground(dlg, d);
            t.EnsureUnobstructed(post, dlg, d);
        }
        catch (StopException) { CancelDialog(t, dlg, d); throw; }
        using (var img = Evidence.Capture(Native.Rect(dlg))) d.Captures.Add(_ev.Shot("before-submit", img));

        markWriteSent();
        Native.Click(post.X, post.Y);
        d.Via("SendInput click").Obs("write input sent: click on Post payment");
        _ev.Log("write_input_sent", new { op = op.OpId });
        if (Environment.GetEnvironmentVariable("PGA_FAULT_CRASH_AFTER_SUBMIT") == "1")
        {
            _ev.Log("fault", new { fault = "driver crash between submit and verification" });
            Environment.FailFast("fault injected: driver crash between submit and verification");
        }

        bool responsive = true, open = true;
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
            string msg = "";
            try { msg = Uia.Call(t, dlg, "read dialog error", () => Uia.ById(dlg, "errorLabel")?.Current.Name ?? "", d); } catch (StopException) { }
            CancelDialog(t, dlg, d);
            return new PaymentOutcome { State = States.Unknown, Reason = $"the dialog stayed open after the submit input (message: '{msg}')" };
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
        try
        {
            var g = Look(l, d, out var cap);
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
        }
        catch (StopException ex) { img.Result = ex.Code; img.Observations.Add(ex.Message); }
        res.Add(img); res.Add(ocr);
        int aw = Native.GetAwarenessFromDpiAwarenessContext(Native.GetWindowDpiAwarenessContext(t.Hwnd));
        d.Obs($"target window: GetDpiForWindow = {Native.GetDpiForWindow(t.Hwnd)}, DPI awareness = {(aw switch { 0 => "unaware", 1 => "system", 2 => "per-monitor", _ => aw.ToString() })}; driver is per-monitor v2");
        return res;
    }
}
