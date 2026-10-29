BeforeAll {
    $script:Generator = Join-Path $PSScriptRoot '../../tools/appsettings-generator/appsettings-generator.ps1'
    $script:RepoSample = Join-Path $PSScriptRoot '../../src/ODVGateway/appsettings.json'
    $script:ExampleProfile = Join-Path $PSScriptRoot '../../tools/appsettings-generator/example.profile.json'

    # Runs the CLI in-process and returns its exit code plus everything it printed.
    function Invoke-Generator([string]$Command, [hashtable]$Arguments = @{}, [string]$Script = $script:Generator) {
        $global:LASTEXITCODE = 0
        $output = & $Script $Command @Arguments *>&1 | Out-String
        [pscustomobject]@{ ExitCode = $global:LASTEXITCODE; Output = $output }
    }

    # Copies the generator into a repository-shaped folder under $TestDrive, so its
    # default sample is a throwaway copy. With -Find, that exact source text is
    # replaced once to plant a defect the safety nets must catch.
    function New-GeneratorCopy([string]$Find, [string]$Replace = '') {
        $root = Join-Path $TestDrive ('copy-' + [guid]::NewGuid().ToString('N'))
        $toolDir = Join-Path $root 'tools/appsettings-generator'
        $sampleDir = Join-Path $root 'src/ODVGateway'
        $null = New-Item -ItemType Directory -Path $toolDir, $sampleDir
        $source = [System.IO.File]::ReadAllText($script:Generator)
        if ($Find) {
            ([regex]::Matches($source, [regex]::Escape($Find))).Count | Should -Be 1 -Because 'the planted defect must hit exactly one place'
            $source = $source.Replace($Find, $Replace)
        }
        $copy = Join-Path $toolDir 'appsettings-generator.ps1'
        [System.IO.File]::WriteAllText($copy, $source)
        $sample = Join-Path $sampleDir 'appsettings.json'
        [System.IO.File]::Copy($script:RepoSample, $sample)
        [pscustomobject]@{ Script = $copy; Sample = $sample }
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

    function Get-TagSampleBytes([string]$Version) {
        $root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = 'git'
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in @('-C', $root, 'show', "v${Version}:src/ODVGateway/appsettings.json")) {
            $startInfo.ArgumentList.Add($argument)
        }
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        $memory = [System.IO.MemoryStream]::new()
        try {
            $null = $process.Start()
            $stderrTask = $process.StandardError.ReadToEndAsync()
            $process.StandardOutput.BaseStream.CopyTo($memory)
            $process.WaitForExit()
            $stderr = $stderrTask.GetAwaiter().GetResult()
            if ($process.ExitCode -ne 0) { throw "git show failed for v${Version}: $stderr" }
            return , $memory.ToArray()
        }
        finally {
            $memory.Dispose()
            $process.Dispose()
        }
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
        $run.Output | Should -Match "ODVGateway\.noSuchKey' does not exist in the sample, so it cannot be unset"
        $run.Output | Should -Not -Match "list it in 'add'"
    }

    It 'Rejects a profile path with an empty segment and writes nothing: <Path>' -ForEach @(
        @{ Path = '.ODVGateway.x' }
        @{ Path = 'ODVGateway.inlineSources.' }
        @{ Path = 'Logging..NewKey' }
        @{ Path = 'Logging:LogLevel:' }
        @{ Path = '..ODVGateway.x' }
    ) {
        $json = '{ "set": { "' + $Path + '": 1 }, "add": [ "' + $Path + '" ] }'
        $profilePath = New-ProfileFile 'p.json' $json
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Sample = $script:Sample }
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match ([regex]::Escape("The profile path '$Path' has an empty segment."))
        Test-Path $script:Out | Should -BeFalse
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
        @{ Json = '{ "set": { "Logging.LogLevel.Default": "Basic Zm9vOmJhcg==" } }' }
        @{ Json = '{ "set": { "ODVGateway.upstream.authorization": "x" }, "add": [ "ODVGateway.upstream.authorization" ] }' }
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

    It 'Refuses to overwrite the sample with generate, explicit or default' {
        $profilePath = New-ProfileFile 'p.json' '{ "set": { "ODVGateway.sessionTtlMinutes": 45 } }'
        $explicit = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Sample; Sample = $script:Sample }
        $explicit.ExitCode | Should -Be 1
        $explicit.Output | Should -Match 'Refusing to overwrite the shipped sample'
        [System.IO.File]::ReadAllText($script:Sample) | Should -BeExactly $script:CommentedSample

        $copy = New-GeneratorCopy
        $before = [System.IO.File]::ReadAllBytes($copy.Sample)
        $default = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $copy.Sample } $copy.Script
        $default.ExitCode | Should -Be 1
        $default.Output | Should -Match 'Refusing to overwrite the shipped sample'
        [System.IO.File]::ReadAllBytes($copy.Sample) | Should -Be $before
    }

    It 'Refuses to overwrite the sample with new-profile, even with -Force' {
        $copy = New-GeneratorCopy
        $before = [System.IO.File]::ReadAllBytes($copy.Sample)
        $default = Invoke-Generator 'new-profile' @{ Out = $copy.Sample; Force = $true } $copy.Script
        $default.ExitCode | Should -Be 1
        $default.Output | Should -Match 'Refusing to overwrite the shipped sample'
        [System.IO.File]::ReadAllBytes($copy.Sample) | Should -Be $before

        $explicit = Invoke-Generator 'new-profile' @{ Out = $script:Sample; Sample = $script:Sample; Force = $true }
        $explicit.ExitCode | Should -Be 1
        [System.IO.File]::ReadAllText($script:Sample) | Should -BeExactly $script:CommentedSample
    }

    It 'Read-back verification refuses a broken <Name> edit and writes nothing' -ForEach @(
        @{
            Name = 'set'
            Find = '$text = $text.Substring(0, $value.Start) + $new + $text.Substring($value.End)'
            Profile = '{ "set": { "AllowedHosts": "viewer.example.org" } }'
            Message = "Verification failed: 'AllowedHosts' does not read back with the profile value."
        }
        @{
            Name = 'add'
            Find = '$text = $text.Substring(0, $at) + $comma + $layout.NewLine + $inner + $member + $text.Substring($at)'
            Profile = '{ "set": { "Logging.LogLevel.ODVGateway": "Debug" }, "add": [ "Logging.LogLevel.ODVGateway" ] }'
            Message = "Verification failed: 'Logging.LogLevel.ODVGateway' is missing from the generated configuration."
        }
        @{
            Name = 'unset'
            Find = '$text = $text.Substring(0, $start) + $text.Substring($end)'
            Profile = '{ "unset": [ "ODVGateway.sessionTtlMinutes" ] }'
            Message = "Verification failed: 'ODVGateway.sessionTtlMinutes' is still present after unset."
        }
    ) {
        # The planted defect turns the splice into a no-op; only Assert-Generated can notice.
        $copy = New-GeneratorCopy -Find $Find -Replace '$null = $text'
        $profilePath = New-ProfileFile 'p.json' $Profile
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out } $copy.Script
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match ([regex]::Escape($Message))
        Test-Path $script:Out | Should -BeFalse
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

