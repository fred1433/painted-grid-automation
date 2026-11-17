using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Guard;

/// <summary>Business identity of a row, as read on screen.</summary>
public static class Identity
{
    /// <summary>
    /// Loose folding of free text (an account name read on screen or typed by a caller): compatibility forms are decomposed
    /// (the ligature U+FB01 becomes fi), combining accents are dropped (é, ü, ó), the Latin letters that do not decompose are
    /// spelled out (ø o, ß ss, ł l, æ ae, œ oe, đ and ð d, þ th, dotless ı i), case, spaces and punctuation are dropped, and
    /// the letters OCR confuses with digits (O, I, L, S) become 0, 1, 1, 5. Letters of other scripts are kept as they are.
    /// Used for two things only: shortlisting rows during a scan, and the account key that blocks a retry, so that two
    /// spellings that fold the same are the same account. It never accepts a value on its own: a value is accepted when
    /// both OCR readers agree on the exact string (and, for Ref #, Date and Amount, when the pixels match it re-drawn).
    /// </summary>
    public static string Fold(string? s)
    {
        var sb = new StringBuilder();
        foreach (char ch in (s ?? "").Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            string? spelled = ch switch
            {
                '\u00F8' or '\u00D8' => "O", '\u00DF' or '\u1E9E' => "SS", '\u0142' or '\u0141' => "L", '\u00E6' or '\u00C6' => "AE",
                '\u0153' or '\u0152' => "OE", '\u0111' or '\u0110' or '\u00F0' or '\u00D0' => "D", '\u00FE' or '\u00DE' => "TH", '\u0131' => "I",
                _ => null,
            };
            if (spelled != null) { sb.Append(spelled); continue; }
            char u = char.ToUpperInvariant(ch);
            if (char.IsLetterOrDigit(u)) sb.Append(u);
        }
        return sb.ToString().Replace('O', '0').Replace('I', '1').Replace('L', '1').Replace('S', '5');
    }
}

/// <summary>The process that holds an IN_FLIGHT operation: its id and start time, so a reused process id is not mistaken for it.</summary>
public sealed record ProcessOwner(int Pid, string Start)
{
    public static ProcessOwner Current { get; } = FromCurrent();

    static ProcessOwner FromCurrent()
    {
        using var p = Process.GetCurrentProcess();
        return new ProcessOwner(p.Id, StartOf(p));
    }

    public static string StartOf(Process p) => p.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// True when a process with this id is running and started at the recorded time (within one second).
    /// A process whose start time cannot be read counts as alive: recovery then waits rather than guesses.
    /// </summary>
    public bool IsAlive(out string why)
    {
        Process p;
        try { p = Process.GetProcessById(Pid); }
        catch (ArgumentException) { why = $"no process {Pid} is running"; return false; }
        using (p)
        {
            try
            {
                if (p.HasExited) { why = $"process {Pid} has exited"; return false; }
                string s = StartOf(p);
                bool same = Math.Abs((DateTimeOffset.Parse(s, CultureInfo.InvariantCulture) - DateTimeOffset.Parse(Start, CultureInfo.InvariantCulture)).TotalSeconds) < 1;
                why = same ? $"process {Pid} started {Start} is running" : $"process id {Pid} now belongs to a process started {s}";
                return same;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or FormatException)
            {
                why = $"process {Pid} exists but its start time cannot be read ({ex.GetType().Name}); treated as alive";
                return true;
            }
        }
    }
}
