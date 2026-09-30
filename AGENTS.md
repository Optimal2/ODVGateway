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

This is a **public** repository, but its GitHub Actions CI is `workflow_dispatch`-only by deliberate choice — it runs only on manual trigger, not on push (public repos get free Actions, so the trigger is a design choice, not a metering constraint). **The actual pre-push gate is local execution.** Run `scripts/local-ci.ps1` before every push to verify build, unit tests, and smoke tests pass. This catches lockstep breaches and runtime regressions before they reach the shared main branch. Because the repository is public, keep secrets, credentials, and customer-specific configuration out of it.

Local CI runs Check 15 (shared-script drift against the canonical OpenModulePlatform copies) with `-Strict`: when no platform checkout is found, the shared resolver `Resolve-PlatformCheckScript` records a validation error, so the gate fails rather than warning and passing. In a worktree that is not beside an OpenModulePlatform checkout, set `OMP_PLATFORM_ROOT` (or pass `-PlatformRepositoryRoot`) before running `scripts/local-ci.ps1`. `-AllowUnverifiedSharedScripts` accepts a missing checkout as NOT VERIFIED on purpose (it sets `OMP_ALLOW_MISSING_PLATFORM=1`) and must not be used to get a push through. `tests/scripts/Check15Strict.Tests.ps1` pins that behaviour and runs in the Pester step at the end of `scripts/local-ci.ps1`, so a regression in the wiring fails the gate; it resolves the platform checkout with the shared resolver `Resolve-PlatformCheckScript` (from `validate-component-versions.helpers.ps1`), in the same order (`-PlatformRepositoryRoot`, `OMP_PLATFORM_ROOT`, `OpenModulePlatformRoot`, sibling `OpenModulePlatform`; local CI forwards `-PlatformRepositoryRoot` as `OMP_PLATFORM_ROOT`).

The Pester step is the canonical OMP one: `scripts/omp/run-script-tests.ps1` plus `scripts/omp/pester-bootstrap.ps1`, copied verbatim from OpenModulePlatform and held identical by Check 15 (required because this repository has `*.Tests.ps1` suites under `tests/`). Never edit those two files here; change them in OpenModulePlatform and copy them back. The runner pins Pester 6.1.0 in the gitignored `.psmodules/` cache, runs every `tests/**/*.Tests.ps1` recursively, and fails when a container runs zero tests or when any `.ps1` under `tests/` is neither a suite (`*.Tests.ps1`) nor a helper (`*.TestHelpers.ps1`). Put shared suite code in a `*.TestHelpers.ps1` file and dot-source it from `BeforeAll`. The runner takes no suite parameters, so `Check15Strict.Tests.ps1` reports its platform-dependent cases as skipped only when `ODVGATEWAY_CHECK15_ALLOW_MISSING_PLATFORM=1`, which `scripts/local-ci.ps1` sets only for `-AllowUnverifiedSharedScripts`.

Unit tests live in `tests/ODVGateway.Tests` (xUnit, `net10.0`), outside `src/` so they are never packaged into the web-app artifact. They are almost all pure in-memory Tier D tests (no filesystem, network, or live HTTP dependencies) and run as the second step of `scripts/local-ci.ps1`, right after `dotnet build` and before the smoke test. The one exception is `GatewayHttpStatusTests`, which boots the real app on the in-memory TestServer via `WebApplicationFactory` to probe actual HTTP status codes; its healthy `/health` probe writes one throwaway dist folder under the temp path. There is still no network or live HTTP involved.

## Dependency pins - this repo is the one WITHOUT central package management

Every other .NET repository in the family (OpenModulePlatform and its private consumer
repositories) pins package versions centrally in a `Directory.Packages.props`. **ODVGateway
does not** - it has a `Directory.Build.props` (analysis and version properties only) and pins
inline in the two `.csproj` files. Adding a package here means adding a `Version=` attribute;
do not assume a central pin exists.

