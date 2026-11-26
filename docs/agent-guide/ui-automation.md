# UI automation (`ui_*` tools)

Drive the UI of Revit and of the add-ins loaded in it **out of process**, without their source code, with
Windows UI Automation (FlaUI.UIA3). The model is "snapshot → ref → act → verify": observe the accessibility
tree, act on an element by ref or selector, then check the result. Fixed sequences can be saved and replayed
as UI scripts (§7).

Code: `src/RevitAgent.Core/Ui/` · tests: `tests/RevitAgent.Tests/Ui/` + `tests/RevitAgent.UiTestApp/`.

## 1. The loop

```text
ui_list_windows pid=…            which windows does the process have? (main window, add-in dialogs, TaskDialogs)
ui_snapshot pid=… [window=…]     tree with refs:  - Button "OK" [ref=e7] id=btnOk (invoke)
ui_click ref=e7  | selector=id=btnOk
ui_expect selector=id=lblStatus text="Saved"      → {pass, message, actual}   (a failed check is data, not an error)
ui_screenshot pid=…               when the tree does not tell you enough (canvas, custom drawing)
```

Never assume an action worked: every action returns what it did (`action`: invoked / toggled / selected /
set_value / keyboard / mouse …) and `target` — re-observe or `ui_expect` afterwards. Verify model changes
through the Bridge (`model_*`, `schedule_read`) when it is loaded: the UI can lie, the model does not.

## 2. Targeting (all `ui_*` tools)

