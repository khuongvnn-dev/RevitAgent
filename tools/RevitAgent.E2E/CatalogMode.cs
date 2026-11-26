using System.Text;
using System.Text.Json;

namespace RevitAgent.E2E;

/// <summary>
/// Regenerates docs/agent-guide/tool-catalog.md from the live gateway exactly as an AI client sees it
/// (tools/list for descriptions and arguments, gateway_get_info for areas and permissions):
/// <code>RevitAgent.E2E.exe catalog [&lt;out.md&gt;] [--shim &lt;exe&gt;]</code>
/// </summary>
internal static class CatalogMode
{
    private const int MaxDescription = 220;

    public static async Task<int> RunAsync(string[] a)
    {
        string Get(string name, string def)
        {
            var i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
        }

        var outFile = Path.GetFullPath(a.Length > 0 && !a[0].StartsWith("--", StringComparison.Ordinal) ? a[0] : Path.Combine("docs", "agent-guide", "tool-catalog.md"));
        var shim = Path.GetFullPath(Get("--shim", Path.Combine("src", "RevitAgent.Shim", "bin", "Debug", "net10.0-windows", "RevitAgent.Shim.exe")));

        // --profile full: the catalog lists every tool, not only the default core profile.
        await using var s = await McpSession.ConnectAsync(shim, Path.Combine(Path.GetTempPath(), "revitagent-catalog"), "--headless", "--profile", "full");
        s.Quiet = true;
        var tools = (await s.ListToolsAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var info = await s.CallAsync("gateway_get_info");
        if (!info.Ok || info.Json?["tools"] is not System.Text.Json.Nodes.JsonObject groups)
        {
            Console.Error.WriteLine("gateway_get_info failed: " + info.Text);
            return 1;
        }

        var md = new StringBuilder();
        md.AppendLine("# RevitAgent tool catalog (generated from `tools/list`)");
        md.AppendLine();
        md.AppendLine($"{tools.Count} tools. Regenerate: `tools\\RevitAgent.E2E\\bin\\Debug\\net10.0-windows\\RevitAgent.E2E.exe catalog` (from the repo folder; calls `tools/list` + `gateway_get_info` through the Shim `--headless --profile full`). " +
                      "Permission: Read (observe only) · Interact (UI/commands) · Write (change models/files in the sandbox) · Admin (processes, installs). `*` = required. " +
                      $"Core = listed in the default `core` profile ({RevitAgent.Core.Mcp.ToolProfiles.CoreTools.Count(n => tools.ContainsKey(n))} tools); other tools need `--profile full` or a macro.");
        foreach (var (area, list) in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            md.AppendLine();
            md.AppendLine($"## `{area}_*`");
            md.AppendLine();
            md.AppendLine("| Tool | Core | Permission | Parameters | Description (short) |");
            md.AppendLine("|---|---|---|---|---|");
            foreach (var entry in (list?.AsArray() ?? new System.Text.Json.Nodes.JsonArray()).OrderBy(e => e?["name"]?.GetValue<string>(), StringComparer.Ordinal))
            {
                var name = entry?["name"]?.GetValue<string>() ?? "?";
                var permission = entry?["permission"]?.GetValue<string>() ?? "?";
                tools.TryGetValue(name, out var tool);
                var core = RevitAgent.Core.Mcp.ToolProfiles.Lists(RevitAgent.Core.Mcp.ToolProfiles.Core, name) ? "yes" : "";
                md.AppendLine($"| `{name}` | {core} | {permission} | {Arguments(tool?.JsonSchema)} | {Short(tool?.Description)} |");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        await File.WriteAllTextAsync(outFile, md.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"{tools.Count} tools → {outFile}");
        return 0;
    }

    private static string Arguments(JsonElement? schema)
    {
        if (schema is not { ValueKind: JsonValueKind.Object } s || !s.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
        {
            return "—";
        }

        var required = s.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(e => e.GetString()).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string?>();
        var names = props.EnumerateObject().Select(p => $"`{p.Name}`{(required.Contains(p.Name) ? "*" : string.Empty)}").ToArray();
        return names.Length == 0 ? "—" : string.Join(", ", names);
    }

    /// <summary>First sentence, one line, pipe-safe, at most <see cref="MaxDescription"/> characters.</summary>
    private static string Short(string? description)
    {
        var d = (description ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
        if (d.StartsWith('[') && d.IndexOf("] ", StringComparison.Ordinal) is var tag and > 0 and < 16)
        {
            d = d[(tag + 2)..]; // "[Read] ..." permission tag: already in its own column
        }

        var end = d.IndexOf(". ", StringComparison.Ordinal);
        if (end > 0)
        {
            d = d[..(end + 1)];
        }

        return d.Length > MaxDescription ? d[..MaxDescription] : d;
    }
}
