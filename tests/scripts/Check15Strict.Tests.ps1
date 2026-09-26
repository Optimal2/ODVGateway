<#
.SYNOPSIS
    Runnable checks for how Check 15 (shared script drift) locates the
    OpenModulePlatform checkout and how local CI makes it strict.

.DESCRIPTION
    Check 15 compares this repository's copies of the shared scripts against
    the canonical copies in an OpenModulePlatform checkout. Measured
    2026-09-26: jobs running in git worktrees under another root found no
    sibling checkout, local CI ran Check 15 without -Strict, the guard printed
    NOT VERIFIED with exit 0, and shared-script drift was pushed.

    The cases below pin the behaviour that closes that gap:

    1. A platform root that cannot be resolved fails the validator under
       -Strict (exit 1 with the exact Check 15 "canonical script not found"
       error for that root, so an unrelated validator error cannot pass it).
    2. OMP_PLATFORM_ROOT is honoured and wins over OpenModulePlatformRoot, so
       the comparison actually runs.
    3. -PlatformRepositoryRoot wins over OMP_PLATFORM_ROOT.
    4. scripts/local-ci.ps1 runs the validator with -Strict unless
       -AllowUnverifiedSharedScripts is passed, and forwards
       -PlatformRepositoryRoot.
    5. scripts/local-ci.ps1 runs this file as a step, so a regression in the
       -Strict wiring fails the local gate.

    The platform checkout for cases 2 and 3 is resolved with the validator's
    own Resolve-Check15PlatformRoot function, taken from its syntax tree
    rather than copied, so this file and the validator look in the same
    places in the same order: -PlatformRepositoryRoot, OMP_PLATFORM_ROOT,
    OpenModulePlatformRoot, then the sibling directory named
    OpenModulePlatform.

    Cases 2 and 3 need a real OpenModulePlatform checkout. It is read, never
    written. Without one those cases fail rather than skip, because a check
    that could not run must not read as a passing one; pass
    -AllowMissingPlatform to report them as skipped instead.

    The validator is invoked in-process with the call operator, and the
    environment variables it reads are restored afterwards.

.PARAMETER PlatformRepositoryRoot
    A valid OpenModulePlatform checkout. Defaults to $env:OMP_PLATFORM_ROOT,
    then $env:OpenModulePlatformRoot, then the sibling directory named
    OpenModulePlatform (the validator's resolution order).

.PARAMETER AllowMissingPlatform
    Report cases 2 and 3 as skipped when no platform checkout is available.

.EXAMPLE
    $env:OMP_PLATFORM_ROOT = 'C:\src\OpenModulePlatform'
    .\tests\scripts\Check15Strict.Tests.ps1
#>
[CmdletBinding()]
param(
    [string]$PlatformRepositoryRoot = '',
    [switch]$AllowMissingPlatform
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$validatorScript = Join-Path $repoRoot 'scripts\omp\validate-component-versions.ps1'
$localCiScript = Join-Path $repoRoot 'scripts\local-ci.ps1'

$results = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param([string]$Name, [string]$Outcome, [string]$Detail = '')
    $results.Add([pscustomobject]@{ Name = $Name; Outcome = $Outcome; Detail = $Detail })
}

# Reuse the validator's own platform-root resolution instead of a copy of it:
# the function definition is taken from the validator's syntax tree and
# defined here, without running the validator itself.
$tokens = $null
$parseErrors = $null
$validatorAst = [System.Management.Automation.Language.Parser]::ParseFile($validatorScript, [ref]$tokens, [ref]$parseErrors)
$resolverAst = $validatorAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Resolve-Check15PlatformRoot'
}, $true)
if ($null -eq $resolverAst) {
    Add-Result 'validator resolves the platform root in one function' 'FAIL' 'Resolve-Check15PlatformRoot not found in validate-component-versions.ps1'
}
else {
    . ([scriptblock]::Create($resolverAst.Extent.Text))
    $PlatformRepositoryRoot = Resolve-Check15PlatformRoot -PlatformRepositoryRoot $PlatformRepositoryRoot -RepositoryRoot $repoRoot
}
$hasPlatform = -not [string]::IsNullOrWhiteSpace($PlatformRepositoryRoot) -and
    (Test-Path -LiteralPath (Join-Path $PlatformRepositoryRoot 'scripts\omp\validate-shared-scripts.ps1') -PathType Leaf)

