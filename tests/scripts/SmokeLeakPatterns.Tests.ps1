BeforeAll {
    # Load only the pure predicate and its real defaults, without starting a gateway.
    $path = Join-Path $PSScriptRoot '../../scripts/smoke-test.ps1'
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)
    $script:LeakPatterns = ($ast.ParamBlock.Parameters |
        Where-Object { $_.Name.VariablePath.UserPath -eq 'LeakPatterns' }).DefaultValue.SafeGetValue()
    $script:defaultLeakPatterns = $script:LeakPatterns
    $function = $ast.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Test-ResponseLeaks'
    }, $true)
    . ([scriptblock]::Create($function.Extent.Text))
}

Describe 'Smoke response leak detection' {
    AfterEach { $script:LeakPatterns = $script:defaultLeakPatterns }

    It 'Allows ordinary configuration and type names' {
        Test-ResponseLeaks -Body 'ConnectionString is required; System.String is a supported type.' -Context test |
            Should -BeNullOrEmpty
    }

    It 'Requires stack-frame context: <Body>' -ForEach @(
        @{ Body = 'ODVGateway.Services.Foo.Bar()' }
        @{ Body = 'Look at ODVGateway.Services.Foo.Bar for details.' }
        @{ Body = 'format ODVGateway.Services.Foo.Bar()' }
    ) {
        Test-ResponseLeaks -Body $Body -Context test | Should -BeNullOrEmpty
    }

    It 'Detects an actual leak: <Body>' -ForEach @(
        @{ Body = 'at System.IO.File.ReadAllText(String path)' }
        @{ Body = '   at ODVGateway.Services.Foo.Bar()' }
        @{ Body = 'at Foo.Bar(String value)' }
        @{ Body = 'at Bar()' }
        @{ Body = 'ConnectionString=secret' }
        @{ Body = 'InvalidOperationException: failure details' }
        @{ Body = 'C:\private\file.pdf' }
    ) {
        Test-ResponseLeaks -Body $Body -Context test | Should -Not -BeNullOrEmpty
    }

    It 'Uses a caller-supplied replacement pattern list' {
        $script:LeakPatterns = @('custom-sensitive-marker')
        Test-ResponseLeaks -Body 'ConnectionString=value' -Context test | Should -BeNullOrEmpty
        Test-ResponseLeaks -Body 'custom-sensitive-marker' -Context test | Should -Not -BeNullOrEmpty
    }
}
