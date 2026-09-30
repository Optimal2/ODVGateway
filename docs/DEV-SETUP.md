# ODVGateway Development Setup

This guide describes how to clone, build, and run ODVGateway locally for
development. Production deployment is documented in
[README.md](../README.md) under "Standalone IIS Deployment" and
"OpenModulePlatform Packaging".

## Prerequisites

- .NET 10 SDK (see `global.json`)
- A local clone of [OpenDocViewer](https://github.com/niclas-berg/OpenDocViewer)
  with a built `dist/` folder
- PowerShell 5.1 or later (for the local CI scripts)

## Repository Layout

```text
ODVGateway/
├── src/ODVGateway/                       # ASP.NET Core gateway project
│   ├── Program.cs                        # endpoints, middleware, source pack stream
│   ├── Services/                         # session store, dist resolver, handoff guard, bundle factory
│   ├── Options/ODVGatewayOptions.cs      # configuration records
│   ├── Models/                           # WebClient + ODV bundle models
│   ├── Packaging/appsettings.json        # OMP artifact configuration baseline
│   ├── Properties/launchSettings.json    # Kestrel dev launch profile
│   ├── appsettings.json                  # production defaults (DO NOT edit for dev)
│   ├── appsettings.Development.json      # dev-only overlay
│   └── web.config                        # published IIS configuration template
├── tests/ODVGateway.Tests/               # in-memory xUnit unit tests
├── scripts/
│   ├── local-ci.ps1                      # local CI gate (build + tests + smoke)
│   ├── smoke-test.ps1                    # smoke test (build, start, verify /health + headers)
│   ├── release.ps1                       # release approval gate
│   └── omp/                              # OMP packaging and version-lockstep tooling
├── demo/                                 # synthetic demo source files (safe to view)
│   ├── sample.pdf
│   └── sample.tif
├── docs/
│   └── DEV-SETUP.md                      # this file
├── release-notes/                        # per-version release notes
├── omp-components.json                   # OMP component/package metadata
├── odvgateway.module-definition.json     # OMP module definition
├── Directory.Build.props                 # analysis settings, <Version>
└── AGENTS.md, CONTRIBUTING.md,           # project documentation
    SECURITY.md, CHANGELOG.md, README.md,
    LICENSE
```

## Quick Start

1. Clone ODVGateway and place it as a sibling of OpenDocViewer so the
   default sibling probe finds its `dist/` folder:

   ```text
   ~/GitHub/
   ├── ODVGateway/
   └── OpenDocViewer/dist/    ← must exist and contain index.html
   ```

2. Configure the Git hooks (one-time):

   ```powershell
   pwsh scripts\setup-hooks.ps1
   ```

3. Build and run:

   ```powershell
   dotnet run --project src\ODVGateway\ODVGateway.csproj
   ```

4. Open `http://localhost:5000` (or whatever `launchSettings.json`
   names for the dev profile).

## Verify It Works

- `GET /health` should return `200 OK` with `status: "ok"` and
  `openDocViewerDistAvailable: true`. The endpoint returns `503
  Service Unavailable` if the dist folder is missing — that is the
  expected status-code signal for a degraded instance.
- `GET /` should render the OpenDocViewer viewer page (when the dev
  overlay enables session-less viewer fallback).
- No external database, no OMP auth, no customer infrastructure
  needed.

## Dev-Only Settings

The file `src/ODVGateway/appsettings.Development.json` configures:

- OpenDocViewer dist path pointing to sibling `OpenDocViewer/dist`.
- Session-less viewer fallback (`allowOpenDocViewerFallbackWithoutSession`)
  so the dev page works without a WebClient handoff.
- Direct file access for demo sources via `{ContentRoot}/../demo`.
- Relaxed handoff validation for local testing
  (`webClientHandoff.allowMissingInitiatorHeaders`).

These settings are ONLY active when
`ASPNETCORE_ENVIRONMENT=Development`. They are never loaded in
production.

## Demo Source Files

The `demo/` folder contains small synthetic files for testing the
viewer:

- `sample.pdf` — minimal valid PDF.
- `sample.tif` — minimal valid TIFF.

These files are committed to the repo and contain no customer data.
They are intended for local validation only and must not be referenced
from production configuration.

## Local CI

Run the local CI gate before pushing:

```powershell
pwsh scripts\local-ci.ps1
```

This runs, in order:

1. `dotnet build` (Release configuration).
2. `dotnet test tests\ODVGateway.Tests` (in-memory xUnit unit tests).
3. `scripts/smoke-test.ps1` (starts the gateway, checks `/health`,
   security headers, error responses).
4. `scripts/omp/validate-component-versions.ps1` (lockstep, payload and
   shared-script drift checks against `omp-components.json`).
5. `scripts/omp/run-script-tests.ps1` (the canonical OMP Pester step:
   Pester 6.1.0 from the gitignored `.psmodules/` cache, every
   `tests/**/*.Tests.ps1` suite).

Step 4 includes Check 15, which compares the shared scripts in
`scripts/omp` with the canonical copies in an OpenModulePlatform checkout.
Local CI runs it with `-Strict`, so a run that cannot find the platform
checkout fails instead of printing `NOT VERIFIED` and passing. The
checkout is found through `-PlatformRepositoryRoot`, then the
`OMP_PLATFORM_ROOT` environment variable, then `OpenModulePlatformRoot`,
then a sibling directory named `OpenModulePlatform`. A git worktree that
does not sit beside the platform checkout must set `OMP_PLATFORM_ROOT`:

```powershell
$env:OMP_PLATFORM_ROOT = 'C:\src\OpenModulePlatform'
pwsh scripts\local-ci.ps1
```

`-AllowUnverifiedSharedScripts` deliberately accepts an unresolvable
platform checkout as NOT VERIFIED (it sets `OMP_ALLOW_MISSING_PLATFORM=1`)
for a run where no OpenModulePlatform checkout is available on purpose;
detected drift still fails. Do not use it to get a push through.
`tests\scripts\Check15Strict.Tests.ps1` pins this behaviour and runs in
step 5.

GitHub Actions CI is `workflow_dispatch`-only by deliberate choice.
ODVGateway is a public repository, so Actions would be free, but the
project gates on this local CI instead of push-triggered runs. The
local gate IS the CI.

## Release Gate

Before a release, run the local release gate as well:

```powershell
pwsh scripts\release.ps1
```

This runs the local CI checks plus
`scripts/omp/validate-component-versions.ps1` to confirm that
`omp-components.json` has been updated for any deployable changes.
Without `-ReleaseType` the script publishes nothing; it only validates
that the repository is ready. With `-ReleaseType patch|minor|major` it
also bumps the version, commits and tags, and with `-Publish` it pushes
— that switch is the approval gate for an official release. See the
Release Process section of the [README](../README.md).

## Production Safety

The following settings in the Development overlay MUST be disabled or
absent in production:

- `allowOpenDocViewerFallbackWithoutSession` — allows viewing without a
  WebClient session.
- `trustClientFilePath` — allows serving arbitrary local files; must be
  paired with `trustedSourceRoots`.
- `webClientHandoff.allowMissingInitiatorHeaders` — bypasses initiator
  URL validation.
- `exposeOpenDocViewerDistPathInHealth` — reveals internal dist path in
  `/health` output.

None of these appear in `appsettings.json` (the production config), so
production deployments are safe by default.