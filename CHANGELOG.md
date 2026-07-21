# Changelog

All notable changes to ODVGateway are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
for its `0.1.x` release line.

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
