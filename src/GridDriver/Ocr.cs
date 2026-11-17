using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace GridDriver;

public sealed record OcrWord(string Text, RectangleF Box);

public interface ILineReader { string Name { get; } string ReadLine(Bitmap prepared); }

/// <summary>Windows built-in OCR (Windows.Media.Ocr), called from this unpackaged executable.</summary>
public sealed class WinRtOcr : ILineReader
{
    readonly OcrEngine _engine;
    public string Name => "Windows.Media.Ocr " + _engine.RecognizerLanguage.LanguageTag;
    WinRtOcr(OcrEngine e) { _engine = e; }

    public static WinRtOcr? TryCreate(out string why)
    {
        try
        {
            var e = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
            why = e == null ? "no OCR recognizer language installed" : "ok";
            return e == null ? null : new WinRtOcr(e);
        }
        catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; return null; }
    }

    static async Task<SoftwareBitmap> ToSoftware(Bitmap b)
    {
        var ms = new MemoryStream();
        b.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var dec = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        return await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    public List<OcrWord> Words(Bitmap b) => Task.Run(async () =>
    {
        using var sb = await ToSoftware(b);
        var res = await _engine.RecognizeAsync(sb);
        return res.Lines.SelectMany(l => l.Words).Select(w => new OcrWord(w.Text, new RectangleF((float)w.BoundingRect.X, (float)w.BoundingRect.Y, (float)w.BoundingRect.Width, (float)w.BoundingRect.Height))).ToList();
    }).GetAwaiter().GetResult();

    public string ReadLine(Bitmap b) => Task.Run(async () =>
    {
        using var sb = await ToSoftware(b);
        var res = await _engine.RecognizeAsync(sb);
        return Ocr.Norm(string.Join(" ", res.Lines.Select(l => l.Text)));
    }).GetAwaiter().GetResult();
}

/// <summary>Local Tesseract, pinned NuGet package and pinned eng.traineddata (tessdata_fast 4.1.0).</summary>
public sealed class TesseractOcr : ILineReader, IDisposable
{
    readonly Tesseract.TesseractEngine _engine;
    readonly object _gate = new();
    public string Name => "Tesseract " + _engine.Version;
    public TesseractOcr(string tessdata) { _engine = new Tesseract.TesseractEngine(tessdata, "eng", Tesseract.EngineMode.LstmOnly); }

    public string ReadLine(Bitmap b)
    {
        using var ms = new MemoryStream();
        b.Save(ms, ImageFormat.Png);
        lock (_gate)
        {
            using var pix = Tesseract.Pix.LoadFromMemory(ms.ToArray());
            using var page = _engine.Process(pix, Tesseract.PageSegMode.SingleLine);
            return Ocr.Norm(page.GetText());
        }
    }
    public void Dispose() => _engine.Dispose();
}

public static class Ocr
{
    public static string Norm(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    public static readonly Regex StrictAmount = new(@"^-?\d{1,3}(,\d{3})*\.\d{2}$", RegexOptions.CultureInvariant);
    public static readonly Regex StrictDate = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant);
    public static readonly Regex StrictRef = new(@"^(INV|PMT)-\d{4}$", RegexOptions.CultureInvariant);

