BeforeAll {
    $script:Generator = Join-Path $PSScriptRoot '../../tools/appsettings-generator/appsettings-generator.ps1'
    $script:RepoSample = Join-Path $PSScriptRoot '../../src/ODVGateway/appsettings.json'
    $script:ExampleProfile = Join-Path $PSScriptRoot '../../tools/appsettings-generator/example.profile.json'

    # Runs the CLI in-process and returns its exit code plus everything it printed.
    function Invoke-Generator([string]$Command, [hashtable]$Arguments = @{}) {
        $global:LASTEXITCODE = 0
        $output = & $script:Generator $Command @Arguments *>&1 | Out-String
        [pscustomobject]@{ ExitCode = $global:LASTEXITCODE; Output = $output }
    }

    function New-ProfileFile([string]$Name, [string]$Json) {
        $path = Join-Path $TestDrive $Name
        [System.IO.File]::WriteAllText($path, $Json)
        $path
    }

    function Read-Loader([string]$Path) {
        $options = [System.Text.Json.JsonDocumentOptions]::new()
        $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Skip
        $options.AllowTrailingCommas = $true
        [System.Text.Json.JsonDocument]::Parse([System.IO.File]::ReadAllText($Path), $options).RootElement
    }

    $script:CommentedSample = (@(
        '{',
        '  // Logging block.',
        '  "Logging": {',
        '    "LogLevel": {',
        '      "Default": "Information", /* inline note */',
        '      "Microsoft.AspNetCore": "Warning"',
        '    }',
        '  },',
        '  "ODVGateway": {',
        '    "sessionTtlMinutes": 30,',
        '    "trustedSourceRoots": [],',
        '    "metadataAliases": {},',
        '    "inlineSources": {',
        '      "maxFileBytes": 2097152,',
        '      "extensions": [ "tif", "tiff" ],',
        '    }',
        '  }',
        '}',
        ''
    ) -join "`n")
}

