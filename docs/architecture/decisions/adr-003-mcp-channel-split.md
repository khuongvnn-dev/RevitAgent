# ADR-003: Two independent named-pipe channels for MCP and the Bridge

- **Status:** Accepted and implemented
- **Date:** 2026-11-01

## Context

The first design used a single named pipe (`\\.\pipe\revitagent-mcp`) for two peers:

- the AI client (through `RevitAgent.Shim`);
- the in-process add-in (`RevitAgent.Bridge` inside Revit).

That risks deadlock and bottlenecks:

1. **IPC contention.** A burst of MCP tool calls from the AI client (for example continuous screenshots) can occupy the pipe so the Bridge cannot send events or heartbeats.
2. **Different protocols.** The AI client speaks Anthropic MCP JSON-RPC (spec 2024-11-05, 13 tools at the time), while the Bridge uses Revit-specific DTOs (CTG graph, system diff, warnings).

## Decision

Run **two physically separate named-pipe servers** in parallel inside `RevitAgent.Host`, defined in `IpcConstants`:

1. **MCP channel** (`DefaultMcpPipeName = "revitagent-mcp"`): only `RevitAgent.Shim` to Host, MCP protocol.
2. **Bridge channel** (`DefaultBridgePipeName = "revitagent-bridge"`): only Host to `RevitAgent.Bridge` inside Revit, for internal Revit API commands.
3. **Bounded binary framing.** Both channels use `LengthPrefixedPipeFramer` with a 4-byte little-endian header and `MaxAllowedPayloadBytes = 32 * 1024 * 1024` to protect memory.

## Consequences

- (+) No contention between the AI client and the Revit add-in.
- (+) Clean schemas: the MCP protocol is not mixed with internal Revit DTOs.
- (+) The Host can serve Win32/GDI requests from the AI client even when Revit is busy or the Bridge thread is stuck.
- (+) Layered timeouts: Bridge 25 s, Host gateway 30 s.
- (-) The Host must manage the lifecycle of two pipe servers.

Note: ADR-004 later made the Bridge pipe per-process (`revitagent-bridge-<pid>`).
