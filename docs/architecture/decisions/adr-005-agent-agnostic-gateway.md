# ADR-005: The gateway is the single source of knowledge for every AI agent; installing the Bridge needs the machine owner's approval

- **Date:** 2026-11-26
- **Status:** Accepted (owner approved 2026-11-26). Decision 2 is approved but not yet implemented.
- **Extends:** ADR-004 (decision 5, safety in the gateway)

## Context

On 2026-11-26 a "new user" simulation was run twice with the same prompt ("create a schedule listing level names and offsets") on a real 397 MB MEP model. The first run used Claude Code, the second used a different agent client with a different model.

- Both runs gave the same result: a "Level Schedule" with Name and Elevation, 33 levels, and the source file unchanged (identical SHA-256 before and after the copy).
- Only the Claude Code run could read project memory (`~/.claude/projects/<repo>/memory/`). That is a Claude Code feature, loaded only when the agent runs inside the repository. Other clients, and open LLM clients, cannot read it, and end users do not run agents inside the repository anyway.
- What every MCP client does receive is the server `instructions` at connect time, the tool descriptions, and tool results and errors. One client saved `instructions` to a file and read it first. Some clients ignore `instructions`, but tool descriptions always reach the model.
- Both agents called `bridge_install` without asking the user, although the agent guide says to install only with the user's consent. The gateway did not enforce this. Installing the Bridge changes a shared machine: every Revit started afterwards loads it, and a Revit started by a person or another script shows an "Unsigned Add-In" dialog. Another agent uninstalled the Bridge mid-run, forcing a Revit restart.

Owner goal: no lock-in to any model or agent.

## Decisions

1. **The gateway is the single source of knowledge for every agent.** An agent may rely only on what the gateway provides over MCP.
   - Safety rules are enforced in gateway code, not only written in documents.
   - How to do a task lives in tool descriptions, tool results and errors, and verified macros.
   - Machine state (who is using Revit, whether the Bridge is installed) is held by the gateway and returned through tools.
   - Nothing depends on an agent's private memory (Claude memory, `GEMINI.md`, etc.). `AGENTS.md` in the repository is only for RevitAgent developers.
   - Tool descriptions state only what has been verified.
2. **`bridge_install` needs the machine owner's approval.** *Approved, not yet implemented:* `bridge_install` currently requires only Admin permission (checked in `BridgeInstallTools.cs`), with no owner-approval step. The decision is to use the same mechanism as forbidden dialogs: the owner approves in the dashboard or through Telegram. An agent has no way to approve itself. If nobody approves, nothing is installed and the tool returns `PERMISSION_DENIED` with a reason.
3. **The Bridge holds only generic API primitives; macros are data in Core.**
   - Add a Bridge primitive only when the Revit API offers something the gateway lacks, for example `schedule_create` that works for any category, columns and sorting.
   - A macro is a YAML file in Core that chains existing tools. Every step still goes through `ToolRegistry`, so policy, grants, timeouts and audit apply.
   - Adding or changing a macro needs no Bridge rebuild, reinstall or Revit restart.
   - Testing across the five supported Revit versions grows only with new primitives, not with the number of macros.
4. **Context budget at connect time: a `core` profile by default.**
   - The Shim accepts `--profile core|full` (or `REVITAGENT_PROFILE`).
   - `core` advertises only the core tools plus `macro_list` and `macro_run`. A unit test keeps the tool list plus instructions within the budget set in decision 5.
   - `full` advertises every tool, for authoring scenarios, recording and administration.
   - The profile affects only the advertised list: macros, scenarios and E2E runs can still call any tool by name.
   - Reason: the 63 tools in `full` cost about 22,000 tokens before the agent does anything, which is most of the context of a 32k open model. The `core` size is fixed whether or not the client defers tool loading.
5. **Additions after the Haiku and second-client comparison (2026-11-26 afternoon).**
   - `view_activate` and `model_get_warnings` joined the `core` profile (now 18 core tools). The budget rose to **at most 21,000 characters (about 5,250 tokens)**.
   - A dialog whose destructive button is **disabled** (for example Review Warnings with "Delete Checked...") is not treated as forbidden. The gateway considers the title, the message text and the buttons that can currently be clicked.
   - The gateway clicks a "safe dismiss" button (Close / Cancel / Later / Not now; never OK / Yes / No) only when **all four** conditions hold:
     1. The Revit instance was launched by the gateway.
     2. The dialog is blocking a gateway command.
     3. The title, **message text** and clickable buttons carry no forbidden or destructive meaning.
     4. A safe dismiss button exists.

     Each click captures a screenshot, writes a `safe_dismiss` audit entry and sends a Telegram notice.
   - `revit_launch` and `revit_wait_ready` descriptions now state the real behavior: the "Unsigned Add-In" dialog of a gateway-launched Revit is answered from the owner's allowlist; the agent never answers it.
   - `schedule_read` returns header rows and data rows separately; `limit` counts data rows only.
   - Messages between models, notes and internal status are written in English. Only user-facing text is Vietnamese or English.

## Consequences

- Any MCP client (Claude, Codex, Cursor, open LLMs, others) gets the same knowledge and is bound by the same rules, with no per-agent setup.
- Editing a tool description has the same impact as editing code: one wrong sentence can make a weaker model refuse or do the wrong thing.
- Once decision 2 is implemented, auto-approving agents (for example `mcp(.*)` auto mode) can no longer install the Bridge by themselves; a run that needs a missing Bridge stops and waits for the owner.
- "New user" simulations must run from a directory outside the repository, otherwise the agent can read repository memory and notes.

## Known issues recorded on 2026-11-26, checked against the code on 2026-11-26

| Issue | Status |
| --- | --- |
| The description of `schedule_list_schedulable_categories` said "Levels cannot be scheduled", while the tool itself returned `schedulable: true` on Revit 2024 (verified live) and both agents created a Levels schedule. | **Fixed.** The text no longer appears in `src`. |
| The `CodeSigning` registry value written by `bridge_install` had no effect on Revit 2024.3: Revit still asked "Unsigned Add-In" (2 of 2 runs), yet the value made `bridge_list_installs` report `trusted: true`. | **Open.** The value is still written (`BridgeTrustStore.cs`) and still reported as trusted (`MachineReadiness.cs`). The gateway works around it by pre-approving the Bridge in the built-in allowlist entry (`AddinAllowlist.cs`), which covers only gateway-launched Revit. |
| The Bridge is not code-signed. A certificate the machine trusts would let Revit load it without a prompt, including Revit started by a person or another script. | **Open.** No signing step exists in the repository. |
