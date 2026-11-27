# RevitAgent: guide for AI agents

RevitAgent is a standalone MCP gateway that lets any AI agent drive Autodesk Revit (2022-2026) and the add-ins running inside it **without knowing their source code**: observe (UI Automation, screenshots, Revit API reads through the "Bridge" add-in), then act, then verify.

Components: `RevitAgent.Core` (the tools), `RevitAgent.Host` (WPF dashboard; runs the MCP server on a named pipe), `RevitAgent.Shim` (stdio relay that the AI client launches), `RevitAgent.Bridge` (the add-in loaded into Revit).

Related documents:

- [tool-catalog.md](tool-catalog.md): every tool with permission and parameters (which tools are in the `core` profile).
- [macros.md](macros.md): verified tool chains (`macro_list` / `macro_run`) for common tasks.
- [recipes.md](recipes.md): step-by-step recipes with tool calls.
- [revit-process-and-dialogs.md](revit-process-and-dialogs.md): finding, starting and closing Revit; readiness; dialogs; sandbox.
- [ui-automation.md](ui-automation.md): UI tools, selectors, UI scripts.
- [scenarios.md](scenarios.md): `scenario.yaml`, `scenario_run`, checks, recorder.
- [qa-scenario-authoring.md](qa-scenario-authoring.md): writing scenarios with a QA engineer from a user story.
- [run-data.md](run-data.md): layout of run data, `run.json`, `index.jsonl`.
- [run-and-schedule.md](run-and-schedule.md): running scenarios from the dashboard and nightly schedules.
- Runnable examples: `examples/scenarios/` (levels-inventory, room-schedule-ui).

## 1. Connect an AI client

Every client launches `RevitAgent.Shim.exe` over stdio. The Shim connects to the Host (dashboard) on the pipe `\\.\pipe\revitagent-mcp`; if the Host is not running, the Shim starts it (minimized). If it still cannot connect, the Shim runs the gateway in its own process (headless).

Shim flags: `--headless` (always run the gateway in-process, no dashboard), `--pipe <name>`, `--no-autostart`, `--host-exe <path>` (or env `REVITAGENT_HOST_EXE`), `--profile core|full` (or env `REVITAGENT_PROFILE`; the flag wins), `--check-machine [--year N]` (section 6).

Development build path: `src\RevitAgent.Shim\bin\Debug\net10.0-windows\RevitAgent.Shim.exe`. In the examples below, `<Shim.exe>` stands for the full path of that file.

| Profile | Tools the model sees | Approx. tokens for tools + instructions |
|---|---|---|
| `core` (default) | 18: `gateway_get_info`, `revit_list_instances`, `revit_launch`, `revit_close`, `revit_list_dialogs`, `revit_handle_dialog`, `sandbox_copy_model`, `doc_open`, `model_query_elements`, `model_get_warnings`, `view_activate`, `ui_snapshot`, `ui_click`, `ui_type`, `ui_screenshot`, `human_ask`, `macro_list`, `macro_run` | ~5k |
| `full` | all 67 | ~22k |

A profile changes the tool list sent to the model, and a direct call to a tool outside it is refused with `NOT_FOUND` (`details: {profile, tool}`), the same for every client; macros and scenarios still run those tools internally. If the model must call a non-core tool directly (for example `schedule_read`, `ribbon_list_items`), use `full`. For a task that already has a macro (`create-schedule`, `run-addin-command`, `run-revit-command`, `read-schedule`; see [macros.md](macros.md)), call `macro_list` then `macro_run` ([macros.md](macros.md)): one call replaces about 15 UI steps. `gateway_get_info` reports `profile`, `tools_listed`, `tools_total`, a `hint` when tools are hidden, and the `macros` available.

| Client | Status on the maintainer's machine |
|---|---|
| Claude Code | Verified 2026-11-26 (Opus 5.5: created a Level schedule end to end in about 4 minutes) |
| agy (Antigravity CLI 1.5.17) | Verified 2026-11-26 (Gemini 3.8 Flash: same task in about 8.5 minutes) |
| Claude Desktop, Cursor | Not verified with `--profile` |
| Codex CLI, other clients | Not verified on this machine; configuration taken from official documentation |

In every config below, replace `<repo>` with the real path of your clone (backslashes doubled inside JSON strings).

### Claude Code

```
claude mcp add revitagent -- <Shim.exe> --profile core
claude mcp add revitagent -- <Shim.exe> --headless --profile core
claude --mcp-config revitagent.json
```

