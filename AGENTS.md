# AGENTS.md

## Repository Workflow

ODVGateway is a companion application for OpenDocViewer. It is designed to run
as a standalone ASP.NET Core/IIS application and as an OpenModulePlatform
artifact.

Before broad changes:
- Inspect the actual repository structure first.
- Keep runtime behavior independent from OpenModulePlatform packages unless the
  task explicitly requires an OMP runtime dependency.
- Keep code, comments, scripts, and documentation in English.
- Keep site/customer configuration in private deployment files, not source code.
- Keep WebClient-specific integration behavior isolated to the gateway contract.
- Validate changes with `dotnet build` and packaging scripts when relevant.

## Security Notes

The gateway can intentionally trust WebClient-supplied file paths only when a
deployment explicitly enables `trustClientFilePath` and constrains access with
`trustedSourceRoots`. Treat that mode as a deployment decision and do not
silently add broader filesystem access.

Future path-resolution or database lookup logic should be implemented behind a
separate resolver so the initial direct-file-path mode remains easy to review.

## Local CI

This is a private repository with metered GitHub Actions. CI is `workflow_dispatch`-only — it runs only on manual trigger. **The actual pre-push gate is local execution.** Run `scripts/local-ci.ps1` before every push to verify build, unit tests, and smoke tests pass. This catches lockstep breaches and runtime regressions before they reach the shared main branch.

Unit tests live in `tests/ODVGateway.Tests` (xUnit, `net10.0`), outside `src/` so they are never packaged into the web-app artifact. They are pure in-memory Tier D tests (no filesystem, network, or live HTTP dependencies) and run as the second step of `scripts/local-ci.ps1`, right after `dotnet build` and before the smoke test.
