# Revit processes, readiness and dialogs (agent guide)

How an AI agent finds, starts, checks, unblocks and closes Revit through the RevitAgent gateway, and how it keeps
every model it touches inside the sandbox. Tools: `revit_*` and `sandbox_*` (`src/RevitAgent.Core/Revit`,
`src/RevitAgent.Core/Sandbox`).

## 1. Tools

| Tool | Permission | What it does |
|---|---|---|
| `revit_list_installed` | Read | Installed Revit years, `Revit.exe` paths, add-in folders, `bridge_manifest_installed`. |
| `revit_list_instances` | Read | Every running `Revit.exe`: `pid`, year, main window, `responding`, `main_window_enabled`, `modal_dialog_count`, `state`, `launched_by_gateway`, `bridge_pipe_available`, `active_document_guess`. Sends **nothing** to the processes. |
| `revit_get_state` | Read | Readiness of one pid + `blockers[]` with texts, buttons and `category`. `include_screenshot:true` attaches PNGs. |
| `revit_list_dialogs` | Read | Current blockers with full details (hwnd, texts, buttons `{text, id, enabled}`). |
| `revit_wait_ready` | Interact | Waits until ready; answers dialogs matched by an informational rule, safe-dismisses harmless ones of a gateway-launched Revit (4c); stops at forbidden/unknown dialogs. |
| `revit_handle_dialog` | Interact | Clicks exactly one button of one dialog and verifies it closed. Refuses forbidden dialogs. |
| `addin_allowlist_get` | Read | Lists the add-in allowlist (see section 4a). There is no tool to change it. |
| `revit_launch` | Admin | Starts a new gateway-owned Revit **without a model** and runs the start gate. |
| `revit_watchdog_status` | Read | Watchdog state: watched gateway-launched processes, hang / crash events. |
| `revit_close` | Admin | Closes a gateway-owned Revit (WM_CLOSE → grace → kill). |
| `sandbox_create_run` | Write | New run folder `runs\<yyyyMMdd-HHmmss>-<slug>\` with `data\`, `evidence\`, `run-info.json`. Optional: `sandbox_copy_model` creates its own run folder. |
| `sandbox_copy_model` | Write | Verified copy of a model (+ extra files) into a run; SHA-256 before/after proves the original is untouched. |
| `sandbox_list_runs` | Read | Runs, newest first. |

`pid` is optional for the Read tools when exactly one Revit runs; it is required for `revit_wait_ready`,
`revit_handle_dialog` and `revit_close`. `revit_get_state`, `revit_list_dialogs`, `revit_wait_ready` and
`revit_handle_dialog` only accept a `Revit.exe` pid or a pid the gateway launched (`PERMISSION_DENIED` otherwise), so a
wrong pid can never make the gateway read or click another application's windows.

**Acting on the user's own Revit (action grant).** A Revit the gateway did not launch
(`launched_by_gateway: false`) may always be *read*, but any tool with permission Interact or higher that targets it
(by `pid`, or by `window` / `hwnd` that belongs to it: `ui_click`, `ui_type`, `ui_select`, `ui_toggle`, `ui_expand`,
`ui_keys`, `ui_scroll`, `ui_run_script`, `command_post`, `view_activate`, `selection_set`, `doc_open`, `doc_close`,
`revit_wait_ready`, `revit_handle_dialog` and `scenario_run {pid}`) returns
`PERMISSION_DENIED` until the user enables **"Let AI act on my Revit"** on the RevitAgent dashboard (one Revit or
all of them; 15 min / 1 h / 4 h / until turned off; kept in Host memory only, revocable with "Revoke"). When you get it,
stop and ask the user to enable it, then retry; read tools keep working meanwhile. Other applications (non-Revit) and
Revits started by `revit_launch` are not affected. Calls allowed by a grant are audited with `grant_used: true`.

## 2. Standard recipe

```text
revit_list_instances                         → pick the pid (or, only if the user asked: revit_launch {year})
revit_wait_ready {pid}                       → ready, or MODAL_BLOCKED / TARGET_NOT_READY with details
sandbox_copy_model {source_path}             → copy_path inside runs\<run_id>\ (original_unchanged: true)
doc_open {pid, path: copy_path}              → (Bridge tools, package B)
... observe → act → verify (revit_wait_ready between long steps) ...
doc_close {pid, path: copy_path}                       → closes the copy, never saves
revit_close {pid, discard_changes: true}              → only for a Revit the gateway launched
```

Never open or save an original model. Never close a Revit you did not launch (`launched_by_gateway: false`): it may hold
the user's unsaved work.

## 3. Readiness gate

Ready = process alive, main window present (visible, unowned, with a caption — a splash screen never counts), enabled,
responding, **no blocker**, and the state stayed clear for `settle_sec` (default 5 s).

Detection is **Win32 only** (EnumWindows, class names, IsWindowEnabled, IsHungAppWindow): it never hangs on a busy Revit.
UI Automation is used only to read details (TaskDialog texts and button ids, WPF modal windows), always with a 2.5 s limit.

| Blocker `kind` | Meaning |
|---|---|
| `dialog` | Visible `#32770` window: MessageBox, Revit TaskDialog, classic dialog. |
| `modal_window` | Main window disabled by another enabled window (WPF/WinForms modal of an add-in). |
| `disabled` | Main window disabled but no modal window identified — use `ui_list_windows` / `ui_screenshot`. |
| `hung` | Main window *Not Responding* (long operation or hung). |

