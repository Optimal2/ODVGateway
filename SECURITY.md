# Security Policy

ODVGateway is a companion gateway for OpenDocViewer. It is intended to be
deployable as a standalone ASP.NET Core/IIS application and as an
OpenModulePlatform artifact.

Security issues should be reported privately before public disclosure.

## Supported Versions

ODVGateway is currently in the `0.1.x` release line.

| Version | Supported |
| --- | --- |
| 0.1.x | Yes |
| < 0.1.0 | No |

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
  host names instead of leaving the development wildcard
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
- keep source proxy and source-pack byte limits aligned with the deployment's
  expected maximum source-file size
- treat prepared sessions as process-local memory: restarts clear pending
  handoffs, and multi-instance deployments need sticky routing or a shared
  session-store implementation before requests can move between instances

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
