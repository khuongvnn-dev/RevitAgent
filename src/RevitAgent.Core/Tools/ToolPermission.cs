namespace RevitAgent.Core.Tools;

/// <summary>
/// Side-effect class of a gateway tool (for a UI-driving gateway).
/// Declared once on each tool; drives MCP annotations, policy checks and audit.
/// </summary>
public enum ToolPermission
{
    /// <summary>Observe only: never changes Revit, the model, files, focus or selection.</summary>
    Read = 0,

    /// <summary>Drives the application UI or Revit commands (click, type, keys, post command, activate view).
    /// May change UI state; model changes happen only through what the user-level UI does.</summary>
    Interact = 1,

    /// <summary>Changes model or files through the API (open/close document copy, write parameters, run scenario).</summary>
    Write = 2,

    /// <summary>Process-level or wide-scope actions (launch/kill Revit, install Bridge, change policy).</summary>
    Admin = 3,
}
