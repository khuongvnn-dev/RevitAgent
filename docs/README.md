# RevitAgent documentation

[Project README](../README.md) | [Tiếng Việt](../README.vi.md)

| You are | Read |
|---|---|
| **An AI agent or scenario author** | [agent-guide/README.md](agent-guide/README.md) (connect a client, the observe-act-verify loop) -> [recipes.md](agent-guide/recipes.md) -> [ui-automation.md](agent-guide/ui-automation.md) -> [revit-process-and-dialogs.md](agent-guide/revit-process-and-dialogs.md) -> [scenarios.md](agent-guide/scenarios.md) -> [qa-scenario-authoring.md](agent-guide/qa-scenario-authoring.md) |
| **A developer** | [architecture/decisions/](architecture/decisions/) -> [components/bridge-v2.md](components/bridge-v2.md) -> code in `src/RevitAgent.Core` (one folder per module: `Ui`, `Revit`, `Bridge`, `Install`, `Sandbox`, `Scenario`, `Macros`, `Policy`, `Mcp`, `Tools`) -> [contributing/](contributing/index.md) |
| **Verifying a build** | `scripts/acceptance.ps1` (live acceptance on Revit; evidence in the run store) |

## Agent guide (`agent-guide/`)

| Page | Topic |
|---|---|
| [README.md](agent-guide/README.md) | Connect a client, first calls, the loop, safety rules, error codes. |
| [tool-catalog.md](agent-guide/tool-catalog.md) | Every tool with permission and arguments. |
| [recipes.md](agent-guide/recipes.md) | Worked recipes. |
| [macros.md](agent-guide/macros.md) | `macro_list` / `macro_run` and the YAML macro format. |
| [ui-automation.md](agent-guide/ui-automation.md) | `ui_*` tools and UI scripts. |
| [revit-process-and-dialogs.md](agent-guide/revit-process-and-dialogs.md) | Find, start, check, unblock and close Revit; dialog policy; sandbox. |
| [scenarios.md](agent-guide/scenarios.md) | `scenario.yaml` format and the runner. |
| [qa-scenario-authoring.md](agent-guide/qa-scenario-authoring.md) | Writing scenarios from user stories. |
| [run-and-schedule.md](agent-guide/run-and-schedule.md), [run-data.md](agent-guide/run-data.md) | Runs, scheduling, the run store. |

## Architecture decisions (`architecture/decisions/`)

| ADR | Topic |
|---|---|
| [adr-001](architecture/decisions/adr-001-hybrid-out-of-process.md) | Out-of-process split. |
| [adr-002](architecture/decisions/adr-002-mvvm-di-refactor.md) | MVVM and dependency injection. |
| [adr-003](architecture/decisions/adr-003-mcp-channel-split.md) | Separate MCP channel. |
| [adr-004](architecture/decisions/adr-004-gateway-v2.md) | Gateway v2 architecture. |
| [adr-005](architecture/decisions/adr-005-agent-agnostic-gateway.md) | Agent-agnostic gateway. |
| [adr-006](architecture/decisions/adr-006-independent-open-source.md) | Independent open-source project. |

## Components and contributing

- [components/bridge-v2.md](components/bridge-v2.md): the in-Revit Bridge add-in (multi-version build, runtime, tools, install).
- [contributing/](contributing/index.md): [git-workflow](contributing/git-workflow.md), [coding-standards](contributing/coding-standards.md), [testing-policy](contributing/testing-policy.md), [pr-checklist](contributing/pr-checklist.md).
- [../examples/scenarios/](../examples/scenarios/README.md): example scenarios.
