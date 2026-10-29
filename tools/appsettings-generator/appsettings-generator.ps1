<#
.SYNOPSIS
    Generates a site appsettings.json from the shipped ODVGateway sample plus a
    small site profile, and checks an existing site file for drift.

.DESCRIPTION
    Commands:
      generate     -Profile <file> [-Out <file>] [-Sample <file> | -Version <x.y.z>]
      check        -Profile <file> -Config <file> [-Sample <file> | -Version <x.y.z>]
      generate-all -Profile <file> -Versions <a,b,...> -OutDir <dir> [-Name <file>]
      new-profile  -Out <file> [-Force]

    The sample defaults to src/ODVGateway/appsettings.json in this repository.
    -Version reads src/ODVGateway/appsettings.json from the annotated revision
    v<x.y.z> in this checkout instead. -Sample and -Version cannot be combined.
    Profile entries can be bounded with since/until version maps; those maps
    require a version-driven run. Edits are surgical and text-preserving: only
    the addressed values change, so comments, key order, indentation and line
    endings of the sample survive. An empty profile reproduces the sample byte
    for byte.

    After every generation the result is read back with System.Text.Json using
    the same options as Microsoft.Extensions.Configuration.Json (comments
    skipped, trailing commas allowed) and every profile entry is asserted.

    The tool never reads or writes appsettings.Development.json or secret files,
    and it refuses profile values that look like secrets.

    Exit codes: 0 success, 1 drift (check) or a handled error.

    This is a developer/operator tool. It is not part of the build output, the
    published release or the OMP artifact. See README.md beside this script.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Command,

    [Alias('Profile')]
    [string]$ProfilePath,

    [string]$Out,

    [string]$Config,

    [string]$Sample,

    [string]$Version,

    [string[]]$Versions,

    [string]$OutDir,

    [string]$Name = 'appsettings.json',

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

class GeneratorError : System.Exception {
    GeneratorError([string]$message) : base($message) {}
}

function Stop-Generator([string]$Message) {
    throw [GeneratorError]::new($Message)
}

# ---------------------------------------------------------------------------
# File guards
# ---------------------------------------------------------------------------

function Resolve-UserPath([string]$Path) {
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function Assert-AllowedFile([string]$FullPath, [string]$Role) {
    $leaf = [System.IO.Path]::GetFileName($FullPath)
    $forbidden = @(
        '^appsettings\.Development(\..+)?\.json$',
        '^secrets\.json$',
        '\.secrets?\.json$',
        '^\.env(\..*)?$',
        '\.(pfx|p12|key|pem)$'
    )
    foreach ($pattern in $forbidden) {
        if ($leaf -match $pattern) {
            Stop-Generator ("Refusing to use '$leaf' as $Role. This tool never reads or writes " +
                'appsettings.Development.json or secret files.')
        }
    }
}

function Read-TextFile([string]$FullPath, [string]$Role) {
    Assert-AllowedFile $FullPath $Role
    if (-not (Test-Path -LiteralPath $FullPath -PathType Leaf)) {
        Stop-Generator "The $Role file does not exist: $FullPath"
    }
    $bytes = [System.IO.File]::ReadAllBytes($FullPath)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $offset = if ($hasBom) { 3 } else { 0 }
    $encoding = [System.Text.UTF8Encoding]::new($false, $true)
    try {
        $text = $encoding.GetString($bytes, $offset, $bytes.Length - $offset)
    }
    catch {
        Stop-Generator "The $Role file is not valid UTF-8: $FullPath"
    }
    [pscustomobject]@{ Text = $text; HasBom = $hasBom }
}

function Assert-VersionShape([string]$Version, [string]$Label = '-Version') {
    if ($Version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
        Stop-Generator "Invalid $Label '$Version'. Use exactly three numeric components: <x.y.z>."
    }
}

function Get-VersionParts([string]$Version) {
    $components = $Version.Split('.')
    $parts = [System.Numerics.BigInteger[]]::new(3)
    for ($i = 0; $i -lt 3; $i++) {
        $parts[$i] = [System.Numerics.BigInteger]::Parse(
            $components[$i], [System.Globalization.NumberStyles]::None,
            [System.Globalization.CultureInfo]::InvariantCulture)
    }
    $parts
}

function Get-NormalizedVersion([string]$Version) {
    (Get-VersionParts $Version | ForEach-Object { $_.ToString([System.Globalization.CultureInfo]::InvariantCulture) }) -join '.'
}

function Compare-Version([string]$Left, [string]$Right) {
    $leftParts = @(Get-VersionParts $Left)
    $rightParts = @(Get-VersionParts $Right)
    for ($i = 0; $i -lt 3; $i++) {
        $comparison = $leftParts[$i].CompareTo($rightParts[$i])
        if ($comparison -ne 0) { return $comparison }
    }
    0
}

function Read-VersionSample([string]$Version) {
    Assert-VersionShape $Version

    $tag = "v$Version"
    $samplePath = 'src/ODVGateway/appsettings.json'
    $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argument in @('-C', $repoRoot, 'show', "${tag}:$samplePath")) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $memory = [System.IO.MemoryStream]::new()
    try {
        try {
            $null = $process.Start()
        }
        catch [System.ComponentModel.Win32Exception] {
            Stop-Generator "Git is required to read sample tag '$tag' but could not be started. Put Git on PATH, or use -Sample instead."
        }
        $stderrTask = $process.StandardError.ReadToEndAsync()
        # Copy the native stdout stream directly: PowerShell's pipeline would
        # decode lines, normalise CRLF/LF and lose the final-newline distinction.
        $process.StandardOutput.BaseStream.CopyTo($memory)
        $process.WaitForExit()
        $null = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            Stop-Generator ("The sample '$samplePath' could not be read from tag '$tag'. " +
                'Check that the annotated tag exists in this checkout and contains that path.')
        }
        $bytes = $memory.ToArray()
    }
    finally {
        $memory.Dispose()
        $process.Dispose()
    }

    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $offset = if ($hasBom) { 3 } else { 0 }
    $encoding = [System.Text.UTF8Encoding]::new($false, $true)
    try {
        $text = $encoding.GetString($bytes, $offset, $bytes.Length - $offset)
    }
    catch {
        Stop-Generator "The sample '$samplePath' from tag '$tag' is not valid UTF-8."
    }
    [pscustomobject]@{ Text = $text; HasBom = $hasBom }
}

