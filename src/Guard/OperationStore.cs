using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Guard;

/// <summary>Business refusal. Code is a stable string; nothing here is a score.</summary>
public sealed class Refusal : Exception
{
    public string Code { get; }
    public Refusal(string code, string message) : base(message) { Code = code; }
}

public sealed record Operation(
    string OpId, string BusinessKey, string Account, string InvoiceRef, string TxId, long AmountCents, string Currency,
    string PayDate, int Pid, string ProcessStart, string State, string? Reason, bool WriteInputSent, string CreatedAt, string UpdatedAt);

/// <summary>
/// Operation states. Reported states are NOT_STARTED, CONFIRMED and UNKNOWN. IN_FLIGHT is written durably
/// before the write input is sent; a process that finds IN_FLIGHT at startup turns it into UNKNOWN.
/// Nothing observed on screen ever moves an operation back to NOT_STARTED.
/// </summary>
public static class States
{
    public const string NotStarted = "NOT_STARTED", InFlight = "IN_FLIGHT", Confirmed = "CONFIRMED", Unknown = "UNKNOWN";
}

public sealed class OperationStore
{
    readonly string _cs;
    public string Path { get; }

    public OperationStore(string dbPath)
    {
        Path = System.IO.Path.GetFullPath(dbPath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _cs = new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, DefaultTimeout = 30 }.ToString();
        using var c = Open();
        Exec(c, null, @"
CREATE TABLE IF NOT EXISTS operations(
  op_id TEXT PRIMARY KEY, business_key TEXT NOT NULL, account TEXT NOT NULL, invoice_ref TEXT NOT NULL, tx_id TEXT NOT NULL,
  amount_cents INTEGER NOT NULL, currency TEXT NOT NULL, pay_date TEXT NOT NULL, pid INTEGER NOT NULL, process_start TEXT NOT NULL,
  state TEXT NOT NULL, reason TEXT, write_input_sent INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS approvals(
  token_hash TEXT PRIMARY KEY, op_id TEXT NOT NULL, tx_id TEXT NOT NULL, amount_cents INTEGER NOT NULL, currency TEXT NOT NULL,
  pid INTEGER NOT NULL, issued_at TEXT NOT NULL, expires_at TEXT NOT NULL, used_at TEXT, used_note TEXT);
CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, op_id TEXT, kind TEXT NOT NULL, detail TEXT);");
    }

    SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        Exec(c, null, "PRAGMA synchronous=FULL;");
        return c;
    }

    static string Now() => DateTimeOffset.UtcNow.ToString("O");
    public static string Key(string account, long cents, string date) => $"{account.Trim().ToUpperInvariant()}|{cents}|{date.Trim()}";

    static int Exec(SqliteConnection c, SqliteTransaction? t, string sql, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    static Operation? ReadOp(SqliteConnection c, SqliteTransaction? t, string where, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = "SELECT op_id,business_key,account,invoice_ref,tx_id,amount_cents,currency,pay_date,pid,process_start,state,reason,write_input_sent,created_at,updated_at FROM operations WHERE " + where + " ORDER BY created_at LIMIT 1";
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Operation(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetString(6),
            r.GetString(7), r.GetInt32(8), r.GetString(9), r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11), r.GetInt64(12) != 0, r.GetString(13), r.GetString(14));
    }

    void Event(SqliteConnection c, SqliteTransaction? t, string? op, string kind, string detail) =>
        Exec(c, t, "INSERT INTO events(at,op_id,kind,detail) VALUES($a,$o,$k,$d)", ("$a", Now()), ("$o", op), ("$k", kind), ("$d", detail));

    public void Event(string? op, string kind, string detail) { using var c = Open(); Event(c, null, op, kind, detail); }

    /// <summary>At server start: any IN_FLIGHT left behind means the outcome is not known.</summary>
    public int RecoverInFlight()
    {
        using var c = Open();
        using var t = c.BeginTransaction();
        int n = Exec(c, t, "UPDATE operations SET state='UNKNOWN', reason='found IN_FLIGHT at startup: the process ended between the write input and its confirmation', updated_at=$n WHERE state='IN_FLIGHT'", ("$n", Now()));
        if (n > 0) Event(c, t, null, "recovered_in_flight", $"{n} operation(s) set to UNKNOWN");
        t.Commit();
        return n;
    }

    public Operation Get(string opId)
    {
        using var c = Open();
        return ReadOp(c, null, "op_id=$id", ("$id", opId)) ?? throw new Refusal("OPERATION_NOT_FOUND", $"no operation {opId}");
    }

    public Operation? BlockingOperation(string account)
    {
        using var c = Open();
        return ReadOp(c, null, "upper(account)=$a AND state IN ('UNKNOWN','IN_FLIGHT')", ("$a", account.Trim().ToUpperInvariant()));
    }