Describe 'appsettings-generator tagged samples' {
    BeforeEach {
        $script:Out = Join-Path $TestDrive 'tagged-site.appsettings.json'
        if (Test-Path $script:Out) { Remove-Item $script:Out }
    }

    It 'Reproduces the v<Version> sample byte for byte from an empty profile' -ForEach @(
        @{ Version = '0.1.43' }
        @{ Version = '0.1.44' }
    ) {
        $profilePath = New-ProfileFile 'empty.json' '{}'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = $Version }
        $run.ExitCode | Should -Be 0
        $run.Output | Should -Match ([regex]::Escape("sample : src/ODVGateway/appsettings.json@v$Version"))
        $expected = Get-TagSampleBytes $Version
        [System.IO.File]::ReadAllBytes($script:Out) | Should -Be $expected
    }

    It 'Keeps the missing-path error for a profile entry absent from v0.1.43' {
        $profilePath = New-ProfileFile 'p.json' '{ "set": { "ODVGateway.signatures.enabled": true } }'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = '0.1.43' }
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "ODVGateway\.signatures\.enabled' does not exist in the sample"
        Test-Path $script:Out | Should -BeFalse
    }

    It 'Reports an unknown tag readably and writes nothing' {
        $profilePath = New-ProfileFile 'empty.json' '{}'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = '0.0.0' }
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "could not be read from tag 'v0\.0\.0'"
        $run.Output | Should -Not -Match 'UNEXPECTED ERROR'
        Test-Path $script:Out | Should -BeFalse
    }

    It 'Rejects a malformed version before trying Git in a copy without history' {
        $copy = New-GeneratorCopy
        $profilePath = New-ProfileFile 'empty.json' '{}'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = '0.1.43;not-a-version' } $copy.Script
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match 'Invalid -Version'
        $run.Output | Should -Not -Match 'could not be read from tag'
        Test-Path $script:Out | Should -BeFalse
    }

    It 'Reports a missing Git history readably in a repository-shaped copy' {
        $copy = New-GeneratorCopy
        $profilePath = New-ProfileFile 'empty.json' '{}'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = '0.1.43' } $copy.Script
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "could not be read from tag 'v0\.1\.43'"
        $run.Output | Should -Not -Match 'UNEXPECTED ERROR'
        Test-Path $script:Out | Should -BeFalse
    }

    It 'Checks a generated file against its own tag and detects drift against another tag' {
        $profilePath = New-ProfileFile 'empty.json' '{}'
        $generate = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = '0.1.43' }
        $generate.ExitCode | Should -Be 0

        $sameTag = Invoke-Generator 'check' @{ Profile = $profilePath; Config = $script:Out; Version = '0.1.43' }
        $sameTag.ExitCode | Should -Be 0
        $sameTag.Output | Should -Match 'up to date'

        $otherTag = Invoke-Generator 'check' @{ Profile = $profilePath; Config = $script:Out; Version = '0.1.44' }
        $otherTag.ExitCode | Should -Be 1
        $otherTag.Output | Should -Match 'DRIFT'
    }

    It 'Refuses to combine -Version and -Sample' {
        $profilePath = New-ProfileFile 'empty.json' '{}'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = '0.1.43'; Sample = $script:RepoSample }
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match '-Sample and -Version options cannot be combined'
        Test-Path $script:Out | Should -BeFalse
    }
}

