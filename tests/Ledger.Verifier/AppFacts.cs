using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ledger.Verifier;

public sealed record LedgerRow(string Tx, string Date, string Ref, string Account, string Type, long AmountCents, string Status, string? PaidBy);

/// <summary>Ground truth for the tests: what the application itself recorded, read after the fact.</summary>
public sealed class AppFacts
{
    public List<LedgerRow> Rows { get; } = new();
    public List<(string Evt, JsonElement Data)> Events { get; } = new();

    public static AppFacts Load(string dataDir)
    {
        var f = new AppFacts();
        string ledger = Path.Combine(dataDir, "ledger.json"), journal = Path.Combine(dataDir, "journal.jsonl");
        if (File.Exists(ledger))
            foreach (var e in JsonDocument.Parse(ReadShared(ledger)).RootElement.EnumerateArray())
                f.Rows.Add(new LedgerRow(e.GetProperty("Tx").GetString()!, e.GetProperty("Date").GetString()!, e.GetProperty("Ref").GetString()!, e.GetProperty("Account").GetString()!,
                    e.GetProperty("Type").GetString()!, e.GetProperty("AmountCents").GetInt64(), e.GetProperty("Status").GetString()!, e.TryGetProperty("PaidBy", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null));
        if (File.Exists(journal))
            foreach (var line in ReadShared(journal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var doc = JsonDocument.Parse(line).RootElement;
                f.Events.Add((doc.GetProperty("evt").GetString()!, doc.GetProperty("data").Clone()));
            }
        return f;
    }

    static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    public int Count(string evt) => Events.Count(e => e.Evt == evt);
    public int PaymentsFor(string invoiceRef) => Events.Count(e => e.Evt == "payment_posted" && e.Data.GetProperty("invoice_ref").GetString() == invoiceRef);
    public int Payments => Count("payment_posted");
    public long PaidAmount(string invoiceRef) => Events.Where(e => e.Evt == "payment_posted" && e.Data.GetProperty("invoice_ref").GetString() == invoiceRef).Select(e => e.Data.GetProperty("amount_cents").GetInt64()).FirstOrDefault();
    public int MouseSelections => Events.Count(e => e.Evt == "selection_changed" && e.Data.GetProperty("source").GetString() == "mouse");
    public string? LastSelectedTx => Events.LastOrDefault(e => e.Evt == "selection_changed").Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("tx", out var t) ? t.GetString() : null;
    public LedgerRow Invoice(string r) => Rows.Single(x => x.Ref == r);
    public List<LedgerRow> ByDate => Rows.OrderBy(r => r.Date, StringComparer.Ordinal).ThenBy(r => r.Ref, StringComparer.Ordinal).ToList();
    public List<LedgerRow> OpenInvoices => Rows.Where(r => r.Type == "Invoice" && r.Status == "Open").ToList();
}