| Argument | Meaning |
|---|---|
| `pid` | Target process. Without `window`, the **main window** = not owned, has a title, largest area (Revit's hidden `…Monitor` windows and ribbon tooltips are skipped). All top-level windows of the pid are searched, so add-in dialogs (separate top-level windows) are found with `window`. |
| `window` | Title substring (exact title wins, case-insensitive, whitespace-normalized), `/regex/`, `hwnd:0x1A2B`, `0x1A2B`, `pid:1234`, `proc:Revit`, `class:#32770`. With `pid` only that process's windows match. |
| `hwnd` | Window handle from `ui_list_windows` (`"0x1A2B"` or integer). |
| `ref` | `e12` from the latest `ui_snapshot` / `ui_find` of that window. |
| `selector` | See §3. |

No targeting at all → the window of your last snapshot/find in this MCP session. Refs are stored per
**(session, window)**; a new snapshot of a window replaces its refs (ui_find adds to them). A ref given with
only `pid` resolves against the most recent snapshot of that process. A stale ref fails fast (`NOT_FOUND`
"stale"), except when the element was re-created and a unique match on type + id + name exists.

Every action result echoes `target: {selector, ref, window, hwnd, pid, is_main_window, element}` — the
`selector` is the one you passed, or (for refs) a computed stable one (`id=…` preferred, `;index=n` only when
needed). The scenario recorder turns these into UI script steps.

## 3. Selectors

`key=value` pairs joined by `;` (or spaces when the next pair starts with a known key):

| Key | Match |
|---|---|
| `id` | AutomationId, exact, case-sensitive (`id~=` contains) |
| `name` | Name, exact, case-insensitive, whitespace-normalized (`name~=` contains) |
| `type` | Control type: Button, Edit, Text, ComboBox, ListItem, TabItem, TreeItem, CheckBox, MenuItem, DataItem, Window, Pane, … |
| `class` | ClassName (`class~=` contains) |
| `index` | 0-based among matches (onscreen first, document order) |
| `in` | Search only inside a container: `in=e12` or `in=id=gridOrders` |
| `from=end` | Reverse document order |

A value written `/…/` is a regular expression. Plain text without `key=` means *name contains*.
Aliases: `automationid`, `controltype`/`role`, `text`, `classname`, `nth`.
Examples: `id=btnOk` · `name="OK" type=Button` · `type=TabItem;name=Architecture` · `name~=Save;index=1`.

Searches are bounded (time budget, 100 000 elements), skip offscreen subtrees unless asked, and report
`stopped` when partial — narrow with `in=` or a closer `window`.

## 4. Snapshot format

```text
- Window "RevitAgent UI Test App" [ref=e1]
  - Text "Ready" [ref=e2] id=lblStatus
  - Edit [ref=e4] id=txtName (type)
  - Edit [ref=e5] id=txtPassword password (type)
  - CheckBox "I agree" [ref=e8] id=chkAgree unchecked (toggle)
  - Button "Submit" [ref=e9] id=btnSubmit disabled (invoke)
```

Attributes: `label="…"` (visible text of WPF buttons/items whose Name is empty or a view-model type),
`id=`, `value="…"` (never for password fields), `disabled`, `focused`, `checked`/`unchecked`/`mixed`,
`collapsed`/`expanded`, `selected`, `offscreen`, `box=x,y,w,h` (with `bounds=true`, window-relative pixels).
Actions in parentheses: `invoke`, `toggle`, `expand`, `select`, `type`, `range`, `scroll`, `text`.
Default: interactive elements only, 400 nodes, depth 25; the JSON header lists `also_open` windows of the
same process (modal dialogs!). Snapshot a subtree (`ref`/`selector`) when a window is large.

## 5. Tools

| Tool | Permission | What it does |
|---|---|---|
| `ui_list_windows` | Read | Top-level windows (hwnd, title, class, owner, `is_modal_dialog`, enabled, bounds, `is_main`); works on a locked desktop and says whether UI automation is possible. |
| `ui_snapshot` | Read | Tree text with refs (§4). |
| `ui_find` | Read | Selector → matches with refs. |
| `ui_inspect` | Read | All properties, supported patterns, values, bounds (screen + window-relative), default action. |
| `ui_read_table` | Read | Grid/table/list → `headers` + `rows` (GridPattern/TablePattern, else row children; WinForms DataGridView "Top Row"). Virtualized grids return realized rows only. |
| `ui_screenshot` | Read | PNG of the window or an element, returned scaled to ≤ `max_width` (1600) + `{path, width, height, scale}`; saved in the session's current run `evidence\` folder (see [run-data.md](run-data.md)), else under `<home>\captures`, or at `save_path` inside the gateway captures/runs folders. Report the `path` of the result; it is listed in `evidence\index.jsonl`. Every result has `method` and `captured_window {pid, title}`. See "Screenshot methods" below. Never activates or restores the window. |
| `ui_wait` | Read | Window appears / gone, element appears / gone / enabled; `TIMEOUT` error with the last state. |
| `ui_expect` | Read | Retrying assertion → `{pass, message, actual, target}`; `is` (exists, gone, enabled, disabled, visible, hidden, focused, checked, unchecked, mixed, selected, expanded, collapsed), `text` (+`exact`), `match` (regex), `count`. |
| `ui_click` | Interact | Pattern first: Invoke → Toggle → SelectionItem → ExpandCollapse → default action; then a real mouse click. `button=right`, `double=true`, `mouse=true` force the mouse. `x`,`y` without ref/selector = window-relative pixels (canvas). |
| `ui_type` | Interact | Value pattern (`append`, `verify`); `keyboard=true` real Unicode keystrokes after Ctrl+A; `clear=true`; no target = focused element. Refuses password fields. |
| `ui_select` | Interact | `item` (name: exact, then contains) or `index` inside a combo/list/tab/tree (expands and collapses combos); no item = select the target itself (a ribbon tab). |
| `ui_toggle` | Interact | `state=on/off` (idempotent) or flip. |
| `ui_expand` | Interact | Expand / `collapse=true`. |
| `ui_keys` | Interact | SendKeys syntax: `{TAB}{ENTER}{ESC}{F2}{DOWN 3}`, `^a`, `+{TAB}`, `%f`, `~`, `^(ac)`; optional element to focus first. Alt+F4 on the main window is refused. |
| `ui_scroll` | Interact | `down/up/pagedown/pageup/left/right` × `amount` (Scroll pattern on the target or an ancestor, mouse wheel fallback) or `into` (ScrollIntoView). |
| `ui_run_script` | Interact | Runs a UI script (§7). |

**Screenshot methods.** `ui_screenshot` never returns pixels of another window. Chain, first match wins:

1. `print_window`: PrintWindow of the target window (works when covered). Used when the image is not blank.
2. `revit_api`: the image is black, the target is Revit's main window and the Bridge is loaded in that pid. The gateway calls `view_export_image` for the active view. The image shows the model view only (no ribbon, palettes or dialogs) and its pixels do not map to `ui_click` x/y.
3. `screen`: the image is black and (2) does not apply. Screen pixels of the window rectangle only, and only if the session renders (not locked, not a disconnected Remote Desktop session), the window is not minimized, and a grid of points across the rectangle all hit the target window (checked again after the copy).
4. Otherwise `TARGET_NOT_READY` with `details.reason` = `covered`, `minimized`, `session_locked` or `desktop_not_rendered`, and a hint to use `view_export_image`. Other windows' titles are never reported.

The `from_screen` parameter no longer exists. To check the Revit model view, call `view_export_image` directly: it renders through the Revit API, so it works when Revit is covered, minimized, locked or RDP-disconnected, and returns `{path, width, height, view_id, view_name, document}`. Check `method` and `captured_window` before treating an image as evidence.

Actions auto-wait (`timeout_s`, default 5 s) until the target exists and is enabled. All UIA work runs on a
worker thread with a wall-clock budget; one global input lock serializes actions (`BUSY` after 20 s).

## 6. Revit specifics

- **Ribbon** (verified on Revit 2024.3, 2026-11-20): tabs are **Buttons whose AutomationId is the tab name**
  (`ui_click selector="id=View"`, `id=Architecture`, `id=Add-Ins`) — not `TabItem`. Buttons expose
  AutomationIds like `ID_OBJECTS_WALL` (`ui_click selector=id=ID_OBJECTS_WALL`) — prefer ids over names
  (names are localized). Collapsed panels / split buttons: `ui_expand` first. Add-in ribbon buttons carry the
  id their add-in gave them. With the Bridge loaded, `ribbon_list_items` / `command_post` are more robust.
- **TaskDialogs** (Revit's own and the API's `TaskDialog`): separate top-level windows of the Revit pid
  (`ui_list_windows pid=…`); buttons are `id=CommandButton_<n>` (1 OK, 2 Cancel, 6 Yes, 7 No, 8 Close) and
  command links `id=CommandLink_1001…`. Win32 dialogs/MessageBoxes are class `#32770`
  (`is_modal_dialog: true`). Never answer security / unsigned add-in / license / recovery / delete /
  overwrite / replace dialogs — ask the user.
- **Add-in windows** (WPF/WinForms) live in the Revit process: `ui_wait pid=… window="Sheet Configure"`,
  then target them with `window`. WPF `x:Name` / `AutomationProperties.AutomationId`, WinForms `Name` become
  the `id`.
- **Drawing canvas** has no UIA tree: `ui_screenshot`, find the spot, `ui_click x=… y=…` with
  x = image pixel / `scale` (only for `method` `print_window` or `screen`). Check the result in the model through the Bridge.
- **Busy Revit**: UIA calls block while Revit runs a command; you get `TARGET_NOT_READY` ("target UI not
  responding") instead of a hang. A button that opens a modal dialog may report `blocked: true`.

## 7. UI scripts (`ui_run_script`)

A UI script is a JSON file (or inline object) that replays UI steps against one process and writes a
report. A step is a call of a UI tool: `action` is the tool name without the `ui_` prefix and the other keys
are that tool's arguments. Property names are case-insensitive.

```json
{
  "name": "Open settings, change Scale min, cancel",
  "timeout": 15,
  "screenshots": "fail",
  "vars": { "scale": "45" },
  "steps": [
    { "action": "click", "selector": "id=ID_SHEET_CONFIGURE" },
    { "action": "wait", "window": "Sheet Configure", "timeout_s": 30 },
    { "action": "type", "selector": "id=txtScaleMin", "text": "${scale}", "keyboard": true, "verify": true },
    { "action": "expect", "selector": "id=txtJsonPreview", "match": "\"ScaleMin\"\\s*:\\s*45" },
    { "action": "screenshot", "save_path": "${env:REVITAGENT_UI_OUT}\\settings.png", "optional": true },
    { "action": "click", "window": "Sheet Configure", "selector": "id=btnCancel" },
    { "action": "expect", "window": "Sheet Configure", "is": "gone" }
  ]
}
```

- Tool arguments: `pid` (required), `script_path` or inline `script`, `out_dir` (inside the gateway
  runs/captures folders; default `<current run>\evidence\ui-scripts\<name>_<time>`, else `<home>\runs\ui-scripts\<name>_<time>`), `vars`, `screenshots`,
  `validate_only`, `timeout_s` (600).
- Top-level keys: `name`, `timeout` (seconds per step, default 10), `screenshots` (`fail` | `each` | `none`),
  `vars`, `dialogs`, `steps`.
- Actions: `list_windows snapshot find inspect screenshot click type select toggle expand keys scroll wait
  expect read_table`, with the argument names of the matching tool (`ref` or `selector`, `window`, `text`,
  `item`, `index`, `state`, `is`, `match`, `exact`, `count`, `button`, `double`, `mouse`, `x`, `y`,
  `keyboard`, `clear`, `append`, `verify`, `keys`, `direction`, `amount`, `collapse`, `limit`, ...).
  Right-click and double-click are `button: "right"` and `double: true`. Differences: `timeout_s` is the
  step's auto-wait; `screenshot` and `read_table` write to `save_path` (PNG / CSV) inside the run folders;
  a `ref` only works after a `snapshot` or `find` step of the same script.
- Two control actions with no tool: `attach` (`window` becomes the current window) and `pause` (`ms`).
- Every step may also carry `name` (its label in the report) and `optional`.
- Variables: `${name}` comes from the script `vars`, the `vars` argument, or `${dir}`. `${env:NAME}` reads
  the environment only for names starting with `REVITAGENT_UI_`; every other name expands to an empty string,
  so a script cannot read the gateway's secrets. `REVITAGENT_UI_HOST_PID` is the target pid and
  `REVITAGENT_UI_OUT` is the output folder.
- The run starts at the pid's main window. `attach`, and `wait` / `snapshot` / `find` with a `window`, make
  that window current; when it closes, steps fall back to the main window.
- Dialog rules (`{name, title, text?, proc?, click | keys, mouse?}`) run inside every auto-wait, `wait` and
  `expect` loop, only for windows of the pid, and never for a window whose title or text contains a forbidden
  word. Such a dialog is left for a human and the step in front of it ends `blocked`.
- Step outcome: every step is required unless `optional: true`. Step status is `passed`, `failed`,
  `skipped` (after the run stopped) or `blocked` (a forbidden dialog or a safety refusal). The first failed or
  blocked required step stops the run; an optional step that fails is recorded and the run goes on.
- Run verdict: `passed`, `failed` (a required step failed) or `blocked` (a required step was blocked).
- Report: `report.json`, `report.md`, `NN_<status>.png`, `NN_<status>_snapshot.txt`. The tool result has
  `status` (the verdict), `passed`, `failed`, `blocked`, `optional_failed`, `skipped`, `failed_steps`
  (numbers of the required steps that failed or were blocked), `report_path`, and per-step `status`,
  `observed`, `expected` (for `expect` and `wait`) and `error`.

### Rejected before running

`PERMISSION_DENIED` is returned for: the actions `launch`, `run` and `close`; Alt+F4 without `window`; `type`
into targets named like password / token / secret; a step in a security / license / sign-in / recovery /
delete / overwrite / replace window; dialog rules whose title or text contains one of those words (some
localized equivalents included). An unknown `action` is a validation error. Warning: Ctrl+S / Ctrl+O keys.

### Script limits compared with the interactive tools

- `screenshot` covers a window or element only, never the whole screen, and never restores a minimized window.
- Region capture is not supported; use `in=` in the selector.
- Every window must belong to `pid`; output files must stay in the gateway runs/captures folders.
- The gateway never moves the mouse just to unlock `SetForegroundWindow`: if the window cannot get the
  foreground, real input is refused (`TARGET_NOT_READY`); pattern actions still work.
- There is no `close`: close a window through its own button or Esc.
- Refs are per MCP session and window; a script uses its own session.

## 8. Errors

| Code | Typical cause → what to do |
|---|---|
| `NOT_FOUND` | No window/element matches, stale ref, process gone → `ui_list_windows`, new `ui_snapshot`. |
| `TARGET_NOT_READY` | Disabled element, no clickable point, UI not responding, focus could not be obtained → observe, wait, scroll into view, retry. |
| `VALIDATION_FAILED` | Bad selector / key string / argument combination. |
| `PERMISSION_DENIED` | Password field, Alt+F4 on the main window (or when another window than the named one is in front), save path outside the runs/captures folders, forbidden script content, elevated target (UIPI: run Revit normally or the Host elevated). |
| `DEPENDENCY_FAILED` | Windows session locked / no interactive desktop (LogonUI in this session or `OpenInputDesktop` fails) → unlock; RDP: keep the session connected. |
| `BUSY` | Another UI action is running. |
| `TIMEOUT` | `ui_wait` condition never became true (details carry the last state). |

## 9. Safety rules built in

- Real keyboard input is sent only while a window of the target pid is in the foreground — checked before
  every keystroke — and, for `ui_type keyboard=true` / `ui_keys` with a target, only after keyboard focus is
  verified to be on that element. Mouse input only when the real cursor position is over a window of the target
  pid (checked right before every button/wheel event; a drag path is checked up front and never drops outside).
  Focus theft or a moved cursor aborts the rest of the sequence.
- After a timeout the abandoned work is cancelled before it can send more input, and the input lock is held
  until it has stopped (at most 15 s).
- Pattern actions need no focus and are preferred everywhere.
- Password fields are never typed into and their values are never returned.
- Tests (`tests/RevitAgent.Tests/Ui`) only target the test app they start, kill it at the end, and skip
  (not fail) when the desktop is locked, non-interactive, or `REVITAGENT_SKIP_UI_TESTS=1`.

## 10. Limits

- Applications running elevated cannot be automated from a non-elevated gateway (UIPI).
- Owner-drawn / DirectX surfaces (Revit canvas) have no tree: screenshot + coordinates only.
- Virtualized lists/grids expose only realized items: scroll, then read again.
- UIA calls are cross-process; a full Revit main-window snapshot can take seconds — snapshot subtrees.
