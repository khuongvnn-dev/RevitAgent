[Project root](../../README.md) > [Docs](../README.md) > [Contributing](index.md) > **Coding standards**

# Coding standards

## 1. Projects and language

| Project | Target | Notes |
|---|---|---|
| `RevitAgent.Core`, `Host`, `Shim`, `Tests`, `E2E` | net10.0-windows | Nullable reference types enabled. |
| `RevitAgent.Contracts` | netstandard2.0 | Shared with the Bridge; BCL only. |
| `RevitAgent.Bridge` | net48 (Revit 2022-2024), net8.0-windows (2025-2026) | One source tree, built per Revit year. |

- The Bridge references only the BCL and the Revit API. No third-party DLLs, because other add-ins in the same Revit
  load their own versions and conflicts break loading. Shared wire code is linked as source (see
  [../components/bridge-v2.md](../components/bridge-v2.md)).
- Isolate Revit API differences between years in the compat helpers (`REVIT2024_OR_GREATER`-style symbols), not at call sites.
- Naming: `PascalCase` for types and members, `camelCase` for parameters and locals, `_camelCase` for private fields.

## 2. WPF and MVVM (Host)

- Use `CommunityToolkit.Mvvm`: `[ObservableProperty]`, `[RelayCommand]`, `ObservableObject`.
- No business logic, pipe or Win32 code in code-behind. Bind through view models or attached behaviours.

## 3. Dependency injection

- Define services behind interfaces and register them in `App.xaml.cs` (generic host container).
- Prefer constructor injection; avoid the service-locator pattern except where WPF forces it (value converters, data templates).

## 4. Errors and Win32 interop

- P/Invoke declarations use `SetLastError = true`; check `Marshal.GetLastWin32Error()` when the call fails.
- Never swallow exceptions with an empty `catch`. Handle them, or surface them as a tool error with a gateway error code. Record gateway actions in the audit log (`AuditLog`).
- Payloads and secrets are never logged in full.

## 5. Safety rules code must keep

- The gateway never answers security, licence, recovery or delete/overwrite dialogs on its own. The only exceptions are
  the documented ones in [../agent-guide/revit-process-and-dialogs.md](../agent-guide/revit-process-and-dialogs.md).
- No tool runs arbitrary code; no tool saves over an original model.
- A new tool that changes a model must declare the right permission and use the sandbox policy.
