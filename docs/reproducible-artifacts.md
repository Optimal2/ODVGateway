# Reproducible OMP artifacts

Build a component from a clean checkout using the universal installer's normal
Release publish and packaging path. The shared publisher already supplies
`ContinuousIntegrationBuild=true`, `Deterministic=true` and a repository-relative
`PathMap`. Its deterministic zip encoder fixes entry order, timestamps and
metadata. Do not remove PDBs or disable artifact identity checks to hide a
content mismatch.

## Checkout bytes are build inputs

Git's `text=auto` alone does not fix working-tree line endings. A Windows
checkout and an LF checkout can represent the same clean commit but feed
different C# bytes to the compiler. Portable PDB document checksums change;
the deterministic assembly's MVID/debug signature and DLL bytes change too.
`Deterministic=true` requires identical inputs; it does not normalize them.

This was verified against the two conflicting 0.1.80 artifacts on 2026-11-03:
their registered content hashes started `8d454475` and `98f88a8c`. Only
`ODVGateway.dll` and `ODVGateway.pdb` differed inside the payload. All fourteen
changed source-document checksums matched CRLF versions of the committed
files in the first artifact and LF versions in the second. Compiler/runtime
versions, document paths and assembly attributes were identical.

The repository now declares LF for text, CRLF explicitly for Windows command
launchers, and preserves its binary exclusions. This covers compiler inputs
and configuration files shipped outside the immutable payload. The checkout
regression suite exercises real Git checkouts with both
`core.autocrlf=true/core.eol=crlf` and `core.autocrlf=false/core.eol=lf`.

After updating an existing checkout to this policy, use a fresh clean worktree
for packaging. Changing `.gitattributes` alone does not rewrite unchanged files
already on disk. Do not restore or overwrite someone else's uncommitted work.

## Comparing packages

Compare outer zip bytes and entries, then the nested `payload/artifact.zip`:
entry names/order, timestamps, content hashes, compression and extra metadata.
OMP's registered SHA-256 is the directory-content hash of the extracted
payload, not the hash of the outer zip. Artifact provenance fields can differ
between the PowerShell and installer envelopes without changing that payload
identity. Compare like-for-like packers when asserting whole-zip equality.

The shared OMP helper does not normalize source checkouts. Other repositories
that leave compiler inputs at `text=auto` have the same exposure; their own
checkout policies must be assessed separately.
