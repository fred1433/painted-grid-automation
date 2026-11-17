using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Guard;

namespace LedgerOperator;

/// <summary>
/// ledger-operator approve   --db F --op OP --tx TX --amount 1,234.56 --currency USD --pid N [--ttl-seconds 300]
/// ledger-operator show      --db F --op OP
/// ledger-operator list      --db F
/// ledger-operator reconcile --db F --op OP --resolution confirmed|not-applied --note "what the human checked"
/// The approver types the row, amount, currency and process of the operation. A token is issued only if those are exactly the
/// values recorded for the operation, and only if nothing on the same account or invoice is UNKNOWN or IN_FLIGHT. This keeps a
/// token from being issued for another operation; it does not prove that the approver looked at the screen.
/// Issuing a token revokes the operation's unused ones; a reconciliation revokes the unused ones on its account, invoice or row.
/// The CLI opens only an existing database (the server creates it) and refuses any option the command does not take.
/// Exit codes: 0 done, 1 refused (code and reason on stderr), 2 bad command line (unknown or repeated option, missing value,
/// no database at the path given), 3 unexpected error (type and message on stderr).
/// </summary>
public static class Program
{
    const string Usage = "usage: ledger-operator approve|show|list|reconcile --db FILE [--op OP] [--tx TX --amount 1,234.56 --currency USD --pid N --ttl-seconds 1..3600] [--resolution confirmed|not-applied --note TEXT]";
    static readonly Dictionary<string, string[]> Options = new()
    {
        ["approve"] = new[] { "db", "op", "tx", "amount", "currency", "pid", "ttl-seconds" },
        ["show"] = new[] { "db", "op" },
        ["list"] = new[] { "db" },
        ["reconcile"] = new[] { "db", "op", "resolution", "note" },
    };
    static readonly Regex StrictAmount = new(@"^\d{1,3}(,\d{3})*\.\d{2}$", RegexOptions.CultureInvariant);

    public static int Main(string[] args)
    {
        if (args.Length == 0 || !Options.TryGetValue(args[0], out var allowed)) { Console.Error.WriteLine(Usage); return 2; }
        var a = new Dictionary<string, string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) { Console.Error.WriteLine($"unexpected argument '{args[i]}'\n{Usage}"); return 2; }
            string key = args[i][2..];
            if (Array.IndexOf(allowed, key) < 0) { Console.Error.WriteLine($"{args[0]} does not take {args[i]} (it takes --{string.Join(", --", allowed)})\n{Usage}"); return 2; }
            if (a.ContainsKey(key)) { Console.Error.WriteLine($"{args[i]} is given twice\n{Usage}"); return 2; }
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--")) { Console.Error.WriteLine($"missing value for {args[i]}\n{Usage}"); return 2; }
            a[key] = args[++i];
        }
        string? Need(string k) { if (a.TryGetValue(k, out var v)) return v; Console.Error.WriteLine($"missing --{k}\n{Usage}"); return null; }
        try
        {
            var dbPath = Need("db"); if (dbPath == null) return 2;
            // Never create a database: a mistyped path would otherwise open an empty one and report nothing blocked.
            string full = System.IO.Path.GetFullPath(dbPath);
            if (!System.IO.File.Exists(full)) { Console.Error.WriteLine($"no operation database at {full}; check --db (the server creates it on its first start, this CLI never does)"); return 2; }
            switch (args[0])
            {
                case "approve":
                {
                    var op = Need("op"); var tx = Need("tx"); var amount = Need("amount"); var cur = Need("currency"); var pid = Need("pid");
                    if (op == null || tx == null || amount == null || cur == null || pid == null) return 2;
                    if (!StrictAmount.IsMatch(amount)) { Console.Error.WriteLine("amount must be in the strict form 1,234.56"); return 2; }
                    if (!decimal.TryParse(amount.Replace(",", ""), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amt) || amt > 1_000_000_000m)
                    { Console.Error.WriteLine("amount is out of range"); return 2; }
                    long cents = (long)Math.Round(amt * 100m);
                    int ttl = 300;
                    if (a.TryGetValue("ttl-seconds", out var t) && (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out ttl) || ttl < 1 || ttl > OperationStore.MaxApprovalSeconds))
                    { Console.Error.WriteLine($"--ttl-seconds must be a whole number from 1 to {OperationStore.MaxApprovalSeconds}"); return 2; }
                    if (!int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out int pidValue) || pidValue < 1) { Console.Error.WriteLine("--pid must be a positive whole number"); return 2; }
                    var store = OperationStore.OpenExisting(full);
                    var o = store.Get(op);
                    Console.Error.WriteLine($"operation {o.OpId}: {o.InvoiceRef} '{o.Account}' recorded as {o.TxId} {Money(o.AmountCents)} {o.Currency}, pid {o.Pid}, state {o.State}");
                    string token = store.IssueApproval(op, tx, cents, cur, pidValue, TimeSpan.FromSeconds(ttl));
                    Console.Error.WriteLine($"approved: {o.TxId} {Money(o.AmountCents)} {o.Currency} pid {o.Pid}, single use, valid {ttl} s");
                    Console.WriteLine(token);
                    return 0;
                }
                case "show":
                {
                    var op = Need("op"); if (op == null) return 2;
                    var o = OperationStore.OpenExisting(full).Get(op);
                    Console.WriteLine($"{o.OpId} {o.State} {o.InvoiceRef} {o.TxId} {o.Account} {o.AmountCents} {o.Currency} {o.PayDate} pid {o.Pid}{(o.OwnerPid != null ? $" owner {o.OwnerPid}" : "")} | {o.Reason}");
                    foreach (var h in OperationStore.OpenExisting(full).History(op)) Console.WriteLine("  " + h);
                    return 0;
                }
                case "list":
                    foreach (var o in OperationStore.OpenExisting(full).List()) Console.WriteLine($"{o.OpId} {o.State} {o.InvoiceRef} {o.TxId} {o.Account} {o.AmountCents} {o.PayDate}");
                    return 0;
                default: // reconcile
                {
                    var op = Need("op"); var res = Need("resolution"); var note = Need("note");
                    if (op == null || res == null || note == null) return 2;
                    var store = OperationStore.OpenExisting(full);
                    // The same lock as the server's locate-verify-write route: no reconciliation while a write is running.
                    RouteLock.Run(RouteLock.LedgerRoute, TimeSpan.FromSeconds(30), () => { store.Reconcile(op, res, note); return 0; });
                    Console.WriteLine($"{op} reconciled: {res}");
                    return 0;
                }
            }
        }
        catch (Refusal r) { Console.Error.WriteLine($"{r.Code}: {r.Message}"); return 1; }
        catch (Exception e) { Console.Error.WriteLine($"error: {e.GetType().Name}: {e.Message}"); return 3; }
    }

    static string Money(long cents) => (cents / 100m).ToString("#,##0.00", CultureInfo.InvariantCulture);
}
