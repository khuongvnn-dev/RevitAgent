[Project root](../../README.md) > [Docs](../README.md) > [Contributing](index.md) > **PR checklist**

# Pull request checklist and template

Before opening a pull request into `main`, tick every item.

## 1. Checklist

- [ ] **Builds:** `dotnet build RevitAgent.slnx -c Debug` succeeds (0 errors, no new warnings).
- [ ] **Tests pass:** `dotnet test tests/RevitAgent.Tests/RevitAgent.Tests.csproj` is green.
- [ ] **New tests** for new behaviour or a fixed bug.
- [ ] **Bridge changes:** `scripts\build-bridge.ps1` builds every Revit year you touched.
- [ ] **No secrets:** no encrypted settings, tokens, personal logs or API keys committed.
- [ ] **No stray files:** no `.rvt`, `.cfs`, `.tmp`, `bin/`, `obj/` in `git status`.
- [ ] **No private data:** no real project, model, company or user names or paths in code, docs, tests or examples.
- [ ] **Docs updated:** the matching page in `docs/` (and `README.md` if user-visible) reflects the change; relative links resolve.
- [ ] **Safety rules intact:** nothing lets an agent answer a security dialog, save over an original model or run arbitrary code.

## 2. Description template

```markdown
## Summary
<!-- 1-3 sentences: what this PR solves -->

## Changes
- Module A: ...
- Module B: ...

## Verification
- `dotnet build RevitAgent.slnx` -> succeeded
- `dotnet test` -> passed
- Live check (if any): Revit version, what was run

## Docs
- Updated: `docs/...`
```
