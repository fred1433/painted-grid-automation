using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
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

/// <summary>Test-only: simulates a reader that drops a digit group ("1,000.00" read as "100.00"). Logged when active.</summary>
public sealed class DropGroupFault : ILineReader
{
    readonly ILineReader _inner;
    public DropGroupFault(ILineReader inner) { _inner = inner; }
    public string Name => _inner.Name + " [fault injected: 1,000.00 read as 100.00]";
    public string ReadLine(Bitmap b) => _inner.ReadLine(b).Replace("1,000.00", "100.00");
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

    /// <summary>A4: crop, invert a dark (selected) background, add a white margin, upscale.</summary>
    public static Bitmap Prepare(Bitmap src, Rectangle r, int k, int margin, out bool inverted)
    {
        r.Intersect(new Rectangle(0, 0, src.Width, src.Height));
        using var crop = src.Clone(r, PixelFormat.Format24bppRgb);
        long sum = 0; int n = 0;
        for (int y = 0; y < crop.Height; y += 2) for (int x = 0; x < crop.Width; x += 2) { var c = crop.GetPixel(x, y); sum += (c.R * 3 + c.G * 6 + c.B) / 10; n++; }
        inverted = n > 0 && sum / n < 128;
        var dst = new Bitmap(crop.Width * k + 2 * margin, crop.Height * k + 2 * margin, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            if (inverted)
            {
                var cm = new ColorMatrix(new[] { new float[] { -1, 0, 0, 0, 0 }, new float[] { 0, -1, 0, 0, 0 }, new float[] { 0, 0, -1, 0, 0 }, new float[] { 0, 0, 0, 1, 0 }, new float[] { 1, 1, 1, 0, 1 } });
                using var ia = new ImageAttributes();
                ia.SetColorMatrix(cm);
                g.DrawImage(crop, new Rectangle(margin, margin, crop.Width * k, crop.Height * k), 0, 0, crop.Width, crop.Height, GraphicsUnit.Pixel, ia);
            }
            else g.DrawImage(crop, new Rectangle(margin, margin, crop.Width * k, crop.Height * k));
        }
        return dst;
    }
}
