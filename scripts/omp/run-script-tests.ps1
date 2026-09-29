#Requires -Version 5.1
<#
.SYNOPSIS
    Runs the OMP Pester script test suites (tests/*.Tests.ps1) and returns a
    pass/fail exit code for use as a blocking gate.

.DESCRIPTION
    Canonical entry point for the script test suites, used by both the local
    pre-push gate (.githooks/pre-push.ps1) and GitHub CI
    (.github/workflows/ci.yml).

    The suites use the Pester 6 dialect ('Should -Be' etc., unchanged from
    Pester 5); the Pester 3.4.0 legacy dialect ('Should Be') was removed in
    Pester 5, and the suites were migrated off it in 2026-09. Pester 3.4.0
    ships inbox with Windows PowerShell 5.1 but is EOL, so the module is
    pinned to an explicit 6.x version instead of floating to whatever a
    machine happens to carry. Callers invoke this runner via powershell.exe
    so the parent and spawned child processes share the same engine: the
    suites stay on Windows PowerShell 5.1 because one suite spawns child
    powershell.exe processes as a Windows requirement -- it is the Pester
    MODULE version that is pinned, not the engine requirement. Pester 6.1.0
    ships a net462 binary and runs on Windows PowerShell 5.1 (verified
    2026-09-29: all suites green on both powershell.exe 5.1 and pwsh).

    Shared per-suite harness code lives in tests/*.TestHelpers.ps1 and is
    dot-sourced from each Describe block's BeforeAll, because Pester 6 runs
    every container in a separate session state where file-scope functions
    and variables are not visible.

    Invoke-Pester never sets $LASTEXITCODE, so the explicit exits below ARE
    the contract, and every gate prints a line starting with 'GATE FAIL:'.
    FailedCount alone is not enough: it only counts tests that RAN and
    failed. The runner is the canonical Pester step for every OMP-compatible
    repository (copied verbatim together with pester-bootstrap.ps1 and held
    identical by Check 15, validate-shared-scripts.ps1), so it carries every
    guard the fleet's copies had between them:

      1. Exactly Pester 6.1.0, imported by full path from the
         repository-local cache and verified by ModuleBase and by the loaded
         Pester.dll version -- never "the first Pester that loaded".
      2. A Pester.dll of another version already loaded in the process is
         refused up front with an instruction to start a new process; a
         loaded .NET assembly cannot be unloaded.
      3. The suite inventory is recursive, like Pester's own discovery: a
         *.Tests.ps1 in a subdirectory runs and is counted.
      4. Every *.ps1 under the tests path must be a suite (*.Tests.ps1) or a
         helper (*.TestHelpers.ps1). Anything else -- typically a suite
         renamed away from the glob, such as Foo.Test.ps1 -- fails the run
         instead of silently no longer running.
      5. The containers that ran must be exactly the suite files found (by
         path, not just by count).
      6. Every container must run at least one test: an emptied Describe or a
         suite whose tests were removed is reported as NotRun and would
         otherwise pass next to green suites.
      7. The overall result must be 'Passed' (covers discovery and container
         failures), at least one test must pass, and FailedCount must be 0.

.EXAMPLE
    powershell.exe -NoProfile -File scripts/omp/run-script-tests.ps1
#>
[CmdletBinding()]
param(
    # Test-suite directory; defaults to the repository's tests folder. Exists
    # so the zero-execution gate itself can be exercised against a directory
    # whose suites discover no tests (tests/PesterBootstrap.Tests.ps1)
    # without touching the real suites.
    [Parameter(Mandatory = $false)]
    [string] $TestsPath = ''
)

$ErrorActionPreference = 'Stop'

# Pin Pester 6.1.0 explicitly: the suites use the Pester 6 dialect, and
# Windows carries Pester 3.4.0 inbox in the module path, so auto-load could
# silently pick the wrong version and fail every suite with
# CommandNotFoundException ('Should -Be' does not exist in Pester 3.4). The
# pin is satisfied from the repository-local module cache (<repoRoot>/
# .psmodules, gitignored): pester-bootstrap.ps1 restores the exact version
# from PSGallery when the cache is empty and prepends the cache to THIS
# PROCESS's module path only, so neither a missing nor a diverging global
# Pester installation can affect the run.
$script:RequiredPesterVersion = '6.1.0'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $PSScriptRoot 'pester-bootstrap.ps1')

# Guard 2: a Pester.dll of another version is already loaded in this process.
$conflict = Get-IncompatiblePesterAssembly -RequiredVersion $script:RequiredPesterVersion
if ($conflict) {
    Write-Host ('GATE FAIL: ' + (Get-IncompatiblePesterMessage -Conflict $conflict -RequiredVersion $script:RequiredPesterVersion)) -ForegroundColor Red
    exit 1
}

# Guard 1: exactly the pinned Pester, from the cache, by ModuleBase.
$pesterModulePath = Ensure-PinnedPester -RequiredVersion $script:RequiredPesterVersion -CacheRoot (Join-Path $repoRoot '.psmodules')
Import-Module (Join-Path $pesterModulePath 'Pester.psd1') -Force
$expectedModuleBase = [System.IO.Path]::GetFullPath($pesterModulePath).TrimEnd('\', '/')
$loadedPester = Get-Module -Name Pester | Where-Object {
    [System.IO.Path]::GetFullPath($_.ModuleBase).TrimEnd('\', '/') -ieq $expectedModuleBase
} | Select-Object -First 1
if (-not $loadedPester -or $loadedPester.Version.ToString() -ne $script:RequiredPesterVersion) {
    $loadedText = 'nothing'
    if ($loadedPester) {
        $loadedText = $loadedPester.Version.ToString()
    }
    Write-Host "GATE FAIL: expected Pester $script:RequiredPesterVersion from $pesterModulePath but loaded '$loadedText'." -ForegroundColor Red
    exit 1
}
$conflict = Get-IncompatiblePesterAssembly -RequiredVersion $script:RequiredPesterVersion
if ($conflict) {
    Write-Host ('GATE FAIL: after importing Pester ' + $script:RequiredPesterVersion + ', ' + $conflict + '; the pinned module would run on a foreign Pester.dll.') -ForegroundColor Red
    exit 1
}

$testsPath = $TestsPath
if ([string]::IsNullOrWhiteSpace($testsPath)) {
    $testsPath = Join-Path $repoRoot 'tests'
}
$testsPath = [System.IO.Path]::GetFullPath($testsPath)

# Guards 3 and 4: the inventory. Recursive, like Pester's discovery. The
# extension is re-checked because -Filter '*.ps1' also matches longer
# extensions on Windows (for example .ps1xml).
$scriptFiles = @(Get-ChildItem -LiteralPath $testsPath -Filter '*.ps1' -File -Recurse |
    Where-Object { $_.Extension -ieq '.ps1' })
$suiteFiles = @($scriptFiles | Where-Object { $_.Name -like '*.Tests.ps1' })
$strayFiles = @($scriptFiles | Where-Object { $_.Name -notlike '*.Tests.ps1' -and $_.Name -notlike '*.TestHelpers.ps1' })
if ($strayFiles.Count -gt 0) {
    Write-Host "GATE FAIL: $($strayFiles.Count) script(s) under $testsPath are neither a suite (*.Tests.ps1) nor a helper (*.TestHelpers.ps1), so Pester would silently skip them:" -ForegroundColor Red
    foreach ($stray in $strayFiles) {
        Write-Host "  - $($stray.FullName)" -ForegroundColor Red
    }
    Write-Host 'Rename a suite back to *.Tests.ps1, or a dot-sourced helper to *.TestHelpers.ps1.' -ForegroundColor Red
    exit 1
}
if ($suiteFiles.Count -eq 0) {
    Write-Host "GATE FAIL: no *.Tests.ps1 suite files under $testsPath." -ForegroundColor Red
    exit 1
}

$results = Invoke-Pester -Path $testsPath -PassThru

if ($results.Result -ne 'Passed') {
    Write-Host "GATE FAIL: overall Pester result is '$($results.Result)', not 'Passed' (a container failed before or during its run)." -ForegroundColor Red
    exit 1
}
if ($results.PassedCount -eq 0) {
    Write-Host "GATE FAIL: Pester ran 0 passing tests. A green exit from zero assertions proves nothing." -ForegroundColor Red
    exit 1
}

# Guard 5: the containers that ran are exactly the suite files found.
$ranPaths = @(@($results.Containers) | ForEach-Object {
    $item = $_.Item
    if ($item -is [System.IO.FileSystemInfo]) {
        $item.FullName
    }
    else {
        [System.IO.Path]::GetFullPath([string]$item)
    }
})
$suitePaths = @($suiteFiles | ForEach-Object { $_.FullName })
$notRun = @($suitePaths | Where-Object { $ranPaths -notcontains $_ })
$unexpected = @($ranPaths | Where-Object { $suitePaths -notcontains $_ })
if ($notRun.Count -gt 0 -or $unexpected.Count -gt 0 -or $ranPaths.Count -ne $suitePaths.Count) {
    Write-Host "GATE FAIL: $($suitePaths.Count) suite file(s) match *.Tests.ps1 under $testsPath, but $($ranPaths.Count) container(s) ran." -ForegroundColor Red
    foreach ($path in $notRun) {
        Write-Host "  - found but did not run: $path" -ForegroundColor Red
    }
    foreach ($path in $unexpected) {
        Write-Host "  - ran but was not found: $path" -ForegroundColor Red
    }
    exit 1
}

# Guard 6: every container ran at least one test.
$emptyContainers = @(@($results.Containers) | Where-Object { $_.TotalCount -eq 0 })
if ($emptyContainers.Count -gt 0) {
    Write-Host "GATE FAIL: $($emptyContainers.Count) suite file(s) ran zero tests (an emptied Describe, or tests removed or renamed away):" -ForegroundColor Red
    foreach ($container in $emptyContainers) {
        Write-Host "  - $($container.Item)" -ForegroundColor Red
    }
    exit 1
}

if ($results.FailedCount -gt 0) {
    exit 1
}
exit 0
