# Shared setup for Validate-ComponentVersions.Tests.ps1.
# Dot-sourced from each Describe block's BeforeAll: Pester 6 runs every
# container in a separate session state, so functions and variables defined
# at file scope are not visible inside It blocks.

$ErrorActionPreference = 'Stop'

$scriptPath = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\omp\validate-component-versions.ps1')
$helpersPath = Resolve-Path (Join-Path $PSScriptRoot '..\scripts\omp\validate-component-versions.helpers.ps1')

# Dot-source the helpers so pure functions such as Compare-WebSharedBinaryIdentity
# can be exercised directly without invoking the full validator.
. $helpersPath

function New-TemporaryTestRepository {
    <#
    .SYNOPSIS
    Creates a temporary repository with the validator script, a minimal
    omp-components.json, a module definition, an owned SQL file, and a
    fake .csproj project.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [Parameter(Mandatory = $false)]
        [string]$ComponentMinVersion = '1.0.0',

        [Parameter(Mandatory = $false)]
        [string]$ModuleDefinitionVersion = '1.0.0',

        [Parameter(Mandatory = $false)]
        [string]$SqlContent = 'SELECT 1;',

        [Parameter(Mandatory = $false)]
        [string]$ComponentVersion = '1.0.0',

        [Parameter(Mandatory = $false)]
        [string]$ComponentAppKey = 'test_app',

        [Parameter(Mandatory = $false)]
        [string]$CompatibleArtifactMaxVersion = '',

        [Parameter(Mandatory = $false)]
        [string]$CompatibleArtifactMinVersion = '',

        # Skip git init/commit entirely: the fixture is then a directory that
        # git cannot answer questions about, which is exactly what the
        # fail-loud git-reader tests need.
        [Parameter(Mandatory = $false)]
        [switch]$NoGit
    )

    if (Test-Path -LiteralPath $RootPath -PathType Container) {
        Remove-Item -LiteralPath $RootPath -Recurse -Force
    }

    $null = New-Item -ItemType Directory -Path $RootPath -Force

    # Copy the validator so its $repositoryRoot resolves to the temp repo.
    $ompScriptsDir = Join-Path $RootPath 'scripts\omp'
    $null = New-Item -ItemType Directory -Path $ompScriptsDir -Force
    Copy-Item -LiteralPath $scriptPath -Destination (Join-Path $ompScriptsDir 'validate-component-versions.ps1') -Force
    Copy-Item -LiteralPath $helpersPath -Destination (Join-Path $ompScriptsDir 'validate-component-versions.helpers.ps1') -Force

    Copy-Item -LiteralPath (Join-Path (Split-Path $scriptPath) 'bump-version.ps1') -Destination (Join-Path $ompScriptsDir 'bump-version.ps1')

    # Create component project.
    $projectDir = Join-Path $RootPath 'TestApp'
    $null = New-Item -ItemType Directory -Path $projectDir -Force
    $csprojContent = "<Project Sdk=`"Microsoft.NET.Sdk`">`r`n  <PropertyGroup>`r`n    <TargetFramework>net8.0</TargetFramework>`r`n  </PropertyGroup>`r`n</Project>`r`n"
    [System.IO.File]::WriteAllText((Join-Path $projectDir 'TestApp.csproj'), $csprojContent, [System.Text.Encoding]::UTF8)

    # Create module definition and SQL.
    $moduleDir = Join-Path $RootPath 'TestModule'
    $sqlDir = Join-Path $moduleDir 'sql'
    $null = New-Item -ItemType Directory -Path $sqlDir -Force
    [System.IO.File]::WriteAllText((Join-Path $sqlDir 'init.sql'), $SqlContent, [System.Text.Encoding]::UTF8)

    $moduleDefinition = @{
        moduleKey = 'test_module'
        definitionVersion = $ModuleDefinitionVersion
        sqlScripts = @(
            @{
                path = 'TestModule/sql/init.sql'
            }
        )
    }

    $compatibleArtifact = @{}
    if (-not [string]::IsNullOrWhiteSpace($CompatibleArtifactMaxVersion)) {
        $compatibleArtifact['maxVersion'] = $CompatibleArtifactMaxVersion
    }
    if (-not [string]::IsNullOrWhiteSpace($CompatibleArtifactMinVersion)) {
        $compatibleArtifact['minVersion'] = $CompatibleArtifactMinVersion
    }
    if (-not [string]::IsNullOrWhiteSpace($ComponentAppKey) -and $compatibleArtifact.Count -gt 0) {
        $compatibleArtifact['appKey'] = $ComponentAppKey
        $moduleDefinition['compatibleArtifacts'] = @($compatibleArtifact)
    }

    $moduleDefinitionJson = $moduleDefinition | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText((Join-Path $moduleDir 'test.module-definition.json'), $moduleDefinitionJson, [System.Text.Encoding]::UTF8)

    # Create the component manifest.
    $componentEntry = @{
        componentKey = 'test_app'
        version = $ComponentVersion
        projectPath = 'TestApp/TestApp.csproj'
        moduleKey = 'test_module'
        minModuleDefinitionVersion = $ComponentMinVersion
    }
    if (-not [string]::IsNullOrWhiteSpace($ComponentAppKey)) {
        $componentEntry['appKey'] = $ComponentAppKey
    }

    $manifest = @{
        repositoryVersion = '1.0.0'
        moduleDefinitions = @(
            @{
                moduleKey = 'test_module'
                definitionVersion = $ModuleDefinitionVersion
                path = 'TestModule/test.module-definition.json'
            }
        )
        components = @(
            $componentEntry
        )
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText((Join-Path $RootPath 'omp-components.json'), $manifestJson, [System.Text.Encoding]::UTF8)

    # Initialize git repository and create initial commit.
    if (-not $NoGit) {
        $originalLocation = Get-Location
        try {
            Set-Location -LiteralPath $RootPath
            & git -C $RootPath init --quiet
            if ($LASTEXITCODE -ne 0) { throw 'git init failed.' }

            & git -C $RootPath config core.autocrlf false
            if ($LASTEXITCODE -ne 0) { throw 'git config core.autocrlf failed.' }

            & git -C $RootPath config user.email 'test@example.com'
            if ($LASTEXITCODE -ne 0) { throw 'git config user.email failed.' }

            & git -C $RootPath config user.name 'Test User'
            if ($LASTEXITCODE -ne 0) { throw 'git config user.name failed.' }

            & git -C $RootPath add -A
            if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }

            & git -C $RootPath commit -m 'Initial commit' --quiet
            if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }
        }
        finally {
            Set-Location $originalLocation
        }
    }

    return (Join-Path $ompScriptsDir 'validate-component-versions.ps1')
}

function Remove-TemporaryTestRepository {
    <#
    .SYNOPSIS
    Removes a temporary test repository created by New-TemporaryTestRepository.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath
    )

    if (Test-Path -LiteralPath $RootPath -PathType Container) {
        Remove-Item -LiteralPath $RootPath -Recurse -Force
    }
}

function Invoke-Validator {
    <#
    .SYNOPSIS
    Runs the validator in the specified repository and returns its exit code.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ValidatorPath,
        [Parameter(Mandatory = $false)][string]$BaseCommit = ''
    )

    $exitCode = $null
    try {
        if ([string]::IsNullOrWhiteSpace($BaseCommit)) {
            & $ValidatorPath 2>&1 | Out-String | Out-Null
        }
        else {
            & $ValidatorPath -BaseCommit $BaseCommit 2>&1 | Out-String | Out-Null
        }
    }
    finally {
        $exitCode = $LASTEXITCODE
    }

    return $exitCode
}

function Invoke-ValidatorWithOutput {
    <#
    .SYNOPSIS
    Runs the validator and returns its exit code together with everything it
    wrote, so tests can assert on warnings and summary lines.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ValidatorPath,
        [Parameter(Mandatory = $false)][string]$BaseCommit = '',
        [Parameter(Mandatory = $false)][switch]$Strict
    )

    $arguments = @{}
    if (-not [string]::IsNullOrWhiteSpace($BaseCommit)) {
        $arguments['BaseCommit'] = $BaseCommit
    }
    if ($Strict) {
        $arguments['Strict'] = $true
    }

    $output = ''
    $exitCode = $null
    try {
        $output = & $ValidatorPath @arguments *>&1 | Out-String -Width 4096
    }
    catch {
        $output += $_.Exception.Message
    }
    finally {
        $exitCode = $LASTEXITCODE
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}
