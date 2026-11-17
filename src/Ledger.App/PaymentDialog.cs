using System;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledger.App;

/// <summary>Standard WinForms controls only: this surface is fully visible to UI Automation.</summary>
public sealed class PaymentDialog : Form
{
    readonly Entry _invoice;
    readonly LedgerStore _store;
    readonly AppOptions _opt;
    readonly TextBox _amount, _date, _memo;
    readonly Label _error;
    readonly Button _post;
    bool _slowFieldDone;

    public PaymentDialog(Entry invoice, LedgerStore store, AppOptions opt)
    {
        _invoice = invoice; _store = store; _opt = opt;
        AutoScaleMode = AutoScaleMode.None;
        float s = DeviceDpi / 96f * opt.RenderScale;
        Text = "Record payment";
        Name = "paymentDialog";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size((int)(420 * s), (int)(230 * s));
        var font = new Font("Segoe UI", 13f * s, GraphicsUnit.Pixel);
        Font = font;
        int lx = (int)(16 * s), tx = (int)(130 * s), w = (int)(270 * s), y = (int)(16 * s), dy = (int)(36 * s);
        Label L(string t, int yy) { var l = new Label { Text = t, AutoSize = true, Location = new Point(lx, yy + (int)(3 * s)) }; Controls.Add(l); return l; }
        TextBox T(string name, string acc, int yy, string val, bool ro = false) { var b = new TextBox { Name = name, AccessibleName = acc, Location = new Point(tx, yy), Width = w, Text = val, ReadOnly = ro }; Controls.Add(b); return b; }
        // Flag dialog-other-invoice: the dialog comes up bound to another invoice than the selected row.
        string shownRef = opt.Flags.Contains("dialog-other-invoice") ? "INV-" + (int.Parse(invoice.Ref[4..], CultureInfo.InvariantCulture) + 1) : invoice.Ref;
        L("Invoice", y); T("invoiceBox", "Invoice", y, $"{shownRef}  {invoice.Account}", ro: true); y += dy;
        L("Amount (USD)", y).Name = "amountLabel"; _amount = T("amountBox", "Amount", y, ""); y += dy;
        L("Payment date", y); _date = T("dateBox", "Payment date", y, "2026-03-31"); y += dy;
        L("Memo", y); _memo = T("memoBox", "Memo", y, ""); y += dy;
        _error = new Label { Name = "errorLabel", AutoSize = true, ForeColor = Color.Firebrick, Location = new Point(lx, y) };
        Controls.Add(_error);
        foreach (var (box, field) in new[] { (_amount, "amount"), (_date, "date"), (_memo, "memo") })
            box.TextChanged += (_, _) => OnField(box, field);
        var post = new Button { Name = "postButton", Text = "Post payment", AccessibleName = "Post payment", Location = new Point(tx, y + (int)(20 * s)), Size = new Size((int)(130 * s), (int)(30 * s)) };
        var cancel = new Button { Name = "cancelButton", Text = "Cancel", AccessibleName = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(tx + (int)(140 * s), y + (int)(20 * s)), Size = new Size((int)(100 * s), (int)(30 * s)) };
        post.Click += OnPost;
        _post = post;
        Controls.Add(post); Controls.Add(cancel);
        AcceptButton = post; CancelButton = cancel;
    }

    static readonly Regex StrictMoney = new(@"^\d{1,3}(,\d{3})*\.\d{2}$|^\d+\.\d{2}$", RegexOptions.CultureInvariant);

    void OnField(TextBox box, string field)
    {
        _store.Journal("dialog_field_changed", new { field, text = box.Text });
        // Flag slow-amount-field: the first change of the amount blocks the UI thread for 6 s (a slow validation hook).
        if (field == "amount" && _opt.Flags.Contains("slow-amount-field") && !_slowFieldDone)
        {
            _slowFieldDone = true;
            _store.Journal("amount_field_slow", new { seconds = 6 });
            Thread.Sleep(6000);
        }
    }

    async void OnPost(object? sender, EventArgs e)
    {
        string a = _amount.Text.Trim();
        if (!StrictMoney.IsMatch(a)) { _error.Text = "Amount must look like 1,234.56"; _store.Journal("post_rejected", new { reason = "amount format", text = a }); return; }
        long cents = (long)Math.Round(decimal.Parse(a.Replace(",", ""), CultureInfo.InvariantCulture) * 100m);
        if (_opt.Flags.Contains("slow-post"))
        {
            // Flag slow-post: the save runs in the background for 8 s while the window keeps answering. Escape in the
            // meantime closes the dialog, but the save still completes: cancelling here is not a safe way out.
            _post.Enabled = false;
            _store.Journal("post_slow_started", new { invoice = _invoice.Ref });
            await Task.Delay(8000);
            _store.PostPayment(_invoice, cents, _date.Text.Trim(), _memo.Text.Trim());
            if (!IsDisposed && Visible) { DialogResult = DialogResult.OK; Close(); }
            return;
        }
        if (_opt.Flags.Contains("freeze-before-post"))
        {
            _store.Journal("freeze_before_post", new { invoice = _invoice.Ref });
            Thread.Sleep(Timeout.Infinite);
        }
        _store.PostPayment(_invoice, cents, _date.Text.Trim(), _memo.Text.Trim());
        if (_opt.Flags.Contains("freeze-after-post"))
        {
            _store.Journal("freeze_after_post", new { invoice = _invoice.Ref });
            Thread.Sleep(Timeout.Infinite);
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
