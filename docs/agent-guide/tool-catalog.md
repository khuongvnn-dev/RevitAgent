# RevitAgent tool catalog

67 tools; 18 are in the `core` profile (column **Core**, the default for the Shim). Regenerate with `RevitAgent.E2E catalog` (uses `--profile full`); the table below was rebuilt by hand from the tool definitions in `src/RevitAgent.Core`, so re-run the generator to be sure it is current.

Permission: Read (observe only) | Interact (UI / commands) | Write (changes the model or files in the sandbox) | Admin (processes, install). `*` = required parameter. Every Bridge tool (`doc_*`, `model_*`, `schedule_*`, `ribbon_*`, `command_*`, `view_*`, `selection_*`, `dialog_*`) needs the Bridge loaded in the target Revit and a `pid`. Tools outside `core` still run when a macro or scenario calls them; list them to the model with `--profile full`.

## `addin_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `addin_allowlist_get` |  | Read | — | List the add-ins the owner trusts (name + path pattern). The gateway answers the unsigned add-in prompt of a Revit it launched from this list; agents cannot change it. |

## `bridge_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `bridge_install` |  | Admin | `years` | Install or update the Bridge add-in for the given Revit years (default: all installed); pre-trusts it so Revit shows no unsigned prompt. A running Revit needs a restart. Needs the owner's approval (dashboard or Telegram), else PERMISSION_DENIED. |
| `bridge_list_installs` |  | Read | — | Show the Bridge installation per installed Revit year: manifest, assembly, trust, up_to_date, running pids with bridge_loaded. |
| `bridge_status` |  | Read | `pid` | Check the Bridge in one Revit (or all): pipe_present, reachable, versions, busy tool. Answers even while Revit is busy. |
| `bridge_uninstall` |  | Admin | `years` | Remove the Bridge manifest, trust value and unlocked copies for the given years (default: all). Other add-ins are never touched. Needs the owner's approval (dashboard or Telegram), else PERMISSION_DENIED. |

## `command_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `command_list_postable` |  | Read | `pid`*, `name_contains`, `limit` | List Revit's PostableCommand names that `command_post` can run (for example Wall, ScheduleOrQuantities). |
| `command_post` |  | Interact | `pid`*, `command`* | Post a PostableCommand or a ribbon `command_id` as if the user clicked it. Returns at once; observe the UI next to see what happened. |

## `dialog_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `dialog_list_events` |  | Read | `pid`*, `after_seq`, `limit` | Recent dialogs announced through Revit's DialogBoxShowing event (record-only; poll with `after_seq`). |

## `doc_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `doc_close` |  | Write | `pid`*, `path`, `title`, `document`, `activate_other`, `activate_document`, `save` | Close an open document WITHOUT saving. Use `activate_other` when it is the active document. |
| `doc_info` |  | Read | `pid`*, `document` | Describe a document: title, path, workshared, active view, project info, units, Revit version/build. |
| `doc_list_open` |  | Read | `pid`* | List the documents open in Revit (title, path, active, linked, workshared, modified). |
| `doc_open` | yes | Write | `pid`*, `path`*, `detach_from_central`, `audit`, `activate`, `worksets` | Open a sandbox COPY of a model (originals are refused with SANDBOX_VIOLATION). Can take minutes; harmless dialogs are handled. |

## `gateway_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `gateway_get_info` | yes | Read | — | Gateway version, folders, policy, session `profile`, `tools_listed` / `tools_total`, `hint`, `macros`, and tools by area with permission. Start here. |

## `human_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `human_ask` | yes | Interact | `question`*, `options`*, `safe_option`*, `timeout_minutes`, `screenshot_path` | Ask the owner on Telegram with 2-6 buttons and wait; `safe_option` is used on timeout or without Telegram. |

## `machine_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `machine_check_unattended` |  | Read | `year` | Check whether this machine can run Revit unattended overnight (session, lock, sleep, reboot, installs, allowlist, disk, Telegram). Read-only. |

