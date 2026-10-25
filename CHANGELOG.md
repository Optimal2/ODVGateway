# Changelog

All notable changes to ODVGateway are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
for its `0.1.x` release line.

## [Unreleased]

## [0.1.44] - 2026-10-25

### Added

- `GET /signatures/{sessionKey}/{fileIndex}` validates the signatures of a PDF in a prepared
  session on the server and reports, per signature, integrity, trust with a reason code, signer
  and issuer, signing time and its source, document timestamps and whole-file coverage. It reuses
  the `/source` session lookup, index check and byte-source resolution and reads the bytes
  through the `/source-pack` reader (a PDF above an explicitly set `signatures.maxFileBytes` below
  `maxSourcePackFrameBytes` answers HTTP 413; a PDF above `maxSourcePackFrameBytes` answers
  HTTP 404 with the source-pack size message), answers `Cache-Control: no-store`, never
  persists or caches document bytes, and is disabled by default
  (`signatures.enabled: false` answers 404 and loads nothing). `signatures.revocationMode` selects
  `Online` (issuer CRLs through the gateway's own bounded transport: direct connections to public
  addresses only, no HTTP proxy, optional exact host allow-list, 1–30 s timeout), `Offline`
  (CRL files from `signatures.crlDirectory` only) or `NoCheck`. Review-found gaps were closed before release:
  detached CMS content is handed to the verifier, ByteRange gaps are checked strictly, archived
  CRL selection is fixed, base CRLs advertising unsupported delta CRLs are rejected, and
  asynchronous validation is bounded by the file budget. See `docs/PDF-SIGNATURE-VALIDATION.md`.
- Server-rendered status and error pages follow the shared OMP light/dark theme
  (`OMP_THEME_PREFERENCE` cookie, theme contract version 1) without adding script or changing
  the default Content-Security-Policy; malformed theme cookies fall back to the system palette.
- `GET /signatures/...` validates each PDF in an isolated worker process by default
  (`signatures.isolateProcess: true`): the same assembly runs as
  `ODVGateway --signature-worker`, is fed the document bytes over stdin (at most 256 MiB per
  file, never a temp file), and answers one JSON envelope over stdout. Each run is supervised
  with a wall-clock timeout (`signatures.workerTimeoutSeconds`, default 30 s) and a working-set cap
  (`signatures.workerMaxMemoryBytes`, default 512 MiB) within the
  `signatures.maxConcurrentValidations` limit. A worker that crashes — including an uncatchable
  stack overflow inside the PDF library's eager open — times out, overruns memory or answers
  unparseably costs one request (a named HTTP 503: `validation-worker-crashed`,
  `validation-worker-timeout`, `validation-worker-memory`, no stack trace, no `Retry-After`)
  instead of the gateway process, which keeps serving. A small warm pool (at most two idle
  workers, retired after 25 requests or any failure, and not reused after five idle minutes) keeps the per-request
  cost near the in-process one; see `docs/PDF-SIGNATURE-VALIDATION.md` for the measured numbers.
  `isolateProcess: false` keeps the in-process path, documented as "only for trusted archives".
- Startup logs a warning when `useBundleUrlHandoff` is `false`: without the `?bundleUrl=`
  redirect, OpenDocViewer inside the WebClient page prefers the host page's own payload and can
  bypass the prepared gateway session and its source-pack transport, fetching every source
  directly from WebClient. When `webClientHandoff.allowedInitiatorUrls` is set, the warning adds
  that the deployment is a WebClient handoff deployment and is affected. The README states the
  caveat next to the setting.
- The release workflow attaches `ODVGateway-sbom-vX.Y.Z.cdx.json` to each GitHub release,
  starting with this one: a CycloneDX JSON SBOM generated with CycloneDX .NET 6.3.1 from the
  published project and its project references, test and development dependencies excluded.
  `SECURITY.md` has a new EU Cyber Resilience Act section: coordinated vulnerability disclosure
  by e-mail or GitHub private vulnerability reporting, the SBOM, and the CRA reporting
  obligations that apply from 11 September 2026.

### Fixed

