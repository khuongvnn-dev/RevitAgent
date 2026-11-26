# Writing a QA scenario from a user story

For an AI agent working **together with a QA engineer** to turn a user story (US) and its attachments into a scenario that runs **without AI** (`scenario_run`). Read first: [scenarios.md](scenarios.md) (the `scenario.yaml` format and the `expect` language) and [recipes.md](recipes.md) (recipe (b), proven on a real Revit, and recipe (d), record and replay).

## 0. Hard rules

- **Use only the US and its attachments.** Do not guess behaviour, data, button names, dialog names or expected values.
- **Anything unclear or missing: ask the QA engineer first (grill them properly) before writing the scenario.** Record every question and answer in the grill sheet (section 3).
- **Every expected value in a check needs a source:** `US §<section>`, `Attachment: <file, section>`, or `QA <date>: <answer>`. No source means it does not go into `checks`; write it as a question instead.
- Run only on a **sandbox copy**. Never open or overwrite the original model.

## 1. Required inputs

| Item | Notes | If missing |
|---|---|---|
| User story | Id, title, description, numbered sections (§) | **STOP** |
| Acceptance criteria (AC) | Each AC has an id (AC-01...) | **STOP**: no checks without AC |
| Attachments | Specs, screenshots, data tables. Every document the US refers to must be present | **STOP** |
| Test model path | A `.rvt` the QA engineer allows to be copied | **STOP** |
| Revit version | A year, for example 2024 (`revit_version`) | **STOP** |
| Add-in build | DLL/build path, if the scenario calls add-in commands | **STOP** if the scenario uses the add-in |

**Stop-if-missing:** if any required item is missing, stop writing and tell the QA engineer exactly which items are missing. Do not guess to fill gaps. Do not create a `scenario.yaml` with made-up values.

## 2. Procedure

**Step 1 - Intake.** Read the US and attachments. Build the AC table. For each AC note whether it can be measured in the model, in a file, or not automatically (then QA decides: change the measurement, or list it under `manual_steps`).

**Step 2 - Grill.** Create the grill sheet (section 3) and ask the QA engineer. Mandatory question groups:

- **Preconditions:** which model, which view is open, what state before the run.
- **Input data:** concrete values (names, numbers, strings). "Any value" is not acceptable.
- **Exact UI path:** tab, panel, command name (`command_post`), window title, button label. If a name is unknown, ask.
- **Dialogs that may appear**, including forbidden ones (security / unsigned add-in, license, recovery, delete / overwrite / replace). The scenario does **not** answer forbidden dialogs; ask QA "stop and report, or rerun by hand?".
- **Measurable expected results:** element counts, names, parameters, file content. "Looks right" is not acceptable.
- **Time / performance:** a threshold in seconds and the model size. No number means no timing check.
- **Negative cases:** wrong data, missing permission, the user cancelling midway. For each: the expected result and its source.
- **Cleanup:** usually none, because runs use a copy. Ask whether result files must be exported anywhere.

**Step 3 - Draft `scenario.yaml`.** Write steps and checks per [scenarios.md](scenarios.md). Where unsure, write a `TODO` with the reason; never fill in a guess.

**Step 4 - QA review.** QA reviews the grill sheet, the step list and every check. Write the review date in the sheet.

**Step 5 - Dry run with the recorder.**
```json
{ "tool": "recorder_start", "args": { "name": "<scenario-name>" } }
```
Perform the task once on the copy (prefer `id=` selectors; observe and read back after each action), then:
```json
{ "tool": "recorder_stop", "args": {} }
{ "tool": "recorder_export", "args": { "name": "<scenario-name>" } }
```

**Step 6 - Resolve the TODOs.** Selectors `TODO-ref:` become `id=<AutomationId>` (`ui_inspect`). `model:` points at the original model (the run copies it). Every expectation the recorder generated comes from **the value seen while recording**: compare it with the source; with no source, delete it or ask QA.

**Step 7 - `scenario_validate`.** Fix errors. Explain or fix warnings in the sheet.

**Step 8 - `scenario_run`.**
```json
{ "tool": "scenario_run", "args": { "scenario_dir": "<path>", "pid": <pid> } }
```
Read `report.md` in the returned `run_dir`. The first run usually reveals the real field names: fix the checks from the `data\<step>.json` of the **real run** and cite `run result <run_id>` as the source. Never change an expectation just to get PASS. QA must look at every NOT_MEASURABLE or FAIL check.

**Step 9 - Freeze.** When the scenario PASSes exactly as the AC require and QA has approved it, copy the scenario folder into the repo with the grill sheet and the traceability table, and commit. After the freeze every change needs a new row in the grill sheet and a new QA review.

## 3. Grill sheet template

