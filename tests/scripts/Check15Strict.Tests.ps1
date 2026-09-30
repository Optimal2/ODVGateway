<#
.SYNOPSIS
    Runnable checks for how Check 15 (shared script drift) locates the
    OpenModulePlatform checkout and how local CI makes it strict.

.DESCRIPTION
    Check 15 compares this repository's copies of the shared scripts against
    the canonical copies in an OpenModulePlatform checkout. Measured
    2026-09-26: jobs running in git worktrees under another root found no
    sibling checkout, the guard printed NOT VERIFIED with exit 0, and
    shared-script drift was pushed.

    The cases below pin the behaviour that closes that gap:

    1. A platform root that cannot be resolved fails the validator (exit 1
       with the shared resolver's Check 15 error for that root, so an
       unrelated validator error cannot pass it). A root NAMED by
       OMP_PLATFORM_ROOT that is not a checkout fails whether or not
       -Strict is passed.
    2. OMP_PLATFORM_ROOT is honoured and wins over OpenModulePlatformRoot, so
       the comparison actually runs.
    3. -PlatformRepositoryRoot wins over OMP_PLATFORM_ROOT.
    4. scripts/local-ci.ps1 runs the validator with -Strict unless
       -AllowUnverifiedSharedScripts is passed, forwards -PlatformRepositoryRoot,
       and maps -AllowUnverifiedSharedScripts to OMP_ALLOW_MISSING_PLATFORM=1.
    5. scripts/local-ci.ps1 runs this file through the canonical Pester step
       (scripts/omp/run-script-tests.ps1), so a regression in the -Strict
       wiring fails the local gate.

    The platform checkout for cases 2 and 3 is resolved with the shared
    resolver Resolve-PlatformCheckScript (validate-component-versions.helpers.ps1),
    the same one the validator calls, so this file and the validator look in
    the same places in the same order: -PlatformRepositoryRoot,
    OMP_PLATFORM_ROOT, OpenModulePlatformRoot, then the sibling directory
    named OpenModulePlatform. local-ci.ps1 forwards its -PlatformRepositoryRoot
    as OMP_PLATFORM_ROOT.

    Cases 2 and 3 need a real OpenModulePlatform checkout. It is read, never
    written. Without one those cases fail rather than skip, because a check
    that could not run must not read as a passing one; set
    ODVGATEWAY_CHECK15_ALLOW_MISSING_PLATFORM=1 (local-ci.ps1 does so only
    for -AllowUnverifiedSharedScripts) to report them as skipped instead.

    The validator is invoked in-process with the call operator, and the
    environment variables it reads are restored afterwards.

.EXAMPLE
    $env:OMP_PLATFORM_ROOT = 'C:\src\OpenModulePlatform'
    .\scripts\omp\run-script-tests.ps1
#>

BeforeDiscovery {
    . (Join-Path $PSScriptRoot 'Check15Strict.TestHelpers.ps1')
    $platformRoot = Get-Check15PlatformRoot
    # Cases 2 and 3 are skipped only when no checkout exists AND the skip was
    # asked for on purpose; otherwise they run and fail on the missing root.
    $skipPlatformCases = (-not (Test-Check15HasPlatform -PlatformRoot $platformRoot)) -and (Test-Check15AllowMissingPlatform)
}

