using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace RevitAgent.Core.Revit;

/// <summary>One UI Automation element read from a dialog.</summary>
public sealed record UiaItem(string ControlType, string Name, string AutomationId, bool Enabled, IntPtr Hwnd);

/// <summary>
/// Reads / invokes dialog content through UI Automation with a hard time limit. UIA is only used for details
/// (TaskDialog texts and button ids, WPF modal windows) — never for detection, because UIA calls hang while
/// Revit's UI thread is busy. Each call runs on a dedicated background MTA thread that is abandoned on timeout.
/// </summary>
public static class UiaDialogReader
{
    private const int MaxItems = 120;

    /// <summary>Elements of the dialog (all descendants for TaskDialogs, buttons + texts + progress bars otherwise), or null on timeout/failure.</summary>
    public static IReadOnlyList<UiaItem>? Read(IntPtr hwnd, bool allDescendants, TimeSpan timeout, out string? note)
    {
        var (completed, value, error) = RunWithTimeout(
            () =>
            {
                using var automation = CreateAutomation(timeout);
                var root = automation.FromHandle(hwnd);
                var elements = allDescendants
                    ? root.FindAllDescendants()
                    : root.FindAllDescendants(cf => cf.ByControlType(ControlType.Button).Or(cf.ByControlType(ControlType.Text)).Or(cf.ByControlType(ControlType.ProgressBar)));
                return elements.Take(MaxItems).Select(ToItem).ToArray();
            },
            timeout);

        note = !completed ? $"UI Automation did not answer within {timeout.TotalSeconds:0.#} s (target busy?)"
            : error is not null ? $"UI Automation failed ({error.GetType().Name})"
            : null;
        return completed && error is null ? value : null;
    }

    /// <summary>
    /// Invokes the button whose AutomationId (preferred) or Name matches. Returns true when Invoke was called;
    /// <paramref name="note"/> explains failures. A timeout after the call is reported as invoked (a WPF button
    /// that opens another modal blocks Invoke until that modal closes).
    /// </summary>
    public static bool InvokeButton(IntPtr hwnd, string? automationId, string? name, TimeSpan timeout, out string? note)
    {
        var invoked = 0;
        var (completed, value, error) = RunWithTimeout(
            () =>
            {
                using var automation = CreateAutomation(timeout);
                var root = automation.FromHandle(hwnd);
                AutomationElement? target = null;
                if (!string.IsNullOrEmpty(automationId))
                {
                    target = root.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                }

                if (target is null && !string.IsNullOrEmpty(name))
                {
                    target = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                        .FirstOrDefault(e => string.Equals(Win32Windows.StripMnemonic(e.Properties.Name.ValueOrDefault), name, StringComparison.OrdinalIgnoreCase));
                }

                if (target is null)
                {
                    return "button not found through UI Automation";
                }

                var invoke = target.Patterns.Invoke.PatternOrDefault;
                if (invoke is null)
                {
                    return "button does not support the Invoke pattern";
                }

                Interlocked.Exchange(ref invoked, 1);
                invoke.Invoke();
                return null;
            },
            timeout);

        if (!completed)
        {
            note = invoked == 1 ? "Invoke did not return in time (the click may have opened another modal window)" : "UI Automation did not answer in time";
            return invoked == 1;
        }

        note = error is not null ? $"UI Automation failed ({error.GetType().Name})" : value;
        return error is null && value is null;
    }

    private static UIA3Automation CreateAutomation(TimeSpan timeout)
    {
        var automation = new UIA3Automation();
        try
        {
            automation.ConnectionTimeout = timeout;
            automation.TransactionTimeout = timeout;
        }
        catch (Exception)
        {
            // Older UIA (no IUIAutomation2): the outer thread timeout still applies.
        }

        return automation;
    }

    private static UiaItem ToItem(AutomationElement e)
    {
        string type;
        try
        {
            type = e.Properties.ControlType.ValueOrDefault.ToString();
        }
        catch (Exception)
        {
            type = "Unknown";
        }

        return new UiaItem(
            type,
            e.Properties.Name.ValueOrDefault ?? string.Empty,
            e.Properties.AutomationId.ValueOrDefault ?? string.Empty,
            // Fail closed: an unreadable IsEnabled counts as enabled, so the button's label still counts for the dialog's safety class.
            !e.Properties.IsEnabled.TryGetValue(out var enabled) || enabled,
            e.Properties.NativeWindowHandle.ValueOrDefault);
    }

    private static (bool Completed, T? Value, Exception? Error) RunWithTimeout<T>(Func<T> work, TimeSpan timeout)
    {
        T? value = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                value = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
            Name = "RevitAgent UIA dialog reader",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        var completed = thread.Join(timeout + TimeSpan.FromMilliseconds(250));
        return (completed, completed ? value : default, completed ? error : null);
    }
}
