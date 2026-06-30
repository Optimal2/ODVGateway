# ODVGateway

ODVGateway is a companion application for OpenDocViewer. It adapts the existing
WebClient `MediaViewerTwo.cshtml` handoff into an OpenDocViewer session where
source files are streamed directly by the gateway.

ODVGateway can read source files directly from server-side paths when an
installation explicitly enables that mode and restricts it to configured trusted
source roots. The safer default is to use the WebClient ticket fallback instead
of trusting client-supplied file paths.

## Why This Exists

The standard WebClient integration sends OpenDocViewer one URL per source file.
For TIFF/JPEG runs where every page is a separate source file, the initial load
is dominated by hundreds of WebClient ticket requests. ODVGateway keeps the
original WebClient iframe contract but serves the selected files itself from
server-side paths.

## WebClient Contract

The original `MediaViewerTwo.cshtml` can remain unchanged when
`model.MediaConfiguration.PathToVideoEditUtility` points at ODVGateway with a
trailing slash:

```text
https://example/WebClientODVGateway/
```

The existing page already performs the two calls ODVGateway needs:

```text
POST {baseUrl}prep
GET  {baseUrl}?sessiondata=<base64-json>
```

`POST /prep` stores the WebClient prep payload in process memory for a short
time.
`GET /?sessiondata=...` decodes the existing WebClient session data, finds the
matching prepared payload, and redirects the viewer to OpenDocViewer's
`bundleUrl` startup flow. The bundle itself is served by ODVGateway as one JSON
request. The public session key used in `/bundle`, `/source`, and
`/source-pack` URLs is random; the deterministic WebClient handoff value is kept
internal and is never returned in URLs. Because prepared sessions are kept only
in the running gateway process, an application restart drops pending handoffs
and a multi-instance deployment needs sticky routing or a shared store added
before requests can move between instances.

ODVGateway intentionally does not depend on OpenModulePlatform authentication.
For production use, lock the handoff to the WebClient pages that are allowed to
start viewer sessions by configuring `webClientHandoff.allowedInitiatorUrls`.
This checks the browser `Referer`/`Origin` headers on `/prep` and viewer startup
requests. Leave the list empty only for local development or environments where
an upstream system already restricts access to the gateway.

`GET /health` reports whether the OpenDocViewer dist path resolved, active
session count, the configured session cap, `useBundleUrlHandoff`, and inline
source limits. The literal dist path is hidden by default and is only returned when
`exposeOpenDocViewerDistPathInHealth` is enabled. Use this during deployment
checks to verify that the running gateway picked up the expected configuration.

When direct server-side source paths are enabled and the WebClient `filePath`
points at a file below a configured trusted source root, source files are then
loaded from:

```text
GET /source/{sessionKey}/{fileIndex}
```

The source endpoint opens the trusted file path on the server and streams it
with range support. If direct path access is disabled, outside the trusted
roots, or not readable, ODVGateway can fall back to the original WebClient
ticket stream URL instead of giving OpenDocViewer a broken `/source` URL. The
diagnostics overlay reports how many files were routed through direct disk
access, gateway source URLs, inline source bytes, and WebClient fallback URLs.
The WebClient proxy fallback is byte-capped and streamed to the client without
full-response buffering in gateway memory; range handling remains focused on
direct server-side file reads.

## Runtime Configuration

Gateway configuration lives in `appsettings.json` or environment variables
under the `ODVGateway` section. ASP.NET Core host filtering uses the top-level
`AllowedHosts` setting.

Important settings:

- `AllowedHosts`: Top-level ASP.NET Core host allowlist. The repository default
  is `localhost;127.0.0.1`, which is only suitable for development. Set this to
  the public host names that serve the gateway in production, without schemes or
  paths (for example `gateway.example;gateway.internal.example`). Keep `*` only
  for local development or a deployment where another trusted front door performs
  equivalent host validation.
- `openDocViewerDistPath`: Path to an OpenDocViewer `dist` folder. If empty,
  the gateway tries `wwwroot/odv`, `wwwroot/OpenDocViewer`, a sibling
  `OpenDocViewer` IIS folder, and sibling `OpenDocViewer/dist` checkout paths.
  Set this explicitly in production so the gateway always serves the intended
  OpenDocViewer build.
- `requireExplicitOpenDocViewerDistPath`: When `true`, disables development
  fallback probing and requires `openDocViewerDistPath` to point at a valid ODV
  dist folder. Keep this `true` in production and `false` for local development.
- `sessionTtlMinutes`: How long prepared WebClient sessions remain in memory.
- `maxConcurrentSessions`: Upper bound for the in-memory prepared session store.
  New `/prep` requests are rejected with HTTP `429` when the cap is reached.
  The store is process-local and not durable; restart clears all prepared
  sessions and multi-node production deployments must keep each browser on the
  same gateway instance unless a shared session store is implemented.
- `maxSourcePackFrameBytes`: Maximum payload size for one
  `application/vnd.opendocviewer.source-pack` frame. Direct files above this
  size are rejected before reading; WebClient fallback responses are bounded
  while streamed into the frame so missing `Content-Length` cannot cause
  unbounded memory use.
