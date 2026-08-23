# Security Policy

ODVGateway is a companion gateway for OpenDocViewer. It is intended to be
deployable as a standalone ASP.NET Core/IIS application and as an
OpenModulePlatform artifact.

Security issues should be reported privately before public disclosure.

## Supported Versions

**ODVGateway v0.1.40** is the current supported release and the recommended
deployment target.

Official releases are tagged `vX.Y.Z` and published with a
`ODVGateway-vX.Y.Z.zip` archive containing the framework-dependent publish
output. The version they report is `<Version>` in `Directory.Build.props`, which
is deliberately separate from the OMP artifact version in `omp-components.json`;
neither is forced to match the other.

v0.1.39 is the first official release. Earlier `0.1.x` builds existed only as OMP
artifacts and were never published as installable releases; deployments running
them should move to v0.1.39, which carries the same application code plus the
test-standard and dependency updates listed below.

| Version | Security support | Notes |
| --- | --- | --- |
| 0.1.40 | :white_check_mark: | Current recommended release and only supported baseline |
| 0.1.39 | :x: | Superseded by v0.1.40 release-process hardening |
| <= 0.1.38 | :x: | OMP-artifact-only builds with no published release; upgrade to v0.1.39 |
| < 0.1.0 | :x: | Not supported |

## Recent release context

The most recent releases are listed below for operational context. Only v0.1.40
is supported.

### ODVGateway v0.1.40
Hardening of the release process after an independent review; runtime behaviour
unchanged. The gate now runs the unit tests, the release refuses a detached HEAD,
a non-main branch, a missing upstream and a stale local main, and existing tags
are checked on origin as well as locally.

### ODVGateway v0.1.39
First official release. Changes since the 0.1.38 artifact:

- Adopted the shared test standard: xUnit tests emit TRX everywhere, and the release workflow runs them rather than assuming they were run locally.
- Updated the test infrastructure baseline (Microsoft.NET.Test.Sdk 18.8.1, xunit.runner.visualstudio 3.1.5).
- Corrected the documented test command and added a "Running tests" section; the local CI help text no longer describes this public repository as private.
- Added the release process itself: an official version in `Directory.Build.props`, a publishing release helper, and a tag-triggered workflow that attaches the deployable archive.

## Reporting a Vulnerability

Use GitHub private vulnerability reporting for this repository if that feature
is enabled. If private vulnerability reporting is not enabled, contact the
project maintainers through a private channel before disclosing details
publicly.

Please include, when possible:

- a clear description of the issue
- affected endpoints, settings, and versions
- reproduction steps or a proof of concept
- impact assessment
- any suggested remediation

## Security Model

ODVGateway intentionally does not require OpenModulePlatform authentication.
It must also be usable outside OpenModulePlatform, together with a host
application that prepares OpenDocViewer sessions.

Production deployments should therefore protect the gateway through explicit
handoff and source-access configuration:

- set `openDocViewerDistPath` explicitly and enable
  `requireExplicitOpenDocViewerDistPath`
- set the top-level ASP.NET Core `AllowedHosts` value to the gateway's public
  host names instead of leaving the development default (`localhost;127.0.0.1`)
- configure `webClientHandoff.allowedInitiatorUrls` so only trusted handoff
  pages can initialize sessions
- keep `webClientHandoff.allowMissingInitiatorHeaders` disabled unless another
  trusted boundary already protects the gateway
- keep `trustClientFilePath` disabled unless the gateway runs inside the same
  trust boundary as the supplied file paths
- when `trustClientFilePath` is enabled, configure `trustedSourceRoots` with
  the smallest practical set of absolute local or UNC roots
- keep `exposeOpenDocViewerDistPathInHealth` disabled in production unless a
  trusted monitor explicitly needs the literal filesystem path
- review startup warnings about an empty `webClientHandoff.allowedInitiatorUrls`
  list or enabled `webClientHandoff.allowMissingInitiatorHeaders`; both are
  intended for development or compatibility scenarios
- keep source proxy and source-pack byte limits aligned with the deployment's
  expected maximum source-file size
- verify that baseline response headers (`X-Frame-Options`,
  `X-Content-Type-Options`, `Referrer-Policy`, `X-Robots-Tag`, and
  `Content-Security-Policy`) are emitted by the host reverse proxy or by the
  gateway's Kestrel middleware
- verify that the Kestrel `Server` response header is disabled or removed by
  the host reverse proxy; the gateway disables it by default for standalone
  Kestrel deployments
- treat prepared sessions as process-local memory: restarts clear pending
  handoffs, and multi-instance deployments need sticky routing or a shared
  session-store implementation before requests can move between instances
- note that gateway error responses do not echo raw exception messages or
  stack traces; keep full diagnostic details in server-side logs

## Public Repository Scope

This repository should not contain customer-specific configuration, deployment
secrets, credentials, production URLs, private file-share paths, or environment
specific metadata mappings. Keep those values in private deployment
configuration or private operations repositories.

The `WebClient` wording in this repository describes a generic handoff contract
for host web clients. It is not intended to identify a specific customer,
vendor, or protected production system.

## Operational Guidance

- Review `appsettings.json` before deploying to any shared or production
  environment.
- Treat ignored `artifacts/`, `publish/`, and runtime folders as local build or
  deployment output, not source-controlled configuration.
- Do not expose direct source-file access without explicit trusted roots.
- Do not expose the gateway directly to untrusted networks unless the host
  system, reverse proxy, and gateway allowlists are configured together.
- Rotate any deployment secrets immediately if they are accidentally committed
  or included in published artifacts.
