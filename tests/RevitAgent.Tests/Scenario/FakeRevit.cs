using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Composition;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Scenario;
using RevitAgent.Core.Scenario.Recorder;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Scenarios;

/// <summary>Test double: a gateway tool with a scripted handler that logs every call's arguments.</summary>
[ExcludeFromGateway]
public sealed class FakeTool : GatewayTool
{
    private readonly Func<ToolCall, object?> _handler;
    private readonly Func<Schema> _schema;
    private readonly List<JsonElement> _calls = new();

    public FakeTool(string name, ToolPermission permission, Func<ToolCall, object?> handler, Func<Schema>? schema = null)
    {
        Name = name;
        Permission = permission;
        _handler = handler;
        _schema = schema ?? (() => Schema.Object().Pid(required: false));
    }

    public override string Name { get; }

    public override string Description => "fake " + Name;

    public override ToolPermission Permission { get; }

    public IReadOnlyList<JsonElement> Calls
    {
        get
        {
            lock (_calls)
            {
                return _calls.ToArray();
            }
        }
    }

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        lock (_calls)
        {
            _calls.Add(call.Arguments.Clone());
        }

        var r = _handler(call);
        return Task.FromResult(r as ToolResult ?? ToolResult.Json(r));
    }

    protected override Schema BuildSchema() => _schema();
}

/// <summary>
/// Scripted stand-in for the Revit-facing modules (process, sandbox, Bridge, UI) so the scenario runner and recorder
/// can be tested without Revit. Behaviour knobs are public fields.
/// </summary>
public sealed class FakeRevit
{
    public const int LaunchedPid = 4242;
    public const int ExistingPid = 999;

    private readonly AgentPaths _paths;

    public FakeRevit(AgentPaths paths)
    {
        _paths = paths;
        Tools = Build().ToDictionary(t => t.Name);
    }

    public Dictionary<string, FakeTool> Tools { get; }

    public string? ActiveDoc { get; set; }

    /// <summary>doc_info reports a document outside the sandbox (opened something else).</summary>
    public bool DocInfoOutside { get; set; }

    /// <summary>revit_get_state result per call index (null → ready).</summary>
    public Func<int, object?>? State { get; set; }

    public int StateCalls { get; private set; }

    /// <summary>ui_run_script status word ("passed", "failed", "blocked").</summary>
    public string UiStatus { get; set; } = "passed";

    /// <summary>ui_list_windows shows an extra titled window after a ui step.</summary>
    public bool LeaveWindowOpen { get; set; }

    public int UiRuns { get; private set; }

    /// <summary>revit_launch starts Revit (it shows in revit_list_instances) but then fails with a timeout.</summary>
    public bool LaunchFailsAfterStart { get; set; }

    public bool LaunchStarted { get; set; }

    /// <summary>revit_close fails with this error code (null: it closes).</summary>
    public string? CloseError { get; set; }

    /// <summary>doc_open opens the document but then fails with a timeout.</summary>
    public bool DocOpenFailsAfterOpen { get; set; }

    /// <summary>doc_close answers CONFLICT (the copy is the active document) unless activate_other can switch to <see cref="UserDocument"/>.</summary>
    public bool CloseConflict { get; set; }

    /// <summary>A document of the user's that is already open (and active before the run): set <see cref="ActiveDoc"/> to it too.</summary>
    public string? UserDocument { get; set; }

    public Func<ToolCall, object?>? FlakyHandler { get; set; }

    public int FlakyCalls { get; set; }

    public Func<int, ScenarioRunner.RevitProcessLiveness>? ProcessLiveness { get; set; }

    public bool Crashed { get; set; }

    public int? ExitCode { get; set; }

    public bool Hung { get; set; }

    public string? MainWindowTitle { get; set; }

    public string? JournalPath { get; set; }

    public byte[]? Screenshot { get; set; }

    public Func<DateTimeOffset>? Now { get; set; }

