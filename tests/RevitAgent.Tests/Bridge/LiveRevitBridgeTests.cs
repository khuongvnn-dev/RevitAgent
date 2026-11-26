using System.IO;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using RevitAgent.Core;
using RevitAgent.Core.Composition;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Bridge;

/// <summary>Runs only when REVITAGENT_LIVE_REVIT_PID names a Revit (that YOU launched) with the Bridge loaded.</summary>
public sealed class LiveRevitFactAttribute : FactAttribute
{
    public LiveRevitFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REVITAGENT_LIVE_REVIT_PID")))
        {
            Skip = "Live Revit check: set REVITAGENT_LIVE_REVIT_PID (+ REVITAGENT_LIVE_MODEL, REVITAGENT_LIVE_MODE=full|smoke, REVITAGENT_LIVE_EVIDENCE_DIR).";
        }
    }
}

/// <summary>
/// Live verification of the Bridge through the real gateway (ToolRegistry → BridgeClient → pipe → Revit).
/// Env: REVITAGENT_LIVE_REVIT_PID (required), REVITAGENT_LIVE_MODEL (sandbox copy to open),
/// REVITAGENT_LIVE_MODEL_B (second copy, full mode: opened in background and closed),
/// REVITAGENT_LIVE_MODE (full|smoke), REVITAGENT_LIVE_EVIDENCE_DIR (JSON evidence output).
/// Uses the real %LOCALAPPDATA%\RevitAgent paths so the sandbox check matches the copies.
/// </summary>
[Trait("Category", "RequiresRevit")]
public class LiveRevitBridgeTests
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [LiveRevitFact]
    public async Task Bridge_EndToEnd_AgainstARealRevit()
    {
        var pid = int.Parse(Environment.GetEnvironmentVariable("REVITAGENT_LIVE_REVIT_PID")!);
        var model = Environment.GetEnvironmentVariable("REVITAGENT_LIVE_MODEL");
        var modelB = Environment.GetEnvironmentVariable("REVITAGENT_LIVE_MODEL_B");
        var full = !string.Equals(Environment.GetEnvironmentVariable("REVITAGENT_LIVE_MODE"), "smoke", StringComparison.OrdinalIgnoreCase);
        var evidenceDir = Environment.GetEnvironmentVariable("REVITAGENT_LIVE_EVIDENCE_DIR");
        if (evidenceDir is not null)
        {
            Directory.CreateDirectory(evidenceDir);
        }

        await using var sp = GatewayServices.BuildGateway(new AgentPaths());
        var registry = sp.GetRequiredService<ToolRegistry>();
        var run = new LiveRun(registry, pid, evidenceDir);

        var status = await run.Call("bridge_status", new { });
        run.Expect(status.Ok && status.Result!["instances"]![0]!["reachable"]!.GetValue<bool>(), "bridge_status reports the Bridge reachable");

        if (model is not null)
        {
            var open = await run.Call("doc_open", new { path = model, activate = true });
            run.Expect(open.Ok && open.Result!["opened"]!.GetValue<bool>() || open.Result?["already_open"] is not null, "doc_open opened the sandbox copy");
        }

        var info = await run.Call("doc_info", new { });
        run.Expect(info.Ok && info.Result!["title"] is not null, "doc_info returns the active document");

        var levels = await run.Call("model_query_elements", new { category = "OST_Levels", fields = new[] { "Name", "Elevation" } });
        run.Expect(levels.Ok && levels.Result!["total"]!.GetValue<int>() > 0, "model_query_elements finds levels with Name/Elevation");

        var ribbon = await run.Call("ribbon_list_items", new { limit = 60 }, clipItems: 60);
        var tabTitles = ribbon.Result?["tabs"]?.AsArray().Select(t => t!["title"]?.GetValue<string>()).ToArray() ?? Array.Empty<string?>();
        run.Note("ribbon_tabs", string.Join(" | ", tabTitles));
        run.Expect(ribbon.Ok && tabTitles.Length > 5, "ribbon_list_items lists tabs");

        if (full)
        {
            await run.Call("doc_list_open", new { });
            await run.Call("model_query_elements", new
            {
                category = "OST_Levels",
                fields = new[] { "Name", "Elevation" },
                filters = new object[] { new { name = "Elevation", op = "gt", value = 0 } },
            });
            await run.Call("model_list_categories", new { limit = 10 });
            var check = await run.Call("schedule_list_schedulable_categories", new { category = "OST_Levels", limit = 10 });
            // Revit 2024 reports Levels as schedulable (live 2026-11-25); other years are not verified, so only the shape is checked.
            run.Expect(check.Ok && check.Result?["check"]?["schedulable"]?.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False, "Levels check reports schedulable as a boolean");
            var walls = await run.Call("schedule_list_schedulable_categories", new { category = "OST_Walls", limit = 1 });
            run.Expect(walls.Ok && walls.Result!["check"]!["schedulable"]!.GetValue<bool>(), "Walls are schedulable");

            var schedules = await run.Call("model_list_views", new { type = "schedule", limit = 20 });
            var firstSchedule = schedules.Result?["views"]?.AsArray().FirstOrDefault();
            if (firstSchedule is not null)
            {
                var read = await run.Call("schedule_read", new { view_id = firstSchedule["id"]!.GetValue<long>(), limit = 20 });
                var body = read.Result?["body"];
                run.Expect(read.Ok && body?["data_rows"] is not null && body["header_rows"] is not null, "schedule_read splits the body into header rows and data rows");
                run.Expect(read.Ok && body?["header_note"] is null, "schedule_read recognizes the column headings (no header_note)");
            }
            else
            {
                run.Note("schedule_read", "model has no schedule views");
            }

            var levelId = levels.Result?["elements"]?.AsArray().FirstOrDefault()?["id"]?.GetValue<long>();
            if (levelId is not null)
            {
                var element = await run.Call("model_get_element", new { id = levelId }, clipItems: 40);
                run.Expect(element.Ok && element.Result!["parameters"]!.AsArray().Count > 0, "model_get_element returns parameters");

                var sel = await run.Call("selection_set", new { ids = new[] { levelId } });
                var got = await run.Call("selection_get", new { });
                run.Expect(sel.Ok && got.Ok && got.Result!["total"]!.GetValue<int>() == 1, "selection_set/selection_get round trip");
                await run.Call("selection_set", new { ids = Array.Empty<long>() });
            }

            var plans = await run.Call("model_list_views", new { type = "floor_plan", limit = 5 });
            var plan = plans.Result?["views"]?.AsArray().FirstOrDefault(v => v!["is_active"]?.GetValue<bool>() != true);
            if (plan is not null)
            {
                var act = await run.Call("view_activate", new { view_id = plan["id"]!.GetValue<long>() });
                run.Expect(act.Ok && act.Result!["activated"]!.GetValue<bool>(), "view_activate switched the active view");
                var after = await run.Call("doc_info", new { });
                run.Expect(after.Result?["active_view"]?["id"]?.GetValue<long>() == plan["id"]!.GetValue<long>(), "doc_info shows the activated view");
            }

            await run.Call("model_get_warnings", new { limit = 5 });
            await run.Call("ribbon_list_items", new { postable_only = true, limit = 40 }, clipItems: 40);
            var postable = await run.Call("command_list_postable", new { name_contains = "Schedule" });
            run.Expect(postable.Ok && postable.Result!["total"]!.GetValue<int>() > 0, "command_list_postable lists commands");

            var post = await run.Call("command_post", new { command = "ThinLines" });
            run.Expect(post.Ok && post.Result!["posted"]!.GetValue<bool>(), "command_post posts ThinLines");
            await Task.Delay(2500);
            await run.Call("doc_info", new { });
            var postBack = await run.Call("command_post", new { command = "ThinLines" }); // toggle back
            run.Expect(postBack.Ok, "command_post toggles ThinLines back");
            await Task.Delay(2500);

            // Concurrency: two Revit calls at once; the second must not queue silently.
            var a = run.Call("ribbon_list_items", new { include_hidden = true, limit = 1000 }, clipItems: 5, label: "concurrent_a");
            var b = run.Call("model_query_elements", new { category = "OST_Levels" }, label: "concurrent_b");
            var both = await Task.WhenAll(a, b);
            run.Note("concurrency", string.Join(", ", both.Select(x => x.Label + "=" + (x.Ok ? "ok" : x.ErrorCode))));
            run.Expect(both.All(x => x.Ok || x.ErrorCode == "BUSY"), "concurrent calls either succeed or get BUSY");

            if (modelB is not null)
            {
                var openB = await run.Call("doc_open", new { path = modelB, activate = false }, label: "doc_open_background");
                run.Expect(openB.Ok, "doc_open(activate=false) opens a background document");
                var title = openB.Result?["title"]?.GetValue<string>();
                if (title is not null)
                {
                    var closeB = await run.Call("doc_close", new { document = title }, label: "doc_close_background");
                    run.Expect(closeB.Ok && closeB.Result!["closed"]!.GetValue<bool>(), "doc_close closes a non-active document without saving");
                }

                // The copy is the ACTIVE document while another one is open (an attached Revit): switch back, then close it.
                var openActive = await run.Call("doc_open", new { path = modelB, activate = true }, label: "doc_open_b_active");
                var titleB = openActive.Result?["title"]?.GetValue<string>();
                if (openActive.Ok && titleB is not null)
                {
                    var refused = await run.Call("doc_close", new { document = titleB }, label: "doc_close_active_b_plain");
                    run.Expect(refused.ErrorCode == "CONFLICT", "doc_close of the active document without activate_other is CONFLICT");
                    var switched = await run.Call("doc_close", new { document = titleB, activate_other = true, activate_document = model }, label: "doc_close_active_b_activate_other");
                    run.Expect(switched.Ok && switched.Result?["activated"]?["path"]?.GetValue<string>() is { } p && string.Equals(Path.GetFullPath(p), Path.GetFullPath(model), StringComparison.OrdinalIgnoreCase),
                        "doc_close activate_other switches to the named already-open document, then closes the active one");
                    var after = await run.Call("doc_info", new { }, label: "doc_info_after_switch");
                    run.Expect(after.Ok && after.Result?["path"]?.GetValue<string>() is { } ap && string.Equals(Path.GetFullPath(ap), Path.GetFullPath(model), StringComparison.OrdinalIgnoreCase),
                        "after the switch the named document is active");
                }
                else
                {
                    run.Expect(false, "doc_open(activate=true) of model B: " + openActive.ErrorCode);
                }
            }

            var outside = await run.Call("doc_open", new { path = @"C:\Windows\not-in-sandbox.rvt" }, label: "doc_open_outside_sandbox");
            run.Expect(outside.ErrorCode == "SANDBOX_VIOLATION", "doc_open outside the sandbox is refused");
        }

        await run.Call("dialog_list_events", new { });
        await run.Call("command_list_postable", new { limit = 5 });
        if (model is not null)
        {
            var close = await run.Call("doc_close", new { }, label: "doc_close_active");
            run.Expect(close.ErrorCode == "CONFLICT", "doc_close on the active/only document returns CONFLICT with guidance");
        }

        run.WriteSummary(full ? "full" : "smoke");
        Assert.True(run.Failures.Count == 0, "Live checks failed:\n" + string.Join("\n", run.Failures));
    }

    private sealed record CallRecord(string Label, string Tool, bool Ok, string? ErrorCode, long Ms, JsonNode? Result);

    private sealed class LiveRun
    {
        private readonly ToolRegistry _registry;
        private readonly int _pid;
        private readonly string? _dir;
        private readonly List<object> _summary = new();
        private readonly Dictionary<string, string> _notes = new();
        private int _n;

        public LiveRun(ToolRegistry registry, int pid, string? dir)
        {
            _registry = registry;
            _pid = pid;
            _dir = dir;
        }

        public List<string> Failures { get; } = new();

        public async Task<CallRecord> Call(string tool, object args, int? clipItems = null, string? label = null)
        {
            var node = JsonSerializer.SerializeToNode(args)!.AsObject();
            node["pid"] = _pid;
            var sw = Stopwatch.StartNew();
            var result = await _registry.InvokeAsync(tool, JsonSerializer.SerializeToElement(node), "live-verify", CancellationToken.None);
            sw.Stop();
            var json = result.Structured is { } s ? JsonNode.Parse(s.GetRawText()) : null;
            var record = new CallRecord(label ?? tool, tool, !result.IsError, result.ErrorCode, sw.ElapsedMilliseconds, result.IsError ? null : json);
            var n = Interlocked.Increment(ref _n);
            lock (_summary)
            {
                _summary.Add(new { n, label = record.Label, tool, ok = record.Ok, error_code = record.ErrorCode, ms = record.Ms });
            }

            if (_dir is not null)
            {
                var stored = json?.DeepClone();
                if (clipItems is { } max && stored is JsonObject o)
                {
                    foreach (var key in new[] { "items", "parameters", "elements" })
                    {
                        if (o[key] is JsonArray arr && arr.Count > max)
                        {
                            while (arr.Count > max)
                            {
                                arr.RemoveAt(arr.Count - 1);
                            }

                            o[key + "_clipped_to"] = max;
                        }
                    }
                }

                var file = Path.Combine(_dir, $"{n:00}-{record.Label}.json");
                File.WriteAllText(file, JsonSerializer.Serialize(new { tool, args = node, ok = record.Ok, error_code = record.ErrorCode, ms = record.Ms, result = stored }, Pretty));
            }

            return record;
        }

        public void Expect(bool condition, string what)
        {
            lock (_summary)
            {
                _summary.Add(new { check = what, pass = condition });
            }

            if (!condition)
            {
                Failures.Add(what);
            }
        }

        public void Note(string key, string value) => _notes[key] = value;

        public void WriteSummary(string mode)
        {
            if (_dir is null)
            {
                return;
            }

            File.WriteAllText(Path.Combine(_dir, "summary.json"), JsonSerializer.Serialize(new
            {
                mode,
                pid = _pid,
                at = DateTimeOffset.Now,
                failures = Failures,
                notes = _notes,
                steps = _summary,
            }, Pretty));
        }
    }
}
