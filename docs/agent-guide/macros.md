# Macros (verified recipes)

A macro is a **fixed, verified chain of tool calls** for a common task (create a schedule, run an add-in button...). A macro is **data** (one YAML file), not code:

- There are exactly **two** MCP tools for all macros: `macro_list` and `macro_run`. Adding a macro adds no MCP tool (the model's context does not grow) and needs no Bridge rebuild.
- Each step is an ordinary tool call through the registry, with the agent's `SessionId`: policy, permission ceiling, the "Let AI act on my Revit" switch, sandbox, each tool's own timeout and audit apply **exactly as if the agent called the tool itself**. `macro_run` declares permission Write (the strongest step among the built-in macros, `schedule_create`), so a client does not group it with the read-only tools it may auto-approve. It changes Revit only when a step does.
- For a common task: call `macro_list` first; `macro_list name=<macro>` for details; then `macro_run`.

## Calling a macro

```json
{ "tool": "macro_list", "args": {} }
{ "tool": "macro_list", "args": { "name": "create-schedule" } }
{ "tool": "macro_run", "args": { "name": "create-schedule", "args": { "pid": 12345, "category": "OST_Rooms", "fields": ["Level", "Number", "Name"] } } }
```

- Compact list: `params` reads like `pid:int*, fields:string[]*, limit:int=100` (`*` = required, `=x` = default). `unavailable_tools` appears when the gateway lacks a tool the macro needs (then `macro_run` returns `DEPENDENCY_FAILED` and runs no step).
- Success: `{macro, ok: true, result, steps: [{id, tool, ok, ms, skipped?}]}`. `verbose: true` adds each step's raw `output` (large; for debugging only).
- Failure: stops at the first failed step and returns `{code, message, details: {macro, ok: false, failed_step, error_code, steps, step_details?}}`. `code` is that step's error code (for example `PERMISSION_DENIED`, `CONFLICT`, `TIMEOUT`); a failed `expect` returns `EXPECTATION_FAILED` (a macro-only code, not in the Bridge).
- Bad parameters (missing required, unknown name, wrong type) return `VALIDATION_FAILED` before any step runs. A numeric string (`"12"`) becomes a number, `"true"` a boolean, and a single value becomes a one-element array.

## Built-in macros

| Macro | Needs | Parameters | What it does |
|---|---|---|---|
| `create-schedule` | bridge | `pid:int*, category:string*, fields:string[]*, sort_by:string[], sort_descending:bool, name:string, limit:int=100` | `schedule_create` then `schedule_read`; returns `{view_id, name, fields, data_rows, truncated, rows}` (`rows` holds data rows only, not the column headings; `data_rows` is the count of rows below the headings and `limit` caps data rows). Changes the model (creates a view): runs only on a sandbox copy. |
| `run-addin-command` | bridge, ui | `pid:int*, text:string*, tab:string, wait_window:string, timeout_s:int=20` | `ribbon_list_items` (exactly **one** postable button matching `text`) then `command_post`, then `ui_wait` for the window `wait_window` (skipped when empty), then `revit_list_dialogs`; returns the button pressed, the window and the dialogs. Dialogs are only listed, never answered. |
| `run-revit-command` | bridge, ui | `pid:int*, command:string*, wait_window:string, timeout_s:int=20` | `command_post` with a built-in Revit command (a `PostableCommand` name or a command id), then `ui_wait` for the window `wait_window` (skipped when empty), then `revit_list_dialogs`; returns `{command_id, resolved_as, window, dialog_count, dialogs}`. No ribbon click and no coordinates. |
| `read-schedule` | bridge | `pid:int*, view_id:int, name:string, offset:int=0, limit:int=100` | `schedule_read` for one page of data rows (`limit` max 1000, `offset` = data rows to skip); returns `{view_id, name, fields, header_cells, data_rows, offset, returned_rows, truncated, rows}`. Read-only. |

Traps: without `wait_window`, `run-addin-command` lists dialogs right after posting, so the command may not have opened anything yet. Several matching buttons give `EXPECTATION_FAILED` with `step_details.sample`: narrow `text` or add `tab`.

### Run a built-in Revit command

`run-revit-command` posts the command exactly as given: no search, so the name must be exact (case-insensitive). An unknown name gives `NOT_FOUND`. Examples (names from the Revit API `PostableCommand` enum):

```json
{ "tool": "macro_run", "args": { "name": "run-revit-command", "args": { "pid": 12345, "command": "VisibilityOrGraphics", "wait_window": "Visibility/Graphic Overrides" } } }
{ "tool": "macro_run", "args": { "name": "run-revit-command", "args": { "pid": 12345, "command": "ReviewWarnings" } } }
{ "tool": "macro_run", "args": { "name": "run-revit-command", "args": { "pid": 12345, "command": "ScheduleOrQuantities", "wait_window": "New Schedule" } } }
```

- Other names: `CloseInactiveViews`, `ManageLinks`, `ProjectUnits`. A command id (`ID_OBJECTS_WALL`, an add-in's `CustomCtrl_%...`) works in the same `command` field.
- `wait_window` is a title substring and depends on the Revit language. Verified on Revit 2024.3 (English): the V/G dialog is titled "Visibility/Graphic Overrides for <view>", but Review Warnings is titled only "Autodesk Revit 2024", which also matches the main window; do not pass `wait_window` for it, call `revit_list_dialogs` after the macro instead.
- Without `wait_window` the dialogs are listed right after posting and the command may not have opened anything yet. The dialog stays open: close it with `revit_handle_dialog` or the UI tools.
- Commands that save, synchronize or close the model are refused (`PERMISSION_DENIED`).

### Read a large schedule page by page

`schedule_read` and `read-schedule` return `limit` data rows starting at `offset`. Loop: call with `offset: 0`; while `truncated` is true, call again with `offset` + `returned_rows`. `data_rows` is the total, so the pages can be checked against it. `create-schedule` reads only the first page: use `read-schedule` with its `view_id` for the rest.

```json
{ "tool": "macro_run", "args": { "name": "read-schedule", "args": { "pid": 12345, "view_id": 777, "offset": 1000, "limit": 1000 } } }
```

## YAML format

```yaml
name: create-schedule            # kebab-case, unique
summary: Create a schedule view and read its rows back.   # up to 120 chars, shown in macro_list
description: >-                  # optional, shown only when one macro is requested
  Creates a schedule with schedule_create, then reads it with schedule_read.
requires: [bridge]               # informational: bridge, ui
params:
  pid: {type: integer, required: true, description: Revit process id.}
  fields: {type: array, required: true, description: Field names in column order.}   # items: string (default) | integer | number | boolean
  limit: {type: integer, default: 100}
steps:
  - id: create                   # snake_case, unique
    tool: schedule_create
    args:
      pid: ${pid}
      fields: ${fields}          # exactly one template keeps the JSON type (array)
  - id: read
    tool: schedule_read
    when: ${create.view_id}      # skip the step when empty / false / null / missing
    args:
      pid: ${pid}
      view_id: ${create.view_id}
      limit: ${limit}
    expect:                      # one or a list; a miss gives EXPECTATION_FAILED
      path: body.rows
      not_empty: true            # or count: N
      message: The schedule has no rows.
returns:
  view_id: ${create.view_id}
  rows: ${read.body.rows}
```

- Parameter types: `string | integer | number | boolean | array`. A required parameter must not have a `default`.
- Templates: `${param}` or `${stepId.path.to.value}` (`[0]` for an array element, for example `${find.items[0].command_id}`). A string that is **only** one template keeps the JSON type; any other string is concatenated text (`"n=${read.body.data_rows}"`).
- A literal `${` cannot be written in a macro string (a stray `${` is a load error).
- A missing value (parameter not given, step skipped, path not found) makes that key or element **disappear** from the args, so the tool uses its own default.
- Checks at load: unique names; every template points to a declared parameter or an **earlier** step (`returns` sees every step); unknown keys are errors; a step may not call `macro_*`. A broken macro is skipped and reported by `macro_list` (`load_errors`).

## Adding a macro

1. Create `src/RevitAgent.Core/Macros/Builtin/<name>.yaml` (embedded as a resource by the `*.yaml` pattern).
2. Use real tool names and parameter names (read `BridgeToolCatalog.cs` / the tools in Core, or `tools/list`); read a real tool result to write each `${step.path}`.
3. Add a row to the "Built-in macros" table above.
4. Rebuild **only** RevitAgent.Core (`dotnet build src/RevitAgent.Core`) and run `dotnet test --filter FullyQualifiedName~RevitAgent.Tests.Macros`: the tests check that every file loads, every `tool` exists in the gateway registry, every `args` key is in that tool's schema, and that the macro name appears in this document. No Bridge rebuild, no new MCP tool.
5. Run it once on a real Revit (sandbox copy) before calling it "verified".
