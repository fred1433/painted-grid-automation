using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

/// <summary>
/// One intended payment. Account is the account as read on screen; AccountKey is <see cref="Identity.Fold"/> of it.
/// The business key is account key, row id (TX), amount and payment date. Row id and invoice reference never change
/// once recorded. OwnerPid and OwnerStart name the server process that holds the operation while it is IN_FLIGHT.
/// </summary>
public sealed record Operation(
    string OpId, string BusinessKey, string Account, string AccountKey, string InvoiceRef, string TxId, long AmountCents, string Currency,
    string PayDate, int Pid, string ProcessStart, string State, string? Reason, bool WriteInputSent, int? OwnerPid, string? OwnerStart,
    string CreatedAt, string UpdatedAt)
{
    public ProcessOwner? Owner => OwnerPid is int p && OwnerStart is string s ? new ProcessOwner(p, s) : null;
}

/// <summary>
/// Operation states. Reported states are NOT_STARTED, CONFIRMED and UNKNOWN. IN_FLIGHT is written durably, with its
/// owner, before the write input is sent; an IN_FLIGHT whose owner process has ended becomes UNKNOWN.
/// Nothing observed on screen ever moves an operation back to NOT_STARTED.
/// </summary>
public static class States
{
    public const string NotStarted = "NOT_STARTED", InFlight = "IN_FLIGHT", Confirmed = "CONFIRMED", Unknown = "UNKNOWN";
}

public sealed class OperationStore
{
    readonly string _cs;
    readonly ProcessOwner _me;
    public string Path { get; }
    public const int MaxApprovalSeconds = 3600;

    const string Columns = "op_id,business_key,account,account_key,invoice_ref,tx_id,amount_cents,currency,pay_date,pid,process_start,state,reason,write_input_sent,owner_pid,owner_start,created_at,updated_at";

    /// <param name="owner">The process recorded as owner of the operations this instance reserves (the current process unless a test says otherwise).</param>
    public OperationStore(string dbPath, ProcessOwner? owner = null)
    {
        _me = owner ?? ProcessOwner.Current;
        Path = System.IO.Path.GetFullPath(dbPath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _cs = new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, DefaultTimeout = 30 }.ToString();
        using var c = Open();
        Exec(c, null, @"
CREATE TABLE IF NOT EXISTS operations(
  op_id TEXT PRIMARY KEY, business_key TEXT NOT NULL, account TEXT NOT NULL, invoice_ref TEXT NOT NULL, tx_id TEXT NOT NULL,
  amount_cents INTEGER NOT NULL, currency TEXT NOT NULL, pay_date TEXT NOT NULL, pid INTEGER NOT NULL, process_start TEXT NOT NULL,
  state TEXT NOT NULL, reason TEXT, write_input_sent INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
  account_key TEXT, owner_pid INTEGER, owner_start TEXT);
CREATE TABLE IF NOT EXISTS approvals(
  token_hash TEXT PRIMARY KEY, op_id TEXT NOT NULL, tx_id TEXT NOT NULL, amount_cents INTEGER NOT NULL, currency TEXT NOT NULL,
  pid INTEGER NOT NULL, issued_at TEXT NOT NULL, expires_at TEXT NOT NULL, used_at TEXT, used_note TEXT);
CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, op_id TEXT, kind TEXT NOT NULL, detail TEXT);");
        Migrate(c);
    }