- `maxSourceProxyBytes`: Maximum payload size for one proxied `/source`
  response when ODVGateway has to fetch the file from WebClient instead of
  reading a trusted server-side path. Keep this aligned with
  `maxSourcePackFrameBytes` unless a deployment needs a distinct proxy limit.
  Proxied source responses are streamed through this cap instead of being fully
  buffered in memory first.
- `sourcePackStreamBufferBytes`: Chunk size used when streaming a known-length
  source-pack frame. Direct source files are streamed from disk without loading
  the full file into a byte array.
- `exposeOpenDocViewerDistPathInHealth`: When `true`, `/health` includes the
  resolved OpenDocViewer dist filesystem path. Keep this `false` in production
  unless a trusted monitor explicitly needs the literal path.
- `trustClientFilePath`: Enables direct server-side file reads from WebClient
  `filePath` values. Keep this `false` unless the gateway process runs in the
  same trust boundary as the file paths it receives.
- `trustedSourceRoots`: Required when `trustClientFilePath` is `true`. Every
  direct source file must resolve below one of these absolute local or UNC
  roots. Startup rejects missing, empty, or relative entries, and runtime
  reloads keep rejecting invalid configurations.
- `useBundleUrlHandoff`: Redirects the existing WebClient iframe URL to
  OpenDocViewer's `bundleUrl` startup flow. Keep this enabled for large batches
  so the viewer HTML stays small and the prepared bundle is fetched as one
  explicit JSON request.
- `sourceCacheControl`: Cache header for streamed source files. The default is
  `no-store`.
- `webClientHandoff.allowedInitiatorUrls`: Optional allowlist for WebClient
  URLs that may initialize gateway sessions. Entries may be absolute URLs, host
  names, or root-relative paths on the gateway host. Path-specific entries match
  `Referer` headers; `Origin` headers can only satisfy host-level entries because
  browsers do not include a path in `Origin`. The gateway logs a startup warning
  when this list is empty.
- `webClientHandoff.allowMissingInitiatorHeaders`: Optional compatibility escape
  hatch if a trusted deployment strips both `Referer` and `Origin`. Keep this
  `false` unless the gateway is protected by another boundary. The gateway logs
  a startup warning when this setting is enabled.
- `webClientSourceFallback`: Optional fallback used when `filePath` is not
  readable by the gateway process. By default the gateway first reuses `filePath`
  when it already looks like a browser URL, matching the legacy OpenDocViewer
  parent-page flow. If that is not possible, the configured URL template is used.
  The default template is `/WebClientODV/DocumentView/GetStream/?ticket={fileId}`
  and supports `{fileId}`, `{sessionKey}`, `{fileIndex}`, and `{extension}`
  tokens. Absolute fallback URLs must target the same host as the gateway
  request unless `allowedHosts` is configured.
- `inlineSources`: Embeds small raster source files directly into the prepared
  OpenDocViewer bundle as native inline source bytes when the configured
  per-file and total size limits allow it. This avoids hundreds of
  browser-to-gateway source requests for small TIF/JPG/PNG batches while larger
  files keep using `/source`.
- `remoteInlineSources`: Optional server-side prefetch for small same-host
  raster source URLs. This lets ODVGateway turn many WebClient source URLs into
  inline bundle bytes when direct file paths are not readable, reducing
  browser-to-WebClient roundtrips over slow/VPN clients. The default profile is
  deliberately sequential with short retries because WebClient ticket endpoints
  can be sensitive to concurrent source requests.
- `web.config`: The published IIS config raises `maxUrl` and `maxQueryString`
  to 65536 so the existing WebClient `sessiondata` query handoff can carry large
  case selections.
