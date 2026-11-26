using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Revit.Tools;

/// <summary>revit_list_installed — installed Revit versions and whether the Bridge manifest is installed for each.</summary>
public sealed class RevitListInstalledTool : GatewayTool
{
    public override string Name => "revit_list_installed";

    public override string Description =>
        "List the Revit versions installed on this machine (registry, uninstall entries, default folders; any drive): year, Revit.exe path, " +
        "product version, add-in folders, and bridge_manifest_installed (the RevitAgent Bridge add-in manifest RevitAgent.Bridge.addin exists " +
        "for that year — install it with bridge_install). Read-only, fast. Use the year with revit_launch.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => Schema.Object()
        .Integer("year", "Only this Revit year (e.g. 2024).");

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var year = call.GetInt("year");
        var installs = RevitInstallations.Discover()
            .Where(i => year is null || i.Year == year)
            .Select(i =>
            {
                var user = Path.Combine(i.UserAddinsDir, RevitProcessService.BridgeManifestName);
                var machine = Path.Combine(i.MachineAddinsDir, RevitProcessService.BridgeManifestName);
                return new
                {
                    year = i.Year,
                    exe_path = i.ExePath,
                    install_dir = i.InstallDir,
                    product_version = i.ProductVersion,
                    dotnet = i.IsDotNetCore ? "net8" : "net48",
                    samples_dir = i.SamplesDir,
                    user_addins_dir = i.UserAddinsDir,
                    machine_addins_dir = i.MachineAddinsDir,
                    bridge_manifest_installed = File.Exists(user),
                    bridge_manifest_path = File.Exists(user) ? user : null,
                    bridge_manifest_machine_installed = File.Exists(machine),
                };
            })
            .ToArray();

        return Task.FromResult(ToolResult.Json(new { count = installs.Length, installs }));
    }
}