function Get-OutputBytes([string]$Text, [bool]$HasBom) {
    $body = [System.Text.UTF8Encoding]::new($false).GetBytes($Text)
    if (-not $HasBom) { return , $body }
    $result = [byte[]]::new($body.Length + 3)
    $result[0] = 0xEF; $result[1] = 0xBB; $result[2] = 0xBF
    [System.Array]::Copy($body, 0, $result, 3, $body.Length)
    , $result
}

# ---------------------------------------------------------------------------
# JSON reading with the product loader semantics
# ---------------------------------------------------------------------------

function Get-LoaderOptions {
    $options = [System.Text.Json.JsonDocumentOptions]::new()
    $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Skip
    $options.AllowTrailingCommas = $true
    $options
}

function ConvertTo-JsonDocument([string]$Text, [string]$Role) {
    try {
        [System.Text.Json.JsonDocument]::Parse($Text, (Get-LoaderOptions))
    }
    catch {
        $inner = $_.Exception
        while ($inner.InnerException) { $inner = $inner.InnerException }
        Stop-Generator "The $Role is not valid JSON (comments and trailing commas are allowed): $($inner.Message)"
    }
}

function Test-JsonElementEqual($Left, $Right) {
    if ($Left.ValueKind -ne $Right.ValueKind) { return $false }
    switch ($Left.ValueKind.ToString()) {
        'Object' {
            $l = @($Left.EnumerateObject())
            $r = @($Right.EnumerateObject())
            if ($l.Count -ne $r.Count) { return $false }
            for ($i = 0; $i -lt $l.Count; $i++) {
                if ($l[$i].Name -cne $r[$i].Name) { return $false }
                if (-not (Test-JsonElementEqual $l[$i].Value $r[$i].Value)) { return $false }
            }
            return $true
        }
        'Array' {
            $l = @($Left.EnumerateArray())
            $r = @($Right.EnumerateArray())
            if ($l.Count -ne $r.Count) { return $false }
            for ($i = 0; $i -lt $l.Count; $i++) {
                if (-not (Test-JsonElementEqual $l[$i] $r[$i])) { return $false }
            }
            return $true
        }
        'String' { return $Left.GetString() -ceq $Right.GetString() }
        'Number' {
            $a = [decimal]0; $b = [decimal]0
            if ($Left.TryGetDecimal([ref]$a) -and $Right.TryGetDecimal([ref]$b)) { return $a -eq $b }
            return $Left.GetRawText() -ceq $Right.GetRawText()
        }
        default { return $true }
    }
}

# ---------------------------------------------------------------------------
# Offset-preserving JSON scanner (comments and trailing commas tolerated)
# ---------------------------------------------------------------------------

function Skip-Trivia([string]$Text, [int]$Pos) {
    while ($Pos -lt $Text.Length) {
        $c = $Text[$Pos]
        if ([char]::IsWhiteSpace($c)) { $Pos++; continue }
        if ($c -eq '/' -and $Pos + 1 -lt $Text.Length) {
            $n = $Text[$Pos + 1]
            if ($n -eq '/') {
                $end = $Text.IndexOf("`n", $Pos)
                $Pos = if ($end -lt 0) { $Text.Length } else { $end + 1 }
                continue
            }
            if ($n -eq '*') {
                $end = $Text.IndexOf('*/', $Pos + 2)
                if ($end -lt 0) { Stop-Generator 'Unterminated block comment in the sample.' }
                $Pos = $end + 2
                continue
            }
        }
        break
    }
    $Pos
}

