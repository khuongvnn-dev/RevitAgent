using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Revit.Tools;

/// <summary>addin_allowlist_get — read-only list of the add-ins the owner trusts. There is deliberately no write tool.</summary>
public sealed class AddinAllowlistGetTool : GatewayTool
{
    private readonly AddinAllowlist _allowlist;

    // Depends on the READ-ONLY type on purpose (AddinAllowlistStore is dashboard-only).
    public AddinAllowlistGetTool(AddinAllowlist allowlist) => _allowlist = allowlist;

    public override string Name => "addin_allowlist_get";

    public override string Description =>
        "List the add-in allowlist: add-ins (name + path pattern) the owner trusts. When a Revit that THIS gateway launched shows the " +
        "'Security - Unsigned Add-In' prompt, the gateway itself presses 'Load Once' for a listed add-in and 'Do Not Load' for any other " +
        "(logged as audit tool 'addin_trust'); you can never answer that dialog. The list is edited only by the owner on the dashboard " +
        "(page 'Allowed add-ins'): there is no tool to change it — if your add-in is missing, ask the user to add it. " +
        "Returns {path, error, count, entries[{id, name, path_pattern, enabled, note, added_at}]}; error is set when the file is unreadable " +
        "(then the gateway presses nothing). Read-only.";

    public override ToolPermission Permission => ToolPermission.Read;

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var snap = _allowlist.Load();
        return Task.FromResult(ToolResult.Json(new
        {
            path = _allowlist.FilePath,
            error = snap.Error,
            count = snap.Entries.Count,
            entries = snap.Entries.Select(e => new
            {
                id = e.Id,
                name = e.Name,
                path_pattern = e.PathPattern,
                enabled = e.Enabled,
                note = e.Note,
                added_at = e.AddedAt,
            }).ToArray(),
        }));
    }
}
