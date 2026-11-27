using RevitAgent.Core.Bridge;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Policy;

/// <summary>
/// The one check every input-producing UI action (ui_click / type / keys / select / toggle / expand / scroll, ui_run_script
/// steps and its dialog rules, revit_handle_dialog) passes on the process that will really receive the input, AFTER the
/// target window was resolved: refs, selectors and the session's current window resolve without a pid argument, so the
/// <see cref="ActionGuard"/> pre-check on the call's own arguments is not enough. Fails closed, in this order:
/// <list type="number">
/// <item>Never the gateway itself: this process, RevitAgent.Host / .Shim / .E2E, an executable in the gateway's own folder,
/// and any other <c>RevitAgent.*</c> process the owner did not list explicitly (an agent must never approve itself on the
/// dashboard or change its settings).</item>
/// <item>Revit.exe only, plus the owner's <see cref="GatewayPolicy.AllowedInputProcesses"/>.</item>
/// <item>A Revit the gateway did not launch needs an active action grant (<see cref="ActionGuard.EnsureGrant"/>).</item>
/// </list>
/// Read-only UI tools never call it: observing any window is allowed.
/// </summary>
public sealed class InputTargetGuard
{
    public const string RevitProcessName = "Revit";

    private const string GatewayPrefix = "RevitAgent.";

    /// <summary>Gateway executables that no allowlist entry can make reachable.</summary>
    private static readonly string[] GatewayProcessNames = { "RevitAgent.Host", "RevitAgent.Shim", "RevitAgent.E2E" };

    private readonly GatewayPolicy _policy;
    private readonly ActionGuard _actions;
    private readonly IRevitProcessProbe _probe;
    private readonly int _selfPid;
    private readonly string? _selfDir;

    public InputTargetGuard(GatewayPolicy policy, ActionGuard actions, IRevitProcessProbe probe)
        : this(policy, actions, probe, Environment.ProcessId, AppContext.BaseDirectory)
    {
    }

    /// <summary>Test seam: which pid and folder count as "this gateway process".</summary>
    public InputTargetGuard(GatewayPolicy policy, ActionGuard actions, IRevitProcessProbe probe, int selfPid, string? selfDir)
    {
        _policy = policy;
        _actions = actions;
        _probe = probe;
        _selfPid = selfPid;
        _selfDir = NormalizeDir(selfDir);
    }

    /// <summary>
    /// Throws PERMISSION_DENIED unless input may go to process <paramref name="pid"/>. <paramref name="what"/> names the action
    /// for the message (for example "ui_click"). Returns true when an action grant allowed it.
    /// </summary>
    public bool EnsureAllowed(int pid, string what)
    {
        var info = _probe.Get(pid)
            ?? throw GatewayException.NotFound($"The target process (pid {pid}) is not running; nothing was sent. Take a new ui_snapshot.");
        var name = info.ProcessName ?? string.Empty;
        if (pid == _selfPid || IsGatewayProcess(name, info.ExePath))
        {
            throw GatewayException.Denied(
                $"{what} refused: pid {pid} ({name}) is the RevitAgent gateway itself (dashboard / shim). Agents never send input to it: " +
                "approvals, grants and settings there are for the owner only.",
                new { pid, process = name, needs = "owner" });
        }

        if (!string.Equals(name, RevitProcessName, StringComparison.OrdinalIgnoreCase) && !_policy.IsInputProcessAllowed(name))
        {
            throw GatewayException.Denied(
                $"{what} refused: UI input only goes to Revit (Revit.exe); pid {pid} is '{name}'. " +
                "The owner can allow more applications in the dashboard settings. Read-only tools (ui_snapshot, ui_find, ui_inspect, ui_screenshot, ui_list_windows) still work on it.",
                new { pid, process = name, needs = "allowed_input_process" });
        }

        return _actions.EnsureGrant(what, pid);
    }

    private bool IsGatewayProcess(string name, string? exePath)
    {
        if (GatewayProcessNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (_selfDir is not null && NormalizeDir(SafeDirectory(exePath)) is { } dir && string.Equals(dir, _selfDir, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Other RevitAgent.* executables (test apps, tools) only when the owner listed that exact name.
        return name.StartsWith(GatewayPrefix, StringComparison.OrdinalIgnoreCase) && !_policy.IsInputProcessAllowed(name);
    }

    private static string? SafeDirectory(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? NormalizeDir(string? dir)
    {
        try
        {
            return string.IsNullOrWhiteSpace(dir) ? null : Path.GetFullPath(dir).TrimEnd('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
