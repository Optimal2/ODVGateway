# ODVGateway WP5 GitHub standard findings

Base: `e274825107e476867b4f78c1b12976a5d0a32be8`.

Scope: `cs/local-not-disposed`, `cs/empty-catch-block`, and `cs/path-combine` only.
No finding was dismissed in the scanner and no analyzer suppression was added. Two
shared-file `cs/path-combine` rows were evaluated and intentionally retained unchanged
by design, with the rationale recorded in the dispositions below; that evaluation is
not a scanner dismissal. The production request, configuration, authentication, and
trusted-source paths were not changed.

## Before counts

The supplied 2026-11-05 standard-findings report measured ten open findings:

| Rule | Count | Severity | Owned locations |
| --- | ---: | --- | --- |
| `cs/empty-catch-block` | 4 | Note | `LegacySignatureStartupTests.cs` scanned lines 478, 525, 544, 556 |
| `cs/local-not-disposed` | 3 | Warning | `LegacySignatureStartupTests.cs` scanned lines 74, 487; `SourceProxyHttpTests.cs` scanned line 76 |
| `cs/path-combine` | 3 | Note | `LegacySignatureStartupTests.cs` scanned line 323; `DeterministicRazorGenerator.cs` scanned lines 27, 28 |

A local CodeQL rerun was attempted with:

```bash
GIT_TERMINAL_PROMPT=0 python codeql_lokal.py <repository> cs/empty-catch-block cs/local-not-disposed cs/path-combine --language csharp --timeout 240
```

It returned native exit code **2**, meaning unmeasured: no usable CodeQL CLI was installed and
the tool deliberately did not download one. This was not interpreted as zero findings.

## Per-finding dispositions

Result locations below name the final method and its line in this revision. The scanned locations
are retained to identify the original findings. The first two rows are the new warnings measured
by the fresh read-only sweep on `1370fe022cc0c52c2df5d520d0f72b2e8705f225`; the remaining rows
preserve the original WP5 dispositions.

