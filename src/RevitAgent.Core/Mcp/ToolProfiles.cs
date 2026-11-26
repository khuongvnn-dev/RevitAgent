using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Mcp;

/// <summary>
/// Per-session tool profiles. A profile shapes <c>tools/list</c> (what a model must load before it starts) and MCP
/// <c>tools/call</c> refuses tools outside it (NOT_FOUND). Internal callers (macros, scenario runner, recorder, dashboard)
/// use <see cref="ToolRegistry"/> directly and are not restricted.
/// <list type="bullet">
/// <item><c>core</c> (default): the tools listed in <see cref="CoreTools"/>, small enough for 32k-context models.</item>
/// <item><c>full</c>: every registered tool.</item>
/// </list>
/// The user picks it with the Shim flag <c>--profile core|full</c> (or <see cref="EnvironmentVariable"/>; the flag wins).
/// How it reaches the session (first match wins):
/// <list type="number">
/// <item>Headless Shim: passed straight to <see cref="McpGatewayServer.RunStdioAsync"/>.</item>
/// <item>Relay Shim: one JSON-RPC notification line (<see cref="NotificationLine"/>) written to the pipe before the
/// client's bytes, which are then relayed unchanged. This works for every MCP revision: since 2026-07-28 there is no
/// <c>initialize</c> request, and client capabilities travel in each request's <c>_meta</c>, so a one-time rewrite of
/// the client's first message would not stick.</item>
/// <item>A client that declares <c>capabilities.experimental.revitagent = {"profile":"full"}</c> itself (<see cref="FromClientCapabilities"/>).</item>
/// <item>Otherwise (old Shim, direct pipe): <c>core</c>.</item>
/// </list>
/// </summary>
public static class ToolProfiles
{
    public const string Core = "core";
    public const string Full = "full";

    /// <summary>Profile for the Shim when <c>--profile</c> is not given.</summary>
    public const string EnvironmentVariable = "REVITAGENT_PROFILE";

    /// <summary>Key under <c>capabilities.experimental</c> a client may use to declare its profile.</summary>
    public const string CapabilityKey = "revitagent";

    /// <summary>Notification the relay Shim sends first: <c>{"method":"notifications/revitagent/profile","params":{"profile":"full"}}</c>.
    /// A server that does not know it ignores it (JSON-RPC notifications get no reply).</summary>
    public const string NotificationMethod = "notifications/revitagent/profile";

    /// <summary>Tools listed in the core profile. Names that are not registered (yet) are ignored.</summary>
    public static IReadOnlyList<string> CoreTools { get; } = new[]
    {
        "gateway_get_info",
        "revit_list_instances", "revit_launch", "revit_close", "revit_list_dialogs", "revit_handle_dialog",
        "sandbox_copy_model", "doc_open", "model_query_elements", "model_get_warnings", "view_activate",
        "ui_snapshot", "ui_click", "ui_type", "ui_screenshot",
        "human_ask",
        "macro_list", "macro_run",
    };

    private static readonly HashSet<string> CoreSet = new(CoreTools, StringComparer.Ordinal);

