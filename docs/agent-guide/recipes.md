# Recipes

Each recipe has a goal, the steps with tool calls, how to verify, and common traps. Field names in tool results (for example `elements`, `columns`) are **examples**: read the real result and use the names it returns.

Common preconditions: you have a `pid` from `revit_list_instances`; the open model is a **copy** in the sandbox (`sandbox_copy_model` then `doc_open`); `revit_get_state` reports ready. Safety rules: [README.md](README.md) section 4.

Traps common to every recipe:

- A modal dialog hangs every call (`MODAL_BLOCKED`): use `revit_list_dialogs`. Answer only informational dialogs; **never answer** a security / unsigned add-in / license / recovery / delete / overwrite / replace dialog. Stop and ask the user. (A disabled Delete button on a dialog does not make it forbidden; see [revit-process-and-dialogs.md](revit-process-and-dialogs.md).)
- Ribbon buttons have ids like `ID_...`; prefer `id=` over `ref` or `name=`.
- Observe again after each action; do not queue several blind actions.
- Before assembling UI steps, call `macro_list`: a verified macro ([macros.md](macros.md)) is faster and cheaper.

---

## (a) List the names of every Level

**Goal**: get the names (and elevations) of all Levels in the model. Replayable example: `examples/scenarios/levels-inventory`.

Levels are schedulable (verified on Revit 2024.3: `schedule_list_schedulable_categories` returns `schedulable: true`, and a Name + Elevation schedule was created). Pick the path by what the user needs:

1. **Data only** (fast, no model change): read straight from the model.
   ```json
   { "tool": "model_query_elements", "args": { "pid": 12345, "category": "Levels", "fields": ["Name", "Elevation"], "limit": 500 } }
   ```
2. **A schedule inside the model** (on the sandbox copy): use the macro instead of clicking through the UI.
   ```json
   { "tool": "macro_run", "args": { "name": "create-schedule", "args": { "pid": 12345, "category": "OST_Levels", "fields": ["Name", "Elevation"], "sort_by": ["Elevation"] } } }
   ```
   The macro calls `schedule_create` (Write; sandbox copies only, otherwise `SANDBOX_VIOLATION`) then `schedule_read`. For another category you are unsure about, check first with `schedule_list_schedulable_categories` (`category`); if `check.schedulable=false`, fall back to path 1.
3. **Verify**: the number of elements returned matches the Levels you see (open a Section/Elevation view or `ui_screenshot`); names are not empty; `doc_info` confirms you read the copy.
   ```json
   { "tool": "doc_info", "args": { "pid": 12345 } }
   ```

**Traps**: set `limit` high enough. `Elevation` is in Revit API internal units (usually feet), not millimetres.

**Freeze**: `examples/scenarios/levels-inventory` performs the data read without an AI (`scenario_validate`, then `scenario_run`).

---

## (b) Create a Room schedule through the UI

**Goal**: create a Rooms schedule with columns Level, Number, Name through the UI, then read it back. Run for real on Revit 2024.3 against a sandbox copy. Frozen example: `examples/scenarios/room-schedule-ui`.

If you only need the schedule, `macro_run create-schedule {category: "OST_Rooms", fields: ["Level", "Number", "Name"]}` does it in one call. Use the UI route below when the task is to exercise the UI itself.

1. Confirm readiness:
   ```json
   { "tool": "revit_get_state", "args": { "pid": 12345 } }
   ```
2. Open **Schedule/Quantities**. Finding ribbon buttons with UI Automation is very slow on the main window of a large model (a few elements per second); with the Bridge, post the command instead of searching the ribbon:
   ```json
   { "tool": "command_list_postable", "args": { "pid": 12345, "name_contains": "Schedule" } }
   { "tool": "command_post", "args": { "pid": 12345, "command": "ScheduleOrQuantities" } }
   ```
   The correct name is `ScheduleOrQuantities` (not `ScheduleQuantities`). `command_post` wakes Revit's message loop (a posted command runs from Revit's idle processing, which a Revit in the background may otherwise not reach) and tries to bring Revit to the foreground (`revit_activated`). If nothing opens within a few seconds, `ui_snapshot` Revit (it also wakes it) or `ui_click` the Revit status bar (bottom left). Without the Bridge: click the ribbon tab `id=View` (Revit 2024 ribbon tabs are Buttons with `id=<tab name>`), then the Schedules dropdown.
