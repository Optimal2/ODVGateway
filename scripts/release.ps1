<#
.SYNOPSIS
    Local release gate for ODVGateway.

.DESCRIPTION
    Runs the local pre-release verification gate for ODVGateway. Without
    -ReleaseType it publishes nothing: it validates that the current tree is
    ready for a release and prints version information.

    With -ReleaseType it also bumps <Version> in Directory.Build.props, commits
    and tags. Only -Publish pushes; that switch is the approval gate for an
    official release.

    Default checks (each can be skipped):

    1. dotnet build src/ODVGateway/ODVGateway.csproj --configuration <Configuration>
    2. dotnet test tests/ODVGateway.Tests/ODVGateway.Tests.csproj
    3. scripts/smoke-test.ps1 -Port <SmokePort>
    4. scripts/validate-component-versions.ps1 -BaseCommit 'origin/main'

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

    # Supplying -ReleaseType turns this from a gate into a release: after the
    # checks pass it bumps <Version> in Directory.Build.props, commits, and tags.
    # Without it the script behaves exactly as before and publishes nothing.
    [ValidateSet('patch', 'minor', 'major')]
    [string]$ReleaseType = '',

    [switch]$Yes,

    # The approval gate for an official release. Without it the release is
    # prepared locally and never leaves this machine.
    [switch]$Publish,

    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$projectPath = Join-Path $repoRoot 'src/ODVGateway/ODVGateway.csproj'
$testProject = Join-Path $repoRoot 'tests/ODVGateway.Tests/ODVGateway.Tests.csproj'
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

function Read-ProjectVersion {
    param([Parameter(Mandatory = $true)][string]$Path)
    $text = Get-Content -LiteralPath $Path -Raw
    $match = [regex]::Match($text, '<Version>\s*([0-9]+\.[0-9]+\.[0-9]+)\s*</Version>')
    if (-not $match.Success) {
        throw "No <Version>X.Y.Z</Version> found in $Path. That element is the official version and release.ps1 owns it."
    }
    return $match.Groups[1].Value
}

function Get-NextVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Current,
        [Parameter(Mandatory = $true)][string]$Type
    )
    $parts = $Current.Split('.')
    $major = [int]$parts[0]; $minor = [int]$parts[1]; $patch = [int]$parts[2]
    switch ($Type) {
        'major' { return "$($major + 1).0.0" }
        'minor' { return "$major.$($minor + 1).0" }
        default { return "$major.$minor.$($patch + 1)" }
    }
}

