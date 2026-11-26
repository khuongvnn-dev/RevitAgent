namespace RevitAgent.Host.Config;

using RevitAgent.Contracts.Protocol;

/// <summary>
/// Application configuration settings for RevitAgent.
/// </summary>
public sealed class AppConfig
{
    private string _language = "vi-VN";
    private string _themeMode = "System";

    /// <summary>
    /// UI Language culture code (e.g. "vi-VN" or "en-US"). Defaults to "vi-VN".
    /// </summary>
    public string Language
    {
        get => string.IsNullOrWhiteSpace(_language) ? "vi-VN" : _language;
        set => _language = string.IsNullOrWhiteSpace(value) ? "vi-VN" : value;
    }

    /// <summary>
    /// UI Theme Mode ("System", "Dark", "Light"). Defaults to "System".
    /// </summary>
    public string ThemeMode
    {
        get => string.IsNullOrWhiteSpace(_themeMode) ? "System" : _themeMode;
        set => _themeMode = string.IsNullOrWhiteSpace(value) ? "System" : value;
    }

    private int _notificationDurationSeconds = 3;

    /// <summary>
    /// Duration in seconds before in-app toast notifications auto-dismiss (default: 3).
    /// </summary>
    public int NotificationDurationSeconds
    {
        get => _notificationDurationSeconds <= 0 ? 3 : _notificationDurationSeconds;
        set => _notificationDurationSeconds = value <= 0 ? 3 : value;
    }

    /// <summary>
    /// Highest tool permission the gateway lets AI clients use: Read, Interact, Write or Admin (default Admin: the user's own tool).
    /// Applied to <c>GatewayPolicy.MaxPermission</c> at startup and when saved from Settings.
    /// </summary>
    public string GatewayMaxPermission { get; set; } = "Admin";

    /// <summary>When true, Write tools may only touch documents inside the sandbox (runs folder). Applied to <c>GatewayPolicy.EnforceSandbox</c>.</summary>
    public bool GatewayEnforceSandbox { get; set; } = true;

    public string IpcPipeName { get; set; } = IpcConstants.DefaultMcpPipeName;
}
