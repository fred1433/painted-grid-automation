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
/// The approver types the row, amount, currency and process it saw. A token is issued only if those are exactly the values
/// recorded for the operation, and only if nothing on the same account or invoice is UNKNOWN or IN_FLIGHT.
/// Exit codes: 0 done, 1 refused (code and reason on stderr), 2 bad command line, 3 unexpected error.
/// </summary>
public static class Program
{
    const string Usage = "usage: ledger-operator approve|show|list|reconcile --db FILE [--op OP] [--tx TX --amount 1,234.56 --currency USD --pid N --ttl-seconds 1..3600] [--resolution confirmed|not-applied --note TEXT]";
    static readonly Regex StrictAmount = new(@"^\d{1,3}(,\d{3})*\.\d{2}$", RegexOptions.CultureInvariant);

    public static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine(Usage); return 2; }
        var a = new Dictionary<string, string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) { Console.Error.WriteLine($"unexpected argument '{args[i]}'\n{Usage}"); return 2; }
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--")) { Console.Error.WriteLine($"missing value for {args[i]}\n{Usage}"); return 2; }
            a[args[i][2..]] = args[++i];
        }
        string? Need(string k) { if (a.TryGetValue(k, out var v)) return v; Console.Error.WriteLine($"missing --{k}\n{Usage}"); return null; }
        try
        {
            if (args[0] is not ("approve" or "show" or "list" or "reconcile")) { Console.Error.WriteLine(Usage); return 2; }
            var dbPath = Need("db"); if (dbPath == null) return 2;
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
                    var store = new OperationStore(dbPath);
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
                    var o = new OperationStore(dbPath).Get(op);
                    Console.WriteLine($"{o.OpId} {o.State} {o.InvoiceRef} {o.TxId} {o.Account} {o.AmountCents} {o.Currency} {o.PayDate} pid {o.Pid}{(o.OwnerPid != null ? $" owner {o.OwnerPid}" : "")} | {o.Reason}");
                    foreach (var h in new OperationStore(dbPath).History(op)) Console.WriteLine("  " + h);
                    return 0;
                }
                case "list":
                    foreach (var o in new OperationStore(dbPath).List()) Console.WriteLine($"{o.OpId} {o.State} {o.InvoiceRef} {o.TxId} {o.Account} {o.AmountCents} {o.PayDate}");
                    return 0;
                default: // reconcile
                {
                    var op = Need("op"); var res = Need("resolution"); var note = Need("note");
                    if (op == null || res == null || note == null) return 2;
                    var store = new OperationStore(dbPath);
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