    public ScenarioRunner.RevitProcessLiveness GetLiveness(int pid)
    {
        if (ProcessLiveness != null)
        {
            return ProcessLiveness(pid);
        }

        return new ScenarioRunner.RevitProcessLiveness(
            HasExited: Crashed,
            ExitCode: Crashed ? (ExitCode ?? -1) : null,
            Responding: !Hung,
            MainWindowTitle: MainWindowTitle ?? "Autodesk Revit 2024 - [Floor Plan: Level 1]");
    }

    public int Count(string tool) => Tools[tool].Calls.Count;

    public JsonElement Last(string tool) => Tools[tool].Calls[^1];

    private IEnumerable<FakeTool> Build()
    {
        yield return new FakeTool("revit_launch", ToolPermission.Admin, c =>
        {
            if (LaunchFailsAfterStart)
            {
                LaunchStarted = true;
                throw new GatewayException(GatewayErrorCodes.Timeout, "launch timed out");
            }

            return new { pid = LaunchedPid, year = c.GetInt("year") };
        },
            () => Schema.Object().Integer("year", "year", required: true));
        yield return new FakeTool("revit_close", ToolPermission.Admin, _ => CloseError is { } code ? throw new GatewayException(code, "revit_close failed: " + code) : new { closed = true }, () => Schema.Object().Pid().Boolean("discard_changes", "d"));
        yield return new FakeTool("revit_wait_ready", ToolPermission.Read, _ => new { ready = true }, () => Schema.Object().Pid().Integer("timeout_sec", "t"));
        yield return new FakeTool("revit_get_state", ToolPermission.Read, _ => State?.Invoke(StateCalls++) ?? new { state = "ready", pid = ExistingPid }, () => Schema.Object().Pid());
        yield return new FakeTool("revit_list_instances", ToolPermission.Read, _ => new { instances = new List<object> { new { pid = ExistingPid, year = 2024, launched_by_gateway = false } }.Concat(LaunchStarted ? new object[] { new { pid = LaunchedPid, year = 2024, launched_by_gateway = true } } : Array.Empty<object>()).ToArray() }, () => Schema.Object());
        yield return new FakeTool("sandbox_create_run", ToolPermission.Write, c =>
        {
            var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{c.GetString("name")}-{Guid.NewGuid().ToString("N")[..4]}";
            var path = Path.Combine(_paths.RunsDir, id);
            Directory.CreateDirectory(path);
            var prevRunId = c.GetString("previous_run_id");
            var rerunOf = c.GetString("rerun_of");
            var runObj = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = id,
                ["scenario"] = c.GetString("name") ?? "run",
                ["started_at"] = DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            };
            if (!string.IsNullOrWhiteSpace(prevRunId))
            {
                runObj["previous_run_id"] = prevRunId;
            }
            if (!string.IsNullOrWhiteSpace(rerunOf))
            {
                runObj["rerun_of"] = rerunOf;
            }
            File.WriteAllText(Path.Combine(path, "run.json"), runObj.ToJsonString());
            return new { run_id = id, path };
        }, () => Schema.Object().String("name", "n").String("previous_run_id", "p").String("rerun_of", "r"));
        yield return new FakeTool("sandbox_copy_model", ToolPermission.Write, c =>
        {
            var src = c.RequireString("source_path");
            var dst = Path.Combine(_paths.RunsDir, c.RequireString("run_id"), Path.GetFileName(src));
            File.Copy(src, dst);
            return new { copy_path = dst };
        }, () => Schema.Object().String("source_path", "s", required: true).String("run_id", "r", required: true));
        yield return new FakeTool("doc_open", ToolPermission.Write, c =>
        {
            ActiveDoc = c.RequireString("path");
            if (DocOpenFailsAfterOpen)
            {
                throw new GatewayException(GatewayErrorCodes.Timeout, "doc_open timed out");
            }

            return new { opened = true, path = ActiveDoc };
        }, () => Schema.Object().Pid().String("path", "p", required: true));
        yield return new FakeTool("doc_info", ToolPermission.Read, _ =>
        {
            if (ActiveDoc is null)
            {
                throw GatewayException.NotFound("No document is open.");
            }

            return new { title = Path.GetFileName(ActiveDoc), path = DocInfoOutside ? @"C:\Projects\original.rvt" : ActiveDoc, is_modified = false };
        }, () => Schema.Object().Pid(required: false));
        yield return new FakeTool("doc_close", ToolPermission.Write, c =>
        {
            if (CloseConflict && !(c.GetBool("activate_other") && UserDocument is not null))
            {
                throw new GatewayException(GatewayErrorCodes.Conflict, "cannot close the active document");
            }

            var target = c.GetString("path");
            if (target is not null && !string.Equals(target, ActiveDoc, StringComparison.OrdinalIgnoreCase))
            {
                throw GatewayException.NotFound("document is not open");
            }

            if (CloseConflict)
            {
                ActiveDoc = UserDocument;
                return (object)new { closed = true, activated = new { title = Path.GetFileNameWithoutExtension(UserDocument), path = UserDocument } };
            }

            ActiveDoc = null;
            return new { closed = true };
        }, () => Schema.Object().Pid().String("path", "p").Boolean("activate_other", "a").String("activate_document", "d").Boolean("save", "s"));
        yield return new FakeTool("ui_run_script", ToolPermission.Interact, c =>
        {
            UiRuns++;
            var script = c.RequireString("script_path");
            using var doc = JsonDocument.Parse(File.ReadAllText(script));
            var n = doc.RootElement.GetProperty("steps").GetArrayLength();
            var outDir = c.RequireString("out_dir");
            File.WriteAllText(Path.Combine(outDir, "report.json"), "{}");
            var failed = UiStatus == "failed" ? 1 : 0;
            return new { status = UiStatus, passed = n - failed, failed, steps = n, out_dir = outDir };
        }, () => Schema.Object().String("script_path", "s").String("script", "s").Integer("pid", "p").String("out_dir", "o"));
        yield return new FakeTool("ui_list_windows", ToolPermission.Read, _ =>
        {
            var wins = new List<object> { new { hwnd = "0x100", title = "Autodesk Revit 2024" } };
            if (LeaveWindowOpen && UiRuns > 0)
            {
                wins.Add(new { hwnd = "0x200", title = "Schedule Properties" });
            }

            return new { windows = wins };
        }, () => Schema.Object().Pid(required: false));
        yield return new FakeTool("model_query_elements", ToolPermission.Read, c => c.GetString("category") == "Levels"
            ? new
            {
                category = "Levels",
                count = 3,
                elements = new object[] { new { id = 1, name = "Level 1", elevation = 0.0 }, new { id = 2, name = "Level 2", elevation = 3000.0 }, new { id = 3, name = "Roof", elevation = 6000.0 } },
            }
            : new { category = c.GetString("category") ?? string.Empty, count = 0, elements = Array.Empty<object>() },
            () => Schema.Object().Pid().String("category", "c", required: true).StringArray("fields", "f").AnyObject("filters", "f").Integer("limit", "l"));
        yield return new FakeTool("schedule_list_schedulable_categories", ToolPermission.Read, _ => new { categories = new[] { new { name = "Rooms" }, new { name = "Doors" }, new { name = "Levels" }, new { name = "Walls" } } }, () => Schema.Object().Pid());
        // Shape of the real Bridge result (ScheduleTools.Read): body.rows holds data rows only, the headings are in body.header_cells.
        yield return new FakeTool("schedule_read", ToolPermission.Read, c => new
        {
            id = 4242,
            name = c.GetString("name"),
            is_itemized = true,
            fields = new[] { new { name = "Name", heading = "Name", hidden = false }, new { name = "Number", heading = "Number", hidden = false }, new { name = "Area", heading = "Area", hidden = false } },
            header = new { total_rows = 1, columns = 3, returned_rows = 1, truncated = false, rows = new[] { new[] { c.GetString("name") ?? string.Empty, string.Empty, string.Empty } } },
            body = new
            {
                columns = 3,
                header_rows = 2,
                header_cells = new[] { new[] { "Name", "Number", "Area" }, new[] { string.Empty, string.Empty, string.Empty } },
                data_rows = 2,
                returned_rows = 2,
                truncated = false,
                rows = new[] { new[] { "Office", "101", "20 m²" }, new[] { "Hall", "102", "35 m²" } },
            },
        }, () => Schema.Object().Pid().String("name", "n").Integer("offset", "o").Integer("limit", "l"));
        yield return new FakeTool("model_list_views", ToolPermission.Read, _ => new { views = new[] { new { name = "Level 1", type = "FloorPlan" }, new { name = "Room Schedule", type = "Schedule" } } }, () => Schema.Object().Pid().Integer("limit", "l"));
        yield return new FakeTool("command_post", ToolPermission.Interact, _ => new { posted = true }, () => Schema.Object().Pid().String("command_id", "c", required: true));
        yield return new FakeTool("ui_click", ToolPermission.Interact, c => new
        {
            clicked = true,
            selector = c.GetString("selector") ?? (c.GetString("ref") == "e7" ? "id=ID_SCHEDULES" : null),
        }, () => Schema.Object().Pid().String("selector", "s").String("ref", "r").String("window", "w"));
        yield return new FakeTool("ui_type", ToolPermission.Interact, _ => new { typed = true }, () => Schema.Object().Pid().String("selector", "s").String("text", "t").String("window", "w"));
        yield return new FakeTool("ui_select", ToolPermission.Interact, _ => new { selected = true }, () => Schema.Object().Pid().String("selector", "s").String("item", "i").String("window", "w"));
        yield return new FakeTool("ui_keys", ToolPermission.Interact, _ => new { sent = true }, () => Schema.Object().Pid().String("keys", "k").String("window", "w"));
        yield return new FakeTool("ui_expect", ToolPermission.Read, _ => new { passed = true }, () => Schema.Object().Pid().String("selector", "s").String("window", "w").String("is", "i").String("text", "t"));
        yield return new FakeTool("ui_wait", ToolPermission.Read, _ => new { found = true }, () => Schema.Object().Pid().String("selector", "s").String("window", "w").Boolean("gone", "g").Integer("timeout_sec", "t"));
        yield return new FakeTool("ui_snapshot", ToolPermission.Read, _ => new { tree = "window Revit" }, () => Schema.Object().Pid());
        yield return new FakeTool("fail_tool", ToolPermission.Write, _ => throw new GatewayException(GatewayErrorCodes.Conflict, "boom: element already exists"), () => Schema.Object().Pid());
        yield return new FakeTool("crash_tool", ToolPermission.Write, _ => throw new InvalidOperationException("secret internals"), () => Schema.Object().Pid());
        yield return new FakeTool("echo", ToolPermission.Write, c => JsonDocument.Parse(c.Arguments.GetRawText()).RootElement.Clone(),
            () => Schema.Object().Pid(required: false).String("path", "p").String("out", "o").String("model", "m").String("pid_text", "p").String("csv", "c").String("ds", "d").StringArray("list", "l"));
        yield return new FakeTool("no_pid_tool", ToolPermission.Read, c => new { got = c.Arguments.GetRawText() }, () => Schema.Object().String("x", "x"));
        yield return new FakeTool("flaky_tool", ToolPermission.Read, c =>
        {
            FlakyCalls++;
            return FlakyHandler != null ? FlakyHandler(c) : new { ok = true, count = 1 };
        }, () => Schema.Object().Pid(required: false));
        yield return new FakeTool("flaky_action_tool", ToolPermission.Write, c =>
        {
            FlakyCalls++;
            return FlakyHandler != null ? FlakyHandler(c) : new { ok = true, count = 1 };
        }, () => Schema.Object().Pid(required: false));
    }
}

