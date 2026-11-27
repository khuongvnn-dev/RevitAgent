# Scenarios, runs and the recorder

A **scenario** is a folder that says what to do in Revit and how to judge the result. `scenario_run` executes it **without AI** and writes a report. A scenario is one file, `scenario.yaml`, plus optional UI scripts and input files.

Tools: `scenario_validate`, `scenario_run`, `scenario_list_runs`, `recorder_start`, `recorder_stop`, `recorder_export`. Examples: `examples/scenarios/`. Writing scenarios from a user story with a QA engineer: [qa-scenario-authoring.md](qa-scenario-authoring.md).

## 1. Folder layout

```
<scenario-name>\          folder name = the scenario name (a mismatch only warns)
  scenario.yaml           required: metadata, steps, checks
  ui\*.json               UI scripts used by `ui` steps (format: ui-automation.md)
  input\                  input files (the original model, data)
```

The scenario folder is **never written to** during a run. Everything a run produces goes into its run folder (section 4).

## 2. scenario.yaml

Windows paths and any value containing `{ }` go in single quotes: `model: '{dataset}\model.rvt'`.

```yaml
name: levels-inventory                 # required
description: 'List the Levels and confirm Levels is schedulable'
revit_version: 2024                    # needed with launch: true
model: 'input\model.rvt'               # ORIGINAL model; the run opens a copy
keep_model_copy: false                 # informational: the copy is always kept in the run folder
timeouts: { startup_min: 5, open_model_min: 10, step_min: 30 }   # defaults shown
retry_default: 0                       # default retries for safe steps (0-5)
retry_delay_sec: 5
hang_timeout_sec: 180                  # Revit not responding this long -> revit_hung
rerun_on_crash: 0                      # 1 = rerun once into a NEW run folder after revit_crashed
manual_steps: ['Look at the keyplan']  # optional; non-empty caps the verdict at SUSPECT

steps:
  - name: levels                       # step name = data file data\levels.json
    tool: model_query_elements         # any gateway tool
    args: { category: 'Levels', fields: ['Name', 'Elevation'], limit: 500 }
    on_error: stop                     # stop (default) | continue
  - name: create-schedule
    ui: 'ui\01-create-schedule.json'   # a UI script, run through ui_run_script
    on_error: stop
    timeout_sec: 300

checks:
  - id: levels-exist                   # unique, stable, never reused
    title: The model has at least one Level
    expect: count(levels.json -> elements) >= 1
    severity: fail                     # fail (default) | warn
    note: Counts the elements of data\levels.json.
```

### Top-level keys

| Key | Meaning |
|---|---|
| `name` | Required. |
| `description`, `revit_version`, `model`, `keep_model_copy`, `timeouts` | As in the example. `model` is never opened directly: the run copies it into its run folder and opens the copy. |
| `retry_default`, `retry_delay_sec`, `hang_timeout_sec`, `rerun_on_crash` | Resilience, section 3. |
| `manual_steps` | Work a person must do or look at. |
| `steps` | Required, non-empty. |
| `checks` | The pass criteria. Without checks nothing is judged and the verdict is at most SUSPECT. |

Unknown keys only warn.

### Steps

Each step has exactly one of `tool` or `ui`.

| Key | Meaning |
|---|---|
| `name` | Step name and result file `data\<name>.json` (default: the tool name, or `ui-NN`). Keep names unique. |
| `tool` / `args` | A gateway tool call. `pid` is added to `args` when the tool takes one and the step did not set it. |
| `ui` | Path of a UI script (JSON, relative to the scenario folder). Format and selectors: [ui-automation.md](ui-automation.md). |
| `on_error` | `stop` (default): skip the remaining steps and end ERROR. `continue`: go on. |
| `timeout_sec` | Default `timeouts.step_min * 60` (ui steps: 600). |
| `leave_open` | ui steps: `true` allows the script to leave a new window open (otherwise that FAILs the step). |
| `retry`, `retry_delay_sec`, `retry_safe` | Section 3. |
| `purpose`, `why` | `purpose: setup` builds a test situation by writing to the model copy and **requires `why`**. The run verifies the open document is the sandbox copy and writes `data\setup-guard.json`. A ui step cannot be `setup`. |

A step may not call `scenario_*` or `recorder_*` tools, Admin-permission tools (the run opens and closes Revit itself), or `doc_close` with `save: true`.

### Variables

| Variable | Value |
|---|---|
| `{run_dir}` | The run folder |
| `{pid}` | Revit pid of the run |
| `{model}` | Path of the opened model copy (needs `model`) |
| `{task_dir}` | The scenario folder |
| `{dataset}` | The `dataset` argument of `scenario_run` |

