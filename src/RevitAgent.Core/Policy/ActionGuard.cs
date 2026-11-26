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
/// Read tools, tools without a process target, other applications and gateway-launched Revits are unaffected.
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

    /// <summary>Returns true when a grant allowed the call; throws PERMISSION_DENIED when it is not allowed.</summary>
    public bool Check(IGatewayTool tool, ToolCall call)
    {
        // revit_close keeps its own owner-only rule (foreign close needs force_foreign + confirm_pid).
        if (tool.Permission < ToolPermission.Interact || tool.Name == "revit_close")
        {
            return false;
        }

        var grantUsed = false;
        foreach (var pid in TargetPids(call))
        {
            var info = _probe.Get(pid);
            if (info is null || !info.IsRevit || _owned.IsOwned(pid))
            {
                continue;
            }

            if (_grants.FindActive(pid) is null)
            {
                throw GatewayException.Denied(
                    $"Tool '{tool.Name}' would act on Revit pid {pid}, which the user started (not launched by the gateway). " +
                    $"Ask the user to enable \"{DashboardSwitch}\" for it on the RevitAgent dashboard (time-limited), then retry. " +
                    "Read-only tools (revit_list_instances, ui_snapshot, model_query_elements, ...) still work without it. " +
                    "Or launch your own Revit with revit_launch (only if the user asked).",
                    new { pid, needs = "action_grant" });
            }

            grantUsed = true;
        }

        return grantUsed;
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
        services.TryAddSingleton<ActionGrantStore>();
        services.TryAddSingleton<IActionGrantProvider>(sp => sp.GetRequiredService<ActionGrantStore>());
        services.TryAddSingleton<IOwnedProcessLookup>(sp => new LaunchedLookup(sp.GetRequiredService<LaunchedProcessRegistry>()));
        services.TryAddSingleton<IWindowProcessResolver, SystemWindowProcessResolver>();
        services.TryAddSingleton<ActionGuard>();
    }

    private sealed class LaunchedLookup : IOwnedProcessLookup
    {
        private readonly LaunchedProcessRegistry _registry;

        public LaunchedLookup(LaunchedProcessRegistry registry) => _registry = registry;

        public bool IsOwned(int pid) => _registry.IsOwned(pid);
    }
}
