using System.Text.Json;

namespace RevitAgent.Core.Tools;

/// <summary>One content block of a tool result.</summary>
public abstract record ToolContent;

/// <summary>Plain text or serialized JSON for the agent.</summary>
public sealed record TextContent(string Text) : ToolContent;

/// <summary>Image the agent (vision model) can look at, e.g. a window screenshot.</summary>
public sealed record ImageContent(byte[] Data, string MimeType = "image/png") : ToolContent;

/// <summary>
/// Result of a gateway tool. Most tools return one JSON object (<see cref="Json"/>);
/// observation tools may add images. Failures should be thrown as <see cref="GatewayException"/>
/// rather than built by hand, so they are formatted and audited uniformly.
/// </summary>
public sealed class ToolResult
{
    /// <summary>Maximum characters of text returned to the agent per block; longer text is truncated with a note.</summary>
    public const int MaxTextChars = 120_000;

    private readonly List<ToolContent> _content = new();

    public IReadOnlyList<ToolContent> Content => _content;

    public bool IsError { get; private init; }

    /// <summary>Error code when <see cref="IsError"/> (for audit/policy), else null.</summary>
    public string? ErrorCode { get; private init; }

    /// <summary>Structured JSON payload (also emitted as the first text block). Null for text/image-only results.</summary>
    public JsonElement? Structured { get; private set; }

    public static ToolResult Json(object? payload)
    {
        var r = new ToolResult();
        var element = GatewayJson.ToElement(payload);
        r.Structured = element;
        r._content.Add(new TextContent(Truncate(element.GetRawText())));
        return r;
    }

    public static ToolResult Text(string text)
    {
        var r = new ToolResult();
        r._content.Add(new TextContent(Truncate(text ?? string.Empty)));
        return r;
    }

    public static ToolResult Error(GatewayException ex)
    {
        var r = new ToolResult { IsError = true, ErrorCode = ex.Code };
        var element = ex.ToJson();
        r.Structured = element;
        r._content.Add(new TextContent(element.GetRawText()));
        return r;
    }

    public ToolResult AddText(string text)
    {
        _content.Add(new TextContent(Truncate(text ?? string.Empty)));
        return this;
    }

    public ToolResult AddImage(byte[] png, string mimeType = "image/png")
    {
        ArgumentNullException.ThrowIfNull(png);
        _content.Add(new ImageContent(png, mimeType));
        return this;
    }

    private static string Truncate(string s) =>
        s.Length <= MaxTextChars ? s : s[..MaxTextChars] + $"\n…[truncated {s.Length - MaxTextChars} chars; narrow the query or use limit/filters]";
}
