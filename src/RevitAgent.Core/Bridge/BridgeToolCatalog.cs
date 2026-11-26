using System.Text.Json;
using System.Text.Json.Nodes;
using RevitAgent.Contracts.Bridge;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Bridge;

/// <summary>Services a <see cref="BridgeToolDefinition.Prepare"/> hook may use (policy, sandbox paths).</summary>
public sealed record BridgeToolServices(GatewayPolicy Policy, AgentPaths Paths);

/// <summary>
/// One gateway tool that forwards to the Bridge tool of the same name. Adding a Bridge tool to the gateway
/// is one entry in <see cref="BridgeToolCatalog.All"/> (plus its handler in the add-in).
/// </summary>
/// <param name="Name">Gateway and Bridge tool name.</param>
/// <param name="Permission">Honest side-effect class.</param>
/// <param name="Description">Written for the AI agent.</param>
/// <param name="Arguments">Adds the tool-specific arguments; <c>pid</c> is added automatically.</param>
/// <param name="TimeoutSeconds">Host budget; the Bridge gets a few seconds less.</param>
/// <param name="Prepare">Optional Host-side validation/normalization of the forwarded arguments (e.g. sandbox check).</param>
/// <param name="After">Optional Host-side step after a successful Bridge call (pid, Bridge result) → result returned to the agent.</param>
/// <param name="WatchDialogs">While the Bridge call waits on Revit, answer informational dialogs that match a rule and, in a
/// Revit the gateway launched, safe-dismiss harmless ones (<see cref="RevitAgent.Core.Revit.SafeDismissPolicy"/>); never forbidden
/// ones. They are listed as <c>handled_dialogs</c> in the result (or in the error details when the call fails).</param>
/// <param name="SandboxDocument">The tool changes the target document, so it must be a sandbox copy: while sandbox enforcement
/// is on, the gateway first asks the Bridge (doc_info) which file the <c>document</c> argument resolves to, refuses with
/// SANDBOX_VIOLATION unless that file is inside the sandbox, and forwards <c>document</c> pinned to that full path.</param>
public sealed record BridgeToolDefinition(
    string Name,
    ToolPermission Permission,
    string Description,
    Func<Schema, Schema> Arguments,
    int TimeoutSeconds = 60,
    Func<ToolCall, JsonObject, BridgeToolServices, JsonObject>? Prepare = null,
    Func<int, JsonElement, JsonElement>? After = null,
    bool WatchDialogs = false,
    bool SandboxDocument = false);

/// <summary>The Bridge-backed gateway tools (doc_*, model_*, schedule_*, ribbon/command/view/selection/dialog).</summary>
public static class BridgeToolCatalog
{
    // What DEPENDENCY_FAILED means (Bridge not loaded: bridge_install, restart Revit) is in the server instructions, once.
    private const string Requires = " Needs the RevitAgent Bridge in that Revit.";
    private const string DocArg = "Document title or path (default: active).";
    private const string LimitText = "Max items.";

