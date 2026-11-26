using System.Text.Json;
using System.Text.Json.Nodes;
using RevitAgent.Contracts.Bridge;
using RevitAgent.Core.Bridge;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Install;

internal static class InstallToolArgs
{
    public static JsonObject YearsSchema(string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new JsonObject { ["type"] = "integer", ["minimum"] = 2022, ["maximum"] = 2026 },
    };

    /// <summary>The owner question for this call (null years = every installed Revit).</summary>
    public static OwnerApprovalRequest Approval(ToolCall call, BridgeInstaller installer, IReadOnlyList<int>? years)
    {
        IReadOnlyList<int> shown = years ?? Array.Empty<int>();
        if (years is not { Count: > 0 })
        {
            try
            {
                shown = installer.InstalledYears();
            }
            catch (Exception)
            {
                shown = Array.Empty<int>();
            }
        }

        return new OwnerApprovalRequest(call.Name, call.SessionId, shown.Distinct().OrderBy(y => y).ToArray(), OwnerApprovalRequest.DefaultTimeout);
    }

    public static IReadOnlyList<int>? Years(ToolCall call)
    {
        if (call.Raw("years") is not { } raw)
        {
            return null;
        }

        var items = raw.ValueKind == JsonValueKind.Array ? raw.EnumerateArray().ToArray() : new[] { raw };
        var years = new List<int>();
        foreach (var item in items)
        {
            int year;
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n))
            {
                year = n;
            }
            else if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out var s))
            {
                year = s;
            }
            else
            {
                throw GatewayException.Validation("'years' must be an array of Revit years, e.g. [2022, 2024].");
            }

            if (year is < 2022 or > 2026)
            {
                throw GatewayException.Validation($"Revit {year} is not supported by the Bridge (2022-2026).");
            }

            years.Add(year);
        }

        return years;
    }
}

/// <summary>bridge_list_installs: per installed Revit year, is the Bridge installed, trusted, up to date and loaded.</summary>
public sealed class BridgeListInstallsTool : GatewayTool
{
    private readonly BridgeInstaller _installer;
    private readonly BridgeClient _client;

    public BridgeListInstallsTool(BridgeInstaller installer, BridgeClient client)
    {
        _installer = installer;
        _client = client;
    }

    public override string Name => "bridge_list_installs";

    public override string Description =>
        "Show the RevitAgent Bridge installation for every Revit version installed on this machine: manifest path, installed assembly and stamp, " +
        "trusted (pre-approved so Revit shows no 'Unsigned Add-In' prompt), available build and up_to_date, legacy v1 manifests, and running Revit pids with bridge_loaded. " +
        "Read-only. Use bridge_install to install or update.";

    public override ToolPermission Permission => ToolPermission.Read;

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var installs = _installer.List();
        var running = _installer.Environment.RunningRevit();
        return Task.FromResult(ToolResult.Json(new
        {
            install_root = _installer.Environment.InstallRoot,
            payload_roots = _installer.Environment.PayloadRoots(),
            installs,
            running = running.Select(p => new { pid = p.Pid, year = p.Year, bridge_loaded = _client.IsPipePresent(p.Pid) }).ToArray(),
        }));
    }
}

/// <summary>bridge_install: install/update the Bridge for Revit years.</summary>
public sealed class BridgeInstallTool : GatewayTool
{
    private readonly BridgeInstaller _installer;
    private readonly IOwnerApproval _approval;

    public BridgeInstallTool(BridgeInstaller installer, IOwnerApproval approval)
    {
        _installer = installer;
        _approval = approval;
    }

    // The owner has up to OwnerApprovalRequest.DefaultTimeout to answer before the install starts.
    public override TimeSpan Timeout => OwnerApprovalRequest.DefaultTimeout + TimeSpan.FromSeconds(60);

    public override string Name => "bridge_install";

    public override string Description =>
        "Install or update the RevitAgent Bridge add-in for the given Revit years (default: every installed Revit 2022-2026). " +
        "Copies the matching build into %LOCALAPPDATA%\\RevitAgent\\bridge\\R<yy>\\<version-hash>\\ (side by side, so a copy locked by a running Revit is never overwritten), " +
        "writes %APPDATA%\\Autodesk\\Revit\\Addins\\<year>\\RevitAgent.Bridge.addin, pre-trusts the Bridge's own AddInId so Revit does not show the 'Unsigned Add-In' prompt, " +
        "and removes the retired v1 manifest. A running Revit loads it only after a restart (see restart_required_pids). Per-year results; failures do not stop other years. " +
        "Requires the machine owner's approval (dashboard or Telegram); without it the call fails with PERMISSION_DENIED.";

    public override ToolPermission Permission => ToolPermission.Admin;

    protected override Schema BuildSchema() => Schema.Object()
        .Raw("years", InstallToolArgs.YearsSchema("Revit years to install for, e.g. [2024]. Default: all installed versions."));

    // Security (review 2026-11-20): the payload folder is deliberately NOT an agent-facing argument. Installing
    // pre-trusts the DLL (no "Unsigned Add-In" prompt), so an agent-chosen folder would let it install and pre-trust
    // arbitrary code. Only the gateway's own build locations are used (REVITAGENT_BRIDGE_PAYLOAD set by the user,
    // the app folder, repo bin\Release|Debug).
    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var years = InstallToolArgs.Years(call);
        await _approval.EnsureApprovedAsync(InstallToolArgs.Approval(call, _installer, years), cancellationToken).ConfigureAwait(false);
        var results = _installer.Install(years);
        return ToolResult.Json(new
        {
            addin_id = BridgeWire.AddInId,
            results,
            ok = results.All(r => r is Dictionary<string, object?> d && d["ok"] is true),
        });
    }
}

/// <summary>bridge_uninstall: remove manifest, trust value and installed copies.</summary>
public sealed class BridgeUninstallTool : GatewayTool
{
    private readonly BridgeInstaller _installer;
    private readonly IOwnerApproval _approval;

    public BridgeUninstallTool(BridgeInstaller installer, IOwnerApproval approval)
    {
        _installer = installer;
        _approval = approval;
    }

    public override TimeSpan Timeout => OwnerApprovalRequest.DefaultTimeout + TimeSpan.FromSeconds(60);

    public override string Name => "bridge_uninstall";

    public override string Description =>
        "Uninstall the RevitAgent Bridge for the given Revit years (default: every installed Revit): removes RevitAgent.Bridge.addin (and the retired v1 manifest), " +
        "the Bridge's CodeSigning trust value, and the installed copies that are not locked by a running Revit (locked ones are listed under kept). Other add-ins are never touched. " +
        "Requires the machine owner's approval (dashboard or Telegram); without it the call fails with PERMISSION_DENIED.";

    public override ToolPermission Permission => ToolPermission.Admin;

    protected override Schema BuildSchema() => Schema.Object()
        .Raw("years", InstallToolArgs.YearsSchema("Revit years to uninstall, e.g. [2022]. Default: all installed versions."));

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var years = InstallToolArgs.Years(call);
        await _approval.EnsureApprovedAsync(InstallToolArgs.Approval(call, _installer, years), cancellationToken).ConfigureAwait(false);
        return ToolResult.Json(new { results = _installer.Uninstall(years) });
    }
}
