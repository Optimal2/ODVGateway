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

## Independent review and final conductor delta

DeepSeek-flash independently reviewed worker commit
`eee3f11f6ee3e47dff9f3285243f5e1cb2dd17fb`, returned schema GILTIG,
GODKAND and VERDICT pass (native exit 0). It independently reproduced the
three worker mutants with byte-identical hashes and real assertion failures,
then restored the original production bytes. It additionally removed the
blank-key half of the constructor guard (M6, SHA256
`cbcc10d94e4342a0531a5794f59cb853fa600828b4d2a1428360e3d633efb6e4`):
the new blank-extension test failed with ArgumentNullException, native exit 1.
This additional receipt belongs to the independent reviewer, not the worker.

The review identified thin marginal value in the fifth case-insensitive
override Fact because the existing override test and the independently
case-normalizing key/map mechanisms already cover that contract. The conductor
removed that redundant Fact. Four useful new configuration contracts remain.
The historical worker counts above remain accurate for its original candidate;
the final candidate has 23 focused tests and 321 complete unit tests.

Public evidence and the AO provenance trailer are retained because the operator
explicitly requested provider, model and causal acceptance evidence. The review
found no personal data, customer data or credentials. Existing dependency
restore warnings are unchanged from the baseline; this test-only change does not
publish a runtime artifact or alter the repository's family dependency pins.
