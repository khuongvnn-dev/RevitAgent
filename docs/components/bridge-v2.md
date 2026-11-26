# RevitAgent Bridge v2 (in-Revit add-in)

The Bridge is the part of RevitAgent that runs **inside** Revit. It gives the gateway read access to
the Revit API (documents, elements, parameters, views, schedules, warnings, the ribbon) plus a few
user-level actions (post a command, activate a view, set the selection, open/close sandbox copies).
It never changes Revit's own behaviour: no dialog overriding, no Sync-with-Central blocking, no
failure processing. `DialogBoxShowing` is only **recorded**.

## Components

| Path | What |
| --- | --- |
| `src/RevitAgent.Bridge/` | The add-in (`RevitAgent.Bridge.BridgeApplication`, `IExternalApplication`). |
| `src/RevitAgent.Contracts/Bridge/BridgeWire.cs`, `BridgePipeNames.cs` | Shared constants (AddInId, pipe names, tool names). Pure BCL. |
| `src/RevitAgent.Contracts/Bridge/Wire/` | Dependency-free JSON reader/writer, framing and the pipe server core (BUSY guard, timeouts). Compiled into Contracts (tests) **and linked as source** into the Bridge. |
| `src/RevitAgent.Core/Bridge/` | Host side: `BridgeClient`, `BridgeToolCatalog` (table of forwarded tools), `bridge_status`. |
| `src/RevitAgent.Core/Install/` | Installer + `bridge_list_installs`, `bridge_install`, `bridge_uninstall`. |
| `scripts/build-bridge.ps1` | Builds every Revit year and checks the output folders. |

## Multi-version build

One source tree, one build per Revit year:

```powershell
dotnet build src/RevitAgent.Bridge/RevitAgent.Bridge.csproj -c Release -p:RevitVersion=2022
powershell -File scripts\build-bridge.ps1                 # all years, Release
powershell -File scripts\build-bridge.ps1 -Years 2024 -Configuration Debug
```

| Year | TFM | Revit API package (Nice3point, reference-only) |
| --- | --- | --- |
| 2022 | net48 | 2022.1.80 |
| 2023 | net48 | 2023.1.90 |
| 2024 | net48 | 2024.3.60 |
| 2025 | net8.0-windows | 2025.0.2 (GA, see below) |
| 2026 | net8.0-windows | 2026.0.4 (GA) |

* Output: `src/RevitAgent.Bridge/bin/<Configuration>/R<yy>/RevitAgent.Bridge.dll` (+ `.pdb`) **and nothing
  else**; intermediate files live in `obj/R<yy>/`. `dotnet build RevitAgent.slnx` builds 2024 only.
* Symbols: `REVIT<year>` and `REVIT2023_OR_GREATER` … `REVIT2026_OR_GREATER`. API differences are
  isolated in `Tools/RevitCompat.cs` (`ElementId.Value` vs `IntegerValue`, `new ElementId(long)`) and
  `ParameterReader` (`GetGroupTypeId` 2024+).
* .NET Framework ignores versions of the (not strong-named) Revit assemblies; .NET 8 refuses a reference
  to a *higher* version than the loaded one, so 2025/2026 compile against the first release packages
  and therefore load on every update of that year.
* **Zero third-party DLLs**: the Bridge references only the BCL and the Revit API (compile time). It does
  not reference Contracts or System.Text.Json; other add-ins in the same Revit ship their own versions and
  conflicts break loading. The wire code is linked as source instead.

## Runtime

* `OnStartup` (fast, never throws, always returns `Succeeded`): creates one `ExternalEvent`, subscribes
  `DialogBoxShowing` (record only), and starts the pipe server
  `\\.\pipe\revitagent-bridge-<pid>`: explicit DACL, current user full control, network logons denied
  (`PipeSecurity` on net48, `NamedPipeServerStreamAcl.Create` on net8).
* Wire format: 4-byte little-endian length + UTF-8 JSON. Request `{id, tool, args, timeout_sec}` →
  response `{id, ok, result}` or `{id, ok:false, error:{code, message, details?}}`; codes are
  `GatewayErrorCodes`.
* Several connections and several requests per connection are accepted. **Immediate** tools (`ping`,
  `dialog_list_events`, `command_list_postable`) run on the pipe thread and answer even while Revit is
  busy. **Revit** tools run one at a time on the UI thread through the ExternalEvent; a second Revit call
  while one is in flight gets `BUSY` immediately (with `running_tool`, `running_sec`).
