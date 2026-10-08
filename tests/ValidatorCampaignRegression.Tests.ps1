[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '', Justification = 'Pester 6 parameters come from the pinned module.')]
param()
Describe 'Check 8: module-definition SQL diff enforcement' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1')
    }

    It 'Completes with its normal summary when a definition without sqlScripts is diffed against a base ref' {
        # Second opinion (2026-10-08): Check 8 read $definition.sqlScripts
        # directly, so under Set-StrictMode a legal definition WITHOUT
        # sqlScripts crashed the validator with PropertyNotFoundException --
        # only when a base ref made Check 8 run at all, which is why the
        # base-ref-less StrictMode fixture never saw it.
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        $originalLocation = Get-Location
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot
            Set-Location -LiteralPath $repoRoot
            $baseCommit = (& git -C $repoRoot rev-parse HEAD).Trim()

            # Remove sqlScripts from the definition and bump definitionVersion
            # (Check 12 requires a bump for any definition content change).
            $definitionPath = Join-Path $repoRoot 'TestModule/test.module-definition.json'
            $definition = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $definition.PSObject.Properties.Remove('sqlScripts')
            $definition.definitionVersion = '2.0.0'
            [System.IO.File]::WriteAllText($definitionPath, ($definition | ConvertTo-Json -Depth 10), [System.Text.Encoding]::UTF8)

            $manifestPath = Join-Path $repoRoot 'omp-components.json'
            $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $manifest.moduleDefinitions[0].definitionVersion = '2.0.0'
            $manifest.components[0].minModuleDefinitionVersion = '2.0.0'
            $manifest.components[0].version = '2.0.0'
            $manifest.repositoryVersion = '2.0.0'
            [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10), [System.Text.Encoding]::UTF8)

            & git -C $repoRoot add -A
            if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
            & git -C $repoRoot commit -m 'Drop sqlScripts and bump definitionVersion' --quiet
            if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath -BaseCommit $baseCommit

            $result.ExitCode | Should -Be 0 -Because $result.Output
            $result.Output | Should -Match 'Component version validation passed'
            $result.Output | Should -Not -Match 'cannot be found on this object'
        }
        finally {
            Set-Location $originalLocation
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }
}
