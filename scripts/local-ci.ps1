<#
.SYNOPSIS
    Local CI gate for ODVGateway.

.DESCRIPTION
    Runs the local pre-push verification gate for ODVGateway:

    1. dotnet build src/ODVGateway/ODVGateway.csproj --configuration Release
    2. scripts/smoke-test.ps1 (builds, starts, and smoke-tests the gateway)
    3. scripts/validate-component-versions.ps1

    Each step reports PASS or FAIL. The script exits with code 0 when every
    step passes and 1 when any step fails.

    Run this before every push to catch build breaks and runtime regressions
    before they reach the shared main branch. GitHub Actions for this private
    repository are workflow_dispatch-only and metered, so local execution is
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
$smokeScript = Join-Path $scriptDir 'smoke-test.ps1'
$validatorScript = Join-Path $scriptDir 'validate-component-versions.ps1'

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
Write-StepResult -Step 'dotnet build' -Passed $step1Pass -Message $step1Message

# Step 2: smoke test
$step2Pass = $false
$step2Message = ''
if ($step1Pass) {
    try {
        Write-Host ''
        Write-Host "Running: $smokeScript -Port $SmokePort"
        & "$smokeScript" -Port $SmokePort
        if ($LASTEXITCODE -eq 0) {
            $step2Pass = $true
        }
        else {
            $step2Message = "smoke-test.ps1 exited with code $LASTEXITCODE"
            $overallPass = $false
        }
    }
    catch {
        $step2Message = "smoke-test.ps1 failed: $_"
        $overallPass = $false
    }
}
else {
    $step2Message = 'Skipped because dotnet build failed'
    $overallPass = $false
}
Write-StepResult -Step 'smoke-test.ps1' -Passed $step2Pass -Message $step2Message

# Step 3: validate component versions
$step3Pass = $false
$step3Message = ''
try {
    Write-Host ''
    Write-Host "Running: $validatorScript -BaseCommit 'origin/main'"
    & "$validatorScript" -BaseCommit 'origin/main'
    if ($LASTEXITCODE -eq 0) {
        $step3Pass = $true
    }
    else {
        $step3Message = "validate-component-versions.ps1 exited with code $LASTEXITCODE"
        $overallPass = $false
    }
}
catch {
    $step3Message = "validate-component-versions.ps1 failed: $_"
    $overallPass = $false
}
Write-StepResult -Step 'validate-component-versions.ps1' -Passed $step3Pass -Message $step3Message

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
