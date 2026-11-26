using System.Globalization;
using System.Text.Json.Nodes;

namespace RevitAgent.E2E;

/// <summary>
/// One tool call per process, for driving Revit step by step from a shell (demos, manual exploration):
/// <code>RevitAgent.E2E.exe call &lt;tool&gt; [&lt;json-args&gt; | @file | -] [--evidence &lt;dir&gt;] [--shim &lt;exe&gt;] [--timeout-min &lt;n&gt;] [--headless]</code>
/// Prints the full text result and saves images under the evidence folder (default: the evidence\ folder of a new
/// gateway run, scenario e2e-call-&lt;tool&gt;, printed as "run folder:"; --evidence &lt;dir&gt; uses &lt;dir&gt;\&lt;time&gt;-&lt;tool&gt; instead). Gateway state (launched pids, sandbox
/// runs) lives in the Host, so consecutive calls share it.
/// </summary>
internal static class CallMode
{
    public static async Task<int> RunAsync(string[] a)
    {
        if (a.Length == 0)
        {
            Console.Error.WriteLine("usage: call <tool> [<json-args> | @file | -] [--evidence <dir>] [--shim <exe>] [--timeout-min <n>] [--headless]");
            Console.Error.WriteLine("evidence defaults to a new run folder in the gateway run store (%REVITAGENT_HOME%\\runs or %LOCALAPPDATA%\\RevitAgent\\runs).");
            return 2;
        }

        string Get(string name, string def)
        {
            var i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8; // room names like "Café" (default console code page mangles them)
        var tool = a[0];
        var json = "{}";
        if (a.Length > 1 && !a[1].StartsWith("--", StringComparison.Ordinal))
        {
            json = a[1] == "-" ? await Console.In.ReadToEndAsync()
                : a[1].StartsWith('@') ? await File.ReadAllTextAsync(a[1][1..])
                : a[1];
        }

        // Windows PowerShell 5.1 pipes text with a UTF-8 BOM ('{...}' | E2E.exe call x -).
        json = json.TrimStart('﻿');
        if (JsonNode.Parse(json) is not JsonObject args)
        {
            Console.Error.WriteLine("arguments must be a JSON object");
            return 2;
        }

        var shim = Path.GetFullPath(Get("--shim", Path.Combine("src", "RevitAgent.Shim", "bin", "Debug", "net10.0-windows", "RevitAgent.Shim.exe")));
        var explicitDir = Get("--evidence", string.Empty);
        var ev = EvidenceRun.Create(
            string.IsNullOrWhiteSpace(explicitDir) ? null : Path.Combine(explicitDir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{tool}"),
            $"e2e-call-{tool}",
            $"E2E call {tool}");
        var timeout = TimeSpan.FromMinutes(double.Parse(Get("--timeout-min", "6"), CultureInfo.InvariantCulture));

        CallOutcome r;

        // --headless: the Shim runs the gateway in-process instead of relaying to the dashboard.
        await using (var s = await McpSession.ConnectAsync(shim, ev.EvidenceDir, a.Contains("--headless") ? new[] { "--headless", "--profile", "full" } : new[] { "--profile", "full" }))
        {
            s.Quiet = true;
            r = await s.CallAsync(tool, args, timeout: timeout);
        }

        ev.Finish(r.Ok);
        Console.WriteLine($"{(r.Ok ? "OK" : "ERROR")} {tool} ({r.Ms} ms)");
        Console.WriteLine(r.Text ?? "(no text)");
        foreach (var img in r.Images)
        {
            Console.WriteLine($"image: {Path.GetFullPath(img)}");
        }

        return r.Ok ? 0 : 1;
    }
}