- `GET /signatures/...` resolves indirect references exactly by (number, generation), as the
  PDF specification and PdfPig do, for the catalog, fields, `/V`, AcroForm, page objects,
  `/Perms/DocMDP`, de-duplication and the physical `/Contents` lookup. An unreleased change had
  remapped every reference to the newest generation of its number, which let an appended
  `N 1 obj` shadow a signed `N 0 obj`: a signature could disappear from the report or have its
  byte range and signer evidence replaced. An incremental update that rewrites an object under
  a higher generation (`5 1 obj` while references keep `5 0 R`) is outside the specification;
  the generation-0 original is read and a signature covering the earlier revision stays
  `modified-after-signing`. Only a reference whose exact entry is missing from the
  cross-reference data falls back to the newest generation, and that is reported in the new
  document-level `diagnostics` array as `reference-generation-fallback` and logged.

- `GET /signatures/...` no longer fails the whole file on, or silently skips, a reference it
  cannot follow inside the field, widget and annotation walk (field and annotation arrays and
  their elements, field `/Kids`, `/Parent` and `/V`). A dangling reference, a cyclic reference
  chain, a direct value of the wrong type and a chain longer than the depth limit are skipped so
  the remaining signatures are still validated, and the `diagnostics` array names each case once
  (`dangling-reference-skipped`, `reference-cycle-skipped`, `unexpected-object-type-skipped`,
  `reference-depth-exceeded`). Page-tree failures stay fatal (HTTP 422), and a cyclic
  `/AcroForm` reference now fails the file (HTTP 422) instead of reading as absent, which had
  hidden every signature.

- `GET /signatures/...` proves the page tree acyclic before PdfPig opens the file. PdfPig builds
  the page tree eagerly inside `PdfDocument.Open` and resolves bare indirect references through
  unguarded recursion, so one cyclic `/Kids` or `/Type` chain killed the whole gateway process with
  an uncatchable stack overflow whenever signature validation was enabled. A new pre-check walks
  the trailer's `/Root` → `/Pages` → `/Kids` path with the gateway's own raw reader (exact
  (number, generation) resolution with fallback diagnostics, visited set, depth bound 32, at most
  100,000 page-tree nodes); a proven cycle, depth overrun or malformed `/Kids` entry now fails
  closed with a named `page-tree-cyclic` / `page-tree-too-deep` / `page-tree-malformed` failure
  (HTTP 422) instead (the malformed code was split out in the entry below). The code does not
  reach the response: the body is the same generic 422 as for an unparseable PDF. With the
  default `signatures.isolateProcess: true` the gateway log records only that the PDF could not
  be parsed; with `isolateProcess: false` the logged exception detail names the code.
  Shapes the pre-check cannot read are left to PdfPig as before; isolating validation in a
  separate process followed in this same release (the `signatures.isolateProcess` entry
  above; see `SECURITY.md`).

- `GET /signatures/...` fails closed with `page-tree-cyclic` (HTTP 422, the same generic body as
  an unparseable PDF) on any page-tree cycle, not just a self-edge: the pre-check now walks
  depth-first and treats any revisit of a node on the current traversal path as a true cycle,
  while a node reached again after its subtree finished (a DAG, legal in some producers) is
  counted once. A `/Kids` entry that is not an array of indirect references gets its own code
  (`page-tree-malformed`, still HTTP 422), and the pre-check runs inside the single
  document-open path so no caller can reach `PdfDocument.Open` without it. The known-limitation
  notes state the pre-check's actual coverage: classic cross-reference tables only. Xref
  streams, object streams, hybrid-reference files, encrypted files and catalog `/Dests`/`/Names`
  trees still reach the library unchecked; with the default `signatures.isolateProcess: true`
  (added in this same release) such a file costs only its own request.

- `GET /signatures/...` maps every one-shot worker write/close failure to the named
  `validation-worker-crashed` failure (HTTP 503): closing stdin of a worker that died before
  reading it no longer escapes the crash mapping as a raw 500. The pooled failure log line now
  carries the real worker exit code and peak working set, read before the process is destroyed
  instead of -1/0 after it.
- `GET /signatures/...` refuses two worker misconfigurations at startup with a named reason
  instead of failing per request: a test-only `signatures.workerTestMode` without the
  `ODVGATEWAY_SIGNATURE_WORKER_TEST=1` environment gate (which used to fail every validation),
  and a `signatures.workerMaxMemoryBytes` below three times the effective file budget (which
  could kill a healthy large file as `validation-worker-memory`). The check runs even while
  `signatures.enabled` is `false`, and the budget is the smaller of `signatures.maxFileBytes`
  (when set) and `maxSourcePackFrameBytes`: with the default 512 MiB cap, a deployment that raised
  `maxSourcePackFrameBytes` above about 170 MiB must set `signatures.maxFileBytes` or raise
  `signatures.workerMaxMemoryBytes` before it starts.