The second line runs without the dashboard. The third loads servers from a JSON file in `mcpServers` form (as in the Claude Desktop section) for this session only. Claude Code's default tool timeout is very long (`MCP_TOOL_TIMEOUT`, about 28 hours), so there is no need to raise it.

### Claude Desktop

Edit `claude_desktop_config.json` (escape backslashes as `\\`):

```json
{
  "mcpServers": {
    "revitagent": {
      "command": "<repo>\\src\\RevitAgent.Shim\\bin\\Debug\\net10.0-windows\\RevitAgent.Shim.exe",
      "args": ["--profile", "core"]
    }
  }
}
```

### Cursor

Create `.cursor/mcp.json` (in the project) or `~/.cursor/mcp.json`, same shape:

```json
{
  "mcpServers": {
    "revitagent": {
      "command": "<repo>\\src\\RevitAgent.Shim\\bin\\Debug\\net10.0-windows\\RevitAgent.Shim.exe",
      "args": ["--headless", "--profile", "core"]
    }
  }
}
```

### agy (Antigravity CLI)

```
agy mcp add revitagent -- <Shim.exe> --profile core
```

- Syntax: `agy mcp add [flags] <name> <command or URL> [args...]`. agy's own flags go before the name; use `--` before the command when its arguments start with `-`.
- The configuration is written to `%USERPROFILE%\.gemini\config\mcp_config.json` as `{"mcpServers":{"revitagent":{"command":"...","args":[...]}}}`.
- agy stores the server's instructions in `%USERPROFILE%\.gemini\antigravity-cli\mcp\revitagent\instructions.md` and reads that file.
- Auto-approval: `%USERPROFILE%\.gemini\antigravity-cli\settings.json`, array `permissions.allow`, each entry `mcp(<server>/<tool>)` (or a regex). Auto-approve Read tools only, for example:

  ```
  "mcp(revitagent/gateway_get_info)",
  "mcp(revitagent/revit_list_instances)",
  "mcp(revitagent/revit_list_dialogs)",
  "mcp(revitagent/model_query_elements)",
  "mcp(revitagent/ui_snapshot)",
  "mcp(revitagent/ui_screenshot)"
  ```

  Do not use `mcp(.*)`: it also auto-approves Admin tools (`revit_launch`, `revit_close`). Whatever the client approves, the gateway still applies its own policy (section 4).

### Codex CLI (not verified on this machine)

Per OpenAI's Codex documentation:

```
codex mcp add revitagent -- <Shim.exe> --profile core
```

This writes to `%USERPROFILE%\.codex\config.toml`. Codex defaults to 10 s for server startup and 60 s per tool, too short for Revit (`revit_launch` 42-70 s, `doc_open` 31-36 s, a macro can take minutes). Adjust:

```toml
[mcp_servers.revitagent]
command = '<repo>\src\RevitAgent.Shim\bin\Debug\net10.0-windows\RevitAgent.Shim.exe'
args = ["--profile", "core"]
startup_timeout_sec = 60   # the Shim may have to start the Host
tool_timeout_sec = 900     # at least 600
```

