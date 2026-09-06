<#
.SYNOPSIS
Validates component version metadata in omp-components.json for the ODVGateway repository.

.DESCRIPTION
Checks that every component listed in omp-components.json has a valid version,
points to an existing .csproj project, references a declared module definition,
and that module definition versions stay in sync with the manifest.

Also verifies that embedded base64-utf8 sqlScripts content in each module
definition matches the current SQL files on disk (embedded SQL freshness).
This is a working-tree consistency check and runs with or without -BaseCommit.

The "Check N" numbers are STABLE identifiers shared by every OMP-compatible
repository: a given number means the same check in every validator. The
canonical list, including which checks are platform-only or consumer-only by
design, lives in docs/VALIDATOR_CHECKS.md in the OpenModulePlatform
repository. The generic helper functions live in
validate-component-versions.helpers.ps1 next to this script; that file is part
of the shared core and is kept byte-identical across repositories by the
shared-script drift guard (Check 15).

This script validates the manifest only. The application version in
Directory.Build.props (<Version>) is intentionally decoupled from the
omp-components.json component version: it is the official release version, owned
by scripts/release.ps1, and an artifact-only build may bump one without the
other. OMP artifact identity is determined by the component manifest version plus
SHA-256 content hash, not by the application version.

Check 15 (shared script drift) needs the OpenModulePlatform repository on disk.
It is located through the OpenModulePlatformRoot environment variable; when that
is not set, the script assumes a sibling directory named 'OpenModulePlatform'
next to this repository. A clone under any other name must set the variable, or
Check 15 reports "not verified" (a warning, or an error with -Strict).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$BaseCommit = '',

    [Parameter(Mandatory = $false)]
    [switch]$SelfTest,

    # Treats "could not be checked" conditions in Check 15 as errors instead of
    # warnings. A plain local run without the OpenModulePlatform sibling can
    # omit it and still validate versions.
    [Parameter(Mandatory = $false)]
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Ensure git output is decoded as UTF-8 so embedded BOMs and non-ASCII
# characters are preserved exactly as stored in the repository.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Get-ScriptDirectory {
    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        return $PSScriptRoot
    }

    $scriptPath = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        $scriptPath = $MyInvocation.MyCommand.Path
    }

    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        throw 'Could not resolve script directory.'
    }

    return Split-Path -Parent $scriptPath
}

# The shared validator core. Mandatory, not optional: without it most checks
# cannot run at all, and a gate that cannot run must not read as a passing one.
# The helpers file is kept byte-identical across repositories by the
# shared-script drift guard (Check 15).
$helpersPath = Join-Path (Get-ScriptDirectory) 'validate-component-versions.helpers.ps1'
if (-not (Test-Path -LiteralPath $helpersPath -PathType Leaf)) {
    throw "Shared validator helpers not found: $helpersPath. The helpers file is part of the shared validator core (see docs/VALIDATOR_CHECKS.md in the OpenModulePlatform repository) and must sit next to this script."
}
. $helpersPath

$checkMark = [char]0x2713
$warningSign = [char]0x26A0
$crossMark = [char]0x2717

if ($SelfTest) {
    # Canonical self-test for the validator family: the PowerShell 5.1 pitfalls
    # that have actually broken this gate (BOM strip, git change detection).
    Invoke-ValidatorSelfTest -CheckMark $checkMark -CrossMark $crossMark
}

$scriptDirectory = Get-ScriptDirectory
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory '..\..'))

$manifestPath = Join-Path $repositoryRoot 'omp-components.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Component manifest not found: $manifestPath"
}

$jsonDepth = 100
$errors = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()
$manifestText = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
$manifest = ConvertFrom-JsonDocument -Json $manifestText -Depth $jsonDepth

Write-Host 'Validating component versions...'
Write-Host ''

# ---------------------------------------------------------------------------
# Check 2: Repository version presence and format.
# ---------------------------------------------------------------------------
$repositoryVersion = [string](Get-OptionalPropertyValue -Object $manifest -Name 'repositoryVersion')
if ([string]::IsNullOrWhiteSpace($repositoryVersion)) {
    Add-ValidationError -Errors $errors -Message 'repositoryVersion is missing or empty in omp-components.json.'
}
elseif (-not (Test-SemverLikeVersion -Value $repositoryVersion)) {
    Add-ValidationError -Errors $errors -Message "repositoryVersion '$repositoryVersion' does not match the expected major.minor or major.minor.patch format."
}

# ---------------------------------------------------------------------------
# Build a lookup of module definitions for mapping and version checks.
# ---------------------------------------------------------------------------
$moduleDefinitionsByKey = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$moduleDefinitionObjectsByKey = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$moduleDefinitionVersionSyncCount = 0

foreach ($manifestDefinition in @($manifest.moduleDefinitions)) {
    if ($null -eq $manifestDefinition) {
        continue
    }

    $moduleKey = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'moduleKey')
    $definitionVersion = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'definitionVersion')
    $relativeDefinitionPath = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'path')

    if (-not [string]::IsNullOrWhiteSpace($moduleKey) -and -not $moduleDefinitionsByKey.ContainsKey($moduleKey)) {
        $moduleDefinitionsByKey.Add($moduleKey, $manifestDefinition)
    }

    # -----------------------------------------------------------------------
    # Check 4: Module definition version sync.
    # -----------------------------------------------------------------------
    if ([string]::IsNullOrWhiteSpace($relativeDefinitionPath)) {
        Add-ValidationError -Errors $errors -Message "Module definition '$moduleKey' is missing path in omp-components.json."
        continue
    }

    $definitionPath = Resolve-RepositoryPath -Path $relativeDefinitionPath -BasePath $repositoryRoot
    if (-not (Test-Path -LiteralPath $definitionPath -PathType Leaf)) {
        Add-ValidationError -Errors $errors -Message "Module definition file was not found: $relativeDefinitionPath"
        continue
    }

    $definitionText = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8
    $definition = ConvertFrom-JsonDocument -Json $definitionText -Depth $jsonDepth

    $actualDefinitionVersion = [string](Get-OptionalPropertyValue -Object $definition -Name 'definitionVersion')
    if (-not [string]::Equals($definitionVersion, $actualDefinitionVersion, [StringComparison]::Ordinal)) {
        Add-ValidationError -Errors $errors -Message "Definition version mismatch for '$relativeDefinitionPath'. Manifest='$definitionVersion', definition='$actualDefinitionVersion'."
    }
    else {
        $moduleDefinitionVersionSyncCount++
    }

    if (-not [string]::IsNullOrWhiteSpace($moduleKey) -and -not $moduleDefinitionObjectsByKey.ContainsKey($moduleKey)) {
        $moduleDefinitionObjectsByKey.Add($moduleKey, $definition)
    }
}

# ---------------------------------------------------------------------------
# Component checks.
# ---------------------------------------------------------------------------
$projectPathCount = 0
$componentVersionCount = 0
$moduleMappingCount = 0
$minModuleVersionErrorCount = 0
$cascadeCheckCount = 0
$cascadeErrorCount = 0