### Changed

- A proxied `/source` response without `Content-Length` is read and checked against
  `maxSourceProxyBytes` in full before a success status is committed, instead of failing
  mid-stream: an oversized body returns HTTP 502. Bodies above 64 KiB are buffered in
  `ASPNETCORE_TEMP` (or the process temp directory), which must be writable, and removed when
  the request finishes; the first response byte waits for the check. A transfer that fails
  after the response has started is logged and the client connection aborted, instead of
  fetching the source from WebClient again.
- The shared OMP deterministic-build files are synced from OpenModulePlatform: the build maps
  the checkout directory out of the compiled output, so unchanged source built from another
  checkout path produces the same OMP artifact.
- NLog.Web.AspNetCore 6.2.2. Signature validation adds the runtime packages PdfPig 0.1.16 and
  System.Security.Cryptography.Pkcs 10.0.13; 10.0.13 fixes CVE-2026-84008 (high, CPU
  exhaustion in `SignedCms.CheckSignature`, which signature validation calls) and
  CVE-2026-70162 (moderate, `EnvelopedCms` on Windows, not used by the gateway), both present
  in the 10.0.12 that the interim OMP artifacts 0.1.57–0.1.72 carried. No known vulnerable
  packages (`dotnet list package --vulnerable --include-transitive`).
- Test infrastructure on Microsoft.NET.Test.Sdk 18.11.0, xunit.v3 4.0.2,
  xunit.runner.visualstudio 4.0.1 and Microsoft.AspNetCore.Mvc.Testing 10.0.13; 321 unit tests.
- Local CI runs the canonical OMP Pester step and the strict shared-script check (Check 15)
  through the shared platform resolver; the canonical component-version validator helpers are
  synced from OpenModulePlatform; smoke-test stack-frame detection is fixed.

## [0.1.43] - 2026-09-27

### Fixed

- The default `Content-Security-Policy` blocked the viewer's PDF print path:
  the missing `frame-src` made the hidden blob-PDF print iframe fall back to
  `default-src 'self'` and get blocked, after which the cross-origin probe of
  `contentWindow.print` threw an uncaught `SecurityError` in the viewer — the
  user saw "Förbereder utskrift" reach 100 % and then nothing. The default now
  carries an explicit `frame-src 'self' blob:` (the fix for the hang) and
  `connect-src 'self' blob:` — the latter not for the generated PDF, which is
  never fetched, but for the viewer's external PDF worker, which fetches the
  page images' blob: URLs and obeys the CSP stamped on its own script
  response; without it the worker path degrades silently to the main thread.
  Reproduced and fix-verified against a simulated WebClient handoff 2026-09-15
  where the viewer's print pipeline hung at "Förbereder utskrift" 100 % and
  the worker degraded silently to the main thread. Deployments that override
  `contentSecurityPolicy` need the same two `blob:` sources, and any IIS-level
  CSP header must be removed or aligned — with two CSP headers the browser
  enforces the intersection, so the stricter copy wins.

### Changed

- NLog.Web.AspNetCore 6.2.1; .NET SDK pin 10.0.400.
- Unit tests migrated from xunit v2 to xunit.v3 4.0.1 (self-hosting test
  executable); Microsoft.NET.Test.Sdk 18.10.1.
- CI and CodeQL workflow actions lifted to their latest majors; the local CI
  gate runs the shared-script check (Check 15) strictly and honours
  `OMP_PLATFORM_ROOT`.
- Documentation refers to private companion repositories generically; README
  restores a 'Known limitations' section.

## [0.1.42] - 2026-09-10

### Fixed

- The `302` from `?sessiondata=` to `?bundleUrl=` carried the blanket
  `Referrer-Policy: no-referrer`, so the browser sent the follow-up viewer request
  without `Referer` and the handoff guard rejected the gateway's own redirect
  with `403` whenever `allowedInitiatorUrls` was configured. The redirect now
  carries `strict-origin-when-cross-origin`; covered by an HTTP-level test that
  also proves the follow-up without `Referer` is still rejected.
