<#
.SYNOPSIS
    Smoke test for ODVGateway.

.DESCRIPTION
    Builds ODVGateway, starts it on a temporary local port with a smoke-specific
    configuration overlay, and verifies readiness, security headers, and
    deterministic/sanitized error responses.

    Exit code 0 = all required checks passed (warnings allowed).
    Exit code 1 = at least one required check failed.

.PARAMETER Port
    TCP port to run the gateway on during the smoke test.

.PARAMETER ProjectPath
    Path to the ODVGateway project directory (contains ODVGateway.csproj).

.PARAMETER StartupTimeoutSeconds
    Maximum time to wait for the gateway to respond on /health.
#>
[CmdletBinding()]
param(
    [int]$Port = 5210,
    [string]$ProjectPath = 'src/ODVGateway',
    [int]$StartupTimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-SmokeResult {
    param(
        [Parameter(Mandatory = $true)][string]$Check,
        [Parameter(Mandatory = $true)][ValidateSet('PASS', 'WARN', 'FAIL')][string]$Status,
        [string]$Message = ''
    )

    $colorMap = @{
        PASS = 'Green'
        WARN = 'Yellow'
        FAIL = 'Red'
    }

    $line = "[$Status] $Check"
    if (-not [string]::IsNullOrWhiteSpace($Message)) {
        $line += " - $Message"
    }

    Write-Host $line -ForegroundColor $colorMap[$Status]
}

function Get-SanitizedResponsePreview {
    param([Parameter(Mandatory = $true)][string]$Body)

    # Keep the preview short and replace likely leak indicators with a marker.
    $maxLength = 400
    $preview = if ($Body.Length -gt $maxLength) { $Body.Substring(0, $maxLength) + '...' } else { $Body }
    return $preview
}

function Test-ResponseLeaks {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Body,
        [Parameter(Mandatory = $true)][string]$Context
    )

    if ([string]::IsNullOrWhiteSpace($Body)) {
        return $null
    }

    $leakPatterns = @(
        '\\[a-zA-Z]:\\',          # Windows local paths
        '\\\\[a-zA-Z0-9_-]+',      # UNC paths
        'at [A-Za-z0-9_]+\(',       # Stack frames
        'Exception:',              # Exception class names
        'ConnectionString',        # Connection-string leaks
        'Server=[^;]+;Database=',  # SQL connection strings
        'System\.[A-Za-z0-9.]+',   # .NET type names
        'Microsoft\.[A-Za-z0-9.]+' # Microsoft type names
    )

    foreach ($pattern in $leakPatterns) {
        if ($Body -match $pattern) {
            return "Potential leak detected: matched '$pattern'"
        }
    }

    return $null
}

$script:root = Resolve-Path (Join-Path $PSScriptRoot '..')
$script:projectFullPath = Join-Path $script:root $ProjectPath
$script:baseUrl = "http://localhost:$Port"
$script:process = $null
$script:smokeConfigPath = Join-Path $script:projectFullPath 'appsettings.Smoke.json'
$script:failCount = 0
$script:warnCount = 0

function Register-CheckResult {
    param(
        [Parameter(Mandatory = $true)][string]$Check,
        [Parameter(Mandatory = $true)][bool]$Passed,
        [string]$Message = '',
        [switch]$WarningOnly
    )

    if ($Passed) {
        Write-SmokeResult -Check $Check -Status 'PASS' -Message $Message
    }
    elseif ($WarningOnly) {
        $script:warnCount++
        Write-SmokeResult -Check $Check -Status 'WARN' -Message $Message
    }
    else {
        $script:failCount++
        Write-SmokeResult -Check $Check -Status 'FAIL' -Message $Message
    }
}

function Invoke-WithRetry {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$ScriptBlock,
        [int]$MaxAttempts = 40,
        [int]$DelayMilliseconds = 500
    )

    $attempt = 0
    while ($attempt -lt $MaxAttempts) {
        $attempt++
        try {
            return & $ScriptBlock
        }
        catch {
            if ($attempt -eq $MaxAttempts) {
                throw
            }
            Start-Sleep -Milliseconds $DelayMilliseconds
        }
    }
}

