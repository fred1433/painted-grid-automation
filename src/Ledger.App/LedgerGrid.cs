using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Ledger.App;

/// <summary>
/// A grid painted entirely in OnPaint. It has no child windows and exposes no rows, cells or selection
/// to accessibility clients: the only way to know what it shows is to look at it.
/// </summary>
public sealed class LedgerGrid : Control
{
    public static readonly string[] Columns = { "Date", "Ref #", "Account", "Type", "Amount", "Balance", "Status" };
    static readonly int[] BaseWidths = { 96, 86, 236, 78, 104, 110, 76 };
    static readonly bool[] RightAligned = { false, false, false, false, true, true, false };

    readonly LedgerStore _store;
    readonly float _s;
    readonly int[] _widths;
    List<Entry> _view = new();
    int _top;
    string _filter = "";
    public string SortMode { get; private set; } = "date";
    public Entry? Selected { get; private set; }
    public event Action? ViewChanged;
    public event Action<Entry?, string>? SelectionChanged;

    int HeaderH => (int)(26 * _s);
    int RowH => (int)(24 * _s);
    public int VisibleRows => Math.Max(1, (Height - HeaderH) / RowH);
    public int ViewCount => _view.Count;

    public LedgerGrid(LedgerStore store, float s, int seed)
    {
        _store = store; _s = s;
        var rng = new Random(seed * 7919 + 13);
        _widths = BaseWidths.Select(w => (int)((w + rng.Next(-8, 22)) * s)).ToArray();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        BackColor = Color.White;
        Rebuild();
    }

    public int FirstRow
    {
        get => _top;
        set { int v = Math.Clamp(value, 0, Math.Max(0, _view.Count - VisibleRows)); if (v != _top) { _top = v; Invalidate(); ViewChanged?.Invoke(); } }
    }

    public string Filter { set { _filter = value ?? ""; Rebuild(); } }

    public void SortBy(string mode) { SortMode = mode; Rebuild(); }

    public void Rebuild()
    {
        IEnumerable<Entry> q = _store.Visible;
        if (_filter.Length > 0) q = q.Where(e => e.Account.Contains(_filter, StringComparison.OrdinalIgnoreCase) || e.Ref.Contains(_filter, StringComparison.OrdinalIgnoreCase));
        q = SortMode == "amount"
            ? q.OrderByDescending(e => e.AmountCents).ThenBy(e => e.Ref, StringComparer.Ordinal)
            : q.OrderBy(e => e.Date, StringComparer.Ordinal).ThenBy(e => e.Ref, StringComparer.Ordinal);
        _view = q.ToList();
        _top = Math.Clamp(_top, 0, Math.Max(0, _view.Count - VisibleRows));
        Invalidate();
        ViewChanged?.Invoke();
    }

    public static string Display(Entry e, long cents)
    {
        var s = LedgerStore.Money(cents);
        if (!e.LookAlikeGlyphs) return s;
        int i1 = s.IndexOf('1'); if (i1 >= 0) s = s[..i1] + "l" + s[(i1 + 1)..];
        int i0 = s.IndexOf('0'); if (i0 >= 0) s = s[..i0] + "O" + s[(i0 + 1)..];
        return s;
    }

    string Cell(Entry e, int c) => c switch
    {
        0 => e.Date, 1 => e.Ref, 2 => e.Account, 3 => e.Type,
        4 => Display(e, e.AmountCents), 5 => LedgerStore.Money(e.BalanceCents), _ => _store.StatusOf(e),
    };

    protected override void OnResize(EventArgs e) { base.OnResize(e); Rebuild(); }

    protected override void OnPaint(PaintEventArgs pe)
    {
        var g = pe.Graphics;
        g.Clear(Color.White);
        using var font = new Font("Segoe UI", 13f * _s, GraphicsUnit.Pixel);
        using var headerFont = new Font("Segoe UI Semibold", 13f * _s, GraphicsUnit.Pixel);
        using var headerBg = new SolidBrush(Color.FromArgb(225, 228, 232));
        using var sep = new Pen(Color.FromArgb(150, 156, 166));
        using var gridLine = new Pen(Color.FromArgb(214, 219, 226));
        using var alt = new SolidBrush(Color.FromArgb(247, 248, 250));
        using var selBg = new SolidBrush(Color.FromArgb(0, 84, 166));
        int pad = (int)(6 * _s);

        g.FillRectangle(headerBg, 0, 0, Width, HeaderH);
        int x = 0;
        for (int c = 0; c < Columns.Length; c++)
        {
            var r = new Rectangle(x + pad, 0, _widths[c] - 2 * pad, HeaderH);
            TextRenderer.DrawText(g, Columns[c], headerFont, r, Color.FromArgb(30, 34, 40), TextFormatFlags.VerticalCenter | (RightAligned[c] ? TextFormatFlags.Right : TextFormatFlags.Left));
            x += _widths[c];
            g.DrawLine(sep, x - 1, 0, x - 1, HeaderH - 1);
        }
        g.DrawLine(sep, 0, HeaderH - 1, Width, HeaderH - 1);

        for (int i = 0; i < VisibleRows && _top + i < _view.Count; i++)
        {
            var e = _view[_top + i];
            int y = HeaderH + i * RowH;
            bool sel = ReferenceEquals(e, Selected);
            if (sel) g.FillRectangle(selBg, 0, y, x, RowH);
            else if ((_top + i) % 2 == 1) g.FillRectangle(alt, 0, y, x, RowH);
            int cx = 0;
            for (int c = 0; c < Columns.Length; c++)
            {
                var r = new Rectangle(cx + pad, y, _widths[c] - 2 * pad, RowH);
                TextRenderer.DrawText(g, Cell(e, c), font, r, sel ? Color.White : Color.FromArgb(24, 26, 30),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | (RightAligned[c] ? TextFormatFlags.Right : TextFormatFlags.Left));
                cx += _widths[c];
            }
            g.DrawLine(gridLine, 0, y + RowH - 1, x - 1, y + RowH - 1);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Y >= HeaderH)
        {
            int i = _top + (e.Y - HeaderH) / RowH;
            int totalW = _widths.Sum();
            if (i >= 0 && i < _view.Count && (e.Y - HeaderH) / RowH < VisibleRows && e.X < totalW) Select(_view[i], "mouse");
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        FirstRow = _top - e.Delta / 120 * 3;
        base.OnMouseWheel(e);
    }

    protected override bool IsInputKey(Keys k) => k is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(k);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int cur = Selected == null ? -1 : _view.IndexOf(Selected);
        int next = e.KeyCode switch
        {
            Keys.Up => cur - 1, Keys.Down => cur + 1, Keys.PageUp => cur - VisibleRows, Keys.PageDown => cur + VisibleRows,
            Keys.Home => 0, Keys.End => _view.Count - 1, _ => int.MinValue,
        };
        if (next != int.MinValue && _view.Count > 0)
        {
            next = Math.Clamp(next, 0, _view.Count - 1);
            Select(_view[next], "keyboard");
            if (next < _top) FirstRow = next; else if (next >= _top + VisibleRows) FirstRow = next - VisibleRows + 1;
        }
        base.OnKeyDown(e);
    }

    void Select(Entry e, string source)
    {
        Selected = e;
        Invalidate();
        SelectionChanged?.Invoke(e, source);
    }
}