TOML single-quoted strings keep `\` literally. `env = { REVITAGENT_PROFILE = "core" }` can replace `args`.

### Other clients and open models

Any MCP client that can run a stdio server works, provided:

| Requirement | Minimum |
|---|---|
| Model | Supports tool / function calling |
| Client tool timeout | At least 10 minutes (600 s) |
| Context | At least ~16k tokens with `core` |

Generic form (most clients read this):

```json
{
  "mcpServers": {
    "revitagent": {
      "command": "<repo>\\src\\RevitAgent.Shim\\bin\\Debug\\net10.0-windows\\RevitAgent.Shim.exe",
      "args": ["--profile", "core"]
    }
  }
}
```

Checked against official documentation, not run on this machine:

| Client | Where to configure | Tool timeout |
|---|---|---|
| LM Studio 0.3.17 or later | `mcp.json` (Program tab, Install, Edit mcp.json), `mcpServers` form as in Cursor | Not documented |
| Cline | `cline_mcp_settings.json` (MCP Servers, Configure), `mcpServers` form; `autoApprove` should hold Read tools only | Exists in MCP settings; key and unit not documented |
| opencode | `opencode.jsonc`: `"mcp": {"servers": {"revitagent": {"type": "local", "command": ["<Shim.exe>", "--profile", "core"]}}}` | `timeout.execution` (ms), default 12 hours |

### Choosing a profile by model class

| Model class | Profile | Prefer macros? | Output limits to set |
|---|---|---|---|
| Frontier, large context | `core`; `full` when non-core tools are needed | Yes, when a macro fits | `limit` + `fields` for `model_query_elements`; `ui_snapshot` by `selector`/`ref` |
| Small/fast (Flash, Haiku...) | `core` | Always: `macro_list` first, do UI steps only when no macro fits | As above, plus `max_nodes` for `ui_snapshot` and a small `max_width` (for example 1000) for `ui_screenshot` |
| Local/open, context <= 32k | `core` (the `full` tool list alone is ~22k) | Macros only; avoid long UI chains | Small `limit` (for example 20); never snapshot the Revit main window; for a model that cannot read images, `ui_screenshot` with `include_image: false` |

Keeping context small:

- Tokens go to three places: the tool list, the instructions, and tool output. The size of the Bridge add-in code costs nothing.
- Do not `ui_snapshot` the whole Revit main window (large output, ~30 s). Snapshot an add-in window or dialog, or pass a `selector`/`ref` for the part you need; leave `interactive_only` at its default `true`.
- Always set `limit` on `model_query_elements` (default 100) and list only the needed `fields`; use a small `limit` on `schedule_read` when you only check a few rows.
- Lower `max_width` for `ui_screenshot` (default 1600 px); capture one control (`ref`/`selector`) instead of the whole window.
- If a macro exists for the task, `macro_run` instead of assembling UI steps.

## 2. The first two calls

1. `gateway_get_info`: gateway version, sandbox and runs folders, current policy, the session's profile, available macros, and the tools by area with permissions.
2. `revit_list_instances`: the running Revit processes. **Take the `pid` here and pass it to every other tool.**

Call `revit_launch` only when the user asks for it. If the Bridge add-in is not in Revit, check with `bridge_status` / `bridge_list_installs` (`bridge_install` / `bridge_uninstall` change the shared Revit add-ins folder, so the machine owner must approve each call: on the RevitAgent dashboard, or on Telegram when configured, within 5 minutes. Without an approval channel the call fails at once with `PERMISSION_DENIED` "Owner approval required"; there is no argument or setting that skips it, so do not retry in a loop: ask the owner to approve, or to press the dashboard's Install Bridge button). The Bridge tools (`doc_*`, `model_*`, `schedule_*`, `ribbon_*`, `command_*`...) work only when the Bridge is loaded; a Revit started before the install needs a restart.

## 3. Observe, act, verify

Never assume an action succeeded; always check the result.

| Phase | What to do | Tools |
|---|---|---|
| Pick the target | Find Revit, check readiness | `revit_list_instances`, `revit_get_state`, `revit_wait_ready` |
| Observe (UI) | UI tree with `ref`s, images, find/inspect controls | `ui_list_windows`, `ui_snapshot`, `ui_find`, `ui_inspect`, `ui_screenshot`, `ui_read_table` |
| Observe (model) | Read Revit state | `doc_info`, `model_list_categories`, `model_query_elements`, `model_list_views`, `ribbon_list_items`, `command_list_postable` |
| Act (UI) | Click, type, select, wait | `ui_click`, `ui_type`, `ui_select`, `ui_toggle`, `ui_expand`, `ui_keys`, `ui_scroll`, `ui_wait` |
| Act (commands) | Run ribbon / add-in commands | `command_post`, `view_activate`, `selection_set` |
| Verify | The UI can lie, the model cannot | `ui_expect`, `schedule_read`, `model_query_elements`, `model_get_element`, `doc_info`, `model_get_warnings`, `selection_get` |

Rules:

- After each action, observe again (`ui_snapshot` or `ui_screenshot`) before the next step.
- Prefer AutomationId selectors (`id=...`) over `ref`. A `ref` belongs to one snapshot; `id=` is stable across runs and survives being recorded into a scenario. Ribbon buttons have ids like `ID_...`; an add-in window is its own window (`ui_list_windows`).
- With the Bridge, verify in the model (`doc_info`, `model_query_elements`, `schedule_read`). Without it, verify through the UI (`ui_read_table`, screenshots).
- To repeat work without an AI, record the session as a scenario (`recorder_*`, see [scenarios.md](scenarios.md)).

## 4. Safety rules

- **Work only on a copy.** Open models with `sandbox_copy_model {source_path}` (it creates the run folder itself; call `sandbox_create_run` only to group several copies in one existing run through `run_id`), then `doc_open {pid, path: copy_path}`. Never save over the original.
- **A modal dialog blocks every call.** Inspect it with `revit_list_dialogs`; answer only informational dialogs (`revit_handle_dialog`). **NEVER answer** a dialog about security / unsigned add-ins, licensing, file recovery, or deleting / overwriting / replacing data: stop and ask the user. The gateway itself presses a harmless Later / Close / Cancel only in a Revit it launched, only while the dialog blocks one of its own operations, and logs it (`safe_dismiss`); see [revit-process-and-dialogs.md](revit-process-and-dialogs.md). The unsigned add-in prompt of a gateway-launched Revit is answered only from the owner's allowlist; an agent never answers it.
- **Close or kill only a Revit you started** (`revit_launch`). Do not touch the user's Revit.
- **A Revit the user started is read-only to you, unless the user turned on "Let AI act on my Revit" on the RevitAgent dashboard** (time-limited, cleared when the Host exits). Tools of permission Interact or higher (ui_click/type/keys/select/toggle/expand/scroll, ui_run_script, command_post, view_activate, selection_set, doc_open/doc_close, revit_wait_ready, revit_handle_dialog, scenario_run with a pid...) aimed at that Revit return `PERMISSION_DENIED` without the grant. Do not retry or look for a workaround: ask the user to enable it, then call again. Read tools (revit_list_instances, ui_snapshot, ui_screenshot, doc_info, model_*...) always work. A Revit started by `revit_launch` does not need the grant.
- Never type passwords, tokens or secrets into any field.
- Close a copy with `doc_close`, which **does not save** (changes are discarded). Do not save unless the user explicitly asks, and only on the copy.
- Every call passes through the policy and is audited; a refused tool returns `PERMISSION_DENIED` or `SANDBOX_VIOLATION`.

## 5. Error codes

Errors come back as `{code, message}`.

| Code | Meaning | What to do |
|---|---|---|
| `TARGET_NOT_READY` | Revit is not ready (starting, loading) | Observe and fix the state: `revit_get_state`, `revit_wait_ready` |
| `MODAL_BLOCKED` | A modal dialog is blocking | `revit_list_dialogs`; handle it if informational, otherwise ask the user |
| `BUSY` | The gateway or Revit is busy with something else | Wait and retry |
| `TIMEOUT` | Time ran out; the work may still be running | Check state (`revit_get_state`, a screenshot) before retrying |
| `PERMISSION_DENIED` | Policy does not allow it | Ask the user; do not work around it |
| `SANDBOX_VIOLATION` | Path is outside the sandbox | Use `sandbox_copy_model` and work on the copy |
| `DEPENDENCY_FAILED` | A dependency is missing (for example the Bridge is not loaded) | Check `bridge_status`; or verify through the UI |

You may also see `VALIDATION_FAILED`, `NOT_FOUND` and `CONFLICT` (bad parameter, does not exist, already exists / wrong state), and `EXPECTATION_FAILED` from a macro.

## 6. Check the machine before an unattended run

Before an unattended overnight run, an agent or operator can check that the machine is fit to run stably. Three entry points:

1. **MCP tool** (for agents): `machine_check_unattended`, permission Read, parameter `{"year": 2024}` (default 2024). Returns JSON with `ready_for_unattended` and `items: [{id, title, status, detail, fix}]`.

2. **Command line**:
   ```bash
   RevitAgent.Shim.exe --check-machine [--year 2024]
   ```
   - Runs in-process without starting the Host dashboard and prints JSON to stdout.
   - Exit code: `0` ready, `1` not ready, `2` error.

3. **PowerShell script**:
   ```powershell
   .\scripts\check-unattended.ps1 [-Year 2024] [-Json]
   ```
   - Prints a summary table (status, title, fix); `-Json` prints raw JSON. Same exit codes as the Shim.

## 7. Where to go next

- A specific task: [recipes.md](recipes.md) or [macros.md](macros.md).
- Turn an exploratory session into a scenario that replays without an AI: [scenarios.md](scenarios.md), section "Recorder".
- Gateway home: `%LOCALAPPDATA%\RevitAgent` (override with `REVITAGENT_HOME`); runs are in `<home>\runs\`.