Describe 'appsettings-generator' {
    BeforeEach {
        $script:Sample = Join-Path $TestDrive 'sample.appsettings.json'
        [System.IO.File]::WriteAllText($script:Sample, $script:CommentedSample)
        $script:Out = Join-Path $TestDrive 'site.appsettings.json'
        if (Test-Path $script:Out) { Remove-Item $script:Out }
    }

    It 'Reproduces the shipped sample byte for byte from an empty profile' {
        $profilePath = New-ProfileFile 'empty.json' '{}'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out }
        $run.ExitCode | Should -Be 0
        [System.IO.File]::ReadAllBytes($script:Out) | Should -Be ([System.IO.File]::ReadAllBytes($script:RepoSample))
    }

    It 'Reproduces a commented LF sample byte for byte from an empty profile' {
        $profilePath = New-ProfileFile 'empty.json' '{ "name": "Example Hospital", "set": {}, "unset": [] }'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Sample = $script:Sample }
        $run.ExitCode | Should -Be 0
        [System.IO.File]::ReadAllBytes($script:Out) | Should -Be ([System.IO.File]::ReadAllBytes($script:Sample))
    }

    It 'Round-trips the example profile against the shipped sample: generate, then check is clean' {
        $generate = Invoke-Generator 'generate' @{ Profile = $script:ExampleProfile; Out = $script:Out }
        $generate.ExitCode | Should -Be 0
        $generate.Output | Should -Match 'set    ODVGateway\.sessionTtlMinutes: 30 -> 45'
        $generate.Output | Should -Match 'unset  ODVGateway\.contentSecurityPolicy: null -> \(removed\)'

        $check = Invoke-Generator 'check' @{ Profile = $script:ExampleProfile; Config = $script:Out }
        $check.ExitCode | Should -Be 0
        $check.Output | Should -Match 'up to date'

        $root = Read-Loader $script:Out
        $root.GetProperty('ODVGateway').GetProperty('sessionTtlMinutes').GetInt32() | Should -Be 45
        $root.GetProperty('Logging').GetProperty('LogLevel').GetProperty('Microsoft.AspNetCore').GetString() | Should -Be 'Error'
        $value = [System.Text.Json.JsonElement]::new()
        $root.GetProperty('ODVGateway').TryGetProperty('contentSecurityPolicy', [ref]$value) | Should -BeFalse
    }

    It 'Changes only the addressed value and keeps comments and layout' {
        $profilePath = New-ProfileFile 'p.json' '{ "set": { "ODVGateway.sessionTtlMinutes": 45, "ODVGateway.inlineSources.extensions": [ "png" ] } }'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Sample = $script:Sample }
        $run.ExitCode | Should -Be 0
        $expected = $script:CommentedSample.Replace('"sessionTtlMinutes": 30', '"sessionTtlMinutes": 45').Replace('[ "tif", "tiff" ]', '[ "png" ]')
        [System.IO.File]::ReadAllText($script:Out) | Should -BeExactly $expected
    }

    It 'Fails on a path that is missing from the sample and writes nothing' {
        $profilePath = New-ProfileFile 'p.json' '{ "set": { "ODVGateway.sessionTtlMinutez": 45 } }'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Sample = $script:Sample }
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "ODVGateway\.sessionTtlMinutez' does not exist in the sample"
        Test-Path $script:Out | Should -BeFalse
    }

    It 'Fails on an unset path that is missing from the sample' {
        $profilePath = New-ProfileFile 'p.json' '{ "unset": [ "ODVGateway.noSuchKey" ] }'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Sample = $script:Sample }
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match 'does not exist in the sample'
    }

    It 'Inserts a path marked add at the end of its block, also into an empty object' {
        $profilePath = New-ProfileFile 'p.json' (@(
            '{ "set": { "Logging.LogLevel.ODVGateway": "Debug", "ODVGateway.metadataAliases.department": "unit",',
            '  "ODVGateway.inlineSources.limits.maxCount": 5 },',
            '  "add": [ "Logging.LogLevel.ODVGateway", "ODVGateway.metadataAliases.department", "ODVGateway.inlineSources.limits.maxCount" ] }'
        ) -join "`n")
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Sample = $script:Sample }
        $run.ExitCode | Should -Be 0
        $run.Output | Should -Match 'add    Logging\.LogLevel\.ODVGateway: \(absent\) -> "Debug"'
        $text = [System.IO.File]::ReadAllText($script:Out)
        $text | Should -Match ([regex]::Escape("      `"Microsoft.AspNetCore`": `"Warning`",`n      `"ODVGateway`": `"Debug`"`n    }"))
        $text | Should -Match ([regex]::Escape("    `"metadataAliases`": {`n      `"department`": `"unit`"`n    },"))
        $text | Should -Match ([regex]::Escape("      `"extensions`": [ `"tif`", `"tiff`" ],`n      `"limits`": {`n        `"maxCount`": 5`n      }`n    }"))
        (Read-Loader $script:Out).GetProperty('ODVGateway').GetProperty('inlineSources').GetProperty('limits').GetProperty('maxCount').GetInt32() | Should -Be 5
    }

    It 'Rejects an add marker on a path that already exists' {
        $profilePath = New-ProfileFile 'p.json' '{ "set": { "ODVGateway.sessionTtlMinutes": 45 }, "add": [ "ODVGateway.sessionTtlMinutes" ] }'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Sample = $script:Sample }
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match 'already exists in the sample'
    }

    It 'Rejects an add marker without a set value' {
        $profilePath = New-ProfileFile 'p.json' '{ "add": [ "ODVGateway.newKey" ] }'
        (Invoke-Generator 'generate' @{ Profile = $profilePath; Sample = $script:Sample }).ExitCode | Should -Be 1
    }

    It 'Removes unset keys, including the last key of a block, and keeps valid JSON' {
        $profilePath = New-ProfileFile 'p.json' '{ "unset": [ "Logging.LogLevel.Microsoft.AspNetCore", "ODVGateway.sessionTtlMinutes" ] }'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Sample = $script:Sample }
        $run.ExitCode | Should -Be 0
        $text = [System.IO.File]::ReadAllText($script:Out)
        $text | Should -Match ([regex]::Escape("      `"Default`": `"Information`" /* inline note */`n    }"))
        $text | Should -Not -Match 'sessionTtlMinutes'
        $text | Should -Match '// Logging block\.'
    }

    It 'Detects drift with check and exits 1' {
        $generate = Invoke-Generator 'generate' @{ Profile = $script:ExampleProfile; Out = $script:Out }
        $generate.ExitCode | Should -Be 0
        $text = [System.IO.File]::ReadAllText($script:Out).Replace('"sessionTtlMinutes": 45', '"sessionTtlMinutes": 50')
        [System.IO.File]::WriteAllText($script:Out, $text)

        $check = Invoke-Generator 'check' @{ Profile = $script:ExampleProfile; Config = $script:Out }
        $check.ExitCode | Should -Be 1
        $check.Output | Should -Match 'DRIFT'
        $check.Output | Should -Match 'changed    ODVGateway\.sessionTtlMinutes: expected 45, found 50'
    }

    It 'Reports layout-only drift when values are equal' {
        $profilePath = New-ProfileFile 'empty.json' '{}'
        $config = Join-Path $TestDrive 'layout.json'
        [System.IO.File]::WriteAllText($config, $script:CommentedSample.Replace('  // Logging block.', '  // Edited comment.'))
        $check = Invoke-Generator 'check' @{ Profile = $profilePath; Config = $config; Sample = $script:Sample }
        $check.ExitCode | Should -Be 1
        $check.Output | Should -Match 'text differs from line 2'
    }

    It 'Refuses a profile value that looks like a secret: <Json>' -ForEach @(
        @{ Json = '{ "set": { "ODVGateway.signatures.clientSecret": "x" }, "add": [ "ODVGateway.signatures.clientSecret" ] }' }
        @{ Json = '{ "set": { "ConnectionStrings.Main": "Server=db.example.org" }, "add": [ "ConnectionStrings.Main" ] }' }
        @{ Json = '{ "set": { "ODVGateway.signatures": { "apiKey": "x" } } }' }
        @{ Json = '{ "set": { "ODVGateway.openDocViewerDistPath": "https://user:pass@viewer.example.org/" } }' }
    ) {
        $profilePath = New-ProfileFile 'secret.json' $Json
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Sample = $script:Sample }
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match 'environment variables'
        Test-Path $script:Out | Should -BeFalse
    }

    It 'Never reads or writes appsettings.Development.json' {
        $profilePath = New-ProfileFile 'empty.json' '{}'
        $development = Join-Path $TestDrive 'appsettings.Development.json'
        [System.IO.File]::WriteAllText($development, '{}')
        $write = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $development; Sample = $script:Sample }
        $write.ExitCode | Should -Be 1
        $write.Output | Should -Match 'never reads or writes'
        [System.IO.File]::ReadAllText($development) | Should -Be '{}'
        (Invoke-Generator 'generate' @{ Profile = $profilePath; Sample = $development }).ExitCode | Should -Be 1
    }

    It 'Rejects unknown profile properties instead of ignoring them' {
        $profilePath = New-ProfileFile 'p.json' '{ "sett": { "ODVGateway.sessionTtlMinutes": 45 } }'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Sample = $script:Sample }
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "Unknown profile property 'sett'"
    }

    It 'Rejects overlapping profile entries' {
        $profilePath = New-ProfileFile 'p.json' '{ "set": { "ODVGateway.inlineSources": {}, "ODVGateway.inlineSources.maxFileBytes": 1 } }'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Sample = $script:Sample }
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match 'overlap'
    }

    It 'Writes a commented starter profile that generates cleanly and is not overwritten without -Force' {
        $starter = Join-Path $TestDrive 'starter.profile.json'
        (Invoke-Generator 'new-profile' @{ Out = $starter }).ExitCode | Should -Be 0
        [System.IO.File]::ReadAllText($starter) | Should -Match '// ODVGateway site profile'
        (Invoke-Generator 'generate' @{ Profile = $starter; Out = $script:Out }).ExitCode | Should -Be 0
        (Invoke-Generator 'new-profile' @{ Out = $starter }).ExitCode | Should -Be 1
        (Invoke-Generator 'new-profile' @{ Out = $starter; Force = $true }).ExitCode | Should -Be 0
    }

    It 'Prints usage and exits 1 for an unknown command' {
        $run = Invoke-Generator 'frobnicate'
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match 'Usage:'
    }
}
