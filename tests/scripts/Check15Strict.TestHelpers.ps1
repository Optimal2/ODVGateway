<#
.SYNOPSIS
    Shared harness for tests/scripts/Check15Strict.Tests.ps1.

.DESCRIPTION
    Dot-sourced from the suite's BeforeDiscovery and BeforeAll blocks, because
    Pester 6 runs every container in its own session state where file-scope
    functions and variables are not visible.

    The platform checkout is resolved with the shared resolver
    Resolve-PlatformCheckScript from validate-component-versions.helpers.ps1,
    which the validator itself calls, so the suite and the validator look in the
    same places in the same order: -PlatformRepositoryRoot, OMP_PLATFORM_ROOT,
    OpenModulePlatformRoot, then the sibling directory named OpenModulePlatform.
    The suite runs under scripts/omp/run-script-tests.ps1, which takes no suite
    parameters, so local CI hands the platform root over as OMP_PLATFORM_ROOT
    and the deliberate "no platform checkout" opt-in as
    ODVGATEWAY_CHECK15_ALLOW_MISSING_PLATFORM=1.
#>

$check15RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$check15ValidatorScript = Join-Path $check15RepoRoot 'scripts\omp\validate-component-versions.ps1'
$check15LocalCiScript = Join-Path $check15RepoRoot 'scripts\local-ci.ps1'
$check15HelpersScript = Join-Path $check15RepoRoot 'scripts\omp\validate-component-versions.helpers.ps1'

# Reuse the validator's own platform-root resolution instead of a copy of it:
# the shared helpers file is dot-sourced here (it is side-effect free by
# contract, see its header) and Resolve-PlatformCheckScript is called exactly as
# the validator calls it. $check15ResolverFound is $false when the shared core
# no longer provides the resolver.
. $check15HelpersScript
$check15ResolverFound = $null -ne (Get-Command Resolve-PlatformCheckScript -ErrorAction SilentlyContinue)

function Get-Check15PlatformRoot {
    if (-not $check15ResolverFound) {
        return ''
    }
    $errors = [System.Collections.Generic.List[string]]::new()
    $warnings = [System.Collections.Generic.List[string]]::new()
    $resolved = Resolve-PlatformCheckScript -RepositoryRoot $check15RepoRoot `
        -ScriptRelativePath 'scripts/omp/validate-shared-scripts.ps1' -CheckLabel 'Check 15' `
        -Errors $errors -Warnings $warnings -PlatformRepositoryRoot ''
    if ($null -eq $resolved) {
        return ''
    }
    return $resolved.PlatformRoot
}

function Test-Check15HasPlatform {
    param([string]$PlatformRoot)
    return -not [string]::IsNullOrWhiteSpace($PlatformRoot) -and
        (Test-Path -LiteralPath (Join-Path $PlatformRoot 'scripts\omp\validate-shared-scripts.ps1') -PathType Leaf)
}

function Test-Check15AllowMissingPlatform {
    return $env:ODVGATEWAY_CHECK15_ALLOW_MISSING_PLATFORM -eq '1'
}

function Invoke-Validator {
    param([hashtable]$Environment, [hashtable]$Arguments)

    $names = @('OMP_PLATFORM_ROOT', 'OpenModulePlatformRoot', 'OMP_ALLOW_MISSING_PLATFORM')
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
        $output = (& $check15ValidatorScript @Arguments *>&1 | ForEach-Object { "$_" }) -join "`n"
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

function Get-LocalCiAst {
    $tokens = $null
    $parseErrors = $null
    return [System.Management.Automation.Language.Parser]::ParseFile($check15LocalCiScript, [ref]$tokens, [ref]$parseErrors)
}

function Get-LocalCiCallText {
    <#
    .SYNOPSIS
        Returns the text of every command in local-ci.ps1 whose first element
        mentions the given variable name.
    #>
    param($Ast, [string]$VariablePattern)
    $calls = @($Ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
            $node.CommandElements.Count -gt 0 -and
            $node.CommandElements[0].Extent.Text -match $VariablePattern
    }, $true))
    return [pscustomobject]@{ Count = $calls.Count; Text = (($calls | ForEach-Object { $_.Extent.Text }) -join "`n") }
}
