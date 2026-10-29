# appsettings-generator

Turns the `appsettings.json` sample that ships with ODVGateway, plus a small
site profile, into a ready site configuration file. Run it again when a new
release changes the sample.

This is a developer and operator tool. It is **not** part of the build output,
the published release zip or the OMP artifact. It lives in its own folder and
nothing in `src/` or the packaging scripts refers to it.

Requirements: PowerShell 7 (`pwsh`). File-based samples need no other
dependencies; `-Version` and `generate-all` also require Git on PATH. The tool
is PowerShell because all tooling in this repository is PowerShell, and its
tests run in the Pester step of `scripts/local-ci.ps1`
(`tests/scripts/AppSettingsGenerator.Tests.ps1`).

## Commands

Run from the repository root (or give full paths):

```powershell
# Start a profile from a commented example
pwsh -File tools/appsettings-generator/appsettings-generator.ps1 new-profile -Out site.profile.json

# Write the site configuration from the current sample plus the profile
pwsh -File tools/appsettings-generator/appsettings-generator.ps1 generate -Profile site.profile.json -Out site.appsettings.json

# Or read the sample from an annotated revision already in this checkout
pwsh -File tools/appsettings-generator/appsettings-generator.ps1 generate -Profile site.profile.json -Version 0.1.43 -Out site.appsettings.json

# Generate one file per release into an existing output directory
pwsh -File tools/appsettings-generator/appsettings-generator.ps1 generate-all -Profile site.profile.json -Versions 0.1.43,0.1.44 -OutDir generated

# After an upgrade: does the existing site file still match sample + profile?
pwsh -File tools/appsettings-generator/appsettings-generator.ps1 check -Profile site.profile.json -Config site.appsettings.json
```

| Command | Parameters | Result |
| --- | --- | --- |
| `generate` | `-Profile <file>` `[-Out <file>]` `[-Sample <file> | -Version <x.y.z>]` | Writes the generated file to `-Out`. Without `-Out`, the JSON goes to the output stream as a preview (use `-Out` for a byte-exact file). |
| `check` | `-Profile <file>` `-Config <file>` `[-Sample <file> | -Version <x.y.z>]` | Regenerates in memory and compares with `-Config`. Exit 0 when byte-identical, exit 1 with a drift summary otherwise. |
| `generate-all` | `-Profile <file>` `-Versions <a,b,...>` `-OutDir <dir>` `[-Name <file>]` | Writes `<OutDir>/<version>/<Name>` for each version. `-Name` defaults to `appsettings.json`. |
| `new-profile` | `-Out <file>` `[-Force]` | Writes a commented example profile. Refuses to overwrite an existing file unless `-Force` is given. |

`-Sample` defaults to `src/ODVGateway/appsettings.json` in this repository. Point
it at the `appsettings.json` inside an extracted release zip to generate from
that release instead. `src/ODVGateway/Packaging/appsettings.json` (the OMP
artifact configuration baseline) works the same way.

`-Version <x.y.z>` instead reads `src/ODVGateway/appsettings.json` from the
annotated revision `v<x.y.z>` already present in this checkout's Git history.
It uses local Git only: no separate checkout and no network fetch. The version
must have exactly three numeric components. `-Sample` and `-Version` are
mutually exclusive and the tool refuses their combination, because they are two
different answers to which sample to use. An unknown tag, a missing sample path
in that tag, or unavailable Git produces a readable error and exit 1. The
`sample :` header identifies the selected tag, and an empty profile reproduces
that tag's sample byte for byte, including its line endings and byte-order mark.

Exit codes: `0` success, `1` drift (`check`) or a handled error with a readable
message.

## Workflow when a new release changes the sample

1. Keep the site profile (not the generated file) as the thing you maintain,
   in your own private deployment repository or folder. It holds only the
   values that differ from the sample.
2. Get the new release and run `check` against the site file that is
   currently deployed. Exit 1 lists every value that would change (`changed`,
   `missing`, `unexpected`), or says that only comments or layout differ.
3. If `generate` fails because a profile path no longer exists in the new
   sample, read the release notes, then fix the path or mark it `add`.
   If an `add` path now exists in the sample, remove it from `add`.
4. Run `generate` with `-Out`, review the printed `old -> new` list, and
   deploy the file.

## Several releases

Keep **one** profile when sites run different releases. Gate values that only
exist in a bounded release window, then generate for the version a site runs
today and for its next version. For example, the immutable `v0.1.43` sample has
no `signatures` block, while `v0.1.44` has one. This profile therefore sets the
value only for 0.1.44 and later:

