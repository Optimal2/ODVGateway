# Security Policy

ODVGateway is a companion gateway for OpenDocViewer. It is intended to be
deployable as a standalone ASP.NET Core/IIS application and as an
OpenModulePlatform artifact.

Security issues should be reported privately before public disclosure.

## Supported Versions

**ODVGateway v0.1.44** is the current supported release and the recommended
deployment target. The OMP artifact version that ships the supported build is
`0.1.61` (`odvgateway-web` in `omp-components.json`); the two lines are
deliberately independent and are not forced to match. Read each version out of
its source file — `Directory.Build.props` for the application version and
`omp-components.json` for the OMP artifact.

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
| 0.1.44 | :white_check_mark: | Current recommended release and only supported baseline |
| 0.1.43 | :x: | Superseded by v0.1.44: server-side PDF signature validation (off by default), themed status pages, reproducible static assets |
| 0.1.42 | :x: | Superseded by v0.1.43: the default Content-Security-Policy no longer blocks the viewer's PDF print path or its PDF worker |
| 0.1.41 | :x: | Superseded by v0.1.42: the handoff redirect keeps the WebClient initiator visible, so `allowedInitiatorUrls` works with the default bundle handoff |
| 0.1.40 | :x: | Superseded by v0.1.41: real `/health` and error status codes, reproducible assemblies, refreshed test baseline |
| 0.1.39 | :x: | Superseded by v0.1.40 release-process hardening |
| <= 0.1.38 | :x: | OMP-artifact-only builds with no published release; upgrade to v0.1.39 |
| < 0.1.0 | :x: | Not supported |

## Recent release context

The most recent releases are listed below for operational context. Only v0.1.43
is supported.

### ODVGateway v0.1.44
Changes since v0.1.43:

