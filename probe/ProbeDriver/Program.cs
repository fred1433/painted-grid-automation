// Probe driver: proves, on the real desktop of the runner, that a separate process can
// 1) capture the screen with CopyFromScreen, 2) click with SendInput, 3) see the change
// on a second capture, 4) run OCR on that capture. Nothing here calls into the app.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

static class Program
{
    static readonly Dictionary<string, object?> Result = new();
    static readonly List<string> Log = new();

    static void Note(string s) { Console.WriteLine(s); Log.Add(s); }

    static async Task<int> Main(string[] args)
    {
        string appExe = args[0];
        string outDir = Directory.CreateDirectory(args[1]).FullName;
        string tessdata = args.Length > 2 ? args[2] : "";

        Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
        Result["os"] = RuntimeInformation.OSDescription;
        Result["dotnet_runtime"] = RuntimeInformation.FrameworkDescription;
        Result["runner_image_os"] = Environment.GetEnvironmentVariable("ImageOS");
        Result["runner_image_version"] = Environment.GetEnvironmentVariable("ImageVersion");
        Result["tesseract_package"] = typeof(Tesseract.TesseractEngine).Assembly.GetName().Version?.ToString();
        Result["virtual_screen"] = $"{Native.GetSystemMetrics(78)}x{Native.GetSystemMetrics(79)}";
        Result["interactive_session"] = Environment.UserInteractive;
        Note($"OS {Result["os"]} | {Result["dotnet_runtime"]} | image {Result["runner_image_os"]} {Result["runner_image_version"]} | screen {Result["virtual_screen"]}");

        var p = Process.Start(new ProcessStartInfo(appExe) { UseShellExecute = false })!;
        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 100 && hwnd == IntPtr.Zero; i++) { Thread.Sleep(100); p.Refresh(); hwnd = p.MainWindowHandle; }
        if (hwnd == IntPtr.Zero) return Fail(outDir, "app window never appeared");
        Thread.Sleep(800);
        Native.ShowWindow(hwnd, 9);
        Native.SetForegroundWindow(hwnd);
        Thread.Sleep(300);
        Result["foreground_is_app"] = Native.GetForegroundWindow() == hwnd;
        Result["dpi_for_window"] = Native.GetDpiForWindow(hwnd);

