# Pester assertions are supplied by the repository's pinned module.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 assertions are provided by the pinned module.')]
param()

Describe 'Shared platform root resolution' {
    BeforeAll {
        $script:RepoRoot = Split-Path -Parent $PSScriptRoot
        . (Join-Path $script:RepoRoot 'scripts\omp\validate-component-versions.helpers.ps1')
        $script:Wrapper = Join-Path $script:RepoRoot 'scripts\omp\resolve-platform-root.ps1'

        function Invoke-PlatformRootResolution {
            param(
                [Parameter(Mandatory = $true)][string]$Consumer,
                [hashtable]$Environment = @{},
                [string]$ExplicitRoot = ''
            )

            $names = @('OMP_PLATFORM_ROOT', 'OpenModulePlatformRoot')
            $saved = @{}
            foreach ($name in $names) {
                $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
                [Environment]::SetEnvironmentVariable($name, $null, 'Process')
            }
            foreach ($name in $Environment.Keys) {
                [Environment]::SetEnvironmentVariable($name, $Environment[$name], 'Process')
            }
            try {
                $resolved = Resolve-OmpPlatformRoot -RepositoryRoot $Consumer -PlatformRepositoryRoot $ExplicitRoot
                return @{ Resolved = $resolved; Error = '' }
            }
            catch {
                return @{ Resolved = $null; Error = $_.Exception.Message }
            }
            finally {
                foreach ($name in $names) {
                    [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
                }
            }
        }
    }

    BeforeEach {
        $script:Root = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $script:Consumer = Join-Path $script:Root 'Consumer'
        $script:Platform = Join-Path $script:Root 'Platform'
        $null = New-Item -ItemType Directory -Path $script:Consumer -Force
        $null = New-Item -ItemType Directory -Path $script:Platform -Force
    }

    It 'Accepts either variable alone and exposes the consumer wrapper contract' {
        foreach ($name in @('OMP_PLATFORM_ROOT', 'OpenModulePlatformRoot')) {
            $result = Invoke-PlatformRootResolution -Consumer $script:Consumer -Environment @{ $name = $script:Platform }
            $result.Error | Should -Be ''
            $result.Resolved.Root | Should -Be $script:Platform
            $result.Resolved.Source | Should -Be $name
        }

        $saved = [Environment]::GetEnvironmentVariable('OMP_PLATFORM_ROOT', 'Process')
        $savedBuild = [Environment]::GetEnvironmentVariable('OpenModulePlatformRoot', 'Process')
        try {
            [Environment]::SetEnvironmentVariable('OMP_PLATFORM_ROOT', $script:Platform, 'Process')
            [Environment]::SetEnvironmentVariable('OpenModulePlatformRoot', $null, 'Process')
            $wrapperResult = & $script:Wrapper -RepositoryRoot $script:Consumer
            $wrapperResult | Should -Be $script:Platform
        }
        finally {
            [Environment]::SetEnvironmentVariable('OMP_PLATFORM_ROOT', $saved, 'Process')
            [Environment]::SetEnvironmentVariable('OpenModulePlatformRoot', $savedBuild, 'Process')
        }
    }

    It 'Accepts both variables when case and trailing separators differ' {
        $result = Invoke-PlatformRootResolution -Consumer $script:Consumer -Environment @{
            OMP_PLATFORM_ROOT = $script:Platform.ToUpperInvariant()
            OpenModulePlatformRoot = $script:Platform + '\'
        }
        $result.Error | Should -Be ''
        $result.Resolved.Root | Should -Be $script:Platform
        $result.Resolved.Source | Should -Be 'OMP_PLATFORM_ROOT'
    }

    It 'Accepts both variables when one uses an 8.3 short-name form' {
        $longParent = Join-Path $script:Root 'long platform parent folder'
        $platform = Join-Path $longParent 'Platform'
        $null = New-Item -ItemType Directory -Path $platform -Force
        $shortParent = (New-Object -ComObject Scripting.FileSystemObject).GetFolder($longParent).ShortPath
        if ([string]::Equals($shortParent, $longParent, [StringComparison]::OrdinalIgnoreCase)) {
            Set-ItResult -Skipped -Because 'this volume does not generate 8.3 names'
            return
        }

        $result = Invoke-PlatformRootResolution -Consumer $script:Consumer -Environment @{
            OMP_PLATFORM_ROOT = $platform
            OpenModulePlatformRoot = Join-Path $shortParent 'Platform'
        }
        $result.Error | Should -Be ''
        $result.Resolved.Root | Should -Be $platform
    }

    It 'Rejects different roots and names both resolved paths' {
        $other = Join-Path $script:Root 'OtherPlatform'
        $null = New-Item -ItemType Directory -Path $other -Force
        $result = Invoke-PlatformRootResolution -Consumer $script:Consumer -Environment @{
            OMP_PLATFORM_ROOT = $script:Platform
            OpenModulePlatformRoot = $other
        }
        $result.Resolved | Should -BeNullOrEmpty
        $result.Error | Should -Match 'different platform checkouts'
        $result.Error | Should -Match ([regex]::Escape($script:Platform))
        $result.Error | Should -Match ([regex]::Escape($other))
    }

    It 'Rejects conflicting variables even when an explicit root is present' {
        $other = Join-Path $script:Root 'OtherPlatform'
        $null = New-Item -ItemType Directory -Path $other -Force
        $result = Invoke-PlatformRootResolution -Consumer $script:Consumer -ExplicitRoot $script:Platform -Environment @{
            OMP_PLATFORM_ROOT = $script:Platform
            OpenModulePlatformRoot = $other
        }
        $result.Error | Should -Match 'different platform checkouts'
    }

    It 'Rejects a named root that does not exist' {
        $missing = Join-Path $script:Root 'MissingPlatform'
        $result = Invoke-PlatformRootResolution -Consumer $script:Consumer -Environment @{ OMP_PLATFORM_ROOT = $missing }
        $result.Resolved | Should -BeNullOrEmpty
        $result.Error | Should -Match 'OMP_PLATFORM_ROOT'
        $result.Error | Should -Match ([regex]::Escape($missing))
        $result.Error | Should -Match 'was not found'
    }
}