    /// <summary>Records the intention (NOT_STARTED), keyed by business context, never by a request id.</summary>
    public Operation PrepareOrReuse(string account, string invoiceRef, string txId, long cents, string currency, string payDate, int pid, string processStart)
    {
        using var c = Open();
        using var t = c.BeginTransaction();
        var blocking = ReadOp(c, t, "upper(account)=$a AND state IN ('UNKNOWN','IN_FLIGHT')", ("$a", account.Trim().ToUpperInvariant()));
        if (blocking != null)
            throw new Refusal("ACCOUNT_BLOCKED_UNKNOWN", $"operation {blocking.OpId} on this account is {blocking.State}; a human must reconcile it with the operator CLI before any new write");
        string key = Key(account, cents, payDate);
        var existing = ReadOp(c, t, "business_key=$k", ("$k", key));
        if (existing != null && existing.State == States.Confirmed)
            throw new Refusal("ALREADY_CONFIRMED", $"operation {existing.OpId} with the same account, amount and date is CONFIRMED");
        string now = Now();
        Operation op;
        if (existing != null)
        {
            Exec(c, t, "UPDATE operations SET invoice_ref=$i, tx_id=$tx, pid=$p, process_start=$ps, updated_at=$n WHERE op_id=$id",
                ("$i", invoiceRef), ("$tx", txId), ("$p", pid), ("$ps", processStart), ("$n", now), ("$id", existing.OpId));
            Event(c, t, existing.OpId, "prepared_again", $"{invoiceRef} {txId} pid {pid}");
            op = ReadOp(c, t, "op_id=$id", ("$id", existing.OpId))!;
        }
        else
        {
            string id = "OP-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
            Exec(c, t, "INSERT INTO operations VALUES($id,$k,$a,$i,$tx,$amt,$cur,$d,$p,$ps,'NOT_STARTED',NULL,0,$n,$n)",
                ("$id", id), ("$k", key), ("$a", account.Trim()), ("$i", invoiceRef), ("$tx", txId), ("$amt", cents), ("$cur", currency),
                ("$d", payDate), ("$p", pid), ("$ps", processStart), ("$n", now));
            Event(c, t, id, "prepared", $"{invoiceRef} {txId} pid {pid}");
            op = ReadOp(c, t, "op_id=$id", ("$id", id))!;
        }
        t.Commit();
        return op;
    }

