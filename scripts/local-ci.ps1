<#
.SYNOPSIS
    Local CI gate for ODVGateway.

.DESCRIPTION
    Runs the local pre-push verification gate for ODVGateway:

    1. dotnet build src/ODVGateway/ODVGateway.csproj --configuration Release
    2. dotnet test tests/ODVGateway.Tests/ODVGateway.Tests.csproj (unit tests)
    3. scripts/smoke-test.ps1 (builds, starts, and smoke-tests the gateway)
    4. scripts/validate-component-versions.ps1

    Each step reports PASS or FAIL. The script exits with code 0 when every
    step passes and 1 when any step fails.

    Run this before every push to catch build breaks and runtime regressions
    before they reach the shared main branch. GitHub Actions for this public
    repository are workflow_dispatch-only by choice, so local execution is
    the actual gate.

.PARAMETER Configuration
    Build configuration passed to dotnet build. Defaults to Release.

.PARAMETER SmokePort
    TCP port used by smoke-test.ps1 while the gateway is running.
    Defaults to 5210.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [int]$SmokePort = 5210
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$projectPath = Join-Path $repoRoot 'src/ODVGateway/ODVGateway.csproj'
$testProjectPath = Join-Path $repoRoot 'tests/ODVGateway.Tests/ODVGateway.Tests.csproj'
$testResultsDir = Join-Path $repoRoot 'TestResults'
$smokeScript = Join-Path $scriptDir 'smoke-test.ps1'
$validatorScript = Join-Path $scriptDir 'validate-component-versions.ps1'

# --- Local-ci telemetry (best-effort; never changes the gate's exit code) ----
# One compact JSONL line per run under
# %APPDATA%\@private\ai-orchestrator\local-ci-telemetry\ODVGateway.jsonl.
$localCiTimer = [System.Diagnostics.Stopwatch]::StartNew()
$buildDurationMs = $null
$telemetrySuites = @()
$telemetryTestStatus = 'not-run'
$telemetryTestCount = $null
$telemetrySkipReason = ''
# Deterministic TRX under the owned local-ci results directory; old dynamic
# TestResults files are never summed.
$localCiTrxRoot = Join-Path $testResultsDir 'local-ci'
$unitTrxDir = Join-Path $localCiTrxRoot 'unit'
$telemetryHelperPath = Join-Path $scriptDir 'local-ci-telemetry.ps1'
if (Test-Path -LiteralPath $telemetryHelperPath -PathType Leaf) {
    try {
        . $telemetryHelperPath
    }
    catch {
        Write-Warning "Local-ci telemetry helper for ODVGateway could not be loaded: $($_.Exception.Message)"
    }
}
else {
    Write-Warning 'Local-ci telemetry helper not found (scripts\local-ci-telemetry.ps1); this run writes no telemetry.'
}

function Write-StepResult {
    param(
        [Parameter(Mandatory = $true)][string]$Step,
        [Parameter(Mandatory = $true)][bool]$Passed,
        [string]$Message = ''
    )

    if ($Passed) {
        Write-Host "[PASS] $Step" -ForegroundColor Green
    }
    else {
        Write-Host "[FAIL] $Step" -ForegroundColor Red
        if (-not [string]::IsNullOrWhiteSpace($Message)) {
            Write-Host "       $Message" -ForegroundColor Red
        }
    }
}

$overallPass = $true

