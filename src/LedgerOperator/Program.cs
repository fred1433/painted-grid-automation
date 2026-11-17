using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Guard;

namespace LedgerOperator;

/// <summary>
/// ledger-operator approve   --db F --op OP --tx TX --amount 1,234.56 --currency USD --pid N [--ttl-seconds 300]
/// ledger-operator show      --db F --op OP
/// ledger-operator list      --db F
/// ledger-operator reconcile --db F --op OP --resolution confirmed|not-applied --note "what the human checked"
/// The approver types the row, amount, currency and process it saw; the token is bound to exactly those values.
/// </summary>
public static class Program
{
    const string Usage = "usage: ledger-operator approve|show|list|reconcile --db FILE [--op OP] [--tx TX --amount 1,234.56 --currency USD --pid N --ttl-seconds S] [--resolution confirmed|not-applied --note TEXT]";

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
            var dbPath = Need("db"); if (dbPath == null) return 2;
            var store = new OperationStore(dbPath);
            switch (args[0])
            {
                case "approve":
                {
                    var op = Need("op"); var tx = Need("tx"); var amount = Need("amount"); var cur = Need("currency"); var pid = Need("pid");
                    if (op == null || tx == null || amount == null || cur == null || pid == null) return 2;
                    if (!System.Text.RegularExpressions.Regex.IsMatch(amount, @"^\d{1,3}(,\d{3})*\.\d{2}$")) { Console.Error.WriteLine("amount must be in the strict form 1,234.56"); return 2; }
                    long cents = (long)Math.Round(decimal.Parse(amount.Replace(",", ""), CultureInfo.InvariantCulture) * 100m);
                    int ttl = a.TryGetValue("ttl-seconds", out var t) ? int.Parse(t, CultureInfo.InvariantCulture) : 300;
                    var o = store.Get(op);
                    Console.Error.WriteLine($"approving {o.OpId}: {o.InvoiceRef} {o.Account} recorded as {o.TxId} {o.AmountCents} cents {o.Currency}, pid {o.Pid}, state {o.State}");
                    Console.WriteLine(store.IssueApproval(op, tx, cents, cur, int.Parse(pid, CultureInfo.InvariantCulture), TimeSpan.FromSeconds(ttl)));
                    return 0;
                }
                case "show":
                {
                    var op = Need("op"); if (op == null) return 2;
                    var o = store.Get(op);
                    Console.WriteLine($"{o.OpId} {o.State} {o.InvoiceRef} {o.TxId} {o.Account} {o.AmountCents} {o.Currency} {o.PayDate} pid {o.Pid} | {o.Reason}");
                    foreach (var h in store.History(op)) Console.WriteLine("  " + h);
                    return 0;
                }
                case "list":
                    foreach (var o in store.List()) Console.WriteLine($"{o.OpId} {o.State} {o.InvoiceRef} {o.Account} {o.AmountCents} {o.PayDate}");
                    return 0;
                case "reconcile":
                {
                    var op = Need("op"); var res = Need("resolution"); var note = Need("note");
                    if (op == null || res == null || note == null) return 2;
                    store.Reconcile(op, res, note);
                    Console.WriteLine($"{op} reconciled: {res}");
                    return 0;
                }
                default: Console.Error.WriteLine(Usage); return 2;
            }
        }
        catch (Refusal r) { Console.Error.WriteLine($"{r.Code}: {r.Message}"); return 1; }
        catch (FormatException f) { Console.Error.WriteLine($"bad number: {f.Message}"); return 2; }
    }
}
