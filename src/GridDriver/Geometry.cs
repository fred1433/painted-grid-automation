using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace GridDriver;

/// <summary>Fast pixel access on a captured bitmap.</summary>
public sealed class Pixels
{
    public readonly int W, H;
    readonly byte[] _d; readonly int _stride;
    public Pixels(Bitmap b)
    {
        W = b.Width; H = b.Height;
        var bd = b.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        _stride = bd.Stride; _d = new byte[_stride * H];
        Marshal.Copy(bd.Scan0, _d, 0, _d.Length);
        b.UnlockBits(bd);
    }
    public (int R, int G, int B) At(int x, int y) { int i = y * _stride + x * 3; return (_d[i + 2], _d[i + 1], _d[i]); }
    public int Lum(int x, int y) { var (r, g, b) = At(x, y); return (r * 3 + g * 6 + b) / 10; }
}

/// <summary>
/// Visual profile of this grid, calibrated once per application: the colour of the header separators and of the row
/// gridlines, and the darkness of a selected row. These are adapter assumptions, written down here and in the README.
/// </summary>
public sealed class GridProfile
{
    public (int R, int G, int B) Separator = (150, 156, 166);
    public (int R, int G, int B) GridLine = (214, 219, 226);
    public int Tolerance = 9;
    public int SelectedMaxLuminance = 110;
    public string[] Columns = { "Date", "Ref #", "Account", "Type", "Amount", "Balance", "Status" };
    /// <summary>Text rendering of the cells at 96 dpi and scale 1, used to re-draw a value and compare it with the pixels.</summary>
    public string FontFamily = "Segoe UI";
    public float FontPx = 13f;
    public int CellPadPx = 6;
    public int RowPx = 24;
}

public sealed class GridGeometry
{
    public Rectangle Screen { get; init; }
    public int HeaderBottom { get; init; }
    public List<(int X0, int X1)> Spans { get; init; } = new();
    public Dictionary<string, int> ColumnIndex { get; init; } = new();
    public List<(int Y0, int Y1)> Rows { get; init; } = new();
    public int? SelectedRow { get; init; }
    public int RowHeight { get; init; }

    public Rectangle Cell(int row, int col) => new(Spans[col].X0 + 2, Rows[row].Y0 + 1, Spans[col].X1 - Spans[col].X0 - 4, Rows[row].Y1 - Rows[row].Y0 - 1);
    public Point ScreenCenter(int row, int col) { var c = Cell(row, col); return new Point(Screen.X + c.X + c.Width / 2, Screen.Y + c.Y + c.Height / 2); }
    public int Col(string name) => ColumnIndex.TryGetValue(name, out int i) ? i : throw new StopException(Codes.GeometryUnconfirmed, $"column '{name}' was not recognised in the header");

    static bool Near((int R, int G, int B) a, (int R, int G, int B) b, int tol) => Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

