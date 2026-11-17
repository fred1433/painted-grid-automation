using System;
using System.IO;
using System.Threading.Tasks;
using GridDriver;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace LedgerMcp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Native.MakeProcessPerMonitorAware();
        string Arg(string name, string? env, string def)
        {
            int i = Array.IndexOf(args, name);
            if (i >= 0 && i + 1 < args.Length) return args[i + 1];
            return (env != null ? Environment.GetEnvironmentVariable(env) : null) ?? def;
        }
        var cfg = new DriverConfig
        {
            AllowedExe = Arg("--allow-exe", "PGA_ALLOW_EXE", ""),
            EvidenceDir = Arg("--evidence", "PGA_EVIDENCE", "evidence"),
            TessdataDir = Arg("--tessdata", "PGA_TESSDATA", "tessdata"),
        };
        string db = Arg("--db", "PGA_DB", Path.Combine("state", "operations.db"));
        if (cfg.AllowedExe == "" || !File.Exists(cfg.AllowedExe))
        {
            Console.Error.WriteLine($"ledger-mcp: --allow-exe must name the application's executable ({(cfg.AllowedExe == "" ? "missing" : "not found: " + cfg.AllowedExe)})\n" +
                "usage: ledger-mcp --allow-exe PATH\\LedgerApp.exe [--db state\\operations.db] [--evidence evidence] [--tessdata tessdata]");
            return 2;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = Array.Empty<string>(), ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton(_ => new PaymentService(cfg, db));
        builder.Services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "ledger-grid", Version = "0.1.0" })
            .WithStdioServerTransport()
            .WithTools<LedgerTools>();
        await builder.Build().RunAsync();
        return 0;
    }
}