| Rule | Scanned location | Final result location | Disposition and reason |
| --- | --- | --- | --- |
| `cs/local-not-disposed` | `tests/ODVGateway.Tests/LegacySignatureStartupBehaviorTests.cs:23` | `LegacySignatureStartupBehaviorTests.cs:142`, `CreateControlledResponse` | **Fixed with explicit escaping-response ownership and behavior-tested.** Both controlled handlers now return responses from one ownership-returning factory instead of allocating them inline. The `/prep` response remains live after `PostPrepAsync` returns and after its `HttpClient` is disposed; the test reads its body, disposes it as the consumer, and observes the tracked disposal. Neither the factory nor the handler disposes an escaping response. |
| `cs/local-not-disposed` | `tests/ODVGateway.Tests/LegacySignatureStartupBehaviorTests.cs:54` | `LegacySignatureStartupBehaviorTests.cs:142`, `CreateControlledResponse`; consumed at `LegacySignatureStartupTests.cs:470` | **Fixed by the same explicit ownership transfer and behavior-tested.** The health handler returns the factory response to `ProbeHealthAsync`, whose existing `using` is the real consuming disposal path. `MalformedHealthJson_IsDiagnosticAndNotChild` still verifies the malformed-JSON behavior and now also observes that this exact tracked response is disposed. |
| `cs/local-not-disposed` | `tests/ODVGateway.Tests/LegacySignatureStartupTests.cs:74` | `LegacySignatureStartupBehaviorTests.cs:135`, `PostPrepAsync` | **Fixed and behavior-tested.** The transient `/prep` `StringContent` remains alive for the request and is disposed when the post completes. `PrepRequestContent_IsDisposedAfterPost` observes the content captured by a controlled handler and proves that reading it afterward throws `ObjectDisposedException`. |
| `cs/local-not-disposed` | `tests/ODVGateway.Tests/LegacySignatureStartupTests.cs:487` | `LegacySignatureStartupTests.cs:492`, `GetFreeLoopbackPort` | **Fixed and behavior-tested.** `FreeLoopbackPort_CanBeReboundAfterProbe` exclusively rebinds the returned port, proving that the temporary listener no longer owns it. |
| `cs/local-not-disposed` | `tests/ODVGateway.Tests/SourceProxyHttpTests.cs:76` | `SourceProxyHttpTests.cs:106`, `CreateResponse` | **Rewritten with ownership preserved and corrected rationale.** The consuming caller owns and disposes the response; `HttpClient` does not dispose it. `EscapingHandlerResponse_RemainsUsableUntilConsumerDisposesIt` disposes the client, reads the still-live response, then disposes the response and observes both response and stream disposal. This file is unchanged by the recovery. |
| `cs/path-combine` | `tests/ODVGateway.Tests/LegacySignatureStartupTests.cs:323` | `LegacySignatureStartupTests.cs:322`, `BootGatewayAsync` | **Fixed.** The gateway assembly path uses `Path.Join`. The three preserved process-boot tests exercise this path by starting the built gateway. |
| `cs/path-combine` | `build/DeterministicRazor/DeterministicRazorGenerator.cs:27` | unchanged `:27` | **Retained unchanged by design; correct by design, not a scanner dismissal.** The second operand is a constant relative DLL filename beneath the trusted compiler directory, so it cannot be rooted and `Path.Combine` cannot discard the base. `Path.Join` adds no clarity here. A local rewrite would also break byte-identity with the canonical OpenModulePlatform copy (measured Check 15 drift failure). No rewrite, suppression, or follow-up is warranted. |
| `cs/path-combine` | `build/DeterministicRazor/DeterministicRazorGenerator.cs:28` | unchanged `:28` | **Retained unchanged for the same reason as line 27; not a scanner dismissal.** The file remains byte-identical by design, not deferred work. |
| `cs/empty-catch-block` | `tests/ODVGateway.Tests/LegacySignatureStartupTests.cs:478` | `LegacySignatureStartupTests.cs:482`, `ProbeHealthAsync` | **Fixed and behavior-tested.** Malformed health JSON is classified as not belonging to the child and emits a diagnostic. `MalformedHealthJson_IsDiagnosticAndNotChild` supplies invalid JSON through a controlled handler and asserts both outcomes. |
| `cs/empty-catch-block` | `tests/ODVGateway.Tests/LegacySignatureStartupTests.cs:525` | `LegacySignatureStartupTests.cs:524`, `ObserveAbandonedProbeAsync` | **Fixed and behavior-tested.** Controlled cancellation and HTTP tasks produce diagnostics; an unexpected `InvalidOperationException` still propagates without a diagnostic. |
| `cs/empty-catch-block` | `tests/ODVGateway.Tests/LegacySignatureStartupTests.cs:544` | `LegacySignatureStartupTests.cs:546`, `KillAndDrain` kill boundary | **Fixed and behavior-tested.** An unstarted, owned, harmless `Process` produces the filtered `InvalidOperationException`; the test observes the kill diagnostic without starting or targeting any external process. |
| `cs/empty-catch-block` | `tests/ODVGateway.Tests/LegacySignatureStartupTests.cs:556` | `LegacySignatureStartupTests.cs:559`, `KillAndDrain` wait boundary | **Fixed and behavior-tested.** The same unstarted owned process reaches the wait boundary and produces its diagnostic. |

The three source-string mirrors in `GitHubQualityWP5Tests.cs` were removed. Seven focused
startup-fixture cases and one response-ownership case test behavior directly alongside the six
preserved process/HTTP behavior tests. The recovery extends two existing cases rather than adding
source mirrors, a parser, a dependency, or redundant test cases. The withdrawn generator source
assertion is not part of final coverage.

## RED/GREEN evidence

### Original WP5 proofs at 1370fe02

The original WP5 run measured the six pre-existing behavior tests green, then four source-level
assertions red on the original source, then nine targeted tests green. Those source assertions
are historical evidence only: three were brittle mirrors and the fourth was the withdrawn
shared-generator assertion. None is claimed as final coverage.