```json
{
  "name": "Example Hospital",
  "set": {
    "ODVGateway.signatures.enabled": true,
    "ODVGateway.sessionTtlMinutes": 45
  },
  "since": { "ODVGateway.signatures.enabled": "0.1.44" }
}
```

`-OutDir` must already exist; the per-version directories are created. Every
version uses the normal generation path, JSON read-back verification and file
guards. `-Sample` cannot be combined with `generate-all`. Empty, malformed or
duplicate versions are rejected before anything is written. The command stops
at the first failing version, names it in the error, and writes no later files.

An actual run of the profile above:

```text
> pwsh -File tools/appsettings-generator/appsettings-generator.ps1 generate-all -Profile gate-smoke.profile.json -Versions 0.1.43,0.1.44 -OutDir generated
appsettings-generator: generate-all [0.1.43]
  sample : src/ODVGateway/appsettings.json@v0.1.43
  profile: gate-smoke.profile.json (Example Hospital)
  skipped  ODVGateway.signatures.enabled: not in 0.1.43 (since 0.1.44)
  set    ODVGateway.sessionTtlMinutes: 30 -> 45
  verified: 1 profile entries read back with System.Text.Json (comments skipped, trailing commas allowed)
  wrote  : 0.1.43/appsettings.json
appsettings-generator: generate-all [0.1.44]
  sample : src/ODVGateway/appsettings.json@v0.1.44
  profile: gate-smoke.profile.json (Example Hospital)
  set    ODVGateway.signatures.enabled: false -> true
  set    ODVGateway.sessionTtlMinutes: 30 -> 45
  verified: 2 profile entries read back with System.Text.Json (comments skipped, trailing commas allowed)
  wrote  : 0.1.44/appsettings.json
```

Check each result against its own release, not against the current sample:

```powershell
pwsh -File tools/appsettings-generator/appsettings-generator.ps1 check -Profile gate-smoke.profile.json -Config generated/0.1.43/appsettings.json -Version 0.1.43
pwsh -File tools/appsettings-generator/appsettings-generator.ps1 check -Profile gate-smoke.profile.json -Config generated/0.1.44/appsettings.json -Version 0.1.44
```

## Profile format

```json
{
  "name": "Example Hospital",
  "description": "Free text.",
  "set": {
    "ODVGateway.sessionTtlMinutes": 45,
    "Logging.LogLevel.ODVGateway": "Information"
  },
  "add": [ "Logging.LogLevel.ODVGateway" ],
  "unset": [ "ODVGateway.contentSecurityPolicy" ],
  "since": { "ODVGateway.sessionTtlMinutes": "0.1.9" },
  "until": { "ODVGateway.contentSecurityPolicy": "0.1.44" }
}
```

| Property | Meaning |
| --- | --- |
| `name`, `description` | Optional free text. `name` is printed in the output. |
| `set` | Object of *path -> JSON value*. The value replaces the sample value; any JSON type is allowed (objects and arrays replace the whole sample value). |
| `add` | Array of paths from `set` that do **not** exist in the sample and are inserted on purpose. This is the per-key add marker. |
| `unset` | Array of paths removed from the file, so the product default applies. |
| `since` | Object of *profile path -> version*. The matching entry applies from that version and up, including the bound. |
| `until` | Object of *profile path -> version*. The matching entry applies up to and including that version. `since` and `until` together bound a window. |

Any other top-level property is an error. The profile itself may contain
`//` and `/* */` comments and trailing commas, like `appsettings.json`.

Paths:

- Dotted (`ODVGateway.inlineSources.maxFileBytes`) or colon-separated, as in
  ASP.NET Core configuration (`Logging:LogLevel:Microsoft.AspNetCore`).
- Keys that contain dots resolve with dotted paths too: at each level the
  longest existing key wins, so `Logging.LogLevel.Microsoft.AspNetCore` finds
  the `Microsoft.AspNetCore` key. To **add** a new key that contains a dot,
  use the colon form.
- Matching is case-insensitive, like the configuration system; the sample
  spelling is kept.
- Paths address object members only. Arrays are replaced as a whole.

Rules:

- For an applicable entry, a `set` or `unset` path that does not exist in the
  sample is an error, unless the path is listed in `add`. An `add` path that
  already exists is also an error, so a sample change never goes unnoticed.
  Gating is therefore a deliberate version declaration, not a general ignore-
  missing-paths option.
- `since` and `until` apply uniformly to `set`, `add` and `unset`. An out-of-
  range entry is skipped before the sample-path and `add` guards are evaluated.
- Versions have exactly three numeric components and are compared numerically
  per component: `0.1.9` is earlier than `0.1.44`, never lexically.