    /// <summary>Databases written by the first version lack the account key and the owner; the key is computed here, in C#.</summary>
    static void Migrate(SqliteConnection c)
    {
        var cols = new HashSet<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(operations)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) cols.Add(r.GetString(1));
        }
        foreach (var (col, type) in new[] { ("account_key", "TEXT"), ("owner_pid", "INTEGER"), ("owner_start", "TEXT") })
            if (!cols.Contains(col)) Exec(c, null, $"ALTER TABLE operations ADD COLUMN {col} {type}");
        var missing = new List<(string Id, string Account)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT op_id, account FROM operations WHERE account_key IS NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read()) missing.Add((r.GetString(0), r.GetString(1)));
        }
        foreach (var (id, acc) in missing) Exec(c, null, "UPDATE operations SET account_key=$k WHERE op_id=$id", ("$k", Identity.Fold(acc)), ("$id", id));
    }

    SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        Exec(c, null, "PRAGMA synchronous=FULL;");
        return c;
    }

    static string Now() => DateTimeOffset.UtcNow.ToString("O");
    public static string Key(string accountKey, string txId, long cents, string date) => $"{accountKey}|{txId}|{cents}|{date.Trim()}";

    static int Exec(SqliteConnection c, SqliteTransaction? t, string sql, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    static List<Operation> ReadOps(SqliteConnection c, SqliteTransaction? t, string where, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = $"SELECT {Columns} FROM operations WHERE {where} ORDER BY created_at";
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var l = new List<Operation>();
        while (r.Read())
            l.Add(new Operation(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? Identity.Fold(r.GetString(2)) : r.GetString(3), r.GetString(4), r.GetString(5),
                r.GetInt64(6), r.GetString(7), r.GetString(8), r.GetInt32(9), r.GetString(10), r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), r.GetInt64(13) != 0,
                r.IsDBNull(14) ? null : r.GetInt32(14), r.IsDBNull(15) ? null : r.GetString(15), r.GetString(16), r.GetString(17)));
        return l;
    }

    static Operation? ReadOp(SqliteConnection c, SqliteTransaction? t, string where, params (string, object?)[] p) => ReadOps(c, t, where, p).FirstOrDefault();

    static void Event(SqliteConnection c, SqliteTransaction? t, string? op, string kind, string detail) =>
        Exec(c, t, "INSERT INTO events(at,op_id,kind,detail) VALUES($a,$o,$k,$d)", ("$a", Now()), ("$o", op), ("$k", kind), ("$d", detail));

    public void Event(string? op, string kind, string detail) { using var c = Open(); Event(c, null, op, kind, detail); }

    /// <summary>
    /// Turns into UNKNOWN every IN_FLIGHT whose owner process has ended. An IN_FLIGHT whose owner is alive is left alone:
    /// that server is still between its write input and its check. Callers hold the route lock.
    /// </summary>
    public int RecoverInFlight()
    {
        using var c = Open();
        int n = 0;
        foreach (var op in ReadOps(c, null, "state='IN_FLIGHT'"))
        {
            string why = "no owner recorded (written by an earlier version)";
            if (op.Owner is { } o && o.IsAlive(out why)) continue;
            using var t = c.BeginTransaction();
            int k = Exec(c, t, "UPDATE operations SET state='UNKNOWN', reason=$r, updated_at=$n WHERE op_id=$id AND state='IN_FLIGHT' AND owner_pid IS $p AND owner_start IS $s",
                ("$r", $"found IN_FLIGHT after its server process ended ({why}): the outcome of the write input is not known"), ("$n", Now()), ("$id", op.OpId), ("$p", op.OwnerPid), ("$s", op.OwnerStart));
            if (k == 1) { Event(c, t, op.OpId, "recovered_in_flight", $"owner ended: {why}; set to UNKNOWN"); n++; }
            t.Commit();
        }
        return n;
    }

    public Operation Get(string opId)
    {
        using var c = Open();
        return ReadOp(c, null, "op_id=$id", ("$id", opId)) ?? throw new Refusal("OPERATION_NOT_FOUND", $"no operation {opId}");
    }

    static Operation? Blocking(SqliteConnection c, SqliteTransaction? t, string accountKey, string? invoiceRef, string? txId, string? exceptOp = null) =>
        ReadOp(c, t, "state IN ('UNKNOWN','IN_FLIGHT') AND (account_key=$k OR invoice_ref=$i OR tx_id=$t) AND op_id IS NOT $x",
            ("$k", accountKey), ("$i", invoiceRef), ("$t", txId), ("$x", exceptOp));

    static Refusal Blocked(Operation b) =>
        new("ACCOUNT_BLOCKED_UNKNOWN", $"operation {b.OpId} ({b.InvoiceRef} {b.TxId}, account read as '{b.Account}') is {b.State}; a human must reconcile it with the operator CLI before any new write on this account or invoice");

    /// <summary>Same account (folded), same invoice reference or same row id as an UNKNOWN or IN_FLIGHT operation.</summary>
    public Operation? BlockingOperation(string account, string? invoiceRef = null, string? txId = null)
    {
        using var c = Open();
        return Blocking(c, null, Identity.Fold(account), invoiceRef, txId);
    }

    /// <summary>
    /// Records the intention (NOT_STARTED) for the row as read on screen: account as read, its invoice reference and row id.
    /// Keyed by business context, never by a request id. An existing operation is never re-bound to another row.
    /// </summary>
    public Operation PrepareOrReuse(string accountAsRead, string invoiceRef, string txId, long cents, string currency, string payDate, int pid, string processStart)
    {
        string ak = Identity.Fold(accountAsRead);
        if (ak.Length == 0) throw new Refusal("INVALID_ARGUMENT", "the account as read is empty");
        using var c = Open();
        using var t = c.BeginTransaction();
        if (Blocking(c, t, ak, invoiceRef, txId) is { } b) throw Blocked(b);
        if (ReadOp(c, t, "tx_id=$t AND state='CONFIRMED'", ("$t", txId)) is { } done)
            throw new Refusal("ALREADY_CONFIRMED", $"operation {done.OpId} already paid {done.InvoiceRef} ({done.TxId})");
        if (ReadOp(c, t, "(tx_id=$t AND invoice_ref<>$i) OR (invoice_ref=$i AND tx_id<>$t)", ("$t", txId), ("$i", invoiceRef)) is { } clash)
            throw new Refusal("IDENTITY_CONFLICT", $"operation {clash.OpId} recorded {clash.InvoiceRef} as row {clash.TxId}; the screen now reads {invoiceRef} as row {txId}; nothing recorded");
        string key = Key(ak, txId, cents, payDate);
        var existing = ReadOp(c, t, "business_key=$k", ("$k", key));
        if (existing == null && ReadOp(c, t, "tx_id=$t AND state='NOT_STARTED'", ("$t", txId)) is { } other)
            throw new Refusal("INVOICE_HAS_OPERATION", $"operation {other.OpId} already targets {other.InvoiceRef} ({other.TxId}) for {other.AmountCents} cents on {other.PayDate}; one operation per invoice");
        string now = Now();
        Operation op;
        if (existing != null)
        {
            if (existing.State != States.NotStarted) throw new Refusal("OPERATION_NOT_STARTABLE", $"operation {existing.OpId} is {existing.State}");
            if (existing.Pid != pid || existing.ProcessStart != processStart)
            {
                Exec(c, t, "UPDATE operations SET pid=$p, process_start=$ps, updated_at=$n WHERE op_id=$id AND state='NOT_STARTED'", ("$p", pid), ("$ps", processStart), ("$n", now), ("$id", existing.OpId));
                Event(c, t, existing.OpId, "prepared_again", $"{invoiceRef} {txId}, application now pid {pid} started {processStart}");
            }
            op = ReadOp(c, t, "op_id=$id", ("$id", existing.OpId))!;
        }
        else
        {
            string id = "OP-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
            Exec(c, t, $"INSERT INTO operations({Columns}) VALUES($id,$k,$a,$ak,$i,$tx,$amt,$cur,$d,$p,$ps,'NOT_STARTED',NULL,0,NULL,NULL,$n,$n)",
                ("$id", id), ("$k", key), ("$a", accountAsRead.Trim()), ("$ak", ak), ("$i", invoiceRef), ("$tx", txId), ("$amt", cents), ("$cur", currency),
                ("$d", payDate), ("$p", pid), ("$ps", processStart), ("$n", now));
            Event(c, t, id, "prepared", $"{invoiceRef} {txId} '{accountAsRead.Trim()}' pid {pid}");
            op = ReadOp(c, t, "op_id=$id", ("$id", id))!;
        }
        t.Commit();
        return op;
    }

    /// <summary>
    /// Issued by the operator CLI only; the MCP server has no code path that calls this. The values the human typed must be
    /// those recorded for the operation, and nothing on the same account or invoice may be UNKNOWN or IN_FLIGHT.
    /// </summary>
    public string IssueApproval(string opId, string txId, long cents, string currency, int pid, TimeSpan ttl)
    {
        if (ttl < TimeSpan.FromSeconds(1) || ttl > TimeSpan.FromSeconds(MaxApprovalSeconds))
            throw new Refusal("INVALID_ARGUMENT", $"an approval lives between 1 and {MaxApprovalSeconds} seconds");
        using var c = Open();
        using var t = c.BeginTransaction();
        var op = ReadOp(c, t, "op_id=$id", ("$id", opId)) ?? throw new Refusal("OPERATION_NOT_FOUND", $"no operation {opId}");
        if (op.State != States.NotStarted) throw new Refusal("NOT_APPROVABLE", $"{op.OpId} is {op.State}; only NOT_STARTED operations are approved");
        var diff = new List<string>();
        if (txId != op.TxId) diff.Add($"row {txId} (recorded {op.TxId})");
        if (cents != op.AmountCents) diff.Add($"amount {cents} cents (recorded {op.AmountCents})");
        if (currency != op.Currency) diff.Add($"currency {currency} (recorded {op.Currency})");
        if (pid != op.Pid) diff.Add($"process {pid} (recorded {op.Pid})");
        if (diff.Count > 0) throw new Refusal("APPROVAL_VALUES_MISMATCH", $"the values typed do not match {op.OpId}: {string.Join(", ", diff)}; no token issued");
        if (Blocking(c, t, op.AccountKey, op.InvoiceRef, op.TxId, op.OpId) is { } b) throw Blocked(b);
        if (ReadOp(c, t, "tx_id=$t AND state='CONFIRMED'", ("$t", op.TxId)) is { } done) throw new Refusal("ALREADY_CONFIRMED", $"operation {done.OpId} already paid {done.TxId}");
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow;
        Exec(c, t, "INSERT INTO approvals VALUES($h,$op,$tx,$amt,$cur,$pid,$i,$e,NULL,NULL)",
            ("$h", Hash(token)), ("$op", opId), ("$tx", txId), ("$amt", cents), ("$cur", currency), ("$pid", pid), ("$i", now.ToString("O")), ("$e", now.Add(ttl).ToString("O")));
        Event(c, t, opId, "approval_issued", $"bound to {txId} {cents} {currency} pid {pid}, expires {now.Add(ttl):O}");
        t.Commit();
        return token;
    }

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// One transaction: check the approval against the operation, consume it, and move the operation to IN_FLIGHT owned by
    /// this process. If anything fails, nothing is consumed and no input may be sent. A database that cannot be written
    /// (read-only file, full disk, lock timeout) gives INTENT_NOT_PERSISTED.
    /// </summary>
    public Operation Reserve(string opId, string? token, string note)
    {
        try { return ReserveCore(opId, token, note); }
        catch (SqliteException ex) { throw new Refusal("INTENT_NOT_PERSISTED", $"the intent could not be recorded (SQLite error {ex.SqliteErrorCode}: {ex.Message}); the approval was not consumed and no input was sent"); }
        catch (IOException ex) { throw new Refusal("INTENT_NOT_PERSISTED", $"the intent could not be recorded ({ex.Message}); the approval was not consumed and no input was sent"); }
    }

    Operation ReserveCore(string opId, string? token, string note)
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
        if (Blocking(c, t, op.AccountKey, op.InvoiceRef, op.TxId, op.OpId) is { } b) throw Blocked(b);
        if (ReadOp(c, t, "tx_id=$t AND state='CONFIRMED'", ("$t", op.TxId)) is { } done) throw new Refusal("ALREADY_CONFIRMED", $"operation {done.OpId} already paid {done.TxId}");
        string now = Now();
        if (Exec(c, t, "UPDATE approvals SET used_at=$n, used_note=$note WHERE token_hash=$h AND used_at IS NULL", ("$n", now), ("$note", note), ("$h", h)) != 1)
            throw new Refusal("APPROVAL_ALREADY_USED", "token consumed by another caller");
        if (Exec(c, t, "UPDATE operations SET state='IN_FLIGHT', reason='intent recorded before the write input', write_input_sent=0, owner_pid=$op, owner_start=$os, updated_at=$n WHERE op_id=$id AND state='NOT_STARTED'",
                ("$op", _me.Pid), ("$os", _me.Start), ("$n", now), ("$id", op.OpId)) != 1)
            throw new Refusal("OPERATION_NOT_STARTABLE", "operation changed state during the reservation");
        Event(c, t, op.OpId, "reserved", $"{note}; owner pid {_me.Pid} started {_me.Start}");
        t.Commit();
        return ReadOp(c, null, "op_id=$id", ("$id", op.OpId))!;
    }

    /// <summary>Recorded just before the write input. Throws if this process no longer owns the IN_FLIGHT operation: then no input may be sent.</summary>
    public void MarkWriteInputSent(string opId)
    {
        using var c = Open();
        using var t = c.BeginTransaction();
        if (Exec(c, t, "UPDATE operations SET write_input_sent=1, updated_at=$n WHERE op_id=$id AND state='IN_FLIGHT' AND owner_pid=$p AND owner_start=$s",
                ("$n", Now()), ("$id", opId), ("$p", _me.Pid), ("$s", _me.Start)) != 1)
            throw new InvalidOperationException($"{opId} is not IN_FLIGHT under this process: the write input must not be sent");
        Event(c, t, opId, "write_input_sent", "the submit input is about to be sent");
        t.Commit();
    }

    /// <summary>IN_FLIGHT owned by this process to CONFIRMED. False (and nothing written) if the operation is no longer in that state.</summary>
    public bool Confirm(string opId, string reason) => Transition(opId, States.Confirmed, reason, "state='IN_FLIGHT' AND owner_pid=$p AND owner_start=$s", false);

    /// <summary>IN_FLIGHT owned by this process to UNKNOWN. False (and nothing written) if the operation is no longer in that state.</summary>
    public bool MarkUnknown(string opId, string reason) => Transition(opId, States.Unknown, reason, "state='IN_FLIGHT' AND owner_pid=$p AND owner_start=$s", false);

    /// <summary>Only while this process owns the IN_FLIGHT operation and the write input was not sent. Screen observations never call this.</summary>
    public bool RevertBeforeWrite(string opId, string reason) =>
        Transition(opId, States.NotStarted, "stopped before any write input: " + reason, "state='IN_FLIGHT' AND write_input_sent=0 AND owner_pid=$p AND owner_start=$s", true);

    bool Transition(string opId, string to, string reason, string condition, bool clearOwner)
    {
        using var c = Open();
        using var t = c.BeginTransaction();
        int n = Exec(c, t, $"UPDATE operations SET state=$to, reason=$r, updated_at=$n{(clearOwner ? ", owner_pid=NULL, owner_start=NULL" : "")} WHERE op_id=$id AND {condition}",
            ("$to", to), ("$r", reason), ("$n", Now()), ("$id", opId), ("$p", _me.Pid), ("$s", _me.Start));
        if (n == 1) Event(c, t, opId, "state_" + to.ToLowerInvariant(), reason);
        t.Commit();
        return n == 1;
    }

    /// <summary>
    /// Human reconciliation (operator CLI): an UNKNOWN operation, or an IN_FLIGHT one whose owner process has ended.
    /// An IN_FLIGHT whose owner is running is refused: that server has not finished.
    /// </summary>
    public void Reconcile(string opId, string resolution, string note)
    {
        string to = resolution switch { "confirmed" => States.Confirmed, "not-applied" => States.NotStarted, _ => throw new Refusal("BAD_RESOLUTION", "use confirmed or not-applied") };
        var op = Get(opId);
        string from = op.State;
        if (op.State == States.InFlight)
        {
            if (op.Owner is { } o && o.IsAlive(out var why)) throw new Refusal("OWNER_ALIVE", $"operation is IN_FLIGHT and its server is running ({why}); wait for its outcome or stop that server first");
        }
        else if (op.State != States.Unknown) throw new Refusal("NOT_UNKNOWN", $"operation is {op.State}; only UNKNOWN operations, or IN_FLIGHT ones whose server has ended, are reconciled");
        using var c = Open();
        using var t = c.BeginTransaction();
        int n = Exec(c, t, "UPDATE operations SET state=$s, reason=$r, write_input_sent=0, owner_pid=NULL, owner_start=NULL, updated_at=$n WHERE op_id=$id AND state=$f AND owner_pid IS $op",
            ("$s", to), ("$r", "human reconciliation: " + note), ("$n", Now()), ("$id", opId), ("$f", from), ("$op", op.OwnerPid));
        if (n != 1) throw new Refusal("STATE_CHANGED", $"{opId} changed state while being reconciled; read it again");
        Event(c, t, opId, "human_reconciled", $"{from} -> {to} ({resolution}): {note}");
        t.Commit();
    }

    public List<Operation> List()
    {
        using var c = Open();
        return ReadOps(c, null, "1=1");
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