    /// <summary>Loose folding, used only to shortlist candidates during a scan. Never used to accept a value.</summary>
    public static string Fold(string s)
    {
        var t = new string((s ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return t.Replace('O', '0').Replace('I', '1').Replace('L', '1').Replace('S', '5');
    }

    static string Digits(string s) => s.Replace('O', '0').Replace('o', '0').Replace('I', '1').Replace('l', '1').Replace('|', '1');

    /// <summary>
    /// Format-constrained reading: where the field's format only allows digits, a reader's O, I or l is taken as 0 or 1.
    /// This is a hypothesis, never an acceptance: the value must then match the pixels when re-drawn (Glyphs.Verify).
    /// </summary>
    public static string ConstrainRef(string s)
    {
        var m = Regex.Match(s, @"^(INV|PMT|TX)-([0-9OoIl|]{4})$");
        return m.Success ? m.Groups[1].Value + "-" + Digits(m.Groups[2].Value) : s;
    }
    public static string ConstrainNumeric(string s) => Regex.IsMatch(s, @"^-?[0-9OoIl|][0-9OoIl|,.\-]*$") ? Digits(s) : s;
    public static string ConstrainLine(string s)
    {
        s = Regex.Replace(s, @"\b(TX|INV|PMT)-([0-9OoIl|]{4})\b", m => m.Groups[1].Value + "-" + Digits(m.Groups[2].Value));
        return Regex.Replace(s, @"(?<![A-Za-z])-?[0-9OoIl|][0-9OoIl|,]*\.[0-9OoIl|]{2}(?=\s?USD)", m => Digits(m.Value));
    }

    public static long? Cents(string amount)
    {
        if (!StrictAmount.IsMatch(amount)) return null;
        return (long)Math.Round(decimal.Parse(amount.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture) * 100m);
    }

    public static string Money(long cents)
    {
        var s = (Math.Abs(cents) / 100m).ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);
        return cents < 0 ? "-" + s : s;
    }

    /// <summary>A4: crop, grey, invert a dark (selected) background, stretch the contrast, add a white margin, upscale.</summary>
    public static Bitmap Prepare(Bitmap src, Rectangle r, int k, int margin, out bool inverted)
    {
        r.Intersect(new Rectangle(0, 0, src.Width, src.Height));
        using var crop = src.Clone(r, PixelFormat.Format24bppRgb);
        var px = new Pixels(crop);
        var lum = new int[px.W * px.H];
        long sum = 0;
        for (int y = 0; y < px.H; y++) for (int x = 0; x < px.W; x++) { int l = px.Lum(x, y); lum[y * px.W + x] = l; sum += l; }
        inverted = lum.Length > 0 && sum / lum.Length < 128;
        if (inverted) for (int i = 0; i < lum.Length; i++) lum[i] = 255 - lum[i];
        int lo = lum.Length == 0 ? 0 : lum.Min(), hi = lum.Length == 0 ? 255 : lum.Max();
        if (hi - lo < 1) hi = lo + 1;
        using var grey = new Bitmap(px.W, px.H, PixelFormat.Format24bppRgb);
        var bd = grey.LockBits(new Rectangle(0, 0, px.W, px.H), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        var buf = new byte[bd.Stride * px.H];
        for (int y = 0; y < px.H; y++)
            for (int x = 0; x < px.W; x++)
            {
                byte v = (byte)Math.Clamp((lum[y * px.W + x] - lo) * 255 / (hi - lo), 0, 255);
                int i = y * bd.Stride + x * 3; buf[i] = buf[i + 1] = buf[i + 2] = v;
            }
        System.Runtime.InteropServices.Marshal.Copy(buf, 0, bd.Scan0, buf.Length);
        grey.UnlockBits(bd);
        var dst = new Bitmap(px.W * k + 2 * margin, px.H * k + 2 * margin, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(grey, new Rectangle(margin, margin, px.W * k, px.H * k));
        }
        return dst;
    }
}

/// <summary>
/// Re-draws a value that both readers agreed on, in the grid's font, and compares it with the captured pixels.
/// Two OCR engines both read "l,2O0.00" (letter l, letter O) as "1,200.00" in the published run; this check is what refuses it.
/// The font, size and padding are adapter assumptions (GridProfile), not something the driver discovers.
/// </summary>
public static class Glyphs
{
    static bool[,] Ink(Bitmap b, out Rectangle box)
    {
        var px = new Pixels(b);
        // Background = the most common luminance in the cell.
        var hist = new int[256];
        for (int y = 0; y < px.H; y++) for (int x = 0; x < px.W; x++) hist[px.Lum(x, y)]++;
        int bg = Array.IndexOf(hist, hist.Max());
        var m = new bool[px.W, px.H];
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = 0; y < px.H; y++)
            for (int x = 0; x < px.W; x++)
                if (Math.Abs(px.Lum(x, y) - bg) > 70) { m[x, y] = true; x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        box = x1 < 0 ? Rectangle.Empty : Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
        return m;
    }

    /// <returns>null when the pixels match the re-drawn text, otherwise what differs. <paramref name="detail"/> always says how close.</returns>
    public static string? Verify(Bitmap img, Rectangle cell, string text, bool rightAligned, GridProfile prof, double scale) => Verify(img, cell, text, rightAligned, prof, scale, out _);

    public static string? Verify(Bitmap img, Rectangle cell, string text, bool rightAligned, GridProfile prof, double scale, out string detail)
    {
        cell.Intersect(new Rectangle(0, 0, img.Width, img.Height));
        using var cap = img.Clone(cell, PixelFormat.Format24bppRgb);
        var bgPx = new Pixels(cap);
        var hist = new Dictionary<(int, int, int), int>();
        for (int y = 0; y < bgPx.H; y++) for (int x = 0; x < bgPx.W; x++) { var c = bgPx.At(x, y); hist[c] = hist.GetValueOrDefault(c) + 1; }
        var bgc = hist.OrderByDescending(kv => kv.Value).First().Key;
        bool dark = (bgc.Item1 * 3 + bgc.Item2 * 6 + bgc.Item3) / 10 < 128;
        using var ren = new Bitmap(cap.Width, cap.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(ren))
        {
            var back = Color.FromArgb(bgc.Item1, bgc.Item2, bgc.Item3);
            g.Clear(back);
            using var f = new Font(prof.FontFamily, (float)(prof.FontPx * scale), GraphicsUnit.Pixel);
            int pad = (int)(prof.CellPadPx * scale);
            System.Windows.Forms.TextRenderer.DrawText(g, text, f, new Rectangle(pad, 0, cap.Width - 2 * pad, cap.Height), dark ? Color.White : Color.FromArgb(24, 26, 30), back,
                System.Windows.Forms.TextFormatFlags.VerticalCenter | System.Windows.Forms.TextFormatFlags.NoPrefix | (rightAligned ? System.Windows.Forms.TextFormatFlags.Right : System.Windows.Forms.TextFormatFlags.Left));
        }
        var a = Ink(cap, out var ba);
        var b = Ink(ren, out var bb);
        detail = "";
        if (ba.IsEmpty || bb.IsEmpty) return "no ink found in the cell";
        detail = $"ink box {ba.Width}x{ba.Height}px on screen, {bb.Width}x{bb.Height}px re-drawn";
        if (Math.Abs(ba.Width - bb.Width) > 1 || Math.Abs(ba.Height - bb.Height) > 1)
            return $"ink box {ba.Width}x{ba.Height}px on screen, {bb.Width}x{bb.Height}px when '{text}' is re-drawn";
        int differ = 0, inkPixels = 0;
        int w = Math.Max(ba.Width, bb.Width), h = Math.Max(ba.Height, bb.Height);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool pa = ba.X + x < a.GetLength(0) && ba.Y + y < a.GetLength(1) && a[ba.X + x, ba.Y + y];
                bool pb = bb.X + x < b.GetLength(0) && bb.Y + y < b.GetLength(1) && b[bb.X + x, bb.Y + y];
                if (pa || pb) inkPixels++;
                if (pa != pb) differ++;
            }
        detail = $"{differ} of {inkPixels} ink pixels differ";
        if (differ * 4 > inkPixels) return $"{differ} of {inkPixels} ink pixels differ from '{text}' re-drawn in {prof.FontFamily} {prof.FontPx * scale:0.#}px";
        return null;
    }
}
