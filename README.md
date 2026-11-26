# RevitAgent

[Tiếng Việt](README.vi.md) (translation; this English README is authoritative)

**An MCP gateway that lets any AI agent drive Autodesk Revit (2022-2026) and its add-ins, with no access to their source code.**
Start the dashboard (or let your AI client start it). Any MCP-capable agent, such as Claude Code, Claude Desktop, Cursor or
Antigravity, can then observe Revit, act on it like a user, read the model to verify the result, and follow a scenario or improvise.

## Key features

- **Agent-agnostic:** plain MCP over stdio; the safety rules live in the gateway, not in the client.
- **Works with any Revit and any add-in:** a UI Automation layer needs nothing installed in Revit; an optional Bridge add-in adds API-level reads and the ribbon of every add-in.
- **Observe, act, verify:** UI snapshots and screenshots, dialog detection, model/schedule reads to check what the UI did.
- **Safe by default:** models are only opened from a sandbox copy, never saved over; dialogs that need a human are never answered by the AI.
- **Scenarios and macros:** a scenario is a folder with a `scenario.yaml` run by the gateway; macros are verified recipes exposed through two tools.
- **Record and replay:** record one AI-solved run, replay it without an AI.
- **Small tool surface for small models:** 67 tools; the default `core` profile lists 18.
- **Audited:** every tool call is logged; evidence of a run goes to the run store.

## How it works

```
AI client --stdio MCP--> RevitAgent.Shim --\\.\pipe\revitagent-mcp--> RevitAgent.Host (WPF dashboard + gateway)
                         (or --headless: the gateway runs inside the Shim)
                                                    |
        UI layer (any Revit, nothing to install)    |   API layer (Bridge add-in, pipe revitagent-bridge-<pid>)
        out-of-process UI Automation,               |   model and schedule reads, ribbon of ALL add-ins,
        screenshots, Win32 dialog detection         |   PostCommand, sandbox document open/close
```

| Project | Role |
|---|---|
| `src/RevitAgent.Shim` | stdio MCP entry point for the client; relays to the Host or runs the gateway headless. |
| `src/RevitAgent.Host` | WPF dashboard (Bridge install, running Revits, permissions, audit) and the gateway. |
| `src/RevitAgent.Core` | Gateway logic: tools, policy, UI automation, Revit process/dialog handling, sandbox, scenarios, macros. |
| `src/RevitAgent.Bridge` | In-Revit add-in, one build per Revit year (net48 for 2022-2024, net8 for 2025-2026). |
| `src/RevitAgent.Contracts` | Shared constants and wire code (netstandard2.0). |
| `tools/RevitAgent.E2E` | End-to-end harness against a real Revit; evidence goes to `%REVITAGENT_HOME%\runs`. |

## Quick start

Requires Windows, the .NET 10 SDK (plus the .NET 8 SDK to build the Bridge for Revit 2025/2026) and Revit 2022-2026.

```powershell
dotnet build RevitAgent.slnx -c Debug
dotnet test tests\RevitAgent.Tests\RevitAgent.Tests.csproj     # UI/dialog tests need an unlocked desktop
src\RevitAgent.Host\bin\Debug\net10.0-windows\RevitAgent.Host.exe   # dashboard: install the Bridge, see Revit, connect AI
```

1. **Install the Bridge (optional but recommended):** the *Install Bridge* button on the dashboard, or the `bridge_install` tool. Build every year with `scripts\build-bridge.ps1`. A running Revit must be restarted.
2. **Connect an AI client:** every client runs the Shim over stdio, for example

   ```powershell
   claude mcp add revitagent -- <repo>\src\RevitAgent.Shim\bin\Debug\net10.0-windows\RevitAgent.Shim.exe --profile core
   ```

   Setup for Claude Code, Claude Desktop, Cursor, Antigravity (agy), Codex CLI, other MCP clients and open models, and how to choose a profile per model: section 1 of [docs/agent-guide/README.md](docs/agent-guide/README.md).