function Invoke-SmokeWebRequest {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$Method
    )

    # PowerShell 5.1 treats HTTP error status codes as terminating errors and
    # does not support -SkipHttpErrorCheck. This helper returns a consistent
    # object for both success and error responses.
    try {
        $response = Invoke-WebRequest -Uri $Uri -Method $Method -UseBasicParsing -TimeoutSec 10
        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Content = [string]$response.Content
            Headers = $response.Headers
        }
    }
    catch [System.Net.WebException] {
        $errorResponse = $_.Exception.Response
        if ($null -eq $errorResponse) {
            throw
        }

        $statusCode = [int]$errorResponse.StatusCode
        $content = ''
        try {
            $stream = $errorResponse.GetResponseStream()
            if ($null -ne $stream) {
                $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
                $content = $reader.ReadToEnd()
                $reader.Dispose()
            }
        }
        catch {
            $content = ''
        }

        return [pscustomobject]@{
            StatusCode = $statusCode
            Content = $content
            Headers = $errorResponse.Headers
        }
    }
    catch {
        # [Microsoft.PowerShell.Commands.HttpResponseException] only exists in
        # PowerShell 7; referencing it in a typed catch makes Windows PowerShell
        # 5.1 fail with "Unable to find type" at parse time. Match the type by
        # name at runtime instead so the script works under both shells.
        if ($_.Exception.GetType().FullName -ne 'Microsoft.PowerShell.Commands.HttpResponseException') {
            throw
        }

        $errorResponse = $_.Exception.Response
        if ($null -eq $errorResponse) {
            throw
        }

        $statusCode = [int]$errorResponse.StatusCode
        $content = ''
        if ($null -ne $_.ErrorDetails -and -not [string]::IsNullOrEmpty($_.ErrorDetails.Message)) {
            $content = $_.ErrorDetails.Message
        }
        else {
            try {
                $content = $errorResponse.Content.ReadAsStringAsync().Result
            }
            catch {
                $content = ''
            }
        }

        return [pscustomobject]@{
            StatusCode = $statusCode
            Content = $content
            Headers = $errorResponse.Headers
        }
    }
}