# A path that is guaranteed not to be an OpenModulePlatform checkout.
$missingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('odvgw-no-omp-' + [guid]::NewGuid().ToString('N'))

function Invoke-Validator {
    param([hashtable]$Environment, [hashtable]$Arguments)

    $names = @('OMP_PLATFORM_ROOT', 'OpenModulePlatformRoot')
    $saved = @{}
    foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    try {
        foreach ($name in $names) {
            $value = if ($Environment.ContainsKey($name)) { $Environment[$name] } else { $null }
            [Environment]::SetEnvironmentVariable($name, $value, 'Process')
        }
        $global:LASTEXITCODE = 0
        # One line per record, joined without Out-String so a long message is
        # not wrapped at the console width before it is matched.
        $output = (& $validatorScript @Arguments *>&1 | ForEach-Object { "$_" }) -join "`n"
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }
    catch {
        return [pscustomobject]@{ ExitCode = -1; Output = "threw: $($_.Exception.Message)" }
    }
    finally {
        foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    }
}

function Test-ComparisonRan {
    param([string]$Output)
    return ($Output -match "Shared scripts: '[^']+' matches the canonical copy" -or
        $Output -match 'Shared script drift detected') -and
        ($Output -notmatch 'Check 15: NOT VERIFIED') -and
        ($Output -notmatch 'Shared scripts: NOT VERIFIED - the OpenModulePlatform checkout')
}

# Case 1: no resolvable root + -Strict => exit 1 with the exact Check 15 error
# for that root. Matching only "Check 15" could pass on an unrelated validator
# error that happens to coincide with a NOT VERIFIED warning.
$case1 = Invoke-Validator -Environment @{ OMP_PLATFORM_ROOT = $missingRoot } -Arguments @{ Strict = $true }
$case1Script = Join-Path $missingRoot 'scripts\omp\validate-shared-scripts.ps1'
$case1Expected = [regex]::Escape("Check 15: canonical script not found at '$case1Script'; shared script drift could not be checked") +
    '.*' + [regex]::Escape('Strict mode treats a guard that could not run as an error.')
if ($case1.ExitCode -eq 1 -and $case1.Output -match $case1Expected -and $case1.Output -notmatch 'Check 15: NOT VERIFIED') {
    Add-Result 'no platform root + -Strict fails' 'PASS'
}
else {
    Add-Result 'no platform root + -Strict fails' 'FAIL' "exit $($case1.ExitCode); expected 1 with the Check 15 'canonical script not found' error for '$missingRoot'"
}

if ($hasPlatform) {
    # Case 2: OMP_PLATFORM_ROOT wins over a bogus OpenModulePlatformRoot.
    $case2 = Invoke-Validator -Environment @{ OMP_PLATFORM_ROOT = $PlatformRepositoryRoot; OpenModulePlatformRoot = $missingRoot } -Arguments @{ Strict = $true }
    if (Test-ComparisonRan -Output $case2.Output) {
        Add-Result 'OMP_PLATFORM_ROOT runs the comparison' 'PASS' "exit $($case2.ExitCode)"
    }
    else {
        Add-Result 'OMP_PLATFORM_ROOT runs the comparison' 'FAIL' "exit $($case2.ExitCode); the canonical comparison did not run"
    }

    # Case 3: -PlatformRepositoryRoot wins over a bogus OMP_PLATFORM_ROOT.
    $case3 = Invoke-Validator -Environment @{ OMP_PLATFORM_ROOT = $missingRoot } -Arguments @{ Strict = $true; PlatformRepositoryRoot = $PlatformRepositoryRoot }
    if (Test-ComparisonRan -Output $case3.Output) {
        Add-Result '-PlatformRepositoryRoot wins over OMP_PLATFORM_ROOT' 'PASS' "exit $($case3.ExitCode)"
    }
    else {
        Add-Result '-PlatformRepositoryRoot wins over OMP_PLATFORM_ROOT' 'FAIL' "exit $($case3.ExitCode); the canonical comparison did not run"
    }
}
else {
    $outcome = if ($AllowMissingPlatform) { 'SKIP' } else { 'FAIL' }
    $detail = "no OpenModulePlatform checkout at '$PlatformRepositoryRoot': set OMP_PLATFORM_ROOT or pass -PlatformRepositoryRoot"
    Add-Result 'OMP_PLATFORM_ROOT runs the comparison' $outcome $detail
    Add-Result '-PlatformRepositoryRoot wins over OMP_PLATFORM_ROOT' $outcome $detail
}

