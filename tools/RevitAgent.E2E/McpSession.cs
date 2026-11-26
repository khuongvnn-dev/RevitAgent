using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RevitAgent.E2E;

/// <summary>
/// Black-box MCP client: talks to RevitAgent exactly like an AI client does (spawns the Shim over stdio),
/// logs every call to the evidence folder (request, response, images) and offers small assertion helpers.
/// </summary>
public sealed class McpSession : IAsyncDisposable
{
    private readonly McpClient _client;
    private readonly string _evidenceDir;
    private readonly StreamWriter _log;
    private int _seq;

    private McpSession(McpClient client, string evidenceDir)
    {
        _client = client;
        _evidenceDir = evidenceDir;
        Directory.CreateDirectory(evidenceDir);
        _log = new StreamWriter(Path.Combine(evidenceDir, "calls.md"), append: false, new UTF8Encoding(false)) { AutoFlush = true };
        _log.WriteLine($"# RevitAgent E2E — {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
    }

    public List<string> Failures { get; } = new();

    public List<string> Passes { get; } = new();

    /// <summary>No pass/fail summary and no exit code on dispose (single-call mode).</summary>
    public bool Quiet { get; set; }

    /// <summary>Called at the very end of disposal (full-run mode) with true when no check failed.</summary>
    public Action<bool>? Completed { get; set; }

    public static async Task<McpSession> ConnectAsync(string shimExe, string evidenceDir, params string[] shimArgs)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "revitagent-e2e",
            Command = shimExe,
            Arguments = shimArgs,
        });
        var client = await McpClient.CreateAsync(transport);
        return new McpSession(client, evidenceDir);
    }

    public string ServerName => _client.ServerInfo?.Name ?? "?";

    public async Task<IList<McpClientTool>> ListToolsAsync() => await _client.ListToolsAsync();

    /// <summary>Calls a tool; returns (ok, json-or-text, images saved). Never throws for tool errors.</summary>
    public async Task<CallOutcome> CallAsync(string tool, object? args = null, string? label = null, TimeSpan? timeout = null)
    {
        var n = Interlocked.Increment(ref _seq);
        var dict = args is null
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(6));
        CallToolResult result;
        try
        {
            result = await _client.CallToolAsync(tool, dict, cancellationToken: cts.Token);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.WriteLine($"## {n:00}. `{tool}` {label}\n- args: `{JsonSerializer.Serialize(args)}`\n- **TRANSPORT ERROR** after {sw.ElapsedMilliseconds} ms: {ex.Message}\n");
            return new CallOutcome(tool, false, null, ex.Message, Array.Empty<string>(), sw.ElapsedMilliseconds);
        }

        sw.Stop();
        string? text = null;
        string? allText = null;
        var images = new List<string>();
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock t:
                    text ??= t.Text;
                    allText = allText is null ? t.Text : allText + Environment.NewLine + t.Text;
                    break;
                case ImageContentBlock img:
                    var file = Path.Combine(_evidenceDir, $"{n:00}-{tool}.png");
                    await File.WriteAllBytesAsync(file, img.DecodedData.ToArray());
                    images.Add(file);
                    break;
            }
        }

        JsonNode? json = null;
        try
        {
            json = text is null ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
        }

        var ok = result.IsError != true;
        var shown = allText is null ? "(no text)" : allText.Length > 3000 ? allText[..3000] + "…" : allText;
        _log.WriteLine($"## {n:00}. `{tool}` {label}\n- args: `{JsonSerializer.Serialize(args)}`\n- {(ok ? "ok" : "**ERROR**")} in {sw.ElapsedMilliseconds} ms{(images.Count > 0 ? $", images: {string.Join(", ", images.Select(Path.GetFileName))}" : string.Empty)}\n\n```json\n{shown}\n```\n");
        File.WriteAllText(Path.Combine(_evidenceDir, $"{n:00}-{tool}.json"), text ?? string.Empty, new UTF8Encoding(false));
        if (allText is not null && allText != text)
        {
            File.WriteAllText(Path.Combine(_evidenceDir, $"{n:00}-{tool}.txt"), allText, new UTF8Encoding(false));
        }

        var outcome = new CallOutcome(tool, ok, json, allText, images, sw.ElapsedMilliseconds);
        if (outcome.DeniedByMissingGrant)
        {
            Note($"{tool} was refused with PERMISSION_DENIED because no action grant is active for this Revit. In the RevitAgent dashboard turn on \"{CallOutcome.GrantMarker}\" for this pid, then re-run.");
            if (!Failures.Contains(MissingGrantFailure))
            {
                Check(false, MissingGrantFailure);
            }
        }

        return outcome;
    }

    private const string MissingGrantFailure = "PERMISSION_DENIED: no action grant on the user's Revit - enable \"Let AI act on my Revit\" on the RevitAgent dashboard";

    public void Check(bool condition, string what)
    {
        (condition ? Passes : Failures).Add(what);
        _log.WriteLine($"> {(condition ? "✅ PASS" : "❌ FAIL")} — {what}\n");
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {what}");
    }

    public void Note(string text)
    {
        _log.WriteLine($"> {text}\n");
        Console.WriteLine($"NOTE  {text}");
    }

    public async ValueTask DisposeAsync()
    {
        if (Quiet)
        {
            await _log.DisposeAsync();
            await _client.DisposeAsync();
            return;
        }

        // Runs after every cleanup step, so the summary and exit code include them.
        Console.WriteLine();
        Console.WriteLine($"RESULT: {Passes.Count} pass, {Failures.Count} fail");
        Environment.ExitCode = Failures.Count == 0 ? 0 : 1;
        _log.WriteLine($"\n---\nResult: {Passes.Count} pass, {Failures.Count} fail\n");
        foreach (var f in Failures)
        {
            _log.WriteLine($"- FAIL: {f}");
        }

        await _log.DisposeAsync();
        await _client.DisposeAsync();
        Completed?.Invoke(Failures.Count == 0);
    }
}

public sealed record CallOutcome(string Tool, bool Ok, JsonNode? Json, string? Text, IReadOnlyList<string> Images, long Ms)
{
    /// <summary>Marker of the gateway's "owner has not enabled AI actions on this Revit" refusal (ActionGuard).</summary>
    public const string GrantMarker = "Let AI act on my Revit";

    /// <summary>PERMISSION_DENIED because the owner did not enable the action grant on the dashboard.</summary>
    public bool DeniedByMissingGrant => !Ok && (Text ?? string.Empty).Contains(GrantMarker, StringComparison.Ordinal);

    /// <summary>A real safety refusal (security dialog etc.): PERMISSION_DENIED that is NOT the missing-grant refusal.</summary>
    public bool IsSafetyRefusal => !Ok && !DeniedByMissingGrant && (Text ?? string.Empty).Contains("PERMISSION_DENIED", StringComparison.Ordinal);

    public string? Str(params string[] path)
    {
        JsonNode? n = Json;
        foreach (var p in path)
        {
            n = n?[p];
        }

        return n?.ToString();
    }
}