foreach ($component in @(Get-OptionalPropertyValue -Object $manifest -Name 'components')) {
    if ($null -eq $component) {
        continue
    }

    $componentKey = [string](Get-OptionalPropertyValue -Object $component -Name 'componentKey')
    if ([string]::IsNullOrWhiteSpace($componentKey)) {
        $componentKey = '<unknown>'
    }

    # -----------------------------------------------------------------------
    # Check 1: Component projectPath existence.
    # -----------------------------------------------------------------------
    $projectPath = [string](Get-OptionalPropertyValue -Object $component -Name 'projectPath')
    if ([string]::IsNullOrWhiteSpace($projectPath)) {
        Add-ValidationError -Errors $errors -Message "Component '$componentKey' is missing projectPath."
    }
    else {
        $fullProjectPath = Resolve-RepositoryPath -Path $projectPath -BasePath $repositoryRoot
        $packageType = [string](Get-OptionalPropertyValue -Object $component -Name 'packageType')
        $foundProject = $false

        if ($projectPath -like '*.csproj') {
            $foundProject = Test-Path -LiteralPath $fullProjectPath -PathType Leaf
        }
        elseif (Test-Path -LiteralPath $fullProjectPath -PathType Container) {
            $csprojFiles = @(Get-ChildItem -LiteralPath $fullProjectPath -Filter '*.csproj' -File -ErrorAction SilentlyContinue)
            if ($csprojFiles.Count -gt 0) {
                $foundProject = $true
            }
            elseif ([string]::Equals($packageType, 'web-app', [StringComparison]::OrdinalIgnoreCase)) {
                $packageJsonFiles = @(Get-ChildItem -LiteralPath $fullProjectPath -Filter 'package.json' -File -ErrorAction SilentlyContinue)
                $foundProject = $packageJsonFiles.Count -gt 0
            }
        }

        if (-not $foundProject) {
            Add-ValidationError -Errors $errors -Message "Component '$componentKey' projectPath does not resolve to a .csproj or web-app package.json file: $projectPath"
        }
        else {
            $projectPathCount++
        }
    }

    # -----------------------------------------------------------------------
    # Check 3: Component version presence and format.
    # -----------------------------------------------------------------------
    $componentVersion = [string](Get-OptionalPropertyValue -Object $component -Name 'version')
    if ([string]::IsNullOrWhiteSpace($componentVersion)) {
        Add-ValidationError -Errors $errors -Message "Component '$componentKey' is missing version."
    }
    elseif (-not (Test-SemverLikeVersion -Value $componentVersion)) {
        Add-ValidationError -Errors $errors -Message "Component '$componentKey' version '$componentVersion' does not match the expected major.minor or major.minor.patch format."
    }
    else {
        $componentVersionCount++
    }

    # -----------------------------------------------------------------------
    # Check 5: Component-to-module mapping integrity.
    # -----------------------------------------------------------------------
    $moduleKey = [string](Get-OptionalPropertyValue -Object $component -Name 'moduleKey')
    if (-not [string]::IsNullOrWhiteSpace($moduleKey)) {
        if (-not $moduleDefinitionsByKey.ContainsKey($moduleKey)) {
            Add-ValidationError -Errors $errors -Message "Component '$componentKey' references moduleKey '$moduleKey' which is not declared in moduleDefinitions."
        }
        else {
            $moduleMappingCount++

            # -------------------------------------------------------------------
            # Check 6: minModuleDefinitionVersion sanity — HARD ERROR.
            # A component requiring a definition version higher than what the
            # module declares produces an internally inconsistent manifest. Any
            # package built from this state would carry a minVersion requirement
            # that no existing module definition can satisfy, so import would
            # always fail at runtime.
            # -------------------------------------------------------------------
            $minModuleDefinitionVersion = [string](Get-OptionalPropertyValue -Object $component -Name 'minModuleDefinitionVersion')
            if (-not [string]::IsNullOrWhiteSpace($minModuleDefinitionVersion)) {
                $actualVersion = [string](Get-OptionalPropertyValue -Object $moduleDefinitionsByKey[$moduleKey] -Name 'definitionVersion')
                $minVersionObj = ConvertTo-VersionOrNull -Value $minModuleDefinitionVersion
                $actualVersionObj = ConvertTo-VersionOrNull -Value $actualVersion

                if ($null -ne $minVersionObj -and $null -ne $actualVersionObj -and $minVersionObj -gt $actualVersionObj) {
                    Add-ValidationError -Errors $errors -Message "Component '$componentKey' requires minModuleDefinitionVersion '$minModuleDefinitionVersion' which is greater than the declared module definition version '$actualVersion' for moduleKey '$moduleKey'."
                    $minModuleVersionErrorCount++
                }
            }

            # -------------------------------------------------------------------
            # Check 10: compatibleArtifacts range sanity — HARD ERROR.
            # A component's version must fall within the minVersion/maxVersion
            # range declared in its module's compatibleArtifacts entry for the
            # same appKey, otherwise the produced artifact cannot be imported.
            # -------------------------------------------------------------------
            $componentAppKey = [string](Get-OptionalPropertyValue -Object $component -Name 'appKey')
            if (-not [string]::IsNullOrWhiteSpace($componentAppKey) -and $moduleDefinitionObjectsByKey.ContainsKey($moduleKey)) {
                $definitionObject = $moduleDefinitionObjectsByKey[$moduleKey]
                $compatibleArtifacts = Get-OptionalPropertyValue -Object $definitionObject -Name 'compatibleArtifacts'
                if ($null -ne $compatibleArtifacts) {
                    $matchingArtifact = $null
                    foreach ($artifact in @($compatibleArtifacts)) {
                        if ($null -eq $artifact) {
                            continue
                        }

                        $artifactAppKey = [string](Get-OptionalPropertyValue -Object $artifact -Name 'appKey')
                        if ([string]::Equals($artifactAppKey, $componentAppKey, [StringComparison]::Ordinal)) {
                            $matchingArtifact = $artifact
                            break
                        }
                    }

                    if ($null -ne $matchingArtifact) {
                        $componentVersionObj = ConvertTo-VersionOrNull -Value $componentVersion
                        $maxArtifactVersion = [string](Get-OptionalPropertyValue -Object $matchingArtifact -Name 'maxVersion')
                        $minArtifactVersion = [string](Get-OptionalPropertyValue -Object $matchingArtifact -Name 'minVersion')

                        if (-not [string]::IsNullOrWhiteSpace($maxArtifactVersion)) {
                            $maxVersionObj = ConvertTo-VersionOrNull -Value $maxArtifactVersion
                            if ($null -ne $componentVersionObj -and $null -ne $maxVersionObj -and $componentVersionObj -gt $maxVersionObj) {
                                Add-ValidationError -Errors $errors -Message "Component '$componentKey' version '$componentVersion' exceeds compatibleArtifacts maxVersion '$maxArtifactVersion' for appKey '$componentAppKey'. Bump maxVersion to at least '$componentVersion'."
                            }
                        }

                        if (-not [string]::IsNullOrWhiteSpace($minArtifactVersion)) {
                            $minVersionObj = ConvertTo-VersionOrNull -Value $minArtifactVersion
                            if ($null -ne $componentVersionObj -and $null -ne $minVersionObj -and $componentVersionObj -lt $minVersionObj) {
                                Add-ValidationError -Errors $errors -Message "Component '$componentKey' version '$componentVersion' is below compatibleArtifacts minVersion '$minArtifactVersion' for appKey '$componentAppKey'. Bump minVersion to at most '$componentVersion'."
                            }
                        }
                    }
                }
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Resolve base ref for diff-based checks (Check 7 and Check 8).
# Exemption: Behavior-neutral refactors (identical emitted strings/IL) do not require
# a cascade consumer bump. Only binary-affecting changes (new/removed APIs, changed
# default values, changed serialization format, etc.) require all consumers to be bumped.
# When running multi-phase campaigns, pass -BaseCommit to pin the diff baseline.
# ---------------------------------------------------------------------------
$baseRef = 'origin/main'
$baseRefAvailable = $false

if (-not [string]::IsNullOrWhiteSpace($BaseCommit)) {
    if (Test-GitRefAvailable -RepositoryRoot $repositoryRoot -Ref $BaseCommit) {
        $baseRef = $BaseCommit
        $baseRefAvailable = $true
    }
    else {
        Add-ValidationError -Errors $errors -Message "The specified -BaseCommit '$BaseCommit' could not be resolved. Verify the commit SHA exists in this repository."
    }
}
else {
    Add-ValidationWarning -Warnings $warnings -Message 'No -BaseCommit specified; cascade diff uses origin/main. Binary-affecting shared changes committed in earlier campaign phases may not trigger cascade bumps. Pass -BaseCommit <sha> to diff against a fixed baseline.'

    $baseRefAvailable = Test-GitRefAvailable -RepositoryRoot $repositoryRoot -Ref 'origin/main'
    if (-not $baseRefAvailable) {
        Add-ValidationWarning -Warnings $warnings -Message 'origin/main could not be resolved; skipping shared-project cascade validation.'
    }
}

$baseManifest = $null
$baseComponentsByKey = $null
if ($baseRefAvailable) {
    $baseManifestText = Get-GitFileTextAtRef -RepositoryRoot $repositoryRoot -BaseRef $baseRef -Path 'omp-components.json' -Errors $errors -CheckDescription 'The baseline manifest read'
    if (-not [string]::IsNullOrWhiteSpace($baseManifestText)) {
        $baseManifest = ConvertFrom-JsonDocument -Json $baseManifestText -Depth $jsonDepth
    }

    $baseComponentsByKey = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    if ($null -ne $baseManifest) {
        foreach ($baseComponent in @($baseManifest.components)) {
            if ($null -eq $baseComponent) {
                continue
            }

            $key = [string](Get-OptionalPropertyValue -Object $baseComponent -Name 'componentKey')
            if (-not [string]::IsNullOrWhiteSpace($key) -and -not $baseComponentsByKey.ContainsKey($key)) {
                $baseComponentsByKey.Add($key, $baseComponent)
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Check 7: Shared project cascade version bumps.
# ---------------------------------------------------------------------------
$sharedProjects = Get-OptionalPropertyValue -Object $manifest -Name 'sharedProjects'
if ($null -ne $sharedProjects -and $baseRefAvailable) {
    foreach ($sharedProject in @($sharedProjects)) {
        if ($null -eq $sharedProject) {
            continue
        }

        $projectPath = [string](Get-OptionalPropertyValue -Object $sharedProject -Name 'projectPath')
        if ([string]::IsNullOrWhiteSpace($projectPath)) {
            continue
        }

        $diffPath = $projectPath
        if ($projectPath -like '*.csproj') {
            $diffPath = Split-Path -Parent $projectPath
        }

        $changedFiles = Get-GitChangedFiles -RepositoryRoot $repositoryRoot -BaseRef $baseRef -Path $diffPath -Errors $errors -CheckDescription "The shared project cascade check (Check 7) for '$projectPath'"
        if ($null -eq $changedFiles) {
            continue
        }
        if ([string]::IsNullOrWhiteSpace($changedFiles)) {
            continue
        }

        # A single string, an array or a missing property all become a clean string list.
        $consumers = @(@(Get-OptionalPropertyValue -Object $sharedProject -Name 'consumers') |
            ForEach-Object { [string]$_ } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($consumers.Count -eq 0) {
            continue
        }

        $unbumpedConsumers = [System.Collections.Generic.List[string]]::new()
        foreach ($consumerKey in $consumers) {
            $currentComponent = $null
            foreach ($component in @(Get-OptionalPropertyValue -Object $manifest -Name 'components')) {
                if (([string](Get-OptionalPropertyValue -Object $component -Name 'componentKey')) -eq $consumerKey) {
                    $currentComponent = $component
                    break
                }
            }

            if ($null -eq $currentComponent) {
                Add-ValidationWarning -Warnings $warnings -Message "Shared project '$projectPath' lists consumer '$consumerKey' which is not declared in components."
                continue
            }

            $baseVersion = $null
            if ($baseComponentsByKey.ContainsKey($consumerKey)) {
                $baseVersion = [string](Get-OptionalPropertyValue -Object $baseComponentsByKey[$consumerKey] -Name 'version')
            }

            $currentVersion = [string](Get-OptionalPropertyValue -Object $currentComponent -Name 'version')

            if (-not [string]::IsNullOrWhiteSpace($baseVersion) -and $baseVersion -eq $currentVersion) {
                $unbumpedConsumers.Add($consumerKey)
            }
        }

        if ($unbumpedConsumers.Count -gt 0) {
            $consumerList = ($unbumpedConsumers | Sort-Object) -join ', '
            Add-ValidationError -Errors $errors -Message "Shared project '$projectPath' changed but the following consumers were not bumped: $consumerList. Bump the listed components manually or via the repository's bump-version helper."
            $cascadeErrorCount++
        }
        else {
            $cascadeCheckCount++
        }
    }
}

# ---------------------------------------------------------------------------
# Check 8: Module-definition SQL diff enforcement.
# If an owned SQL script referenced by a production module definition changes
# in a material way (not just comments or whitespace), the module's
# definitionVersion must be bumped in both omp-components.json and the
# .module-definition.json file.
# ---------------------------------------------------------------------------
$sqlFilesChecked = 0
$sqlFilesPassed = 0
$sqlFilesChanged = 0

if (-not $baseRefAvailable) {
    Add-ValidationWarning -Warnings $warnings -Message 'No valid base ref available; skipping module-definition SQL diff enforcement (Check 8). Pass -BaseCommit to enable it.'
}
else {
    $baseModuleDefinitionsByKey = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    if ($null -ne $baseManifest) {
        foreach ($baseDefinition in @($baseManifest.moduleDefinitions)) {
            if ($null -eq $baseDefinition) {
                continue
            }

            $key = [string](Get-OptionalPropertyValue -Object $baseDefinition -Name 'moduleKey')
            if (-not [string]::IsNullOrWhiteSpace($key) -and -not $baseModuleDefinitionsByKey.ContainsKey($key)) {
                $baseModuleDefinitionsByKey.Add($key, $baseDefinition)
            }
        }
    }

    $ownedSqlFiles = [System.Collections.Generic.List[System.Collections.Hashtable]]::new()
    foreach ($manifestDefinition in @($manifest.moduleDefinitions)) {
        if ($null -eq $manifestDefinition) {
            continue
        }

        $moduleKey = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'moduleKey')
        $relativeDefinitionPath = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'path')

        if ([string]::IsNullOrWhiteSpace($moduleKey) -or [string]::IsNullOrWhiteSpace($relativeDefinitionPath)) {
            continue
        }

        $definitionPath = Resolve-RepositoryPath -Path $relativeDefinitionPath -BasePath $repositoryRoot
        if (-not (Test-Path -LiteralPath $definitionPath -PathType Leaf)) {
            continue
        }

        $definitionText = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8
        $definition = ConvertFrom-JsonDocument -Json $definitionText -Depth $jsonDepth

        foreach ($sqlScript in @($definition.sqlScripts)) {
            if ($null -eq $sqlScript) {
                continue
            }

            $sqlPath = [string](Get-OptionalPropertyValue -Object $sqlScript -Name 'path')
            if ([string]::IsNullOrWhiteSpace($sqlPath)) {
                continue
            }

            $alreadyOwned = $false
            foreach ($ownedSqlFile in $ownedSqlFiles) {
                if ([string]::Equals($ownedSqlFile.sqlPath, $sqlPath, [StringComparison]::OrdinalIgnoreCase)) {
                    $alreadyOwned = $true
                    break
                }
            }

            if (-not $alreadyOwned) {
                $ownedSqlFiles.Add(@{
                    moduleKey = $moduleKey
                    relativeDefinitionPath = $relativeDefinitionPath
                    sqlPath = $sqlPath
                })
            }
        }
    }

    foreach ($ownedSqlFile in $ownedSqlFiles) {
        $moduleKey = $ownedSqlFile.moduleKey
        $relativeDefinitionPath = $ownedSqlFile.relativeDefinitionPath
        $sqlPath = $ownedSqlFile.sqlPath
        $fullSqlPath = Resolve-RepositoryPath -Path $sqlPath -BasePath $repositoryRoot

        $sqlFilesChecked++

        if (-not (Test-Path -LiteralPath $fullSqlPath -PathType Leaf)) {
            Add-ValidationError -Errors $errors -Message "SQL script referenced by module '$moduleKey' was not found: $sqlPath"
            continue
        }

        $changedFiles = Get-GitChangedFiles -RepositoryRoot $repositoryRoot -BaseRef $baseRef -Path $sqlPath -Errors $errors -CheckDescription "The SQL diff check (Check 8) for '$sqlPath'"
        if ($null -eq $changedFiles) {
            continue
        }
        if ([string]::IsNullOrWhiteSpace($changedFiles)) {
            $sqlFilesPassed++
            continue
        }

        $headText = Get-Content -LiteralPath $fullSqlPath -Raw -Encoding UTF8

        $baseText = Get-GitFileTextAtRef -RepositoryRoot $repositoryRoot -BaseRef $baseRef -Path $sqlPath -Errors $errors -CheckDescription "The SQL diff check (Check 8) for '$sqlPath'"
        if ($null -eq $baseText) {
            continue
        }
        $isNewFile = [string]::IsNullOrWhiteSpace($baseText)

        $headNormalized = ConvertTo-NormalizedSql -SqlText $headText
        $baseNormalized = ConvertTo-NormalizedSql -SqlText $baseText

        $headHash = Get-Sha256Hex -Text $headNormalized
        $baseHash = Get-Sha256Hex -Text $baseNormalized

        if (-not $isNewFile -and $headHash -eq $baseHash) {
            $sqlFilesPassed++
            continue
        }

        $sqlFilesChanged++

        if (-not $moduleDefinitionsByKey.ContainsKey($moduleKey)) {
            Add-ValidationError -Errors $errors -Message "Module '$moduleKey' owns '$sqlPath' but is no longer declared in omp-components.json; declare it or remove the SQL ownership."
            continue
        }
        $headManifestDefinitionVersion = [string](Get-OptionalPropertyValue -Object $moduleDefinitionsByKey[$moduleKey] -Name 'definitionVersion')
        $baseManifestDefinitionVersion = $null
        if ($baseModuleDefinitionsByKey.ContainsKey($moduleKey)) {
            $baseManifestDefinitionVersion = [string](Get-OptionalPropertyValue -Object $baseModuleDefinitionsByKey[$moduleKey] -Name 'definitionVersion')
        }

        $manifestBumpPresent = $false
        if ([string]::IsNullOrWhiteSpace($baseManifestDefinitionVersion)) {
            $manifestBumpPresent = -not [string]::IsNullOrWhiteSpace($headManifestDefinitionVersion)
        }
        else {
            $manifestBumpPresent = -not [string]::Equals($baseManifestDefinitionVersion, $headManifestDefinitionVersion, [StringComparison]::Ordinal)
        }

        # The HEAD definition was already read and parsed for Check 4; reuse it.
        # A module missing from the cache had no readable definition file, which
        # Check 4 has already reported as an error.
        if (-not $moduleDefinitionObjectsByKey.ContainsKey($moduleKey)) {
            continue
        }
        $headDefinition = $moduleDefinitionObjectsByKey[$moduleKey]
        $headDefinitionVersion = [string](Get-OptionalPropertyValue -Object $headDefinition -Name 'definitionVersion')

        $baseDefinitionText = Get-GitFileTextAtRef -RepositoryRoot $repositoryRoot -BaseRef $baseRef -Path $relativeDefinitionPath -Errors $errors -CheckDescription "The SQL diff check (Check 8) for module '$moduleKey'"
        if ($null -eq $baseDefinitionText) {
            continue
        }
        $baseDefinitionVersion = $null
        if (-not [string]::IsNullOrWhiteSpace($baseDefinitionText)) {
            # The parsed module-definition document at the base ref. Named distinctly from the
            # $baseDefinition manifest entries iterated earlier so the two cannot be confused.
            $baseDefinitionObject = ConvertFrom-JsonDocument -Json $baseDefinitionText -Depth $jsonDepth
            $baseDefinitionVersion = [string](Get-OptionalPropertyValue -Object $baseDefinitionObject -Name 'definitionVersion')
        }

        $definitionBumpPresent = $false
        if ([string]::IsNullOrWhiteSpace($baseDefinitionVersion)) {
            $definitionBumpPresent = -not [string]::IsNullOrWhiteSpace($headDefinitionVersion)
        }
        else {
            $definitionBumpPresent = -not [string]::Equals($baseDefinitionVersion, $headDefinitionVersion, [StringComparison]::Ordinal)
        }

        if (-not $manifestBumpPresent -or -not $definitionBumpPresent) {
            Add-ValidationError -Errors $errors -Message "SQL '$sqlPath' changed (module '$moduleKey') but definitionVersion was not bumped in omp-components.json and/or the module-definition JSON. Bump definitionVersion and update relevant minModuleDefinitionVersion values."
        }
        else {
            $newDefinitionVersion = $headManifestDefinitionVersion
            $newDefinitionVersionObj = ConvertTo-VersionOrNull -Value $newDefinitionVersion

            foreach ($component in @(Get-OptionalPropertyValue -Object $manifest -Name 'components')) {
                if ($null -eq $component) {
                    continue
                }

                $componentModuleKey = [string](Get-OptionalPropertyValue -Object $component -Name 'moduleKey')
                if (-not [string]::Equals($componentModuleKey, $moduleKey, [StringComparison]::Ordinal)) {
                    continue
                }

                $minModuleDefinitionVersion = [string](Get-OptionalPropertyValue -Object $component -Name 'minModuleDefinitionVersion')
                if ([string]::IsNullOrWhiteSpace($minModuleDefinitionVersion)) {
                    continue
                }

                $minVersionObj = ConvertTo-VersionOrNull -Value $minModuleDefinitionVersion
                if ($null -ne $minVersionObj -and $null -ne $newDefinitionVersionObj -and $minVersionObj -lt $newDefinitionVersionObj) {
                    $componentKey = [string](Get-OptionalPropertyValue -Object $component -Name 'componentKey')
                    if ([string]::IsNullOrWhiteSpace($componentKey)) {
                        $componentKey = '<unknown>'
                    }

                    # Check 8b: minModuleDefinitionVersion lags a bumped definitionVersion — HARD ERROR.
                    # The module's SQL contract changed and the definitionVersion was raised. Any
                    # component that exposes a minModuleDefinitionVersion for the same module must
                    # be updated to at least the new version, otherwise packages can be imported
                    # into environments with an older definition and fail at runtime due to missing
                    # schema/metadata.
                    Add-ValidationError -Errors $errors -Message "Component '$componentKey' has minModuleDefinitionVersion '$minModuleDefinitionVersion' which is less than the new definitionVersion '$newDefinitionVersion' for module '$moduleKey'. Update minModuleDefinitionVersion to at least '$newDefinitionVersion'."
                }
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Check 12: Module-definition content diff enforcement.
# HostAgent rejects re-importing a module definition whose version already
# exists in the database with different JSON, and that rejection silently
# skips every artifact item bundled in the same universal package (the import
# summary only shows "Skipped"). Any content change to a .module-definition.json
# therefore requires a definitionVersion bump - not only SQL-affecting changes,
# which Check 8 already covers.
# ---------------------------------------------------------------------------
$definitionDiffChecked = 0
$definitionDiffChanged = 0

if (-not $baseRefAvailable) {
    Add-ValidationWarning -Warnings $warnings -Message 'No valid base ref available; skipping module-definition content diff enforcement (Check 12). Pass -BaseCommit to enable it.'
}
else {
    foreach ($manifestDefinition in @($manifest.moduleDefinitions)) {
        if ($null -eq $manifestDefinition) {
            continue
        }

        $moduleKey = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'moduleKey')
        $relativeDefinitionPath = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'path')
        if ([string]::IsNullOrWhiteSpace($moduleKey) -or [string]::IsNullOrWhiteSpace($relativeDefinitionPath)) {
            continue
        }

        $definitionPath = Resolve-RepositoryPath -Path $relativeDefinitionPath -BasePath $repositoryRoot
        if (-not (Test-Path -LiteralPath $definitionPath -PathType Leaf)) {
            continue # missing file is already an error in Check 4
        }

        $definitionDiffChecked++

        $changedFiles = Get-GitChangedFiles -RepositoryRoot $repositoryRoot -BaseRef $baseRef -Path $relativeDefinitionPath -Errors $errors -CheckDescription "The module-definition content diff check (Check 12) for '$relativeDefinitionPath'"
        if ($null -eq $changedFiles) {
            continue
        }
        if ([string]::IsNullOrWhiteSpace($changedFiles)) {
            continue
        }

        $baseDefinitionText = Get-GitFileTextAtRef -RepositoryRoot $repositoryRoot -BaseRef $baseRef -Path $relativeDefinitionPath -Errors $errors -CheckDescription "The module-definition content diff check (Check 12) for '$relativeDefinitionPath'"
        if ($null -eq $baseDefinitionText) {
            continue
        }
        if ([string]::IsNullOrWhiteSpace($baseDefinitionText)) {
            continue # new definition file; nothing to bump against
        }

        $headDefinitionText = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8
        $headNormalized = (Remove-Utf8Bom -Text $headDefinitionText).Replace("`r`n", "`n").TrimEnd("`n")
        $baseNormalized = $baseDefinitionText.Replace("`r`n", "`n").TrimEnd("`n")
        if ([string]::Equals($headNormalized, $baseNormalized, [StringComparison]::Ordinal)) {
            continue
        }

        $definitionDiffChanged++

        $headDefinition = ConvertFrom-JsonDocument -Json $headDefinitionText -Depth $jsonDepth
        $headDefinitionVersion = [string](Get-OptionalPropertyValue -Object $headDefinition -Name 'definitionVersion')
        $baseDefinition = ConvertFrom-JsonDocument -Json $baseDefinitionText -Depth $jsonDepth
        $baseDefinitionVersion = [string](Get-OptionalPropertyValue -Object $baseDefinition -Name 'definitionVersion')

        if (-not [string]::IsNullOrWhiteSpace($baseDefinitionVersion) -and [string]::Equals($baseDefinitionVersion, $headDefinitionVersion, [StringComparison]::Ordinal)) {
            Add-ValidationError -Errors $errors -Message "Module definition '$relativeDefinitionPath' (module '$moduleKey') changed but definitionVersion is still '$headDefinitionVersion'. HostAgent rejects a re-imported definition version with different JSON and silently skips artifacts packaged with it. Bump definitionVersion in both the definition file and omp-components.json."
        }
    }
}

# ---------------------------------------------------------------------------
# Check 9: Transitive ProjectReference lockstep bumps.
# If a component's own project or any project it references (directly or
# through one level of ProjectReference transitivity) changed since the base,
# the component's version must be bumped. References already covered by
# Check 7's sharedProjects cascade are excluded to avoid double-counting.
# ---------------------------------------------------------------------------
$transitiveCheckCount = 0
$transitiveErrorCount = 0

if (-not $baseRefAvailable) {
    Add-ValidationWarning -Warnings $warnings -Message 'No valid base ref available; skipping transitive ProjectReference lockstep validation (Check 9). Pass -BaseCommit to enable it.'
}
else {
    $sharedProjectDirs = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($sharedProject in @($sharedProjects)) {
        if ($null -eq $sharedProject) {
            continue
        }

        $sharedProjectPath = [string](Get-OptionalPropertyValue -Object $sharedProject -Name 'projectPath')
        if ([string]::IsNullOrWhiteSpace($sharedProjectPath)) {
            continue
        }

        $fullSharedProjectPath = Resolve-RepositoryPath -Path $sharedProjectPath -BasePath $repositoryRoot
        $sharedProjectDir = $fullSharedProjectPath
        if ($fullSharedProjectPath -like '*.csproj') {
            $sharedProjectDir = Split-Path -Parent $fullSharedProjectPath
        }

        if (Test-Path -LiteralPath $sharedProjectDir -PathType Container) {
            [void]$sharedProjectDirs.Add([System.IO.Path]::GetFullPath($sharedProjectDir))
        }
    }

    foreach ($component in @(Get-OptionalPropertyValue -Object $manifest -Name 'components')) {
        if ($null -eq $component) {
            continue
        }

        $componentKey = [string](Get-OptionalPropertyValue -Object $component -Name 'componentKey')
        if ([string]::IsNullOrWhiteSpace($componentKey)) {
            $componentKey = '<unknown>'
        }

        $projectPath = [string](Get-OptionalPropertyValue -Object $component -Name 'projectPath')
        if ([string]::IsNullOrWhiteSpace($projectPath)) {
            continue
        }

        $fullProjectPath = Resolve-RepositoryPath -Path $projectPath -BasePath $repositoryRoot
        $csprojPath = $fullProjectPath
        if (Test-Path -LiteralPath $fullProjectPath -PathType Container) {
            $csprojFiles = @(Get-ChildItem -LiteralPath $fullProjectPath -Filter '*.csproj' -File -ErrorAction SilentlyContinue)
            if ($csprojFiles.Count -eq 0) {
                continue
            }
            $csprojPath = $csprojFiles[0].FullName
        }

        if (-not (Test-Path -LiteralPath $csprojPath -PathType Leaf)) {
            continue
        }

        $directRefDirs = @(Get-ProjectReferences -CsprojPath $csprojPath)
        $allRefDirs = [System.Collections.Generic.List[string]]::new()
        foreach ($directRefDir in $directRefDirs) {
            if (-not $allRefDirs.Contains($directRefDir)) {
                [void]$allRefDirs.Add($directRefDir)
            }

            $directRefCsprojFiles = @(Get-ChildItem -LiteralPath $directRefDir -Filter '*.csproj' -File -ErrorAction SilentlyContinue)
            if ($directRefCsprojFiles.Count -gt 0) {
                $transitiveRefDirs = @(Get-ProjectReferences -CsprojPath $directRefCsprojFiles[0].FullName)
                foreach ($transitiveRefDir in $transitiveRefDirs) {
                    if (-not $allRefDirs.Contains($transitiveRefDir)) {
                        [void]$allRefDirs.Add($transitiveRefDir)
                    }
                }
            }
        }

        $changedRefDirs = [System.Collections.Generic.List[string]]::new()
        foreach ($refDir in $allRefDirs) {
            if ($sharedProjectDirs.Contains($refDir)) {
                continue
            }

            $relRefDir = $refDir.Substring($repositoryRoot.Length).TrimStart('\', '/')
            $changedFiles = Get-GitChangedFiles -RepositoryRoot $repositoryRoot -BaseRef $baseRef -Path $relRefDir -Errors $errors -CheckDescription "The transitive ProjectReference check (Check 9) for '$componentKey'"
            if ($null -eq $changedFiles) {
                continue
            }
            if (-not [string]::IsNullOrWhiteSpace($changedFiles)) {
                [void]$changedRefDirs.Add($relRefDir)
            }
        }

        if ($changedRefDirs.Count -eq 0) {
            continue
        }

        $baseVersion = $null
        if ($baseComponentsByKey.ContainsKey($componentKey)) {
            $baseVersion = [string](Get-OptionalPropertyValue -Object $baseComponentsByKey[$componentKey] -Name 'version')
        }

        $currentVersion = [string](Get-OptionalPropertyValue -Object $component -Name 'version')

        if (-not [string]::IsNullOrWhiteSpace($baseVersion) -and [string]::Equals($baseVersion, $currentVersion, [StringComparison]::Ordinal)) {
            $changedRefList = ($changedRefDirs | Sort-Object) -join ', '
            Add-ValidationError -Errors $errors -Message "Component '$componentKey' references changed project(s) ($changedRefList) since $baseRef but its version was not bumped. Bump the component version."
            $transitiveErrorCount++
        }
        else {
            $transitiveCheckCount++
        }
    }
}

# ---------------------------------------------------------------------------
# Check 13: LOCKSTEP version bump against baseline (own project source).
# When a component's OWN project directory changed since the base ref, both
# the component version and repositoryVersion must move. Check 9 covers
# referenced projects only; this check covers the component's own tree. Its
# absence in this validator let an own-source change ship with untouched
# versions (measured 2026-09-06: a committed own-project source edit passed
# validation green).
#
# Two refinements, both proven against the BUILT artifact on 2026-09-06:
# - Markdown outside wwwroot never reaches the published payload, so a
#   docs-only change must not force a bump. Markdown UNDER wwwroot is payload
#   (dotnet publish copies wwwroot verbatim; the portal artifact contains
#   wwwroot/img/blank-widget/README.md) and must force one.
# - A project can publish content from OUTSIDE its own directory: Portal's
#   csproj includes ..\tools\universal-package-builder\** into wwwroot. Those
#   bytes shape the artifact exactly like own source, so this check also
#   watches every out-of-project Content/None/Compile/EmbeddedResource
#   include directory that lives inside this repository.
# ---------------------------------------------------------------------------
$lockstepCheckCount = 0
$lockstepErrorCount = 0

if (-not $baseRefAvailable) {
    Add-ValidationWarning -Warnings $warnings -Message 'No valid base ref available; skipping LOCKSTEP validation (Check 13). Pass -BaseCommit to enable it.'
}
elseif ($null -eq $baseManifest -or $null -eq $baseComponentsByKey) {
    Add-ValidationWarning -Warnings $warnings -Message 'Baseline manifest unreadable; skipping LOCKSTEP validation (Check 13).'
}
else {
    $baseRepositoryVersion = [string](Get-OptionalPropertyValue -Object $baseManifest -Name 'repositoryVersion')

    foreach ($component in @($manifest.components)) {
        if ($null -eq $component) {
            continue
        }

        $componentKey = [string](Get-OptionalPropertyValue -Object $component -Name 'componentKey')
        if ([string]::IsNullOrWhiteSpace($componentKey)) {
            continue
        }

        $projectPath = [string](Get-OptionalPropertyValue -Object $component -Name 'projectPath')
        if ([string]::IsNullOrWhiteSpace($projectPath)) {
            continue
        }

        $diffPath = $projectPath
        if ($projectPath -like '*.csproj') {
            $diffPath = Split-Path -Parent $projectPath
        }

        $watchPaths = [System.Collections.Generic.List[string]]::new()
        [void]$watchPaths.Add($diffPath)

        # Watch out-of-project payload inputs as well (see the block comment).
        $componentCsproj = $null
        $projectFullPath = Resolve-RepositoryPath -Path $projectPath -BasePath $repositoryRoot
        if (Test-Path -LiteralPath $projectFullPath -PathType Leaf) {
            $componentCsproj = $projectFullPath
        }
        elseif (Test-Path -LiteralPath $projectFullPath -PathType Container) {
            $foundCsproj = @(Get-ChildItem -LiteralPath $projectFullPath -Filter '*.csproj' -File -ErrorAction SilentlyContinue)
            if ($foundCsproj.Count -gt 0) {
                $componentCsproj = $foundCsproj[0].FullName
            }
        }

        if ($null -ne $componentCsproj) {
            $componentCsprojDir = Split-Path -Parent $componentCsproj
            $componentCsprojText = Get-Content -LiteralPath $componentCsproj -Raw -Encoding UTF8
            foreach ($includeMatch in [System.Text.RegularExpressions.Regex]::Matches($componentCsprojText, '<(?:Content|None|Compile|EmbeddedResource)\s+[^>]*?Include="([^"]+)"')) {
                $includeValue = $includeMatch.Groups[1].Value
                if (-not $includeValue.StartsWith('..') -or $includeValue.Contains('$')) {
                    continue
                }

                $includeDir = $includeValue
                $wildcardAt = $includeDir.IndexOf('*')
                if ($wildcardAt -ge 0) {
                    $includeDir = $includeDir.Substring(0, $wildcardAt)
                }

                $resolvedInclude = [System.IO.Path]::GetFullPath((Join-Path $componentCsprojDir $includeDir))
                if (-not (Test-Path -LiteralPath $resolvedInclude -PathType Container)) {
                    continue
                }
                if (-not $resolvedInclude.StartsWith($repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
                    continue # outside this repository: Check 14's jurisdiction, not this check's
                }

                $relativeInclude = $resolvedInclude.Substring($repositoryRoot.Length).TrimStart('\', '/')
                if (-not $watchPaths.Contains($relativeInclude)) {
                    [void]$watchPaths.Add($relativeInclude)
                }
            }
        }

        $changedFilesText = ''
        $changedFilesFailed = $false
        foreach ($watchPath in $watchPaths) {
            $watchChanges = Get-GitChangedFiles -RepositoryRoot $repositoryRoot -BaseRef $baseRef -Path $watchPath -Errors $errors -CheckDescription "The LOCKSTEP check (Check 13) for '$componentKey'"
            if ($null -eq $watchChanges) {
                $changedFilesFailed = $true
                break
            }
            if (-not [string]::IsNullOrWhiteSpace($watchChanges)) {
                $changedFilesText = ($changedFilesText + "`n" + $watchChanges).Trim()
            }
        }
        if ($changedFilesFailed) {
            continue
        }

        # Markdown outside wwwroot never reaches the published payload, so a
        # docs-only change must not force a version bump. Markdown UNDER
        # wwwroot is payload (dotnet publish copies wwwroot verbatim) and does.
        $changedFiles = @($changedFilesText -split "`n" | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and -not (
                $_.Trim().EndsWith('.md', [StringComparison]::OrdinalIgnoreCase) -and
                $_ -notmatch '[\\/]wwwroot[\\/]'
            )
        })
        if ($changedFiles.Count -eq 0) {
            continue
        }

        $lockstepCheckCount++

        $currentVersion = [string](Get-OptionalPropertyValue -Object $component -Name 'version')
        $baseVersion = ''
        if ($baseComponentsByKey.ContainsKey($componentKey)) {
            $baseVersion = [string](Get-OptionalPropertyValue -Object $baseComponentsByKey[$componentKey] -Name 'version')
        }

        $versionBumped = $false
        if (-not [string]::IsNullOrWhiteSpace($baseVersion)) {
            $versionBumped = -not [string]::Equals($baseVersion, $currentVersion, [StringComparison]::Ordinal)
        }
        else {
            # No baseline version means this is a new component; treat as bumped if it has a valid version.
            $versionBumped = (-not [string]::IsNullOrWhiteSpace($currentVersion))
        }

        $repositoryVersionBumped = $false
        if (-not [string]::IsNullOrWhiteSpace($baseRepositoryVersion)) {
            $repositoryVersionBumped = -not [string]::Equals($baseRepositoryVersion, $repositoryVersion, [StringComparison]::Ordinal)
        }
        else {
            $repositoryVersionBumped = (-not [string]::IsNullOrWhiteSpace($repositoryVersion))
        }

        if (-not $versionBumped -or -not $repositoryVersionBumped) {
            $missing = [System.Collections.Generic.List[string]]::new()
            if (-not $versionBumped) {
                [void]$missing.Add("component version (current '$currentVersion', baseline '$baseVersion')")
            }
            if (-not $repositoryVersionBumped) {
                [void]$missing.Add("repositoryVersion (current '$repositoryVersion', baseline '$baseRepositoryVersion')")
            }

            $missingText = ($missing | Sort-Object) -join ' and '
            Add-ValidationError -Errors $errors -Message "Component '$componentKey' project files changed since '$baseRef' but $missingText were not bumped (LOCKSTEP). Bump the component version and repositoryVersion."
            $lockstepErrorCount++
        }
    }
}

# ---------------------------------------------------------------------------
# Check 16: Embedded sqlScripts freshness.
# For every module definition listed in omp-components.json, every sqlScripts
# entry with contentEncoding 'base64-utf8' must carry content/sha256 that match
# the current SQL file bytes on disk, after the same USE/GO prologue stripping
# applied by the OpenModulePlatform embed tool
# (scripts/dev/embed-module-definition-sql.ps1). This is a working-tree/HEAD
# consistency check, not a diff-range check, so it runs with or without
# -BaseCommit.
#
# Line-ending tolerance: the SQL blobs in git are LF-only, but a working tree
# checked out with core.autocrlf=true materializes CRLF files (and an embed
# run on such a machine embeds CRLF bytes). Pure CRLF/LF drift is normalized
# away on both sides; any other byte difference is reported as staleness.
# ---------------------------------------------------------------------------
$embeddedSqlChecked = 0
$embeddedSqlFresh = 0

foreach ($manifestDefinition in @($manifest.moduleDefinitions)) {
    if ($null -eq $manifestDefinition) {
        continue
    }

    $moduleKey = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'moduleKey')
    $relativeDefinitionPath = [string](Get-OptionalPropertyValue -Object $manifestDefinition -Name 'path')
    if ([string]::IsNullOrWhiteSpace($relativeDefinitionPath)) {
        continue
    }

    $definitionPath = Resolve-RepositoryPath -Path $relativeDefinitionPath -BasePath $repositoryRoot
    if (-not (Test-Path -LiteralPath $definitionPath -PathType Leaf)) {
        continue
    }

    $definitionText = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8
    $definition = ConvertFrom-JsonDocument -Json $definitionText -Depth $jsonDepth

    foreach ($sqlScript in @($definition.sqlScripts)) {
        if ($null -eq $sqlScript) {
            continue
        }

        $contentEncoding = [string](Get-OptionalPropertyValue -Object $sqlScript -Name 'contentEncoding')
        if (-not [string]::Equals($contentEncoding, 'base64-utf8', [StringComparison]::Ordinal)) {
            continue
        }

        $scriptKey = [string](Get-OptionalPropertyValue -Object $sqlScript -Name 'key')
        if ([string]::IsNullOrWhiteSpace($scriptKey)) {
            $scriptKey = '<no-key>'
        }

        $sqlPath = [string](Get-OptionalPropertyValue -Object $sqlScript -Name 'path')
        if ([string]::IsNullOrWhiteSpace($sqlPath)) {
            Add-ValidationError -Errors $errors -Message "Embedded SQL script '$scriptKey' (module '$moduleKey') has contentEncoding 'base64-utf8' but no path."
            continue
        }

        $embeddedSqlChecked++

        $fullSqlPath = Resolve-RepositoryPath -Path $sqlPath -BasePath $repositoryRoot
        if (-not (Test-Path -LiteralPath $fullSqlPath -PathType Leaf)) {
            Add-ValidationError -Errors $errors -Message "Embedded SQL script '$scriptKey' (module '$moduleKey') references a missing file: $sqlPath"
            continue
        }

        $diskText = Get-Content -LiteralPath $fullSqlPath -Raw -Encoding UTF8
        $portableText = ConvertTo-PortableModuleDefinitionSql -SqlText $diskText

        $actualContent = [string](Get-OptionalPropertyValue -Object $sqlScript -Name 'content')
        $actualSha256 = [string](Get-OptionalPropertyValue -Object $sqlScript -Name 'sha256')

        $embeddedText = ''
        if (-not [string]::IsNullOrWhiteSpace($actualContent)) {
            try {
                $embeddedText = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($actualContent))
            }
            catch {
                $embeddedText = ''
            }
        }

        # Compare with CRLF normalized to LF on both sides (see Check 16 note).
        $normalizedDiskText = ConvertTo-LfLineEndings -Text $portableText
        $normalizedEmbeddedText = ConvertTo-LfLineEndings -Text $embeddedText
        $contentMatches = [string]::Equals($normalizedEmbeddedText, $normalizedDiskText, [StringComparison]::Ordinal)

        # The stored hash was computed over whichever line-ending form the
        # embed tool saw, so accept a match against the raw embedded text, the
        # raw disk text, or the LF-normalized form.
        $sha256Matches = $false
        foreach ($shaCandidateText in @($embeddedText, $portableText, $normalizedDiskText)) {
            if ([string]::Equals($actualSha256, (Get-Sha256Hex -Text $shaCandidateText), [StringComparison]::Ordinal)) {
                $sha256Matches = $true
                break
            }
        }

        if (-not $contentMatches -or -not $sha256Matches) {
            Add-ValidationError -Errors $errors -Message "Embedded SQL for script '$scriptKey' ($sqlPath, module '$moduleKey') is stale: sqlScripts content/sha256 do not match the current file bytes. Refresh it with the embed tool in the OpenModulePlatform repository: scripts/dev/embed-module-definition-sql.ps1 -RepositoryRoot '<path to this repository>'."
            continue
        }

        $embeddedSqlFresh++
    }
}

# ---------------------------------------------------------------------------
# Assembly version documentation (informational only, not enforced).
# ---------------------------------------------------------------------------
Write-Host 'Application version note:'
Write-Host '  Directory.Build.props <Version> is the official release version, owned by release.ps1.'
Write-Host '  It is decoupled from the omp-components.json component version by design.'
Write-Host '  OMP artifact identity uses manifest version + SHA-256, not the application version.'
Write-Host '  This script validates the manifest, not the application version.'
Write-Host ''

# Check 14 (cross-repository sharedDependencies cascade) is intentionally absent
# here: this standalone repository declares no sharedDependencies in
# omp-components.json. See docs/VALIDATOR_CHECKS.md in the OpenModulePlatform
# repository for the canonical check list.

# ---------------------------------------------------------------------------
# Summaries.
# ---------------------------------------------------------------------------
$componentCount = 0
if ($null -ne (Get-OptionalPropertyValue -Object $manifest -Name 'components')) {
    $componentCount = @(Get-OptionalPropertyValue -Object $manifest -Name 'components').Count
}

$moduleDefinitionCount = 0
if ($null -ne $manifest.moduleDefinitions) {
    $moduleDefinitionCount = @($manifest.moduleDefinitions).Count
}

$sharedProjectCount = 0
if ($null -ne $sharedProjects) {
    $sharedProjectCount = @($sharedProjects).Count
}

$repositoryVersionStatus = if ([string]::IsNullOrWhiteSpace($repositoryVersion)) { 'missing' } else { 'validated' }
Write-Host "$checkMark $projectPathCount of $componentCount component project paths validated"
Write-Host "$checkMark Repository version $repositoryVersionStatus"
Write-Host "$checkMark $componentVersionCount of $componentCount component versions validated"
Write-Host "$checkMark $moduleDefinitionVersionSyncCount of $moduleDefinitionCount module definition versions synced"
Write-Host "$checkMark $moduleMappingCount component-to-module mappings validated"

if ($sharedProjectCount -gt 0 -and ($cascadeCheckCount -gt 0 -or $cascadeErrorCount -gt 0)) {
    Write-Host "$checkMark $cascadeCheckCount of $sharedProjectCount changed shared project(s) passed cascade bump validation ($cascadeErrorCount error(s))"
}

if ($sqlFilesChecked -gt 0) {
    Write-Host "$checkMark $sqlFilesPassed of $sqlFilesChecked owned SQL file(s) passed diff validation ($sqlFilesChanged changed)"
}

if ($embeddedSqlChecked -gt 0) {
    Write-Host "$checkMark $embeddedSqlFresh of $embeddedSqlChecked embedded SQL script(s) passed freshness validation"
}

if ($definitionDiffChecked -gt 0) {
    Write-Host "$checkMark $definitionDiffChecked module definition(s) passed content diff validation ($definitionDiffChanged changed)"
}

if ($transitiveCheckCount -gt 0 -or $transitiveErrorCount -gt 0) {
    Write-Host "$checkMark $transitiveCheckCount component(s) passed transitive ProjectReference lockstep validation ($transitiveErrorCount error(s))"
}

if ($lockstepCheckCount -gt 0 -or $lockstepErrorCount -gt 0) {
    $lockstepPassed = $lockstepCheckCount - $lockstepErrorCount
    Write-Host "$checkMark $lockstepPassed of $lockstepCheckCount changed component(s) passed LOCKSTEP bump validation ($lockstepErrorCount error(s))"
}

# Check 15: the shared omp scripts must be byte-identical to the canonical copies
# in OpenModulePlatform. Keeping them identical was a manual act twice, and
# nothing held them that way: a stale copy looks green locally and only surfaces
# when a bump behaves differently here than in a neighbouring repository -
# typically mid-incident. Same neighbour resolution and Strict semantics as
# Check 14 in the sibling repositories; the guard is CALLED from the platform
# repository rather than copied here, because a copied guard would be subject
# to the drift it detects. The platform repository is found through
# $env:OpenModulePlatformRoot, else assumed to be the sibling directory named
# 'OpenModulePlatform' (see the script help); a clone under another name
# without the variable set is reported below as "not verified".
$check15OmpRoot = $env:OpenModulePlatformRoot
if ([string]::IsNullOrWhiteSpace($check15OmpRoot)) {
    $check15OmpRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot '..\OpenModulePlatform'))
}
$check15Script = Join-Path $check15OmpRoot 'scripts\omp\validate-shared-scripts.ps1'
if (Test-Path -LiteralPath $check15Script -PathType Leaf) {
    & $check15Script -ConsumerRepositoryRoot $repositoryRoot -PlatformRepositoryRoot $check15OmpRoot -Strict:$Strict
    if ($LASTEXITCODE -ne 0) {
        Add-ValidationError -Errors $errors -Message 'Check 15 (shared script drift) failed; see the Check 15 lines above.'
    }
}
elseif ($Strict) {
    Add-ValidationError -Errors $errors -Message "Check 15: canonical script not found at '$check15Script'; shared script drift could not be checked (set OpenModulePlatformRoot if the platform repository is cloned under another name). Strict mode treats a guard that could not run as an error."
}
else {
    Write-Warning "Check 15: NOT VERIFIED - canonical script not found at '$check15Script' (set OpenModulePlatformRoot if the platform repository is cloned under another name)."
}

if ($warnings.Count -gt 0) {
    Write-Host "$warningSign $($warnings.Count) warning(s):"
    foreach ($warningMessage in $warnings) {
        Write-Host "   $warningMessage"
    }
}

Write-Host ''

if ($errors.Count -gt 0) {
    Write-Host "$crossMark $($errors.Count) error(s), $($warnings.Count) warning(s) found"
    foreach ($errorMessage in $errors) {
        Write-Host " - $errorMessage"
    }

    exit 1
}

Write-Host "$checkMark Component version validation passed"
exit 0