Describe 'Check 15 strict wiring' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Check15Strict.TestHelpers.ps1')
        $platformRoot = Get-Check15PlatformRoot
        $hasPlatform = Test-Check15HasPlatform -PlatformRoot $platformRoot
        $missingPlatformDetail = "no OpenModulePlatform checkout at '$platformRoot': set OMP_PLATFORM_ROOT or pass -PlatformRepositoryRoot to local-ci.ps1"
        # A path that is guaranteed not to be an OpenModulePlatform checkout.
        $missingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('odvgw-no-omp-' + [guid]::NewGuid().ToString('N'))
        $localCiAst = Get-LocalCiAst
    }

    It 'validator resolves the platform root through the shared resolver' {
        $check15ResolverFound | Should -BeTrue -Because 'Resolve-PlatformCheckScript must exist in validate-component-versions.helpers.ps1'
    }

    # Case 1: a named root that is not a checkout => exit 1 with the shared
    # resolver's Check 15 error for that root. Matching only "Check 15" could
    # pass on an unrelated validator error that happens to coincide with a NOT
    # VERIFIED warning.
    It 'a named platform root that is not a checkout fails' {
        $case1 = Invoke-Validator -Environment @{ OMP_PLATFORM_ROOT = $missingRoot } -Arguments @{ Strict = $true }
        $case1.ExitCode | Should -Be 1 -Because "the validator must fail when OMP_PLATFORM_ROOT '$missingRoot' is not a platform checkout"
        $case1.Output | Should -Match 'Check 15'
        $case1.Output | Should -Match 'does not contain'
        $case1.Output | Should -Match 'OMP_PLATFORM_ROOT'
        $case1.Output | Should -Not -Match 'NOT VERIFIED'
    }

    # Case 2: OMP_PLATFORM_ROOT wins over a bogus OpenModulePlatformRoot.
    It 'OMP_PLATFORM_ROOT runs the comparison' -Skip:$skipPlatformCases {
        $hasPlatform | Should -BeTrue -Because $missingPlatformDetail
        $case2 = Invoke-Validator -Environment @{ OMP_PLATFORM_ROOT = $platformRoot; OpenModulePlatformRoot = $missingRoot } -Arguments @{ Strict = $true }
        Test-ComparisonRan -Output $case2.Output | Should -BeTrue -Because "the canonical comparison must run (exit $($case2.ExitCode))"
    }

    # Case 3: -PlatformRepositoryRoot wins over a bogus OMP_PLATFORM_ROOT.
    It '-PlatformRepositoryRoot wins over OMP_PLATFORM_ROOT' -Skip:$skipPlatformCases {
        $hasPlatform | Should -BeTrue -Because $missingPlatformDetail
        $case3 = Invoke-Validator -Environment @{ OMP_PLATFORM_ROOT = $missingRoot } -Arguments @{ Strict = $true; PlatformRepositoryRoot = $platformRoot }
        Test-ComparisonRan -Output $case3.Output | Should -BeTrue -Because "the canonical comparison must run (exit $($case3.ExitCode))"
    }

    # Case 4: local-ci passes -Strict unless explicitly allowed, forwards the
    # root, and maps -AllowUnverifiedSharedScripts to the shared resolver's
    # OMP_ALLOW_MISSING_PLATFORM exception.
    It 'local-ci runs Check 15 strict by default' {
        $paramNames = @()
        if ($null -ne $localCiAst.ParamBlock) { $paramNames = @($localCiAst.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath }) }
        $paramNames | Should -Contain 'AllowUnverifiedSharedScripts'
        $paramNames | Should -Contain 'PlatformRepositoryRoot'
        $validatorCalls = Get-LocalCiCallText -Ast $localCiAst -VariablePattern 'validatorScript'
        $validatorCalls.Count | Should -BeGreaterThan 0 -Because 'local-ci.ps1 must call the validator'
        $validatorCalls.Text | Should -Match '-Strict:\s*\(\s*-not\s+\$AllowUnverifiedSharedScripts\s*\)'
        $validatorCalls.Text | Should -Match 'PlatformRepositoryRoot'
        $localCiAst.Extent.Text | Should -Match '\$env:OMP_ALLOW_MISSING_PLATFORM\s*=\s*if\s*\(\s*\$AllowUnverifiedSharedScripts\s*\)\s*\{\s*[''"]1[''"]\s*\}'
    }

    # Case 5: local-ci runs this suite through the canonical Pester step,
    # forwards the root as OMP_PLATFORM_ROOT, and allows a missing platform
    # only when -AllowUnverifiedSharedScripts was passed.
    It 'local-ci runs this suite through the canonical Pester step' {
        $text = $localCiAst.Extent.Text
        $text | Should -Match '\$scriptTestsRunner\s*=\s*Join-Path\s+\$scriptDir\s+[''"]omp[/\\]run-script-tests\.ps1[''"]'
        (Get-LocalCiCallText -Ast $localCiAst -VariablePattern 'scriptTestsRunner').Count | Should -BeGreaterThan 0 -Because 'run-script-tests.ps1 must be invoked'
        $text | Should -Match '\$env:OMP_PLATFORM_ROOT\s*=\s*\$PlatformRepositoryRoot'
        $text | Should -Match '\$env:ODVGATEWAY_CHECK15_ALLOW_MISSING_PLATFORM\s*=\s*if\s*\(\s*\$AllowUnverifiedSharedScripts\s*\)\s*\{\s*[''"]1[''"]\s*\}'
    }
}