Describe 'appsettings-generator version gates and generate-all' {
    BeforeEach {
        $script:Out = Join-Path $TestDrive 'gated-site.appsettings.json'
        if (Test-Path $script:Out) { Remove-Item $script:Out }
        $script:OutDir = Join-Path $TestDrive ('all-versions-' + [guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $script:OutDir
    }

    It 'Skips a since-gated signatures entry for v0.1.43 and reports the bound' {
        $profilePath = New-ProfileFile 'p.json' '{ "set": { "ODVGateway.signatures.enabled": true }, "since": { "ODVGateway.signatures.enabled": "0.1.44" } }'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = '0.1.43' }
        $run.ExitCode | Should -Be 0
        $run.Output | Should -Match '  skipped  ODVGateway\.signatures\.enabled: not in 0\.1\.43 \(since 0\.1\.44\)'
        $run.Output | Should -Match 'verified: 0 profile entries'
        $missing = [System.Text.Json.JsonElement]::new()
        (Read-Loader $script:Out).GetProperty('ODVGateway').TryGetProperty('signatures', [ref]$missing) | Should -BeFalse
    }

    It 'Applies the same since-gated signatures entry for v0.1.44 and verifies it' {
        $profilePath = New-ProfileFile 'p.json' '{ "set": { "ODVGateway.signatures.enabled": true }, "since": { "ODVGateway.signatures.enabled": "0.1.44" } }'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = '0.1.44' }
        $run.ExitCode | Should -Be 0
        $run.Output | Should -Match 'set    ODVGateway\.signatures\.enabled: false -> true'
        $run.Output | Should -Not -Match 'skipped  ODVGateway\.signatures\.enabled'
        (Read-Loader $script:Out).GetProperty('ODVGateway').GetProperty('signatures').GetProperty('enabled').GetBoolean() | Should -BeTrue
    }

    It 'Applies an until-gated entry at its bound and skips it above the bound' {
        $profilePath = New-ProfileFile 'p.json' '{ "set": { "ODVGateway.sessionTtlMinutes": 45 }, "until": { "ODVGateway.sessionTtlMinutes": "0.1.43" } }'
        $atBound = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = '0.1.43' }
        $atBound.ExitCode | Should -Be 0
        (Read-Loader $script:Out).GetProperty('ODVGateway').GetProperty('sessionTtlMinutes').GetInt32() | Should -Be 45

        $aboveBound = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = '0.1.44' }
        $aboveBound.ExitCode | Should -Be 0
        $aboveBound.Output | Should -Match '  skipped  ODVGateway\.sessionTtlMinutes: not in 0\.1\.44 \(until 0\.1\.43\)'
        (Read-Loader $script:Out).GetProperty('ODVGateway').GetProperty('sessionTtlMinutes').GetInt32() | Should -Be 30
    }

    It 'Compares version gates numerically rather than lexically' {
        $profilePath = New-ProfileFile 'p.json' '{ "set": { "ODVGateway.sessionTtlMinutes": 45 }, "since": { "ODVGateway.sessionTtlMinutes": "0.1.9" } }'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = '0.1.44' }
        $run.ExitCode | Should -Be 0
        $run.Output | Should -Match 'set    ODVGateway\.sessionTtlMinutes: 30 -> 45'
        $run.Output | Should -Not -Match 'skipped  ODVGateway'
        (Read-Loader $script:Out).GetProperty('ODVGateway').GetProperty('sessionTtlMinutes').GetInt32() | Should -Be 45
    }

    It 'Rejects a version gate without a matching set or unset entry' {
        $profilePath = New-ProfileFile 'p.json' '{ "set": { "ODVGateway.sessionTtlMinutes": 45 }, "since": { "ODVGateway.signatures.enabled": "0.1.44" } }'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = '0.1.43' }
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "The 'since' entry 'ODVGateway\.signatures\.enabled' has no corresponding 'set' or 'unset' entry"
        Test-Path $script:Out | Should -BeFalse
    }

    It 'Skips and applies a gated add entry without weakening the sample-path guards' {
        $profilePath = New-ProfileFile 'p.json' '{ "set": { "ODVGateway.futureSetting": "example" }, "add": [ "ODVGateway.futureSetting" ], "since": { "ODVGateway.futureSetting": "0.1.44" } }'
        $skipped = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = '0.1.43' }
        $skipped.ExitCode | Should -Be 0
        $skipped.Output | Should -Match '  skipped  ODVGateway\.futureSetting: not in 0\.1\.43 \(since 0\.1\.44\)'
        $missing = [System.Text.Json.JsonElement]::new()
        (Read-Loader $script:Out).GetProperty('ODVGateway').TryGetProperty('futureSetting', [ref]$missing) | Should -BeFalse

        $applied = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = '0.1.44' }
        $applied.ExitCode | Should -Be 0
        $applied.Output | Should -Match 'add    ODVGateway\.futureSetting'
        (Read-Loader $script:Out).GetProperty('ODVGateway').GetProperty('futureSetting').GetString() | Should -Be 'example'
    }

    It 'Skips a since-gated unset for v0.1.43 and writes the file' {
        $profilePath = New-ProfileFile 'p.json' '{ "unset": [ "ODVGateway.signatures.revocationMode" ], "since": { "ODVGateway.signatures.revocationMode": "0.1.44" } }'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = '0.1.43' }
        $run.ExitCode | Should -Be 0
        $run.Output | Should -Match '  skipped  ODVGateway\.signatures\.revocationMode: not in 0\.1\.43 \(since 0\.1\.44\)'
        Test-Path $script:Out | Should -BeTrue
    }

    It 'Applies the same since-gated unset for v0.1.44' {
        $profilePath = New-ProfileFile 'p.json' '{ "unset": [ "ODVGateway.signatures.revocationMode" ], "since": { "ODVGateway.signatures.revocationMode": "0.1.44" } }'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = '0.1.44' }
        $run.ExitCode | Should -Be 0
        $run.Output | Should -Match 'unset  ODVGateway\.signatures\.revocationMode'
        $missing = [System.Text.Json.JsonElement]::new()
        (Read-Loader $script:Out).GetProperty('ODVGateway').GetProperty('signatures').TryGetProperty('revocationMode', [ref]$missing) | Should -BeFalse
    }

    It 'Keeps the missing-path guard for an ungated unset absent from v0.1.43' {
        $profilePath = New-ProfileFile 'p.json' '{ "unset": [ "ODVGateway.signatures.revocationMode" ] }'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out; Version = '0.1.43' }
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "ODVGateway\.signatures\.revocationMode' does not exist in the sample, so it cannot be unset"
        Test-Path $script:Out | Should -BeFalse
    }

    It 'Generates both releases and checks each output against its own version' {
        $profilePath = New-ProfileFile 'p.json' '{ "name": "Example Hospital", "set": { "ODVGateway.signatures.enabled": true, "ODVGateway.sessionTtlMinutes": 45 }, "since": { "ODVGateway.signatures.enabled": "0.1.44" } }'
        $run = Invoke-Generator 'generate-all' @{ Profile = $profilePath; Versions = @('0.1.43', '0.1.44'); OutDir = $script:OutDir }
        $run.ExitCode | Should -Be 0
        $run.Output | Should -Match 'generate-all \[0\.1\.43\]'
        $run.Output | Should -Match 'generate-all \[0\.1\.44\]'
        $run.Output | Should -Match '  skipped  ODVGateway\.signatures\.enabled: not in 0\.1\.43 \(since 0\.1\.44\)'
        $old = Join-Path $script:OutDir '0.1.43/appsettings.json'
        $new = Join-Path $script:OutDir '0.1.44/appsettings.json'
        Test-Path $old | Should -BeTrue
        Test-Path $new | Should -BeTrue

        $oldCheck = Invoke-Generator 'check' @{ Profile = $profilePath; Config = $old; Version = '0.1.43' }
        $oldCheck.ExitCode | Should -Be 0
        $newCheck = Invoke-Generator 'check' @{ Profile = $profilePath; Config = $new; Version = '0.1.44' }
        $newCheck.ExitCode | Should -Be 0
        $missing = [System.Text.Json.JsonElement]::new()
        (Read-Loader $old).GetProperty('ODVGateway').TryGetProperty('signatures', [ref]$missing) | Should -BeFalse
        (Read-Loader $new).GetProperty('ODVGateway').GetProperty('signatures').GetProperty('enabled').GetBoolean() | Should -BeTrue
    }

    It 'Stops generate-all on the first failing version and writes no later version' {
        $profilePath = New-ProfileFile 'p.json' '{ "set": { "ODVGateway.signatures.enabled": true } }'
        $run = Invoke-Generator 'generate-all' @{ Profile = $profilePath; Versions = @('0.1.43', '0.1.44'); OutDir = $script:OutDir }
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "generate-all failed for version '0\.1\.43'"
        $run.Output | Should -Not -Match 'generate-all \[0\.1\.44\]'
        Test-Path (Join-Path $script:OutDir '0.1.43/appsettings.json') | Should -BeFalse
        Test-Path (Join-Path $script:OutDir '0.1.44/appsettings.json') | Should -BeFalse
    }

    It 'Uses -Name for every generate-all output file' {
        $profilePath = New-ProfileFile 'empty.json' '{}'
        $run = Invoke-Generator 'generate-all' @{ Profile = $profilePath; Versions = @('0.1.43'); OutDir = $script:OutDir; Name = 'site.json' }
        $run.ExitCode | Should -Be 0
        Test-Path (Join-Path $script:OutDir '0.1.43/site.json') | Should -BeTrue
        Test-Path (Join-Path $script:OutDir '0.1.43/appsettings.json') | Should -BeFalse
    }

    It 'Refuses a gated profile on an ungated run so no value is silently dropped' {
        $profilePath = New-ProfileFile 'p.json' '{ "set": { "ODVGateway.sessionTtlMinutes": 45 }, "since": { "ODVGateway.sessionTtlMinutes": "0.1.44" } }'
        $run = Invoke-Generator 'generate' @{ Profile = $profilePath; Out = $script:Out }
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match "requires a known sample version"
        Test-Path $script:Out | Should -BeFalse
    }

    It 'Rejects duplicate and empty generate-all versions before writing anything: <Versions>' -ForEach @(
        @{ Versions = @('0.1.43', '0.1.43'); Message = 'duplicate version' }
        @{ Versions = @('0.1.43,,0.1.44'); Message = 'empty entry' }
    ) {
        $profilePath = New-ProfileFile 'empty.json' '{}'
        $run = Invoke-Generator 'generate-all' @{ Profile = $profilePath; Versions = $Versions; OutDir = $script:OutDir }
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match $Message
        Test-Path (Join-Path $script:OutDir '0.1.43/appsettings.json') | Should -BeFalse
        Test-Path (Join-Path $script:OutDir '0.1.44/appsettings.json') | Should -BeFalse
    }

    It 'Refuses to combine generate-all with -Sample' {
        $profilePath = New-ProfileFile 'empty.json' '{}'
        $run = Invoke-Generator 'generate-all' @{ Profile = $profilePath; Versions = @('0.1.43'); OutDir = $script:OutDir; Sample = $script:RepoSample }
        $run.ExitCode | Should -Be 1
        $run.Output | Should -Match '-Sample option cannot be combined with generate-all'
        Test-Path (Join-Path $script:OutDir '0.1.43/appsettings.json') | Should -BeFalse
    }
}