Keep it next to the scenario (for example `grill.md`). A question is closed when it has an answer and a source.

| # | Question | Why (AC / US section) | QA answer | Source |
|---|---|---|---|---|
| 1 | Which view is the test model in before the run? | AC-01, precondition | | |
| 2 | What is the ribbon command name (for example `ScheduleOrQuantities`)? | AC-01, UI path | | |
| 3 | Does dialog "..." appear? If so, which button does QA want? | Dialogs that may appear | | |
| 4 | How many elements are expected after step X? | AC-02, measurable result | | |
| 5 | Maximum time for step Y (seconds)? | AC-03, performance | | |

Note: "I don't know" is a valid answer, but then the matching check cannot be written as PASS/FAIL. Say so in the "QA answer" column.

## 4. Traceability

Every AC must trace to a step and a check. An AC with no step or check means the scenario is not finished.

| AC | Step (`scenario.yaml`) | Check | Source |
|---|---|---|---|
| AC-01 | `create-room-schedule` (ui) | `ui-create-ok`: `step_ok(create-room-schedule)` | US §2.1; QA 2026-11-25 #2 |
| AC-02 | `room-schedule` (tool `schedule_read`) | `schedule-columns`: `count(room-schedule.json -> fields) == 3` | US §2.2; Attachment spec-A section 3 |
| AC-03 | none | none yet (question #5 unanswered) | none |

## 5. Rules for scenarios that run unattended

- **Verify through the Bridge when available** (`doc_info`, `model_query_elements`, `schedule_read`, `model_get_warnings`). Use the UI only to act. Without the Bridge, verify through the UI (`ui_read_table`, screenshots) and say so in the check's `note`.
- **Selectors `id=<AutomationId>`** come first. Never use `ref` (it lives for one snapshot). Do not use display names when an id exists.
- **Explicit waits:** `ui_wait` with `window` and `state` and a `timeout`. No fixed sleeps. Observe again after each action.
- **Do not search ribbon buttons with UIA on a large model** (slow, see recipe (b) in [recipes.md](recipes.md)). Use `command_list_postable` and `command_post` with a confirmed command name.
- **Never answer** security / unsigned add-in, license, recovery, delete, overwrite or replace dialogs. A scenario must not contain a `dialogs` rule that answers them. If they appear during a run the run stops and reports; it does not handle them.
- **Sandbox copy only.** No `doc_close` with `save: true`. No `scenario_*`, `recorder_*` or Admin tools in `steps`.
- **Delete nothing from run data.** Do not write a step or script that removes run folders; run data is always kept.
- **A check with missing data is NOT_MEASURABLE, never PASS.** Pair every `== 0` with a `>= 1` check on the same array to prove the data is real.
- **Do not leave `TBD` / `pending` in a threshold to get a quiet run.** It is a flag for QA to decide.
- **Never type passwords, tokens or secrets** into any field.
- **Field names in checks** must match the real tool result (confirm with `data\<step>.json` of a real run), not a guess.

## 6. Prompt for MCP clients (Claude, Gemini, ...)

Use it to start a scenario-writing session. Replace the `<...>` items.

```
You work with a QA engineer to write a RevitAgent scenario from a user story. Read and follow
docs/agent-guide/qa-scenario-authoring.md before doing anything else.

Inputs:
- US: <id and title>, content: <path or paste>
- Acceptance criteria: <path or paste>
- Attachments: <list of paths>
- Test model (a copy is allowed): <path>
- Revit: <year>
- Add-in build: <path, or "not used">

Rules:
1. Use only the US and the attachments. Do not guess behaviour, data, button names or expected values.
2. If any required input is missing: stop, list exactly what is missing, write no file.
3. Build the AC table. Build the grill sheet and ask the QA engineer each question. Record every question and answer.
4. Do not write scenario.yaml while blocking questions are unanswered.
5. Every expected value in a check needs a source (US §, attachment, or QA + date).
6. Never answer security, unsigned add-in, license, recovery, delete, overwrite or replace dialogs.
   If they appear, stop and ask QA.
7. Run only on a sandbox copy. Never overwrite the original model. Never delete run data.
8. Procedure: dry run with recorder_start -> do the task -> recorder_stop -> recorder_export,
   resolve TODOs, scenario_validate, scenario_run, read report.md, then present to QA for review.

Start with: the inputs you have and those still missing, the AC table, and the first grill sheet.
```

## 7. Definition of done

- Every AC has a row in the traceability table, with a step, a check and a source.
- The grill sheet has no open question that blocks writing a check.
- `scenario_validate` reports no errors; `scenario_run` PASSes on the copy, or a FAIL / SUSPECT is clearly explained to and by QA.
- QA has reviewed and the review date is in the sheet.
