using System.Text.Json.Nodes;
using RevitAgent.E2E;

// RevitAgent acceptance run — a black-box MCP client that drives Revit exactly like an AI agent would.
//
//   RevitAgent.E2E.exe [--shim <RevitAgent.Shim.exe>] [--evidence <dir>] [--headless]
//                      [--launch <year> | --attach <pid>] [--model <source .rvt>] [--offline]
//
//   --launch 2024   start our own Revit 2024 through revit_launch (closed again at the end)
//   --attach <pid>  use a Revit the user started (and whose startup prompts the user answered); never closed
//   --offline       only the checks that need no Revit
//   --evidence <dir>  write evidence there, no run record. Default: a normal gateway run (suite e2e-acceptance,
//                   trigger cli) at <run store>\<yyyy-MM>\<yyyy-MM-dd>\<HHmmss>_e2e-acceptance_<id6>\evidence, where the
//                   run store is %REVITAGENT_HOME%\runs or %LOCALAPPDATA%\RevitAgent\runs (config\run-store.json).
//                   The run folder is printed and the run is finished PASS/FAIL (runs_list, dashboard history).
//
//   RevitAgent.E2E.exe call <tool> [<json-args> | @file | -]   one tool call (step-by-step driving, demos)
//   RevitAgent.E2E.exe catalog [<out.md>]                       regenerate docs/agent-guide/tool-catalog.md
if (args.Length > 0 && args[0] == "call")
{
    Environment.ExitCode = await CallMode.RunAsync(args[1..]);
    return;
}

if (args.Length > 0 && args[0] == "catalog")
{
    Environment.ExitCode = await CatalogMode.RunAsync(args[1..]);
    return;
}

var opt = Options.Parse(args);
var ev = EvidenceRun.Create(opt.Evidence, "e2e-acceptance", "E2E acceptance run");
Console.WriteLine($"evidence: {Path.GetFullPath(ev.EvidenceDir)}");

// --profile full: the catalog check below expects tools outside the default core profile in tools/list.
await using var s = await McpSession.ConnectAsync(opt.Shim, ev.EvidenceDir, opt.Headless ? new[] { "--headless", "--profile", "full" } : new[] { "--profile", "full" });
s.Completed = ev.Finish;

// ---------------------------------------------------------------- 1. catalog (no Revit)
var tools = (await s.ListToolsAsync()).Select(t => t.Name).ToHashSet();
s.Check(s.ServerName == "revitagent", $"connected to '{s.ServerName}' through the Shim");
string[] expected =
{
    "gateway_get_info",
    "ui_list_windows", "ui_snapshot", "ui_find", "ui_click", "ui_type", "ui_select", "ui_keys", "ui_wait", "ui_expect", "ui_read_table", "ui_screenshot", "ui_run_script",
    "revit_list_installed", "revit_list_instances", "revit_launch", "revit_close", "revit_get_state", "revit_wait_ready", "revit_list_dialogs", "revit_handle_dialog",
    "sandbox_create_run", "sandbox_copy_model",
    "bridge_status", "bridge_list_installs", "doc_info", "doc_open", "doc_close", "model_query_elements", "schedule_list_schedulable_categories", "schedule_read", "ribbon_list_items", "command_post",
    "scenario_validate", "scenario_run", "recorder_start", "recorder_export",
};
var missing = expected.Where(e => !tools.Contains(e)).ToArray();
s.Check(missing.Length == 0, $"{tools.Count} tools listed; all key tools present{(missing.Length > 0 ? " — missing: " + string.Join(", ", missing) : string.Empty)}");

var installed = await s.CallAsync("revit_list_installed");
s.Check(installed.Ok, "revit_list_installed");
var bridges = await s.CallAsync("bridge_list_installs");
s.Check(bridges.Ok, "bridge_list_installs");
var before = await s.CallAsync("revit_list_instances");
s.Check(before.Ok, "revit_list_instances (pids that existed before the run are never touched)");
var foreignPids = Pids(before.Json);
s.Note($"Revit processes before the run: {(foreignPids.Count == 0 ? "none" : string.Join(", ", foreignPids))}");