    public static IReadOnlyList<BridgeToolDefinition> All { get; } = new[]
    {
        new BridgeToolDefinition(
            BridgeToolNames.DocInfo,
            ToolPermission.Read,
            "Describe a document open in Revit pid: title, path, is_workshared, is_modifiable/modified, active view (name, id, type), project info, display units (length/area/volume/angle) and the Revit version/build. Fast (<1 s)." + Requires,
            s => s.String("document", DocArg)),

        new BridgeToolDefinition(
            BridgeToolNames.DocListOpen,
            ToolPermission.Read,
            "List the documents open in Revit pid (title, path, active, linked, family, workshared, modified). Linked models are included with is_linked=true." + Requires,
            s => s),

        new BridgeToolDefinition(
            BridgeToolNames.DocOpen,
            ToolPermission.Write,
            "Open a model COPY in Revit pid: the path must be in the sandbox (sandbox_copy_model); originals get SANDBOX_VIOLATION. " +
            "Can take minutes; rule-matched info dialogs are answered and, in a gateway-launched Revit, " +
            "harmless ones get Close/Cancel/Later (handled_dialogs); forbidden ones never: if slow, check revit_list_dialogs and ask the user. An open path is activated." + Requires,
            s => s
                .String("path", "Absolute path of the copy in the sandbox.", required: true)
                .Boolean("detach_from_central", "Workshared: detach from central (worksets kept).", defaultValue: true)
                .Boolean("audit", "Run Revit audit (slower).", defaultValue: false)
                .Boolean("activate", "Open in the UI as active (false: background).", defaultValue: true)
                .String("worksets", "Workshared: worksets to open.", enumValues: new[] { "last_viewed", "open_all", "close_all" }, defaultValue: "last_viewed"),
            TimeoutSeconds: 600,
            Prepare: PrepareDocOpen,
            WatchDialogs: true),

        new BridgeToolDefinition(
            BridgeToolNames.DocClose,
            ToolPermission.Write,
            "Close an open document in Revit pid WITHOUT saving (changes are discarded; it never saves). Revit cannot close the active document through the API: " +
            "if the target is the active document, pass activate_other=true to first switch to another ALREADY-open document (activate_document names which; it never opens a file), " +
            "otherwise you get CONFLICT with guidance. The only open document cannot be closed this way (close Revit with revit_close). " +
            "Identify the document by path or title (doc_list_open); default is the active document. save=true is rejected (VALIDATION_FAILED): the gateway never saves models." + Requires,
            s => s
                .String("path", "Full path of the open document to close.")
                .String("title", "Title of the open document to close (doc_list_open).")
                .String("document", "Title or full path (alternative to path/title).")
                .Boolean("activate_other", "If the target is the active document, first make another already-open document (saved to a local file) active.", defaultValue: false)
                .String("activate_document", "With activate_other: title or path of the already-open document to make active (falls back to any other qualifying one).")
                .Boolean("save", "Must be false: documents are always closed without saving.", defaultValue: false),
            TimeoutSeconds: 120,
            Prepare: PrepareDocClose),

        new BridgeToolDefinition(
            BridgeToolNames.ModelListCategories,
            ToolPermission.Read,
            "List the categories of a model: name (localized), id, builtin (OST_* name, use it in model_query_elements), type (model/annotation/...), schedulable, subcategory_count. Sorted by name." + Requires,
            s => s
                .String("document", DocArg)
                .String("name_contains", "Case-insensitive filter on the category name.")
                .String("type", "Filter by category type.", enumValues: new[] { "model", "annotation", "analyticalmodel", "internal" })
                .Integer("limit", LimitText, defaultValue: 100, minimum: 1, maximum: 1000)),

        new BridgeToolDefinition(
            BridgeToolNames.ModelQueryElements,
            ToolPermission.Read,
            "Query model elements by category and/or class with parameter filters (name = parameter or BuiltInParameter; numbers in display units) " +
            "and return fields: value (raw, lengths in feet), display, display_number/unit. Returns id, unique_id, name, category, class, type_id, total, truncated." + Requires,
            s => s
                .String("document", DocArg)
                .String("category", "OST_ name (OST_Levels) or display name.")
                .String("class", "Revit API class (Wall, Level...).")
                .Raw("filters", new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "All must match.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject { ["type"] = "string" },
                            ["op"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray("equals", "not_equals", "contains", "gt", "gte", "lt", "lte", "exists"),
                            },
                            ["value"] = new JsonObject { ["description"] = "String or number." },
                        },
                        ["required"] = new JsonArray("name"),
                    },
                })
                .StringArray("fields", "Parameters, e.g. [Name, Elevation].")
                .Integer("view_id", "Only visible in this view.")
                .Boolean("include_types", "Include element types.", defaultValue: false)
                .Integer("offset", "Skip (paging).", defaultValue: 0, minimum: 0)
                .Integer("limit", LimitText, defaultValue: 100, minimum: 1, maximum: 1000),
            TimeoutSeconds: 180),

        new BridgeToolDefinition(
            BridgeToolNames.ModelGetElement,
            ToolPermission.Read,
            "Read one element by id or unique_id: name, category, class, type, level, workset, location (internal units, feet), bounding box and ALL parameters " +
            "(name, storage type, value, display value, read_only, builtin, group, shared_guid). include_type_parameters adds the type's parameters." + Requires,
            s => s
                .String("document", DocArg)
                .Integer("id", "Element id.")
                .String("unique_id", "Element UniqueId (stable across sessions).")
                .Boolean("include_type_parameters", "Also return the parameters of the element's type.", defaultValue: false)),

        new BridgeToolDefinition(
            BridgeToolNames.ModelListViews,
            ToolPermission.Read,
            "List the views of a model (id, name, kind, view_type, level, sheet_number, is_active). Templates are excluded unless include_templates=true. Use the id with view_activate or schedule_read." + Requires,
            s => s
                .String("document", DocArg)
                .String("type", "View kind filter.", enumValues: new[] { "floor_plan", "ceiling_plan", "structural_plan", "area_plan", "section", "detail", "elevation", "3d", "schedule", "sheet", "legend", "drafting", "walkthrough", "rendering", "report" })
                .String("name_contains", "Case-insensitive filter on the view name.")
                .Boolean("include_templates", "Include view templates.", defaultValue: false)
                .Integer("limit", LimitText, defaultValue: 100, minimum: 1, maximum: 1000)),

        new BridgeToolDefinition(
            BridgeToolNames.ModelGetWarnings,
            ToolPermission.Read,
            "List the model's Revit warnings (Manage > Warnings): description, severity, failing and additional element ids. Read-only: warnings are never deleted or resolved." + Requires,
            s => s
                .String("document", DocArg)
                .Integer("limit", LimitText, defaultValue: 100, minimum: 1, maximum: 1000)),

        new BridgeToolDefinition(
            BridgeToolNames.ScheduleListSchedulableCategories,
            ToolPermission.Read,
            "List the categories Revit can build a schedule for (ViewSchedule.GetValidCategoriesForSchedule). Pass category to check one: " +
            "check.schedulable=false means Revit cannot schedule it (read its elements with model_query_elements instead); otherwise create one with schedule_create." + Requires,
            s => s
                .String("document", DocArg)
                .String("category", "Category to check (BuiltInCategory name or display name).")
                .String("name_contains", "Case-insensitive filter on the category name.")
                .Integer("limit", LimitText, defaultValue: 100, minimum: 1, maximum: 1000)),

        new BridgeToolDefinition(
            BridgeToolNames.ScheduleRead,
            ToolPermission.Read,
            "Read a schedule view as text exactly as Revit displays it: fields (name, heading, hidden), header (title rows) and body. " +
            "body.rows holds data rows only; the heading rows (grouped headers, column headings, the blank row under them) are in body.header_cells, counted by body.header_rows. " +
            "body.data_rows counts all rows below the headings, including grouping header/footer, blank and grand-total rows: it equals the element count only for an itemized schedule without those. " +
            "limit caps data rows; truncated=true means more exist. Identify the schedule by view_id or exact name (model_list_views with type=schedule)." + Requires,
            s => s
                .String("document", DocArg)
                .Integer("view_id", "Schedule view id.")
                .String("name", "Exact schedule name (case-insensitive).")
                .Integer("limit", "Maximum data rows (default 100, max 1000).", defaultValue: 100, minimum: 1, maximum: 1000),
            TimeoutSeconds: 120),

        new BridgeToolDefinition(
            BridgeToolNames.ScheduleCreate,
            ToolPermission.Write,
            "Create a schedule view in a sandbox copy (else SANDBOX_VIOLATION) in one transaction; any error rolls it back. " +
            "Unknown field: VALIDATION_FAILED listing up to 30 valid names. Schedule name in use: CONFLICT. " +
            "Returns view_id, name, category, fields, sort, activated; read rows with schedule_read." + Requires,
            s => s
                .String("document", "Open document (title or path); default: active.")
                .String("category", "OST_* or display name; must be schedulable.", required: true)
                .StringArray("fields", "Field names (case-insensitive), in column order.", required: true)
                .StringArray("sort_by", "Fields to sort by, in order; each must be in fields.")
                .Boolean("sort_descending", "Sort descending.", defaultValue: false)
                .String("name", "Schedule name.")
                .Boolean("activate", "Make it the active view (active document only).", defaultValue: true),
            TimeoutSeconds: 120,
            Prepare: PrepareScheduleCreate,
            SandboxDocument: true),

        new BridgeToolDefinition(
            BridgeToolNames.RibbonListItems,
            ToolPermission.Read,
            "List the Revit ribbon as the user sees it, INCLUDING third-party add-in tabs: tabs (id, title, visible, contextual, item_count) and flattened items " +
            "(tab, panel, id, text, tooltip, kind, enabled, visible, command_id, postable). Pass an item's command_id to command_post to run it without clicking. " +
            "Hidden tabs/items are skipped unless include_hidden=true; filter with tab, panel, text_contains, postable_only." + Requires,
            s => s
                .String("tab", "Tab title or id (substring, case-insensitive).")
                .String("panel", "Panel title or id (substring).")
                .String("text_contains", "Substring of item text, id or tooltip.")
                .Boolean("include_hidden", "Include invisible tabs and items.", defaultValue: false)
                .Boolean("postable_only", "Only items with a postable command_id.", defaultValue: false)
                .Integer("offset", "Skip this many items (paging).", defaultValue: 0, minimum: 0)
                .Integer("limit", LimitText, defaultValue: 100, minimum: 1, maximum: 1000)),

        new BridgeToolDefinition(
            BridgeToolNames.CommandListPostable,
            ToolPermission.Read,
            "List Revit's PostableCommand names (built-in commands command_post can run, e.g. Wall, CloseInactiveViews, ScheduleOrQuantities). Works even while Revit is busy." + Requires,
            s => s
                .String("name_contains", "Case-insensitive filter on the command name.")
                .Integer("limit", LimitText, defaultValue: 100, minimum: 1, maximum: 1000)),

        new BridgeToolDefinition(
            BridgeToolNames.CommandPost,
            ToolPermission.Interact,
            "Post a Revit command, as if the user clicked it: a PostableCommand name (command_list_postable) or a ribbon command_id (ribbon_list_items, e.g. ID_OBJECTS_WALL or an add-in's CustomCtrl_%...). " +
            "Returns {posted:true, command_id, revit_activated} immediately; the command itself runs AFTER this call returns, when Revit is idle, and may open dialogs or wait for clicks. " +
            "Revit's main window is brought to the foreground (a posted command does not start while Revit sits idle behind other windows). " +
            "You must observe the UI next (ui_wait / ui_snapshot, revit_list_dialogs, dialog_list_events) to see what happened. CONFLICT if Revit cannot post it now." + Requires,
            s => s.String("command", "PostableCommand name or command id string.", required: true),
            Prepare: PrepareCommandPost,
            After: AfterCommandPost),

        new BridgeToolDefinition(
            BridgeToolNames.ViewActivate,
            ToolPermission.Interact,
            "Make a view of the active document the active view (view_id, or exact name plus optional type). Find views with model_query_elements (class View3D, ViewPlan...). Returns the new and previous view." + Requires,
            s => s
                .String("document", "Must be the active document.")
                .Integer("view_id", "View id (model_list_views).")
                .String("name", "Exact view name (case-insensitive).")
                .String("type", "View kind to disambiguate a name, e.g. floor_plan, 3d, schedule, sheet.")),

        new BridgeToolDefinition(
            BridgeToolNames.ViewExportImage,
            ToolPermission.Read,
            "Render a view to PNG with the Revit API (ExportImage): independent of the screen, so it works when Revit is covered, minimized, or the session is locked or RDP-disconnected. " +
            "Default: the active view of the document. Returns {path, width, height, view_id, view_name, document}; the image is saved under captures (or save_path inside captures/runs). Shows the model view only, not the UI." + Requires,
            s => s
                .String("document", DocArg)
                .Integer("view_id", "View id (model_list_views).")
                .String("name", "Exact view name (default: the active view).")
                .Integer("pixel_width", "Image width in pixels (default 1600, max 4096).", defaultValue: 1600, minimum: 64, maximum: 4096)
                .String("save_path", "PNG path inside the gateway captures or runs folder."),
            TimeoutSeconds: 90,
            Prepare: ViewExportImageSupport.Prepare),

        new BridgeToolDefinition(
            BridgeToolNames.SelectionGet,
            ToolPermission.Read,
            "Return the elements currently selected in the active document (id, unique_id, name, category, class) and the total count." + Requires,
            s => s.Integer("limit", LimitText, defaultValue: 100, minimum: 1, maximum: 1000)),

        new BridgeToolDefinition(
            BridgeToolNames.SelectionSet,
            ToolPermission.Interact,
            "Replace the selection in the active document with the given element ids ([] clears it). NOT_FOUND (selection unchanged) if any id does not exist. The model is not modified." + Requires,
            s => s.Raw("ids", new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Element ids to select.",
                ["items"] = new JsonObject { ["type"] = "integer" },
            }, required: true)),

        new BridgeToolDefinition(
            BridgeToolNames.DialogListEvents,
            ToolPermission.Read,
            "Recent dialogs Revit announced through its DialogBoxShowing event (time, dialog_id, kind, message, during_tool), oldest first; seq numbers allow polling with after_seq. " +
            "Record-only: the Bridge never answers dialogs. Works while Revit is busy. The API does not expose buttons: use revit_list_dialogs / ui_snapshot to inspect and revit_handle_dialog to answer." + Requires,
            s => s
                .Integer("after_seq", "Only events with seq greater than this (use last_seq from the previous call).", defaultValue: 0, minimum: 0)
                .Integer("limit", "Maximum events (default 50, max 200).", defaultValue: 50, minimum: 1, maximum: 200),
            TimeoutSeconds: 20),
    };

    private static JsonObject PrepareDocOpen(ToolCall call, JsonObject args, BridgeToolServices services)
    {
        var path = call.RequireString("path");
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw GatewayException.Validation($"'path' is not a valid file path: {path}");
        }

        if (!Path.IsPathRooted(path))
        {
            throw GatewayException.Validation("'path' must be an absolute path (inside the sandbox runs folder).");
        }

        services.Policy.EnsureInSandbox(full, services.Paths, "Model");
        args["path"] = full;
        return args;
    }

    /// <summary>Argument checks that need no Revit (the Bridge repeats them and matches the field names).</summary>
    private static JsonObject PrepareScheduleCreate(ToolCall call, JsonObject args, BridgeToolServices services)
    {
        call.RequireString("category");
        var fields = FieldNames(call, "fields");
        if (fields.Count == 0)
        {
            throw GatewayException.Validation("'fields' must list at least one field name of the category.");
        }

        if (fields.GroupBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } repeated)
        {
            throw GatewayException.Validation($"Field '{repeated.Key}' is listed twice in 'fields'.");
        }

        var sortBy = FieldNames(call, "sort_by");
        if (sortBy.FirstOrDefault(s => !fields.Contains(s, StringComparer.OrdinalIgnoreCase)) is { } missing)
        {
            throw GatewayException.Validation($"sort_by '{missing}' must also be listed in 'fields'.");
        }

        if (sortBy.GroupBy(s => s, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw GatewayException.Validation($"Field '{twice.Key}' is listed twice in 'sort_by'.");
        }

        call.GetBool("sort_descending");
        call.GetBool("activate", true);
        call.GetString("name");
        return args;
    }

    /// <summary>A string or an array of non-empty strings (trimmed); absent = empty.</summary>
    private static IReadOnlyList<string> FieldNames(ToolCall call, string name)
    {
        if (call.Raw(name) is not { } value)
        {
            return Array.Empty<string>();
        }

        var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : new[] { value };
        if (items.Any(i => i.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(i.GetString())))
        {
            throw GatewayException.Validation($"'{name}' must be an array of non-empty field names.");
        }

        return items.Select(i => i.GetString()!.Trim()).ToArray();
    }

    private static JsonObject PrepareCommandPost(ToolCall call, JsonObject args, BridgeToolServices services)
    {
        var reason = RevitAgent.Contracts.Bridge.CommandSafety.BlockReason(call.GetString("command"));
        if (reason is not null)
        {
            throw GatewayException.Denied(reason);
        }

        return args;
    }

    /// <summary>
    /// A posted command runs only when Revit processes window messages: while its main window sits behind another
    /// application it can wait indefinitely (live finding 2026-11-25: New Schedule appeared only once Revit was
    /// activated). Bring the main window to the foreground and report whether that worked.
    /// </summary>
    private static JsonElement AfterCommandPost(int pid, JsonElement result)
    {
        bool activated = false;
        try
        {
            var main = RevitAgent.Core.Revit.Win32Windows.FindMainWindow(RevitAgent.Core.Revit.Win32Windows.TopLevelWindows(pid));
            activated = main is not null && RevitAgent.Core.Ui.InputSender.Activate(main.Hwnd, pid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Activation is best effort: the command is posted either way.
        }

        if (JsonNode.Parse(result.GetRawText()) is not JsonObject obj)
        {
            return result;
        }

        obj["revit_activated"] = activated;
        if (!activated)
        {
            obj["activation_note"] = "Revit could not be brought to the foreground: the command may start only when Revit gets focus (e.g. after a ui_click on Revit's status bar).";
        }

        return JsonSerializer.SerializeToElement(obj);
    }

    private static JsonObject PrepareDocClose(ToolCall call, JsonObject args, BridgeToolServices services)
    {
        if (call.GetBool("save"))
        {
            throw GatewayException.Validation("doc_close never saves: pass save=false (or omit it). Changes are discarded; work on sandbox copies.");
        }

        var keys = new[] { call.GetString("path"), call.GetString("title"), call.GetString("document") }.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().ToArray();
        if (keys.Length > 1)
        {
            throw GatewayException.Validation("Pass only one of path, title or document to doc_close.");
        }

        args.Remove("save");
        args.Remove("path");
        args.Remove("title");
        args.Remove("document");
        if (keys.Length == 1)
        {
            args["document"] = keys[0];
        }

        return args;
    }

    /// <summary>The forwarded argument object: everything except <c>pid</c>.</summary>
    internal static JsonObject ForwardedArgs(JsonElement arguments)
    {
        var obj = JsonNode.Parse(arguments.GetRawText()) as JsonObject ?? new JsonObject();
        obj.Remove("pid");
        return obj;
    }
}