function Read-JsonStringEnd([string]$Text, [int]$Pos) {
    # $Pos is at the opening quote; returns the offset after the closing quote.
    $i = $Pos + 1
    while ($i -lt $Text.Length) {
        $c = $Text[$i]
        if ($c -eq '\') { $i += 2; continue }
        if ($c -eq '"') { return $i + 1 }
        $i++
    }
    Stop-Generator 'Unterminated string in the sample.'
}

function Read-JsonNode([string]$Text, [int]$Pos) {
    $Pos = Skip-Trivia $Text $Pos
    if ($Pos -ge $Text.Length) { Stop-Generator 'Unexpected end of the sample.' }
    $c = $Text[$Pos]
    if ($c -eq '{') {
        $node = [pscustomobject]@{ Kind = 'Object'; Start = $Pos; End = 0; Members = [System.Collections.Generic.List[object]]::new() }
        $p = Skip-Trivia $Text ($Pos + 1)
        while ($Text[$p] -ne '}') {
            if ($Text[$p] -ne '"') { Stop-Generator "Expected a property name at offset $p in the sample." }
            $keyEnd = Read-JsonStringEnd $Text $p
            $key = [System.Text.Json.JsonSerializer]::Deserialize[string]($Text.Substring($p, $keyEnd - $p))
            $q = Skip-Trivia $Text $keyEnd
            if ($Text[$q] -ne ':') { Stop-Generator "Expected ':' at offset $q in the sample." }
            $value = Read-JsonNode $Text ($q + 1)
            $member = [pscustomobject]@{ Key = $key; KeyStart = $p; Value = $value; CommaPos = -1 }
            $node.Members.Add($member)
            $p = Skip-Trivia $Text $value.End
            if ($Text[$p] -eq ',') {
                $member.CommaPos = $p
                $p = Skip-Trivia $Text ($p + 1)
            }
            elseif ($Text[$p] -ne '}') { Stop-Generator "Expected ',' or '}' at offset $p in the sample." }
        }
        $node.End = $p + 1
        return $node
    }
    if ($c -eq '[') {
        $p = Skip-Trivia $Text ($Pos + 1)
        while ($Text[$p] -ne ']') {
            $item = Read-JsonNode $Text $p
            $p = Skip-Trivia $Text $item.End
            if ($Text[$p] -eq ',') { $p = Skip-Trivia $Text ($p + 1) }
            elseif ($Text[$p] -ne ']') { Stop-Generator "Expected ',' or ']' at offset $p in the sample." }
        }
        return [pscustomobject]@{ Kind = 'Array'; Start = $Pos; End = $p + 1; Members = $null }
    }
    if ($c -eq '"') {
        return [pscustomobject]@{ Kind = 'Scalar'; Start = $Pos; End = (Read-JsonStringEnd $Text $Pos); Members = $null }
    }
    $p = $Pos
    while ($p -lt $Text.Length -and ',}]/'.IndexOf($Text[$p]) -lt 0 -and -not [char]::IsWhiteSpace($Text[$p])) { $p++ }
    [pscustomobject]@{ Kind = 'Scalar'; Start = $Pos; End = $p; Members = $null }
}

function Get-LineStart([string]$Text, [int]$Pos) {
    $nl = $Text.LastIndexOf("`n", [Math]::Max($Pos - 1, 0))
    if ($Pos -eq 0) { return 0 }
    $nl + 1
}

function Get-LineIndent([string]$Text, [int]$Pos) {
    $start = Get-LineStart $Text $Pos
    $i = $start
    while ($i -lt $Text.Length -and ($Text[$i] -eq ' ' -or $Text[$i] -eq "`t")) { $i++ }
    $Text.Substring($start, $i - $start)
}

function Get-Layout([string]$Text, $Root) {
    $newline = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $unit = '  '
    if ($Root.Kind -eq 'Object' -and $Root.Members.Count -gt 0) {
        $first = Get-LineIndent $Text $Root.Members[0].KeyStart
        if ($first.Length -gt 0) { $unit = $first }
    }
    [pscustomobject]@{ NewLine = $newline; Unit = $unit }
}

# ---------------------------------------------------------------------------
# Rendering of profile values in the sample layout
# ---------------------------------------------------------------------------

function Format-JsonKey([string]$Name) {
    $encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
    '"' + [System.Text.Json.JsonEncodedText]::Encode($Name, $encoder).ToString() + '"'
}

function Format-JsonValue($Element, [string]$Indent, $Layout) {
    $kind = $Element.ValueKind.ToString()
    $inner = $Indent + $Layout.Unit
    if ($kind -eq 'Array') {
        $items = @($Element.EnumerateArray())
        if ($items.Count -eq 0) { return '[]' }
        $scalar = -not ($items | Where-Object { $_.ValueKind.ToString() -in 'Object', 'Array' })
        if ($scalar) { return '[ ' + (($items | ForEach-Object { $_.GetRawText() }) -join ', ') + ' ]' }
        $parts = $items | ForEach-Object { $inner + (Format-JsonValue $_ $inner $Layout) }
        return '[' + $Layout.NewLine + ($parts -join (',' + $Layout.NewLine)) + $Layout.NewLine + $Indent + ']'
    }
    if ($kind -eq 'Object') {
        $props = @($Element.EnumerateObject())
        if ($props.Count -eq 0) { return '{}' }
        $parts = $props | ForEach-Object { $inner + (Format-JsonKey $_.Name) + ': ' + (Format-JsonValue $_.Value $inner $Layout) }
        return '{' + $Layout.NewLine + ($parts -join (',' + $Layout.NewLine)) + $Layout.NewLine + $Indent + '}'
    }
    $Element.GetRawText()
}

function Format-NewMember([string[]]$Keys, $Element, [string]$Indent, $Layout) {
    # Renders "k1": { "k2": ... value } for keys that do not exist yet.
    $text = (Format-JsonKey $Keys[0]) + ': '
    if ($Keys.Count -eq 1) { return $text + (Format-JsonValue $Element $Indent $Layout) }
    $inner = $Indent + $Layout.Unit
    $child = Format-NewMember $Keys[1..($Keys.Count - 1)] $Element $inner $Layout
    $text + '{' + $Layout.NewLine + $inner + $child + $Layout.NewLine + $Indent + '}'
}

function Format-Short([string]$Raw) {
    $flat = [regex]::Replace($Raw.Trim(), '\s*\r?\n\s*', ' ')
    if ($flat.Length -gt 100) { $flat = $flat.Substring(0, 97) + '...' }
    $flat
}

# ---------------------------------------------------------------------------
# Path resolution
# ---------------------------------------------------------------------------

function Split-ConfigPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { Stop-Generator 'A profile path is empty.' }
    $separator = if ($Path.Contains(':')) { ':' } else { '.' }
    $tokens = $Path.Split($separator)
    if ($tokens -contains '') { Stop-Generator "The profile path '$Path' has an empty segment." }
    [pscustomobject]@{ Separator = $separator; Tokens = [string[]]$tokens }
}

function Find-Member($ObjectNode, [string]$Key) {
    $hits = @($ObjectNode.Members | Where-Object { $_.Key -eq $Key })
    if ($hits.Count -gt 1) { Stop-Generator "The key '$Key' appears more than once in one sample object." }
    if ($hits.Count -eq 1) { return $hits[0] }
    $null
}

