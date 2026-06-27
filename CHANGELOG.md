# Changelog

All notable changes to ODVGateway are documented here.

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

## 0.1.11 - 2026-06-24

- Established the initial OMP-compatible ODVGateway module definition and web artifact.
- Documented the WebClient prep and iframe handoff contract.
- Added standalone and OMP-compatible packaging guidance.
