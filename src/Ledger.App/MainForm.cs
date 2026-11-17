using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Ledger.App;

/// <summary>
/// Three surfaces: a toolbar painted in OnPaint, a grid painted in OnPaint with a real VScrollBar child,
/// and a modal payment dialog made of standard WinForms controls. A standard search box sits above the grid.
/// </summary>
public sealed class MainForm : Form
{
    readonly LedgerStore _store;
    readonly AppOptions _opt;
    readonly PaintedToolbar _toolbar;
    readonly LedgerGrid _grid;
    readonly VScrollBar _scroll;
    readonly DetailPanel _detail;
    readonly TextBox _search;

    public MainForm(LedgerStore store, AppOptions opt)
    {
        _store = store; _opt = opt;
        AutoScaleMode = AutoScaleMode.None;
        Text = opt.Title;
        Name = "ledgerMain";
        StartPosition = FormStartPosition.Manual;
        float s = DeviceDpi / 96f * opt.RenderScale;
        var rng = new Random(opt.Seed);
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Left + 6 + rng.Next(40), wa.Top + 4 + rng.Next(24));
        ClientSize = new Size(Math.Min((int)(930 * s), wa.Width - 70), Math.Min((int)(680 * s), wa.Height - 60));
        KeyPreview = true;

        _toolbar = new PaintedToolbar(s) { Name = "toolbar", Dock = DockStyle.Top, Height = (int)(34 * s) };
        _toolbar.Command += OnCommand;

        var searchPanel = new Panel { Name = "searchPanel", Dock = DockStyle.Top, Height = (int)(34 * s), Padding = new Padding((int)(8 * s), (int)(6 * s), 0, 0) };
        var label = new Label { Text = "Search", AutoSize = true, Location = new Point((int)(8 * s), (int)(9 * s)), Font = new Font("Segoe UI", 13f * s, GraphicsUnit.Pixel) };
        _search = new TextBox { Name = "searchBox", AccessibleName = "Search ledger", Location = new Point((int)(70 * s), (int)(5 * s)), Width = (int)(260 * s), Font = new Font("Segoe UI", 13f * s, GraphicsUnit.Pixel) };
        _search.TextChanged += (_, _) => { _grid!.Filter = _search.Text; _store.Journal("search_changed", new { text = _search.Text }); SyncScroll(); };
        searchPanel.Controls.Add(label);
        searchPanel.Controls.Add(_search);

        _detail = new DetailPanel(s, store) { Name = "detailPanel", Dock = DockStyle.Bottom, Height = (int)(46 * s) };

        var host = new Panel { Name = "gridHost", Dock = DockStyle.Fill };
        _grid = new LedgerGrid(store, s, opt.Seed) { Name = "ledgerGrid", Dock = DockStyle.Fill };
        _scroll = new VScrollBar { Name = "gridScroll", Dock = DockStyle.Right, Width = (int)(18 * s) };
        _scroll.ValueChanged += (_, _) => { _grid.FirstRow = _scroll.Value; };
        _grid.ViewChanged += SyncScroll;
        _grid.SelectionChanged += (e, source) =>
        {
            _detail.Show(e);
            _store.Journal("selection_changed", new { tx = e?.Tx, source });
        };
        host.Controls.Add(_grid);
        host.Controls.Add(_scroll);

        Controls.Add(host);
        Controls.Add(_detail);
        Controls.Add(searchPanel);
        Controls.Add(_toolbar);
        Shown += (_, _) => { Activate(); _grid.Focus(); SyncScroll(); };
    }

    void SyncScroll()
    {
        int rows = _grid.ViewCount, vis = Math.Max(1, _grid.VisibleRows);
        _scroll.Minimum = 0;
        _scroll.LargeChange = vis;
        _scroll.SmallChange = 1;
        _scroll.Maximum = Math.Max(0, rows - 1);
        _scroll.Enabled = rows > vis;
        int v = Math.Min(_grid.FirstRow, Math.Max(0, rows - vis));
        if (_scroll.Value != v) _scroll.Value = v;
    }

    void OnCommand(string cmd)
    {
        switch (cmd)
        {
            case "new": OpenPayment(); break;
            case "date": _grid.SortBy("date"); _store.Journal("sort_changed", new { by = "date" }); break;
            case "amount": _grid.SortBy("amount"); _store.Journal("sort_changed", new { by = "amount" }); break;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.P)) { OpenPayment(); return true; }
        if (keyData == Keys.F6) { OnCommand(_grid.SortMode == "date" ? "amount" : "date"); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void OpenPayment()
    {
        var sel = _grid.Selected;
        if (sel == null || sel.Type != "Invoice" || _store.StatusOf(sel) != "Open") { System.Media.SystemSounds.Beep.Play(); _store.Journal("dialog_refused", new { tx = sel?.Tx }); return; }
        _store.Journal("dialog_opened", new { tx = sel.Tx, invoice_ref = sel.Ref });
        using var dlg = new PaymentDialog(sel, _store, _opt);
        var r = dlg.ShowDialog(this);
        if (r != DialogResult.OK) _store.Journal("dialog_cancelled", new { tx = sel.Tx });
        _grid.Rebuild();
        _detail.Show(_grid.Selected);
    }
}