## `macro_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `macro_list` | yes | Read | `name` | List verified macros (name, summary, requires, params); with `name`, the full detail of one. |
| `macro_run` | yes | Write | `name`*, `args`, `verbose` | Run a macro. Each step is a normal tool call with full policy / sandbox / audit; stops at the first failed step. |

## `model_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `model_get_element` |  | Read | `pid`*, `document`, `id`, `unique_id`, `include_type_parameters` | Read one element by id or unique_id with all its parameters, location and bounding box. |
| `model_get_warnings` | yes | Read | `pid`*, `document`, `limit` | List the model's Revit warnings (read-only). |
| `model_list_categories` |  | Read | `pid`*, `document`, `name_contains`, `type`, `limit` | List categories (name, OST_ id, type, schedulable, subcategory count). |
| `model_list_views` |  | Read | `pid`*, `document`, `type`, `name_contains`, `include_templates`, `limit` | List views (id, name, kind, level, sheet number, is_active); templates excluded by default. |
| `model_query_elements` | yes | Read | `pid`*, `document`, `category`, `class`, `filters`, `fields`, `view_id`, `include_types`, `offset`, `limit` | Query elements by category and/or class with parameter filters; returns the chosen `fields`, `total`, `truncated`. |

## `notify_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `notify_status` |  | Read | — | Is the Telegram channel configured and enabled? Never reveals the bot token. |

## `recorder_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `recorder_export` |  | Write | `name`*, `out_dir`, `overwrite`, `session_id` | Write the session recording as a draft scenario folder (`scenario.yaml`, `ui\*.json`) with checks and TODOs; review, validate, then replay. |
| `recorder_start` |  | Read | `name`, `restart` | Start recording this session's successful calls, to turn an exploration into a scenario. |
| `recorder_stop` |  | Read | — | Stop recording; returns the call counts per tool. The recording stays in memory for `recorder_export`. |

## `revit_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `revit_close` | yes | Admin | `pid`*, `discard_changes`, `grace_sec`, `force_foreign`, `confirm_pid`, `force_kill` | Close a Revit the gateway launched (WM_CLOSE, grace, kill). Never saves. A foreign Revit needs `force_foreign` + `confirm_pid`. |
| `revit_get_state` |  | Read | `pid`, `include_details`, `include_screenshot`, `run_id` | Is this Revit ready? State, blockers (dialogs, modal windows) with category info / forbidden / unknown. |
| `revit_handle_dialog` | yes | Interact | `pid`*, `hwnd`*, `button`*, `discard_changes`, `verify_timeout_sec` | Click one button of one blocking dialog and verify it closed. Forbidden dialogs are refused. |
| `revit_launch` | yes | Admin | `year`, `exe_path`, `args`, `timeout_sec`, `settle_sec`, `tie_to_gateway`, `wait_for_bridge`, `escalate_dialogs` | Start a NEW gateway-owned Revit without a model and wait until ready. Only when the user asks. 30-180 s. |
| `revit_list_dialogs` | yes | Read | `pid`, `include_screenshot`, `limit` | Dialogs blocking a Revit: hwnd, title, text, buttons, category, forbidden_reason. |
| `revit_list_installed` |  | Read | `year` | List installed Revit versions (year, Revit.exe path, add-in folders, Bridge manifest installed). |
| `revit_list_instances` | yes | Read | `year`, `launched_by_gateway`, `limit` | Running Revit processes: pid, year, state, launched_by_gateway, bridge_pipe_available. Sends nothing to Revit. |
| `revit_wait_ready` |  | Interact | `pid`*, `timeout_sec`, `settle_sec`, `phase`, `apply_rules`, `require_bridge`, `run_id`, `escalate_dialogs` | Wait until Revit is ready (no dialog, stable for `settle_sec`); answers rule-matched info dialogs; stops at forbidden ones. |
| `revit_watchdog_status` |  | Read | — | Watchdog state: watched gateway-launched processes, hang / crash events. |

