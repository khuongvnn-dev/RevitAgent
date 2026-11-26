# ADR-001: Out-of-process hybrid automation

- **Status:** Accepted
- **Date:** 2026-10-31

## Context

Typical Revit automation ships as a single add-in that runs entirely inside `Revit.exe` (an in-process monolith). That approach has serious drawbacks:

1. **One failure takes everything down.** If Revit shows a modal dialog (`#32770`) or crashes natively, the whole automation engine goes with it.
2. **Technology lock-in.** Revit 2024 runs on .NET Framework 4.8, which blocks modern libraries: AI SDKs, gRPC, ASP.NET Core, an MCP server.
3. **No view from outside.** While the process is frozen, nothing can take a screenshot or send Win32 messages to dismiss a dialog.

## Decision

Adopt an **out-of-process hybrid** model:

- A separate **Host** process on **.NET 10** owns the control UI, the MCP engine, the Win32 logic and logging.
- Inside Revit sits only a **thin in-process Bridge** (.NET Framework 4.8) that executes Revit API calls on request.
- Host and Bridge talk over a Windows named pipe with a binary protocol using 4-byte little-endian length-prefixed framing.

## Consequences

- (+) The Host survives a Revit crash: it can collect logs, capture the screen and restart Revit.
- (+) The Host can use current .NET features (async/await, CommunityToolkit MVVM).
- (+) No assembly-loading conflicts with third-party Revit add-ins.
- (-) The IPC protocol between processes must be maintained.
- (-) UAC elevation mismatches between the two processes must be managed.
