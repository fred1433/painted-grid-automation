// Probe app: one window, one control that paints its own rows. No child controls.
using System;
using System.Drawing;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.Run(new ProbeForm());
    }
}

sealed class ProbeForm : Form
{
    public ProbeForm()
    {
        Text = "Probe Ledger";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(40, 40);
        ClientSize = new Size(560, 300);
        var grid = new PaintedRows { Dock = DockStyle.Fill };
        Controls.Add(grid);
        Shown += (_, _) => { Activate(); BringToFront(); };
    }
}

sealed class PaintedRows : Control
{
    public const int HeaderH = 28, RowH = 26;
    static readonly string[] Rows =
    {
        "2026-03-02  INV-1040  Northwind Supply   418.20",
        "2026-03-03  INV-1041  Harbor Paper Co    1,250.00",
        "2026-03-04  INV-1042  Ridge Freight      77.95",
        "2026-03-05  INV-1043  Alder Office       3,300.10",
        "2026-03-06  INV-1044  Quarry Tools       905.00",
    };
    int _selected = -1;

    public PaintedRows()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        BackColor = Color.White;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using var font = new Font("Segoe UI", 10f);
        using var header = new SolidBrush(Color.FromArgb(225, 228, 232));
        g.FillRectangle(header, 0, 0, Width, HeaderH);
        TextRenderer.DrawText(g, "Date        Ref       Account            Amount", font, new Point(8, 5), Color.Black);
        for (int i = 0; i < Rows.Length; i++)
        {
            var r = new Rectangle(0, HeaderH + i * RowH, Width, RowH);
            bool sel = i == _selected;
            using var b = new SolidBrush(sel ? Color.FromArgb(0, 84, 166) : (i % 2 == 0 ? Color.White : Color.FromArgb(246, 247, 249)));
            g.FillRectangle(b, r);
            TextRenderer.DrawText(g, Rows[i], font, new Point(8, r.Y + 4), sel ? Color.White : Color.Black);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int idx = (e.Y - HeaderH) / RowH;
        if (e.Y >= HeaderH && idx >= 0 && idx < Rows.Length) { _selected = idx; Invalidate(); }
        base.OnMouseDown(e);
    }
}
