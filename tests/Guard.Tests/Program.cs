using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Guard;

namespace GuardTests;

/// <summary>
/// Plain console runner: each test builds its own database in a temporary folder and prints PASS or FAIL with the reason.
/// The test executable also serves as the "other server process" that reserves an operation and then exits.
/// </summary>
public static class Program
{
    const string Start = "2026-01-01T00:00:00.0000000+00:00";
    const string Date = "2026-03-31";
    static string _root = "";
    static readonly List<(string Name, bool Ok, string Why)> Results = new();

    public static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--child-reserve")
        {
            // A server process that records IN_FLIGHT and then ends without confirming (owner dies).
            new OperationStore(args[1]).Reserve(args[2], args[3], "child process");
            return 0;
        }
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "guard-tests-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var tests = typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name.Contains('_') && m.ReturnType == typeof(void) && m.GetParameters().Length == 0)
            .OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
        foreach (var t in tests)
        {
            try { t.Invoke(null, null); Results.Add((t.Name, true, "")); }
            catch (TargetInvocationException ex) { Results.Add((t.Name, false, Describe(ex.InnerException!))); }
        }
        foreach (var (n, ok, why) in Results) Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {n}{(ok ? "" : ": " + why)}");
        int failed = Results.Count(r => !r.Ok);
        Console.WriteLine($"{Results.Count - failed}/{Results.Count} guard tests passed");
        return failed == 0 ? 0 : 1;
    }

    static string Describe(Exception e) => e is Fail f ? f.Message : $"{e.GetType().Name}: {e.Message}";

    sealed class Fail : Exception { public Fail(string m) : base(m) { } }
    static void Assert(bool ok, string what) { if (!ok) throw new Fail(what); }

    static OperationStore NewStore(out string db)
    {
        db = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8], "operations.db");
        return new OperationStore(db);
    }

    static Operation Prep(OperationStore s, string account, string inv, string tx, long cents = 123_400) =>
        s.PrepareOrReuse(account, inv, tx, cents, "USD", Date, 4242, Start);

    static string Approve(OperationStore s, Operation op) => s.IssueApproval(op.OpId, op.TxId, op.AmountCents, op.Currency, op.Pid, TimeSpan.FromMinutes(5));

    /// <summary>prepare, approve, reserve, send the input, then the outcome is unknown.</summary>
    static Operation MakeUnknown(OperationStore s, string account, string inv, string tx)
    {
        var op = Prep(s, account, inv, tx);
        s.Reserve(op.OpId, Approve(s, op), "test");
        s.MarkWriteInputSent(op.OpId);
        s.MarkUnknown(op.OpId, "test: the application froze after the submit input");
        Assert(s.Get(op.OpId).State == States.Unknown, "setup: operation is UNKNOWN");
        return op;
    }

    static string? RefusalCode(Action a)
    {
        try { a(); return null; }
        catch (Refusal r) { return r.Code; }
    }

    static (int Code, string Out, string Err) Cli(params string[] args)
    {
        var o = Console.Out; var e = Console.Error;
        var so = new StringWriter(); var se = new StringWriter();
        Console.SetOut(so); Console.SetError(se);
        try { return (LedgerOperator.Program.Main(args), so.ToString(), se.ToString()); }
        catch (Exception ex) { return (-1, so.ToString(), se + $"[uncaught {ex.GetType().Name}: {ex.Message}]"); }
        finally { Console.SetOut(o); Console.SetError(e); }
    }

    static void ReserveInChildThatExits(string db, string opId, string token)
    {
        string self = Environment.ProcessPath!;
        var psi = Path.GetFileNameWithoutExtension(self) == "dotnet"
            ? new ProcessStartInfo(self) { ArgumentList = { typeof(Program).Assembly.Location } }
            : new ProcessStartInfo(self);
        foreach (var a in new[] { "--child-reserve", db, opId, token }) psi.ArgumentList.Add(a);
        psi.UseShellExecute = false;
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert(p.ExitCode == 0, $"child process reserved the operation (exit {p.ExitCode})");
    }

    // ------------------------------------------------------------------ identity as read, Unicode included

    static void Identity_spelling_variants_stay_blocked_after_unknown()
    {
        foreach (var variant in new[] { "Northwind  Supply", "Northwind-Supply", "NorthwindSupply", "N0rthwind Supply", "Northwind Supply." })
        {
            var s = NewStore(out _);
            MakeUnknown(s, "Northwind Supply", "INV-1004", "TX-0004");
            var code = RefusalCode(() => Prep(s, variant, "INV-1009", "TX-0009", 55_000));
            Assert(code == "ACCOUNT_BLOCKED_UNKNOWN", $"'{variant}' after UNKNOWN on 'Northwind Supply': expected ACCOUNT_BLOCKED_UNKNOWN, got {code ?? "a new operation " + s.List().Last().State}");
        }
    }

    static void Identity_same_invoice_other_account_spelling_blocked()
    {
        var s = NewStore(out _);
        MakeUnknown(s, "Northwind Supply", "INV-1004", "TX-0004");
        var code = RefusalCode(() => Prep(s, "NORTHWIND SUPPLY INC", "INV-1004", "TX-0004"));
        Assert(code == "ACCOUNT_BLOCKED_UNKNOWN", $"same invoice TX-0004 under another spelling: expected ACCOUNT_BLOCKED_UNKNOWN, got {code ?? "a new operation"}");
    }

    static void Identity_accented_account_blocked()
    {
        var s = NewStore(out _);
        MakeUnknown(s, "Café Supply", "INV-1020", "TX-0020");
        var same = RefusalCode(() => Prep(s, "Café Supply", "INV-1021", "TX-0021", 9_900));
        Assert(same == "ACCOUNT_BLOCKED_UNKNOWN", $"'Café Supply' written identically: expected ACCOUNT_BLOCKED_UNKNOWN, got {same ?? "a new operation"}");
        var plain = RefusalCode(() => Prep(s, "Cafe Supply", "INV-1022", "TX-0022", 9_900));
        Assert(plain == "ACCOUNT_BLOCKED_UNKNOWN", $"'Cafe Supply' (no accent): expected ACCOUNT_BLOCKED_UNKNOWN, got {plain ?? "a new operation"}");
    }

    static void Identity_cli_refuses_to_approve_a_twin_of_an_unknown()
    {
        var s = NewStore(out var db);
        var twin = Prep(s, "Northwind Supply", "INV-1009", "TX-0009", 55_000);
        MakeUnknown(s, "Northwind Supply", "INV-1004", "TX-0004");
        var (code, _, err) = Cli("approve", "--db", db, "--op", twin.OpId, "--tx", twin.TxId, "--amount", "550.00", "--currency", "USD", "--pid", "4242");
        Assert(code == 1 && err.Contains("ACCOUNT_BLOCKED_UNKNOWN"), $"approve of a NOT_STARTED twin while INV-1004 is UNKNOWN: expected exit 1 ACCOUNT_BLOCKED_UNKNOWN, got exit {code}: {err.Trim()}");
    }

    // ------------------------------------------------------------------ recovery only of dead owners, conditional transitions

    static void Owner_second_server_start_leaves_a_live_in_flight_alone()
    {
        var a = NewStore(out var db);
        var op = Prep(a, "Ridge Freight", "INV-1030", "TX-0030");
        a.Reserve(op.OpId, Approve(a, op), "server A");
        var b = new OperationStore(db);
        int n = b.RecoverInFlight();
        var st = b.Get(op.OpId).State;
        Assert(n == 0 && st == States.InFlight, $"server B started while A (alive) holds IN_FLIGHT: expected 0 recovered and IN_FLIGHT, got {n} recovered and {st}");
        a.MarkWriteInputSent(op.OpId);
        a.Confirm(op.OpId, "read back as Paid");
        var hist = string.Join("\n", a.History(op.OpId));
        Assert(a.Get(op.OpId).State == States.Confirmed && !hist.Contains("state_unknown") && !hist.Contains("recovered"), "A confirms its own operation and the history holds no UNKNOWN step:\n" + hist);
    }

    static void Owner_dead_owner_is_recovered_to_unknown()
    {
        var s = NewStore(out var db);
        var op = Prep(s, "Ridge Freight", "INV-1031", "TX-0031");
        ReserveInChildThatExits(db, op.OpId, Approve(s, op));
        int n = new OperationStore(db).RecoverInFlight();
        Assert(n == 1 && s.Get(op.OpId).State == States.Unknown, $"owner process ended: expected 1 recovered to UNKNOWN, got {n}, {s.Get(op.OpId).State}");
    }

    static void Owner_transition_requires_the_expected_state()
    {
        var s = NewStore(out _);
        var op = Prep(s, "Alder Office", "INV-1040", "TX-0040");
        s.Reserve(op.OpId, Approve(s, op), "test");
        s.MarkWriteInputSent(op.OpId);
        s.Confirm(op.OpId, "read back");
        int events = s.History(op.OpId).Count;
        try { s.MarkUnknown(op.OpId, "late writer"); } catch (Exception) { }
        Assert(s.Get(op.OpId).State == States.Confirmed, $"MarkUnknown on a CONFIRMED operation must not apply; state is {s.Get(op.OpId).State}");
        Assert(s.History(op.OpId).Count == events, "no event is written for a transition that did not apply:\n" + string.Join("\n", s.History(op.OpId)));
    }

    static void Owner_history_records_only_real_transitions()
    {
        var s = NewStore(out _);
        var op = Prep(s, "Alder Office", "INV-1041", "TX-0041");
        int events = s.History(op.OpId).Count;
        try { s.RevertBeforeWrite(op.OpId, "nothing was reserved"); } catch (Exception) { }
        Assert(s.History(op.OpId).Count == events, "RevertBeforeWrite on a NOT_STARTED operation wrote an event:\n" + string.Join("\n", s.History(op.OpId)));
    }

    // ------------------------------------------------------------------ a real write failure, orphaned IN_FLIGHT

    static void Failure_read_only_database_gives_intent_not_persisted()
    {
        var s = NewStore(out var db);
        var op = Prep(s, "Quarry Tools", "INV-1050", "TX-0050");
        string token = Approve(s, op);
        File.SetAttributes(db, FileAttributes.ReadOnly);
        string? code; string what;
        try { s.Reserve(op.OpId, token, "test"); code = null; what = "no error"; }
        catch (Refusal r) { code = r.Code; what = r.Code + ": " + r.Message; }
        catch (Exception e) { code = null; what = e.GetType().Name + ": " + e.Message; }
        finally { File.SetAttributes(db, FileAttributes.Normal); }
        Assert(code == "INTENT_NOT_PERSISTED", $"database file read-only: expected INTENT_NOT_PERSISTED, got {what}");
        s.Reserve(op.OpId, token, "test, writable again");
        Assert(s.Get(op.OpId).State == States.InFlight, "the approval was not consumed by the failed attempt");
    }

    static void Failure_cli_reconciles_an_orphaned_in_flight()
    {
        var s = NewStore(out var db);
        var op = Prep(s, "Quarry Tools", "INV-1051", "TX-0051");
        ReserveInChildThatExits(db, op.OpId, Approve(s, op));
        var (code, _, err) = Cli("reconcile", "--db", db, "--op", op.OpId, "--resolution", "not-applied", "--note", "no payment in the ledger");
        Assert(code == 0 && s.Get(op.OpId).State == States.NotStarted, $"IN_FLIGHT whose owner ended: expected reconcile exit 0 and NOT_STARTED, got exit {code} {s.Get(op.OpId).State}: {err.Trim()}");
    }

    static void Failure_cli_refuses_to_reconcile_a_live_in_flight()
    {
        var s = NewStore(out var db);
        var op = Prep(s, "Quarry Tools", "INV-1052", "TX-0052");
        s.Reserve(op.OpId, Approve(s, op), "this process, alive");
        var (code, _, _) = Cli("reconcile", "--db", db, "--op", op.OpId, "--resolution", "not-applied", "--note", "x");
        Assert(code == 1 && s.Get(op.OpId).State == States.InFlight, $"IN_FLIGHT owned by a live process: expected exit 1, got {code} {s.Get(op.OpId).State}");
    }

    // ------------------------------------------------------------------ CLI values, TTL, no silent re-binding

    static void Values_cli_refuses_values_that_do_not_match_the_operation()
    {
        var s = NewStore(out var db);
        var op = Prep(s, "Linden Print", "INV-1060", "TX-0060", 123_400);
        foreach (var (what, args) in new[]
        {
            ("another row", new[] { "--tx", "TX-9999", "--amount", "1,234.00", "--currency", "USD", "--pid", "4242" }),
            ("another amount", new[] { "--tx", "TX-0060", "--amount", "0,000.00", "--currency", "USD", "--pid", "4242" }),
            ("another currency", new[] { "--tx", "TX-0060", "--amount", "1,234.00", "--currency", "usd", "--pid", "4242" }),
            ("another process", new[] { "--tx", "TX-0060", "--amount", "1,234.00", "--currency", "USD", "--pid", "1" }),
        })
        {
            var (code, outp, err) = Cli(new[] { "approve", "--db", db, "--op", op.OpId }.Concat(args).ToArray());
            Assert(code != 0 && outp.Trim() == "", $"approve with {what}: expected a refusal and no token, got exit {code} {err.Trim()}");
        }
        var (ok, tok, _) = Cli("approve", "--db", db, "--op", op.OpId, "--tx", "TX-0060", "--amount", "1,234.00", "--currency", "USD", "--pid", "4242");
        Assert(ok == 0 && tok.Trim().Length > 20, "the exact values still get a token");
    }

    static void Values_cli_ttl_is_bounded_and_never_crashes()
    {
        var s = NewStore(out var db);
        var op = Prep(s, "Linden Print", "INV-1061", "TX-0061", 123_400);
        foreach (var ttl in new[] { "-10", "0", "3601", "99999999999", "ten" })
        {
            var (code, outp, err) = Cli("approve", "--db", db, "--op", op.OpId, "--tx", "TX-0061", "--amount", "1,234.00", "--currency", "USD", "--pid", "4242", "--ttl-seconds", ttl);
            Assert(code == 2 && outp.Trim() == "", $"--ttl-seconds {ttl}: expected exit 2 and no token, got exit {code}: {err.Trim()}");
        }
    }

    static void Values_prepare_never_rebinds_an_existing_operation()
    {
        var s = NewStore(out _);
        var first = Prep(s, "Maple Courier", "INV-1070", "TX-0070", 80_000);
        var second = Prep(s, "Maple Courier", "INV-1071", "TX-0071", 80_000);
        Assert(second.OpId != first.OpId, "two invoices with the same account, amount and date are two operations");
        Assert(s.Get(first.OpId).TxId == "TX-0070" && s.Get(first.OpId).InvoiceRef == "INV-1070", $"the first operation still targets TX-0070 INV-1070, not {s.Get(first.OpId).TxId} {s.Get(first.OpId).InvoiceRef}");
    }

    static void Values_same_row_under_another_reference_is_refused()
    {
        var s = NewStore(out _);
        Prep(s, "Maple Courier", "INV-1072", "TX-0072", 80_000);
        var code = RefusalCode(() => Prep(s, "Maple Courier", "INV-1073", "TX-0072", 80_000));
        Assert(code == "IDENTITY_CONFLICT", $"TX-0072 read with another invoice reference: expected IDENTITY_CONFLICT, got {code ?? "a silent update"}");
    }

    static void Owner_only_the_owner_may_record_the_write_input()
    {
        var a = NewStore(out var db);
        var op = Prep(a, "Ridge Freight", "INV-1032", "TX-0032");
        a.Reserve(op.OpId, Approve(a, op), "server A");
        var b = new OperationStore(db, new ProcessOwner(1, Start));
        int events = a.History(op.OpId).Count;
        bool refused;
        try { b.MarkWriteInputSent(op.OpId); refused = false; } catch (InvalidOperationException) { refused = true; }
        Assert(refused && a.History(op.OpId).Count == events, "another process may not record the write input of an operation it does not own");
        a.MarkWriteInputSent(op.OpId);
        Assert(a.History(op.OpId).Count == events + 1, "the owner may");
    }

    static void Values_one_open_operation_per_invoice()
    {
        var s = NewStore(out _);
        Prep(s, "Maple Courier", "INV-1074", "TX-0074", 80_000);
        var code = RefusalCode(() => Prep(s, "Maple Courier", "INV-1074", "TX-0074", 90_000));
        Assert(code == "INVOICE_HAS_OPERATION", $"the same invoice with another amount: expected INVOICE_HAS_OPERATION, got {code ?? "a second operation"}");
    }

    // ------------------------------------------------------------------ behaviour kept from before (must pass before and after)

    static void Approval_approval_is_single_use_and_bound()
    {
        var s = NewStore(out _);
        var op = Prep(s, "Pioneer Water", "INV-1080", "TX-0080");
        var other = Prep(s, "Summit Janitorial", "INV-1081", "TX-0081", 10_000);
        Assert(RefusalCode(() => s.Reserve(op.OpId, Approve(s, other), "t")) == "APPROVAL_MISMATCH", "a token for another operation is refused");
        Assert(s.Get(op.OpId).State == States.NotStarted, "a refused token changes nothing");
        string t = Approve(s, op);
        s.Reserve(op.OpId, t, "t");
        Assert(RefusalCode(() => s.Reserve(op.OpId, t, "t")) == "APPROVAL_ALREADY_USED", "a used token is refused");
    }
}