`state`: `ready` · `busy` (alive, no main window yet) · `modal` (a dialog / modal window / disabled main window blocks
input) · `not_responding` · `exited`; readiness waits can also end in `timeout`. `revit_get_state` returns
`{pid, state, ready, blocked, blocking_dialogs[{hwnd, title, class, text, category, buttons}], blockers[], responding,
main_window_enabled, bridge_pipe_available, launched_by_gateway}` — `blockers` also lists a disabled or hung main window.

`revit_wait_ready` / `revit_launch` loop (poll 0.7 s):

0. **`Security - Unsigned Add-In`** in a Revit the gateway launched, with a decision from the allowlist → the gateway presses `Load Once` / `Do Not Load` itself, records it in `handled[]` and keeps waiting (section 4a). Anything else about this dialog → step 1.
1. **Forbidden** dialog → stop at once: `MODAL_BLOCKED`, screenshot saved (`captures\dialogs\` or the run's `evidence\`
   when `run_id` is passed), nothing clicked. **Ask the user.**
2. Dialog matching an **informational rule** → click exactly the rule's button, record it in `handled[]` (max 2 tries per dialog).
3. Unknown dialog / disabled main window still there after `max(10 s, 2 × settle_sec)` → in a gateway-launched Revit a harmless
   dialog first gets its safe-dismiss button pressed and the wait continues (section 4c); otherwise `MODAL_BLOCKED` with screenshot;
   Not Responding after `max(30 s, 6 × settle_sec)` → `TARGET_NOT_READY`.
4. Timeout → `TARGET_NOT_READY` with the last blockers.

`revit_launch` returns instead of failing (so you always get the `pid`): `{pid, year, ready, state, code, reason,
next_step, handled[], blockers[], screenshots[]}`. A blocked launch leaves Revit running — the gateway never kills it
automatically. With the Bridge manifest installed for that year it also waits for the Bridge pipe (`wait_for_bridge`).

## 4. Dialog categories and what you may do

| `category` | Meaning | You may |
|---|---|---|
| `info` | Matches an informational rule (`matched_rule`). | Let `revit_wait_ready` answer it, or `revit_handle_dialog`. |
| `unknown` | No rule. | Read title/text/buttons (and the screenshot). Answer with `revit_handle_dialog` only if it is clearly informational; otherwise ask the user. |
| `forbidden` | Security / unsigned add-in / signature / publisher, license / licensing / entitlement, sign in / log in, recover / recovery, delete, overwrite, replace (English and Vietnamese terms, with or without diacritics), matched on title, texts **and** the labels of **enabled** buttons (a disabled button cannot be pressed, so a disabled `Delete Checked...` in Review Warnings does not make the dialog forbidden); and every *save changes?* prompt. | **Nothing.** `revit_handle_dialog` returns `PERMISSION_DENIED`: a human must answer. Show the screenshot to the user. |

The only exception: a pure *save changes?* prompt (no other forbidden term) of a Revit **the gateway launched** may be
answered with its **don't-save** button (`No`, `Don't save`, `Discard` or the localized equivalent, id 7) when you pass
`discard_changes: true` — to `revit_handle_dialog` or `revit_close`. Do this only for sandbox copies or when the user
agreed to lose the changes. In any other Revit the user answers save prompts. Saving (`Yes`) is never clicked.

### 4a. Unsigned add-ins: the allowlist exception

Every Revit start can show the TaskDialog **`Security - Unsigned Add-In`** (text lines `Name: ...` and `Location: ...`; buttons
`CommandButton_1001` Always Load, `CommandButton_1002` Load Once, `CommandButton_1003` Do Not Load). It stays `forbidden`
for you — `revit_handle_dialog`, `ui_click` and `ui_keys` refuse it, even for a listed add-in. The **gateway itself** answers it
(inside `revit_launch`, `revit_wait_ready` and therefore scenario runs) only when **all** of these hold:

- the pid was launched by the gateway (`launched_by_gateway: true`); a Revit the user started is never touched;
- the dialog was read completely, `Name:` and `Location:` were parsed unambiguously, `Location` is a full path, and no other
  forbidden term (license, delete, ...) appears in the dialog;
- `%REVITAGENT_HOME%\config\addin-allowlist.json` is readable (a corrupt file = nothing is pressed).

Decision: an **enabled** entry whose `name` equals the dialog's name (case-insensitive) and whose `path_pattern` matches the full
path (case-insensitive glob: `*` = any characters except `\`, `**` = any characters, `?` = one character) → **Load Once**;
otherwise → **Do Not Load**. **Always Load** is never pressed. Each press appears in `handled[]` as
`{by: "allowlist: Load Once", button_id, addin_trust: {name, location, matched_entry_id, decision}}`, in the audit log as tool
`addin_trust`, with a screenshot under `captures\dialogs\` (or the run's `evidence\`), and as a dashboard status event.

The list is edited **only by the owner** on the dashboard page "Allowed add-ins". You can read it with `addin_allowlist_get`;
if your add-in is missing, the gateway will press `Do Not Load` and the add-in will not be loaded — ask the user to add it
(with a pattern such as `D:\Dev\MyAddin\*\bin\MyAddin.dll` when the folder changes per build). An agent never answers this prompt.
The RevitAgent Bridge itself is pre-trusted by `bridge_install`, so it never raises it. `bridge_install` / `bridge_uninstall` need the machine owner's
approval (dashboard card, or Telegram when configured; no answer in 5 minutes = denied). An agent cannot approve it; the owner can also use the
dashboard's Install Bridge button directly.

Rules are applied only to dialogs that were read completely (`Complete`): when a text could not be read in time or UI
Automation did not answer, the dialog stays `unknown` and nothing is clicked automatically. Password edit fields are
never read.

### 4b. Telegram escalation (unattended runs)

For the OTHER forbidden dialogs (license, sign-in, recovery, delete / overwrite / replace, save prompts, ...) the gateway can ask the
owner on Telegram instead of just stopping. It does so only when **all** hold: the pid was launched by the gateway; the caller opted
in (`escalate_dialogs: true` on `revit_launch` / `revit_wait_ready`, default **false**; the scenario runner always opts in for its own
Revit); the dialog was read completely. An interactive AI agent should not use it: it asks the human itself with `human_ask`.

Flow: the gateway sends a screenshot and the dialog's enabled buttons (max 6) as inline buttons; only the configured chat may answer.

- The owner taps a button → the gateway re-checks that the same window (same title) is still there, then presses exactly that button
  (`handled[].human = {via: "telegram", choice, answered_by, timed_out, asked_at, answered_at}`, audit tool `human_dialog_decision`,
  dashboard status event `human_dialog`).
- The owner clicks in Revit himself → the dialog disappears, the Telegram question is withdrawn (a note says it was handled directly in Revit) and the wait continues.
- No answer before min(the Telegram ask timeout, the rest of the wait) → the **safe button** is pressed (`timed_out: true`).
  Telegram off or unreachable (the owner never saw the question) → nothing is pressed.

Safe button (`SafeButtonPolicy`, exact label, English and Vietnamese): Do Not Load > Cancel > No > Don't Save / Discard (only for a
pure save prompt of a gateway-launched Revit) > Close. Never Always Load, OK / Yes, Recover / Restore, Sign in or license buttons. No such
button (or two with the same label) → nothing is pressed and the call returns `MODAL_BLOCKED` as before. `revit_handle_dialog`, `ui_click`
and `ui_keys` still refuse forbidden dialogs: an AI cannot answer them, only the owner (Telegram or in Revit).

### 4c. Safe dismiss by the gateway (its own operations only)

When a dialog blocks a gateway operation in progress — `revit_close`, the dialog watch of `doc_open`, or the readiness wait of
`revit_launch` / `revit_wait_ready` (after the grace of step 3) — the gateway presses one "safe dismiss" button itself, only when
**all** of these hold (`SafeDismissPolicy`):

- the Revit was launched by this gateway (`launched_by_gateway: true`); a Revit the user started is never touched;
- the dialog was read completely, shows no progress bar, and its title, texts and **enabled** buttons carry no forbidden meaning
  (section 4, save prompts included) and no destructive one (discard, remove, erase, purge, uninstall, lose / lost, and their Vietnamese equivalents); a disabled destructive button does not count;
- an enabled button has one of these labels (exact, first line; English and Vietnamese), in this order of preference: `Later` / `Remind me later` / `Not now`,
  then `Close`, then `Cancel` (two hits in the same tier → nothing is pressed).
  `revit_close` never presses `Cancel` (on a closing question it would cancel the close).

OK, Yes, No, Apply, Update or any other button is never pressed. Each press is listed in `handled[]` /
`handled_dialogs` (also in the error details when `doc_open` fails afterwards) as `{by: "safe_dismiss: <operation>",
safe_dismiss: {operation, text, screenshot_path}}`, written to the audit log as tool `safe_dismiss`, saved as a screenshot
under `captures\dialogs\` (or the run's `evidence\`), shown as a dashboard status event, and sent to the owner's Telegram
chat as a notification (no question) when Telegram is configured. Forbidden dialogs keep their handling (refusal, section 4b).

## 5. Clicking precisely (`revit_handle_dialog`)

Arguments: `pid`, `hwnd` (from `revit_list_dialogs`), `button` = label (case-insensitive, `&` mnemonics ignored), Win32
control id (`"6"`), TaskDialog id (`"CommandButton_8"`, `"CommandLink_1002"`), or a selector `id=...` / `name=...`.
Exactly one button must match (ambiguous → `VALIDATION_FAILED`, missing → `NOT_FOUND` with the button list; disabled → `CONFLICT`).

| Dialog | How the gateway clicks |
|---|---|
| Classic `#32770` (MessageBox) | `WM_COMMAND(BN_CLICKED, id)` with that button's real id and window, posted to its parent (`method: wm_command`). |
| TaskDialog (`#32770` + `DirectUIHWND`, Revit's TaskDialog) | `TDM_CLICK_BUTTON` with the id read from the UIA AutomationId `CommandButton_<n>` / `CommandLink_<n>` (`tdm_click_button`). Without UIA: `BM_CLICK` on that exact button window (`bm_click`). |
| WPF / other modal window | UI Automation Invoke on the matched button, 2.5 s limit (`uia_invoke`). |

Then it waits up to `verify_timeout_sec` for the dialog to disappear: `handled.dialog_closed` tells you whether it did,
`remaining_blockers` what is still open. Only one button is ever clicked per call.

## 6. Dialog rules

Built-in rules live in `src/RevitAgent.Core/Revit/Rules/builtin-dialog-rules.json` (embedded in the gateway):
*Copied Central Model* → Close, *Cannot Find Central Model* → Close, *Desktop Connector isn't installed* → OK,
*Unresolved References* → the command link that ignores them and continues. Add your own in
`%REVITAGENT_HOME%\config\dialog-rules.json` (re-read when it changes; default home `%LOCALAPPDATA%\RevitAgent`). The user file
uses the same schema as the built-in one:

```json
{
  "schema": "revitagent.dialog-rules/1",
  "rules": [
    {
      "id": "missing-fonts",
      "description": "Missing fonts notice; acknowledge with OK.",
      "process": "Revit",
      "titleContains": "Missing Fonts",
      "bodyPattern": "font",
      "press": "name=OK"
    },
    { "id": "central-model-copied", "process": "Revit", "titleContains": "Copied Central Model", "press": "id=CommandButton_8" }
  ]
}
```

`id` names the rule; `process` (optional) is the process name; `titleContains` is a case-insensitive substring of the title;
`bodyPattern` (optional) is a regex over the dialog texts; `press` is `id=<control id | CommandButton_n | CommandLink_n | AutomationId>`
or `name=<label>`. A bare JSON array of rules is also accepted. Rejected at load (reported in `rule_errors`): rules whose title is
in a forbidden category or is a save prompt, missing `titleContains` / `press`, an invalid regex (the gateway only clicks one
button precisely; there are no key-sequence rules). Forbidden dialogs are also skipped at run time, so no rule can ever answer them.

## 7. Process ownership and closing

`revit_launch` records `{pid, start_time_utc, exe_path, year}` in `%REVITAGENT_HOME%\launched.json`. Ownership survives
gateway restarts and is immune to pid reuse (an entry counts only while a live process with that pid has the recorded
start time). `tie_to_gateway: true` puts Revit in a kill-on-close Job Object (Revit dies with the gateway); default false.
Launch arguments that name an existing file or folder, or a model / template / family / journal (`.rvt .rfa .rte .rft .rvtx .rws .adsk .ifc .dwg .txt .jrn`), are refused.

`revit_close {pid}`:

1. Not launched by the gateway → `PERMISSION_DENIED`, unless the user explicitly asked: `force_foreign: true` **and**
   `confirm_pid` = pid (and it must be a `Revit.exe`).
2. A dialog already open → a save prompt with `discard_changes: true` (gateway-launched Revit only) is answered, a harmless
   dialog of a gateway-launched Revit gets `Close` / `Later` / `Not now` (section 4c); anything else → `MODAL_BLOCKED`.
3. `WM_CLOSE` to the main window as soon as there is one; during `grace_sec` (default 60): save prompt → don't-save only
   with `discard_changes`, otherwise `MODAL_BLOCKED` and Revit stays open; a harmless dialog → its `Close` / `Later` / `Not now`
   (section 4c, never `Cancel`); any other dialog → `MODAL_BLOCKED`.
4. Still running after `grace_sec` (no prompt appeared, or no main window ever appeared) → killed
   (`method: killed_after_grace`). `force_kill: true` kills at once. Nothing is ever saved.

## 8. Sandbox

- `sandbox_create_run {name?, purpose?}` → `{run_id, path, data_dir, evidence_dir}`; `run_id` like `20261120-031500-door-check`. Optional: `sandbox_copy_model` without `run_id` creates its own run folder.
- `sandbox_copy_model {source_path, run_id?, extra_files?, name?}` → copies into the run (new run when `run_id` is omitted),
  name collisions get `-2`, `-3`; returns `{copy_path (open that), run_id, source_sha256_before, source_sha256_after,
  copy_sha256, original_unchanged, copies[]}` (`copies[]` repeats the hashes per file, extra files included).
  The source is only read; the copy is made writable. `extra_files` accepts model-related types only (.rvt .rfa .rte
  .rft .txt .csv .tsv .xlsx .xls .json .xml .yaml .yml .ifc .dwg .dxf .dgn .sat .skp .nwc .nwd .pdf and images); the
  gateway's own data folder (config, audit, logs) is never copied.
  `CONFLICT` when the source changed during the copy (the copy is discarded).
- `run_id` must be a single folder name under the runs folder: traversal (`..`, separators, drive paths) → `VALIDATION_FAILED`;
  every write is checked to stay inside the runs folder and not to pass through a junction/symlink (`SANDBOX_VIOLATION`).
- Copies and their hashes are appended to `run-info.json` (evidence for "the original was not modified").

## 9. Errors

| Code | Typical cause | Next step |
|---|---|---|
| `MODAL_BLOCKED` | Forbidden dialog, unknown dialog past grace, save prompt without `discard_changes` | Read `details` (blockers, screenshot); answer informational dialogs with `revit_handle_dialog`; ask the user otherwise. |
| `TARGET_NOT_READY` | Timeout, Not Responding, no main window yet | `revit_get_state`, retry `revit_wait_ready` later. |
| `PERMISSION_DENIED` | Forbidden dialog, foreign Revit for `revit_close`, policy | Ask the user. |
| `NOT_FOUND` | pid gone, hwnd not a current blocker, button missing, model missing | Re-list (`revit_list_instances`, `revit_list_dialogs`). |
| `BUSY` | Another wait/close/dialog call is acting on the same pid | Retry when it finishes. (Bridge calls to one Revit are queued up to 15 s first; `details.busy_tool` names the call in the way.) |
| `VALIDATION_FAILED` | Bad argument (hwnd, run_id, launch args, ambiguous button) | Fix the argument. |

## 10. Limits

- Detection sees top-level windows only; a modal drawn inside the main window (no own window) is invisible except as
  `disabled`.
- UIA cannot read processes running with higher integrity than the gateway (UIPI); the gate then falls back to title only.
- `active_document_guess` is parsed from the window title; confirm with `doc_info` (Bridge).
- A Revit TaskDialog whose UIA read times out cannot be answered by TaskDialog id; it can still be answered by label
  (exact button window) or by the user.
