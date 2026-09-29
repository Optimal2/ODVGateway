<#
.SYNOPSIS
    Shared harness for tests/scripts/Check15Strict.Tests.ps1.

.DESCRIPTION
    Dot-sourced from the suite's BeforeDiscovery and BeforeAll blocks, because
    Pester 6 runs every container in its own session state where file-scope
    functions and variables are not visible.

    The platform checkout is resolved with the validator's own
    Resolve-Check15PlatformRoot function, taken from its syntax tree rather
    than copied, so the suite and the validator look in the same places in the
    same order: -PlatformRepositoryRoot, OMP_PLATFORM_ROOT,
    OpenModulePlatformRoot, then the sibling directory named
    OpenModulePlatform. The suite runs under scripts/omp/run-script-tests.ps1,
    which takes no suite parameters, so local CI hands the platform root over
    as OMP_PLATFORM_ROOT and the deliberate "no platform checkout" opt-in as
    ODVGATEWAY_CHECK15_ALLOW_MISSING_PLATFORM=1.
#>

$check15RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$check15ValidatorScript = Join-Path $check15RepoRoot 'scripts\omp\validate-component-versions.ps1'
$check15LocalCiScript = Join-Path $check15RepoRoot 'scripts\local-ci.ps1'

# Reuse the validator's own platform-root resolution instead of a copy of it:
# the function definition is taken from the validator's syntax tree and
# defined here (in the scope that dot-sources this file), without running the
# validator itself. $check15ResolverFound is $false when the validator no
# longer resolves the platform root in that one function.
$check15Tokens = $null
$check15ParseErrors = $null
$check15ValidatorAst = [System.Management.Automation.Language.Parser]::ParseFile($check15ValidatorScript, [ref]$check15Tokens, [ref]$check15ParseErrors)
$check15ResolverAst = $check15ValidatorAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Resolve-Check15PlatformRoot'
}, $true)
$check15ResolverFound = $null -ne $check15ResolverAst
if ($check15ResolverFound) {
    . ([scriptblock]::Create($check15ResolverAst.Extent.Text))
}

function Get-Check15PlatformRoot {
    if (-not (Get-Command Resolve-Check15PlatformRoot -ErrorAction SilentlyContinue)) {
        return ''
    }
    return Resolve-Check15PlatformRoot -PlatformRepositoryRoot '' -RepositoryRoot $check15RepoRoot
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
