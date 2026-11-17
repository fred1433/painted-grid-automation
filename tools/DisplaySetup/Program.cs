using System;
using System.Runtime.InteropServices;

// display-setup <width> <height> <scalePercent>
// 1) ChangeDisplaySettings to the requested resolution.
// 2) Per-monitor scaling through DisplayConfigSetDeviceInfo with the undocumented
//    DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE (-4), the call the Settings app uses. Relative to the recommended scale.
// Exit code 0 only if both took effect.
static class Program
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string? dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint nPaths, IntPtr paths, ref uint nModes, IntPtr modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(IntPtr packet);
    [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(IntPtr packet);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(long pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);

    static readonly int[] Scales = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    static int Main(string[] a)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        int w = int.Parse(a[0]), h = int.Parse(a[1]), want = int.Parse(a[2]);
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        EnumDisplaySettings(null, -1, ref dm);
        Console.WriteLine($"current mode {dm.dmPelsWidth}x{dm.dmPelsHeight}");
        var m = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        for (int i = 0; EnumDisplaySettings(null, i, ref m); i++) if (m.dmPelsWidth == w && m.dmPelsHeight == h) { Console.WriteLine($"mode {w}x{h} is listed"); break; }
        dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmFields = 0x80000 | 0x100000;
        int r = ChangeDisplaySettings(ref dm, 1);
        Console.WriteLine($"ChangeDisplaySettings {w}x{h}: {r} (0 = success)");

        GetDisplayConfigBufferSizes(2, out uint np, out uint nm);
        IntPtr paths = Marshal.AllocHGlobal((int)np * 72), modes = Marshal.AllocHGlobal((int)nm * 64);
        int q = QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero);
        Console.WriteLine($"QueryDisplayConfig: {q}, {np} path(s)");
        if (q != 0 || np == 0) return 3;
        long adapter = Marshal.ReadInt64(paths, 0); int source = Marshal.ReadInt32(paths, 8);
        IntPtr pkt = Marshal.AllocHGlobal(32);
        Marshal.WriteInt32(pkt, 0, -3); Marshal.WriteInt32(pkt, 4, 32); Marshal.WriteInt64(pkt, 8, adapter); Marshal.WriteInt32(pkt, 16, source);
        int g = DisplayConfigGetDeviceInfo(pkt);
        int min = Marshal.ReadInt32(pkt, 20), cur = Marshal.ReadInt32(pkt, 24), max = Marshal.ReadInt32(pkt, 28);
        int rec = Math.Abs(min);
        Console.WriteLine($"get dpi scale: {g}; min {min} cur {cur} max {max}; recommended {Scales[rec]} %, current {Scales[Math.Clamp(rec + cur, 0, Scales.Length - 1)]} %");
        int target = Array.IndexOf(Scales, want) - rec;
        if (target < min || target > max) { Console.WriteLine($"{want} % is outside the allowed range at this resolution"); return 4; }
        IntPtr set = Marshal.AllocHGlobal(24);
        Marshal.WriteInt32(set, 0, -4); Marshal.WriteInt32(set, 4, 24); Marshal.WriteInt64(set, 8, adapter); Marshal.WriteInt32(set, 16, source); Marshal.WriteInt32(set, 20, target);
        int s = DisplayConfigSetDeviceInfo(set);
        System.Threading.Thread.Sleep(1500);
        var mon = MonitorFromPoint(0, 1);
        GetDpiForMonitor(mon, 0, out uint dx, out _);
        Console.WriteLine($"set dpi scale relative {target}: {s}; GetDpiForMonitor now {dx}");
        return r == 0 && s == 0 && dx == (uint)(96 * want / 100) ? 0 : 5;
    }
}
