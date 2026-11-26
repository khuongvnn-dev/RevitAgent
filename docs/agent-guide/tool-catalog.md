# RevitAgent tool catalog (generated from `tools/list`)

67 tools. Regenerate: `tools\RevitAgent.E2E\bin\Debug\net10.0-windows\RevitAgent.E2E.exe catalog` (from the repo folder; calls `tools/list` + `gateway_get_info` through the Shim `--headless --profile full`). Permission: Read (observe only) · Interact (UI/commands) · Write (change models/files in the sandbox) · Admin (processes, installs). `*` = required. Core = listed in the default `core` profile (18 tools); other tools need `--profile full` or a macro.

## `addin_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `addin_allowlist_get` |  | Read | — | List the add-in allowlist: add-ins (name + path pattern) the owner trusts. |

## `bridge_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `bridge_install` |  | Admin | `years` | Install or update the RevitAgent Bridge add-in for the given Revit years (default: every installed Revit 2022-2026). |
| `bridge_list_installs` |  | Read | — | Show the RevitAgent Bridge installation for every Revit version installed on this machine: manifest path, installed assembly and stamp, trusted (pre-approved so Revit shows no 'Unsigned Add-In' prompt), available build a |
| `bridge_status` |  | Read | `pid` | Check the RevitAgent Bridge (the in-Revit add-in behind doc_*, model_*, schedule_*, ribbon_*, command_*, view_*, selection_* and dialog_* tools). |
| `bridge_uninstall` |  | Admin | `years` | Uninstall the RevitAgent Bridge for the given Revit years (default: every installed Revit): removes RevitAgent.Bridge.addin (and the retired v1 manifest), the Bridge's CodeSigning trust value, and the installed copies th |