That difference has an observable consequence, so treat it as a known state rather than
rediscovering it: because a family-wide pin bump does not reach this repository automatically,
a shared pin has to be lifted here by hand, and this repository is where such a pin lags.
Re-measured 2026-09-27 against the OpenModulePlatform central pins:

| Package | Here | OpenModulePlatform | State |
| --- | --- | --- | --- |
| `Microsoft.NET.Test.Sdk` | 18.10.1 | 18.10.1 | in line |
| `xunit.v3.mtp-off` | 4.0.1 | `xunit` 2.9.3 (v2) | ahead, deliberately (see below) |
| `xunit.runner.visualstudio` | 4.0.0 | 4.0.0 | in line |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.12 | 10.0.12 | in line |
| `NLog.Web.AspNetCore` | 6.2.1 | 6.2.1 | in line |

Earlier rows were brought in line by family-wide campaigns rather than by this repository
catching up on its own; `NLog.Web.AspNetCore` lagged at 6.1.4 until 2026-09-08.

The xunit row is the one deliberate divergence. ODVGateway ships standalone to production
and its third-party dependencies are kept at the latest stable versions before each official
release, so on 2026-09-27 its tests moved from xunit v2 (2.9.3, the last v2 release) to
xunit.v3. The test project is therefore an `Exe`, and it references the `xunit.v3.mtp-off`
flavour on purpose: the default `xunit.v3` package enables Microsoft.Testing.Platform, which
the .NET 10 SDK refuses to run under the VSTest-mode `dotnet test` that `scripts/local-ci.ps1`
and the CI workflows use (`--logger trx`). `mtp-off` keeps VSTest through
`xunit.runner.visualstudio`, so the TRX output and the commands stay unchanged. The rest of the
family keeps xunit v2 until its own deliberate migration, which is possible because this
repository does not link the shared UI-test sources (below).

When you bump a pin here, bump it to the version the rest of the family already carries rather
than to whatever is newest, unless the task is explicitly a family-wide upgrade.

This repository also stays outside the shared Playwright UI-test tier: the other seven link
`$(OpenModulePlatformRoot)\tests\shared\Ui\*.cs` into a `*.UiTests` project, ODVGateway does
not. Its coverage is the Tier D unit tests plus `scripts/smoke-test.ps1`.

## Releases

Official releases are tagged `vX.Y.Z` and publish a GitHub release with
`ODVGateway-vX.Y.Z.zip`, the framework-dependent publish output.

**The single approval gate is a maintainer running `scripts/release.ps1
-ReleaseType <patch|minor|major> -Publish`.** That validates the tree, bumps
`<Version>` in `Directory.Build.props`, commits, tags, and pushes. Without
`-Publish` the release is prepared locally and nothing leaves the machine;
without `-ReleaseType` the script is the same local gate it always was. Never
hand-edit `<Version>`, and never push a release tag directly.

`.github/workflows/release.yml` triggers on the tag and does the publishing. It
is the one workflow in this repository that runs on push rather than on manual
dispatch - the deliberate exception to the rule above, because building the
published archive has to happen on a clean machine from the tagged commit rather
than from whatever a developer had on disk. It refuses to publish if the tag does
not match `<Version>`, or if `release-notes/vX.Y.Z.md` is missing; that file is
the release body.

Write the release notes and update `CHANGELOG.md` and `SECURITY.md` **before**
running the helper, then commit and push those. The helper releases a commit; it
does not create one from your working tree.

Two version lines exist and are independent by design:

| Where | What it is |
| --- | --- |
| `Directory.Build.props` `<Version>` | the official application version, in the binaries |
| `omp-components.json` | the OMP artifact version |

An artifact-only test build may bump the artifact without an official release,
and an official release may happen without an artifact rebuild. Never force them
to match. After an official release that should reach OMP, bump the artifact
version **from the post-release commit** so the delivered artifact carries the
released build.