## `ribbon_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `ribbon_list_items` |  | Read | `pid`*, `tab`, `panel`, `text_contains`, `include_hidden`, `postable_only`, `offset`, `limit` | List the ribbon including add-in tabs: tab, panel, id, text, `command_id`, postable. |

## `runs_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `runs_get` |  | Read | `run_id`* | Full metadata (`run.json`) and file list of one run. |
| `runs_list` |  | Read | `scenario`, `verdict`, `from`, `to`, `limit` | List runs (newest first) from the run store; filter by scenario, verdict, date. |

## `sandbox_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `sandbox_copy_model` | yes | Write | `source_path`*, `run_id`, `extra_files`, `name` | Copy a model (+ extra files) into a run, creating the run folder if `run_id` is omitted; returns `copy_path` and SHA-256 proof the original is unchanged. |
| `sandbox_create_run` |  | Write | `name`, `purpose` | Create a run folder (`data\`, `evidence\`, `run-info.json`). Optional: `sandbox_copy_model` creates its own. |
| `sandbox_list_runs` |  | Read | `contains`, `limit` | List sandbox runs, newest first, with their model copies. |

## `scenario_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `scenario_list_runs` |  | Read | `scenario`, `verdict`, `limit` | List recent `scenario_run` runs: id, scenario, verdict, times, run folder and report paths. |
| `scenario_run` |  | Write | `scenario_dir`, `scenario_path`, `pid`, `launch`, `keep_open`, `dataset` | Run a `scenario.yaml` end to end without AI on a sandbox copy, evaluate its checks, write the reports. Minutes to hours; one at a time. |
| `scenario_validate` |  | Read | `scenario_dir`, `scenario_path`, `dataset` | Lint a scenario folder without touching Revit (YAML, unknown / forbidden tools, missing files, check syntax). |

## `schedule_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `schedule_create` |  | Write | `pid`*, `document`, `category`*, `fields`*, `sort_by`, `sort_descending`, `name`, `activate` | Create a schedule view (category, fields, optional sort) in a sandbox copy in one transaction; SANDBOX_VIOLATION otherwise. Read it with `schedule_read`. |
| `schedule_list_schedulable_categories` |  | Read | `pid`*, `document`, `category`, `name_contains`, `limit` | List categories Revit can schedule; with `category`, check one (`check.schedulable`). |
| `schedule_read` |  | Read | `pid`*, `document`, `view_id`, `name`, `offset`, `limit` | Read a schedule as displayed: `fields`, header, `body` {header_rows, header_cells, data_rows (total), offset, returned_rows, truncated, rows}. `offset` skips data rows to page through large schedules (`truncated`: more rows follow). |

## `selection_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `selection_get` |  | Read | `pid`*, `limit` | Elements currently selected in the active document, with the total count. |
| `selection_set` |  | Interact | `pid`*, `ids`* | Replace the selection with the given element ids (`[]` clears). The model is not modified. |

## `ui_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `ui_click` | yes | Interact | `pid`, `window`, `hwnd`, `ref`, `selector`, `timeout_s`, `button`, `double`, `mouse`, `x`, `y` | Click an element (`ref` or `selector`) or window pixel x/y; UI Automation patterns first, mouse as fallback. |
| `ui_expand` |  | Interact | `pid`, `window`, `hwnd`, `ref`, `selector`, `timeout_s`, `collapse` | Expand or collapse a tree item, combo, expander, menu or ribbon panel. |
| `ui_expect` |  | Read | `pid`, `window`, `hwnd`, `ref`, `selector`, `is`, `text`, `exact`, `match`, `count`, `timeout_s` | Retrying UI assertion on an element or window; returns {pass, message, actual}. |
| `ui_find` |  | Read | `pid`, `window`, `hwnd`, `selector`*, `limit`, `include_offscreen`, `timeout_s` | Find elements matching a selector and give them refs. |
| `ui_inspect` |  | Read | `pid`, `window`, `hwnd`, `ref`, `selector` | All UI Automation properties of one element or window. |
| `ui_keys` |  | Interact | `pid`, `window`, `hwnd`, `ref`, `selector`, `keys`* | Send a SendKeys sequence to a window brought to the foreground. Alt+F4 on a main window is refused. |
| `ui_list_windows` |  | Read | `pid`, `title`, `include_untitled`, `limit` | List visible top-level windows (hwnd, title, pid, owner, is_modal_dialog); `pid` shows one process incl. add-in dialogs. |
| `ui_read_table` |  | Read | `pid`, `window`, `hwnd`, `ref`, `selector`, `limit` | Read a grid / table / list as headers + rows of cell text. |
| `ui_run_script` |  | Interact | `pid`*, `script_path`, `script`, `out_dir`, `vars`, `screenshots`, `validate_only`, `timeout_s` | Run a JSON UI script (steps = `ui_*` tool calls) against one process; returns status passed \| failed \| blocked, counts, `failed_steps`, per-step results, report paths. |
| `ui_screenshot` | yes | Read | `pid`, `window`, `hwnd`, `ref`, `selector`, `save_path`, `max_width`, `include_image` | PNG of a window or element scaled to `max_width`; returns {path, scale, method, captured_window}. `method` is `print_window`, `revit_api` (Revit canvas printed black: the Bridge renders the active model view) or `screen` (the window rectangle only, after checking nothing covers it); otherwise `TARGET_NOT_READY` with the reason. |
| `ui_scroll` |  | Interact | `pid`, `window`, `hwnd`, `ref`, `selector`, `timeout_s`, `direction`, `amount` | Scroll a list / grid / pane, or scroll an item into view. |
| `ui_select` |  | Interact | `pid`, `window`, `hwnd`, `ref`, `selector`, `timeout_s`, `item`, `index` | Select an item in a combo / list / tab / tree (by `item` or `index`), or the target itself. |
| `ui_snapshot` | yes | Read | `pid`, `window`, `hwnd`, `ref`, `selector`, `interactive_only`, `max_depth`, `max_nodes`, `bounds`, `include_offscreen`, `raw` | UI Automation tree of a window (or subtree) as text, one element per line with a `ref`. |
| `ui_toggle` |  | Interact | `pid`, `window`, `hwnd`, `ref`, `selector`, `timeout_s`, `state` | Toggle a check box / toggle button; `state` on\|off is idempotent. |
| `ui_type` | yes | Interact | `pid`, `window`, `hwnd`, `ref`, `selector`, `timeout_s`, `text`, `keyboard`, `clear`, `append`, `verify` | Type into an edit field (sets the value; `keyboard` sends real keystrokes). Password fields are refused. |
| `ui_wait` |  | Read | `pid`, `window`, `hwnd`, `ref`, `selector`, `state`, `timeout_s` | Wait until a window or element appears / is gone / is enabled. |

## `view_*`

| Tool | Core | Permission | Parameters | Description |
|---|---|---|---|---|
| `view_activate` | yes | Interact | `pid`*, `document`, `view_id`, `name`, `type` | Make a view of the active document the active view (`view_id`, or exact `name` + `type`). |
| `view_export_image` |  | Read | `pid`*, `document`, `view_id`, `name`, `pixel_width`, `save_path` | Render a view (default: the active one) to PNG with the Revit API. Works when Revit is covered, minimized, locked or RDP-disconnected. Returns {path, width, height, view_id, view_name, document}; saved in the session's current run `evidence` folder (else captures) or at `save_path` inside captures/runs (else `SANDBOX_VIOLATION`); cite the returned path. Not in the `core` profile. |
