using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace GridDriver;

/// <summary>Step journal (JSONL file plus stderr, never stdout) and real screen captures.</summary>
public sealed class Evidence
{
    public string Dir { get; }
    readonly string _log;
    int _seq;
    static readonly object Gate = new();

    public Evidence(string dir)
    {
        Dir = Directory.CreateDirectory(dir).FullName;
        _log = Path.Combine(Dir, "steps.jsonl");
    }

    public void Log(string kind, object data)
    {
        string line = JsonSerializer.Serialize(new { at = DateTime.UtcNow.ToString("O"), pid = Environment.ProcessId, kind, data });
        lock (Gate)
        {
            File.AppendAllText(_log, line + "\n");
            Console.Error.WriteLine(line);
        }
    }

    public string Shot(string label, Bitmap bmp)
    {
        int n = Interlocked.Increment(ref _seq);
        string name = $"{Environment.ProcessId}-{n:000}-{label}.png";
        lock (Gate) bmp.Save(Path.Combine(Dir, name), ImageFormat.Png);
        return name;
    }

    public static Bitmap Capture(Rectangle r)
    {
        var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        return bmp;
    }
}
