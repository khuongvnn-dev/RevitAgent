namespace RevitAgent.Core.Mcp;

/// <summary>
/// Server instructions sent to every MCP client at initialize: how an agent should drive Revit through this gateway.
/// Every model loads them before it starts, so they stay short (≤ 2,000 chars, see ToolProfileTests) and hold only verified
/// facts. Rules shared by several tools (safety, selector grammar, error codes) live here once; detailed recipes live in docs/agent-guide.
/// </summary>
public static class AgentInstructions
{
    /// <summary>The instructions with \n line ends, whatever line ends the source file has (git autocrlf checks it out as CRLF).</summary>
    public static string Text { get; } = Source.ReplaceLineEndings("\n");

    private const string Source = """
RevitAgent drives Autodesk Revit (any version) and its add-ins without their source.
For common tasks call macro_list / macro_run first.
Loop: observe, act, verify. Never assume an action worked.
1. revit_list_instances: pick the pid (state ready|busy|modal|not_responding). revit_launch only if the user asked.
2. A modal dialog blocks everything: revit_list_dialogs, then revit_handle_dialog for informational ones only. NEVER answer security, unsigned add-in, license, sign-in, recovery, delete, overwrite or replace dialogs: they are refused (PERMISSION_DENIED) and a human decides; ask with human_ask.
3. Observe with ui_snapshot (refs like e12, valid until the next snapshot of that window) and ui_screenshot, act with ui_click / ui_type, observe again.
Selector: key=value pairs joined by ; or spaces: id= (AutomationId, prefer it), name=, type= (Button, Edit, ListItem...), class=, index= (0-based), in=ref|selector; ~= contains, /x/ regex, plain text = name contains. Revit ribbon: tabs are Buttons id=TabName, commands id=ID_... Add-in dialogs are separate windows of the same pid.
4. Verify in the model (model_query_elements) when the Bridge is loaded (bridge_pipe_available): the UI can lie. DEPENDENCY_FAILED = no Bridge (bridge_install, restart Revit).
5. Open models only as sandbox copies: sandbox_copy_model, then doc_open the copy. Never save over an original.
6. Close or kill only a Revit the gateway launched. Another Revit may be read, but actions return PERMISSION_DENIED until the owner turns on "Let AI act on my Revit" on the dashboard: stop, ask, retry.
Errors {code, message}: TARGET_NOT_READY / MODAL_BLOCKED: observe, fix the state. BUSY: wait, retry. TIMEOUT: may still run, check state first. PERMISSION_DENIED: ask the user. SANDBOX_VIOLATION: use a sandbox copy. VALIDATION_FAILED: fix arguments.
Default profile core lists the main tools; Shim --profile full lists all (ui_find, ui_keys, ui_wait, revit_get_state, recorder_*, scenario_*...).
""";
}