/// <summary>Gateway for scenario tests: temp home, fake Revit tools, the real scenario/recorder tools.</summary>
public sealed class ScenarioHost : IAsyncDisposable
{
    public static readonly Type[] PackageTools =
    {
        typeof(ScenarioRunTool), typeof(ScenarioValidateTool), typeof(ScenarioListRunsTool),
        typeof(RecorderStartTool), typeof(RecorderStopTool), typeof(RecorderExportTool),
    };

    public ScenarioHost(IEnumerable<string>? without = null)
    {
        Paths = new AgentPaths(TestGateway.NewTempDir("revitagent_home_"));
        Revit = new FakeRevit(Paths);
        var skip = new HashSet<string>(without ?? Array.Empty<string>());
        var sc = new ServiceCollection();
        sc.AddSingleton(Paths);
        sc.AddSingleton<AuditLog>();
        sc.AddSingleton<GatewayPolicy>();
        sc.AddSingleton<GatewayEvents>();
        sc.AddSingleton(new RevitAgent.Core.Policy.ActionGuard(new RevitAgent.Core.Policy.ActionGrantStore(), new RevitAgent.Tests.Bridge.FakeRevitProbe(), new NoOwned(), new RevitAgent.Core.Policy.SystemWindowProcessResolver()));
        sc.AddSingleton<ToolRegistry>();
        foreach (var fake in Revit.Tools.Values.Where(t => !skip.Contains(t.Name)))
        {
            sc.AddSingleton<IGatewayTool>(fake);
        }

        foreach (var type in PackageTools)
        {
            sc.AddSingleton(type);
            sc.AddSingleton(typeof(IGatewayTool), sp => sp.GetRequiredService(type));
        }

        sc.AddSingleton<ScenarioRecorder>();
        sc.AddSingleton<IToolCallObserver>(sp => sp.GetRequiredService<ScenarioRecorder>());
        sc.AddSingleton<Func<int, ScenarioRunner.RevitProcessLiveness>>(sp => pid => Revit.GetLiveness(pid));
        sc.AddSingleton<Func<int, string?>>(sp => year => Revit.JournalPath);
        sc.AddSingleton<Func<int, byte[]?>>(sp => pid => Revit.Screenshot);
        sc.AddSingleton<Func<DateTimeOffset>>(sp => () => Revit.Now?.Invoke() ?? DateTimeOffset.Now);
        Services = sc.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Registry = Services.GetRequiredService<ToolRegistry>();
        Recorder = Services.GetRequiredService<ScenarioRecorder>();
    }