function Set-ProjectVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Version
    )
    $text = Get-Content -LiteralPath $Path -Raw
    $updated = [regex]::Replace(
        $text,
        '<Version>\s*[0-9]+\.[0-9]+\.[0-9]+\s*</Version>',
        "<Version>$Version</Version>",
        [System.Text.RegularExpressions.RegexOptions]::None)
    if ($updated -eq $text) {
        throw "Failed to update <Version> in $Path."
    }
    # No BOM, and keep the file's own newline convention.
    [System.IO.File]::WriteAllText($Path, $updated, (New-Object System.Text.UTF8Encoding($false)))
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
# Step 1b: dotnet test
#
# This is not optional padding. The pre-push hook runs local-ci.ps1, which runs
# the unit tests -- and -Publish pushes with --no-verify, so without this step a
# published release would never have run them locally at all. They would first
# run in the workflow, AFTER the tag is public, and a failure there leaves a
# public tag with no release and no sanctioned way back (retagging by hand is
# exactly what AGENTS.md forbids). Found by review, 2026-08-23.
$stepTestPass = $false
$stepTestMessage = ''
if (-not $SkipBuild) {
    try {
        Write-Host "Running: dotnet test `"$testProject`" --configuration $Configuration"
        & dotnet test $testProject --configuration $Configuration
        if ($LASTEXITCODE -eq 0) {
            $stepTestPass = $true
        }
        else {
            $stepTestMessage = "dotnet test exited with code $LASTEXITCODE"
            $overallPass = $false
        }
    }
    catch {
        $stepTestMessage = "dotnet test failed: $_"
        $overallPass = $false
    }
}
else {
    $stepTestPass = $true
    $stepTestMessage = 'Skipped by -SkipBuild'
}
Write-StepResult -Step 'dotnet test' -Passed $stepTestPass -Message $stepTestMessage

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
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$appVersion = Read-ProjectVersion -Path $propsPath

Write-Host ''
Write-Host "Official application version: $appVersion   (Directory.Build.props)"
Write-Host "OMP artifact version:         $version   (omp-components.json)"
Write-Host 'These two are independent by design; never force them to match.'

if (-not $overallPass) {
    Write-Host 'RELEASE GATE FAILED' -ForegroundColor Red
    Write-Host 'Do NOT proceed with release until all checks pass.' -ForegroundColor Red
    exit 1
}

Write-Host 'RELEASE GATE PASSED' -ForegroundColor Green

if ([string]::IsNullOrWhiteSpace($ReleaseType)) {
    Write-Host 'Gate only. Pass -ReleaseType patch|minor|major to cut a release.' -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------------------
# Release. Everything below runs only when -ReleaseType was supplied.
# ---------------------------------------------------------------------------

# A release must describe exactly one commit, so the tree has to be clean and
# pushed first. Releasing a dirty tree produces a tag whose contents exist
# nowhere else.
$status = & git -C $repoRoot status --porcelain
if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
if ($status) {
    Write-Host 'RELEASE ABORTED: the working tree has uncommitted changes.' -ForegroundColor Red
    Write-Host 'Commit and push them first; this script releases a commit, it does not create one from your edits.' -ForegroundColor Red
    exit 1
}

# A release must come from main, and from a commit that exists on origin.
# Checking only "ahead of upstream" let three cases through silently, because a
# missing upstream makes the command fail and 2>$null hides it: a detached HEAD
# (tag lands on a loose commit), a feature branch (push succeeds, workflow
# publishes a release from a commit never on main), and a local main BEHIND
# origin (push fails halfway). Found by review, 2026-08-23.
$branch = (& git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'git rev-parse --abbrev-ref HEAD failed.' }
if ($branch -eq 'HEAD') {
    Write-Host 'RELEASE ABORTED: HEAD is detached.' -ForegroundColor Red
    Write-Host 'Check out main before releasing; a tag on a loose commit is not reachable from any branch.' -ForegroundColor Red
    exit 1
}
if ($branch -ne 'main') {
    Write-Host "RELEASE ABORTED: on branch '$branch', not main." -ForegroundColor Red
    Write-Host 'Official releases are cut from main. Merge first.' -ForegroundColor Red
    exit 1
}

$upstream = & git -C $repoRoot rev-parse --abbrev-ref '@{u}' 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($upstream)) {
    Write-Host 'RELEASE ABORTED: the current branch has no upstream.' -ForegroundColor Red
    Write-Host 'Without one there is no way to tell whether this commit exists on origin.' -ForegroundColor Red
    Write-Host '  Set one with: git branch --set-upstream-to=origin/main main' -ForegroundColor Red
    exit 1
}

& git -C $repoRoot fetch --quiet origin 2>$null | Out-Null
$counts = (& git -C $repoRoot rev-list --left-right --count '@{u}...HEAD').Trim() -split '\s+'
if ($LASTEXITCODE -ne 0 -or $counts.Count -lt 2) { throw 'git rev-list --left-right --count failed.' }
$behind = [int]$counts[0]
$ahead = [int]$counts[1]
if ($ahead -gt 0) {
    Write-Host "RELEASE ABORTED: $ahead commit(s) not pushed to upstream." -ForegroundColor Red
    Write-Host 'Push them first so the tag points at a commit that exists on origin.' -ForegroundColor Red
    exit 1
}
if ($behind -gt 0) {
    Write-Host "RELEASE ABORTED: local main is $behind commit(s) behind origin." -ForegroundColor Red
    Write-Host '  Update with: git pull --ff-only' -ForegroundColor Red
    exit 1
}

$nextVersion = Get-NextVersion -Current $appVersion -Type $ReleaseType
$tag = "v$nextVersion"

# The workflow reads the notes by exact filename, so a missing file means a
# release published with an empty body. Fail here instead.
$notesPath = Join-Path $repoRoot "release-notes/$tag.md"
if (-not (Test-Path -LiteralPath $notesPath)) {
    Write-Host "RELEASE ABORTED: release-notes/$tag.md is missing." -ForegroundColor Red
    Write-Host 'The release workflow uses that file as the release body. Write it first.' -ForegroundColor Red
    exit 1
}

$existingTag = & git -C $repoRoot tag --list $tag
if ($existingTag) {
    Write-Host "RELEASE ABORTED: tag $tag already exists locally." -ForegroundColor Red
    exit 1
}

# A local check only sees local tags. A tag that already exists on origin would
# not surface until the tag push is refused - at which point the commit is
# already published and the advice "push the tag manually" cannot work.
$remoteTag = & git -C $repoRoot ls-remote --tags origin "refs/tags/$tag"
if ($LASTEXITCODE -ne 0) {
    Write-Host 'RELEASE ABORTED: could not query origin for existing tags.' -ForegroundColor Red
    Write-Host 'Releasing without that answer risks a tag collision mid-publish.' -ForegroundColor Red
    exit 1
}
if ($remoteTag) {
    Write-Host "RELEASE ABORTED: tag $tag already exists on origin." -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host "Release summary" -ForegroundColor Cyan
Write-Host "  type:        $ReleaseType"
Write-Host "  version:     $appVersion -> $nextVersion"
Write-Host "  tag:         $tag"
Write-Host "  notes:       release-notes/$tag.md"
Write-Host "  publish:     $(if ($Publish) { 'YES - commit and tag will be pushed to origin' } else { 'no - prepared locally only' })"

if (-not $Yes) {
    $answer = Read-Host 'Proceed? (y/N)'
    if ($answer -ne 'y' -and $answer -ne 'Y') {
        Write-Host 'Aborted by operator.'
        exit 1
    }
}

# Each step below can fail, and each leaves a different amount of work behind.
# Say which, and say how to undo it - a half-finished release is worse than a
# failed one only when nobody knows which half finished.
try {
    Set-ProjectVersion -Path $propsPath -Version $nextVersion
}
catch {
    Write-Host "RELEASE FAILED while writing the version: $_" -ForegroundColor Red
    Write-Host '  Nothing was committed. Undo with: git checkout -- Directory.Build.props' -ForegroundColor Red
    exit 1
}

& git -C $repoRoot add 'Directory.Build.props'
if ($LASTEXITCODE -ne 0) {
    Write-Host 'RELEASE FAILED at git add.' -ForegroundColor Red
    Write-Host '  Undo with: git checkout -- Directory.Build.props' -ForegroundColor Red
    exit 1
}

& git -C $repoRoot commit -m "chore(release): $nextVersion"
if ($LASTEXITCODE -ne 0) {
    Write-Host 'RELEASE FAILED at git commit.' -ForegroundColor Red
    Write-Host '  The version file is staged but not committed.' -ForegroundColor Red
    Write-Host '  Undo with: git restore --staged Directory.Build.props; git checkout -- Directory.Build.props' -ForegroundColor Red
    exit 1
}

& git -C $repoRoot tag -a $tag -m "ODVGateway $tag"
if ($LASTEXITCODE -ne 0) {
    Write-Host 'RELEASE FAILED at git tag.' -ForegroundColor Red
    Write-Host '  The release commit exists locally but is untagged and unpushed.' -ForegroundColor Red
    Write-Host '  Undo with: git reset --hard HEAD~1' -ForegroundColor Red
    exit 1
}

if (-not $Publish) {
    Write-Host ''
    Write-Host "Prepared $tag locally. Nothing was pushed." -ForegroundColor Yellow
    Write-Host "To publish:  git push origin HEAD && git push origin $tag" -ForegroundColor Yellow
    Write-Host "To undo:     git tag -d $tag; git reset --hard HEAD~1" -ForegroundColor Yellow
    exit 0
}

# --no-verify: this script has just run build, unit tests, smoke and version
# validation - the same checks as the pre-push hook - and the release commit only
# changes a version string. Keep the test step above in sync with local-ci.ps1;
# if the hook ever gains a check this script lacks, this bypass starts hiding it.
& git -C $repoRoot push --no-verify origin HEAD
if ($LASTEXITCODE -ne 0) {
    Write-Host 'RELEASE FAILED at git push (commit).' -ForegroundColor Red
    Write-Host "  The commit and tag $tag exist locally only; nothing was published." -ForegroundColor Red
    Write-Host "  Retry the push, or undo with: git tag -d $tag; git reset --hard HEAD~1" -ForegroundColor Red
    exit 1
}

& git -C $repoRoot push --no-verify origin $tag
if ($LASTEXITCODE -ne 0) {
    Write-Host 'RELEASE FAILED at git push (tag).' -ForegroundColor Red
    Write-Host '  The release COMMIT is already on origin; only the tag is missing, so' -ForegroundColor Red
    Write-Host '  the workflow has not run and no release was published.' -ForegroundColor Red
    Write-Host "  Finish with: git push origin $tag" -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host "Done. Published release commit and tag $tag." -ForegroundColor Green
Write-Host 'The Release workflow triggers on the tag and publishes the GitHub release.' -ForegroundColor Green
Write-Host 'After it finishes, bump the OMP artifact version from the post-release commit.' -ForegroundColor Green
exit 0
