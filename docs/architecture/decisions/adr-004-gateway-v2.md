# ADR-004: RevitAgent v2, a standalone gateway for AI control of any Revit version and of add-ins without source code

- **Date:** 2026-11-20
- **Status:** Accepted (owner approved 2026-11-19). Decision 7 is superseded by ADR-006.
- **Supersedes:** the scope of ADR-001..003 for the MCP layer

## Context

v1 (October 2026) built the infrastructure (Host outside Revit, Bridge inside Revit, named pipes) but:

- The hand-written MCP server did not accept numeric JSON-RPC ids, so real clients could not connect.
- The Bridge had only 5 read tools, **globally blocked** Sync with Central, auto-closed every dialog and cleared warnings.
- The dialog-closing tool clicked OK/Yes/No blindly.
- There were no scenarios and no evidence capture.
- Only Revit 2024 was supported.

Owner goal: "Turn the tool on and any AI agent can drive Revit in any version, together with its add-ins, without knowing their source code, either following a scenario or working it out to reach a result."

## Decisions

1. **Two layers of "eyes and hands", one verification loop.** The UI layer (out-of-process UI Automation, screenshots, Win32 dialog detection) works with **any running Revit, with nothing installed**. The API layer (the Bridge add-in) adds model and schedule reading, the ribbon catalog of **every add-in**, and `PostCommand` to run add-in buttons without source code. The agent follows *observe, act, verify*.
2. **MCP through the official SDK** (`ModelContextProtocol.Core` 2.4.0) in `RevitAgent.Core`. The Host (dashboard) serves it on `\\.\pipe\revitagent-mcp` (current user only). The Shim is a stdio-to-pipe byte relay that starts the Host and falls back to headless mode if no Host is available.
3. **Tool framework.** `IGatewayTool` with auto-discovery. Every call goes through `ToolRegistry`: policy check, timeout, standard error code, JSONL audit, observer. Error codes follow a standard set, plus `SANDBOX_VIOLATION` and `MODAL_BLOCKED`.
4. **Thin, multi-version Bridge** for Revit 2022-2026 (net48 up to 2024, net8 from 2025), with **no third-party DLLs** (avoids assembly conflicts with other add-ins). A per-PID pipe `revitagent-bridge-<pid>` lets one gateway drive several Revit instances. The Bridge does **not** block or auto-answer dialogs and does **not** cancel Sync with Central; it only records dialog events for the agent to read.
5. **Safety in the gateway**, valid for every client: only copies in the sandbox (`runs\`) are opened; security, license, recovery and delete/overwrite dialogs are never answered automatically; only Revit instances the gateway launched are closed or killed; each tool declares its permission (Read/Interact/Write/Admin) and the ceiling is adjustable in the dashboard.
6. **No tool that runs arbitrary C#.** This may be revisited later under Admin permission.
7. **Scenarios and evidence, with a recorder.** *Superseded by ADR-006:* scenario and evidence formats and any compatibility mode are now RevitAgent's own. The recorder idea stays: when an agent solves a task, export it as a scenario and replay it without AI.
8. **Keep the WPF dashboard** for enabling tools and monitoring. Data lives in `%LOCALAPPDATA%\RevitAgent` (override with `REVITAGENT_HOME`); the repository stores no run data.

## Consequences

- A Revit that was already running *before* the Bridge was installed can use only the UI layer (an add-in cannot be loaded into a running Revit).
- The UI layer needs an unlocked Windows session at the same privilege level as Revit (UIPI). Drawing areas have no UIA tree, so use screenshots with coordinates, or the API.
- `PostCommand` is fire-and-forget: the agent must observe the UI afterwards. Tool descriptions say so.
