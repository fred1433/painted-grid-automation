using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ledger.Verifier;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Scenarios;

public sealed class Paths
{
    public string App = "", Server = "", Operator = "", Tessdata = "", Out = "proof", Work = "work", Repo = ".", Launcher = "";
}

public sealed record CallResult(bool IsError, JsonElement Body, string Tool)
{
    public string Code => Body.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
    public JsonElement Result => Body.TryGetProperty("result", out var r) ? r : default;
    public string Str(params string[] path)
    {
        JsonElement e = Body;
        foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return ""; }
        return e.ValueKind == JsonValueKind.String ? e.GetString()! : e.ToString();
    }
}

/// <summary>One scenario: its own app data folder, operation database, server evidence and transcript.</summary>
public sealed class Ctx : IAsyncDisposable
{
    public readonly string Name, Dir, Data, Db, Evidence, Proof;
    public readonly Paths P;
    readonly List<Process> _procs = new();
    readonly List<McpClient> _clients = new();
    public readonly List<(bool Ok, string Text)> Checks = new();
    readonly StreamWriter _transcript;
    public static int FrameSeq;

    public Ctx(string name, Paths p)
    {
        Name = name; P = p;
        Dir = Directory.CreateDirectory(Path.Combine(p.Work, name)).FullName;
        Data = Path.Combine(Dir, "app-data");
        Db = Path.Combine(Dir, "operations.db");
        Evidence = Path.Combine(Dir, "server-evidence");
        Proof = Directory.CreateDirectory(Path.Combine(p.Out, "scenarios", name)).FullName;
        _transcript = new StreamWriter(Path.Combine(Proof, "mcp-transcript.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Note(string s) { Console.WriteLine($"  [{Name}] {s}"); _transcript.WriteLine(JsonSerializer.Serialize(new { at = DateTime.UtcNow.ToString("O"), note = s })); }

    public bool Check(bool ok, string text)
    {
        Checks.Add((ok, text));
        Console.WriteLine($"  [{Name}] {(ok ? "ok  " : "FAIL")} {text}");
        _transcript.WriteLine(JsonSerializer.Serialize(new { at = DateTime.UtcNow.ToString("O"), check = text, ok }));
        return ok;
    }

    public Process StartApp(int seed, string flags = "", string? exe = null, string? data = null, string extra = "")
    {
        var args = $"--data \"{data ?? Data}\" --seed {seed}" + (flags != "" ? $" --flags {flags}" : "") + (extra != "" ? " " + extra : "");
        var p = Process.Start(new ProcessStartInfo(exe ?? P.App, args) { UseShellExecute = false })!;
        _procs.Add(p);
        for (int i = 0; i < 100; i++) { Thread.Sleep(100); p.Refresh(); if (p.MainWindowHandle != IntPtr.Zero) break; }
        Thread.Sleep(900);
        Note($"started app pid {p.Id} seed {seed} flags '{flags}' {extra}");
        return p;
    }

    public void Track(Process p) => _procs.Add(p);

    /// <param name="integrity">null: same level as this process; "medium" or "low": started through run-at-integrity.</param>
    public async Task<McpClient> Server(Dictionary<string, string?>? env = null, string? integrity = null)
    {
        var e = env ?? new Dictionary<string, string?>();
        var args = new List<string> { "--allow-exe", P.App, "--db", Db, "--evidence", Evidence, "--tessdata", P.Tessdata };
        if (integrity != null) args.InsertRange(0, new[] { integrity, P.Server });
        var t = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ledger-grid",
            Command = integrity != null ? P.Launcher : P.Server,
            Arguments = args.ToArray(),
            EnvironmentVariables = e,
            WorkingDirectory = Dir,
        });
        var c = await McpClient.CreateAsync(t);
        _clients.Add(c);
        Note($"MCP server started{(e.Count > 0 ? " with " + string.Join(",", e.Select(kv => kv.Key + "=" + kv.Value)) : "")}{(integrity != null ? " at " + integrity + " integrity" : "")}");
        return c;
    }

    public async Task StopServer(McpClient c) { _clients.Remove(c); await c.DisposeAsync(); Note("MCP server stopped"); }

    public async Task<CallResult> Call(McpClient c, string tool, Dictionary<string, object?> args)
    {
        var sw = Stopwatch.StartNew();
        var res = await c.CallToolAsync(tool, args);
        string text = res.Content.Count > 0 && res.Content[0] is TextContentBlock tb ? tb.Text : "";
        JsonElement body;
        if (res.StructuredContent is { } sc) body = sc;
        else { try { body = JsonDocument.Parse(text).RootElement; } catch (JsonException) { body = JsonSerializer.SerializeToElement(new { ok = false, code = "", unstructured_error = text }); } }
        var r = new CallResult(res.IsError == true, body.Clone(), tool);
        var shown = args.ToDictionary(k => k.Key, k => k.Key == "approval_token" && k.Value != null ? "(token)" : k.Value);
        _transcript.WriteLine(JsonSerializer.Serialize(new { at = DateTime.UtcNow.ToString("O"), request = new { tool, arguments = shown }, ms = sw.ElapsedMilliseconds, is_error = r.IsError, response = r.Body }));
        Console.WriteLine($"  [{Name}] {tool} -> {(r.IsError ? "error " + r.Code : "ok")} ({sw.ElapsedMilliseconds} ms)");
        return r;
    }

    public string Approve(string op, string tx, string amount, int pid, int ttl = 300, string currency = "USD")
    {
        var (code, stdout, stderr) = Operator($"approve --db \"{Db}\" --op {op} --tx {tx} --amount {amount} --currency {currency} --pid {pid} --ttl-seconds {ttl}");
        Note($"operator approve exit {code}: {stderr.Trim()}");
        if (code != 0) throw new Exception("operator approve failed: " + stderr);
        return stdout.Trim();
    }

    public (int Code, string Out, string Err) Operator(string args)
    {
        var p = Process.Start(new ProcessStartInfo(P.Operator, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        _transcript.WriteLine(JsonSerializer.Serialize(new { at = DateTime.UtcNow.ToString("O"), operator_cli = args.Replace(Db, "operations.db"), exit = p.ExitCode, stdout = args.StartsWith("approve") ? "(token)" : o.Trim(), stderr = e.Trim() }));
        return (p.ExitCode, o, e);
    }

    public AppFacts Facts() => AppFacts.Load(Data);

    /// <summary>A real capture of the screen, with a caption band naming the requested id, the observed id, the state and the reason.</summary>
    public void Frame(string label, string requested, string observed, string state, string reason)
    {
        int w = Native.GetSystemMetrics(0), h = Native.GetSystemMetrics(1);
        using var shot = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(0, 0, 0, 0, new Size(w, h));
        shot.Save(Path.Combine(Proof, $"screen-{label}.png"), ImageFormat.Png);
        const int width = 66, line = 25;
        var rows = new List<(string Key, string Text)>();
        foreach (var (k, v) in new[] { ("requested", requested), ("observed", observed), ("state", state), ("reason", reason) })
        {
            var lines = Wrap(v, width).Take(k == "reason" ? 5 : k == "observed" ? 2 : 1).ToList();
            for (int i = 0; i < lines.Count; i++) rows.Add((i == 0 ? k : "", lines[i]));
        }
        int band = 56 + rows.Count * line + 14;
        using var f = new Bitmap(w, h + band, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(f))
        {
            g.Clear(Color.FromArgb(28, 27, 25));
            g.DrawImage(shot, 0, 0);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var big = new Font("Segoe UI Semibold", 27f, GraphicsUnit.Pixel);
            using var keyFont = new Font("Segoe UI", 19f, GraphicsUnit.Pixel);
            using var small = new Font("Consolas", 20f, GraphicsUnit.Pixel);
            using var accent = new SolidBrush(Color.FromArgb(242, 201, 76));
            using var grey = new SolidBrush(Color.FromArgb(170, 166, 158));
            using var text = new SolidBrush(Color.FromArgb(240, 237, 230));
            g.DrawString($"{Name}: {label}", big, accent, 16, h + 12);
            int y = h + 56;
            foreach (var (k, v) in rows) { g.DrawString(k, keyFont, grey, 16, y + 1); g.DrawString(v, small, text, 130, y); y += line; }
        }
        var dir = Directory.CreateDirectory(Path.Combine(P.Out, "frames")).FullName;
        f.Save(Path.Combine(dir, $"{Interlocked.Increment(ref FrameSeq):00}-{Name}-{label}.png"), ImageFormat.Png);
    }

    static IEnumerable<string> Wrap(string s, int width)
    {
        var line = new StringBuilder();
        foreach (var w in (s ?? "").Split(' '))
        {
            if (line.Length + w.Length + 1 > width && line.Length > 0) { yield return line.ToString(); line.Clear(); }
            line.Append(line.Length > 0 ? " " : "").Append(w);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    public void KeyToApp(Process app, params ushort[] vks)
    {
        Native.SetForegroundWindow(app.MainWindowHandle);
        Thread.Sleep(200);
        Native.Chord(vks);
        Thread.Sleep(400);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients.ToList()) { try { await c.DisposeAsync(); } catch { } }
        foreach (var p in _procs) { try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(5000); } } catch { } }
        Thread.Sleep(300);
        // Keep the evidence: server steps, its captures, and the application's own journal (read by the verifier).
        if (Directory.Exists(Evidence)) foreach (var f in Directory.GetFiles(Evidence)) File.Copy(f, Path.Combine(Directory.CreateDirectory(Path.Combine(Proof, "server-evidence")).FullName, Path.GetFileName(f)), true);
        foreach (var f in new[] { "journal.jsonl", "ledger.json" }) { var s = Path.Combine(Data, f); if (File.Exists(s)) File.Copy(s, Path.Combine(Proof, "app-" + f), true); }
        _transcript.Dispose();
    }
}

public static class Native
{
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

    /// <summary>Minimises the console windows on the desktop (the runner's own console) so captures show the application.</summary>
    public static List<string> MinimiseConsoles()
    {
        var done = new List<string>();
        EnumWindows((h, _) =>
        {
            var sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity);
            string cls = sb.ToString();
            if (IsWindowVisible(h) && cls is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS") { ShowWindow(h, 6); done.Add(cls); }
            return true;
        }, IntPtr.Zero);
        return done;
    }

    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    public static void Chord(params ushort[] vks)
    {
        var l = new INPUT[vks.Length * 2];
        for (int i = 0; i < vks.Length; i++) l[i] = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vks[i] } };
        for (int i = 0; i < vks.Length; i++) l[vks.Length + i] = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vks[vks.Length - 1 - i], dwFlags = 2 } };
        SendInput((uint)l.Length, l, Marshal.SizeOf<INPUT>());
    }
}
