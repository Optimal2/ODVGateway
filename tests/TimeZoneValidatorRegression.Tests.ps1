[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseCompatibleCommands', '',
    Justification = 'Pester 6 parameters come from the pinned module.')]
param()

Describe 'Check 21: nested interpolation and nameof boundaries' {
    BeforeAll {
        . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1')
    }

    It 'Detects the call after adjacent nested closing braces' {
        $text = 'var s = $"{new { A = 1 }}"; TimeZoneInfo.FindSystemTimeZoneById(id);'
        Test-DirectTimeZonePlatformCall (Remove-CSharpCommentsAndStringLiterals $text) | Should -BeTrue
    }

    It 'Masks a mention in a string after adjacent nested closing braces' {
        $text = 'var s = $"{new { A = 1 }}"; var prose = "TimeZoneInfo.FindSystemTimeZoneById(id)";'
        Test-DirectTimeZonePlatformCall (Remove-CSharpCommentsAndStringLiterals $text) | Should -BeFalse
    }

    It 'Handles nested switch braces and nested string literals' {
        $text = 'var s = $"{x switch { 1 => "a", _ => "b" }}"; TimeZoneInfo.FindSystemTimeZoneById(id);'
        Test-DirectTimeZonePlatformCall (Remove-CSharpCommentsAndStringLiterals $text) | Should -BeTrue
    }

    It 'Preserves multiline verbatim layout around a nested hole' {
        $text = '$@"first' + "`r`n" + '{new { A = 1 }} last' + "`r`n" + '"; TimeZoneInfo.FindSystemTimeZoneById(id);'
        $masked = Remove-CSharpCommentsAndStringLiterals $text
        $masked.Length | Should -Be $text.Length
        ($masked -replace '[^\r\n]', '') | Should -Be ($text -replace '[^\r\n]', '')
        Test-DirectTimeZonePlatformCall $masked | Should -BeTrue
    }

    It 'Counts raw-string nested braces individually before its two-brace delimiter' {
        $text = 'var s = $$"""{{new { A = new { B = 1 }}}}}"""; TimeZoneInfo.FindSystemTimeZoneById(id);'
        Test-DirectTimeZonePlatformCall (Remove-CSharpCommentsAndStringLiterals $text) | Should -BeTrue
    }

    It 'Keeps a call inside a raw-string hole visible after a nested object' {
        $text = 'var s = $$"""{{new { A = 1 }.ToString() + TimeZoneInfo.FindSystemTimeZoneById(id)}}""";'
        Test-DirectTimeZonePlatformCall (Remove-CSharpCommentsAndStringLiterals $text) | Should -BeTrue
    }

    It 'Does not mistake xnameof for the nameof keyword' {
        Test-DirectTimeZonePlatformCall 'xnameof(TimeZoneInfo.FindSystemTimeZoneById(id));' | Should -BeTrue
    }

    It 'Still excludes the actual nameof keyword with whitespace' {
        Test-DirectTimeZonePlatformCall 'nameof ( TimeZoneInfo.FindSystemTimeZoneById)' | Should -BeFalse
    }
}


Describe 'Shared C# masking H1-H5' {
    BeforeAll { . (Join-Path $PSScriptRoot 'Validate-ComponentVersions.TestHelpers.ps1') }
    It 'Masks multiline verbatim prose (H1)' {
        $text = '@"first' + "`n" + 'TimeZoneInfo.FindSystemTimeZoneById prose"'
        Test-DirectTimeZonePlatformCall (Remove-CSharpCommentsAndStringLiterals $text) | Should -BeFalse
    }
    It 'Detects code after multiline verbatim prose (H1)' {
        $text = '@"first' + "`n" + 'last"; TimeZoneInfo.FindSystemTimeZoneById(id);'
        Test-DirectTimeZonePlatformCall (Remove-CSharpCommentsAndStringLiterals $text) | Should -BeTrue
    }
    It 'Detects code inside interpolation (H2)' {
        Test-DirectTimeZonePlatformCall (Remove-CSharpCommentsAndStringLiterals '$"{TimeZoneInfo.FindSystemTimeZoneById(id)}"') | Should -BeTrue
    }
    It 'Handles a quote inside a verbatim literal (H3)' {
        Test-DirectTimeZonePlatformCall (Remove-CSharpCommentsAndStringLiterals '@""""; TimeZoneInfo.FindSystemTimeZoneById(id);') | Should -BeTrue
    }
    It 'Collects static and alias globals and excludes nameof (H4)' {
        $globals = Get-CSharpGlobalTimeZoneDirectives ("global using static System.TimeZoneInfo;`n" + 'global using X = System.TimeZoneInfo;')
        $globals.UsingStatic | Should -BeTrue
        $globals.Aliases | Should -Contain X
        Test-DirectTimeZonePlatformCall 'FindSystemTimeZoneById(id)' -GlobalUsingStatic:$globals.UsingStatic | Should -BeTrue
        Test-DirectTimeZonePlatformCall 'X.FindSystemTimeZoneById(id)' -GlobalAliases $globals.Aliases | Should -BeTrue
        Test-DirectTimeZonePlatformCall 'nameof(FindSystemTimeZoneById)' -GlobalUsingStatic:$globals.UsingStatic | Should -BeFalse
    }
    It 'Fails honestly for a missing repository (H5)' {
        { Get-CSharpTestProjectDirectory (Join-Path $TestDrive 'missing') } | Should -Throw '*does not exist*'
    }
}