function Resolve-ConfigPath($Root, [string]$Path) {
    # Walks the dotted (or colon-separated) path. With '.', the longest existing
    # key wins at each level, so keys that contain dots resolve naturally.
    $split = Split-ConfigPath $Path
    $tokens = $split.Tokens
    $node = $Root
    $segments = [System.Collections.Generic.List[string]]::new()
    $member = $null
    $parent = $null
    $i = 0
    while ($i -lt $tokens.Count) {
        if ($node.Kind -ne 'Object') {
            Stop-Generator ("The profile path '$Path' continues below '" + ($segments -join '.') +
                "', which is not an object in the sample.")
        }
        $found = $null
        $width = 0
        $maxWidth = if ($split.Separator -eq '.') { $tokens.Count - $i } else { 1 }
        for ($k = $maxWidth; $k -ge 1; $k--) {
            $candidate = $tokens[$i..($i + $k - 1)] -join '.'
            $found = Find-Member $node $candidate
            if ($found) { $width = $k; break }
        }
        if (-not $found) {
            return [pscustomobject]@{
                Found = $false; Parent = $node; Segments = $segments.ToArray()
                Missing = [string[]]$tokens[$i..($tokens.Count - 1)]
            }
        }
        $segments.Add($found.Key)
        $parent = $node
        $member = $found
        $node = $found.Value
        $i += $width
    }
    [pscustomobject]@{ Found = $true; Parent = $parent; Member = $member; Segments = $segments.ToArray(); Missing = @() }
}

# ---------------------------------------------------------------------------
# Profile loading and secret refusal
# ---------------------------------------------------------------------------

$SecretKeyPattern = '(?i)(password|passwd|pwd|secret|token|api[-_]?key|access[-_]?key|private[-_]?key|account[-_]?key|credential|connection[-_]?string|authorization)'
$SecretValuePattern = '(?i)(password\s*=|pwd\s*=|accountkey\s*=|sharedaccesssignature|-----BEGIN|bearer\s+[a-z0-9._-]{8,}|basic\s+[A-Za-z0-9+/=]{12,}|://[^/\s:@]+:[^/\s@]+@)'
$SecretAdvice = ('Set secrets on the server instead (user secrets or environment variables, ' +
    'for example ODVGateway__Section__Key), never in this generated file.')

function Assert-NotSecret([string]$Path, $Element) {
    foreach ($segment in (Split-ConfigPath $Path).Tokens) {
        if ($segment -match $SecretKeyPattern) {
            Stop-Generator "Refusing profile entry '$Path': the key looks like a secret. $SecretAdvice"
        }
    }
    $kind = $Element.ValueKind.ToString()
    if ($kind -eq 'String' -and $Element.GetString() -match $SecretValuePattern) {
        Stop-Generator "Refusing profile entry '$Path': the value looks like a secret or carries credentials. $SecretAdvice"
    }
    if ($kind -eq 'Object') {
        foreach ($prop in $Element.EnumerateObject()) { Assert-NotSecret ($Path + ':' + $prop.Name).Replace('.', ':') $prop.Value }
    }
    if ($kind -eq 'Array') {
        foreach ($item in $Element.EnumerateArray()) { Assert-NotSecret $Path $item }
    }
}

function Read-SiteProfile([string]$FullPath) {
    $file = Read-TextFile $FullPath 'profile'
    $doc = ConvertTo-JsonDocument $file.Text 'profile'
    $root = $doc.RootElement
    if ($root.ValueKind.ToString() -ne 'Object') { Stop-Generator 'The profile must be a JSON object.' }

    $known = 'name', 'description', 'set', 'add', 'unset', 'since', 'until'
    foreach ($prop in $root.EnumerateObject()) {
        if ($prop.Name -cnotin $known) {
            Stop-Generator "Unknown profile property '$($prop.Name)'. Allowed: $($known -join ', ')."
        }
    }

    $profileName = ''
    $nameElement = [System.Text.Json.JsonElement]::new()
    if ($root.TryGetProperty('name', [ref]$nameElement)) { $profileName = $nameElement.GetString() }

    $set = [System.Collections.Generic.List[object]]::new()
    $element = [System.Text.Json.JsonElement]::new()
    if ($root.TryGetProperty('set', [ref]$element)) {
        if ($element.ValueKind.ToString() -ne 'Object') { Stop-Generator "The profile 'set' must be an object of path -> value." }
        foreach ($prop in $element.EnumerateObject()) {
            Assert-NotSecret $prop.Name $prop.Value
            $set.Add([pscustomobject]@{ Path = $prop.Name; Value = $prop.Value })
        }
    }

    $lists = @{}
    foreach ($listName in 'add', 'unset') {
        $items = [System.Collections.Generic.List[string]]::new()
        if ($root.TryGetProperty($listName, [ref]$element)) {
            if ($element.ValueKind.ToString() -ne 'Array') { Stop-Generator "The profile '$listName' must be an array of paths." }
            foreach ($item in $element.EnumerateArray()) {
                if ($item.ValueKind.ToString() -ne 'String') { Stop-Generator "Every '$listName' entry must be a path string." }
                $items.Add($item.GetString())
            }
        }
        $lists[$listName] = $items
    }

    foreach ($path in $lists['add']) {
        if (-not ($set | Where-Object { $_.Path -ceq $path })) {
            Stop-Generator "The 'add' entry '$path' has no value in 'set'. Every added path needs a 'set' value."
        }
    }

    $gates = @{}
    foreach ($gateName in 'since', 'until') {
        $versions = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
        if ($root.TryGetProperty($gateName, [ref]$element)) {
            if ($element.ValueKind.ToString() -ne 'Object') { Stop-Generator "The profile '$gateName' must be an object of path -> version." }
            foreach ($prop in $element.EnumerateObject()) {
                if ($prop.Value.ValueKind.ToString() -ne 'String') {
                    Stop-Generator "Every '$gateName' value must be a version string for path '$($prop.Name)'."
                }
                $version = $prop.Value.GetString()
                Assert-VersionShape $version "$gateName['$($prop.Name)']"
                $versions[$prop.Name] = $version
            }
        }
        $gates[$gateName] = $versions
    }

    $entryPaths = @($set | ForEach-Object { $_.Path }) + @($lists['unset'])
    foreach ($gateName in 'since', 'until') {
        foreach ($path in $gates[$gateName].Keys) {
            if ($entryPaths -cnotcontains $path) {
                Stop-Generator "The '$gateName' entry '$path' has no corresponding 'set' or 'unset' entry."
            }
        }
    }

    [pscustomobject]@{
        Name = $profileName; Set = $set; Add = $lists['add']; Unset = $lists['unset']
        Since = $gates['since']; Until = $gates['until']; Document = $doc
    }
}