For the original behavioral follow-up, each property was mutated independently. Every mutation
produced a real test failure with native exit code **1**, after which the original file bytes were
restored. A byte comparison returned native exit code **0** after every row. No mutation was
committed. These eight causal proofs are preserved here as historical evidence:

| Temporary mutation | Fully qualified test filter suffix | RED observation |
| --- | --- | --- |
| Removed the malformed-JSON diagnostic | `ODVGateway.Tests.LegacySignatureStartupTests.MalformedHealthJson_IsDiagnosticAndNotChild` | 1 failed: `Assert.Single`, diagnostic collection empty |
| Removed the filtered abandoned-probe diagnostic | `ODVGateway.Tests.LegacySignatureStartupTests.AbandonedProbeExpectedFailures_AreDiagnostic` | 2 failed (HTTP and cancellation): `Assert.Single`, diagnostic collection empty |
| Removed the abandoned-probe exception filter | `ODVGateway.Tests.LegacySignatureStartupTests.AbandonedProbeUnexpectedFailure_Propagates` | 1 failed: `Assert.Throws`, no `InvalidOperationException` escaped |
| Removed the filtered kill diagnostic | `ODVGateway.Tests.LegacySignatureStartupTests.UnstartedOwnedProcess_CleanupReportsKillAndWaitErrors` | 1 failed: expected 2 diagnostics, actual 1 |
| Removed the unfiltered wait diagnostic | `ODVGateway.Tests.LegacySignatureStartupTests.UnstartedOwnedProcess_CleanupReportsKillAndWaitErrors` | 1 failed: expected 2 diagnostics, actual 1 |
| Removed `/prep` content disposal | `ODVGateway.Tests.LegacySignatureStartupTests.PrepRequestContent_IsDisposedAfterPost` | 1 failed: `Assert.Throws`, no `ObjectDisposedException` |
| Removed temporary listener disposal | `ODVGateway.Tests.LegacySignatureStartupTests.FreeLoopbackPort_CanBeReboundAfterProbe` | 1 failed: `Assert.Null` observed socket error 10048 on exclusive rebind |
| Disposed the handler response before returning it | `ODVGateway.Tests.SourceProxyHttpTests.EscapingHandlerResponse_RemainsUsableUntilConsumerDisposesIt` | 1 failed: `Assert.False`, response was already disposed |

The historical restored files at `1370fe022cc0c52c2df5d520d0f72b2e8705f225`, independently
read back from Git during this recovery, had these SHA-256 hashes:

```text
957161a914ef021896d7ec6881bb60158412ff5b62073fafdeab019a65d7f4ce  LegacySignatureStartupTests.cs
9ddc28f6da5f87302925dc6db88e3afa672a06aca38e593a0d4c279be9a81cbf  LegacySignatureStartupBehaviorTests.cs
0e288e354ae54fdd8316a46e0226c1d4ec3eb8da42f5d693fc5bdab2fad436d3  SourceProxyHttpTests.cs
```

### Recovery proofs for the two new warnings

The fresh worktree first required `dotnet restore tests/ODVGateway.Tests/ODVGateway.Tests.csproj`,
which returned native exit code **0**. A pre-restore `--no-restore` invocation emitted no test
summary and is excluded as unmeasured rather than claimed as GREEN. Each measured row below then
used:

```bash
GIT_TERMINAL_PROMPT=0 dotnet test tests/ODVGateway.Tests/ODVGateway.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName=<fully-qualified-test-from-the-row>' --logger 'console;verbosity=minimal'
```

The two affected original properties and the new response-ownership properties were mutated
independently. Every row returned native exit code **1** with **1 failed, 0 passed, 0 skipped**.
The exact intended bytes were restored after every row and their SHA-256 was measured before the
next mutation. No mutation was committed.

