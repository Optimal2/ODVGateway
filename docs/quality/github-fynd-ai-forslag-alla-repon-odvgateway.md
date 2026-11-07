# ODVGateway AI findings, all repositories round (WP0)

Base: `3f49a14c87afd1b3ea3bf986fa7d8e9b84b882ff`.

Scope: the four AI-suggested findings listed for ODVGateway in the
2026-11-06 WP0 prep sweep (section `## ODVGateway`), plus the two
`cs/path-combine` standard rows they reference.

Scan freshness: the sweep scanned HEAD `a177e757` on 2026-11-05T22:47:07Z.
Two commits landed after the scan: `3f49a14` (wait-cancellation test plus WP5
wording) and this job's canonical sync. Both deltas are covered by the local
verification below; the post-push GitHub rescan is the remaining freshness
gate and is reported, not assumed.

No finding was dismissed in the scanner and no analyzer suppression was added.
Canonical OpenModulePlatform files are held byte-identical by Check 15 and are
never edited locally; upstream-canonical rows below name the blob they came
from instead.

## Per-finding dispositions

| Rule / source | Location | Disposition and reason |
| --- | --- | --- |
| `cs/path-combine` | `build/DeterministicRazor/DeterministicRazorGenerator.cs:27` | **Retained by design; rationale now in the file.** The second operand is a constant relative DLL filename beneath the trusted compiler directory, so it cannot be rooted and `Path.Combine` cannot discard the base. The canonical sync (OpenModulePlatform `5e1c43c49c2541982f9ddab9e5a5f60fbd0bb07a`, blob `193ea95b0b`) carries exactly this rationale as a comment above the lines. Not a scanner dismissal. |
| `cs/path-combine` | `build/DeterministicRazor/DeterministicRazorGenerator.cs:28` | **Same as line 27.** Byte-identity with the canonical copy is required by Check 15, so no local rewrite is possible either. |
| AI suggestion | `docs/quality/github-fynd-standardfynd-publika-repon-wp5.md:2-8` | **Fixed in `3f49a14`.** The WP5 summary now states that no finding was dismissed in the scanner, that no suppression was added, and that the two shared-file rows were evaluated and retained unchanged by design with the rationale recorded in the table. The "Rejected" wording the suggestion objected to is gone, so the summary is internally consistent. |
| AI suggestion (double `ShouldProcess`) | `scripts/omp/bump-version.ps1` (widget write path) | **Fixed upstream, brought in by this sync** (blob `fa3100dd06`). `Save-JsonFile` no longer confirms; every planned write takes exactly one confirmation in the caller, and a declined file is reported as declined instead of as a write. The old double-prompt behavior is documented in the function comment as the reason for the shape. |
| AI suggestion (`widgetVersion` staleness) | `scripts/omp/bump-version.ps1` (`Set-WidgetFileEntryVersion`) | **Upstream canonical by design; no local edit allowed.** The canonical design normalizes manifest entries toward `widgetVersion` (the reader prefers it, and the package root plus every inner widget are stamped together). This repository's `omp-components.json` carries no `widgetFiles`, so the path is unreached here. A change belongs in OpenModulePlatform, not in a Check-15-held copy. |
| AI suggestion (wait cancellation) | `tests/ODVGateway.Tests/GatewaySessionStoreTests.cs:71` | **Fixed and behavior-tested in `3f49a14`.** `WaitForHandoffLookupKeyAsync_CancelledWhileWaiting_ThrowsOperationCanceledException` starts a 30 s wait with a dedicated `CancellationTokenSource`, cancels it, and asserts `OperationCanceledException` propagates. RED/GREEN evidence is recorded below. |

## RED/GREEN evidence (this job)

Targeted command per row:

```bash
GIT_TERMINAL_PROMPT=0 dotnet test tests/ODVGateway.Tests/ODVGateway.Tests.csproj -c Release --filter 'FullyQualifiedName~<test>' --logger 'console;verbosity=minimal'
```

| Temporary mutation | Test | RED observation | GREEN after restore |
| --- | --- | --- | --- |
| `Task.Delay(..., cancellationToken)` replaced with `CancellationToken.None` in `GatewaySessionStore.WaitForHandoffLookupKeyAsync` | `GatewaySessionStoreTests.WaitForHandoffLookupKeyAsync_CancelledWhileWaiting_ThrowsOperationCanceledException` | exit **1**: `Assert.ThrowsAny()` failure, no exception thrown after the 30 s timeout | `GatewaySessionStoreTests` filter: **10 passed, 0 failed** (full GREEN rerun in local CI below) |

The mutation was restored byte-identical: `git diff --stat` after the restore
lists only the two canonical syncs, and `src/` is untouched. No mutation was
committed.

## Full local CI

```bash
GIT_TERMINAL_PROMPT=0 pwsh -NoProfile -NonInteractive -WindowStyle Hidden -File scripts/local-ci.ps1 -PlatformRepositoryRoot "$OMP_PLATFORM_ROOT"
```

Result: native exit code **0**; Release build **0 warnings / 0 errors**;
**154 unit tests passed, 0 failed, 0 skipped**; smoke **10/10 required checks
passed** with port 5210 (one expected warning for the intentionally unresolved
viewer dist); component/version and strict shared-script validation passed
(both synced files match the canonical copies); **96 Pester tests passed, 0
failed**. Final output `LOCAL CI PASSED`.

No version was bumped: the validator passes on the existing matrix
(repository/component/max `0.1.84`, module definition `0.1.54`, minimum
`0.1.11`). A tooling-script sync plus a comment-only generator change is not
deployable behavior.

## Dismissals

None. Nothing in this round needs a GitHub dismissal: the standard rows are
handled in code or retained by recorded design, and AI findings have no
dismiss control (they clear on rescan after the file changes).
