# ADR-006: An independent, English-first open-source project

- **Date:** 2026-11-26
- **Status:** Accepted (owner approved on 2026-11-26)
- **Supersedes:** ADR-004 decision 7 (scenario and evidence formats, compatibility mode)

## Context

RevitAgent will be published as an open-source project. It has to stand on its own: its formats, rules, documentation and history must be RevitAgent's own work, and nothing in it may depend on, quote or imitate private tooling. Two practical problems showed up:

- The scenario format, the built-in dialog rules, the UI-script conventions and a compatibility mode were inherited from earlier internal tooling.
- Developer verification output (screenshots, call logs) was written into `docs/evidence` inside the repository and committed. It held machine names, user names and data from real projects.

AI agents read English most reliably, whatever the model.

## Decisions

1. **Own formats only.**
   - The compatibility mode was removed.
   - Scenarios use RevitAgent's own `scenario.yaml`: steps, checks and an English expression language. See `docs/agent-guide/scenarios.md`.
   - Built-in dialog rules were re-authored as `builtin-dialog-rules.json` with RevitAgent's own schema.
   - UI scripts are documented as RevitAgent "UI scripts".
   - Environment variables use the `REVITAGENT_` prefix.
2. **English first.**
   - Code, comments, tool descriptions, server instructions, agent documentation, the README and ADRs are written in English.
   - Vietnamese is a supporting translation only (for example `README.vi.md`) and the dashboard's vi-VN localization.
   - Messages between agents, notes and status records are written in English.
   - Vietnamese strings stay in code only where they match Vietnamese UI text (dialog labels and buttons).
3. **No project or company data in the repository.**
   - Developer verification runs (the E2E tool, `scripts/acceptance.ps1`) write to the run store (`%REVITAGENT_HOME%\runs`, trigger `cli`), like every other run.
   - `docs/evidence/` is git-ignored.
   - Internal working documents (old reports, specs, the v1 archive) live outside the repository.
   - Examples and tests use neutral names.
4. **A fresh history.**
   - The pre-publication history was archived as a git bundle outside the repository.
   - The repository restarts from a single commit that contains only the cleaned tree.

## Consequences

- Scenarios written in the old format must be rewritten in `scenario.yaml`. The old format is not read any more.
- A user dialog-rules file (`%REVITAGENT_HOME%\config\dialog-rules.json`) must use the new schema.
- Evidence of live verification is kept in the run store. A short summary table (Revit version, date, result) may be added to the docs, without screenshots or project data.