var exampleDir = Path.GetFullPath(Path.Combine("examples", "scenarios", "levels-inventory"));
if (Directory.Exists(exampleDir))
{
    var lint = await s.CallAsync("scenario_validate", new { scenario_dir = exampleDir });
    s.Check(lint.Ok, "scenario_validate on examples/scenarios/levels-inventory");
}

if (opt.Offline)
{
    return;
}

// ---------------------------------------------------------------- 2. get a Revit
int pid;
bool launchedHere = false;
bool blockedByHuman = false;
if (opt.AttachPid is int attach)
{
    pid = attach;
    s.Note($"attaching to the user's Revit pid {pid} (it will not be closed)");
    s.Note("ACTION GRANT NEEDED: before this run turn on \"Let AI act on my Revit\" for this Revit in the RevitAgent dashboard (15 min or more). Read steps work without it; steps that click, type or open documents need it.");
}
else
{
    var launch = await s.CallAsync("revit_launch", new { year = opt.LaunchYear, timeout_sec = 300 }, timeout: TimeSpan.FromMinutes(7));
    var p = launch.Json?["pid"]?.GetValue<int>();
    s.Check(p is > 0, $"revit_launch {opt.LaunchYear} → pid {p}");
    if (p is not > 0)
    {
        return;
    }

    pid = p.Value;
    launchedHere = true;
}