3. The **New Schedule** dialog (its own window; fast to read):
   ```json
   { "tool": "ui_wait", "args": { "pid": 12345, "window": "New Schedule", "state": "appears", "timeout_s": 30 } }
   { "tool": "ui_type", "args": { "pid": 12345, "window": "New Schedule", "selector": "id=3010", "text": "Rooms" } }
   ```
   The Category list (`id=2167`) is an owner-drawn control: UI Automation does **not** see its items, so `ui_select` does not work. Type into the "Category name search" box (`id=3010`) to filter to one item, `ui_screenshot` to see it, then `ui_click` the **Rooms** item by image coordinates (x/y = image pixel / `scale`). Revit changes Name to "Room Schedule" when Rooms is selected, which is a verification sign. To set the name, `ui_type` into `id=2010`. Click `name=OK`.
4. The **Schedule Properties** dialog; the "Available fields" list (`id=2095`) is a standard List:
   ```json
   { "tool": "ui_select", "args": { "pid": 12345, "window": "Schedule Properties", "selector": "in=id=2095;name=Level" } }
   { "tool": "ui_click",  "args": { "pid": 12345, "window": "Schedule Properties", "selector": "id=2092" } }
   ```
   (`id=2092` is the Add button.) Repeat for `Number` and `Name`. Check "Scheduled fields" (`id=2094`) with `ui_snapshot` and `selector: "id=2094"`. To sort, `ui_select` `type=TabItem;name=Sorting/Grouping`, then `ui_select` the combo `id=2104` with `item: "Level"`. Click `name=OK`; `ui_wait` with `state: "gone"`.
5. **Verify in the model** (the UI can lie):
   ```json
   { "tool": "doc_info", "args": { "pid": 12345 } }
   { "tool": "schedule_read", "args": { "pid": 12345, "name": "AI Demo - Rooms by Level" } }
   ```
   Expect: `active_view` is the schedule you just created; `fields` = Level, Number, Name; `body.data_rows` = the number of rooms in the model (rows below the headings); `body.rows` holds data rows only, the column headings are in `body.header_cells` (counted by `body.header_rows`); `body.returned_rows` and `body.truncated` tell you whether `limit` cut the list.

**Traps**: steps 3-4 open **modal dialogs**; if the agent stops halfway, every later Bridge call hangs. Always close them (OK/Cancel). Numeric ids (`2104`, `2092`...) are Win32 control ids of Revit 2024: stable between runs but possibly different in other versions, so confirm with `ui_snapshot`. The "Then by" box may still be locked right after choosing "Sort by": skip it or observe again. The "Delete" / "Delete Field" buttons of this form do not make the gateway refuse it (only a dialog that *asks to confirm* a deletion is blocked). The column and row counts from `schedule_read` are the truth.

---

## (c) Run an add-in command without its source code

**Goal**: run a ribbon command of an add-in, fill in its dialog, and verify the result in the model.

Shortcut: `macro_run run-addin-command {pid, text: "<button text>", wait_window: "<window title>"}` finds the one matching postable ribbon button, posts it, waits for the window, and lists dialogs (see [macros.md](macros.md)). The manual steps follow.

1. List ribbon buttons and postable commands:
   ```json
   { "tool": "ribbon_list_items", "args": { "pid": 12345 } }
   { "tool": "command_list_postable", "args": { "pid": 12345 } }
   ```
   Find the add-in's button (id like `ID_...`, tab, panel). **Read the command name before running it**; if it looks like it deletes or overwrites in bulk, ask the user.