3. **Tool profile:** `--profile core|full` (or env `REVITAGENT_PROFILE`; default `core`). `core` lists 18 tools, `full` lists all 67. A direct call to a tool outside the profile is refused (`NOT_FOUND`); macros and scenarios still use those tools internally.
4. **Acceptance on a real Revit:** open Revit 2024, then `powershell -ExecutionPolicy Bypass -File scripts\acceptance.ps1`.

## Tool groups

| Group | Tools | Use |
|---|---|---|
| `gateway_*`, `bridge_*` | gateway_get_info; bridge_status, list_installs, install, uninstall | Self-description, install the Bridge per Revit year. |
| `revit_*` | list_installed, list_instances, launch, close, get_state, wait_ready, list_dialogs, handle_dialog | Find, start, close Revit; readiness gate; dialogs. |
| `ui_*` | list_windows, snapshot, find, inspect, click, type, select, toggle, expand, keys, scroll, wait, expect, read_table, screenshot, run_script | Drive Revit and add-in windows. |
| `doc_*`, `model_*`, `schedule_*`, `view_*`, `selection_*` | info, open, close, query_elements, get_element, schedule_read, schedule_create, ... | Read the model through the Bridge; `schedule_create` only on a sandbox copy. |
| `ribbon_list_items`, `command_post` | | See and run the ribbon button of any add-in. |
| `sandbox_*` | create_run, copy_model, list_runs | Model copy plus SHA-256 proof the original is unchanged. |
| `scenario_*`, `recorder_*` | validate, run, list_runs; start, stop, export | Scenarios with checks and reports; record and replay. |
| `macro_list`, `macro_run` | built-in `create-schedule`, `run-addin-command` | Verified multi-step recipes ([macros](docs/agent-guide/macros.md)). |
| `human_ask` | | Ask the owner a question. |

Full catalog: [docs/agent-guide/tool-catalog.md](docs/agent-guide/tool-catalog.md).

## Safety principles

Enforced by the gateway for every client:

1. **Dialogs that need a human are never answered by the AI:** security, unsigned add-in, licence, recovery, delete/overwrite return `PERMISSION_DENIED`. Documented exceptions only:
   - For a Revit **the gateway launched**, the "Security - Unsigned Add-In" prompt is answered from the owner's add-in allowlist (dashboard): listed add-in gets `Load Once`, anything else `Do Not Load`, never `Always Load`. The AI has no tool to edit the list; each click is audited with a screenshot.
   - The gateway may safe-dismiss harmless dialogs that block its own operation in a Revit it launched (see [revit-process-and-dialogs.md](docs/agent-guide/revit-process-and-dialogs.md)).
   - Optionally, a forbidden dialog in unattended runs can be escalated to the owner, who chooses the button.
2. **Only a Revit the gateway launched is closed or killed.** For the user's own Revit the AI may always read, but may act (click, type, run commands, open/close documents) only while the owner turns on **"Let AI act on my Revit"** on the dashboard. It is time-limited, held in Host memory only, revocable at any moment, and each use is audited.
3. **Models are opened only from the sandbox** (`%REVITAGENT_HOME%\runs`, default `%LOCALAPPDATA%\RevitAgent\runs`) and are never saved over the original.
4. **Every tool call is audited** (`%LOCALAPPDATA%\RevitAgent\audit\*.jsonl`); a permission ceiling (Read/Interact/Write/Admin) is set on the dashboard.
5. **No tool runs arbitrary code.**

## Documentation

| Where | What |
|---|---|
| [docs/README.md](docs/README.md) | Index. |
| [docs/agent-guide/](docs/agent-guide/README.md) | For AI agents and scenario authors: connect a client, recipes, UI automation, dialogs, scenarios, macros. |
| [docs/architecture/decisions/](docs/architecture/decisions/) | Architecture decision records. |
| [docs/components/bridge-v2.md](docs/components/bridge-v2.md) | The Bridge add-in. |
| [docs/contributing/](docs/contributing/index.md) | Git, coding, testing, PR rules. |
| [examples/scenarios/](examples/scenarios/README.md) | Example scenarios. |

## Status

Verified live on Revit 2024.3 on 2026-11-26 with Claude Code (Haiku and Opus) and Antigravity CLI (Gemini 3.8 Flash).
Branch `feature/gateway-v2`.

## License

License: TBD by the owner.
