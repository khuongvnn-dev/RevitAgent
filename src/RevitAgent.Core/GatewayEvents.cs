namespace RevitAgent.Core;

/// <summary>
/// Process-wide notifications for the dashboard (and tests). Modules raise them; subscribers must be
/// cheap and must not throw (exceptions are swallowed).
/// </summary>
public sealed class GatewayEvents
{
    public event Action<string>? ClientConnected;

    public event Action<string>? ClientDisconnected;

    /// <summary>(pid, connected) — the Bridge pipe of a Revit process came up or went down.</summary>
    public event Action<int, bool>? BridgeStateChanged;

    /// <summary>Free-form status line for the dashboard ("Revit 2024 (pid 1234) started", ...).</summary>
    public event Action<string, string>? Status;

    /// <summary>Decision made on an unsigned add-in security prompt.</summary>
    public event Action<AddinTrustEvent>? AddinTrust;

    public void RaiseClientConnected(string sessionId) => Safe(() => ClientConnected?.Invoke(sessionId));

    public void RaiseClientDisconnected(string sessionId) => Safe(() => ClientDisconnected?.Invoke(sessionId));

    public void RaiseBridgeStateChanged(int pid, bool connected) => Safe(() => BridgeStateChanged?.Invoke(pid, connected));

    public void RaiseStatus(string category, string message) => Safe(() => Status?.Invoke(category, message));

    public void RaiseAddinTrust(AddinTrustEvent e) => Safe(() => AddinTrust?.Invoke(e));

    private static void Safe(Action a)
    {
        try
        {
            a();
        }
        catch
        {
            // Never let a UI subscriber break the gateway.
        }
    }
}

/// <summary>Event raised when an allowlist decision is made on an unsigned add-in.</summary>
public sealed record AddinTrustEvent(int Pid, string Name, string Location, string Decision, string? ScreenshotPath);
