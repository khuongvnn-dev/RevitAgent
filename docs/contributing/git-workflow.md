[Project root](../../README.md) > [Docs](../README.md) > [Contributing](index.md) > **Git workflow**

# Git workflow and commit format

Short-lived feature branches merged into `main`, with [Conventional Commits](https://www.conventionalcommits.org/).

## 1. Branches

- **`main`**: always builds with 0 errors and passes the test suite.
- Working branches:

| Prefix | Use |
|---|---|
| `feat/<name>` | New feature, e.g. `feat/macro-library`. |
| `fix/<name>` | Bug fix, e.g. `fix/pipe-timeout-reconnect`. |
| `docs/<topic>` | Documentation only. |
| `refactor/<area>` | Restructuring with no behaviour change. |

## 2. Commit messages

```text
<type>(<scope>): <short description>
```

| Type | Use | Example |
|---|---|---|
| `feat` | New capability | `feat(bridge): add schedule_create` |
| `fix` | Bug fix | `fix(bridge): roll back the transaction before closing a document` |
| `docs` | Documentation only | `docs(agent-guide): describe tool profiles` |
| `test` | Add or change tests | `test(watchdog): wait for notifications instead of a fixed delay` |
| `refactor` | Neither fix nor feature | `refactor(ui): extract the label lookup` |
| `perf` | Performance | `perf(ui): read a button's subtree in one round trip` |

Write the description in English, imperative mood.

## 3. Opening a pull request

```powershell
git checkout main
git pull origin main
git checkout -b feat/my-feature
# change, build, test, commit
git push -u origin feat/my-feature
```

Then open a pull request into `main` and fill in the [checklist](pr-checklist.md).