- New `GET /signatures/{sessionKey}/{fileIndex}` validates PDF signatures server-side (integrity, trust with reason codes, signer, signing time, timestamps, whole-file coverage) in bounded memory and time, never persisting or caching document bytes. **Off by default** (`signatures.enabled: false` answers 404 and loads nothing). `revocationMode` selects `Online` (issuer CRLs through the gateway's own bounded transport: public addresses only, optional exact host allow-list, 1–30 s timeout, AIA and OS downloads disabled), `Offline` (CRL files only, no network) or `NoCheck` (verdict always `unknown`). Review-found gaps in integrity, trust and revocation handling were closed before release (detached CMS content handed to the verifier, strict ByteRange gap check, archived CRL selection, rejection of base CRLs advertising unsupported delta CRLs, asynchronous validation bounded by the file budget), and object references resolve exactly by (number, generation), so an appended higher-generation object cannot shadow a signed one (a missing exact entry falls back to the newest generation only with a `reference-generation-fallback` diagnostic).
- Known limitation while validation is enabled: a pre-open page-tree check rejects the measured stack-overflow shapes for files with classic cross-reference tables (cyclic `/Pages`→`/Kids` chains, self-referencing nodes, malformed `/Kids` entries) with a named HTTP 422 failure before PdfPig opens the file. Files whose cross-reference is a stream (xref streams / object streams, i.e. most modern producers) pass through to PdfPig without the pre-check, and catalog `/Dests` and `/Names` trees are not pre-checked either, so one crafted file of those shapes can still overflow the stack inside `PdfDocument.Open` (uncatchable, terminates the process). The default `signatures.enabled: false` never opens a file; process isolation is the remaining mitigation (campaign `odvgateway-signaturvalidering-i-separat-process`). Enable validation only for PDFs from a trusted archive until then.
- Status and error pages follow the shared OMP light/dark theme with hardened theme-cookie parsing; unknown-length proxy responses are validated; static web assets carry a pinned `Last-Modified` so the web artifact is reproducible. No known vulnerable packages (`dotnet list package --vulnerable --include-transitive`).

### ODVGateway v0.1.43
Changes since v0.1.42:

- The default `Content-Security-Policy` carries an explicit `frame-src 'self' blob:` and `connect-src 'self' blob:`. Without them the hidden blob-PDF print frame fell back to `default-src 'self'` and was blocked (printing hung at 100 %), and the viewer's external PDF worker degraded silently to the main thread. Deployments that override `contentSecurityPolicy` need the same two `blob:` sources, and any IIS-level CSP header must be removed or aligned, because the browser enforces the intersection of two CSP headers.
- NLog.Web.AspNetCore 6.2.1; .NET SDK pin 10.0.400; tests on xunit.v3 4.0.1 and Microsoft.NET.Test.Sdk 18.10.1; CI and CodeQL workflow actions on their latest majors. No known vulnerable packages (`dotnet list package --vulnerable --include-transitive`).

### ODVGateway v0.1.42
Changes since v0.1.41:

- The `?sessiondata=` to `?bundleUrl=` redirect carries `Referrer-Policy: strict-origin-when-cross-origin` instead of the blanket `no-referrer`, so the browser keeps sending the WebClient initiator on the follow-up request and the handoff guard accepts the gateway's own redirect. Until this fix, `webClientHandoff.allowedInitiatorUrls` could only be used together with `useBundleUrlHandoff: false`, and operators were tempted to loosen the guard instead.

### ODVGateway v0.1.41
Changes since v0.1.40:

- `/health` and the error pages return the HTTP status they describe (503 for an unavailable viewer, 4xx for rejected input) instead of 200 with an error body, so monitoring and load balancers see failures; the renderer's 503 paths are test-covered.
- The git commit is kept out of the assembly bytes, so a rebuild of unchanged source produces byte-identical binaries and an OMP artifact cannot change content under one version.
- NLog.Web.AspNetCore 6.2.0; test infrastructure on Microsoft.NET.Test.Sdk 18.10.0, xunit.runner.visualstudio 4.0.0, Microsoft.AspNetCore.Mvc.Testing 10.0.12.
- Canonical component-version validator with lockstep, payload and shared-script drift checks; hardened smoke test and local CI gate.

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

## Known limitations

PDF signature validation (`GET /signatures/...`, off by default) runs inside the gateway process.
A pre-open page-tree check rejects the measured stack-overflow shapes for files with classic
cross-reference tables (cyclic `/Kids` and `/Type` reference chains, self-referencing page-tree
nodes, malformed `/Kids` entries) with a named HTTP 422 failure before the PDF library opens the
file. Files whose cross-reference is a stream (xref streams / object streams, i.e. most modern
producers) pass through to PdfPig without the pre-check, as do shapes outside the walked path
that the library's eager open dereferences through the same unguarded recursion — catalog
`/Dests` and `/Names` name trees in particular — so one crafted file of those shapes could still
kill the gateway process while validation is enabled. Running validation in a separate process
is the complete fix and is still pending. Deployments that do not need server-side signature
verdicts should keep `signatures.enabled` at its default `false`.

## Reporting a Vulnerability

Report vulnerabilities privately before any public disclosure. The
maintainers can be reached by either of these channels:

- e-mail: **dev@optimal2.se** (the address under the LICENSE holder
  [Optimal2](https://github.com/Optimal2))
- GitHub private vulnerability reporting — the **Report a vulnerability**
  button on the repository's **Security** tab on GitHub. This repository
  has GitHub's private vulnerability reporting enabled.

Please report the issue before opening a public GitHub issue, a public
discussion, or a pull request that demonstrates the problem. If both
channels can reach the maintainers, prefer the one that lets you share
proof-of-concept material without exposing it publicly.

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
  `X-Content-Type-Options`, `X-Robots-Tag`, and `Content-Security-Policy`)
  are emitted by the host reverse proxy or by the gateway's Kestrel middleware
- verify that `Referrer-Policy` is emitted by the gateway ALONE: never add it in
  `web.config`, `applicationHost.config`, URL Rewrite outbound rules, or the
  reverse proxy. Browsers honour the last value, and any extra `no-referrer`
  overrides the `strict-origin-when-cross-origin` the gateway sets on the
  `?sessiondata=` to `?bundleUrl=` redirect, which breaks the initiator
  allowlist. Check the redirect itself, not `/health`: `curl -sD - -o NUL
  "https://<site>/ODVGateway/?sessiondata=<token>"` must show exactly one
  `Referrer-Policy` line with that value
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
