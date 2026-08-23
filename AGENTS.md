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

Unit tests live in `tests/ODVGateway.Tests` (xUnit, `net10.0`), outside `src/` so they are never packaged into the web-app artifact. They are pure in-memory Tier D tests (no filesystem, network, or live HTTP dependencies) and run as the second step of `scripts/local-ci.ps1`, right after `dotnet build` and before the smoke test.

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
