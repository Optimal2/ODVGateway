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

Unit tests live in `tests/ODVGateway.Tests` (xUnit, `net10.0`), outside `src/` so they are never packaged into the web-app artifact. They are almost all pure in-memory Tier D tests (no filesystem, network, or live HTTP dependencies) and run as the second step of `scripts/local-ci.ps1`, right after `dotnet build` and before the smoke test. The one exception is `GatewayHttpStatusTests`, which boots the real app on the in-memory TestServer via `WebApplicationFactory` to probe actual HTTP status codes; its healthy `/health` probe writes one throwaway dist folder under the temp path. There is still no network or live HTTP involved.

## Dependency pins - this repo is the one WITHOUT central package management

Every other .NET repository in the family (OpenModulePlatform, IbsPackager, LogSearch,
EArkivChecker, Dokumentbibliotek, VajSkrivare, iKrock2) pins package versions centrally in a
`Directory.Packages.props`. **ODVGateway does not** - it has a `Directory.Build.props` (analysis
and version properties only) and pins inline in the two `.csproj` files. Adding a package here
means adding a `Version=` attribute; do not assume a central pin exists.

That difference has an observable consequence, so treat it as a known state rather than
rediscovering it: because a family-wide pin bump does not reach this repository automatically,
a shared pin has to be lifted here by hand, and this repository is where such a pin lags.
Re-measured 2026-09-02 across all eight .NET repos:

| Package | Here | Rest of the family | State |
| --- | --- | --- | --- |
| `Microsoft.NET.Test.Sdk` | 18.9.0 | 18.9.0 (7 of 7) | in line |
| `xunit` | 2.9.3 | 2.9.3 (7 of 7) | in line |
| `xunit.runner.visualstudio` | 4.0.0 | 4.0.0 (7 of 7) | in line |
| `NLog.Web.AspNetCore` | 6.1.4 | 6.2.0 (4 of 4 that use it) | **behind** |

Three of those four rows were brought in line by family-wide campaigns, not by this repository
catching up on its own: `Microsoft.NET.Test.Sdk` reached 18.9.0 on 2026-09-01 and the xunit
runner reached 4.0.0 on 2026-08-31. `NLog.Web.AspNetCore` is the one pin still behind, in
`src/ODVGateway/ODVGateway.csproj`. When you bump a pin here, bump it to the version the rest
of the family already carries rather than to whatever is newest, unless the task is explicitly
a family-wide upgrade.

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