    /// <summary>Issued by the operator CLI only. The MCP server has no code path that calls this.</summary>
    public string IssueApproval(string opId, string txId, long cents, string currency, int pid, TimeSpan ttl)
    {
        using var c = Open();
        if (ReadOp(c, null, "op_id=$id", ("$id", opId)) == null) throw new Refusal("OPERATION_NOT_FOUND", $"no operation {opId}");
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow;
        Exec(c, null, "INSERT INTO approvals VALUES($h,$op,$tx,$amt,$cur,$pid,$i,$e,NULL,NULL)",
            ("$h", Hash(token)), ("$op", opId), ("$tx", txId), ("$amt", cents), ("$cur", currency), ("$pid", pid), ("$i", now.ToString("O")), ("$e", now.Add(ttl).ToString("O")));
        Event(c, null, opId, "approval_issued", $"bound to {txId} {cents} {currency} pid {pid}, expires {now.Add(ttl):O}");
        return token;
    }

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// One transaction: check the approval against the operation, consume it, and move the operation to IN_FLIGHT.
    /// If anything fails, nothing is consumed and no input may be sent.
    /// </summary>
    public Operation Reserve(string opId, string? token, string note)
    {
        using var c = Open();
        using var t = c.BeginTransaction();
        var op = ReadOp(c, t, "op_id=$id", ("$id", opId)) ?? throw new Refusal("OPERATION_NOT_FOUND", $"no operation {opId}");
        if (string.IsNullOrWhiteSpace(token)) throw new Refusal("APPROVAL_REQUIRED", "write tools need a single-use human approval token");
        string h = Hash(token);
        string? aOp = null, aTx = null, aCur = null, used = null, exp = null; long aAmt = 0; int aPid = 0;
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = t;
            cmd.CommandText = "SELECT op_id,tx_id,amount_cents,currency,pid,expires_at,used_at FROM approvals WHERE token_hash=$h";
            cmd.Parameters.AddWithValue("$h", h);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new Refusal("APPROVAL_UNKNOWN", "this token was never issued");
            aOp = r.GetString(0); aTx = r.GetString(1); aAmt = r.GetInt64(2); aCur = r.GetString(3); aPid = r.GetInt32(4); exp = r.GetString(5); used = r.IsDBNull(6) ? null : r.GetString(6);
        }
        if (used != null) throw new Refusal("APPROVAL_ALREADY_USED", $"token already consumed at {used}");
        if (DateTimeOffset.Parse(exp!) < DateTimeOffset.UtcNow) throw new Refusal("APPROVAL_EXPIRED", $"token expired at {exp}");
        if (aOp != op.OpId) throw new Refusal("APPROVAL_MISMATCH", $"token was issued for operation {aOp}, not {op.OpId}");
        if (aTx != op.TxId) throw new Refusal("APPROVAL_MISMATCH", $"token was issued for row {aTx}, the operation targets {op.TxId}");
        if (aAmt != op.AmountCents) throw new Refusal("APPROVAL_MISMATCH", $"token was issued for {aAmt} cents, the operation is {op.AmountCents} cents");
        if (aCur != op.Currency) throw new Refusal("APPROVAL_MISMATCH", $"token was issued in {aCur}, the operation is in {op.Currency}");
        if (aPid != op.Pid) throw new Refusal("APPROVAL_MISMATCH", $"token was issued for process {aPid}, the operation is bound to {op.Pid}");
        if (op.State != States.NotStarted) throw new Refusal("OPERATION_NOT_STARTABLE", $"operation is {op.State}");
        var blocking = ReadOp(c, t, "upper(account)=$a AND state IN ('UNKNOWN','IN_FLIGHT') AND op_id<>$id", ("$a", op.Account.ToUpperInvariant()), ("$id", op.OpId));
        if (blocking != null) throw new Refusal("ACCOUNT_BLOCKED_UNKNOWN", $"operation {blocking.OpId} on this account is {blocking.State}");
        string now = Now();
        Exec(c, t, "UPDATE approvals SET used_at=$n, used_note=$note WHERE token_hash=$h AND used_at IS NULL", ("$n", now), ("$note", note), ("$h", h));
        Exec(c, t, "UPDATE operations SET state='IN_FLIGHT', reason='intent recorded before the write input', write_input_sent=0, updated_at=$n WHERE op_id=$id", ("$n", now), ("$id", op.OpId));
        Event(c, t, op.OpId, "reserved", note);
        if (Environment.GetEnvironmentVariable("PGA_FAULT_INTENT_WRITE") == "1")
            throw new IOException("fault injected: the intent journal could not be written");
        t.Commit();
        return ReadOp(c, null, "op_id=$id", ("$id", op.OpId))!;
    }

    public void MarkWriteInputSent(string opId) { using var c = Open(); Exec(c, null, "UPDATE operations SET write_input_sent=1, updated_at=$n WHERE op_id=$id", ("$n", Now()), ("$id", opId)); Event(c, null, opId, "write_input_sent", "the submit input was sent"); }

    public void Confirm(string opId, string reason) => SetState(opId, States.Confirmed, reason, null);
    public void MarkUnknown(string opId, string reason) => SetState(opId, States.Unknown, reason, null);

    /// <summary>Only possible while the code knows no write input was sent. Screen observations never call this.</summary>
    public void RevertBeforeWrite(string opId, string reason)
    {
        var op = Get(opId);
        if (op.WriteInputSent) throw new InvalidOperationException("write input already sent: cannot revert to NOT_STARTED");
        SetState(opId, States.NotStarted, "stopped before any write input: " + reason, States.InFlight);
    }

    public void Reconcile(string opId, string resolution, string note)
    {
        var op = Get(opId);
        if (op.State != States.Unknown) throw new Refusal("NOT_UNKNOWN", $"operation is {op.State}; only UNKNOWN operations are reconciled");
        string to = resolution switch { "confirmed" => States.Confirmed, "not-applied" => States.NotStarted, _ => throw new Refusal("BAD_RESOLUTION", "use confirmed or not-applied") };
        using var c = Open();
        Exec(c, null, "UPDATE operations SET state=$s, reason=$r, write_input_sent=0, updated_at=$n WHERE op_id=$id", ("$s", to), ("$r", "human reconciliation: " + note), ("$n", Now()), ("$id", opId));
        Event(c, null, opId, "human_reconciled", $"{resolution}: {note}");
    }

    void SetState(string opId, string state, string reason, string? expectedFrom)
    {
        using var c = Open();
        int n = Exec(c, null, "UPDATE operations SET state=$s, reason=$r, updated_at=$n WHERE op_id=$id" + (expectedFrom != null ? " AND state=$f" : ""),
            ("$s", state), ("$r", reason), ("$n", Now()), ("$id", opId), ("$f", expectedFrom));
        Event(c, null, opId, "state_" + state.ToLowerInvariant(), reason);
        if (n != 1) throw new InvalidOperationException($"state change to {state} did not apply");
    }

    public List<Operation> List()
    {
        using var c = Open();
        var all = new List<Operation>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT op_id FROM operations ORDER BY created_at";
        using var r = cmd.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        foreach (var id in ids) all.Add(ReadOp(c, null, "op_id=$id", ("$id", id))!);
        return all;
    }

    public List<string> History(string opId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT at,kind,detail FROM events WHERE op_id=$id ORDER BY id";
        cmd.Parameters.AddWithValue("$id", opId);
        using var r = cmd.ExecuteReader();
        var l = new List<string>();
        while (r.Read()) l.Add($"{r.GetString(0)} {r.GetString(1)} {(r.IsDBNull(2) ? "" : r.GetString(2))}");
        return l;
    }
}