# ---------------------------------------------------------------------------
# Generation
# ---------------------------------------------------------------------------

function Test-PathPrefix([string[]]$Left, [string[]]$Right) {
    $count = [Math]::Min($Left.Count, $Right.Count)
    for ($i = 0; $i -lt $count; $i++) { if ($Left[$i] -ne $Right[$i]) { return $false } }
    $true
}

function Get-SkipReason($SiteProfile, [string]$Path, [string]$TargetVersion) {
    $since = $null
    if ($SiteProfile.Since.TryGetValue($Path, [ref]$since) -and (Compare-Version $TargetVersion $since) -lt 0) {
        return "not in $TargetVersion (since $since)"
    }
    $until = $null
    if ($SiteProfile.Until.TryGetValue($Path, [ref]$until) -and (Compare-Version $TargetVersion $until) -gt 0) {
        return "not in $TargetVersion (until $until)"
    }
    $null
}

function Invoke-Generation([string]$SampleText, $SiteProfile, [string]$TargetVersion = '') {
    $null = ConvertTo-JsonDocument $SampleText 'sample'
    $text = $SampleText
    $root = Read-JsonNode $text 0
    $layout = Get-Layout $text $root

    # Plan every applicable entry against the original sample first so conflicts
    # and missing paths are reported before anything is edited. An out-of-range
    # entry is deliberately skipped before any sample-path guard is evaluated.
    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $SiteProfile.Set) {
        $skipReason = if ($TargetVersion) { Get-SkipReason $SiteProfile $item.Path $TargetVersion } else { $null }
        $entries.Add([pscustomobject]@{
            Action = 'set'; Path = $item.Path; Value = $item.Value; Add = ($SiteProfile.Add -ccontains $item.Path)
            Skipped = [bool]$skipReason; SkipReason = $skipReason
        })
    }
    foreach ($path in $SiteProfile.Unset) {
        $skipReason = if ($TargetVersion) { Get-SkipReason $SiteProfile $path $TargetVersion } else { $null }
        $entries.Add([pscustomobject]@{
            Action = 'unset'; Path = $path; Value = $null; Add = $false
            Skipped = [bool]$skipReason; SkipReason = $skipReason
        })
    }
    foreach ($entry in $entries) {
        if ($entry.Skipped) { continue }
        $resolved = Resolve-ConfigPath $root $entry.Path
        if ($resolved.Found -and $entry.Add) {
            Stop-Generator ("The 'add' entry '$($entry.Path)' already exists in the sample. Remove it from " +
                "'add' so it is treated as an ordinary 'set' (the sample now ships this key).")
        }
        if (-not $resolved.Found -and $entry.Action -eq 'unset') {
            Stop-Generator ("The profile path '$($entry.Path)' does not exist in the sample, so it cannot be unset. " +
                "Fix the spelling, or drop it from 'unset' (the sample does not ship this key).")
        }
        if (-not $resolved.Found -and -not $entry.Add) {
            Stop-Generator ("The profile path '$($entry.Path)' does not exist in the sample, so it cannot be set. " +
                "Fix the spelling, or list it in 'add' to insert it deliberately.")
        }
        $segments = [string[]]($resolved.Segments + $resolved.Missing)
        $entry | Add-Member -NotePropertyName Segments -NotePropertyValue $segments
    }
    for ($a = 0; $a -lt $entries.Count; $a++) {
        if ($entries[$a].Skipped) { continue }
        for ($b = $a + 1; $b -lt $entries.Count; $b++) {
            if ($entries[$b].Skipped) { continue }
            if (Test-PathPrefix $entries[$a].Segments $entries[$b].Segments) {
                Stop-Generator "The profile entries '$($entries[$a].Path)' and '$($entries[$b].Path)' overlap. Address each value once."
            }
        }
    }

    # Apply one edit at a time on freshly scanned text, so offsets never go stale.
    $report = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $entries) {
        if ($entry.Skipped) {
            $report.Add("  skipped  $($entry.Path): $($entry.SkipReason)")
            continue
        }
        $root = Read-JsonNode $text 0
        $resolved = Resolve-ConfigPath $root $entry.Path
        $label = $entry.Segments -join '.'
        if ($entry.Action -eq 'unset') {
            $member = $resolved.Member
            $parent = $resolved.Parent
            $old = Format-Short $text.Substring($member.Value.Start, $member.Value.End - $member.Value.Start)
            $index = $parent.Members.IndexOf($member)
            if ($parent.Members.Count -eq 1) {
                $text = $text.Substring(0, $parent.Start) + '{}' + $text.Substring($parent.End)
            }
            elseif ($member.CommaPos -ge 0) {
                $start = Get-LineStart $text $member.KeyStart
                if ($text.Substring($start, $member.KeyStart - $start).Trim() -ne '') { $start = $member.KeyStart }
                $end = $member.CommaPos + 1
                $rest = if ($index + 1 -lt $parent.Members.Count) { $parent.Members[$index + 1].KeyStart } else { $parent.End - 1 }
                $lineEnd = $text.IndexOf("`n", $end)
                if ($start -ne $member.KeyStart -and $lineEnd -ge 0 -and $lineEnd -lt $rest -and
                    $text.Substring($end, $lineEnd - $end).Trim() -eq '') { $end = $lineEnd + 1 }
                $text = $text.Substring(0, $start) + $text.Substring($end)
            }
            else {
                # Last member: drop the preceding comma and the member's own line.
                $previous = $parent.Members[$index - 1]
                $start = Get-LineStart $text $member.KeyStart
                if ($start -gt 0 -and $text.Substring($start, $member.KeyStart - $start).Trim() -eq '') {
                    $start = $start - 1
                    if ($start -gt 0 -and $text[$start - 1] -eq "`r") { $start-- }
                }
                else { $start = $member.KeyStart }
                $text = $text.Substring(0, $start) + $text.Substring($member.Value.End)
                $text = $text.Substring(0, $previous.CommaPos) + $text.Substring($previous.CommaPos + 1)
            }
            $report.Add("  unset  ${label}: $old -> (removed)")
            continue
        }

        if ($resolved.Found) {
            $value = $resolved.Member.Value
            $old = Format-Short $text.Substring($value.Start, $value.End - $value.Start)
            $indent = Get-LineIndent $text $resolved.Member.KeyStart
            $new = Format-JsonValue $entry.Value $indent $layout
            $text = $text.Substring(0, $value.Start) + $new + $text.Substring($value.End)
            $report.Add("  set    ${label}: $old -> $(Format-Short $new)")
            continue
        }

        $parent = $resolved.Parent
        if ($parent.Members.Count -eq 0) {
            $outer = Get-LineIndent $text $parent.Start
            $inner = $outer + $layout.Unit
            $member = Format-NewMember $resolved.Missing $entry.Value $inner $layout
            $insert = '{' + $layout.NewLine + $inner + $member + $layout.NewLine + $outer + '}'
            $text = $text.Substring(0, $parent.Start) + $insert + $text.Substring($parent.End)
        }
        else {
            $last = $parent.Members[$parent.Members.Count - 1]
            $inner = Get-LineIndent $text $last.KeyStart
            $member = Format-NewMember $resolved.Missing $entry.Value $inner $layout
            $at = if ($last.CommaPos -ge 0) { $last.CommaPos + 1 } else { $last.Value.End }
            $comma = if ($last.CommaPos -ge 0) { '' } else { ',' }
            $text = $text.Substring(0, $at) + $comma + $layout.NewLine + $inner + $member + $text.Substring($at)
        }
        $report.Add("  add    ${label}: (absent) -> $(Format-Short (Format-JsonValue $entry.Value '' $layout))")
    }

    $activeEntries = @($entries | Where-Object { -not $_.Skipped })
    Assert-Generated $text $activeEntries
    [pscustomobject]@{ Text = $text; Report = $report; Entries = $activeEntries }
}

