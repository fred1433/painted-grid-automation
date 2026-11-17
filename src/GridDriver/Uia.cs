using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace GridDriver;

/// <summary>UI Automation calls, each behind the WM_NULL responsiveness guard and a hard timeout.</summary>
public static class Uia
{
    public static T Call<T>(TargetWindow t, IntPtr window, string what, Func<T> f, Decision d, int timeoutMs = 4000)
    {
        if (!t.IsResponsive(window))
            throw new StopException(Codes.AppNotResponding, $"UIA call '{what}' skipped: the window did not answer WM_NULL within 500 ms; it is read by screen capture only", d);
        var task = Task.Run(f);
        if (!task.Wait(timeoutMs))
            throw new StopException(Codes.AppNotResponding, $"UIA call '{what}' did not return within {timeoutMs} ms", d);
        d.Via("UI Automation");
        return task.Result;
    }

    public static AutomationElement? ById(IntPtr root, string id) =>
        AutomationElement.FromHandle(root).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    public static Rectangle Rect(AutomationElement e)
    {
        var r = e.Current.BoundingRectangle;
        return new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
    }

    public static string GetValue(AutomationElement e) => ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
    public static void SetValue(AutomationElement e, string v) => ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).SetValue(v);
}
