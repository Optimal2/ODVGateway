# Contributing

Thank you for considering a contribution to ODVGateway.

## Scope

ODVGateway is a standalone handoff gateway for OpenDocViewer. Keep contributions
generic and deployable outside OpenModulePlatform unless a change is explicitly
about the optional OpenModulePlatform packaging layer.

Do not commit customer-specific configuration, production URLs, internal host
names, credentials, private file-share paths, or environment-specific metadata
mappings. Deployment values belong in private runtime configuration, not in this
public repository.

## Development

- Keep changes small and reviewable.
- Prefer explicit configuration over environment-specific assumptions.
- Preserve the standalone OpenDocViewer use case.
- Validate with `dotnet build` before submitting source changes.
- Update `README.md` and `SECURITY.md` when behavior or security guidance
  changes.

## Security

Report security issues privately as described in `SECURITY.md`. Do not open a
public issue with exploit details or sensitive deployment information.