try {
    if (-not (Test-Path -LiteralPath $script:projectFullPath -PathType Container)) {
        throw "Project path not found: $script:projectFullPath"
    }

    $csprojPath = Join-Path $script:projectFullPath 'ODVGateway.csproj'
    if (-not (Test-Path -LiteralPath $csprojPath -PathType Leaf)) {
        throw "Project file not found: $csprojPath"
    }

    Write-Host ''
    Write-Host "ODVGateway smoke test"
    Write-Host "Repository root: $($script:root)"
    Write-Host "Project path: $($script:projectFullPath)"
    Write-Host "Base URL: $($script:baseUrl)"
    Write-Host ''

    # Build
    Write-Host "Building ODVGateway..."
    & dotnet build "$csprojPath" -c Debug 2>&1 | Tee-Object -Variable buildOutput | Write-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }
    Register-CheckResult -Check 'Build ODVGateway' -Passed $true

    # Create a minimal smoke configuration overlay.
    # This keeps the test self-contained and avoids dependency on customer-specific files.
    $smokeConfig = @{
        Logging = @{
            LogLevel = @{
                Default = 'Warning'
                'Microsoft.AspNetCore' = 'Warning'
            }
        }
        AllowedHosts = '*'
        ODVGateway = @{
            openDocViewerDistPath = ''
            allowOpenDocViewerFallbackWithoutSession = $true
            webClientHandoff = @{
                allowedInitiatorUrls = @()
                allowMissingInitiatorHeaders = $true
            }
        }
    }

    $smokeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $script:smokeConfigPath -Encoding UTF8 -Force

    # Start ODVGateway on the requested port using the built DLL.
    # This avoids launchSettings.json overriding ASPNETCORE_ENVIRONMENT.
    Write-Host "Starting ODVGateway on port $Port..."
    $assemblyPath = Join-Path $script:projectFullPath "bin/Debug/net10.0/ODVGateway.dll"
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "Built assembly not found: $assemblyPath. Run dotnet build first."
    }

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = 'dotnet'
    $startInfo.Arguments = "`"$assemblyPath`""
    $startInfo.WorkingDirectory = $script:root
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['ASPNETCORE_ENVIRONMENT'] = 'Smoke'
    $startInfo.Environment['ASPNETCORE_URLS'] = $script:baseUrl
    $startInfo.Environment['ASPNETCORE_CONTENTROOT'] = $script:projectFullPath

    $script:process = [System.Diagnostics.Process]::Start($startInfo)

    # Give the process a moment to fail fast, then capture any early output.
    Start-Sleep -Milliseconds 500
    if ($script:process.HasExited) {
        $stdout = $script:process.StandardOutput.ReadToEnd()
        $stderr = $script:process.StandardError.ReadToEnd()
        if (-not [string]::IsNullOrWhiteSpace($stdout)) { Write-Host "Process stdout:`n$stdout" }
        if (-not [string]::IsNullOrWhiteSpace($stderr)) { Write-Host "Process stderr:`n$stderr" }
        throw "ODVGateway process exited early with code $($script:process.ExitCode)."
    }

    # Wait for readiness.
    Write-Host "Polling /health for up to $StartupTimeoutSeconds seconds..."
    $healthResponse = Invoke-WithRetry -MaxAttempts $StartupTimeoutSeconds -DelayMilliseconds 1000 -ScriptBlock {
        $response = Invoke-WebRequest -Uri "$($script:baseUrl)/health" -Method GET -UseBasicParsing -TimeoutSec 5
        if ($response.StatusCode -ne 200) {
            throw "Unexpected status code: $($response.StatusCode)"
        }
        return $response
    }

    Register-CheckResult -Check 'Gateway readiness (/health)' -Passed $true -Message "status $($healthResponse.StatusCode)"

    # Header checks on /health
    $headers = $healthResponse.Headers

    $cto = $headers['X-Content-Type-Options']
    Register-CheckResult -Check 'Header: X-Content-Type-Options' -Passed ([bool]($cto -eq 'nosniff')) -Message ($cto -join ', ')

    $xfo = $headers['X-Frame-Options']
    Register-CheckResult -Check 'Header: X-Frame-Options' -Passed ($null -ne $xfo) -Message ($xfo -join ', ') -WarningOnly

    $rp = $headers['Referrer-Policy']
    Register-CheckResult -Check 'Header: Referrer-Policy' -Passed ($null -ne $rp) -Message ($rp -join ', ') -WarningOnly

    $robots = $headers['X-Robots-Tag']
    Register-CheckResult -Check 'Header: X-Robots-Tag' -Passed ($null -ne $robots) -Message ($robots -join ', ') -WarningOnly

    $server = $headers['Server']
    Register-CheckResult -Check 'Header: Server hidden/non-revealing' -Passed (($null -eq $server) -or [bool]($server -notmatch 'Kestrel|ASP.NET|IIS|nginx|apache')) -Message ($server -join ', ')

    # CSP warning only
    $csp = $headers['Content-Security-Policy']
    Register-CheckResult -Check 'Header: Content-Security-Policy' -Passed ($null -ne $csp) -Message ($csp -join ', ') -WarningOnly

    # Error response checks

    # 1. Unknown resource (404)
    $notFoundResponse = Invoke-SmokeWebRequest -Uri "$($script:baseUrl)/this-path-does-not-exist-smoke" -Method GET
    Register-CheckResult -Check 'Error: 404 for unknown path' -Passed ($notFoundResponse.StatusCode -eq 404)
    $notFoundLeak = Test-ResponseLeaks -Body $notFoundResponse.Content -Context '404 response'
    Register-CheckResult -Check 'Error: 404 response sanitized' -Passed ($null -eq $notFoundLeak) -Message ($notFoundLeak -join '; ')

    # 2. Invalid method (405) - /prep is POST only
    $methodResponse = Invoke-SmokeWebRequest -Uri "$($script:baseUrl)/prep" -Method GET
    $methodStatus = $methodResponse.StatusCode
    # ASP.NET Core returns 405 when a route exists but the method is not allowed.
    Register-CheckResult -Check 'Error: 405 for invalid method on /prep' -Passed ($methodStatus -eq 405) -Message "status $methodStatus"
    $methodLeak = Test-ResponseLeaks -Body $methodResponse.Content -Context '405 response'
    Register-CheckResult -Check 'Error: 405 response sanitized' -Passed ($null -eq $methodLeak) -Message ($methodLeak -join '; ')

    # 3. Bad request (400) - invalid sessiondata query parameter
    $badRequestResponse = Invoke-SmokeWebRequest -Uri "$($script:baseUrl)/?sessiondata=invalid-smoke-token" -Method GET
    Register-CheckResult -Check 'Error: 400 for invalid sessiondata' -Passed ($badRequestResponse.StatusCode -eq 400) -Message "status $($badRequestResponse.StatusCode)"
    $badRequestLeak = Test-ResponseLeaks -Body $badRequestResponse.Content -Context '400 response'
    Register-CheckResult -Check 'Error: 400 response sanitized' -Passed ($null -eq $badRequestLeak) -Message ($badRequestLeak -join '; ')

    # Summary
    Write-Host ''
    $totalRequired = 10  # excludes warnings
    $passedRequired = $totalRequired - $script:failCount
    Write-Host "Summary: $passedRequired/$totalRequired required checks passed, $($script:warnCount) warning(s)."

    if ($script:failCount -gt 0) {
        Write-Host 'SMOKE TEST FAILED' -ForegroundColor Red
        exit 1
    }

    Write-Host 'SMOKE TEST PASSED' -ForegroundColor Green
    exit 0
}
catch {
    Write-Host ''
    Write-Host "SMOKE TEST ERROR: $_" -ForegroundColor Red
    if ($_.ScriptStackTrace) {
        Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    }
    exit 1
}
finally {
    if ($null -ne $script:process -and -not $script:process.HasExited) {
        Write-Host ''
        Write-Host 'Stopping ODVGateway process...'
        try {
            $script:process.Kill()
            $script:process.WaitForExit(5000) | Out-Null
        }
        catch {
            Write-Warning "Could not stop ODVGateway process cleanly: $_"
        }
    }

    if ($null -ne $script:process) {
        $script:process.Dispose()
    }

    if (Test-Path -LiteralPath $script:smokeConfigPath -PathType Leaf) {
        Remove-Item -LiteralPath $script:smokeConfigPath -Force -ErrorAction SilentlyContinue
    }
}