        Native.GetWindowRect(hwnd, out var wr);
        var clientOrigin = new Native.POINT { X = 0, Y = 0 };
        Native.ClientToScreen(hwnd, ref clientOrigin);
        var winRect = Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom);
        Note($"window {winRect} client origin {clientOrigin.X},{clientOrigin.Y} dpi {Result["dpi_for_window"]} foreground {Result["foreground_is_app"]}");

        string before = Path.Combine(outDir, "01-before.png");
        using (var bmp = Capture(winRect)) bmp.Save(before, ImageFormat.Png);

        // Target row 3 (0-based) of the painted control. Geometry assumed only for the probe;
        // the real driver locates rows from the image.
        double scale = Native.GetDpiForWindow(hwnd) / 96.0;
        int targetRow = 3;
        int cx = clientOrigin.X + (int)(200 * scale);
        int cy = clientOrigin.Y + (int)((28 + targetRow * 26 + 13) * scale);
        var rowBand = new Rectangle(clientOrigin.X + 2, clientOrigin.Y + (int)((28 + targetRow * 26 + 3) * scale), (int)(540 * scale), (int)(20 * scale));

        double darkBefore = DarkShare(rowBand);
        Native.Click(cx, cy);
        Thread.Sleep(500);
        double darkAfter = DarkShare(rowBand);
        string after = Path.Combine(outDir, "02-after-sendinput-click.png");
        using (var bmp = Capture(winRect)) bmp.Save(after, ImageFormat.Png);
        string rowPng = Path.Combine(outDir, "03-selected-row.png");
        using (var bmp = Capture(rowBand)) bmp.Save(rowPng, ImageFormat.Png);

        bool clickSeen = darkBefore < 0.05 && darkAfter > 0.5;
        Result["click_point"] = $"{cx},{cy}";
        Result["selection_band_blue_share_before"] = Math.Round(darkBefore, 3);
        Result["selection_band_blue_share_after"] = Math.Round(darkAfter, 3);
        Result["real_click_changed_selection"] = clickSeen;
        Note($"blue share in target row: before {darkBefore:F3} after {darkAfter:F3} -> click seen {clickSeen}");

        // OCR 1: Windows.Media.Ocr from this unpackaged exe
        try
        {
            var langs = Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray();
            Result["winrt_ocr_languages"] = langs;
            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                         ?? (langs.Length > 0 ? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(langs[0])) : null);
            if (engine == null) { Result["winrt_ocr"] = "NOT_AVAILABLE: no recognizer language installed"; }
            else
            {
                string text = await WinRtOcr(engine, after);
                Result["winrt_ocr"] = text;
            }
        }
        catch (Exception ex) { Result["winrt_ocr"] = "ERROR: " + ex.GetType().Name + ": " + ex.Message; }
        Note($"Windows.Media.Ocr: {Result["winrt_ocr"]}");

        // OCR 2: local Tesseract, pinned package and traineddata
        if (tessdata != "")
        {
            try
            {
                using var eng = new Tesseract.TesseractEngine(tessdata, "eng", Tesseract.EngineMode.LstmOnly);
                using var big = Upscale(rowPng, 3, invert: true);
                string scaled = Path.Combine(outDir, "04-selected-row-x3-inverted.png");
                big.Save(scaled, ImageFormat.Png);
                using var pix = Tesseract.Pix.LoadFromFile(scaled);
                using var page = eng.Process(pix, Tesseract.PageSegMode.SingleLine);
                Result["tesseract_version"] = eng.Version;
                Result["tesseract_selected_row"] = page.GetText().Trim();
            }
            catch (Exception ex) { Result["tesseract_selected_row"] = "ERROR: " + ex.GetType().Name + ": " + ex.Message; }
            Note($"Tesseract on selected row: {Result["tesseract_selected_row"]}");
        }

        string ocrAll = $"{Result.GetValueOrDefault("winrt_ocr")} {Result.GetValueOrDefault("tesseract_selected_row")}";
        bool ocrOk = ocrAll.Contains("INV-1043") || ocrAll.Contains("3,300.10");
        Result["ocr_read_selected_row"] = ocrOk;

        try { p.Kill(); } catch { }
        File.WriteAllText(Path.Combine(outDir, "probe-result.json"), JsonSerializer.Serialize(Result, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllLines(Path.Combine(outDir, "probe-log.txt"), Log);
        if (!clickSeen) { Note("PROBE FAILED: real click not observed on screen"); return 2; }
        if (!ocrOk) { Note("PROBE FAILED: OCR could not read the selected row"); return 3; }
        Note("PROBE PASSED: capture, SendInput click, second capture and OCR all work on this desktop");
        return 0;
    }

    static int Fail(string outDir, string why)
    {
        Result["failure"] = why;
        File.WriteAllText(Path.Combine(outDir, "probe-result.json"), JsonSerializer.Serialize(Result, new JsonSerializerOptions { WriteIndented = true }));
        Note("PROBE FAILED: " + why);
        return 1;
    }

    static Bitmap Capture(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        return bmp;
    }

    static double DarkShare(Rectangle band)
    {
        using var bmp = Capture(band);
        int hit = 0, total = 0;
        for (int y = 0; y < bmp.Height; y += 2)
            for (int x = 0; x < bmp.Width; x += 3)
            {
                var c = bmp.GetPixel(x, y); total++;
                if (c.B > 120 && c.R < 60 && c.G < 120) hit++;
            }
        return total == 0 ? 0 : (double)hit / total;
    }

    static Bitmap Upscale(string path, int k, bool invert)
    {
        using var src = new Bitmap(path);
        var dst = new Bitmap(src.Width * k + 40, src.Height * k + 40, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.Clear(Color.White);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, new Rectangle(20, 20, src.Width * k, src.Height * k));
        }
        if (invert)
            for (int y = 20; y < dst.Height - 20; y++)
                for (int x = 20; x < dst.Width - 20; x++)
                {
                    var c = dst.GetPixel(x, y);
                    int lum = (c.R * 3 + c.G * 6 + c.B) / 10;
                    dst.SetPixel(x, y, lum > 150 ? Color.Black : Color.White);
                }
        return dst;
    }

    static async Task<string> WinRtOcr(Windows.Media.Ocr.OcrEngine engine, string png)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(png));
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var sb = await decoder.GetSoftwareBitmapAsync();
        var res = await engine.RecognizeAsync(sb);
        return string.Join(" | ", res.Lines.Select(l => l.Text));
    }
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    public static void Click(int x, int y)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        int nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        int ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        const uint MOVE = 0x0001, ABS = 0x8000, VDESK = 0x4000, DOWN = 0x0002, UP = 0x0004;
        var inputs = new[]
        {
            new INPUT { type = 0, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOVE | ABS | VDESK } },
            new INPUT { type = 0, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = DOWN | ABS | VDESK } },
            new INPUT { type = 0, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = UP | ABS | VDESK } },
        };
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length) throw new InvalidOperationException($"SendInput sent {sent}/3, error {Marshal.GetLastWin32Error()}");
    }
}