# Case 4: local-ci passes -Strict unless explicitly allowed, and forwards the root.
$ast = [System.Management.Automation.Language.Parser]::ParseFile($localCiScript, [ref]$tokens, [ref]$parseErrors)
$paramNames = @()
if ($null -ne $ast.ParamBlock) { $paramNames = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath }) }
$validatorCalls = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.CommandElements.Count -gt 0 -and
        $node.CommandElements[0].Extent.Text -match 'validatorScript'
}, $true))
$callText = ($validatorCalls | ForEach-Object { $_.Extent.Text }) -join "`n"
$case4Problems = @()
if ($paramNames -notcontains 'AllowUnverifiedSharedScripts') { $case4Problems += 'no -AllowUnverifiedSharedScripts parameter' }
if ($paramNames -notcontains 'PlatformRepositoryRoot') { $case4Problems += 'no -PlatformRepositoryRoot parameter' }
if ($validatorCalls.Count -eq 0) { $case4Problems += 'validator call not found' }
elseif ($callText -notmatch '-Strict:\s*\(\s*-not\s+\$AllowUnverifiedSharedScripts\s*\)') { $case4Problems += 'validator is not called with -Strict:(-not $AllowUnverifiedSharedScripts)' }
elseif ($callText -notmatch 'PlatformRepositoryRoot') { $case4Problems += 'validator call does not forward -PlatformRepositoryRoot' }
if ($case4Problems.Count -eq 0) {
    Add-Result 'local-ci runs Check 15 strict by default' 'PASS'
}
else {
    Add-Result 'local-ci runs Check 15 strict by default' 'FAIL' ($case4Problems -join '; ')
}

# Case 5: local-ci runs this file as a step, forwards the root, and allows a
# missing platform only when -AllowUnverifiedSharedScripts was passed.
$testCalls = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.CommandElements.Count -gt 0 -and
        $node.CommandElements[0].Extent.Text -match 'check15TestScript'
}, $true))
$testCallText = ($testCalls | ForEach-Object { $_.Extent.Text }) -join "`n"
$case5Problems = @()
if ($ast.Extent.Text -notmatch '\$check15TestScript\s*=\s*Join-Path\s+\$repoRoot\s+[''"]tests[/\\]scripts[/\\]Check15Strict\.Tests\.ps1[''"]') {
    $case5Problems += '$check15TestScript is not set to tests/scripts/Check15Strict.Tests.ps1'
}
if ($testCalls.Count -eq 0) { $case5Problems += 'Check15Strict.Tests.ps1 is not invoked' }
elseif ($testCallText -notmatch 'PlatformRepositoryRoot') { $case5Problems += 'the test call does not forward -PlatformRepositoryRoot' }
elseif ($testCallText -notmatch '-AllowMissingPlatform:\s*\$AllowUnverifiedSharedScripts') { $case5Problems += 'the test call is not made with -AllowMissingPlatform:$AllowUnverifiedSharedScripts' }
if ($case5Problems.Count -eq 0) {
    Add-Result 'local-ci runs Check15Strict.Tests.ps1 as a step' 'PASS'
}
else {
    Add-Result 'local-ci runs Check15Strict.Tests.ps1 as a step' 'FAIL' ($case5Problems -join '; ')
}

$failed = 0
foreach ($result in $results) {
    $color = switch ($result.Outcome) { 'PASS' { 'Green' } 'SKIP' { 'Yellow' } default { 'Red' } }
    $line = "[$($result.Outcome)] $($result.Name)"
    if (-not [string]::IsNullOrWhiteSpace($result.Detail)) { $line += " - $($result.Detail)" }
    Write-Host $line -ForegroundColor $color
    if ($result.Outcome -eq 'FAIL') { $failed++ }
}

if ($failed -gt 0) {
    Write-Host "$failed of $($results.Count) Check 15 case(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host "All Check 15 cases passed ($($results.Count))." -ForegroundColor Green
exit 0
