using System.Text.Json;

namespace RevitAgent.Core.Tools;

/// <summary>
/// A tool the gateway exposes to AI agents over MCP.
///
/// Implementations are discovered automatically: any public non-abstract class implementing this
/// interface in a gateway assembly is registered as a DI singleton (constructor injection works),
/// so adding a tool never requires editing a shared registry file.
///
/// Rules:
/// - Name: lower snake_case <c>&lt;area&gt;_&lt;verb&gt;_&lt;noun&gt;</c> or <c>&lt;area&gt;_&lt;verb&gt;</c>, unique.
/// - Description: written for the agent — what it does, side effects, typical duration, common errors.
/// - Declare the real <see cref="Permission"/>. Read tools must not change anything.
/// - Throw <see cref="GatewayException"/> for expected failures.
/// - List-returning tools take a limit (≤ 100 default) and filters.
/// - Never show modal UI and never block the Revit UI thread.
/// </summary>
public interface IGatewayTool
{
    string Name { get; }

    string Description { get; }

    ToolPermission Permission { get; }

    /// <summary>JSON Schema (type: object) of the arguments. Build with <see cref="Schema"/>.</summary>
    JsonElement InputSchema { get; }

    /// <summary>Default time budget for one call. The registry cancels the token when it elapses.</summary>
    TimeSpan Timeout => TimeSpan.FromSeconds(60);

    /// <summary>Time budget for this call (default <see cref="Timeout"/>); e.g. longer when the call may wait for a human.</summary>
    TimeSpan TimeoutFor(ToolCall call) => Timeout;

    Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken);
}

/// <summary>Convenience base class: implement <see cref="IGatewayTool"/> with less boilerplate.</summary>
public abstract class GatewayTool : IGatewayTool
{
    private JsonElement? _schema;

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract ToolPermission Permission { get; }

    public JsonElement InputSchema => _schema ??= BuildSchema().Build();

    public virtual TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public virtual TimeSpan TimeoutFor(ToolCall call) => Timeout;

    public abstract Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken);

    /// <summary>Describe the arguments. Default: no arguments.</summary>
    protected virtual Schema BuildSchema() => Schema.Object();
}
