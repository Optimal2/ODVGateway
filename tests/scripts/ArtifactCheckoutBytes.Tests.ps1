# Git checkout bytes are compiler and artifact inputs, including PDB checksums.
Describe 'Artifact input bytes across Git checkout settings' {
    BeforeAll {
        function Invoke-FixtureGit {
            param([string]$Directory, [string[]]$Arguments)
            Push-Location $Directory
            try {
                $output = @(& git @Arguments 2>&1)
                $code = $LASTEXITCODE
                if ($code -ne 0) { throw "Fixture git failed ($code): $output" }
            }
            finally { Pop-Location }
        }
        $fixture = Join-Path $TestDrive 'source'
        $emptyHooks = Join-Path $TestDrive 'empty-hooks'
        New-Item -ItemType Directory -Path $fixture, $emptyHooks | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../.gitattributes') -Destination $fixture
        $utf8 = [Text.UTF8Encoding]::new($false)
        [IO.File]::WriteAllText((Join-Path $fixture 'Gateway.cs'), "// compiler input`nclass Gateway { }`n", $utf8)
        [IO.File]::WriteAllText((Join-Path $fixture 'appsettings.json'), "{`n  `"enabled`": true`n}`n", $utf8)
        [IO.File]::WriteAllText((Join-Path $fixture 'launch.cmd'), "@echo off`nexit /b 0`n", $utf8)
        $binary = [byte[]]@(137,80,78,71,0,13,10,10,255)
        [IO.File]::WriteAllBytes((Join-Path $fixture 'scan.png'), $binary)
        Invoke-FixtureGit $fixture @('init', '--quiet')
        Invoke-FixtureGit $fixture @('config', 'core.hooksPath', $emptyHooks)
        Invoke-FixtureGit $fixture @('config', 'core.autocrlf', 'false')
        Invoke-FixtureGit $fixture @('add', '--', '.')
        Invoke-FixtureGit $fixture @('-c', 'user.name=Checkout test', '-c', 'user.email=checkout@example.invalid', '-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', 'fixture')
        $windows = Join-Path $TestDrive 'windows-checkout'
        $lf = Join-Path $TestDrive 'lf-checkout'
        Invoke-FixtureGit $TestDrive @('clone', '--quiet', '--no-checkout', '--no-hardlinks', $fixture, $windows)
        Invoke-FixtureGit $TestDrive @('clone', '--quiet', '--no-checkout', '--no-hardlinks', $fixture, $lf)
        Invoke-FixtureGit $windows @('config', 'core.autocrlf', 'true')
        Invoke-FixtureGit $windows @('config', 'core.eol', 'crlf')
        Invoke-FixtureGit $lf @('config', 'core.autocrlf', 'false')
        Invoke-FixtureGit $lf @('config', 'core.eol', 'lf')
        Invoke-FixtureGit $windows @('checkout', '--quiet', '--force', 'HEAD')
        Invoke-FixtureGit $lf @('checkout', '--quiet', '--force', 'HEAD')
    }

    It 'checks out byte-identical C# compiler inputs using LF' {
        [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $windows 'Gateway.cs'))) | Should -BeExactly ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $lf 'Gateway.cs'))))
        [IO.File]::ReadAllText((Join-Path $windows 'Gateway.cs')) | Should -Not -Match "`r"
    }

    It 'checks out byte-identical packaged configuration using LF' {
        [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $windows 'appsettings.json'))) | Should -BeExactly ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $lf 'appsettings.json'))))
        [IO.File]::ReadAllText((Join-Path $windows 'appsettings.json')) | Should -Not -Match "`r"
    }

    It 'preserves binary bytes in both checkouts' {
        foreach ($checkout in @($windows, $lf)) {
            [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $checkout 'scan.png'))) | Should -BeExactly ([Convert]::ToBase64String($binary))
        }
    }

    It 'keeps Windows command launchers byte-identical using CRLF' {
        $expected = "@echo off`r`nexit /b 0`r`n"
        foreach ($checkout in @($windows, $lf)) {
            [IO.File]::ReadAllText((Join-Path $checkout 'launch.cmd')) | Should -BeExactly $expected
        }
    }
}
