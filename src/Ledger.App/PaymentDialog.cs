using System;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
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
        L("Invoice", y); T("invoiceBox", "Invoice", y, $"{invoice.Ref}  {invoice.Account}", ro: true); y += dy;
        L("Amount (USD)", y); _amount = T("amountBox", "Amount", y, ""); y += dy;
        L("Payment date", y); _date = T("dateBox", "Payment date", y, "2026-03-31"); y += dy;
        L("Memo", y); _memo = T("memoBox", "Memo", y, ""); y += dy;
        _error = new Label { Name = "errorLabel", AutoSize = true, ForeColor = Color.Firebrick, Location = new Point(lx, y) };
        Controls.Add(_error);
        var post = new Button { Name = "postButton", Text = "Post payment", AccessibleName = "Post payment", Location = new Point(tx, y + (int)(20 * s)), Size = new Size((int)(130 * s), (int)(30 * s)) };
        var cancel = new Button { Name = "cancelButton", Text = "Cancel", AccessibleName = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(tx + (int)(140 * s), y + (int)(20 * s)), Size = new Size((int)(100 * s), (int)(30 * s)) };
        post.Click += OnPost;
        Controls.Add(post); Controls.Add(cancel);
        AcceptButton = post; CancelButton = cancel;
    }

    static readonly Regex StrictMoney = new(@"^\d{1,3}(,\d{3})*\.\d{2}$|^\d+\.\d{2}$", RegexOptions.CultureInvariant);

    void OnPost(object? sender, EventArgs e)
    {
        string a = _amount.Text.Trim();
        if (!StrictMoney.IsMatch(a)) { _error.Text = "Amount must look like 1,234.56"; _store.Journal("post_rejected", new { reason = "amount format", text = a }); return; }
        long cents = (long)Math.Round(decimal.Parse(a.Replace(",", ""), CultureInfo.InvariantCulture) * 100m);
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
