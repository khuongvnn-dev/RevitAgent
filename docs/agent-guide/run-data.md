# Run Data Store layout

Every scenario run (through `scenario_run` or the dashboard) and every sandbox run an AI agent creates (`sandbox_create_run`, `sandbox_copy_model`) is kept under one user-configured root folder, grouped by year-month and day.

## 1. Core rules

- **Never deleted.** Everything a run produces (model copy, step logs, evidence screenshots, reports) is kept forever. There is no cleanup logic that deletes data.
- **Never overwritten.** Each run folder name contains a random 6-character hex id (`<id6>`), so a new folder is always independent and an old one is never reused or overwritten.
- **Configurable root.** Set in `%REVITAGENT_HOME%\config\run-store.json` (default `%REVITAGENT_HOME%\runs`, that is `%LOCALAPPDATA%\RevitAgent\runs`). Changing the root only affects new runs; older runs stay reachable.

## 2. Folder structure

```
<root>\
  index.jsonl                                   one line per run (append-only)
  <yyyy-MM>\<yyyy-MM-dd>\<HHmmss>_<scenario-slug>_<id6>\
    run.json        id, scenario name (key `scenario`), trigger (manual|schedule|agent|cli), machine, user,
                    revit {year, build, pid}, addins [{name, path, sha256}], gateway version,
                    scenario sha256, started_at, ended_at, verdict, counts
    scenario\       frozen copy of scenario.yaml, the ui\*.json scripts and dataset files
    model\          the run's model copy + source.sha256.txt (before/after)
    data\           step_status.json, checks.json, one <step>.json per step, ui\ output
    evidence\       screenshots, exports
    logs\           audit slice of the run, bridge log copy
    report.md, report.html, result.json, run-info.json
```

### Components

1. **`index.jsonl`**
   - Directly under `<root>\`.
   - One JSON object per finished run, appended under a file lock so several processes can write at once.
   - Summary metadata: `id`, scenario name, `trigger`, `verdict`, `started_at`, `ended_at`, `run_dir`, `report_html`, `report_md`, `machine`, `counts`.
   - Lets `runs_list` and the Dashboard list runs quickly without walking the whole disk.

2. **`<yyyy-MM>\<yyyy-MM-dd>\<HHmmss>_<scenario-slug>_<id6>\`**
   - Grouped by local time: year-month, then day.
   - The name is the time `HHmmss`, the scenario slug (lower-case ASCII `[a-z0-9-]`, at most 40 characters) and `id6`.

3. **`run.json`**
   - Identity and full outcome of the run:
     - `id`: run id (same as the folder name).
     - `scenario`: the scenario name, or the run name for sandbox runs. Records written by older versions use the key `suite`; readers accept it as a fallback.
     - `trigger`: `manual`, `schedule`, `agent` or `cli`.
     - `machine`, `user`: computer and OS user.
     - `revit`: `year`, `build`, `pid`.
     - `addins`: add-ins loaded in Revit with their SHA-256.
     - `gateway_version`: MCP gateway version.
     - `scenario_sha256`: SHA-256 of the frozen scenario.
     - `started_at`, `ended_at`: ISO-8601 times.
     - `verdict`: `PASS`, `FAIL`, `SUSPECT`, `ERROR` or `RUNNING`.
     - `counts`: step counts (`steps`) and check counts (`checks`: total, pass, fail, not_measurable).

4. **`scenario\`**
   - Frozen snapshot of the scenario at run time: `scenario.yaml`, the `ui\` folder and the dataset files.
   - Reproduces the exact run even if the original scenario is edited later.

5. **`model\`**
   - The Revit model copy (`.rvt`, `.rfa`, `.rte`, `.rft`) and linked files opened in the run.
   - `source.sha256.txt`: the SHA-256 of the original file before and after the copy, proving the original was not changed.

6. **`data\`**
   - Per-step execution data: `step_status.json`, the check results `checks.json`, and the JSON/CSV files each tool step wrote.

7. **`evidence\`**
   - UI screenshots, viewport images and other visual diagnostics collected during the run.
   - **Current run.** When an MCP session creates or uses a run (`sandbox_create_run`, `sandbox_copy_model`, or `doc_open` of a model copy inside a run folder), that run becomes the session's *current run*; `doc_open` also remembers pid -> run. Every capture then defaults to `<run>\evidence\` instead of `<home>\captures`: `ui_screenshot` and `view_export_image` without `save_path`, `revit_list_dialogs` / `revit_get_state` with `include_screenshot`, `ui_run_script` without `out_dir` (`evidence\ui-scripts\<name>_<time>`), and the gateway's own dialog captures for that Revit pid (add-in trust, safe dismiss, escalation, timeouts). The session's run wins over the pid's; with no run the captures folder is used. An explicit `save_path` / `out_dir` keeps working with the same sandbox checks. Default names are sortable: `<HHmmss-fff>_<tool>_<detail>.png`.
   - **`evidence\index.jsonl`.** Whenever the gateway writes a file into a run's `evidence\` folder (default or explicit path, any depth) it appends one JSON line (append-only, file-locked): `{"at", "file", "tool", "session_id", "pid", "call_id", "kind", "bytes"}`. `file` is the absolute path, `kind` is `screenshot`, `dialog`, `report` or `export`, `bytes` the size when written. A report that cites an evidence file can be verified against this index: a cited file that is not listed (or not on disk) was not captured by the gateway. Always cite the `path` a tool result returned.

8. **`logs\`**
   - `audit.jsonl`: the tool-call audit records of the run's time window, sliced from the gateway's audit log.
   - `bridge-<pid>.log`: copy of the Bridge add-in diagnostic log of the matching Revit process.

9. **`report.md`, `report.html`, `result.json`, `run-info.json`**
   - Reports in Markdown and visual HTML.
   - `result.json`: per-check detail (`{scenario, run_id, verdict, error, checks:[...]}`), see [scenarios.md](scenarios.md).
   - `run-info.json`: run metadata written by the runner (scenario, run, pid, hashes, verdict, cleanup).

## 3. MCP tools

- **`runs_list`** (Read): recent runs (newest first) from `index.jsonl` plus a scan of folders not yet indexed (covers crashes and runs in progress).
  - Filters: `scenario` (name substring), `verdict` (`PASS`, `FAIL`, `SUSPECT`, `ERROR`), `from`, `to` (ISO-8601 range), `limit` (default 50).
- **`runs_get`** (Read): the full `run.json` and a list of every file in the folder with its size in bytes (`path`, `size`).
- **`scenario_list_runs`** (Read): the scenario-oriented view over the same store (filter `scenario`).

## 4. Dashboard (Host)

- **Run history page**
  - Runs grouped by day.
  - Status filter (`ALL`, `PASS`, `FAIL`, `SUSPECT`, `ERROR`, `RUNNING`) and a search box on scenario name or id.
  - **Open report** opens `report.html` in the default browser.
  - **Open folder** opens the run folder in Windows File Explorer.
- **Settings page**
  - **Run data folder:** shows the current root with a browse button (`OpenFolderDialog`).
