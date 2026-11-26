[Project root](../../README.md) > [Docs](../README.md) > [Contributing](index.md) > **Testing policy**

# Testing policy

Every pull request must keep the automated suite green.

```powershell
dotnet test tests/RevitAgent.Tests/RevitAgent.Tests.csproj
```

The suite uses xUnit. Tests that drive real UI or dialogs need an unlocked interactive desktop.

## 1. Principles

1. **Isolated and deterministic.** A unit test must not depend on a running Revit or on files on the developer's machine. Use temp folders, in-memory streams, fakes, and the test gateway in `tests/RevitAgent.Tests`.
2. **Naming:** `Method_Condition_ExpectedResult`, e.g. `Close_WhenSaveRequested_ReturnsValidationFailed`.
3. **Arrange, Act, Assert** in that order.
4. **A change in behaviour comes with a test** that fails without it.
5. Prefer waiting for a condition over fixed sleeps.

## 2. Pipes and framing

Test the wire formats (4-byte little-endian length prefix, UTF-8 JSON) against `MemoryStream` first. Use real named pipes only for tests of the pipe server core or a client talking to it:

```csharp
using var ms = new MemoryStream();
await LengthPrefixedPipeFramer.WriteMessageAsync(ms, payload, CancellationToken.None);
ms.Position = 0;
var read = await LengthPrefixedPipeFramer.ReadMessageAsync(ms, CancellationToken.None);
Assert.Equal(payload, read);
```

## 3. Live tests (opt-in)

Tests that need a real Revit carry `[Trait("Category", "RequiresRevit")]` and are skipped unless their environment variables are set (for the Bridge: `REVITAGENT_LIVE_REVIT_PID`, `REVITAGENT_LIVE_MODEL`, `REVITAGENT_LIVE_INSTALL_YEARS`, ...; see [../components/bridge-v2.md](../components/bridge-v2.md)).

End-to-end checks against a real Revit use `scripts/acceptance.ps1`. Its evidence goes to the run store (`%REVITAGENT_HOME%\runs`, default `%LOCALAPPDATA%\RevitAgent\runs`).

Never use a real project model in a committed test or example. Use a sample model supplied by Autodesk or a tiny file you created.
