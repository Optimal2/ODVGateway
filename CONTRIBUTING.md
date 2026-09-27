# Contributing to ODVGateway

Thank you for helping improve ODVGateway.

## Building

Build the gateway from the repository root:

```powershell
dotnet build src/ODVGateway/ODVGateway.csproj --configuration Release
```

The project targets `net10.0` (see `src/ODVGateway/ODVGateway.csproj`) and
pins `NLog.Web.AspNetCore 6.2.1` inline. There is no central package
management in this repository — see `AGENTS.md` for the dependency-pin
rationale.

## Testing

Run the xUnit unit tests in `tests/ODVGateway.Tests`:

```powershell
dotnet test tests/ODVGateway.Tests/ODVGateway.Tests.csproj --configuration Release
```

The tests are in-memory (Tier D) and need nothing installed, with one
exception (`GatewayHttpStatusTests`) that boots the real app on the
in-memory TestServer via `WebApplicationFactory` to probe HTTP status
codes. There is no `*.UiTests` project in this repository by design —
the user interface is the OpenDocViewer SPA, tested in its own suite
in the OpenDocViewer repository.

## Packaging

Build the OMP universal package without prompting for a key press:

```powershell
.\scripts\omp\build-universal-package.cmd --no-pause
```

Package output is written to `artifacts/universal-packages/` and is
ignored by Git. Do not commit build artifacts.

## Local CI gate

Before every push, run the local CI gate:

```powershell
.\scripts\local-ci.ps1
```

The script runs `dotnet build`, the xUnit unit tests, the smoke test
in `scripts/smoke-test.ps1`, and the canonical component-version
validator in `scripts/omp/validate-component-versions.ps1`. The
pre-push Git hook is wired by `scripts\setup-hooks.ps1` and blocks the
push if the gate fails. GitHub Actions are `workflow_dispatch`-only
by deliberate choice — the local gate is the actual pre-push
verification.

## Public-Readiness Checklist

ODVGateway is a public repository. Before opening a pull request,
verify that your changes do not introduce customer-specific or
internal-only content:

- No customer, site, or vendor names in code, docs, comments, or
  examples.
- No local filesystem paths, UNC shares, or internal host names in
  committed examples.
- No secrets, credentials, connection strings, or private URLs.
- `appsettings.json` keeps safe defaults:
  - `AllowedHosts` remains `localhost;127.0.0.1`.
  - `trustClientFilePath` remains `false`.
  - `webClientHandoff.allowedInitiatorUrls` remains empty.
  - `metadataAliases` remains empty unless the example values are
    clearly generic.
- New configuration examples use placeholder names such as
  `gateway.example` or `webclient.example` rather than real hosts.

## Security

Please review [SECURITY.md](SECURITY.md) before deploying or reporting
issues.

Report security vulnerabilities privately through GitHub private
vulnerability reporting if it is enabled, or contact the maintainers
through a private channel before disclosing details publicly.
