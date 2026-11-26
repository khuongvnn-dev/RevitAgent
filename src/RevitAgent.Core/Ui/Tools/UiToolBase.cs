using System.Text.Json.Nodes;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Ui.Tools;

/// <summary>
/// Base of the ui_* tools: shared targeting arguments (<c>pid</c>, <c>window</c>, <c>hwnd</c>, <c>ref</c>,
/// <c>selector</c>), environment checks and result shaping.
/// </summary>
public abstract class UiToolBase : GatewayTool
{
    /// <summary>
    /// Short selector hint for argument descriptions. The full grammar (key=value pairs: id= name= type= class= index= in=,
    /// ~= contains, /regex/, plain text = name contains) is in the server instructions (<see cref="Mcp.AgentInstructions"/>), once.
    /// </summary>
    protected const string SelectorHelp = "Selector (grammar in the server instructions), e.g. id=btnOk | name=OK type=Button | name~=Save;index=1.";

    protected UiToolBase(UiAutomationService ui) => Ui = ui;

    protected UiAutomationService Ui { get; }

    /// <summary>Adds pid / window / hwnd.</summary>
    protected static Schema Targeting(Schema s) => s
        .Integer("pid", "Process id; default window: its main one.")
        .String("window", "Window title substring, /regex/ or hwnd:0x..")
        .Raw("hwnd", new JsonObject
        {
            ["type"] = new JsonArray("string", "integer"),
            ["description"] = "Window handle.",
        });

    /// <summary>Adds ref / selector.</summary>
    protected static Schema ElementArgs(Schema s, string what = "the element") => s
        .String("ref", $"Ref of {what} (e12, from ui_snapshot).")
        .String("selector", "Or a selector, e.g. id=btnOk.");

    protected static WindowSpec Spec(ToolCall call) => WindowSpec.FromCall(call);

    protected static string? Ref(ToolCall call) => call.GetString("ref");

    protected static string? SelectorArg(ToolCall call) => call.GetString("selector");

    protected static TimeSpan Seconds(ToolCall call, string name, double defaultSeconds, double max)
    {
        var v = call.GetDouble(name, defaultSeconds) ?? defaultSeconds;
        if (v < 0)
        {
            throw GatewayException.Validation($"'{name}' must be >= 0 seconds.");
        }

        return TimeSpan.FromSeconds(Math.Min(v, max));
    }

    /// <summary>Environment checks for tools that read or drive a UI (not for pure window enumeration).</summary>
    protected static void CheckDesktop() => UiEnvironment.EnsureDesktop();

    protected static void CheckTarget(TopLevelWindow w) => UiEnvironment.EnsureNotElevatedAboveUs(w.ProcessId);

    /// <summary>Action result: outcome fields + the resolved target (selector + window) + notes.</summary>
    protected static Dictionary<string, object?> ActionResult(UiOp op, UiTarget t, Dictionary<string, object?> outcome)
    {
        var r = new Dictionary<string, object?>(outcome)
        {
            ["target"] = op.Service.TargetInfo(op, t),
        };
        if (op.Notes.Count > 0)
        {
            r["notes"] = op.Notes.ToArray();
        }

        return r;
    }

    /// <summary>
    /// Target echo when no element was resolved (e.g. it is gone): the caller's selector/ref plus the window it lives in
    /// (resolved when it still exists), so results always carry {selector, window, hwnd}.
    /// </summary>
    protected static object FallbackTarget(UiOp op, WindowSpec spec, string? selector, string? reference, TopLevelWindow? window = null)
    {
        if (window is null)
        {
            try
            {
                window = op.Service.ResolveWindow(op, spec);
            }
            catch (GatewayException)
            {
                // The window itself is gone: echo what the caller asked for.
            }
        }

        return new
        {
            selector,
            @ref = reference,
            window = window?.Title ?? spec.Window,
            hwnd = window?.HwndHex ?? (spec.Hwnd is { } h ? UiText.Hex(h) : null),
            pid = window?.ProcessId ?? spec.Pid,
            is_main_window = window is null ? (bool?)null : op.Service.IsMainWindow(window),
        };
    }

    /// <summary>Target echo for window-level calls (no element): selector null, window title + hwnd.</summary>
    protected static object WindowTarget(UiOp op, TopLevelWindow w) => new
    {
        selector = (string?)null,
        window = w.Title,
        hwnd = w.HwndHex,
        pid = w.ProcessId,
        is_main_window = op.Service.IsMainWindow(w),
    };

    protected static object WindowInfo(UiOp op, TopLevelWindow w) => new
    {
        hwnd = w.HwndHex,
        title = w.Title,
        pid = w.ProcessId,
        process = w.ProcessName,
        @class = w.ClassName,
        is_main = op.Service.IsMainWindow(w),
    };
}
