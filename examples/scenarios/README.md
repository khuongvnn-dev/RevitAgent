# Example scenarios

Two examples for `scenario_validate` / `scenario_run` (format: [docs/agent-guide/scenarios.md](../../docs/agent-guide/scenarios.md)). Each scenario is a folder with one `scenario.yaml` (metadata, steps, checks) and optional `ui\*.json` UI scripts ([docs/agent-guide/ui-automation.md](../../docs/agent-guide/ui-automation.md)).

| Folder | What it does |
|---|---|
| `levels-inventory` | Tool steps only, no UI: confirms Levels is a schedulable category (`schedule_list_schedulable_categories`, verified on Revit 2024.3), lists the Levels with `model_query_elements`, runs `doc_info`. |
| `room-schedule-ui` | One UI step creates a Room schedule (View > Schedules > Schedule/Quantities), then `schedule_read` and `model_list_views` verify it. |

**These are examples; confirm them on a real Revit.** The selectors in `ui\01-create-room-schedule.json` and the result field names used in the checks (`elements`, `categories`, `views`, `name`) are best guesses. Confirm them with `ui_snapshot` and with the `data\<step>.json` files of a real run, then adjust.

Both scenarios declare `model: 'input\model.rvt'`. That file is **not shipped**: copy a small .rvt into the scenario's `input\` folder (or change `model:` in `scenario.yaml`). Without it `scenario_validate` reports `model-missing` and `scenario_run` refuses to start.

## Run

1. Open Revit (with the Bridge) and get its pid: `revit_list_instances`.
2. Validate: `scenario_validate {scenario_dir: "<path>\\examples\\scenarios\\levels-inventory"}`.
3. Run: `scenario_run {scenario_dir: "...", pid: <pid>}` (or `launch: true` to let the run start Revit 2024 and close it afterwards).
4. Read `report.md` in the returned `run_dir`.

The model is always copied into the run folder (inside the sandbox) and only the copy is opened; the original is never touched.