    /// <summary>"core" / "full" (case-insensitive, trimmed) → the constant; anything else → null.</summary>
    public static string? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Core => Core,
        Full => Full,
        _ => null,
    };

    /// <summary>True when <paramref name="toolName"/> appears in tools/list of <paramref name="profile"/>.</summary>
    public static bool Lists(string profile, string toolName) => profile == Full || CoreSet.Contains(toolName);

    /// <summary>The tools of <paramref name="profile"/>, sorted by name.</summary>
    public static IReadOnlyList<IGatewayTool> Filter(IEnumerable<IGatewayTool> tools, string profile) =>
        tools.Where(t => Lists(profile, t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The one-line message the relay Shim writes to the gateway pipe before relaying the client: a JSON-RPC notification
    /// (no id, so no reply) terminated by <c>\n</c>, the MCP stdio framing (newline-delimited JSON, no embedded newlines).
    /// </summary>
    public static byte[] NotificationLine(string profile) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = NotificationMethod,
            ["params"] = new JsonObject { ["profile"] = profile },
        }.ToJsonString() + "\n");

    /// <summary>The profile in the params of a <see cref="NotificationMethod"/> notification; null when missing or invalid.</summary>
    public static string? FromNotification(JsonNode? parameters) =>
        parameters is JsonObject o && o["profile"] is JsonValue v && v.TryGetValue<string>(out var s) ? Parse(s) : null;

    /// <summary>The profile a client declared in <c>capabilities.experimental.revitagent.profile</c>; null when absent or invalid.</summary>
    public static string? FromClientCapabilities(ClientCapabilities? capabilities)
    {
        if (capabilities?.Experimental is not { } experimental || !experimental.TryGetValue(CapabilityKey, out var value) || value is null)
        {
            return null;
        }

        try
        {
            var element = value is JsonElement e ? e : JsonSerializer.SerializeToElement(value, value.GetType());
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty("profile", out var p) && p.ValueKind == JsonValueKind.String
                ? Parse(p.GetString())
                : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Reads the relay Shim's profile line off the pipe BEFORE the MCP server sees it, so the session's profile is fixed before
/// any request is handled. The SDK runs notification and request handlers concurrently, so relying on the notification
/// handler alone lets the client's first tools/list race it and get <c>core</c> although the Shim announced <c>full</c>.
/// Reads up to the first <c>\n</c>: when that line is the profile notification it is consumed; otherwise every byte read so
/// far is replayed to the server unchanged (old Shim, or a client connected to the pipe directly).
/// </summary>
public static class ShimPreamble
{
    /// <summary>The notification is ~90 bytes; a first line longer than this is the client's own message.</summary>
    internal const int MaxLineBytes = 4096;

    public static async Task<(Stream Input, string? Profile)> ReadAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[512];
        var newline = -1;
        while (newline < 0 && buffer.Length < MaxLineBytes)
        {
            var n = await pipe.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break; // client closed before sending a full line
            }

            var start = (int)buffer.Length;
            buffer.Write(chunk, 0, n);
            var at = Array.IndexOf(chunk, (byte)'\n', 0, n);
            if (at >= 0)
            {
                newline = start + at;
            }
        }

        var bytes = buffer.ToArray();
        if (newline >= 0 && ProfileOf(bytes.AsSpan(0, newline)) is { } profile)
        {
            return (new PrefixedReadStream(bytes.AsMemory(newline + 1), pipe), profile);
        }

        return (new PrefixedReadStream(bytes, pipe), null);
    }

    /// <summary>The profile when <paramref name="line"/> is the Shim's <see cref="ToolProfiles.NotificationMethod"/> notification; else null.</summary>
    internal static string? ProfileOf(ReadOnlySpan<byte> line)
    {
        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(line).TrimEnd('\r')) is JsonObject o &&
                   o["method"] is JsonValue m && m.TryGetValue<string>(out var method) &&
                   method == ToolProfiles.NotificationMethod && o["id"] is null
                ? ToolProfiles.FromNotification(o["params"])
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Read-only stream that returns <c>prefix</c> first, then reads <c>inner</c>. Does not dispose <c>inner</c>.</summary>
internal sealed class PrefixedReadStream : Stream
{
    private readonly Stream _inner;
    private ReadOnlyMemory<byte> _prefix;

    public PrefixedReadStream(ReadOnlyMemory<byte> prefix, Stream inner)
    {
        _prefix = prefix;
        _inner = inner;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (TakePrefix(buffer) is var n && n > 0)
        {
            return n;
        }

        return _inner.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (TakePrefix(buffer.Span) is var n && n > 0)
        {
            return n;
        }

        return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int TakePrefix(Span<byte> destination)
    {
        if (_prefix.IsEmpty || destination.IsEmpty)
        {
            return 0;
        }

        var n = Math.Min(_prefix.Length, destination.Length);
        _prefix.Span[..n].CopyTo(destination);
        _prefix = _prefix[n..];
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>The tool profile of each live MCP session (set by <see cref="McpGatewayServer"/>, read by gateway_get_info).</summary>
public sealed class SessionProfiles
{
    private readonly ConcurrentDictionary<string, string> _profiles = new(StringComparer.Ordinal);

    /// <summary>Profile of <paramref name="sessionId"/>; <see cref="ToolProfiles.Core"/> for unknown sessions.</summary>
    public string Get(string sessionId) => _profiles.TryGetValue(sessionId, out var p) ? p : ToolProfiles.Core;

    public void Set(string sessionId, string profile) => _profiles[sessionId] = profile;

    public void Remove(string sessionId) => _profiles.TryRemove(sessionId, out _);
}
