# Evidence: native ContentTypeMapper regression tests

Provider: Muse Code powered by Meta Muse Spark (native Windows, no fallback provider).
Shell: Windows PowerShell 5.1 for `dotnet` runs, `pwsh` 7 only for `scripts/local-ci.ps1`.
Runtime: .NET SDK 10.0.402, Windows 10.0.19045. All commands ran in the job worktree.

## Change

`tests/ODVGateway.Tests/ContentTypeMapperTests.cs`: removed the duplicated
`GetContentType_NormalizesWhitespacePaddedPdf` Fact (already covered by the
`GetContentType_NormalizesCaseLeadingDotAndWhitespace` theory with
`InlineData("  .PDF ", "application/pdf")`) and added five focused,
independent configuration-contract regression tests:

- `GetContentType_ConfiguredExtensionKeyIsNormalized`: a whitespace/dot/case
  padded configured key (`"  .PDF "`) resolves for `pdf`, `.PDF`, `  pdf  `;
  `png` default stays.
- `GetContentType_ConfiguredMimeValueIsTrimmed`: a padded configured MIME value
  resolves trimmed for `pdf` and `.PDF`.
- `GetContentType_BlankConfiguredExtensionIsIgnored`: blank keys (`""`,
  `"   "`, `"."`) do not delete the `pdf`/`png` defaults.
- `GetContentType_WhitespaceOnlyConfiguredValueIsIgnored`: blank values for
  `pdf`/`png` do not delete the defaults.
- `GetContentType_ConfiguredOverrideIsCaseInsensitive`: mixed-case key `PdF`
  wins for `pdf`/`PDF`/`.Pdf`; `png`/`jpg` defaults stay.

No production source was changed. `src/` is byte-identical before and after.

## Build and test receipts (native exits)

- `dotnet build src/ODVGateway/ODVGateway.csproj --configuration Release`
  -> exit 0 (4 NU1903 warnings, 0 errors).
- Baseline `dotnet test ... --filter FullyQualifiedName~ContentTypeMapperTests`
  -> exit 0, 20/20 passed.
- After the change, same filter -> exit 0, 24/24 passed (20 - 1 + 5).
- `pwsh -File scripts/local-ci.ps1 -PlatformRepositoryRoot
  G:/GitHub/OpenModulePlatform -SmokePort 15213` -> exit 0, LOCAL CI PASSED
  (port 15213 verified free first; unit TRX 322/322 passed; Pester 35/35;
  strict Check 13/15 validator passed, so no `omp-components.json` bump is
  required for this test-only change).

## Causal mutation proof (one mutant at a time, restored after each)

Original `src/ODVGateway/Services/ContentTypeMapper.cs` SHA256:
`24e6915f40cb79d369609063aef221dd3eb909bb4ebaa885a6247f05333f2ce6`
(1589 bytes). After every mutant the file was restored from a scratch backup
and the hash re-verified; `git status` shows no `src/` modification.

| Mutant | Change | File SHA256 | Targeted test (`dotnet test --filter`) | RED exit | Post-restore |
| --- | --- | --- | --- | --- | --- |
| M1 | `map[key] = entry.Value.Trim();` -> `map[key] = entry.Value;` | `00e381340395fb673186d431b7724e52deadc49f8f95e4ede96e0f66003e7001` | `FullyQualifiedName~ConfiguredMimeValueIsTrimmed` | exit 1, Failed 1/1 | exit 0, Passed 1/1 |
| M2 | `if (!IsNullOrWhiteSpace(key) && !IsNullOrWhiteSpace(entry.Value))` -> `if (!IsNullOrWhiteSpace(key))` | `fbf7beb10f8518bb643521576863bb0f1df02b4df256d86a6c027dac99453850` | `FullyQualifiedName~WhitespaceOnlyConfiguredValueIsIgnored` | exit 1, Failed 1/1 | exit 0, Passed 1/1 |
| M3 | `map[key] = ...` -> `map[entry.Key] = ...` | `419499ebfdaeedf965d80137136dcf65a2409c5b048d6ab4b3bced5e14cc92cc` | `FullyQualifiedName~ConfiguredExtensionKeyIsNormalized` | exit 1, Failed 1/1 | exit 0, full `ContentTypeMapperTests` 24/24 |

No mutation survives in the final tree.

## Measurement notes

- Restoring via `Copy-Item` keeps the backup's older timestamp, so the next
  `dotnet test` reused the mutant DLL (stale incremental build, still RED).
  Setting `LastWriteTime` to now after each restore fixed it; hashes prove the
  bytes were already identical. This trap is worth knowing for future
  mutate-and-restore loops on Windows.
- `&&` inside a double-quoted Windows PowerShell string is literal; escaping
  it as `\&\&` breaks the match. A quoting probe wrote one marker byte to the
  source mid-run and it was restored immediately (hash re-verified).
