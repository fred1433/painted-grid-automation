using System.Collections.Generic;

namespace GridDriver;

/// <summary>
/// What a step decided and why. No score and no percentage: method, what was observed, how many candidates,
/// which column discriminated them, which criteria were met, and the refusal if any.
/// </summary>
public sealed class Decision
{
    public string Step { get; set; } = "";
    public List<string> Methods { get; set; } = new();
    public List<string> Observations { get; set; } = new();
    public int? Candidates { get; set; }
    public string? Discriminant { get; set; }
    public List<string> CriteriaMet { get; set; } = new();
    public string? RefusalCode { get; set; }
    public string? RefusalReason { get; set; }
    public List<string> Captures { get; set; } = new();
    public Decision(string step) { Step = step; }
    public Decision Obs(string s) { Observations.Add(s); return this; }
    public Decision Met(string s) { CriteriaMet.Add(s); return this; }
    public Decision Via(string s) { if (!Methods.Contains(s)) Methods.Add(s); return this; }
}

/// <summary>
/// Stops the route. The adapter binds one path per surface (UIA for the search box and the dialog, keyboard to open the
/// dialog, image and OCR for the grid); there is no automatic fallback to another path. NOT_AVAILABLE is a diagnostic
/// result (the path does not expose that surface); ACCESS_DENIED, PATH_FAILED and every other code stop the call.
/// </summary>
public sealed class StopException : System.Exception
{
    public string Code { get; }
    public Decision? Decision { get; }
    public StopException(string code, string message, Decision? d = null) : base(message) { Code = code; Decision = d; if (d != null) { d.RefusalCode = code; d.RefusalReason = message; } }
}

public static class Codes
{
    public const string NotAvailable = "NOT_AVAILABLE", WindowBlocked = "WINDOW_BLOCKED", AccessDenied = "ACCESS_DENIED",
        AmbiguousTarget = "AMBIGUOUS_TARGET", FocusLost = "FOCUS_LOST", AmbiguousReading = "AMBIGUOUS", NotFound = "NOT_FOUND",
        AppNotResponding = "APP_NOT_RESPONDING", WindowNotFound = "WINDOW_NOT_FOUND", IdentityChanged = "WINDOW_IDENTITY_CHANGED",
        SelectionNotObserved = "SELECTION_NOT_OBSERVED", TargetChanged = "TARGET_CHANGED", GeometryUnconfirmed = "GEOMETRY_UNCONFIRMED",
        AmountMismatch = "AMOUNT_MISMATCH", DialogNotOpened = "DIALOG_NOT_OPENED", DialogMismatch = "DIALOG_MISMATCH",
        PathFailed = "PATH_FAILED", ScanIncomplete = "SCAN_INCOMPLETE", UnexpectedError = "UNEXPECTED_ERROR";
}