function Assert-Generated([string]$Text, $Entries) {
    # Read the result back exactly as Microsoft.Extensions.Configuration.Json does.
    $doc = ConvertTo-JsonDocument $Text 'generated configuration'
    foreach ($entry in $Entries) {
        $current = $doc.RootElement
        $present = $true
        foreach ($segment in $entry.Segments) {
            $next = [System.Text.Json.JsonElement]::new()
            if ($current.ValueKind.ToString() -ne 'Object' -or -not $current.TryGetProperty($segment, [ref]$next)) {
                $present = $false; break
            }
            $current = $next
        }
        $label = $entry.Segments -join '.'
        if ($entry.Action -eq 'unset' -and $present) {
            Stop-Generator "Verification failed: '$label' is still present after unset."
        }
        if ($entry.Action -eq 'set' -and -not $present) {
            Stop-Generator "Verification failed: '$label' is missing from the generated configuration."
        }
        if ($entry.Action -eq 'set' -and -not (Test-JsonElementEqual $current $entry.Value)) {
            Stop-Generator "Verification failed: '$label' does not read back with the profile value."
        }
    }
}

# ---------------------------------------------------------------------------
# Drift summary for check
# ---------------------------------------------------------------------------

function Get-FlatValues($Element, [string]$Prefix, $Into) {
    if ($Element.ValueKind.ToString() -eq 'Object') {
        $props = @($Element.EnumerateObject())
        if ($props.Count -eq 0 -and $Prefix) { $Into[$Prefix] = '{}' }
        foreach ($prop in $props) {
            $name = if ($Prefix) { "$Prefix.$($prop.Name)" } else { $prop.Name }
            Get-FlatValues $prop.Value $name $Into
        }
        return
    }
    $Into[$Prefix] = Format-Short $Element.GetRawText()
}

function Get-DriftSummary([string]$Expected, [string]$Actual) {
    $lines = [System.Collections.Generic.List[string]]::new()
    $expectedValues = [ordered]@{}
    $actualValues = [ordered]@{}
    Get-FlatValues (ConvertTo-JsonDocument $Expected 'regenerated configuration').RootElement '' $expectedValues
    Get-FlatValues (ConvertTo-JsonDocument $Actual 'existing configuration').RootElement '' $actualValues
    foreach ($key in $expectedValues.Keys) {
        if (-not $actualValues.Contains($key)) { $lines.Add("  missing    ${key}: expected $($expectedValues[$key])") }
        elseif ($actualValues[$key] -cne $expectedValues[$key]) {
            $lines.Add("  changed    ${key}: expected $($expectedValues[$key]), found $($actualValues[$key])")
        }
    }
    foreach ($key in $actualValues.Keys) {
        if (-not $expectedValues.Contains($key)) { $lines.Add("  unexpected ${key}: $($actualValues[$key])") }
    }
    if ($lines.Count -eq 0) {
        $e = $Expected -split "`n"
        $a = $Actual -split "`n"
        $count = [Math]::Max($e.Count, $a.Count)
        for ($i = 0; $i -lt $count; $i++) {
            $el = if ($i -lt $e.Count) { $e[$i].TrimEnd("`r") } else { '(end of file)' }
            $al = if ($i -lt $a.Count) { $a[$i].TrimEnd("`r") } else { '(end of file)' }
            if ($el -cne $al -or ($i -lt $e.Count -and $i -lt $a.Count -and $e[$i] -cne $a[$i])) {
                $lines.Add("  layout     values are equal but the text differs from line $($i + 1) (comments, whitespace or line endings):")
                $lines.Add("             expected: $(Format-Short $el)")
                $lines.Add("             found   : $(Format-Short $al)")
                break
            }
        }
        if ($lines.Count -eq 0) { $lines.Add('  layout     values are equal but the bytes differ (encoding or byte-order mark).') }
    }
    $lines
}

