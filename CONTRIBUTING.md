# Contributing to ODVGateway

Thank you for helping improve ODVGateway.

## Building

Build the gateway from the repository root:

```powershell
dotnet build src/ODVGateway/ODVGateway.csproj --configuration Release
```

## Packaging

Build the OMP universal package without prompting for a key press:

```powershell
.\scripts\omp\build-universal-package.cmd --no-pause
```

Package output is written to `artifacts/universal-packages/` and is ignored by
Git. Do not commit build artifacts.

## Public-Readiness Checklist

ODVGateway is moving toward public visibility. Before opening a pull request,
verify that your changes do not introduce customer-specific or internal-only
content:

- No customer, site, or vendor names in code, docs, comments, or examples.
- No local filesystem paths, UNC shares, or internal host names in committed examples.
- No secrets, credentials, connection strings, or private URLs.
- `appsettings.json` keeps safe defaults:
  - `AllowedHosts` remains `localhost;127.0.0.1`.
  - `trustClientFilePath` remains `false`.
  - `webClientHandoff.allowedInitiatorUrls` remains empty.
  - `metadataAliases` remains empty unless the example values are clearly generic.
- New configuration examples use placeholder names such as `gateway.example` or
  `webclient.example` rather than real hosts.

## Security

Please review [SECURITY.md](SECURITY.md) before deploying or reporting issues.

Report security vulnerabilities privately through GitHub private vulnerability
reporting if it is enabled, or contact the maintainers through a private channel
before disclosing details publicly.