# Step 1: dotnet build
$step1Pass = $false
$step1Message = ''
$buildStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
try {
    Write-Host ''
    Write-Host "Running: dotnet build `"$projectPath`" --configuration $Configuration"
    & dotnet build "$projectPath" --configuration $Configuration
    if ($LASTEXITCODE -eq 0) {
        $step1Pass = $true
    }
    else {
        $step1Message = "dotnet build exited with code $LASTEXITCODE"
        $overallPass = $false
    }
}
catch {
    $step1Message = "dotnet build failed: $_"
    $overallPass = $false
}
$buildStopwatch.Stop()
$buildDurationMs = $buildStopwatch.ElapsedMilliseconds
Write-StepResult -Step 'dotnet build' -Passed $step1Pass -Message $step1Message

# Step 2: unit tests
$step2Pass = $false
$step2Message = ''
if ($step1Pass) {
    try {
        # Clear only the owned local-ci results directory; deterministic file
        # name so telemetry never sums stale dynamic TRX files.
        if (Test-Path -LiteralPath $localCiTrxRoot) {
            Remove-Item -LiteralPath $localCiTrxRoot -Recurse -Force
        }
        Write-Host ''
        Write-Host "Running: dotnet test `"$testProjectPath`" --configuration $Configuration --logger `"trx;LogFileName=local-ci-unit.trx`" --results-directory `"$unitTrxDir`""
        & dotnet test "$testProjectPath" --configuration $Configuration --logger "trx;LogFileName=local-ci-unit.trx" --results-directory "$unitTrxDir"
        if ($LASTEXITCODE -eq 0) {
            $step2Pass = $true
        }
        else {
            $step2Message = "dotnet test exited with code $LASTEXITCODE"
            $overallPass = $false
        }
    }
    catch {
        $step2Message = "dotnet test failed: $_"
        $overallPass = $false
    }
    $telemetryTestStatus = if ($step2Pass) { 'passed' } else { 'failed' }
    if (Get-Command Get-LocalCiTrxCounters -ErrorAction SilentlyContinue) {
        try {
            $suite = Get-LocalCiTrxCounters -ResultsDirectory $unitTrxDir -SuiteName 'unit tests'
            if ($null -ne $suite) { $telemetrySuites += $suite }
        }
        catch {
            Write-Warning "TRX counters for the unit tests could not be parsed: $($_.Exception.Message)"
        }
    }
}
else {
    $step2Message = 'Skipped because dotnet build failed'
    $overallPass = $false
    $telemetrySkipReason = 'test step not run (build failed)'
}
Write-StepResult -Step 'dotnet test' -Passed $step2Pass -Message $step2Message

# Step 3: smoke test
$step3Pass = $false
$step3Message = ''
if ($step2Pass) {
    try {
        Write-Host ''
        Write-Host "Running: $smokeScript -Port $SmokePort"
        & "$smokeScript" -Port $SmokePort
        if ($LASTEXITCODE -eq 0) {
            $step3Pass = $true
        }
        else {
            $step3Message = "smoke-test.ps1 exited with code $LASTEXITCODE"
            $overallPass = $false
        }
    }
    catch {
        $step3Message = "smoke-test.ps1 failed: $_"
        $overallPass = $false
    }
}
else {
    $step3Message = 'Skipped because dotnet test failed'
    $overallPass = $false
}
Write-StepResult -Step 'smoke-test.ps1' -Passed $step3Pass -Message $step3Message

# Step 4: validate component versions
$step4Pass = $false
$step4Message = ''
try {
    Write-Host ''
    Write-Host "Running: $validatorScript -BaseCommit 'origin/main'"
    & "$validatorScript" -BaseCommit 'origin/main'
    if ($LASTEXITCODE -eq 0) {
        $step4Pass = $true
    }
    else {
        $step4Message = "validate-component-versions.ps1 exited with code $LASTEXITCODE"
        $overallPass = $false
    }
}
catch {
    $step4Message = "validate-component-versions.ps1 failed: $_"
    $overallPass = $false
}
Write-StepResult -Step 'validate-component-versions.ps1' -Passed $step4Pass -Message $step4Message

# Telemetry: one compact JSONL line per run. Written AFTER the gate result is
# decided, in its own try/catch: a failure here is a visible Write-Warning and
# can never change the exit code.
$localCiTimer.Stop()
if ($telemetrySuites.Count -gt 0 -and $null -eq $telemetryTestCount) {
    $executedSum = 0
    foreach ($suiteRow in $telemetrySuites) { $executedSum += [int]$suiteRow.executed }
    $telemetryTestCount = $executedSum
}
$telemetryStatus = if ($overallPass) { 'pass' } else { 'fail' }
try {
    if (Get-Command Write-LocalCiTelemetry -ErrorAction SilentlyContinue) {
        Write-LocalCiTelemetry -Repo 'ODVGateway' -RepositoryRoot $repoRoot -Status $telemetryStatus -DurationMs $localCiTimer.ElapsedMilliseconds -BuildDurationMs $buildDurationMs -TestStatus $telemetryTestStatus -TestCount $telemetryTestCount -TestSkipReason $telemetrySkipReason -Suites $telemetrySuites
    }
}
catch {
    $telemetryTarget = '(unknown: APPDATA not set)'
    if (-not [string]::IsNullOrWhiteSpace($env:APPDATA)) {
        $telemetryTarget = Join-Path $env:APPDATA '@private\ai-orchestrator\local-ci-telemetry\ODVGateway.jsonl'
    }
    Write-Warning "Local-ci telemetry failed for ODVGateway (target: $telemetryTarget): $($_.Exception.Message)"
}

# Summary
Write-Host ''
if ($overallPass) {
    Write-Host 'LOCAL CI PASSED' -ForegroundColor Green
    exit 0
}
else {
    Write-Host 'LOCAL CI FAILED' -ForegroundColor Red
    exit 1
}
