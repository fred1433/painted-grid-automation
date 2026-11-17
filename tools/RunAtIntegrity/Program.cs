using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

// run-at-integrity <medium|low> <exe> [args...]
// Starts <exe> with this process's own token lowered to the given integrity level, with the same standard handles
// (so an MCP client can talk to it over stdio), inside a job that ends with this process. Returns the child's exit code.
// This is the documented way to start a lower-integrity process (DuplicateTokenEx, SetTokenInformation, CreateProcessAsUser).
static class Program
{
    [StructLayout(LayoutKind.Sequential)] struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct TOKEN_MANDATORY_LABEL { public SID_AND_ATTRIBUTES Label; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetHandleInformation(IntPtr h, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint len);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr t, uint access, IntPtr attrs, int level, int type, out IntPtr dup);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool ConvertStringSidToSid(string s, out IntPtr sid);
    [DllImport("advapi32.dll")] static extern int GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr t, int cls, ref TOKEN_MANDATORY_LABEL info, int len);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr token, string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    static int Fail(string what) { Console.Error.WriteLine($"run-at-integrity: {what} failed, error {Marshal.GetLastWin32Error()}"); return 3; }
    static string Quote(string a) => a.Length > 0 && !a.Any(ch => ch == ' ' || ch == '"' || ch == '\t') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    static int Main(string[] a)
    {
        if (a.Length < 2 || (a[0] != "medium" && a[0] != "low")) { Console.Error.WriteLine("usage: run-at-integrity medium|low <exe> [args...]"); return 2; }
        string sidText = a[0] == "low" ? "S-1-16-4096" : "S-1-16-8192";
        if (!OpenProcessToken(GetCurrentProcess(), 0x0002 | 0x0080 | 0x0008 | 0x0001, out var tok)) return Fail("OpenProcessToken");
        if (!DuplicateTokenEx(tok, 0x02000000, IntPtr.Zero, 2, 1, out var dup)) return Fail("DuplicateTokenEx");
        if (!ConvertStringSidToSid(sidText, out var sid)) return Fail("ConvertStringSidToSid");
        var tml = new TOKEN_MANDATORY_LABEL { Label = new SID_AND_ATTRIBUTES { Sid = sid, Attributes = 0x20 } };
        if (!SetTokenInformation(dup, 25, ref tml, Marshal.SizeOf<TOKEN_MANDATORY_LABEL>() + GetLengthSid(sid))) return Fail("SetTokenInformation(TokenIntegrityLevel)");
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), dwFlags = 0x100, hStdInput = GetStdHandle(-10), hStdOutput = GetStdHandle(-11), hStdError = GetStdHandle(-12) };
        foreach (var h in new[] { si.hStdInput, si.hStdOutput, si.hStdError }) SetHandleInformation(h, 1, 1);
        var cmd = new StringBuilder(string.Join(" ", a.Skip(1).Select(Quote)));
        if (!CreateProcessAsUser(dup, null, cmd, IntPtr.Zero, IntPtr.Zero, true, 0x08000000 | 0x00000004, IntPtr.Zero, Environment.CurrentDirectory, ref si, out var pi)) return Fail("CreateProcessAsUser");
        var job = CreateJobObject(IntPtr.Zero, null);
        var lim = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { LimitFlags = 0x2000 };
        if (job != IntPtr.Zero && SetInformationJobObject(job, 9, ref lim, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()) && !AssignProcessToJobObject(job, pi.hProcess))
            Console.Error.WriteLine($"run-at-integrity: AssignProcessToJobObject error {Marshal.GetLastWin32Error()} (the child may outlive this process)");
        Console.Error.WriteLine($"run-at-integrity: started pid {pi.dwProcessId} at {a[0]} integrity ({sidText})");
        ResumeThread(pi.hThread);
        WaitForSingleObject(pi.hProcess, 0xFFFFFFFF);
        GetExitCodeProcess(pi.hProcess, out uint code);
        return (int)code;
    }
}
