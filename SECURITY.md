# Security policy

## Reporting a vulnerability

Please report vulnerabilities privately. Use GitHub private vulnerability reporting for this repository (Security tab, "Report a vulnerability"), which opens a private Security Advisory. Do not open a public issue for a security problem.

Include the RevitAgent version or commit, the Revit year, the steps to reproduce, and the impact you see. Do not attach real client models; use an Autodesk sample model.

## What counts as a vulnerability

- A bypass of the dialog safety rules (an AI answering a security, unsigned add-in, licence, recovery or delete/overwrite dialog).
- A bypass of the sandbox (opening or saving over an original model outside the run folder).
- A bypass of the permission ceiling or of owner approval (for example `bridge_install`, the add-in allowlist, "Let AI act on my Revit").
- A way to make the gateway run arbitrary code.
- A leak of a token, a pipe name or other secret, or an audit log that can be forged or skipped.

Bugs that need the owner to approve a dangerous prompt on purpose are not vulnerabilities.

## Supported versions

Only the latest `main` is supported. There are no maintained older releases.
