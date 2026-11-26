using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Core;
using RevitAgent.Core.Composition;
using RevitAgent.Core.Tools;
using RevitAgent.Core.Ui;

namespace RevitAgent.Tests.Bridge;

/// <summary>Runs only when the named environment variable is set (opt-in machine-level steps).</summary>
public sealed class LiveEnvFactAttribute : FactAttribute
{
    public LiveEnvFactAttribute(string variable)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
        {
            Skip = $"Opt-in live step: set {variable}.";
        }
    }
}

/// <summary>
/// Opt-in helpers for the live verification: install the Bridge into the REAL user profile through the
/// gateway tool (REVITAGENT_LIVE_INSTALL_YEARS="2022,2024", optional REVITAGENT_LIVE_SOURCE_DIR), and snapshot
/// the top-level windows of a Revit pid you launched (REVITAGENT_LIVE_WINDOWS_PID, PNGs when REVITAGENT_LIVE_CAPTURE=1).
/// Evidence goes to REVITAGENT_LIVE_EVIDENCE_DIR.
/// </summary>
[Trait("Category", "RequiresRevit")]
public class LiveRevitHelpersTests
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [LiveEnvFact("REVITAGENT_LIVE_INSTALL_YEARS")]
    public async Task Install_Bridge_ThroughTheGatewayTool()
    {
        var years = Environment.GetEnvironmentVariable("REVITAGENT_LIVE_INSTALL_YEARS")!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
        var source = Environment.GetEnvironmentVariable("REVITAGENT_LIVE_SOURCE_DIR");
        if (!string.IsNullOrWhiteSpace(source))
        {
            // The payload folder is not an agent-facing argument (security); the user-controlled env var selects it.
            Environment.SetEnvironmentVariable("REVITAGENT_BRIDGE_PAYLOAD", source);
        }

        await using var sp = GatewayServices.BuildGateway(new AgentPaths());
        var registry = sp.GetRequiredService<ToolRegistry>();
        var args = new JsonObject { ["years"] = new JsonArray(years.Select(y => (JsonNode)y).ToArray()) };

        var install = await registry.InvokeAsync("bridge_install", JsonSerializer.SerializeToElement(args), "live-install", CancellationToken.None);
        var list = await registry.InvokeAsync("bridge_list_installs", null, "live-install", CancellationToken.None);
        Write("bridge_install.json", install);
        Write("bridge_list_installs.json", list);
        Assert.False(install.IsError, (install.Content[0] as TextContent)?.Text);
        Assert.True(install.Structured!.Value.GetProperty("ok").GetBoolean(), (install.Content[0] as TextContent)?.Text);
    }

    [LiveEnvFact("REVITAGENT_LIVE_WINDOWS_PID")]
    public void Snapshot_WindowsOfPid()
    {
        var pid = int.Parse(Environment.GetEnvironmentVariable("REVITAGENT_LIVE_WINDOWS_PID")!);
        var capture = Environment.GetEnvironmentVariable("REVITAGENT_LIVE_CAPTURE") == "1";
        var dir = Environment.GetEnvironmentVariable("REVITAGENT_LIVE_EVIDENCE_DIR");
        var label = Environment.GetEnvironmentVariable("REVITAGENT_LIVE_LABEL") ?? DateTime.Now.ToString("HHmmss");
        var windows = new List<object>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid || !IsWindowVisible(hwnd))
            {
                return true;
            }

            var title = new StringBuilder(512);
            GetWindowText(hwnd, title, title.Capacity);
            var cls = new StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            GetWindowRect(hwnd, out var r);
            string? png = null;
            if (capture && dir is not null && r.Right - r.Left > 50 && r.Bottom - r.Top > 30)
            {
                png = Path.Combine(dir, $"{label}-0x{hwnd.ToInt64():X}.png");
                WindowCapture.SavePng(hwnd, png);
            }

            windows.Add(new
            {
                hwnd = $"0x{hwnd.ToInt64():X}",
                title = title.ToString(),
                @class = cls.ToString(),
                owner = $"0x{GetWindow(hwnd, 4).ToInt64():X}",
                rect = new { r.Left, r.Top, width = r.Right - r.Left, height = r.Bottom - r.Top },
                png,
            });
            return true;
        }, IntPtr.Zero);

        var json = JsonSerializer.Serialize(new { pid, at = DateTimeOffset.Now, windows }, Pretty);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"windows-{label}.json"), json);
        }

        Console.WriteLine(json);
    }

    /// <summary>
    /// Which UIA properties make a children round trip slow on a real window (REVITAGENT_LIVE_UIA_BENCH_HWND=0x...): the same
    /// parents (first elements of a breadth-first control-view walk), one Element|Children cache request per parent, per
    /// property set. Interleaved forward/backward so warm-up does not favour one set.
    /// </summary>
    [LiveEnvFact("REVITAGENT_LIVE_UIA_BENCH_HWND")]
    public void UiaBench_ChildrenRoundTrip_PerPropertySet()
    {
        var hwnd = Convert.ToInt64(Environment.GetEnvironmentVariable("REVITAGENT_LIVE_UIA_BENCH_HWND")!.Replace("0x", string.Empty), 16);
        using var core = new UiaCore();
        var walker = core.Walker(raw: false);
        int[] minimal = { Interop.UIAutomationClient.UIA_PropertyIds.UIA_NamePropertyId, Interop.UIAutomationClient.UIA_PropertyIds.UIA_ControlTypePropertyId };
        var full = (int[])typeof(UiaCore).GetField("NodeProperties", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        int[] state =
        {
            Interop.UIAutomationClient.UIA_PropertyIds.UIA_ValueValuePropertyId, Interop.UIAutomationClient.UIA_PropertyIds.UIA_ValueIsReadOnlyPropertyId,
            Interop.UIAutomationClient.UIA_PropertyIds.UIA_ToggleToggleStatePropertyId, Interop.UIAutomationClient.UIA_PropertyIds.UIA_ExpandCollapseExpandCollapseStatePropertyId,
            Interop.UIAutomationClient.UIA_PropertyIds.UIA_SelectionItemIsSelectedPropertyId,
        };
        int[] geometry = { Interop.UIAutomationClient.UIA_PropertyIds.UIA_BoundingRectanglePropertyId, Interop.UIAutomationClient.UIA_PropertyIds.UIA_IsOffscreenPropertyId };
        var sets = new Dictionary<string, int[]>
        {
            ["minimal(2)"] = minimal,
            ["full(25)"] = full,
            ["full-minus-state"] = full.Except(state).ToArray(),
            ["full-minus-geometry"] = full.Except(geometry).ToArray(),
            ["full-minus-state-and-geometry"] = full.Except(state).Except(geometry).ToArray(),
        };
        foreach (var p in full)
        {
            sets["only+" + p] = minimal.Append(p).Distinct().ToArray();
        }

        // Parents: breadth-first over the control view with the minimal set.
        var root = core.FromHandle(hwnd);
        var parents = new List<Interop.UIAutomationClient.IUIAutomationElement> { root };
        for (var i = 0; i < parents.Count && parents.Count < 12; i++)
        {
            var cr = core.NewCacheRequest(minimal);
            cr.TreeScope = Interop.UIAutomationClient.TreeScope.TreeScope_Element | Interop.UIAutomationClient.TreeScope.TreeScope_Children;
            cr.TreeFilter = walker.condition;
            var kids = parents[i].BuildUpdatedCache(cr).GetCachedChildren();
            for (var k = 0; kids is not null && k < kids.Length && parents.Count < 12; k++)
            {
                parents.Add(kids.GetElement(k));
            }
        }

        var totals = sets.Keys.ToDictionary(k => k, _ => 0.0);
        var order = sets.Keys.ToList();
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var name in pass == 0 ? order : Enumerable.Reverse(order))
            {
                var cr = core.NewCacheRequest(sets[name]);
                cr.TreeScope = Interop.UIAutomationClient.TreeScope.TreeScope_Element | Interop.UIAutomationClient.TreeScope.TreeScope_Children;
                cr.TreeFilter = walker.condition;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var p in parents)
                {
                    try
                    {
                        p.BuildUpdatedCache(cr).GetCachedChildren();
                    }
                    catch (COMException)
                    {
                    }
                }

                totals[name] += sw.Elapsed.TotalMilliseconds;
            }
        }

        var rows = totals.Select(t => new { set = t.Key, ms_per_round_trip = Math.Round(t.Value / (2 * parents.Count), 1) }).OrderByDescending(r => r.ms_per_round_trip).ToArray();
        var json = JsonSerializer.Serialize(new { hwnd = $"0x{hwnd:X}", parents = parents.Count, at = DateTimeOffset.Now, rows }, Pretty);
        var dir = Environment.GetEnvironmentVariable("REVITAGENT_LIVE_EVIDENCE_DIR");
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "uia-bench.json"), json);
        }

        Console.WriteLine(json);
    }

    private static void Write(string name, ToolResult result)
    {
        var dir = Environment.GetEnvironmentVariable("REVITAGENT_LIVE_EVIDENCE_DIR");
        if (dir is null)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var node = result.Structured is { } s ? JsonNode.Parse(s.GetRawText()) : null;
        File.WriteAllText(Path.Combine(dir, name), JsonSerializer.Serialize(new { ok = !result.IsError, error_code = result.ErrorCode, result = node }, Pretty));
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
}