- `web.config` no longer adds its own `Referrer-Policy: no-referrer`: IIS appends
  `customHeaders` after the application's headers and browsers honour the last
  value, so the IIS copy silently undid the fix above under IIS. The
  application middleware is the header's only owner.

## [0.1.41] - 2026-09-10

A maintenance release with two runtime fixes and a refreshed baseline.

### Fixed

- `/health` and the error pages return real HTTP status codes (503 for an
  unavailable viewer, 4xx for rejected input) instead of 200 with an error body.
- The renderer's own 503 paths, which shipped untested, are covered by tests.
- The git commit is kept out of the assembly bytes, so builds of unchanged
  source are byte-identical and an OMP artifact keeps its content hash.

### Changed

- NLog.Web.AspNetCore 6.2.0 (the OpenModulePlatform family pin); test
  infrastructure on Microsoft.NET.Test.Sdk 18.10.0, xunit.runner.visualstudio
  4.0.0 and Microsoft.AspNetCore.Mvc.Testing 10.0.12.
- The component-version validator is canonicalized under `scripts/omp` with
  lockstep, payload and shared-script drift checks (Checks 12, 13, 15, 16).
- The smoke test reads the target framework from the project file, fails fast on
  an unexpected `/health` status, drains process output, detects leaked paths
  reliably and never deletes a developer's `appsettings.Smoke.json`.
- The local CI gate records a best-effort telemetry line per run; the pre-push
  hook drops inherited `GIT_*` variables.
- Documentation: removed sections copied from OpenModulePlatform that this
  repository does not ship; corrected the version and validator paragraphs;
  recorded the no-central-package-management pin situation.

## [0.1.40] - 2026-08-23

Hardening of the release process after an independent review of v0.1.39. Runtime
behaviour is unchanged.

### Changed

- The release gate runs the unit tests. Publishing pushes with `--no-verify`, so
  they were previously skipped locally and first ran in the workflow after the
  tag was public.
- The release refuses a detached HEAD, a branch other than `main`, a missing
  upstream, and a local `main` behind origin. Only "ahead of upstream" was
  checked before, and a missing upstream made even that pass silently.
- Existing tags are checked on origin as well as locally.
- A failed release reports what it left behind and how to undo or finish it.
- The workflow's build-configuration input works for dispatch runs; a tag always
  builds Release.

### Fixed

- Documentation that contradicted the process: the README's manual release
  proposal, `docs/DEV-SETUP.md` claiming the script never publishes, and the
  version validator's assembly-version note.

## [0.1.39] - 2026-08-23

First official release. Earlier `0.1.x` versions existed only as OpenModulePlatform
artifacts and were never published as installable releases.

### Added

- An official release line: `vX.Y.Z` tags, a published `ODVGateway-vX.Y.Z.zip`
  archive with the framework-dependent publish output, and a tag-triggered
  release workflow.
- `<Version>` in `Directory.Build.props` as the official application version,
  separate from the OMP artifact version in `omp-components.json`.
- Release support in `scripts/release.ps1` (`-ReleaseType`, `-Yes`, `-Publish`).
  Without those switches it is the same local gate as before.

### Changed

- The release gate now runs the unit tests. It did not, which mattered because
  `-Publish` pushes with `--no-verify` and would otherwise skip the only local
  run of them.
- Adopted the shared test standard: TRX output everywhere, and the release
  workflow runs the tests rather than assuming they ran locally.
- Updated the test infrastructure baseline (Microsoft.NET.Test.Sdk 18.8.1,
  xunit.runner.visualstudio 3.1.5).
- Corrected the documented test command and added a "Running tests" section; the
  local CI help text no longer describes this public repository as private.

## [0.1.38] - 2026-07-20

### Added

- NLog file/console logging with a request-correlation middleware, and the NLog
  runtime configuration shipped as an OMP artifact configuration file.
- xUnit Tier D unit-test baseline wired into the local CI gate, with expanded
  coverage for the decoder, fallback URL builder, proxy limiter, and alias options.
- Tracked pre-push and pre-commit Git hooks.

### Changed

- Corrected the repository-visibility documentation: ODVGateway is a public repository.

### Fixed

