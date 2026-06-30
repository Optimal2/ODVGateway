# Changelog

All notable changes to ODVGateway are documented here.

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