## `command_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `command_list_postable` |  | Read | `pid`*, `name_contains`, `limit` | List Revit's PostableCommand names (built-in commands command_post can run, e.g. |
| `command_post` |  | Interact | `pid`*, `command`* | Post a Revit command, as if the user clicked it: a PostableCommand name (command_list_postable) or a ribbon command_id (ribbon_list_items, e.g. |

## `dialog_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `dialog_list_events` |  | Read | `pid`*, `after_seq`, `limit` | Recent dialogs Revit announced through its DialogBoxShowing event (time, dialog_id, kind, message, during_tool), oldest first; seq numbers allow polling with after_seq. |

## `doc_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `doc_close` |  | Write | `pid`*, `path`, `title`, `document`, `activate_other`, `activate_document`, `save` | Close an open document in Revit pid WITHOUT saving (changes are discarded; it never saves). |
| `doc_info` |  | Read | `pid`*, `document` | Describe a document open in Revit pid: title, path, is_workshared, is_modifiable/modified, active view (name, id, type), project info, display units (length/area/volume/angle) and the Revit version/build. |
| `doc_list_open` |  | Read | `pid`* | List the documents open in Revit pid (title, path, active, linked, family, workshared, modified). |
| `doc_open` | yes | Write | `pid`*, `path`*, `detach_from_central`, `audit`, `activate`, `worksets` | Open a model COPY in Revit pid: the path must be in the sandbox (sandbox_copy_model); originals get SANDBOX_VIOLATION. |

## `gateway_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `gateway_get_info` | yes | Read | — | Describe the RevitAgent gateway: version, sandbox/run folders, current policy, macro names (macro_list) and all tools grouped by area with their permission. |

## `human_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `human_ask` | yes | Interact | `question`*, `options`*, `safe_option`*, `timeout_minutes`, `screenshot_path` | Ask the owner on Telegram with 2-6 buttons and wait. |

## `machine_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `machine_check_unattended` |  | Read | `year` | Check whether this machine is ready to run Revit tests unattended overnight: interactive desktop session, workstation lock, RDP state, sleep/hibernate timeouts on AC, pending reboot, elevation, Revit & Bridge install, al |

## `macro_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `macro_list` | yes | Read | `name` | Verified macros: fixed chains of gateway tools for common tasks (e.g. |
| `macro_run` | yes | Write | `name`*, `args`, `verbose` | Run a macro from macro_list. |

## `model_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `model_get_element` |  | Read | `pid`*, `document`, `id`, `unique_id`, `include_type_parameters` | Read one element by id or unique_id: name, category, class, type, level, workset, location (internal units, feet), bounding box and ALL parameters (name, storage type, value, display value, read_only, builtin, group, sha |
| `model_get_warnings` | yes | Read | `pid`*, `document`, `limit` | List the model's Revit warnings (Manage > Warnings): description, severity, failing and additional element ids. |
| `model_list_categories` |  | Read | `pid`*, `document`, `name_contains`, `type`, `limit` | List the categories of a model: name (localized), id, builtin (OST_* name, use it in model_query_elements), type (model/annotation/...), schedulable, subcategory_count. |
| `model_list_views` |  | Read | `pid`*, `document`, `type`, `name_contains`, `include_templates`, `limit` | List the views of a model (id, name, kind, view_type, level, sheet_number, is_active). |
| `model_query_elements` | yes | Read | `pid`*, `document`, `category`, `class`, `filters`, `fields`, `view_id`, `include_types`, `offset`, `limit` | Query model elements by category and/or class with parameter filters (name = parameter or BuiltInParameter; numbers in display units) and return fields: value (raw, lengths in feet), display, display_number/unit. |

## `notify_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `notify_status` |  | Read | — | Check whether Telegram notification and human escalation channel is configured and enabled. |

## `recorder_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `recorder_export` |  | Write | `name`*, `out_dir`, `overwrite`, `session_id` | Write this session's recording as a DRAFT scenario folder (default <gateway home>\scenarios\<name>\): scenario.yaml (consecutive ui_* actions become one 'ui' step with a generated UI script ui\NN-ui.json, other calls bec |
| `recorder_start` |  | Read | `name`, `restart` | Start recording THIS session's successful tool calls, to turn an exploration into a replayable scenario: UI actions (ui_click/type/select/toggle/keys/wait/expect...), Revit commands and writes, plus your verification rea |
| `recorder_stop` |  | Read | — | Stop recording this session. |

## `revit_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `revit_close` | yes | Admin | `pid`*, `discard_changes`, `grace_sec`, `force_foreign`, `confirm_pid`, `force_kill` | Close a Revit the gateway launched: WM_CLOSE, wait grace_sec, kill. |
| `revit_get_state` |  | Read | `pid`, `include_details`, `include_screenshot`, `run_id` | Is this Revit ready to be driven? Returns {pid, ready, state (ready \| busy = no main window yet \| modal \| not_responding \| exited), blocked, responding, main_window_enabled, bridge_pipe_available, launched_by_gateway |
| `revit_handle_dialog` | yes | Interact | `pid`*, `hwnd`*, `button`*, `discard_changes`, `verify_timeout_sec` | Click one button of a blocking Revit dialog (hwnd, button from revit_list_dialogs) and verify it closed. |
| `revit_launch` | yes | Admin | `year`, `exe_path`, `args`, `timeout_sec`, `settle_sec`, `tie_to_gateway`, `wait_for_bridge`, `escalate_dialogs` | Start a NEW gateway-owned Revit without a model and wait until ready; rule-matched info dialogs are answered, harmless lingering ones get Close/Cancel/Later (handled). |
| `revit_list_dialogs` | yes | Read | `pid`, `include_screenshot`, `limit` | Dialogs blocking a Revit: hwnd, title, text, buttons [{text, id, enabled}], category (info\|forbidden\|unknown), forbidden_reason, save_prompt. |
| `revit_list_installed` |  | Read | `year` | List the Revit versions installed on this machine (registry, uninstall entries, default folders; any drive): year, Revit.exe path, product version, add-in folders, and bridge_manifest_installed (the RevitAgent Bridge add |
| `revit_list_instances` | yes | Read | `year`, `launched_by_gateway`, `limit` | Running Revit processes: pid (for other tools), year, state (ready\|busy\|modal\|not_responding), launched_by_gateway (only those can be closed), bridge_pipe_available (Bridge loaded: doc/model tools work), active_docume |
| `revit_wait_ready` |  | Interact | `pid`*, `timeout_sec`, `settle_sec`, `phase`, `apply_rules`, `require_bridge`, `run_id`, `escalate_dialogs` | Wait until Revit is ready: main window present, enabled and responding, no dialog, stable for settle_sec. |
| `revit_watchdog_status` |  | Read | — | Inspect the Revit watchdog background service: watched processes (PIDs launched by the gateway), their liveness and hang states, configuration (hang_after_sec, enabled), and recent watchdog events (hang, recovered, crash |

## `ribbon_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `ribbon_list_items` |  | Read | `pid`*, `tab`, `panel`, `text_contains`, `include_hidden`, `postable_only`, `offset`, `limit` | List the Revit ribbon as the user sees it, INCLUDING third-party add-in tabs: tabs (id, title, visible, contextual, item_count) and flattened items (tab, panel, id, text, tooltip, kind, enabled, visible, command_id, post |

## `runs_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `runs_get` |  | Read | `run_id`* | Get full run metadata (run.json) and a recursive list of all files with their byte sizes for a run_id. |
| `runs_list` |  | Read | `scenario`, `verdict`, `from`, `to`, `limit` | List runs (newest first) from the run data store index and crash-safe folder scan: run id, scenario, verdict, trigger, start/end timestamps, folder paths and report links. |

## `sandbox_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `sandbox_copy_model` | yes | Write | `source_path`*, `run_id`, `extra_files`, `name` | Copy a Revit file (.rvt/.rfa/.rte/.rft) and extra_files into a sandbox run, so you never work on the original (only read; sha256 proves it: original_unchanged). |
| `sandbox_create_run` |  | Write | `name`, `purpose` | Create a sandbox run folder <runs>\<yyyyMMdd-HHmmss>-<slug>\ with data\ (outputs), evidence\ (screenshots, reports) and run-info.json. |
| `sandbox_list_runs` |  | Read | `contains`, `limit` | List sandbox runs, newest first: run_id, path, created_at, name, purpose, models (copies made with sandbox_copy_model), copy_count. |

## `scenario_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `scenario_list_runs` |  | Read | `scenario`, `verdict`, `limit` | List recent scenario_run runs (newest first) from the sandbox runs folder: run id, scenario, verdict, start/end, RUN_DIR and report paths. |
| `scenario_run` |  | Write | `scenario_dir`, `scenario_path`, `pid`, `launch`, `keep_open`, `dataset` | Run a scenario (scenario.yaml: steps + checks) end to end WITHOUT AI and evaluate its checks: creates a RUN_DIR in the sandbox, attaches to Revit (pid) or launches revit_version (launch: true), copies the model into RUN_ |
| `scenario_validate` |  | Read | `scenario_dir`, `scenario_path`, `dataset` | Lint a scenario folder (scenario.yaml + ui\*.json UI scripts) without touching Revit: YAML errors with line numbers, steps calling tools unknown to this gateway or forbidden in scenarios (Admin, scenario_*, recorder_*),  |

## `schedule_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `schedule_create` |  | Write | `pid`*, `document`, `category`*, `fields`*, `sort_by`, `sort_descending`, `name`, `activate` | Create a schedule view in a sandbox copy (else SANDBOX_VIOLATION) in one transaction; any error rolls it back. |
| `schedule_list_schedulable_categories` |  | Read | `pid`*, `document`, `category`, `name_contains`, `limit` | List the categories Revit can build a schedule for (ViewSchedule.GetValidCategoriesForSchedule). |
| `schedule_read` |  | Read | `pid`*, `document`, `view_id`, `name`, `offset`, `limit` | Read a schedule view as text exactly as Revit displays it: fields (name, heading, hidden), header (title rows) and body. |

## `selection_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `selection_get` |  | Read | `pid`*, `limit` | Return the elements currently selected in the active document (id, unique_id, name, category, class) and the total count. |
| `selection_set` |  | Interact | `pid`*, `ids`* | Replace the selection in the active document with the given element ids ([] clears it). |

## `ui_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `ui_click` | yes | Interact | `pid`, `window`, `hwnd`, `ref`, `selector`, `timeout_s`, `button`, `double`, `mouse`, `x`, `y` | Click an element (ref or selector): UI Automation patterns first (no focus needed), else a real mouse click. |
| `ui_expand` |  | Interact | `pid`, `window`, `hwnd`, `ref`, `selector`, `timeout_s`, `collapse` | Expand (or collapse=true) a tree item, combo box, expander, menu or collapsed Revit ribbon panel / split button (ExpandCollapse pattern). |
| `ui_expect` |  | Read | `pid`, `window`, `hwnd`, `ref`, `selector`, `is`, `text`, `exact`, `match`, `count`, `timeout_s` | Retrying UI assertion: checks an element (ref/selector) or the window (none) until it passes or timeout_s (default 5) elapses, and returns {pass, message, actual} — a failed check is data, not an error. |
| `ui_find` |  | Read | `pid`, `window`, `hwnd`, `selector`*, `limit`, `include_offscreen`, `timeout_s` | Find elements matching a selector in a window and give them refs (added to the window's current refs). |
| `ui_inspect` |  | Read | `pid`, `window`, `hwnd`, `ref`, `selector` | All UI Automation properties of one element (or of the window when no ref/selector): type, name, automation_id, class, framework, bounds (screen and window-relative), enabled/offscreen/focused, supported patterns, value, |
| `ui_keys` |  | Interact | `pid`, `window`, `hwnd`, `ref`, `selector`, `keys`* | Send a key sequence (SendKeys syntax) to a window after bringing it to the foreground: {TAB} {ENTER} {ESC} {F2} {DOWN 3}, ^a = Ctrl+A, +{TAB} = Shift+Tab, %f = Alt+F, ~ = Enter. |
| `ui_list_windows` |  | Read | `pid`, `title`, `include_untitled`, `limit` | List visible top-level windows (z-order, topmost first): hwnd, title, class, pid, owner, is_modal_dialog (#32770 or an owned window whose owner is disabled), enabled, bounds, is_main. |
| `ui_read_table` |  | Read | `pid`, `window`, `hwnd`, `ref`, `selector`, `limit` | Read a grid/table/list element as headers + rows of cell text (GridPattern/TablePattern, else row-like children: DataItem/ListItem rows, WinForms DataGridView). |
| `ui_run_script` |  | Interact | `pid`*, `script_path`, `script`, `out_dir`, `vars`, `screenshots`, `validate_only`, `timeout_s` | Run a UI script against one process: {name, timeout (s per step, default 10), screenshots: fail\|each\|none, vars, dialogs (rules file(s) or array), steps: [{action, name, optional, window, ref\|selector, timeout_s, ...t |
| `ui_screenshot` | yes | Read | `pid`, `window`, `hwnd`, `ref`, `selector`, `save_path`, `max_width`, `include_image` | PNG of a window or element (ref/selector), scaled to max_width: {path, scale, method, captured_window}. |
| `ui_scroll` |  | Interact | `pid`, `window`, `hwnd`, `ref`, `selector`, `timeout_s`, `direction`, `amount` | Scroll a list / grid / pane (the target or its nearest scrollable ancestor; Scroll pattern, mouse wheel as fallback): direction down\|up\|pagedown\|pageup\|left\|right with amount steps, or direction=into to scroll the t |
| `ui_select` |  | Interact | `pid`, `window`, `hwnd`, `ref`, `selector`, `timeout_s`, `item`, `index` | Select an item: with 'item' (name, exact first then contains) or 'index' (0-based), picks it inside a combo box / list / tab control / tree (a combo is expanded and collapsed again); without them, selects the target itse |
| `ui_snapshot` | yes | Read | `pid`, `window`, `hwnd`, `ref`, `selector`, `interactive_only`, `max_depth`, `max_nodes`, `bounds`, `include_offscreen`, `raw` | UI Automation tree of a window as text, one element per line with a ref (e12) for actions. |
| `ui_toggle` |  | Interact | `pid`, `window`, `hwnd`, `ref`, `selector`, `timeout_s`, `state` | Toggle a check box / toggle button (Toggle pattern). |
| `ui_type` | yes | Interact | `pid`, `window`, `hwnd`, `ref`, `selector`, `timeout_s`, `text`, `keyboard`, `clear`, `append`, `verify` | Type into an edit field (ref or selector; none = the focused element). |
| `ui_wait` |  | Read | `pid`, `window`, `hwnd`, `ref`, `selector`, `state`, `timeout_s` | Wait until a window appears / is gone (no ref/selector: e.g. |

## `view_*`

| Tool | Core | Permission | Parameters | Description (short) |
|---|---|---|---|---|
| `view_activate` | yes | Interact | `pid`*, `document`, `view_id`, `name`, `type` | Make a view of the active document the active view (view_id, or exact name plus optional type). |
| `view_export_image` |  | Read | `pid`*, `document`, `view_id`, `name`, `pixel_width`, `save_path` | Render a view to PNG with the Revit API (ExportImage): independent of the screen, so it works when Revit is covered, minimized, or the session is locked or RDP-disconnected. |
