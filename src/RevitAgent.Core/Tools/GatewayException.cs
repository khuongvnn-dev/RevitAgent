using System.Text.Json;
using RevitAgent.Contracts.Protocol;

namespace RevitAgent.Core.Tools;

/// <summary>
/// Expected, agent-actionable failure. The registry turns it into an MCP tool result with
/// <c>isError: true</c> and body <c>{ "code", "message", "details"? }</c>. Use <see cref="GatewayErrorCodes"/>.
/// Anything that is not a <see cref="GatewayException"/> is reported as INTERNAL without a stack trace.
/// </summary>
public sealed class GatewayException : Exception
{
    public GatewayException(string code, string message, object? details = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = string.IsNullOrWhiteSpace(code) ? GatewayErrorCodes.Internal : code;
        Details = details;
    }

    public string Code { get; }

    /// <summary>Optional structured details (serialized with System.Text.Json). Must not contain secrets.</summary>
    public object? Details { get; }

    public static GatewayException Validation(string message, object? details = null) => new(GatewayErrorCodes.ValidationFailed, message, details);

    public static GatewayException NotFound(string message, object? details = null) => new(GatewayErrorCodes.NotFound, message, details);

    public static GatewayException NotReady(string message, object? details = null) => new(GatewayErrorCodes.TargetNotReady, message, details);

    public static GatewayException Denied(string message, object? details = null) => new(GatewayErrorCodes.PermissionDenied, message, details);

    public static GatewayException Dependency(string message, object? details = null) => new(GatewayErrorCodes.DependencyFailed, message, details);

    public JsonElement ToJson()
    {
        var body = new Dictionary<string, object?> { ["code"] = Code, ["message"] = Message };
        if (Details is not null)
        {
            body["details"] = Details;
        }

        return JsonSerializer.SerializeToElement(body, GatewayJson.Options);
    }
}