# ---------------------------------------------------------------------------
# new-profile template
# ---------------------------------------------------------------------------

$ProfileTemplate = @'
// ODVGateway site profile for tools/appsettings-generator.
// Comments and trailing commas are allowed, as in appsettings.json itself.
{
  // Free text, shown in the generator output.
  "name": "Example Hospital",
  "description": "Fictional example site. Replace every value with your own.",

  // Path -> value. Paths are dotted ("ODVGateway.sessionTtlMinutes") or
  // colon-separated ("Logging:LogLevel:Microsoft.AspNetCore"). A path must
  // already exist in the shipped sample unless it is also listed in "add".
  "set": {
    "AllowedHosts": "viewer.example.org",
    "ODVGateway.sessionTtlMinutes": 45,
    "ODVGateway.openDocViewerDistPath": "D:\\Sites\\viewer.example.org\\dist",
    "ODVGateway.trustedSourceRoots": [ "D:\\DocumentArchive\\EXAMPLE-TEST" ],
    "ODVGateway.inlineSources.maxFileBytes": 4194304,
    "Logging.LogLevel.Default": "Warning",
    "Logging.LogLevel.Microsoft.AspNetCore": "Error",
    "Logging.LogLevel.ODVGateway": "Information"
  },

  // Paths in "set" that do not exist in the sample and must be inserted on
  // purpose. Without this list a missing path is an error.
  "add": [
    "Logging.LogLevel.ODVGateway"
  ],

  // Paths removed from the sample, so the product default applies.
  "unset": [
    "ODVGateway.contentSecurityPolicy"
  ]
}
'@

# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------

function Get-SamplePath {
    if ($Sample) { return Resolve-UserPath $Sample }
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../src/ODVGateway/appsettings.json'))
}