| Temporary mutation | Fully qualified test filter | Actual RED observation |
| --- | --- | --- |
| Removed `/prep` request-content disposal | `ODVGateway.Tests.LegacySignatureStartupTests.PrepRequestContent_IsDisposedAfterPost` | `Assert.Throws` failed because no `ObjectDisposedException` was thrown. |
| Disposed the controlled response in the factory before the handler returned it | `ODVGateway.Tests.LegacySignatureStartupTests.PrepRequestContent_IsDisposedAfterPost` | `HttpClient.PostAsync` threw `ObjectDisposedException` for the disposed `StringContent`; this is an additional boundary failure, not the assertion proof in the next row. |
| Disposed the escaping response in `PostPrepAsync` after the post and before returning it | `ODVGateway.Tests.LegacySignatureStartupTests.PrepRequestContent_IsDisposedAfterPost` | `Assert.False` failed: expected a live response, actual `IsDisposed=True`. |
| Removed the malformed-JSON diagnostic | `ODVGateway.Tests.LegacySignatureStartupTests.MalformedHealthJson_IsDiagnosticAndNotChild` | `Assert.Single` failed because the diagnostic collection was empty. |
| Removed `ProbeHealthAsync` response disposal | `ODVGateway.Tests.LegacySignatureStartupTests.MalformedHealthJson_IsDiagnosticAndNotChild` | `Assert.True` failed: the consumed response's actual `IsDisposed=False`. |

The final restored hashes, distinct from the historical behavior-file hash above, are:

```text
957161a914ef021896d7ec6881bb60158412ff5b62073fafdeab019a65d7f4ce  LegacySignatureStartupTests.cs (unchanged from 1370fe02)
fc4af56993d9bf2a9f3143fa3df7d31823142f66e2c857e884dd2be656275251  LegacySignatureStartupBehaviorTests.cs (final recovery bytes)
0e288e354ae54fdd8316a46e0226c1d4ec3eb8da42f5d693fc5bdab2fad436d3  SourceProxyHttpTests.cs (unchanged from 1370fe02)
```

After the final restoration, the targeted command was:

```bash
GIT_TERMINAL_PROMPT=0 dotnet test tests/ODVGateway.Tests/ODVGateway.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~LegacySignatureStartupTests|FullyQualifiedName~SourceProxyHttpTests' --logger 'console;verbosity=minimal'
```

Final native exit code **0**; **14 passed, 0 failed, 0 skipped**. This includes the six
preserved behavior tests and the eight focused behavioral cases, two of them extended by this
recovery. `git diff --check` returned native exit code **0**. All owned files retain LF line
endings.

## Full local CI

The required command is:

```bash
GIT_TERMINAL_PROMPT=0 pwsh -NoProfile -NonInteractive -WindowStyle Hidden -File scripts/local-ci.ps1 -PlatformRepositoryRoot "$OMP_PLATFORM_ROOT"
```

The first run returned native exit code **1** solely because Check 15 detected the deliberate
experiment with the shared generator. Its other gates all passed: Release build with **0
warnings / 0 errors**, **149 unit tests**, **10/10 smoke checks**, and **96 Pester tests**.
That measured failure caused the generator rewrite to be withdrawn; no failing shared-file
drift was committed or published.

The original WP5 run after the generator withdrawal returned native exit code **0** with **148
unit tests passed**. After the original behavioral follow-up replaced the source mirrors, the same
full local CI command returned native exit code **0** with **153 unit tests passed**.

This recovery reran the required full command with the read-only OpenModulePlatform checkout as
`OMP_PLATFORM_ROOT`. It returned native exit code **0**: Release build with **0 warnings / 0
errors**, **153 unit tests passed, 0 failed, 0 skipped**, **10/10 smoke checks passed** (one
expected warning for the intentionally unresolved viewer dist), component/version and strict
shared-script validation passed, and **96 Pester tests passed, 0 failed, 0 skipped**. The final
output was `LOCAL CI PASSED`.

Only test-fixture and evidence files changed, so no version was bumped again. The existing matrix
remains coherent and passed the full validator without hand editing: repository/component/max
`0.1.84`, module definition `0.1.54`, and minimum `0.1.11`.
