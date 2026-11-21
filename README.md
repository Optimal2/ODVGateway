# ODVGateway

ODVGateway is a small ASP.NET Core service that turns the existing
WebClient `MediaViewerTwo.cshtml` handoff into an OpenDocViewer session. It
keeps the original iframe URL the host application already renders, but
serves the selected files itself from server-side paths, the WebClient
ticket stream, or both. ODVGateway is also packaged as an OpenModulePlatform
(OMP) web-app artifact (`odvgateway-web`).

Current application version (`Directory.Build.props` `<Version>`):
**v0.1.45**. Current OMP artifact version (`omp-components.json`):
**0.1.90**. The two lines are deliberately independent — the OMP artifact
can move for a deployable change without an official release, and an
official release can ship without bumping the artifact. Read both numbers
out of their files; this paragraph names them once and can lag. Verified
2026-11-21 against `Directory.Build.props`, `omp-components.json`, and
`odvgateway.module-definition.json`.

The companion SPA it serves is the public
[OpenDocViewer](https://github.com/niclas-berg/OpenDocViewer) project.
ODVGateway contains no viewer code of its own; it only hands off to and
streams bytes for that viewer.

## What it is

- A minimal-API ASP.NET Core 10 application that adapts a host WebClient
  `prep` payload into an OpenDocViewer bootstrap (`/bundle/{key}`) and
  serves the document bytes (`/source/{key}/{idx}` or
  `/source-pack/{key}`).
- A reusable WebClient ticket-stream proxy: when the host WebClient sends
  a `filePath` the gateway cannot read directly, ODVGateway can either
  pass the original ticket URL to the viewer or proxy the bytes itself,
  byte-capped and chunked.
- An OMP-compatible web-app component (`moduleKey: odvgateway`,
  `appKey: odvgateway_webapp`, `packageType: web-app`,
  `targetName: odvgateway`) built through the shared `scripts/omp/*`
  tooling.

## What it is not

- Not an OpenDocViewer fork and not a viewer. The viewer HTML, JS, CSS and
  viewer-side print/PDF pipeline live in the OpenDocViewer project and are
  consumed as a built `dist/` folder.
- Not an OpenModulePlatform host. ODVGateway intentionally does not
  require OMP authentication, because the WebClient handoff is the trust
  boundary in front of it. It can run as a standalone IIS or Kestrel
  application outside OMP.
- Not a shared session store. The prepared session table is in process
  memory (see [Session store](#session-store)), so multi-instance
  deployments need sticky routing or a shared store added before traffic
  can move between instances.
- Not a generic file server. Direct server-side reads only fire when
  `trustClientFilePath=true` AND every requested path resolves below a
  configured `trustedSourceRoots` entry (see
  [Direct source access](#direct-source-access)).

## Architecture

The full architecture description lives in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): components, the handoff and
source-delivery flows with diagrams, the `odvsp1` source-pack format, data
ownership, integration contracts, configuration precedence, the security
model, build and deployment paths, operations and known trade-offs.

In short: the gateway sits between the host WebClient and the OpenDocViewer
SPA. WebClient posts `/prep`; the gateway keeps the payload in an in-memory
session and answers the viewer's `?sessiondata=` request with a redirect to
`?bundleUrl=`. The viewer then fetches `/bundle/{sessionKey}` and the source
bytes from `/source/{sessionKey}/{fileIndex}` or `/source-pack/{sessionKey}`.
Bytes come from a trusted server-side root or from the WebClient ticket
stream. All endpoints are mapped in `src/ODVGateway/Program.cs`.

## Quickstart

Prerequisites:

- .NET 10 SDK (see `global.json`)
- PowerShell 5.1 or PowerShell 7 for the local CI scripts
- A built OpenDocViewer `dist/` folder (sibling checkout or absolute
  path); see `OpenDocViewerDistResolver.ResolveDistPath`

Clone alongside OpenDocViewer so the default sibling probe
(`../../../OpenDocViewer/dist`) finds it:

```text
~/GitHub/
├── ODVGateway/
└── OpenDocViewer/dist/    ← must contain index.html
```

Run locally:

```powershell
dotnet run --project .\src\ODVGateway\ODVGateway.csproj
```

Default URLs:

| URL | Purpose |
| --- | --- |
| `GET /health` | Liveness + configuration snapshot. Returns 503 when the OpenDocViewer dist folder is missing. |
| `POST /prep` | Store a WebClient payload in the in-memory session table. |
| `GET /` (and `GET /index.html`) | Viewer bootstrap. |
| `GET /bundle/{sessionKey}` | One-shot JSON bundle (with diagnostics headers). |
| `GET /source/{sessionKey}/{fileIndex:int}` | Single source file (answers `GET` and `HEAD`; range-supported when served from a trusted root). |
| `GET /source-pack/{sessionKey}` | `application/vnd.opendocviewer.source-pack` (odvsp1) stream of all source files. |

Standalone IIS deployment: see [Standalone IIS Deployment](#standalone-iis-deployment).
OMP deployment: see [OpenModulePlatform Packaging](#openmoduleplatform-packaging).

## Configuration

Gateway configuration lives in `appsettings.json` or environment
variables under the `ODVGateway` section. ASP.NET Core host filtering
uses the top-level `AllowedHosts` setting. The shipped defaults are
generic; override them per deployment.

To turn the shipped `src/ODVGateway/appsettings.json` sample plus a small
site profile into a site configuration file, and to check that file for
drift after an upgrade, use
[tools/appsettings-generator](tools/appsettings-generator/README.md). The
tool is not part of the build output or any package.

### Session store

| Setting | Default | Notes |
| --- | --- | --- |
| `sessionTtlMinutes` | 30 | Effective minimum is 5 minutes; lower values are clamped and a startup warning is logged. |
| `maxConcurrentSessions` | 50 000 | Per-process; new `/prep` requests receive 429 when full. |
| `maxPrepBodyBytes` | 50 MiB | Caps the `/prep` body. Kestrel and `FormOptions` read this limit at startup, so restart after changing it. The shipped IIS request-filtering limit is also 50 MiB; raise that limit with this setting. |
| `webClientHandoff.allowedInitiatorUrls` | `[]` | Empty list **means "everything allowed"** (the guard fails open so a fresh clone can run locally without a configured allowlist). Production deployments must name the host WebClient pages that may start viewer sessions, otherwise the gateway accepts any initiator. |
| `webClientHandoff.allowMissingInitiatorHeaders` | `false` | Compatibility escape hatch; startup logs a warning when enabled. |

The store is process-local and non-durable. Restarts drop pending
handoffs; multi-node production needs sticky routing or a shared session
store added before traffic can move between instances.

**`sessiondata` query exposure:** the host handoff contract places `sessionId`
and `aspxAuth` in unencrypted Base64/Base64URL JSON in the `?sessiondata=`
query parameter. Base64 is encoding, not encryption. Query strings can be
retained by browser history and by access logs on hosts, reverse proxies and
other infrastructure that process the request. The gateway's own NLog layout
and application messages do not write the parameter or its decoded values.
Deployments must nevertheless redact `sessiondata` from surrounding access
logs and avoid URLs that log query strings. Replacing the handoff contract
requires a coordinated host-application and gateway change; it cannot be
changed by this repository alone.

### Direct source access

| Setting | Default | Notes |
| --- | --- | --- |
| `trustClientFilePath` | `false` | Enables direct server-side reads from WebClient `filePath`. Keep disabled unless the gateway is in the same trust boundary as the file paths it receives. |
| `trustedSourceRoots` | `[]` | Required when `trustClientFilePath=true`. Every accepted direct file must resolve below one of these absolute local or UNC roots. Startup rejects missing, empty, or relative entries. `{ContentRoot}` is supported as a literal prefix. |

### Source transport

| Setting | Default | Notes |
| --- | --- | --- |
| `prepHandoffWaitMilliseconds` | 5000 | How long a `?sessiondata=` viewer request waits for its matching `/prep` before it shows "Prepared ODVGateway session was not found". WebClient sends `/prep` and opens the viewer at the same time, so the viewer request can arrive first. `0` disables the wait; values above 30000 are capped. |
| `useBundleUrlHandoff` | `true` | Redirect `?sessiondata=` to `?bundleUrl=`. Keep enabled for large batches; the inline HTML stays small and the bundle is fetched as one JSON request. **Operational caveat:** `false` is not an equivalent mode when OpenDocViewer runs inside the WebClient page. Without a `?bundleUrl=`, OpenDocViewer's bootstrap prefers the same-origin parent (WebClient) payload over the gateway's inline bundle, so the viewer can bypass the prepared gateway session and its source-pack transport and fetch every source directly from WebClient. Startup logs a warning when this is disabled (with an explicit note when `webClientHandoff.allowedInitiatorUrls` is set). Only disable it for a standalone viewer with no parent payload. |
| `sourceCacheControl` | `no-store` | Cache header for streamed source files. |
| `webClientSourceFallback.enabled` | `true` | Enables WebClient ticket-stream fallback when direct files are not readable. |
| `webClientSourceFallback.requireSameHost` | `true` | Reject fallback URLs whose host differs from the gateway request. |
| `webClientSourceFallback.proxyThroughGateway` | `true` | Proxy fallback bytes through the gateway instead of handing the ticket URL to the viewer. |
| `webClientSourceFallback.proxyThroughGatewayAboveSourceCount` | `1000` | Below this count the viewer is given the WebClient ticket URL; above it the gateway proxies. |
| `webClientSourceFallback.proxyMaxConcurrency` | `14` | Shared per-process cap for upstream host fetches from both `/source` and `/source-pack`. |
| `maxSourcePackFrameBytes` | 64 MiB | Per-frame cap for `application/vnd.opendocviewer.source-pack`. |
| `maxSourceProxyBytes` | `0` (follows `maxSourcePackFrameBytes`) | Per-response cap for the proxied `/source` path. |
| `sourcePackStreamBufferBytes` | 128 KiB | Stream copy chunk size (clamped to 4 KiB – 1 MiB). |
| `inlineSources.*` | `enabled: false`; other values in `appsettings.json` | Bound and reported by `/health` only. The current bundle factory never embeds source bytes in the bundle (`OpenDocViewerBundleFactory.BuildViewerFile` sets `InlineBase64 = null`), because WebClient tickets can be one-use. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#6-configuration). |
| `remoteInlineSources.*` | `enabled: false`; other values in `appsettings.json` | No prefetch happens. Only `requestTimeoutMs`, `retryCount`, `retryBaseDelayMs`, `aspxAuthCookieNames` and `sessionCookieNames` are used, by the `/source` proxy and the source pack when they fetch from WebClient. The other keys are reported by `/health` only. |

### Reverse proxy

Forwarded headers are ignored by default. When the gateway is behind a reverse
proxy that supplies `X-Forwarded-For`, `X-Forwarded-Host` and
`X-Forwarded-Proto`, opt in with both settings:

| Setting | Default | Notes |
| --- | --- | --- |
| `forwardedProxy.enabled` | `false` | Enables ASP.NET Core forwarded-header processing before URL generation and handoff validation. |
| `forwardedProxy.knownProxies` | `[]` | Required when enabled. Exact IP addresses of the directly connected trusted proxies. No implicit networks or proxies are trusted; invalid or empty configuration fails startup. Only one forwarded-header hop is consumed. |

Enabling this changes the request scheme, host and remote IP used by
`bundleUrl`, relative fallback URLs, same-host checks and relative initiator
allowlist entries. Configure only the actual proxy addresses; trusting
client-supplied forwarded headers would let callers spoof those security and
routing decisions.

### Viewer and response hardening

| Setting | Default | Notes |
| --- | --- | --- |
| `openDocViewerDistPath` | `""` | Absolute path to the OpenDocViewer `dist/` folder. Empty triggers sibling probe. |
| `requireExplicitOpenDocViewerDistPath` | `false` | Set to `true` in production to disable the sibling probe. |
| `exposeOpenDocViewerDistPathInHealth` | `false` | When `true`, `/health` includes the resolved filesystem path. Keep disabled in production. |
| `contentSecurityPolicy` | `null` (uses `DefaultContentSecurityPolicy`) | Optional override for the `Content-Security-Policy` response header. When omitted, the gateway emits a restrictive default that allows same-origin dist files, the inline `odvgateway-bootstrap` script, inline status-page styles, `blob:`/`data:` images, same-origin API calls, and WebAssembly compilation (`'wasm-unsafe-eval'`) for OpenDocViewer's opt-in OCR and pdf.js decoders. JavaScript `eval` remains prohibited. Custom overrides must keep this token when OCR is enabled; the viewer's HTML meta policy is enforced independently. |
| `metadataAliases` | `{}` | Optional alias mapping copied into the neutral ODV bundle so print templates such as `{{metadata.patientId}}` keep working. Deployments own these mappings in private config; the public repo ships an empty alias map. |
| `contentTypes` | pdf/tif/tiff/jpg/jpeg/png/bmp/gif/webp/jxl/avif | Extension-to-content-type mapping for source files. JPEG XL (`image/jxl`) and AVIF (`image/avif`) are streamed unchanged; OpenDocViewer handles decoding. |

The default `inlineSources.extensions` and `remoteInlineSources.extensions` lists
include `jxl` and `avif`. These lists do not change behaviour today: no source is
inlined, whatever the list contains (see the rows above).

Kestrel middleware always stamps `X-Frame-Options: SAMEORIGIN`,
`X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`,
`X-Robots-Tag: noindex`, and `Content-Security-Policy`. Static `index.html`,
`odv.config.js` and `odv.site.config.js` responses use `Cache-Control: no-store`;
other dist assets keep the one-year immutable cache policy and must therefore
be content-versioned by the viewer build. The
`?sessiondata=` to `?bundleUrl=` redirect stamps
`Referrer-Policy: strict-origin-when-cross-origin` on top, so the
follow-up browser request still carries the WebClient `Referer`. The
redirect itself is the only place to verify it; `/health` cannot show
it. See [SECURITY.md](SECURITY.md) for the full baseline.

`Referrer-Policy` and `Content-Security-Policy` must be owned by the
gateway alone. With two copies on one response the browser enforces
the intersection, so a stricter reverse-proxy or IIS copy silently wins
— usually in the wrong direction. The published `web.config` ships
without them, with comments explaining the constraint.

### Shared light/dark theme

The gateway's own status and error pages (the 400/404/503 HTML pages,
not the viewer itself) follow the shared OMP theme contract: they read
the `OMP_THEME_PREFERENCE` cookie (`{"version":1,"mode":...}`) and stamp
`data-theme` / `data-theme-mode` on `<html>` while rendering. `system`
follows `prefers-color-scheme`; a missing, malformed or unknown-version
cookie falls back to `system`. No script is involved and the default CSP
is unchanged — the pages keep using inline styles under the existing
`style-src 'self' 'unsafe-inline'`, and PDF printing keeps its `blob:`
support in `frame-src`/`connect-src`.

The viewer itself (the OpenDocViewer build served from the configured
`dist/` folder) reads and writes the same preference on its own, under
the gateway's origin. The default CSP needs no change for that code: it
only touches cookies/localStorage and, when enabled, `postMessage`,
none of which CSP restricts.

OpenDocViewer also ships an opt-in cross-origin theme bridge
(`src/integrations/ompThemeBridge.js` in the OpenDocViewer repository)
that syncs the preference with an embedding page on a **different**
origin over `postMessage`. It is **off by default**. To enable it for a
specific embedding origin, edit `odv.site.config.js` in the deployed
OpenDocViewer `dist/` folder (the gateway serves it as a static file):

```js
theme: {
  bridge: { allowedOrigins: ['https://portal.example'] }
}
```

Only exact `https://`/`http://` origins are accepted, never wildcards;
every message is checked for origin, source window, message shape and
revision before it is applied. Leave the list empty (or the key absent)
in deployments that do not embed the viewer cross-origin.

## Known limitations

These are things ODVGateway intentionally does **not** do today. Each has
been verified against the current source — none of them is a documentation
oversight:

- **No database-backed path lookup.** The session store and the direct
  source resolver read only from in-memory state. A database or catalogue
  source for client-supplied `filePath` values is out of scope.
- **Single shared allowlist for client `filePath`, not per-root
  authorization.** `trustedSourceRoots` is a flat allowlist: every
  configured root grants identical read access. Fine-grained access
  policies per root are not implemented.
- **No shared session store.** Prepared sessions live in process memory
  (see [Session store](#session-store)), so multi-instance deployments
  need sticky routing or a shared store added before traffic can move
  between instances.
- **No bundled archive/manifest streaming for very large raster runs.**
  Source bytes flow as per-file frames inside
  `application/vnd.opendocviewer.source-pack` (or per-file `/source`
  responses); very large raster runs must keep every individual file
  inside `maxSourcePackFrameBytes` / `maxSourceProxyBytes`.
- **Unknown-length source proxy responses are buffered before delivery.**
  Without an upstream `Content-Length`, `/source` validates the entire
  response against `maxSourceProxyBytes` before sending a successful body.
  Buffers above 64 KiB spill to the ASP.NET Core temporary directory
  (`ASPNETCORE_TEMP`, or the process temp directory), which must be writable
  and have room for concurrent responses. Temporary files are removed when
  the request finishes. This delays the first response byte until validation
  completes; exceeding the limit returns HTTP 502.

## Standalone IIS Deployment

1. Publish the app:

   ```powershell
   $publishRoot = 'C:\deploy\ODVGateway'
   dotnet publish .\src\ODVGateway\ODVGateway.csproj -c Release -o $publishRoot
   ```

   Treat the publish output as a deployment folder or a disposable
   staging copy outside this repository. Do not keep long-lived
   deployment configuration under `.\artifacts\publish\...` in the
   repo worktree: ignored local publish folders can preserve stale
   `appsettings*.json` across source updates.

2. Put or reference an OpenDocViewer `dist/` folder.

3. Configure `appsettings.json` in the deployed gateway folder.

   Replace the sample paths with deployment-specific values in private
   environment configuration. Keep deployment-specific
   `metadataAliases` mappings in that private config as well; the public
   repo intentionally ships an empty alias map because WebClient
   metadata field IDs vary by deployment. Set the top-level
   `AllowedHosts` value to the gateway's public host names. The
   prepared session store is in memory, so plan production restarts and
   any load balancing around process-local, non-durable handoffs.

4. Create an IIS application that points to the published gateway
   folder.

5. Set WebClient `PathToVideoEditUtility` to the gateway URL, including
   the trailing slash.

OpenDocViewer site configuration, including print logging endpoints such
as `/WebClientODV/DocumentView/LogPrint`, remains in the OpenDocViewer
`odv.site.config.js` loaded from the configured `dist/` folder.

The published `src/ODVGateway/web.config` ships with
`maxUrl="65536"` and `maxQueryString="65536"` so the WebClient
`sessiondata` query handoff can carry large case selections. It does
**not** add `Referrer-Policy` or `Content-Security-Policy`; the
application middleware is the header's only owner.

## OpenModulePlatform Packaging

ODVGateway is its own OMP module:

```text
moduleKey:  odvgateway
appKey:     odvgateway_webapp
target:     web-app / odvgateway
```

Build OMP portable objects:

```powershell
.\build-omp-objects.ps1 -AllComponents -BuildArtifacts
```

Export a universal package:

```powershell
.\scripts\omp\export-universal-package.ps1 -AllComponents -BuildArtifacts
```

Runtime `appsettings.json` is not part of the immutable artifact payload.
The package ships a baseline `appsettings.json` (NLog file/console
logging with correlation-id layout plus environment-neutral `ODVGateway`
defaults) as an artifact configuration file:
`src/ODVGateway/Packaging/appsettings.json`, wired through
`artifactConfigurationFiles` in `omp-components.json`. The OMP HostAgent
writes it to the site at deploy time and deep-merges its built-in
web-app sections underneath. Site-specific values (dist paths, trusted
roots, aliases) belong in host-specific config overlays or deployment
files, never in this repository
([tools/appsettings-generator](tools/appsettings-generator/README.md) can
derive such a file from either sample). Packaging scripts warn when ignored
standalone publish `appsettings*.json` files remain under
`artifacts/publish`, because those files are easy to confuse with
package input but are excluded from OMP artifact payloads.

## Development

Run locally against a sibling OpenDocViewer checkout:

```powershell
dotnet run --project .\src\ODVGateway\ODVGateway.csproj
```

The development config resolves `../../../OpenDocViewer/dist` from the
project folder, which matches the default sibling repository layout.

Run the xUnit unit tests:

```powershell
dotnet test tests\ODVGateway.Tests\ODVGateway.Tests.csproj --configuration Release
```

They target net10.0. Most are in-memory; `LegacySignatureStartupTests` boots
the real gateway over loopback HTTP and uses temporary files, but no test needs
an external service. The local CI gate (`scripts/local-ci.ps1`) runs them
between build and smoke test.

There is no UiTests project in this repository, and that is deliberate:
the gateway is a minimal-API service with no Razor pages or
server-rendered UI of its own. The user interface it serves is the
OpenDocViewer SPA, which is tested with its own suite in the
OpenDocViewer repository.

## Local pre-push gate

This repository uses tracked Git hooks to run the local CI gate before
every push. Configure the hooks once after cloning:

```powershell
.\scripts\setup-hooks.ps1
```

The configuration points Git at the `.githooks` directory in this
repository.

Hooks:

- `pre-commit` — light static checks only (`git diff --cached --check`).
  Does not build or run tests.
- `pre-push` — runs `scripts\local-ci.ps1`, which builds the gateway,
  runs the xUnit unit tests in `tests\ODVGateway.Tests` (including the
  loopback/temp-file startup probes), runs the smoke test, and validates OMP
  component version lockstep. Its shared-script drift check (Check 15)
  is strict: set `OMP_PLATFORM_ROOT` to an OpenModulePlatform checkout
  when this repository is not beside one, or the push is blocked. See
  [docs/DEV-SETUP.md](docs/DEV-SETUP.md#local-ci).

The push is blocked if the local CI gate fails. Because this
repository's GitHub Actions are `workflow_dispatch`-only by deliberate
choice — public repositories get free Actions, so the trigger is a
design choice rather than a metering constraint — the local gate is
the actual pre-push verification.

## Security and Public Repository Hygiene

This repository intentionally ships only generic defaults. Keep
production URLs, trusted source roots, metadata alias mappings,
credentials, and customer-specific deployment settings in private
configuration outside this public source tree.

See [SECURITY.md](SECURITY.md) for supported versions, vulnerability
reporting, and deployment hardening guidance.

## Documentation Index

| Document | Purpose |
| --- | --- |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Architecture: components, flows, data, integration contracts, configuration, security model, build and deployment, operations, trade-offs. |
| [AGENTS.md](AGENTS.md) | Repository workflow, dependency-pin policy, release gate, two-version-line model. |
| [SECURITY.md](SECURITY.md) | Supported versions, vulnerability reporting, security model, operational guidance. |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Build, packaging, public-readiness checklist. |
| [CHANGELOG.md](CHANGELOG.md) | Release history in Keep-a-Changelog format. |
| [LICENSE](LICENSE) | MIT license (Copyright Optimal2). |
| [docs/DEV-SETUP.md](docs/DEV-SETUP.md) | Local clone layout, dev-only settings, demo source files. |
| [scripts/omp/README.md](scripts/omp/README.md) | OMP packaging and version-lockstep tooling. |
| [tools/appsettings-generator/README.md](tools/appsettings-generator/README.md) | Generate and drift-check a site `appsettings.json` from the shipped sample plus a site profile (not packaged). |
| [release-notes/](release-notes/) | Per-version release notes (v0.1.39 – v0.1.45). |
| [docs/reproducible-artifacts.md](docs/reproducible-artifacts.md) | Why checkout line endings are pinned and how to compare OMP artifact packages. |

### Published release vs OMP artifact

The two distribution outputs share the `ODVGateway-` prefix and the same
`<application-version>` number, but they are different files:

- `ODVGateway-v<application-version>.zip` — the published GitHub release:
  framework-dependent `dotnet publish` output, attached to the
  `v<application-version>` tag by `.github/workflows/release.yml`. Example:
  `ODVGateway-v0.1.39.zip`. The leading `v` matches the git tag.
- `odvgateway__odvgateway_webapp__web-app__odvgateway__<artifact-version>.zip`
  — the OMP portable-object package produced by
  `scripts/omp/export-universal-package.ps1`; the file name is built by
  `scripts/omp/build-repository-objects.ps1:Get-ArtifactPackageName` from
  the five `omp-components.json` fields (`moduleKey`, `appKey`,
  `packageType`, `targetName`, `version`). Example for the current build:
  `odvgateway__odvgateway_webapp__web-app__odvgateway__0.1.87.zip`.

The two archives are rebuilt independently: an official release may or
may not roll into the next OMP artifact, and vice versa — the
OpenModulePlatform packaging section below describes the version
relationship and the trigger to bump the OMP artifact after an official
release.

## License

ODVGateway is licensed under the [MIT License](LICENSE).
