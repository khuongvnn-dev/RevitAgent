namespace RevitAgent.Core;

/// <summary>
/// Well-known folders of the gateway. Root defaults to <c>%LOCALAPPDATA%\RevitAgent</c> and can be
/// overridden with the <c>REVITAGENT_HOME</c> environment variable (tests use a temp folder so they
/// never touch the real user data).
/// </summary>
public sealed class AgentPaths
{
    public AgentPaths(string? root = null)
    {
        Root = root
            ?? Environment.GetEnvironmentVariable("REVITAGENT_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitAgent");
    }

    public string Root { get; }

    /// <summary>Daily JSONL audit of every tool call.</summary>
    public string AuditDir => Ensure(Path.Combine(Root, "audit"));

    /// <summary>Gateway diagnostic logs (full exception details live here, never in tool results).</summary>
    public string LogDir => Ensure(Path.Combine(Root, "logs"));

    /// <summary>Sandbox: every opened model copy and every scenario run lives under here (RUN_DIR).</summary>
    public string RunsDir => Ensure(Path.Combine(Root, "runs"));

    /// <summary>Screenshots and other evidence captured outside a scenario run.</summary>
    public string CapturesDir => Ensure(Path.Combine(Root, "captures"));

    /// <summary>User-editable rules (dialog rules, policy overrides).</summary>
    public string ConfigDir => Ensure(Path.Combine(Root, "config"));

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