function Assert-NotSample([string]$OutPath) {
    # Guards both the shipped sample and an explicit -Sample against every write.
    $samples = @([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../src/ODVGateway/appsettings.json')))
    if ($Sample) { $samples += Resolve-UserPath $Sample }
    foreach ($samplePath in $samples) {
        if ([string]::Equals($OutPath, $samplePath, [System.StringComparison]::OrdinalIgnoreCase)) {
            Stop-Generator 'Refusing to overwrite the shipped sample. Write to another file.'
        }
    }
}

function Show-Header([string]$Name, [string]$SampleDisplay, [string]$ProfileDisplay, [string]$SiteName) {
    Write-Host "appsettings-generator: $Name"
    Write-Host "  sample : $SampleDisplay"
    $suffix = if ($SiteName) { " ($SiteName)" } else { '' }
    Write-Host "  profile: $ProfileDisplay$suffix"
}

function Invoke-FromProfile([string]$CommandName, [string]$SelectedVersion = $Version) {
    if (-not $ProfilePath) { Stop-Generator "The $CommandName command needs -Profile <file>." }
    if ($Sample -and $SelectedVersion) {
        Stop-Generator 'The -Sample and -Version options cannot be combined: they select two different samples. Use exactly one.'
    }
    $samplePath = $null
    if ($SelectedVersion) {
        $sampleFile = Read-VersionSample $SelectedVersion
        $sampleDisplay = "src/ODVGateway/appsettings.json@v$SelectedVersion"
    }
    else {
        $samplePath = Get-SamplePath
        $sampleFile = Read-TextFile $samplePath 'sample'
        $sampleDisplay = if ($Sample) { $Sample } else { 'src/ODVGateway/appsettings.json' }
    }
    $siteProfile = Read-SiteProfile (Resolve-UserPath $ProfilePath)
    if (-not $SelectedVersion -and ($siteProfile.Since.Count -gt 0 -or $siteProfile.Until.Count -gt 0)) {
        Stop-Generator ("The profile uses 'since' or 'until', which requires a known sample version. " +
            'Run with -Version <x.y.z> or generate-all so no gated value is silently applied or dropped.')
    }
    Show-Header $CommandName $sampleDisplay $ProfilePath $siteProfile.Name
    $result = Invoke-Generation $sampleFile.Text $siteProfile $SelectedVersion
    if ($result.Report.Count -eq 0) { Write-Host '  (profile changes nothing; output equals the sample)' }
    foreach ($line in $result.Report) { Write-Host $line }
    Write-Host "  verified: $($result.Entries.Count) profile entries read back with System.Text.Json (comments skipped, trailing commas allowed)"
    [pscustomobject]@{ SamplePath = $samplePath; Sample = $sampleFile; Result = $result }
}

function Invoke-GenerateAll {
    if (-not $ProfilePath) { Stop-Generator 'The generate-all command needs -Profile <file>.' }
    if ($Sample) {
        Stop-Generator 'The -Sample option cannot be combined with generate-all: the -Versions list selects every sample. Use generate for an explicit sample.'
    }
    if ($Version) {
        Stop-Generator 'The -Version option cannot be combined with generate-all. Use -Versions <a,b,...> instead.'
    }
    if (-not $OutDir) { Stop-Generator 'The generate-all command needs -OutDir <dir>.' }
    if (@($Versions).Count -eq 0) { Stop-Generator 'The generate-all command needs -Versions <a,b,...>.' }
    if ([string]::IsNullOrWhiteSpace($Name) -or [System.IO.Path]::IsPathRooted($Name) -or
        $Name -cne [System.IO.Path]::GetFileName($Name)) {
        Stop-Generator "The -Name value must be a file name without directory components: '$Name'."
    }

    $outRoot = Resolve-UserPath $OutDir
    if (-not (Test-Path -LiteralPath $outRoot -PathType Container)) {
        Stop-Generator "The output directory does not exist: $outRoot"
    }
    Assert-AllowedFile (Join-Path $outRoot $Name) 'output'

    $versionList = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($raw in @($Versions)) {
        if ($null -eq $raw) { Stop-Generator 'The -Versions list contains an empty entry.' }
        foreach ($piece in ([string]$raw).Split(',')) {
            $version = $piece.Trim()
            if (-not $version) { Stop-Generator 'The -Versions list contains an empty entry.' }
            Assert-VersionShape $version '-Versions'
            $normalized = Get-NormalizedVersion $version
            if (-not $seen.Add($normalized)) {
                Stop-Generator "The -Versions list contains duplicate version '$version'."
            }
            $versionList.Add($version)
        }
    }

    foreach ($version in $versionList) {
        try {
            $run = Invoke-FromProfile "generate-all [$version]" $version
            $versionDirectory = Join-Path $outRoot $version
            $outPath = Join-Path $versionDirectory $Name
            Assert-AllowedFile $outPath 'output'
            Assert-NotSample $outPath
            if (-not (Test-Path -LiteralPath $versionDirectory -PathType Container)) {
                $null = New-Item -ItemType Directory -Path $versionDirectory
            }
            $bytes = Get-OutputBytes $run.Result.Text $run.Sample.HasBom
            [System.IO.File]::WriteAllBytes($outPath, $bytes)
            Write-Host "  wrote  : $version/$Name"
        }
        catch {
            Stop-Generator "generate-all failed for version '$version': $($_.Exception.Message)"
        }
    }
    0
}

function Invoke-Generate {
    $run = Invoke-FromProfile 'generate'
    $bytes = Get-OutputBytes $run.Result.Text $run.Sample.HasBom
    if (-not $Out) {
        # Preview only: the JSON goes to the output stream, the report stays on the host.
        $script:PreviewText = $run.Result.Text
        return 0
    }
    $outPath = Resolve-UserPath $Out
    Assert-AllowedFile $outPath 'output'
    Assert-NotSample $outPath
    $directory = [System.IO.Path]::GetDirectoryName($outPath)
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        Stop-Generator "The output directory does not exist: $directory"
    }
    [System.IO.File]::WriteAllBytes($outPath, $bytes)
    Write-Host "  wrote  : $Out"
    0
}

function Invoke-Check {
    if (-not $Config) { Stop-Generator 'The check command needs -Config <file>.' }
    $run = Invoke-FromProfile 'check'
    $configFile = Read-TextFile (Resolve-UserPath $Config) 'configuration'
    Write-Host "  config : $Config"
    $expected = Get-OutputBytes $run.Result.Text $run.Sample.HasBom
    $actual = Get-OutputBytes $configFile.Text $configFile.HasBom
    if ([System.Linq.Enumerable]::SequenceEqual($expected, $actual)) {
        Write-Host '  result : up to date (byte-identical to a fresh generate)'
        return 0
    }
    Write-Host '  result : DRIFT - the configuration differs from a fresh generate:'
    foreach ($line in (Get-DriftSummary $run.Result.Text $configFile.Text)) { Write-Host $line }
    Write-Host '  Review the differences, then run generate again or update the profile.'
    1
}

function Invoke-NewProfile {
    if (-not $Out) { Stop-Generator 'The new-profile command needs -Out <file>.' }
    $outPath = Resolve-UserPath $Out
    Assert-AllowedFile $outPath 'output'
    Assert-NotSample $outPath
    if ((Test-Path -LiteralPath $outPath) -and -not $Force) {
        Stop-Generator "The file already exists: $Out (use -Force to overwrite)."
    }
    $template = $ProfileTemplate -replace "`r?`n", [Environment]::NewLine
    [System.IO.File]::WriteAllText($outPath, $template + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
    Write-Host "appsettings-generator: new-profile"
    Write-Host "  wrote  : $Out"
    Write-Host '  Edit the values, then run: generate -Profile <file> -Out <site appsettings.json>'
    0
}

$usage = @'
Usage:
  appsettings-generator.ps1 generate     -Profile <file> [-Out <file>] [-Sample <file> | -Version <x.y.z>]
  appsettings-generator.ps1 check        -Profile <file> -Config <file> [-Sample <file> | -Version <x.y.z>]
  appsettings-generator.ps1 generate-all -Profile <file> -Versions <a,b,...> -OutDir <dir> [-Name <file>]
  appsettings-generator.ps1 new-profile  -Out <file> [-Force]
'@

$exitCode = 1
$script:PreviewText = $null
try {
    switch ($Command) {
        'generate' { $exitCode = Invoke-Generate }
        'check' { $exitCode = Invoke-Check }
        'generate-all' { $exitCode = Invoke-GenerateAll }
        'new-profile' { $exitCode = Invoke-NewProfile }
        default {
            if ($Command) { Write-Host "Unknown command '$Command'." }
            Write-Host $usage
            $exitCode = 1
        }
    }
}
catch [GeneratorError] {
    Write-Host "appsettings-generator: ERROR: $($_.Exception.Message)"
    $exitCode = 1
}
catch {
    Write-Host "appsettings-generator: UNEXPECTED ERROR: $($_.Exception.Message)"
    $exitCode = 1
}
if ($null -ne $script:PreviewText) { Write-Output $script:PreviewText }
exit $exitCode
