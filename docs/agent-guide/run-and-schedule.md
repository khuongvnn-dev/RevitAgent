# Running scenarios from the Dashboard and the nightly schedule

RevitAgent lets a QA team run automated tests on their own, without an AI client:

1. **Manual run:** pick scenarios in the dashboard and press "Run selected".
2. **Nightly schedule:** set a time and weekdays and the machine runs a batch of scenarios by itself.

Every run is stored in the central **Run Data Store** with its `trigger` (`manual` or `schedule`), and a short summary is sent over Telegram after each scenario.

---

## 1. Scenario library

- **Location**
  - Default: `%REVITAGENT_HOME%\scenarios` (usually `%LOCALAPPDATA%\RevitAgent\scenarios`).
  - Custom: set it in **Settings** under "Scenario folder", or write `%REVITAGENT_HOME%\config\scenarios.json`:
    ```json
    {
      "library_dir": "C:\\path\\to\\Scenarios"
    }
    ```
- **Discovery:** every direct sub-folder that contains a `scenario.yaml` is listed, with its folder name, scenario name (`name`), required Revit version (`revit_version`), description (`description`) and last result (`last_verdict`, looked up in the Run Store). The file format is in [scenarios.md](scenarios.md).

---

## 2. Manual run from the Dashboard

The **Run scenarios** page of the RevitAgent Host offers:

- **Selection:** tick one or more scenarios (*Select all* / *Clear*).
- **Readiness check:** before running, the machine is checked (`MachineReadiness.Check`). On a serious problem (locked screen, Revit version not installed, Bridge add-in missing...) the app warns and asks for confirmation.
- **Sequential execution:** scenarios run one after another with `ScenarioRunner` and `launch: true` (a separate Revit session per scenario, for full isolation).
- **Single-run gate (concurrency gate):** only one run, or one MCP `scenario_run` call, at a time. A second request returns `BUSY`.
- **Live progress:** the current scenario and counter (for example `2/5`), the current tool or UI step and its duration, the elapsed time and a progress bar.
- **Stop:** *Cancel* stops safely after the current step.
- **Quick access:** *View in Run history* switches to the History page; *Open last run folder* opens the result folder in Windows Explorer.

---

## 3. Nightly schedule

Runs tests periodically when nobody is at the machine.

- **Config file:** `%REVITAGENT_HOME%\config\schedule.json`
  ```json
  {
    "enabled": true,
    "time": "22:00",
    "days": ["Mon", "Tue", "Wed", "Thu", "Fri"],
    "scenarios": [
      "C:\\path\\to\\Scenarios\\levels-inventory",
      "C:\\path\\to\\Scenarios\\room-schedule-ui"
    ],
    "revit_year": 2024,
    "last_fired": "2026-11-25T22:00:00+07:00"
  }
  ```
- **ScheduleService**
  - Runs in the background of the Host and checks every 30 seconds.
  - Fires once per day at or after `time` on the selected `days`.
  - Records `last_fired` as soon as it starts, so a Host restart on the same day does not run it twice.
- **Missed runs**
  - If the computer or the Host was off at the scheduled time, the run is **not made up later**, so it does not disturb daytime work.
  - Instead, the next time the Host starts it sends a Telegram message: `Missed the nightly run YYYY-MM-DD`.
- **Machine readiness gate**
  - Before a nightly run `MachineReadiness.Check` runs.
  - If any item has status `fail` (screen locked, RDP session disconnected, less than 5 GB free disk...):
    1. The whole nightly run is cancelled.
    2. A Telegram message is sent: `Skipped the nightly run: the machine is not ready`, with the failing items.
    3. A `SKIPPED` record is added to the Run Store's `index.jsonl` (no folder is created or deleted).

---

## 4. Telegram notification

After each scenario of a run (manual or scheduled) a `RunResult` notification is sent through the Telegram bot:

```
<scenario>: <verdict> (<pass_count> pass, <fail_count> fail, <duration>)
<run folder>
```

`pass_count` and `fail_count` count the scenario's checks. Up to **2 evidence screenshots** from the run's `evidence\` folder are attached when present.

---

## 5. Requirements for the unattended machine

For UI scenarios and Revit actions to succeed without anyone watching:

1. **The RevitAgent Host must keep running** (do not close the dashboard).
2. **The desktop must stay open and unlocked** (UI Automation cannot act on a locked screen or a disconnected session).
3. **Run the readiness script** before leaving the machine:
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\check-unattended.ps1 -Year 2024
   ```
   If every item shows `PASS` or `WARN`, the machine is ready for the nightly run.