    public AgentPaths Paths { get; }

    public FakeRevit Revit { get; }

    public ServiceProvider Services { get; }

    public ToolRegistry Registry { get; }

    public ScenarioRecorder Recorder { get; }

    public GatewayPolicy Policy => Services.GetRequiredService<GatewayPolicy>();

    public Task<ToolResult> CallAsync(string tool, object? args = null, string session = "s1") =>
        Registry.InvokeAsync(tool, args is null ? null : JsonSerializer.SerializeToElement(args), session, CancellationToken.None);

    public async Task<JsonElement> CallOkAsync(string tool, object? args = null, string session = "s1")
    {
        var r = await CallAsync(tool, args, session);
        Assert.False(r.IsError, $"{tool} failed: {((TextContent)r.Content[0]).Text}");
        return r.Structured!.Value;
    }

    public ScenarioRunner Runner() => new(Registry, Paths, Policy)
    {
        BusyProbeDelay = TimeSpan.FromMilliseconds(10),
        BusyWaitLimit = TimeSpan.FromMilliseconds(200),
        ProcessLivenessProbe = pid => Revit.GetLiveness(pid),
        JournalPathFinder = year => Revit.JournalPath,
        ScreenshotCapture = pid => Revit.Screenshot,
        Clock = () => Revit.Now?.Invoke() ?? DateTimeOffset.Now,
    };

    /// <summary>Writes a scenario folder (relative path → content) under a fresh temp dir; returns the folder.</summary>
    public static string NewScenario(string name, params (string Path, string Content)[] files)
    {
        var dir = Path.Combine(TestGateway.NewTempDir("revitagent_scn_"), name);
        Directory.CreateDirectory(dir);
        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return dir;
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}

/// <summary>Test double: the gateway owns no process (the fake probe lists no Revit, so the action guard never applies).</summary>
internal sealed class NoOwned : RevitAgent.Core.Policy.IOwnedProcessLookup
{
    public bool IsOwned(int pid) => false;
}