- Added a global exception handler and stopped swallowing `UnauthorizedAccessException`.
- Matched bare `host:port` AllowedHosts entries in the fallback URL builder.
- Made `smoke-test.ps1` Windows PowerShell 5.1-safe.

> Versions 0.1.32–0.1.37 were internal development increments (no separate release);
> their changes are consolidated here under 0.1.38.

## [0.1.31] - 2026-07-13

### Added

- Added a default `Content-Security-Policy` response header to the Kestrel
  middleware pipeline. The default policy is restrictive (`default-src 'self'`)
  with targeted allowances for the same-origin OpenDocViewer dist files, the
  gateway's inline bootstrap script, inline status-page styles, blob/data image
  sources, and same-origin API calls.
- Made the `Content-Security-Policy` value configurable via
  `ODVGateway:contentSecurityPolicy` in `appsettings.json`. When the setting is
  omitted or null, the gateway uses the built-in default.

### Changed

- Documented the release-approval model in `README.md`: the current default is a
  single-person operator-confirmation flag run through `scripts/release.ps1`,
  with an explicit **ÄGARBESLUT KRÄVS** note that multi-person sign-off must be
  added later if required.

## 0.1.29 - 2026-06-30

- Avoided repeated low-session-TTL warnings during runtime configuration changes.
- Documented the tolerant WebClient file-ticket parser contract.

## 0.1.28 - 2026-06-30

- Logged when configured prepared-session TTL values are raised to the supported minimum.
- Simplified local file URI detection before later trusted-root validation.

## 0.1.27 - 2026-06-30

- Raised the effective prepared-session TTL floor to five minutes.
- Clarified safe `file://` path normalization before trusted-root validation.
- Documented why prepared-session pruning uses a bounded snapshot.

## 0.1.26 - 2026-06-30

- Clarified prepared-session lookup nullability without changing runtime behavior.

## 0.1.25 - 2026-06-30

- Named the prepared-session minimum limit and file-ticket parsing constants.

## 0.1.24 - 2026-06-30

- Made prepared-session and handoff lookup pruning remove only exact key/value pairs.

## 0.1.23 - 2026-06-30

- Clarified prepared-session pruning lock ownership and handoff lookup cleanup.

## 0.1.22 - 2026-06-30

- Clarified prepared-session store cleanup and ODVGateway form limit handling.
- Replaced constant WebClient source log description helper with a named constant.

## 0.1.21 - 2026-06-28

- Replaced exception messages in source-pack and source-proxy error responses with fixed generic texts.
- Removed `WebClientSourceFallback.UrlTemplate` and `AllowedHosts` from `/health` output to avoid leaking internal environment details.

## 0.1.20 - 2026-06-28

- Disabled the Kestrel `Server` response header in standalone deployments.
- Added `Cache-Control: no-store` to `/prep` responses.
- Removed raw exception messages from `/prep` error responses to avoid
  information disclosure.
- Documented the new baseline header and safe error-response behavior in
  `README.md` and `SECURITY.md`.

## 0.1.19 - 2026-06-27

- Added production startup warnings when `webClientHandoff.allowedInitiatorUrls`
  is empty or `webClientHandoff.allowMissingInitiatorHeaders` is enabled.
- Added baseline Kestrel response headers (`X-Frame-Options`,
  `X-Content-Type-Options`, `Referrer-Policy`, `X-Robots-Tag`) to every
  response, including static files.
- Documented the new warnings and headers in `README.md` and `SECURITY.md`.

## 0.1.18 - 2026-06-27

- Prepared the repository for public visibility with project metadata and contribution guidance.
- Changed the default `AllowedHosts` value to local development hosts instead of a wildcard.
- Avoided logging raw client source paths in WebClient fallback diagnostics.

## 0.1.17 - 2026-06-26

- Documented IIS security defaults.
- Hardened WebClient handoff diagnostics.
- Synchronized prepared-session reads and cleanup behavior.

Versions 0.1.12 through 0.1.16 were internal OMP artifact iteration builds and
did not introduce separately documented standalone gateway changes.

## 0.1.11 - 2026-06-24

- Established the initial OMP-compatible ODVGateway module definition and web artifact.
- Documented the WebClient prep and iframe handoff contract.
- Added standalone and OMP-compatible packaging guidance.