2. Record the state before, for comparison (element count, warnings):
   ```json
   { "tool": "model_query_elements", "args": { "pid": 12345, "category": "Walls", "fields": ["Name"], "limit": 1000 } }
   { "tool": "model_get_warnings", "args": { "pid": 12345 } }
   ```
3. Run the command:
   ```json
   { "tool": "command_post", "args": { "pid": 12345, "command": "ID_..." } }
   ```
   If the add-in has no postable command, click its ribbon button with `ui_click` and an `id=` selector.
4. Observe the add-in window (its own window):
   ```json
   { "tool": "ui_list_windows", "args": { "pid": 12345 } }
   { "tool": "ui_wait", "args": { "pid": 12345, "window": "<add-in window title>" } }
   { "tool": "ui_snapshot", "args": { "pid": 12345, "window": "<add-in window title>" } }
   ```
5. Fill in: `ui_type` into fields (`id=` if the add-in sets AutomationIds; otherwise `ui_inspect` to get the best selector), `ui_select` for combo boxes and lists, `ui_toggle` for check boxes, `ui_click` the run button. Observe again after each step.
6. Close the add-in's dialog with its own button (OK/Close) and `ui_expect` that the window is gone.
7. **Verify in the model**: compare `model_query_elements` / `doc_info` / `model_get_warnings` with the state before (the right number of new elements, no unusual new warnings).

**Traps**: an add-in may raise a security / unsigned / license dialog: **do not answer it; stop and ask the user**. (In a Revit the gateway launched, the unsigned add-in prompt is answered only from the owner's allowlist.) An add-in that shows an overwrite / replace / delete dialog: also stop and ask. Work only on the sandbox copy. A modal add-in window hangs Bridge calls until it is closed: use UI tools (they work while a modal is open) to close it. On `TIMEOUT` the work may still be running: check the state before calling the command again.

---

## (d) Record and replay

**Goal**: turn recipe (b) into a scenario that replays without an AI.

1. Start recording (in the same MCP session that will do the work):
   ```json
   { "tool": "recorder_start", "args": { "name": "Create a Room schedule through the UI" } }
   ```
2. Do recipe (b) from the start through the verification (`schedule_read`, `model_list_views`). Prefer `id=` selectors; failed calls are not recorded.
3. Stop recording:
   ```json
   { "tool": "recorder_stop", "args": {} }
   ```
4. Export a draft:
   ```json
   { "tool": "recorder_export", "args": { "name": "room-schedule-ui" } }
   ```
   The result lists `files`, `todos` and `not_exported`. By default the folder is `<home>\scenarios\room-schedule-ui\` and it contains `scenario.yaml` (and `ui\*.json` UI scripts).
5. Fix the TODOs in `scenario.yaml`: `TODO-ref:...` selectors become `id=<AutomationId>` (`ui_inspect`); set `model:` to the original model (the runner copies it into the run folder); set `revit_version`; review the expected values in `checks` (they come from the recording and need a person to confirm them).
6. Validate, then run:
   ```json
   { "tool": "scenario_validate", "args": { "scenario_dir": "C:\\Users\\<user>\\AppData\\Local\\RevitAgent\\scenarios\\room-schedule-ui" } }
   { "tool": "scenario_run", "args": { "scenario_dir": "C:\\Users\\<user>\\AppData\\Local\\RevitAgent\\scenarios\\room-schedule-ui", "pid": 12345 } }
   ```
7. Read `report.md` in the run folder (the path is in the `scenario_run` result; or `scenario_list_runs {scenario: "room-schedule-ui"}`). The "Needs attention" section lists FAIL / NOT_MEASURABLE / SUSPECT checks. Fix the scenario until it PASSes, then copy the folder into the repository and commit.

**Traps**: a ui step that leaves a window open is marked FAILED (`leave_open: false` by default), so close dialogs inside the script. Do not record or replay answers to security / license / recovery / delete / overwrite dialogs: validate refuses them. `scenario_run` runs one at a time and uses its own copy of the model; do not open the model yourself while it runs.

Format details and checks: [scenarios.md](scenarios.md).
