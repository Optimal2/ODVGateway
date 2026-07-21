# ODVGateway Development Setup

## Prerequisites

- .NET 10 SDK (check `global.json` for exact version)
- A local clone of [OpenDocViewer](https://github.com/niclas-berg/OpenDocViewer) with a built `dist/` folder
- PowerShell 5.1 or later (for local CI scripts)

## Repository Layout

```
ODVGateway/
├── src/ODVGateway/       # ASP.NET Core gateway project
│   ├── Program.cs
│   ├── appsettings.json          # Production defaults (DO NOT edit for dev)
│   └── appsettings.Development.json  # Dev-only overlay
├── scripts/
│   ├── local-ci.ps1       # Local CI gate (build + smoke test)
│   ├── smoke-test.ps1     # Smoke test (build, start, verify /health + headers)
│   └── omp/
├── demo/                  # Synthetic demo source files (safe to view)
│   ├── sample.pdf
│   └── sample.tif
├── docs/
│   └── DEV-SETUP.md       # This file
└── omp-components.json     # Component/package metadata
```

## Quick Start

1. Clone ODVGateway and place it as a sibling of OpenDocViewer:
   ```
   ~/GitHub/
   ├── ODVGateway/
   └── OpenDocViewer/dist/    ← must exist
   ```

2. Build and run:
   ```powershell
   cd ODVGateway
   dotnet run --project src/ODVGateway
   ```

3. Open http://localhost:5000 (or check launchSettings.json for the dev port).

## Verify It Works

- `GET /health` should return `"ok"` with `openDocViewerDistAvailable: true`.
- `GET /` should render the OpenDocViewer viewer page.
- No external database, no OMP auth, no customer infrastructure needed.

## Dev-Only Settings

The file `src/ODVGateway/appsettings.Development.json` configures:
- OpenDocViewer dist path pointing to sibling `OpenDocViewer/dist`
- Session-less viewer fallback (view without WebClient session)
- Direct file access for demo sources via `{ContentRoot}/../demo`
- Relaxed handoff validation for local testing

These settings are ONLY active when `ASPNETCORE_ENVIRONMENT=Development`. They are never loaded in production.

## Demo Source Files

The `demo/` folder contains small synthetic files for testing the viewer:
- `sample.pdf` — minimal valid PDF
- `sample.tif` — minimal valid TIFF

These files are committed to the repo and contain no customer data.

## Local CI

Run the local CI gate before pushing:
```powershell
pwsh scripts/local-ci.ps1
```

This runs:
1. `dotnet build` (Release configuration)
2. `dotnet test tests/ODVGateway.Tests` (unit tests)
3. `scripts/smoke-test.ps1` (starts the gateway, checks /health, security headers, error responses)
4. `scripts/validate-component-versions.ps1` (`omp-components.json` manifest check)

GitHub Actions CI is `workflow_dispatch`-only by deliberate choice. ODVGateway is a
public repository, so Actions would be free, but the project gates on this local CI
instead of push-triggered runs. The local gate IS the CI.

## Release Gate

Before a release, run the local release gate as well:
```powershell
pwsh scripts/release.ps1
```

This runs the local CI checks plus `scripts/validate-component-versions.ps1` to
confirm that `omp-components.json` has been updated for any deployable changes.
The script does not publish anything; it only validates that the repository is
ready for a manually approved release.

## Production Safety

The following settings in the Development overlay MUST be disabled/absent in production:
- `allowOpenDocViewerFallbackWithoutSession` — allows viewing without a WebClient session
- `trustClientFilePath` — allows serving arbitrary local files
- `webClientHandoff.allowMissingInitiatorHeaders` — bypasses initiator URL validation
- `exposeOpenDocViewerDistPathInHealth` — reveals internal dist path

None of these appear in `appsettings.json` (the production config), so production deployments are safe by default.