- Standalone Kestrel deployments add a baseline set of response headers to every
  response, including static files: `X-Frame-Options: SAMEORIGIN`,
  `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, and
  `X-Robots-Tag: noindex`. Kestrel also disables the default `Server` response
  header. IIS deployments already set the first three through `web.config` and
  can remove the `Server` header with `web.config` requestFiltering or URL
  Rewrite configuration; deployment-specific `Content-Security-Policy` and
  `Strict-Transport-Security` headers remain the responsibility of the host
  reverse proxy or IIS configuration.
- `metadataAliases`: Optional alias mapping copied into the neutral ODV bundle
  so print templates such as `{{metadata.patientId}}` keep working. The
  `fieldId` values come from the deployment's WebClient metadata schema and
  should be supplied in private deployment configuration rather than committed
  as public defaults. Separately from this optional alias map, ODVGateway also
  reads metadata field `504` internally as a page-count hint; that code path
  does not make other metadata field IDs product-standard defaults.
- `contentTypes`: Extension-to-content-type mapping for source files.

Example production snippet:

```json
{
  "AllowedHosts": "gateway.example;gateway.internal.example",
  "ODVGateway": {
    "openDocViewerDistPath": "C:\\path\\to\\OpenDocViewer\\dist",
    "requireExplicitOpenDocViewerDistPath": true,
    "sessionTtlMinutes": 30,
    "maxConcurrentSessions": 50000,
    "maxSourcePackFrameBytes": 67108864,
    "maxSourceProxyBytes": 67108864,
    "sourcePackStreamBufferBytes": 131072,
    "exposeOpenDocViewerDistPathInHealth": false,
    "trustClientFilePath": true,
    "trustedSourceRoots": [
      "\\\\<file-server>\\<trusted-share>"
    ],
    "useBundleUrlHandoff": true,
    "sourceCacheControl": "no-store",
    "webClientHandoff": {
      "allowedInitiatorUrls": [
        "https://webclient.example/WebClientODV/MediaViewerTwo.cshtml"
      ],
      "allowMissingInitiatorHeaders": false
    },
    "webClientSourceFallback": {
      "enabled": true,
      "requireSameHost": true,
      "allowedHosts": [],
      "useFilePathUrlWhenDirectFileMissing": true,
      "useWhenDirectFileMissing": true,
      "urlTemplate": "/WebClientODV/DocumentView/GetStream/?ticket={fileId}"
    }
  }
}
```

## Standalone IIS Deployment

1. Publish the app:

   ```powershell
   $publishRoot = 'C:\deploy\ODVGateway'
   dotnet publish .\src\ODVGateway\ODVGateway.csproj -c Release -o $publishRoot
   ```

   Treat the publish output as a deployment folder or a disposable staging copy
   outside this repository. Do not keep long-lived deployment configuration in
   `.\artifacts\publish\...` under the repo worktree because ignored local
   publish folders can preserve stale `appsettings*.json` across source updates.

2. Put or reference an OpenDocViewer `dist` folder.

3. Configure `appsettings.json` in the deployed gateway folder.

   Replace the sample paths with deployment-specific values in private
   environment configuration. Keep deployment-specific `metadataAliases`
   mappings in that private config as well; the public repo intentionally ships
   an empty alias map because WebClient metadata field IDs vary by deployment.
   Set the top-level `AllowedHosts` value to the gateway's public host names.
   The prepared session store is in memory, so plan production restarts and any
   load balancing around process-local, non-durable handoffs.

4. Create an IIS application that points to the published gateway folder.

5. Set WebClient `PathToVideoEditUtility` to the gateway URL, including the
   trailing slash.

OpenDocViewer site configuration, including print logging endpoints such as
`/WebClientODV/DocumentView/LogPrint`, remains in the OpenDocViewer
`odv.site.config.js` loaded from the configured `dist` folder.

## Security and Public Repository Hygiene

This repository intentionally ships only generic defaults. Keep production URLs,
trusted source roots, metadata alias mappings, credentials, and customer-specific
deployment settings in private configuration outside this public source tree.

See [SECURITY.md](SECURITY.md) for supported versions, vulnerability reporting,
and deployment hardening guidance.

## OpenModulePlatform Packaging

ODVGateway is its own OMP module:

```text
moduleKey: odvgateway
appKey:    odvgateway_webapp
target:    web-app / odvgateway
```

Build OMP portable objects:

```powershell
.\build-omp-objects.ps1 -AllComponents -BuildArtifacts
```

Export a universal package:

```powershell
.\scripts\omp\export-universal-package.ps1 -AllComponents -BuildArtifacts
```

Runtime `appsettings.json` should be supplied as an artifact configuration file
or host-specific deployment file. It is not part of the immutable artifact zip.
Packaging scripts warn when ignored standalone publish `appsettings*.json`
files remain under `artifacts/publish` because those files are easy to confuse
with package input but are excluded from OMP artifact payloads.

## Development

Run locally against a sibling OpenDocViewer checkout:

```powershell
dotnet run --project .\src\ODVGateway\ODVGateway.csproj
```

The development config resolves `../../../OpenDocViewer/dist` from the project
folder, which matches the default sibling repository layout.

Health check:

```text
GET /health
```

## Current 0.1.x Scope

The current repository and web component version is `0.1.23`. The module
definition version remains `0.1.11` because the public OMP module contract did
not need a schema change for the later runtime hardening work.

Included:

- Existing WebClient `prep` and iframe contract.
- In-memory prepared session store.
- Direct server-side file streaming from WebClient `filePath`.
- Explicit WebClient ticket fallback when direct file paths are not readable.
- Conservative same-host remote inline source prefetch for small raster
  WebClient URLs.
- OpenDocViewer `index.html` injection using the supported `window.ODV` API.
- Metadata preservation and configurable alias projection.
- WebClient metadata fields tolerate string, number, boolean, and null values.
- Standalone and OMP-compatible packaging.
- Bundle URL handoff and diagnostics for source routing.
- Conservative source page-count hints for better initial OpenDocViewer totals.
- Same-host remote inline source prefetch for small raster WebClient URLs.

Deferred:

- Database-backed path lookup.
- File path allowlists and per-root authorization.
- Multi-node/shared session storage.
- Bundled archive/manifest streaming for very large raster runs.

## License

ODVGateway is licensed under the [MIT License](LICENSE).