- A `since` or `until` key without the same path in `set` or `unset` is an
  error, so a stale gate cannot go unnoticed.
- Gated profiles require `-Version` or `generate-all`. A run against the current
  sample or an explicit `-Sample` refuses such a profile rather than guessing
  whether to apply or drop a value.
- Every skipped entry is printed on its own `skipped` line with the target
  version and the bound that excluded it. Nothing is silently ignored.
- An `add` path is inserted at the end of the deepest existing block; missing
  intermediate objects are created.
- Two applicable entries that overlap (one path inside the other) are an error.
- Every applicable entry is printed as `old -> new`.

## Guarantees

- **Text-preserving edits.** Only the addressed values change. Comments, key
  order, indentation, inline arrays and line endings of the sample stay as
  they are. An empty profile (`{}`) reproduces the sample byte for byte.
  New values are written in the sample style (2-space indentation, scalar
  arrays inline).
- **Loader read-back.** After every generation the result is parsed with
  `System.Text.Json` using the options of
  `Microsoft.Extensions.Configuration.Json` (comments skipped, trailing commas
  allowed). Every applicable `set` value must read back equal and every
  applicable `unset` key must be gone, otherwise the run fails and nothing is
  written.
- **No secrets.** The tool never reads or writes `appsettings.Development.json`
  or secret files (`secrets.json`, `.env`, certificate and key files). Profile
  entries whose key looks like a secret (password, secret, token, API key,
  credential, connection string, authorization) or whose value carries
  credentials (for example a `Bearer` or `Basic` authorization value, or
  `user:pass@` in a URL) are refused. Set those on the server with user
  secrets or environment variables (for example `ODVGateway__Section__Key`).
- **Sample protection.** Neither `generate`, `generate-all` nor `new-profile`
  overwrites the shipped sample or the file passed as `-Sample`.
- These refusals match file names and paths, not link targets: a hard link or
  junction under an innocent name is not followed or detected.

## Worked examples

### 1. Generate a site file from the example profile

`example.profile.json` in this folder is a fictional site:

```text
> pwsh -File tools/appsettings-generator/appsettings-generator.ps1 generate -Profile tools/appsettings-generator/example.profile.json -Out site.appsettings.json
appsettings-generator: generate
  sample : src/ODVGateway/appsettings.json
  profile: tools/appsettings-generator/example.profile.json (Example Hospital)
  set    AllowedHosts: "localhost;127.0.0.1" -> "viewer.example.org"
  set    ODVGateway.sessionTtlMinutes: 30 -> 45
  set    ODVGateway.openDocViewerDistPath: "" -> "D:\\Sites\\viewer.example.org\\dist"
  set    ODVGateway.trustedSourceRoots: [] -> [ "D:\\DocumentArchive\\EXAMPLE-TEST" ]
  set    ODVGateway.inlineSources.maxFileBytes: 2097152 -> 4194304
  set    Logging.LogLevel.Default: "Information" -> "Warning"
  set    Logging.LogLevel.Microsoft.AspNetCore: "Warning" -> "Error"
  add    Logging.LogLevel.ODVGateway: (absent) -> "Information"
  unset  ODVGateway.contentSecurityPolicy: null -> (removed)
  verified: 9 profile entries read back with System.Text.Json (comments skipped, trailing commas allowed)
  wrote  : site.appsettings.json
```

### 2. Check after an upgrade and find drift

Someone changed `sessionTtlMinutes` by hand in the deployed file:

```text
> pwsh -File tools/appsettings-generator/appsettings-generator.ps1 check -Profile tools/appsettings-generator/example.profile.json -Config site.appsettings.json
appsettings-generator: check
  ...
  config : site.appsettings.json
  result : DRIFT - the configuration differs from a fresh generate:
  changed    ODVGateway.sessionTtlMinutes: expected 45, found 60
  Review the differences, then run generate again or update the profile.
```

The exit code is 1. Either put the value in the profile or regenerate.

### 3. A typo, and a deliberate addition

```json
{
  "set": {
    "ODVGateway.sessionTtlMinutez": 45,
    "ODVGateway.metadataAliases.department": "unitName"
  },
  "add": [ "ODVGateway.metadataAliases.department" ]
}
```

```text
appsettings-generator: ERROR: The profile path 'ODVGateway.sessionTtlMinutez' does not exist in the sample, so it cannot be set. Fix the spelling, or list it in 'add' to insert it deliberately.
```

After fixing the spelling, `department` is inserted into the empty
`metadataAliases` block:

```json
    "metadataAliases": {
      "department": "unitName"
    },
```