Every string in `args` is expanded. Unknown variables are left as written (validate warns). A known variable with no value (for example `{dataset}` without a `dataset` argument) fails the step with `INPUT`. Input paths: `input\X` means `<task_dir>\input\X`; `{dataset}\X` means `<task_dir>\input\X` if that file exists (override), else `<dataset>\X`.

## 3. Resilience

- `retry: N` (0-5) retries a step after an ERROR or TIMEOUT result, never after a failed check. Read-only tools are always safe to retry; action tools and ui steps retry only with `retry_safe: true`. Every attempt is logged in `data\step_status.json` (`attempts`) and `report.md`.
- When the run launched Revit it checks the process before and after each step and every 5 s while waiting. If Revit exits, the run stops with `revit_crashed` (exit code and last window title recorded; the newest Revit journal is copied into `logs\`, never moved). If the main window stays not responding for `hang_timeout_sec`, a screenshot goes to `evidence\` and the run stops with `revit_hung`.
- `rerun_on_crash: 1` reruns the scenario once, with a fresh Revit, into a new run folder linked by `previous_run_id` / `rerun_of` in `run.json`. Old data is never overwritten.

## 4. Running: scenario_validate, then scenario_run

```json
{ "name": "scenario_validate", "arguments": { "scenario_dir": "<repo>\\examples\\scenarios\\levels-inventory" } }
{ "name": "scenario_run",      "arguments": { "scenario_dir": "<repo>\\examples\\scenarios\\levels-inventory", "pid": 12345 } }
```

`scenario_run` arguments: `scenario_dir` or `scenario_path` (the scenario.yaml file); `pid` (use a running Revit, never closed by the run) **or** `launch: true` (start `revit_version`, close it afterwards), not both; `keep_open` (leave the document and a Revit launched by the run open); `dataset`. A scenario with a `model` or a ui step needs `pid` or `launch`. One run at a time (a second call returns `BUSY`); a run may take hours (limit 2 h). `scenario_run` refuses to start while `scenario_validate` reports **errors** (warnings do not block).

What a run does, in order:

1. **Validate**, then **create the run folder** through `sandbox_create_run` (inside the sandbox, `<home>\runs\...`; home defaults to `%LOCALAPPDATA%\RevitAgent`, override with `REVITAGENT_HOME`). The scenario is frozen into `scenario\` of the run folder.
2. **Attach or launch Revit**: `launch: true` runs `revit_launch {year}` + `revit_wait_ready`; `pid` is checked against `revit_list_instances`.
3. **Model copy**: sha256 of the original, `sandbox_copy_model` into the run folder, `doc_open` of the copy, then `doc_info` must report a path inside the run folder or the run stops (ENV).
4. **Each step**: a readiness check (`revit_get_state`) first: busy or loading is waited for up to 90 s; a modal dialog, an unresponsive or exited Revit stops the run (ENV).
   - `tool` step: the result is saved as `data\<name>.json`. A failing tool FAILs the step and its error goes to `data\<name>.error.json`; `MODAL_BLOCKED` stops the whole run.
   - `ui` step: `ui_run_script {script_path, pid, out_dir: data\ui\NN-<name>}`. A script whose verdict is `blocked` (a forbidden dialog or a safety refusal stopped it) makes the step **BLOCKED**; it writes no `data\<name>.json` (the result is `<name>.blocked.json`); the run continues and every check reading `<name>.json` is NOT_MEASURABLE. A script that succeeds but leaves a **new window** open FAILs unless `leave_open: true`. `doc_info` is re-checked after a ui step.
5. **Cleanup, always**, even after errors: `doc_close` without saving, close Revit **only if this run launched it**, keep the model copy, and verify the original model's sha256 is unchanged (a change is ERROR).
6. **Evaluate the checks and write the reports.**

### Files in the run folder

| File | Content |
|---|---|
| `scenario\` | Frozen copy of scenario.yaml and `ui\*.json` |
| `run.log` | Log of every call |
| `run-info.json` | Metadata: scenario, run, pid, scenario sha256, model sha256, verdict, cleanup |
| `data\<step>.json` | Result of each step (`.error.json` on error, `.blocked.json` for a BLOCKED ui step) |
| `data\ui\NN-<name>\` | Output of `ui_run_script` |
| `data\step_status.json` | `{ok, steps:[{stepCode, status}]}` (ERROR shows as FAILED) |
| `data\checks.json` | Every check with its evaluated terms |
| `result.json` | `{scenario, run_id, verdict, error, checks:[{id, title, severity, expect, actual, evidence, verdict, note, key}]}` |
| `report.md`, `report.html` | Report for people ("Needs attention" first) |

`scenario_run` returns the check and step counts, `needs_attention`, `failed_steps` and the report paths. Read `report.md` next, then `data\*.json`. More on the layout: [run-data.md](run-data.md).

### Verdicts

Run verdict, first match wins:

| Verdict | When |
|---|---|
| `ERROR` | The run stopped early (ENV or INPUT problem, cancelled), a step with `on_error: stop` FAILED, or the original model file changed |
| `FAIL` | A check with `severity: fail` FAILs |
| `SUSPECT` | No checks, a check is NOT_MEASURABLE, a `severity: warn` check FAILs, or `manual_steps` exist |
| `PASS` | Every check PASSes |

Check verdicts are `PASS`, `FAIL` and `NOT_MEASURABLE`. **A check is never PASS when its data is missing**, and a threshold still `TBD` is NOT_MEASURABLE.

### scenario_list_runs

`scenario_list_runs {scenario?, verdict?, limit?}` lists recent runs (newest first): run id, scenario, verdict, times, `run_dir`, report paths. `scenario` filters by name substring, `verdict` by PASS/FAIL/SUSPECT/ERROR, `limit` defaults to 20.

## 5. Checks and the `expect` language

Each check:

| Field | Meaning |
|---|---|
| `id` | Required. Unique within the scenario; letters, digits, `-`, `_`, `.`. Never reuse a retired id. |
| `title` | Short human description. |
| `expect` | Required. The condition, an expression (below). |
| `severity` | `fail` (default): a FAIL fails the run. `warn`: a FAIL only caps the run at SUSPECT. |
| `note` | Optional. How the value is measured and where the threshold comes from. |

### Grammar

```
expr       := term ( "or" term )*              "and" binds tighter than "or"
term       := factor ( "and" factor )*
factor     := "not" factor | "(" expr ")" | comparison | condition | TBD
comparison := value OP value [ "+-" value ]    OP: == != < <= > >=;  "+-" (tolerance) only after ==
value      := number | "text" | TBD | count(source) | value(source)
condition  := step_ok(step-name) | has_line(file, "regex") | no_line(file, "regex")
source     := file -> path
path       := field.field[filter, filter].field        field = name or "quoted name"
filter     := Field = "x" | Field != "x" | Field starts_with "x" | Field contains "x" | Field not_contains "x"
```

| Function | Result |
|---|---|
| `count(file -> path)` | Number of nodes the path selects |
| `value(file -> path)` | The single scalar the path selects (zero or several nodes: NOT_MEASURABLE) |
| `step_ok(name)` | The step finished SUCCEEDED according to `data\step_status.json` (never ran: FAIL; BLOCKED: NOT_MEASURABLE) |
| `has_line(file, "regex")` / `no_line(...)` | A line of the file matches / no line matches |

Rules of the language:

- `file` is a file of the run: a name under `data\` (`levels.json`) or in the run folder (`run.log`). `file.json` alone is the file's root, for example `count(rows.json) == 2`.
- Arrays are flattened at every path segment. Field names are case-insensitive. A quoted segment allows spaces: `value(doc.json -> info."Room Name") == "Office"`.
- Numbers compare numerically (with `+-` tolerance after `==`); two texts compare as exact text. Ordering a non-number is NOT_MEASURABLE.
- Terms combine with three-valued logic: `and` is FAIL if any term FAILs, else NOT_MEASURABLE if any is, else PASS; `or` is PASS if any term PASSes, else NOT_MEASURABLE if any is, else FAIL; `not` swaps PASS and FAIL.
- **NOT_MEASURABLE, never PASS,** when: a file or step result is missing (a step that failed, a BLOCKED ui step), the first path segment is absent from the file (a typo would otherwise count 0), `value` does not select exactly one scalar, the expression cannot be read, or a `TBD` (or `pending`) appears as a threshold or tolerance.
- A check reading `count(...) == 0` proves nothing if the data may be absent: add a `>= 1` check on the same array.
- Field names must match the real tool result: read `data\<step>.json` of a real run. A wrong field name *inside a filter* counts 0 items.

Examples:

```
count(levels.json -> elements[name="Roof"]) == 1
value(levels.json -> elements[name="Level 2"].elevation) == 3000 +- 0.05
step_ok(create-schedule) and count(schedule.json -> fields) == 3
no_line(run.log, "ERROR|Exception") and not has_line(run.log, "unsigned")
count(views.json -> views[name starts_with "Room"]) >= 1 or step_ok(manual-review)
```

### What scenario_validate checks

No Revit needed, under 1 second. Errors block `scenario_run`.

- YAML: syntax (with line), mapping at the root, `name` present, `steps` non-empty, exactly one of `tool`/`ui` per step, valid `on_error`, `purpose`, `timeout_sec`, `revit_version`, `timeouts`, booleans and ranges (`retry` 0-5, `hang_timeout_sec` > 0, `rerun_on_crash` 0 or 1).
- Warnings: unknown keys, `name` different from the folder name, duplicate step names (a later data file overwrites an earlier one), unknown variables, ui step without `name`, model that is not .rvt/.rfa.
- Model: the file must exist (`model-missing`); `{model}` without a `model` is an error.
- Tool steps: tool not registered in this gateway, forbidden tool (`scenario_*`, `recorder_*`, Admin, `doc_close save: true`), unknown or missing required arguments (except `pid`), `purpose: setup` without `why`.
- ui steps (`UiScriptLint`): script exists and is valid JSON with a non-empty `steps` array; every `do` is a known command; no `launch` / `run`; `close` and Alt+F4 need a `window`; no typing into password/token/secret fields; no acting inside a window titled like a security, unsigned add-in, license or recovery dialog (error) or delete/overwrite/replace (warning); `dialogs` rules never auto-answer those dialogs; Ctrl+S / Ctrl+O warn; the top-level `window` should stay empty. Script format: [ui-automation.md](ui-automation.md).
- Checks: no checks (warning), duplicate ids (error), expression syntax (error, with the reason), invalid `severity` (error), missing `title` (warning), `step_ok(x)` naming a step that does not exist, and a read of `x.json` that no step writes (warnings).

## 6. Recorder: explore, record, freeze, replay without AI

```
recorder_start {name?}
   ...the agent works as usual (prefer id= selectors, then read back to verify)...
recorder_stop
recorder_export {name: "room-schedule-ui"}
   -> fix the TODOs -> scenario_validate -> scenario_run {scenario_dir, pid}
   -> copy the folder into the repo and commit
```

- `recorder_start {name?, restart?}` records the **successful** calls of the current MCP session. Already recording: `CONFLICT` unless `restart: true`.
- Recorded: action and write tools (UI click/type/select..., Revit commands, model writes) and verification reads (`ui_expect`, `ui_wait`, `ui_read_table`, `model_query_elements`, `schedule_read`, `doc_info`). Not recorded: failed calls, `recorder_*`, `scenario_*`, `gateway_*`. At most 5000 calls per session, kept in memory until the gateway restarts.
- `recorder_stop` stops and returns the call counts per tool.
- `recorder_export {name, out_dir?, overwrite?, session_id?}`: `name` is lower-case letters, digits, `-`, `_`. Default target `<home>\scenarios\<name>\`; `out_dir` must be inside the home folder. An existing folder is `CONFLICT` unless `overwrite: true`.

The draft it writes:

- **Steps.** Consecutive `ui_click / ui_type / ui_select / ui_toggle / ui_keys / ui_wait / ui_expect / ui_expand / ui_scroll` calls become one `ui` step with a generated script `ui\NN-ui.json`. A recorded `ui_run_script` copies that script. Everything else becomes a `tool` step (`pid` removed, the model copy path becomes `{model}`, paths in the run folder become `{run_dir}\...`).
- **Selectors.** Selectors and `id=` are used as recorded. If only a `ref` was used the exporter tries to derive a selector from the result; otherwise it writes `TODO-ref:<ref>` (the step fails loudly) and lists it in `todos`. Replace it with `id=<AutomationId>` (`ui_inspect`).
- **Not exported** because `scenario_run` does it: `revit_launch`, `revit_close`, `revit_wait_ready`, `sandbox_create_run`, `sandbox_copy_model`, `doc_open`, `doc_close`, `bridge_install` / `bridge_uninstall`, `revit_handle_dialog` (listed in `not_exported`). The original model and `revit_version` are derived from the recorded `sandbox_copy_model` / `revit_launch`; otherwise they are left as TODO.
- **Checks `C01`, `C02`, ...** come from the verification reads, with **the recorded value as the expectation** (a person must confirm it): a ui step becomes `step_ok(<step>)`; a list result (`model_query_elements`, ...) becomes `count(<file> -> <array>) == n` plus per-label counts when there are at most 10 items; `schedule_read` becomes `value(<file> -> body.data_rows) == n` (data rows, not the column headings); `doc_info` becomes `value(<file> -> title) == "..."`; a result too large or without a list becomes `step_ok(<step>)` with a TODO. Each check's `note` says how it was recorded.

After exporting: open `scenario.yaml`, resolve the TODOs, point `model` at the original file, run `scenario_validate` and `scenario_run`, and fix until it passes.

## 7. Differences worth knowing

- The run folder lives in the gateway sandbox (`<home>\runs\...`), never next to the scenario.
- A model copy is always kept in the run folder; nothing in a run folder is ever deleted.
- Not supported: grouped counts, image comparison, baseline comparison. Anything the expression language cannot express is checked by a person or an agent reading the report, and is not a check.