    public static GridGeometry Detect(Bitmap img, Rectangle screen, GridProfile prof, WinRtOcr ocr, Decision d, Func<Bitmap, string>? fallback = null)
    {
        d.Via("image: header template and gridlines");
        var px = new Pixels(img);
        int headerBottom = -1;
        for (int y = 4; y < Math.Min(px.H, 140) && headerBottom < 0; y++)
        {
            int n = 0;
            for (int x = 0; x < px.W; x++) if (Near(px.At(x, y), prof.Separator, prof.Tolerance)) n++;
            if (n >= px.W * 0.6) headerBottom = y;
        }
        if (headerBottom < 0) throw new StopException(Codes.GeometryUnconfirmed, "header bottom line not found in the capture", d);

        var seps = new List<int>();
        int bandTop = 2, bandBottom = headerBottom - 2, need = (int)((bandBottom - bandTop + 1) * 0.8);
        for (int x = 0; x < px.W; x++)
        {
            int n = 0;
            for (int y = bandTop; y <= bandBottom; y++) if (Near(px.At(x, y), prof.Separator, prof.Tolerance)) n++;
            if (n >= need && (seps.Count == 0 || x - seps[^1] > 3)) seps.Add(x);
        }
        if (seps.Count < 3) throw new StopException(Codes.GeometryUnconfirmed, $"only {seps.Count} column separators found in the header", d);
        var spans = new List<(int, int)>();
        int x0 = 0;
        foreach (var s in seps) { spans.Add((x0, s)); x0 = s + 1; }
        int right = seps[^1];

        var lines = new List<int>();
        for (int y = headerBottom + 1; y < px.H; y++)
        {
            int n = 0;
            for (int x = 0; x < right; x += 2) if (Near(px.At(x, y), prof.GridLine, prof.Tolerance)) n++;
            if (n >= right / 2 * 0.7) lines.Add(y);
        }
        var rows = new List<(int, int)>();
        int start = headerBottom + 1;
        foreach (var y in lines)
        {
            if (y - start >= 8) rows.Add((start, y - 1));
            start = y + 1;
        }
        if (rows.Count == 0) throw new StopException(Codes.GeometryUnconfirmed, "no row gridlines found below the header", d);
        int median = rows.Select(r => r.Item2 - r.Item1 + 1).OrderBy(h => h).ElementAt(rows.Count / 2);
        rows = rows.Where(r => r.Item2 - r.Item1 + 1 >= median * 0.75).ToList();

        int? selected = null;
        for (int i = 0; i < rows.Count; i++)
        {
            long sum = 0; int n = 0;
            for (int y = rows[i].Item1; y <= rows[i].Item2; y += 2) for (int x = 0; x < right; x += 4) { sum += px.Lum(x, y); n++; }
            if (n > 0 && sum / n < prof.SelectedMaxLuminance) selected = i;
        }

        // Name the columns by reading the header (template = the expected header labels).
        var colIndex = new Dictionary<string, int>();
        const int k = 3, m = 12;
        using (var head = Ocr.Prepare(img, new Rectangle(0, 0, right, headerBottom), k, m, out _))
        {
            var words = ocr.Words(head);
            var perSpan = new string[spans.Count];
            foreach (var w in words.OrderBy(w => w.Box.X))
            {
                float cx = (w.Box.X + w.Box.Width / 2 - m) / k;
                int si = spans.FindIndex(s => cx >= s.Item1 && cx <= s.Item2);
                if (si >= 0) perSpan[si] = ((perSpan[si] ?? "") + " " + w.Text).Trim();
            }
            for (int i = 0; i < spans.Count && fallback != null; i++)
            {
                if (prof.Columns.Any(c => Ocr.Fold(c) == Ocr.Fold(perSpan[i] ?? ""))) continue;
                // A header label the first reader missed is read on its own by the second reader (naming only, not a value).
                using var one = Ocr.Prepare(img, new Rectangle(spans[i].Item1, 0, spans[i].Item2 - spans[i].Item1, headerBottom), 3, 12, out _);
                var t = fallback(one);
                if (prof.Columns.Any(c => Ocr.Fold(c) == Ocr.Fold(t))) { d.Obs($"header column {i} named '{t}' by the second reader"); perSpan[i] = t; }
            }
            for (int i = 0; i < spans.Count; i++)
            {
                var match = prof.Columns.FirstOrDefault(c => Ocr.Fold(c) == Ocr.Fold(perSpan[i] ?? ""));
                if (match != null) colIndex[match] = i;
            }
            d.Obs($"header read: {string.Join(" | ", perSpan.Select(p => p ?? "?"))}");
        }
        d.Obs($"geometry: {spans.Count} columns, {rows.Count} full rows of {median}px, header bottom at {headerBottom}px, selected row {(selected?.ToString() ?? "none")}");
        return new GridGeometry { Screen = screen, HeaderBottom = headerBottom, Spans = spans, ColumnIndex = colIndex, Rows = rows, SelectedRow = selected, RowHeight = median };
    }

    /// <summary>Scan pass: one OCR call over the visible rows, words assigned to cells by position. Used only to shortlist.</summary>
    public List<string[]> Scan(Bitmap img, WinRtOcr ocr) => Enumerable.Range(0, Rows.Count).Select(i => ReadRow(img, ocr, i, 2)).ToList();

    public Rectangle FullCell(int row, int col) => new(Spans[col].X0, Rows[row].Y0, Spans[col].X1 - Spans[col].X0, Rows[row].Y1 - Rows[row].Y0 + 1);
    public double Scale(GridProfile p) => (RowHeight + 1) / (double)p.RowPx;

    /// <summary>One OCR call over a whole row (WinRT reads lines far better than isolated cells); words go to cells by position.</summary>
    public string[] ReadRow(Bitmap img, WinRtOcr ocr, int i, int k)
    {
        var cells = new string[Spans.Count];
        const int m = 10;
        {
            // Rows are prepared one by one so a selected (dark) row is inverted on its own.
            using var prep = Ocr.Prepare(img, new Rectangle(0, Rows[i].Y0, Spans[^1].X1, Rows[i].Y1 - Rows[i].Y0 + 1), k, m, out _);
            foreach (var w in ocr.Words(prep).OrderBy(w => w.Box.X))
            {
                float cx = (w.Box.X + w.Box.Width / 2 - m) / k;
                int si = Spans.FindIndex(s => cx >= s.X0 && cx <= s.X1);
                if (si >= 0) cells[si] = ((cells[si] ?? "") + " " + w.Text).Trim();
            }
        }
        return cells;
    }
}