* Timeouts never block the UI thread. If Revit has not *started* the call when `timeout_sec` elapses
  (modal dialog, running command), the call is cancelled and never runs: `TIMEOUT` with
  `details.started=false`. If it started, `TIMEOUT` says it "may still be running" and further calls get
  `BUSY` until it ends.
* The SynchronizationContext is cleared while a tool runs; results are serialized on the UI thread.
* Log: `%LOCALAPPDATA%\RevitAgent\logs\bridge-<pid>.log` (or `REVITAGENT_HOME\logs`), 2 MB then rotated
  to `.1.log`, lines clipped to 2 KB (payloads are never logged in full), logs older than 14 days deleted.

## Tools

Every gateway tool below takes a required `pid` and forwards to the Bridge tool of the same name.
Permissions: Read unless noted.

| Tool | Notes |
| --- | --- |
| `doc_info` | title, path ("" when unsaved), workshared/modifiable/modified, active view, project info, units, Revit version/build. `opened_from`: for a copy doc_open detached from central (no path until saved), the file it was opened from. |
| `doc_list_open` | title, path, active, linked, family, workshared, detached, modified. |
| `doc_open` (Write) | `path` must be inside the sandbox (Host calls `GatewayPolicy.EnsureInSandbox` before forwarding). `detach_from_central` default true (workshared only), `audit` false, `activate` true, `worksets`. Returns `opened, title, path, is_active, saved_in_version, upgraded, elapsed_ms, dialogs_during_open`. |
| `doc_close` (Write) | `path` / `title` / `document`, default active. Never saves; `save:true` → VALIDATION_FAILED. Active/only document → CONFLICT with guidance (the API cannot close the active document). |
| `model_list_categories` | name, id, `builtin` (OST_*), type, schedulable. |
| `model_query_elements` | `category` (OST_* or display name) and/or `class`; `filters` `[{name, op, value}]` (`equals, not_equals, contains, gt, gte, lt, lte, exists`; numbers in display units); `fields` → `{value, display, storage, display_number, unit}`; `view_id`, `include_types`, `offset`, `limit` (100/1000); returns `total`. |
| `model_get_element` | by `id` or `unique_id`; all parameters (storage, value, display, read_only, builtin, group, shared_guid), type, level, workset, location, bounding box. |
| `model_list_views` | `type` (floor_plan, ceiling_plan, section, elevation, 3d, schedule, sheet, legend, drafting, …), `name_contains`, `include_templates`. |
| `model_get_warnings` | description, severity, failing/additional ids (read only, never resolved). |
| `schedule_list_schedulable_categories` | `ViewSchedule.GetValidCategoriesForSchedule`; `category` → `check.schedulable` (false: read the elements with `model_query_elements`; true: `schedule_create`). |
| `schedule_read` | `view_id` or `name`; fields, header (title) rows and body as text (`GetCellText`). The body is split by `ScheduleHeader.CountHeaderRows` (shared source in Contracts): `header_rows` + `header_cells` (grouped-header rows, the column headings matched to the visible fields' `ColumnHeading`, the blank row under them; none when `ShowHeaders` is off), then `data_rows` / `rows` (everything below, including grouping and total rows). `limit` and `truncated` refer to data rows; `header_note` appears when headings are shown but no leading row matched them. |
| `schedule_create` (Write) | `category` (must pass `ViewSchedule.IsValidCategoryForSchedule`), `fields` (matched case-insensitively to `SchedulableField.GetName`; unknown → VALIDATION_FAILED listing up to 30), `sort_by` (each also in `fields`) + `sort_descending`, `name` (schedule name in use → CONFLICT), `activate` (default true, active document only; `UIDocument.ActiveView` after the commit). One transaction `RevitAgent: schedule_create`, rolled back on any error. Returns `view_id, name, category, fields[{name, heading}], sort[{field, descending}], activated`. Sandbox: see below. |
| `ribbon_list_items` | all tabs incl. third-party; items with `command_id` + `postable`. |
| `command_list_postable` | `PostableCommand` names. |
| `command_post` (Interact) | PostableCommand name or command id (`ID_…`, `CustomCtrl_%…`); returns immediately, the command runs afterwards. |
| `view_activate` (Interact) | by `view_id` or `name` (+`type`). |
| `selection_get` / `selection_set` (Interact) | ids; `selection_set` is all-or-nothing. |
| `dialog_list_events` | DialogBoxShowing records (`seq, time, kind, dialog_id, message, during_tool`); poll with `after_seq`. |
| `bridge_status` | per Revit pid: pipe_present, reachable, ping (version, protocol, busy). No pid → every Revit. |

Adding a Bridge tool = one handler in `BridgeToolRegistry` (add-in) + one `BridgeToolDefinition` in
`BridgeToolCatalog` (name, permission, description, schema, timeout, optional Host-side `Prepare`).

A tool that changes the model sets `SandboxDocument: true` (today: `schedule_create`). The Bridge does not know the
sandbox root, so `BridgeForwardingTool` first calls `doc_info` with the agent's `document` key, takes `path` (or
`opened_from` for a detached copy), refuses with SANDBOX_VIOLATION unless `GatewayPolicy.IsInSandbox` accepts it, and
forwards `document` pinned to that full path; the Bridge resolves a path key exactly (never by title, because a sandbox
copy keeps the original's file name). Skipped when sandbox enforcement is off. Like every Write/Interact tool, it is also
subject to `ActionGuard` (a Revit the gateway did not launch needs the owner's dashboard grant).

## Installation

`bridge_install` (Admin; `years` default = every installed Revit, optional `source_dir`):

1. Picks the newest build per year (`source_dir`, `REVITAGENT_BRIDGE_PAYLOAD`, `<app>\bridge`, repo
   `src\RevitAgent.Bridge\bin\Release|Debug`).
2. Copies it to `%LOCALAPPDATA%\RevitAgent\bridge\R<yy>\<fileversion>-<sha256[0..10]>\`. A changed build
   always goes into a **new** folder, so a DLL locked by a running Revit is never overwritten; the
   manifest is repointed and unlocked old copies are cleaned (locked ones are reported).
3. Writes `%APPDATA%\Autodesk\Revit\Addins\<year>\RevitAgent.Bridge.addin`
   (AddInId `2B0C5AC5-295C-415B-BAAD-F731A61C66D8`, VendorId `RVAG`, absolute assembly path).
4. Pre-trusts **our** AddInId: `HKCU\SOFTWARE\Autodesk\Revit\Autodesk Revit <year>\CodeSigning`,
   value `2b0c5ac5-295c-415b-baad-f731a61c66d8` = REG_DWORD 1. This is exactly what Revit writes when a
   user clicks "Always Load" (the values there are the lowercase AddInIds of the add-ins the user chose to always load).
5. Removes the retired v1 manifest (AddInId `B62FE48B-…`) and the v1 trust values, including the braced
   `{b62fe48b-…}` name the old `setup-bridge.ps1` wrote: Revit cannot parse it and logs
   `DBG_WARN: Unrecognized Guid format` (AddInManagerUtils.cpp) at every start.

`bridge_uninstall` removes the manifest, the trust value(s) and the unlocked copies. `bridge_list_installs`
shows per year: installed, trusted, up_to_date vs the newest build, legacy leftovers, running pids and
whether their Bridge is loaded. A running Revit only loads the Bridge at its next start.

### Startup prompts of other add-ins (observed 2026-11-20)

Revit evaluates all-users add-ins (`%ProgramData%\Autodesk\Revit\Addins\<year>`) before per-user ones,
so a "Security - Unsigned Add-In" prompt raised for **another** add-in blocks Revit before the Bridge is
loaded; the Bridge pipe then does not exist (`bridge_status`: pipe_present=false). An unsigned all-users add-in
with a frequently rebuilt DLL can prompt at every Revit start even when its AddInId already has a CodeSigning
value of 1, so Revit does not always honour that value.

How the gateway treats that prompt:

* A Revit the gateway launched: the gateway answers it only from the owner's add-in allowlist (dashboard), with
  `Load Once` for an add-in on the list and `Do Not Load` otherwise, never `Always Load`. See
  [revit-process-and-dialogs.md](../agent-guide/revit-process-and-dialogs.md).
* A Revit the owner started: the gateway does not touch the prompt. It is the user's decision.

## Tests

`tests/RevitAgent.Tests/Bridge/`: JSON/framing round trips and System.Text.Json interop; the real
pipe-server core ↔ `BridgeClient` over real pipes (BUSY, started/not-started TIMEOUT, error mapping,
missing pipe → DEPENDENCY_FAILED); a hand-written fake Bridge pinning the wire format; gateway contract
(names, permissions, `pid` required), sandbox enforcement on `doc_open`, `doc_close` argument rules;
installer against temp folders and an in-memory trust store. Live checks are opt-in
(`Category=RequiresRevit`): `REVITAGENT_LIVE_REVIT_PID` (+ `REVITAGENT_LIVE_MODEL`, `_MODEL_B`, `_MODE`,
`_EVIDENCE_DIR`), `REVITAGENT_LIVE_INSTALL_YEARS`, `REVITAGENT_LIVE_WINDOWS_PID`.
