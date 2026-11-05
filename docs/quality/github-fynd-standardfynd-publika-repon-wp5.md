# ODVGateway WP5 GitHub standard findings

Base: `e274825107e476867b4f78c1b12976a5d0a32be8`.

Scope: `cs/local-not-disposed`, `cs/empty-catch-block`, and `cs/path-combine` only.
No finding was dismissed and no analyzer suppression was added. The production request,
configuration, authentication, and trusted-source paths were not changed.

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

Line numbers in the result-location column refer to the edited source before subsequent
version-only commits.

| Rule | Scanned location | Result location | Disposition and reason |
| --- | --- | --- | --- |
| `cs/local-not-disposed` | `tests/ODVGateway.Tests/LegacySignatureStartupTests.cs:74` | `:73` | **Fixed.** The transient `/prep` `StringContent` now has a `using` declaration and remains alive for the request. |
| `cs/local-not-disposed` | `tests/ODVGateway.Tests/LegacySignatureStartupTests.cs:487` | `:488` | **Fixed.** `TcpListener` now has a `using` declaration; disposal still occurs immediately after the ephemeral port is obtained. |
| `cs/local-not-disposed` | `tests/ODVGateway.Tests/SourceProxyHttpTests.cs:76` | `:80` | **Rewritten with ownership preserved.** Response creation escapes through `CreateResponse()`. Returning a handler response transfers ownership to `HttpClient`, so disposing it in the stub would be incorrect. The existing three HTTP tests prove the response and stream remain usable. |
| `cs/path-combine` | `tests/ODVGateway.Tests/LegacySignatureStartupTests.cs:323` | `:323` | **Fixed.** The gateway assembly path uses `Path.Join`. |
| `cs/path-combine` | `build/DeterministicRazor/DeterministicRazorGenerator.cs:27` | unchanged `:27` | **Deferred; not silently resolved.** The `Path.Join` rewrite passed its regression but was withdrawn because Check 15 requires this shared file to remain byte-identical to the canonical OpenModulePlatform copy. An ODVGateway-only change makes mandatory local CI fail. The canonical copy must change first, followed by an identical copy here. |
| `cs/path-combine` | `build/DeterministicRazor/DeterministicRazorGenerator.cs:28` | unchanged `:28` | **Deferred for the same shared-file reason as line 27.** No suppression or dismissal was used. |
| `cs/empty-catch-block` | `tests/ODVGateway.Tests/LegacySignatureStartupTests.cs:478` | `:478` | **Fixed.** Malformed health JSON is still classified as not belonging to the child, but the expected parse failure now emits a diagnostic instead of being silent. |
| `cs/empty-catch-block` | `tests/ODVGateway.Tests/LegacySignatureStartupTests.cs:525` | `:519` | **Fixed.** The abandoned probe still observes only cancellation/HTTP failures after its child exits; the observation now emits a diagnostic. |
| `cs/empty-catch-block` | `tests/ODVGateway.Tests/LegacySignatureStartupTests.cs:544` | `:540` | **Fixed.** The existing best-effort child-kill exception filter and cleanup behavior remain, with diagnostics added. |
| `cs/empty-catch-block` | `tests/ODVGateway.Tests/LegacySignatureStartupTests.cs:556` | `:552` | **Fixed.** The existing best-effort process-wait behavior remains, with diagnostics added. |

`tests/ODVGateway.Tests/GitHubQualityWP5Tests.cs` retains three source-level regressions
covering the eight fixed test-code locations. These complement, rather than replace, the six
existing process/HTTP behavior tests. The generator assertion was removed together with the
shared-file edit; it must return when the canonical file is synchronized.

## RED/GREEN evidence

Before the fixes, the existing six targeted behavior tests passed:

```bash
dotnet test tests/ODVGateway.Tests/ODVGateway.Tests.csproj --filter 'FullyQualifiedName~LegacySignatureStartupTests|FullyQualifiedName~SourceProxyHttpTests' --logger 'console;verbosity=normal'
```

Native exit code **0**; **6 passed, 0 failed**.

After adding only `GitHubQualityWP5Tests.cs`, before changing the finding locations:

```bash
GIT_TERMINAL_PROMPT=0 dotnet test tests/ODVGateway.Tests/ODVGateway.Tests.csproj --filter 'FullyQualifiedName~GitHubQualityWP5Tests' --logger 'console;verbosity=normal'
```

Native exit code **1**; **4 failed, 0 passed**. Every new regression failed on the original
source. Three of those four regressions remain in the final suite; the withdrawn fourth was
the shared-generator assertion described above.

The first fixed run, including the then-present generator rewrite, returned native exit code
**0** with **10 passed, 0 failed**. After withdrawing only that shared-file rewrite and its
assertion, the final targeted command was rerun:

```bash
GIT_TERMINAL_PROMPT=0 dotnet test tests/ODVGateway.Tests/ODVGateway.Tests.csproj --filter 'FullyQualifiedName~GitHubQualityWP5Tests|FullyQualifiedName~LegacySignatureStartupTests|FullyQualifiedName~SourceProxyHttpTests' --logger 'console;verbosity=normal'
```

Final native exit code **0**; **9 passed, 0 failed**. This includes all three retained new
regressions and all six existing behavior tests.

`git diff --check` also returned native exit code **0**. All owned pre-existing files retained
LF line endings; the new test and this document use LF.

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

The final run after the withdrawal returned native exit code **0**: Release build with **0
warnings / 0 errors**, **148 unit tests passed**, **10/10 smoke checks passed** (one expected
warning for the intentionally unresolved viewer dist), component/version and shared-script
validation passed, and **96 Pester tests passed**. The final output was `LOCAL CI PASSED`.