public sealed class PaintedToolbar : Control
{
    readonly float _s;
    readonly (string Label, string Cmd)[] _buttons = { ("New payment  Ctrl+P", "new"), ("Sort: date", "date"), ("Sort: amount", "amount") };
    public event Action<string>? Command;

    public PaintedToolbar(float s)
    {
        _s = s;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.FromArgb(244, 241, 234);
    }

    Rectangle ButtonRect(int i)
    {
        int x = (int)(8 * _s);
        for (int k = 0; k < i; k++) x += (int)(MeasureW(_buttons[k].Label) + 30 * _s);
        return new Rectangle(x, (int)(5 * _s), (int)(MeasureW(_buttons[i].Label) + 22 * _s), Height - (int)(10 * _s));
    }

    float MeasureW(string t)
    {
        using var f = new Font("Segoe UI", 13f * _s, GraphicsUnit.Pixel);
        return TextRenderer.MeasureText(t, f).Width;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using var f = new Font("Segoe UI", 13f * _s, GraphicsUnit.Pixel);
        using var border = new Pen(Color.FromArgb(190, 182, 166));
        for (int i = 0; i < _buttons.Length; i++)
        {
            var r = ButtonRect(i);
            using var b = new SolidBrush(Color.FromArgb(252, 250, 245));
            g.FillRectangle(b, r);
            g.DrawRectangle(border, r);
            TextRenderer.DrawText(g, _buttons[i].Label, f, r, Color.FromArgb(40, 36, 30), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        g.DrawLine(border, 0, Height - 1, Width, Height - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        for (int i = 0; i < _buttons.Length; i++) if (ButtonRect(i).Contains(e.Location)) Command?.Invoke(_buttons[i].Cmd);
        base.OnMouseDown(e);
    }
}

public sealed class DetailPanel : Control
{
    readonly float _s;
    readonly LedgerStore _store;
    Entry? _e;
    public DetailPanel(float s, LedgerStore store)
    {
        _s = s; _store = store;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.FromArgb(250, 248, 243);
    }
    public void Show(Entry? e) { _e = e; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using var line = new Pen(Color.FromArgb(190, 182, 166));
        g.DrawLine(line, 0, 0, Width, 0);
        using var f = new Font("Segoe UI", 15f * _s, GraphicsUnit.Pixel);
        if (_e == null) { TextRenderer.DrawText(g, "No row selected", f, new Point((int)(12 * _s), (int)(12 * _s)), Color.FromArgb(120, 112, 100)); return; }
        var parts = new List<string> { _e.Tx, _e.Ref, _e.Account, LedgerGrid.Display(_e, _e.AmountCents) + " USD", _store.StatusOf(_e) };
        if (_store.PaidByOf(_e) is { } by) parts.Add("paid by " + by);
        int x = (int)(12 * _s);
        foreach (var p in parts)
        {
            TextRenderer.DrawText(g, p, f, new Point(x, (int)(12 * _s)), Color.FromArgb(30, 28, 24));
            x += TextRenderer.MeasureText(p, f).Width + (int)(26 * _s);
        }
    }
}

public sealed class ParasiteForm : Form
{
    public ParasiteForm(Rectangle r)
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = r;
        TopMost = true;
        ShowInTaskbar = false;
        Text = "Reminder";
        BackColor = Color.FromArgb(255, 236, 179);
        Controls.Add(new Label { Text = "Reminder: nightly backup finished", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 14f, GraphicsUnit.Pixel) });
    }
    protected override bool ShowWithoutActivation => true;
}
