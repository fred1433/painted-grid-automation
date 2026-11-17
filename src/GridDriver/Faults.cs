using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace GridDriver;

/// <summary>
/// Test-only faults, all named in one environment variable (PGA_FAULT, comma separated), read once and logged at server
/// start. Empty in normal runs. Each one stands for a failure that cannot be produced on demand on a hosted runner:
///   ocr-1000-as-100       reader A drops a digit group ("1,000.00" read as "100.00")
///   garble-scan:INV-1234  reader A's scan pass misreads the Account cell of that row
///   wrong-click           the selection click is sent to the neighbouring row (a real SendInput, on the wrong row)
///   msaa-access-denied    AccessibleObjectFromWindow returns E_ACCESSDENIED
///   throw-before-submit   a Win32Exception (as CopyFromScreen throws on a locked desktop) just before the Post click
///   throw-after-submit    the same, just after the Post click
///   crash-after-submit    the server process dies between the Post click and its check
/// The operation database is never faked: its write failure is produced with a read-only file.
/// </summary>
public static class Faults
{
    static readonly Dictionary<string, string> Active = Parse(Environment.GetEnvironmentVariable("PGA_FAULT"));

    static Dictionary<string, string> Parse(string? v) =>
        (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.Split(':', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");

    public static bool Has(string name) => Active.ContainsKey(name);
    public static string? Arg(string name) => Active.TryGetValue(name, out var v) ? v : null;
    public static string[] Names => Active.Select(kv => kv.Value == "" ? kv.Key : kv.Key + ":" + kv.Value).ToArray();

    public static void ThrowIf(string name, string where)
    {
        if (Has(name)) throw new Win32Exception(6, $"fault injected ({name}): screen capture failed {where}");
    }
}
