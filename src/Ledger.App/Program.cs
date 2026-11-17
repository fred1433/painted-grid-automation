using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;

namespace Ledger.App;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var opt = AppOptions.Parse(args);
        Application.SetHighDpiMode(opt.DpiUnaware ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (opt.Parasite != null) { Application.Run(new ParasiteForm(opt.Parasite.Value)); return 0; }
        var store = LedgerStore.OpenOrCreate(opt.DataDir, opt.Seed, opt.Flags);
        Application.Run(new MainForm(store, opt));
        return 0;
    }
}

/// <summary>Command line of the witness app. Every variation is driven by a logged seed or a named flag.</summary>
public sealed class AppOptions
{
    public string DataDir = "ledger-data";
    public int Seed = 1;
    public HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase);
    public float RenderScale = 1f;
    public bool DpiUnaware;
    public string Title = "Payables Ledger - Synthetic Co.";
    public System.Drawing.Rectangle? Parasite;

    public static AppOptions Parse(string[] a)
    {
        var o = new AppOptions();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--data": o.DataDir = a[++i]; break;
                case "--seed": o.Seed = int.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--flags": foreach (var f in a[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)) o.Flags.Add(f.Trim()); break;
                case "--render-scale": o.RenderScale = float.Parse(a[++i], CultureInfo.InvariantCulture); break;
                case "--dpi-unaware": o.DpiUnaware = true; break;
                case "--title": o.Title = a[++i]; break;
                case "--parasite":
                    var p = Array.ConvertAll(a[++i].Split(','), s => int.Parse(s, CultureInfo.InvariantCulture));
                    o.Parasite = new System.Drawing.Rectangle(p[0], p[1], p[2], p[3]); break;
            }
        }
        return o;
    }
}
