using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RevitAgent.Core.Bridge;
using RevitAgent.Core.Composition;
using RevitAgent.Core.Revit;
using RevitAgent.Core.Tools;
using RevitAgent.Core.Ui;

namespace RevitAgent.Core.Policy;

/// <summary>Which processes the gateway launched (fail closed: unknown = not owned).</summary>
public interface IOwnedProcessLookup
{
    bool IsOwned(int pid);
}

/// <summary>Resolves the process a <c>window</c> / <c>hwnd</c> targeting spec points at (null when it resolves to nothing).</summary>
public interface IWindowProcessResolver
{
    int? ProcessOf(WindowSpec spec);
}

/// <summary>Resolves specs with the same resolver the ui_* tools use.</summary>
public sealed class SystemWindowProcessResolver : IWindowProcessResolver
{
    public int? ProcessOf(WindowSpec spec)
    {
        try
        {
            return spec.Resolve().ProcessId;
        }
        catch (GatewayException)
        {
            return null; // the tool itself fails with NOT_FOUND / VALIDATION_FAILED before acting
        }
    }
}

/// <summary>
/// Central action guard (called from <see cref="ToolRegistry"/>): a tool with permission &gt;= Interact that targets a
/// Revit process the gateway did NOT launch needs an active <see cref="ActionGrant"/> set by the owner on the dashboard.
/// Read tools, tools without a process target and gateway-launched Revits are unaffected here; ui_* input to other
/// applications is limited by <see cref="InputTargetGuard"/>, which also repeats this rule on the resolved target.
/// </summary>
public sealed class ActionGuard
{
    public const string DashboardSwitch = "Let AI act on my Revit";

    private readonly IActionGrantProvider _grants;
    private readonly IRevitProcessProbe _probe;
    private readonly IOwnedProcessLookup _owned;
    private readonly IWindowProcessResolver _windows;

    public ActionGuard(IActionGrantProvider grants, IRevitProcessProbe probe, IOwnedProcessLookup owned, IWindowProcessResolver windows)
    {
        _grants = grants;
        _probe = probe;
        _owned = owned;
        _windows = windows;
    }

    /// <summary>
    /// Pre-check on the call's own pid / window / hwnd arguments. Returns true when a grant allowed the call; throws
    /// PERMISSION_DENIED when it is not allowed. Tools that resolve their target later (refs, selectors, the session's
    /// current window) repeat the grant rule on the resolved process through <see cref="InputTargetGuard"/>.
    /// </summary>
    public bool Check(IGatewayTool tool, ToolCall call)
    {
        // revit_close keeps its own rule: a foreign close needs force_foreign + confirm_pid AND the owner's approval
        // (RevitProcessService.CloseAsync), and is graceful only. A grant never stands in for that approval.
        if (tool.Permission < ToolPermission.Interact || tool.Name == "revit_close")
        {
            return false;
        }

        var grantUsed = false;
        foreach (var pid in TargetPids(call))
        {
            grantUsed |= EnsureGrant(tool.Name, pid);
        }

        return grantUsed;
    }

    /// <summary>
    /// The grant rule for one target process: a Revit the gateway did not launch needs an active <see cref="ActionGrant"/>.
    /// Returns true when a grant allowed it; throws PERMISSION_DENIED when none does. Other processes and gateway-launched
    /// Revits pass (other processes are limited by <see cref="InputTargetGuard"/>).
    /// </summary>
    public bool EnsureGrant(string toolName, int pid)
    {
        var info = _probe.Get(pid);
        if (info is null || !info.IsRevit || _owned.IsOwned(pid))
        {
            return false;
        }

        if (_grants.FindActive(pid) is null)
        {
            throw GatewayException.Denied(
                $"Tool '{toolName}' would act on Revit pid {pid}, which the user started (not launched by the gateway). " +
                $"Ask the user to enable \"{DashboardSwitch}\" for it on the RevitAgent dashboard (time-limited), then retry. " +
                "Read-only tools (revit_list_instances, ui_snapshot, model_query_elements, ...) still work without it. " +
                "Or launch your own Revit with revit_launch (only if the user asked).",
                new { pid, needs = "action_grant" });
        }

        return true;
    }

    private HashSet<int> TargetPids(ToolCall call)
    {
        var pids = new HashSet<int>();
        var spec = WindowSpec.FromCall(call);
        if (spec.Pid is { } p && p > 0)
        {
            pids.Add(p);
        }

        if (spec.NamesWindow && _windows.ProcessOf(spec) is { } wp && wp > 0)
        {
            pids.Add(wp);
        }

        return pids;
    }
}

/// <summary>Registers the action-guard services (the default grant store is empty = no grant).</summary>
public sealed class PolicyModule : IGatewayModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        // A pid grant holds only while the process with that pid keeps its start time (pid reuse).
        services.TryAddSingleton(sp =>
        {
            var probe = sp.GetRequiredService<IRevitProcessProbe>();
            return new ActionGrantStore(TimeProvider.System, pid => probe.Get(pid)?.StartTime);
        });
        services.TryAddSingleton<IActionGrantProvider>(sp => sp.GetRequiredService<ActionGrantStore>());
        services.TryAddSingleton<IOwnedProcessLookup>(sp => new LaunchedLookup(sp.GetRequiredService<LaunchedProcessRegistry>()));
        services.TryAddSingleton<IWindowProcessResolver, SystemWindowProcessResolver>();
        services.TryAddSingleton<ActionGuard>();
        services.TryAddSingleton<InputTargetGuard>();
    }

    private sealed class LaunchedLookup : IOwnedProcessLookup
    {
        private readonly LaunchedProcessRegistry _registry;

        public LaunchedLookup(LaunchedProcessRegistry registry) => _registry = registry;

        public bool IsOwned(int pid) => _registry.IsOwned(pid);
    }
}
