<#
.SYNOPSIS
    Local release gate for ODVGateway.

.DESCRIPTION
    Runs the local pre-release verification gate for ODVGateway without
    publishing anything. The script validates that the current tree is ready
    for a release, then prints version information and a reminder that actual
    publishing requires explicit human approval.

    Default checks (each can be skipped):

    1. dotnet build src/ODVGateway/ODVGateway.csproj --configuration <Configuration>
    2. scripts/smoke-test.ps1 -Port <SmokePort>
    3. scripts/validate-component-versions.ps1 -BaseCommit 'origin/main'

    Exit codes: 0 = all executed checks passed, 1 = one or more checks failed.

.PARAMETER Configuration
    Build configuration passed to dotnet build. Defaults to Release.

.PARAMETER SmokePort
    TCP port used by smoke-test.ps1 while the gateway is running.
    Defaults to 5210.

.PARAMETER SkipBuild
    Skip the dotnet build step.

.PARAMETER SkipSmoke
    Skip the smoke test step.

.PARAMETER SkipValidate
    Skip the validate-component-versions step.

.PARAMETER WhatIf
    Show what would be executed without running any checks.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [int]$SmokePort = 5210,

    [switch]$SkipBuild,
    [switch]$SkipSmoke,
    [switch]$SkipValidate,

    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$projectPath = Join-Path $repoRoot 'src/ODVGateway/ODVGateway.csproj'
$smokeScript = Join-Path $scriptDir 'smoke-test.ps1'
$validatorScript = Join-Path $scriptDir 'validate-component-versions.ps1'
$componentsPath = Join-Path $repoRoot 'omp-components.json'

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

function Read-RepositoryVersion {
    param([string]$Path)

    try {
        $json = Get-Content -Path $Path -Raw | ConvertFrom-Json -Depth 8
        return $json.repositoryVersion
    }
    catch {
        return '(unknown)'
    }
}

$version = Read-RepositoryVersion -Path $componentsPath

if ($WhatIf) {
    Write-Host 'WhatIf: The following checks would be run:' -ForegroundColor Cyan
    if (-not $SkipBuild) {
        Write-Host "  dotnet build `"$projectPath`" --configuration $Configuration"
    }
    if (-not $SkipSmoke) {
        Write-Host "  $smokeScript -Port $SmokePort"
    }
    if (-not $SkipValidate) {
        Write-Host "  $validatorScript -BaseCommit 'origin/main'"
    }
    Write-Host ''
    Write-Host "Repository version: $version"
    Write-Host 'WhatIf: No checks were executed. Publishing requires explicit approval.'
    exit 0
}

$overallPass = $true

# Step 1: dotnet build
$step1Pass = $false
$step1Message = ''
if (-not $SkipBuild) {
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
}
else {
    $step1Pass = $true
    $step1Message = 'Skipped by -SkipBuild'
}
Write-StepResult -Step 'dotnet build' -Passed $step1Pass -Message $step1Message

# Step 2: smoke test
$step2Pass = $false
$step2Message = ''
if (-not $SkipSmoke) {
    # Smoke tests still run even if build was skipped, because smoke-test.ps1
    # builds and starts the gateway itself when it executes.
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
    $step2Pass = $true
    $step2Message = 'Skipped by -SkipSmoke'
}
Write-StepResult -Step 'smoke-test.ps1' -Passed $step2Pass -Message $step2Message

# Step 3: validate component versions
$step3Pass = $false
$step3Message = ''
if (-not $SkipValidate) {
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
}
else {
    $step3Pass = $true
    $step3Message = 'Skipped by -SkipValidate'
}
Write-StepResult -Step 'validate-component-versions.ps1' -Passed $step3Pass -Message $step3Message

# Summary
Write-Host ''
Write-Host "Repository version: $version"
if ($overallPass) {
    Write-Host 'RELEASE GATE PASSED' -ForegroundColor Green
    Write-Host 'All executed checks passed. Publishing still requires explicit human approval.' -ForegroundColor Green
    exit 0
}
else {
    Write-Host 'RELEASE GATE FAILED' -ForegroundColor Red
    Write-Host 'Do NOT proceed with release until all checks pass.' -ForegroundColor Red
    exit 1
}
