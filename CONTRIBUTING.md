# Contributing

Thanks for helping. The rules for Git, coding, tests and pull requests are in [docs/contributing/](docs/contributing/index.md). Read them first.

## Build and test

Requires Windows and the .NET 10 SDK (plus the .NET 8 SDK to build the Bridge for Revit 2025/2026).

```powershell
dotnet build RevitAgent.slnx -c Debug
$env:REVITAGENT_SKIP_UI_TESTS = '1'
dotnet test tests/RevitAgent.Tests -c Debug
```

`REVITAGENT_SKIP_UI_TESTS=1` skips the desktop UI tests and the live-Revit tests. Set it on machines without an unlocked interactive desktop (CI, remote sessions) or without Revit. See [testing policy](docs/contributing/testing-policy.md).

## Test data

Never use real client or company models in tests, issues, logs or screenshots. Use the Autodesk sample models that ship with Revit. Do not commit models.

## Pull requests

Keep a pull request focused, add a test for a behaviour change, and keep `README.md` and `README.vi.md` in sync.

By contributing you agree that your work is released under the [MIT license](LICENSE).
