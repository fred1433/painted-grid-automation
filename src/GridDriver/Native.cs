using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GridDriver;

public static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SCROLLINFO { public uint cbSize, fMask; public int nMin, nMax; public uint nPage; public int nPos, nTrackPos; }

    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowDpiAwarenessContext(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] public static extern bool GetScrollInfo(IntPtr h, int bar, ref SCROLLINFO si);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);
    [DllImport("oleacc.dll")] public static extern int AccessibleObjectFromWindow(IntPtr h, uint id, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object acc);

    // Process identity and integrity level, readable with PROCESS_QUERY_LIMITED_INFORMATION (also on an elevated process).
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref uint size);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, uint len, out uint ret);
    [DllImport("advapi32.dll")] public static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] public static extern IntPtr GetSidSubAuthority(IntPtr sid, uint i);
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x0008;

    public static string? ImagePath(int pid, out string why)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) { why = $"OpenProcess error {Marshal.GetLastWin32Error()}"; return null; }
        try
        {
            var sb = new StringBuilder(1024); uint n = (uint)sb.Capacity;
            if (!QueryFullProcessImageName(h, 0, sb, ref n)) { why = $"QueryFullProcessImageName error {Marshal.GetLastWin32Error()}"; return null; }
            why = "ok"; return sb.ToString();
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Mandatory integrity level RID (0x1000 low, 0x2000 medium, 0x3000 high, 0x4000 system), or null with the reason.</summary>
    public static int? IntegrityLevel(int pid, out string why)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) { why = $"OpenProcess error {Marshal.GetLastWin32Error()}"; return null; }
        IntPtr tok = IntPtr.Zero, buf = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(h, TOKEN_QUERY, out tok)) { why = $"OpenProcessToken error {Marshal.GetLastWin32Error()}"; return null; }
            GetTokenInformation(tok, 25, IntPtr.Zero, 0, out uint len);
            buf = Marshal.AllocHGlobal((int)len);
            if (!GetTokenInformation(tok, 25, buf, len, out _)) { why = $"GetTokenInformation error {Marshal.GetLastWin32Error()}"; return null; }
            IntPtr sid = Marshal.ReadIntPtr(buf);
            int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            why = "ok";
            return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); if (tok != IntPtr.Zero) CloseHandle(tok); CloseHandle(h); }
    }

    public static string LevelName(int? rid) => rid switch { null => "unreadable", < 0x2000 => $"low (0x{rid:X})", < 0x3000 => $"medium (0x{rid:X})", < 0x4000 => $"high (0x{rid:X})", _ => $"system (0x{rid:X})" };

    public const uint WM_NULL = 0x0000, WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E, SMTO_ABORTIFHUNG = 0x0002, GA_ROOT = 2, GA_ROOTOWNER = 3, GW_OWNER = 4;
    public const uint OBJID_CLIENT = 0xFFFFFFFC;
    public static readonly Guid IID_IAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    public static string Text(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); }
    public static string Class(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }
    public static System.Drawing.Rectangle Rect(IntPtr h) { GetWindowRect(h, out var r); return System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom); }
    public static int Pid(IntPtr h) { GetWindowThreadProcessId(h, out int p); return p; }
    public static void MakeProcessPerMonitorAware() => SetProcessDpiAwarenessContext(new IntPtr(-4));

    // SendInput. On x64 the INPUT union starts at offset 8 and the struct is 40 bytes.
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; [FieldOffset(8)] public KEYBDINPUT ki; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    static void Send(INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length) throw new InvalidOperationException($"SendInput sent {sent}/{inputs.Length}, error {Marshal.GetLastWin32Error()}");
    }

    static (int, int) Normalize(int x, int y)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        return ((int)Math.Round((x - vx) * 65535.0 / (vw - 1)), (int)Math.Round((y - vy) * 65535.0 / (vh - 1)));
    }

    const uint MOVE = 0x0001, DOWN = 0x0002, UP = 0x0004, WHEEL = 0x0800, VDESK = 0x4000, ABS = 0x8000, KEYUP = 0x0002;

    public static void Click(int x, int y)
    {
        var (nx, ny) = Normalize(x, y);
        Send(new[]
        {
            new INPUT { type = 0, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOVE | ABS | VDESK } },
            new INPUT { type = 0, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = DOWN | ABS | VDESK } },
            new INPUT { type = 0, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = UP | ABS | VDESK } },
        });
    }

    public static void Wheel(int x, int y, int delta)
    {
        var (nx, ny) = Normalize(x, y);
        Send(new[]
        {
            new INPUT { type = 0, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOVE | ABS | VDESK } },
            new INPUT { type = 0, mi = new MOUSEINPUT { dx = nx, dy = ny, mouseData = unchecked((uint)delta), dwFlags = WHEEL | ABS | VDESK } },
        });
    }

    public static void Chord(params ushort[] vks)
    {
        var l = new INPUT[vks.Length * 2];
        for (int i = 0; i < vks.Length; i++) l[i] = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vks[i] } };
        for (int i = 0; i < vks.Length; i++) l[vks.Length + i] = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vks[vks.Length - 1 - i], dwFlags = KEYUP } };
        Send(l);
    }

    public const ushort VK_CONTROL = 0x11, VK_MENU = 0x12, VK_ESCAPE = 0x1B, VK_F6 = 0x75, VK_P = 0x50;
}