try
{
    // ------------------------------------------------------------ 3. readiness + dialog safety on the real process
    var state = await s.CallAsync("revit_get_state", new { pid, include_details = true, include_screenshot = true });
    s.Check(state.Ok, $"revit_get_state → {state.Str("state")}");
    var shot = await s.CallAsync("ui_screenshot", new { pid }, "main window");
    s.Check(shot.Ok && shot.Images.Count > 0, "ui_screenshot returns an image of the Revit window");

    if (state.Str("state") == "modal")
    {
        var dialogs = await s.CallAsync("revit_list_dialogs", new { pid, include_screenshot = true });
        s.Check(dialogs.Ok, "revit_list_dialogs describes the blocking dialog(s)");
        var first = dialogs.Json?["dialogs"]?.AsArray().FirstOrDefault() ?? dialogs.Json?["blockers"]?.AsArray().FirstOrDefault();
        var title = first?["title"]?.ToString() ?? "?";
        var category = first?["category"]?.ToString() ?? "?";
        s.Note($"blocking dialog: '{title}' (category {category})");
        if (category == "forbidden" && first?["hwnd"] is { } hwnd)
        {
            var refuse = await s.CallAsync("revit_handle_dialog", new { pid, hwnd = hwnd.ToString(), button = "Load Once" }, "must be refused");
            s.Check(refuse.IsSafetyRefusal, "gateway REFUSES to answer a security dialog (needs a human)");

            // Read-only UI observation still works while Revit is modal-blocked.
            var ribbon = await s.CallAsync("ui_snapshot", new { pid, max_nodes = 400 }, "ribbon while blocked (read-only)");
            s.Check(ribbon.Ok && (ribbon.Text ?? string.Empty).Contains("ID_"), "ui_snapshot reads Revit's ribbon tree (AutomationIds ID_...)");
            var tabs = await s.CallAsync("ui_find", new { pid, selector = "id=View", limit = 5 }, "ribbon tab View");
            s.Check(tabs.Ok && tabs.Json?["count"]?.GetValue<int>() >= 1, "ui_find locates the ribbon tab 'View' (Revit 2024 tabs are Buttons with id=<tab name>)");
            var dlg = await s.CallAsync("ui_snapshot", new { pid, window = $"hwnd:{hwnd}" }, "security dialog tree (read-only)");
            s.Check(dlg.Ok && (dlg.Text ?? string.Empty).Contains("CommandButton_"), "ui_snapshot reads the TaskDialog buttons (CommandButton_n)");

            // The UI layer must not be a back door around the dialog rule.
            var uiClick = await s.CallAsync("ui_click", new { pid, window = $"hwnd:{hwnd}", selector = "id=CommandButton_1002" }, "must be refused (Load Once via UI)");
            s.Check(uiClick.IsSafetyRefusal, "ui_click on the security dialog is REFUSED too (no UI back door)");
            var uiKeys = await s.CallAsync("ui_keys", new { pid, window = $"hwnd:{hwnd}", keys = "{ENTER}" }, "must be refused (Enter on the dialog)");
            s.Check(uiKeys.IsSafetyRefusal, "ui_keys into the security dialog is REFUSED too");
            s.Note("Revit is blocked by a security prompt that only a human may answer → Bridge steps skipped. Re-run with --attach <pid> after answering it.");
            blockedByHuman = true;
        }
        else
        {
            var ready = await s.CallAsync("revit_wait_ready", new { pid, timeout_sec = 120 });
            s.Check(ready.Ok && ready.Str("state") == "ready", $"revit_wait_ready → {ready.Str("state")}");
        }
    }

    // ------------------------------------------------------------ 4. Bridge: open a sandbox copy and read the model
    var status = blockedByHuman ? null : await s.CallAsync("bridge_status", new { pid });
    if (status is null)
    {
        return;
    }

    var bridgeUp = status.Ok && (status.Text ?? string.Empty).Contains("\"reachable\":true");
    s.Check(bridgeUp, "bridge_status: the RevitAgent Bridge is loaded in this Revit");
    if (bridgeUp)
    {
        var run = await s.CallAsync("sandbox_create_run", new { name = "e2e-acceptance" });
        var copy = await s.CallAsync("sandbox_copy_model", new { source_path = opt.Model, run_id = run.Str("run_id") });
        s.Check(copy.Ok && copy.Str("original_unchanged") != "false", $"sandbox_copy_model → {copy.Str("copy_path")}");
        var open = await s.CallAsync("doc_open", new { pid, path = copy.Str("copy_path") }, timeout: TimeSpan.FromMinutes(6));
        s.Check(open.Ok, "doc_open on the sandbox copy");
        var info = await s.CallAsync("doc_info", new { pid });
        s.Check(info.Ok && (info.Str("path") ?? string.Empty).Contains(@"\runs\"), $"doc_info: active document is the sandbox copy ({info.Str("title")})");

        var outside = await s.CallAsync("doc_open", new { pid, path = opt.Model }, "original must be refused");
        s.Check(!outside.Ok && (outside.Text ?? string.Empty).Contains("SANDBOX_VIOLATION"), "doc_open refuses the original model (SANDBOX_VIOLATION)");

        // The user's example: list all Level names. A good agent first checks whether Levels can be scheduled…
        var schedulable = await s.CallAsync("schedule_list_schedulable_categories", new { pid, limit = 1000 });
        var levelsSchedulable = (schedulable.Text ?? string.Empty).Contains("\"Levels\"");
        s.Check(schedulable.Ok, $"schedule_list_schedulable_categories (Levels schedulable: {levelsSchedulable})");
        // …and reads the model instead.
        var levels = await s.CallAsync("model_query_elements", new { pid, category = "OST_Levels", fields = new[] { "Name", "Elevation" }, limit = 200 });
        s.Check(levels.Ok && (levels.Json?["count"]?.GetValue<int>() ?? levels.Json?["elements"]?.AsArray().Count ?? 0) > 0, "model_query_elements(OST_Levels) returns the Level names");

        var ribbon = await s.CallAsync("ribbon_list_items", new { pid, limit = 1000 });
        s.Check(ribbon.Ok, "ribbon_list_items lists ribbon items (incl. third-party add-ins)");

        var snap = await s.CallAsync("ui_snapshot", new { pid, max_nodes = 300 });
        s.Check(snap.Ok, "ui_snapshot of the Revit main window");

        // The user's example through the UI: open "Schedule/Quantities" (Bridge posts the command), read the
        // category list in the real dialog (Levels is not schedulable), then Cancel — nothing is created.
        var postable = await s.CallAsync("command_list_postable", new { pid, name_contains = "Schedule", limit = 20 });
        // Revit's PostableCommand is ScheduleOrQuantities (live check 2026-11-25; "ScheduleQuantities" does not exist).
        var scheduleCmd = (postable.Text ?? string.Empty).Contains("\"ScheduleOrQuantities\"") ? "ScheduleOrQuantities" : null;
        s.Check(scheduleCmd is not null, "command_list_postable finds ScheduleOrQuantities");
        if (scheduleCmd is not null)
        {
            var posted = await s.CallAsync("command_post", new { pid, command = scheduleCmd });
            s.Check(posted.Ok && posted.Str("revit_activated") == "true", "command_post ScheduleOrQuantities (opens the New Schedule dialog; Revit brought to the foreground)");
            var appeared = await s.CallAsync("ui_wait", new { pid, window = "New Schedule", state = "appears", timeout_s = 30 });
            s.Check(appeared.Ok && appeared.Text?.Contains("\"ok\":false") != true, "ui_wait: the 'New Schedule' dialog appears");
            var dlg = await s.CallAsync("ui_snapshot", new { pid, window = "New Schedule", interactive_only = false, max_nodes = 600 }, "New Schedule dialog");
            var dlgText = dlg.Text ?? string.Empty;
            s.Check(dlg.Ok && dlgText.Contains("Cancel"), "ui_snapshot reads the real New Schedule dialog");
            s.Note($"visible categories include Rooms: {dlgText.Contains("Rooms")}; Levels offered: {dlgText.Contains("\"Levels\"")} (list may be scrolled)");
            var cancel = await s.CallAsync("ui_click", new { pid, window = "New Schedule", selector = "name=Cancel" }, "close without creating");
            s.Check(cancel.Ok, "ui_click Cancel closes the dialog without creating a schedule");
            var gone = await s.CallAsync("ui_wait", new { pid, window = "New Schedule", state = "gone", timeout_s = 15 });
            s.Check(gone.Ok, "the dialog is gone; Revit is ready again");
        }

        var close = await s.CallAsync("doc_close", new { pid });
        s.Note($"doc_close: {(close.Ok ? "closed" : close.Text)}");
    }
}
finally
{
    if (launchedHere)
    {
        var closed = await s.CallAsync("revit_close", new { pid, discard_changes = true, grace_sec = 30, force_kill = true }, timeout: TimeSpan.FromMinutes(3));
        s.Check(closed.Ok, $"revit_close our own pid {pid}");
    }

    var after = await s.CallAsync("revit_list_instances");
    var stillThere = Pids(after.Json);
    s.Check(foreignPids.All(stillThere.Contains), "every Revit that existed before the run is still running (untouched)");
}


static List<int> Pids(JsonNode? json)
{
    var list = json?["instances"]?.AsArray() ?? json?["processes"]?.AsArray();
    return list?.Select(n => n?["pid"]?.GetValue<int>() ?? 0).Where(p => p > 0).ToList() ?? new List<int>();
}

internal sealed record Options(string Shim, string? Evidence, bool Headless, int LaunchYear, int? AttachPid, string Model, bool Offline)
{
    public static Options Parse(string[] a)
    {
        string Get(string name, string def)
        {
            var i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
        }

        var attach = Get("--attach", string.Empty);
        return new Options(
            Path.GetFullPath(Get("--shim", Path.Combine("src", "RevitAgent.Shim", "bin", "Debug", "net10.0-windows", "RevitAgent.Shim.exe"))),
            Get("--evidence", string.Empty),
            a.Contains("--headless"),
            int.Parse(Get("--launch", "2024")),
            string.IsNullOrEmpty(attach) ? null : int.Parse(attach),
            Get("--model", @"D:\Autodesk\Revit 2024\Samples\Snowdon Towers Sample Architectural.rvt"),
            a.Contains("--offline"));
    }
}
