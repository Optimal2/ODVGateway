[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '', Justification = 'Pester 6 parameters come from the pinned module.')]
param()
Describe 'Check 20: summary gating' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1')
        function Write-EmbeddedSqlContentAsOnDisk {
            <#
            .SYNOPSIS
            Mimics the PRE-FIX embed behavior: embeds the SQL bytes exactly as
            they lie on disk, with no .gitattributes normalization.
            #>
            param(
                [Parameter(Mandatory = $true)][string]$RepoRoot,
                [Parameter(Mandatory = $true)][string]$DefinitionRelativePath,
                [Parameter(Mandatory = $true)][string]$SqlRelativePath
            )

            $definitionPath = Join-Path $RepoRoot $DefinitionRelativePath
            $sqlText = Get-Content -LiteralPath (Join-Path $RepoRoot $SqlRelativePath) -Raw -Encoding UTF8
            $content = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($sqlText))
            $sha256 = Get-Sha256Hex -Text $sqlText

            $definition = Get-Content -LiteralPath $definitionPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $definition.sqlScripts[0] | Add-Member -NotePropertyName contentEncoding -NotePropertyValue 'base64-utf8' -Force
            $definition.sqlScripts[0] | Add-Member -NotePropertyName content -NotePropertyValue $content -Force
            $definition.sqlScripts[0] | Add-Member -NotePropertyName sha256 -NotePropertyValue $sha256 -Force
            [System.IO.File]::WriteAllText($definitionPath, ($definition | ConvertTo-Json -Depth 10), [System.Text.UTF8Encoding]::new($false))
        }

        function Save-TextFile {
            param(
                [Parameter(Mandatory = $true)][string]$Path,
                [Parameter(Mandatory = $true)][string]$Content
            )

            [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
        }

        function Invoke-GitCommitAll {
            param(
                [Parameter(Mandatory = $true)][string]$RepoRoot,
                [Parameter(Mandatory = $true)][string]$Message
            )

            # git writes advisory noise ('LF will be replaced by CRLF ...') to
            # stderr, and a Windows PowerShell 5.1 native-command stderr line
            # becomes a RemoteException that $ErrorActionPreference = 'Stop'
            # (the harness setting) escalates to a terminating error -- even
            # through 2>&1, where Out-String re-emits the wrapped ErrorRecord.
            # Drop the preference for the two native calls and judge them by
            # $LASTEXITCODE alone, keeping the output for the throw message.
            $previousPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $addOutput = & git -C $RepoRoot add -A 2>&1 | Out-String
                $addExitCode = $LASTEXITCODE
                $commitOutput = & git -C $RepoRoot commit -m $Message --quiet 2>&1 | Out-String
                $commitExitCode = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference = $previousPreference
            }
            if ($addExitCode -ne 0) { throw "git add failed: $addOutput" }
            if ($commitExitCode -ne 0) { throw "git commit failed: $commitOutput" }
        }

    }
    It 'Fails when CRLF is embedded where .gitattributes declares eol=lf' {
        $repoRoot = Join-Path ([System.IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
        try {
            $validatorPath = New-TemporaryTestRepository -RootPath $repoRoot -SqlContent "SELECT 1;`r`nSELECT 2;`r`n"
            Save-TextFile -Path (Join-Path $repoRoot '.gitattributes') -Content "*.sql text eol=lf`n"
            Write-EmbeddedSqlContentAsOnDisk -RepoRoot $repoRoot -DefinitionRelativePath 'TestModule/test.module-definition.json' -SqlRelativePath 'TestModule/sql/init.sql'
            Invoke-GitCommitAll -RepoRoot $repoRoot -Message 'Embed SQL with CRLF endings under an eol=lf declaration'

            $result = Invoke-ValidatorWithOutput -ValidatorPath $validatorPath

            $result.ExitCode | Should -Not -Be 0
            $result.Output | Should -Match 'wrong line endings'
            $result.Output | Should -Match 'eol=lf'

            # The summary line used to print its check mark unconditionally,
            # so this failing run still read as green in the summary (second
            # opinion, 2026-10-08). The mark now follows the error counter,
            # exactly like Check 16: no check-mark line, one cross-mark line.
            $checkMark = [char]0x2713
            $crossMark = [char]0x2717
            $result.Output | Should -Not -Match "$checkMark[^\r\n]*carry the line endings"
            $result.Output | Should -Match "$crossMark[^\r\n]*0 of 1 embedded SQL script\(s\) carry the line endings \.gitattributes declares \(1 error\(s\)\)"
        }
        finally {
            Remove-TemporaryTestRepository -RootPath $repoRoot
        }
    }

    It 'Carries exactly one Check 20 error counter initialization' {
        # Same single-counter pin as Check 16: a duplicated block that resets
        # the counters would let the check-mark line print on a failed run.
        $validatorSource = Get-Content -LiteralPath $scriptPath -Raw -Encoding UTF8
        @([regex]::Matches($validatorSource, '(?m)^\$embeddedEolErrorCount = 0\r?$')).Count | Should -Be 1
    }

}
