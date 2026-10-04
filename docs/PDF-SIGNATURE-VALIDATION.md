# PDF signature validation on the gateway (`GET /signatures/{sessionKey}/{fileIndex}`)

Status: implemented, disabled by default. Verified 2026-10-04 against
`src/ODVGateway/Options/SignatureValidationOptions.cs` — `Enabled` has no initializer, so it is
`false` and `GET /signatures/...` answers 404 until an environment opts in through configuration.
A deployment that has enabled it through its own configuration overlay will behave differently
from a fresh checkout; read the environment's effective configuration, not this default, when
diagnosing a live gateway.
Scope: ODVGateway (server track). OpenDocViewer is not involved and renders nothing from this contract.

## Why this exists

A signed PDF only proves that *some* certificate signed *some* bytes. Before a document is
treated as signed, someone has to answer three separate questions: are the bytes still the
signed ones, is the cryptographic signature well formed, and is the certificate chain trusted
right now (or at the moment the signature timestamp says). WebClient users needed that answer
from the server, next to the same session that already serves `/source`, without shipping PDF
bytes and a validation stack to every browser.

The gateway answers it as a **verdict for a human reader**, not as a compliance decision:
three values per signature (`integrity`, `trust`, plus reasons) and the time the verdict was
computed at. Nothing is persisted, nothing is cached, no document content leaves the gateway.

## Endpoint contract

```
GET /signatures/{sessionKey}/{fileIndex:int}
```

Same session lookup, same index bounds check, and the same byte-source resolution order as
`/source`: prepared gateway session in memory, then the direct file under `trustedSourceRoots`,
then the configured WebClient fallback URL proxied through the gateway, then 403/404. The route
shares the helpers instead of duplicating the proxy logic (see "Byte source" below).

Response is `Cache-Control: no-store`, JSON, camelCase:

```json
{
  "signatures": [
    {
      "fieldName": "Signature1",
      "signer": "Jane Signer",
      "signerOrganization": "Example Org",
      "issuer": "Issuing CA",
      "serial": "1a2b3c",
      "notBefore": "2024-03-01T00:00:00+00:00",
      "notAfter": "2026-03-01T00:00:00+00:00",
      "signingTime": "2025-06-04T09:12:33+00:00",
      "signingTimeSource": "timestamp",
      "reason": "Approval",
      "location": "Stockholm",
      "subFilter": "adbe.pkcs7.detached",
      "kind": "approval",
      "integrity": "intact",
      "integrityReason": null,
      "coversWholeFile": true,
      "trust": "valid",
      "trustReason": null,
      "validationTime": "2025-06-04T09:12:35+00:00"
    }
  ],
  "validatedAt": "2025-06-04T09:12:35+00:00",
  "diagnostics": []
}
```

Field values:

| Field | Values | Notes |
| --- | --- | --- |
| `kind` | `approval`, `certification`, `timestamp` | `certification` when the signature dictionary is referenced by `/Perms/DocMDP`, `timestamp` for a document timestamp field (`/SubFilter` naming an RFC 3161 token), otherwise `approval`. |
| `integrity` | `intact`, `modified-after-signing`, `digest-mismatch`, `signature-invalid`, `unsupported`, `unreadable` | byte level, independent of trust. |
| `trust` | `valid`, `invalid`, `unknown` | chain level, evaluated at `validationTime`. |
| `signingTimeSource` | `signed-attribute`, `pdf-M`, `timestamp`, `none` | where `signingTime` came from. |
| `integrityReason`, `trustReason` | string or `null` | short stable codes; see the tables below. |
| `diagnostics` | array of strings | document-level notes on how the PDF was read; empty for specification-conforming files. `reference-generation-fallback`: a missing exact generation was read at the newest generation of that number. `dangling-reference-skipped`: a missing object was skipped during a field/widget/annotation walk; `reference-cycle-skipped`: a cyclic indirect-reference chain was skipped; `unexpected-object-type-skipped`: a direct value of the wrong type was skipped, and `reference-depth-exceeded`: a reference chain longer than the depth limit was skipped (see "Reading the PDF"). |

Unknown or absent values are reported as `null` rather than guessed. `fieldName` is the AcroForm
field name when the signature sits on a field, and `/Perms` markers use `DocMDP` / the dictionary
name they were found under.

## HTTP status and limits

| Situation | Status |
| --- | --- |
| `Signatures:Enabled` is `false` | `404` with `{ "error": "Signature validation is not enabled on this gateway." }` |
| session not found or expired | `404` with the same body as `/source` |
| `fileIndex` outside the prepared session | `404` with the same body as `/source` |
| resolved source is not a PDF (by extension) | `415` |
| PDF cannot be opened/parsed at all, is encrypted, or fails the page-tree pre-check (`page-tree-cyclic`, `page-tree-too-deep`) | `422` |
| file larger than `Signatures:MaxFileBytes` | `413` |
| all signature validation slots occupied | `503` with `Retry-After: 1` |
| PDF parses but has no signature fields | `200` with `signatures: []` |

Limits:

- **Size**: `Signatures:MaxFileBytes`. `0` (the default) follows the gateway's existing source
  transport limit (`maxSourcePackFrameBytes`, 64 MiB); a positive value caps the endpoint separately
  but never above that transport limit. The gateway buffers the file to validate it, so the limit is
  enforced before parsing, both for the direct file and for a proxied fallback body.
- **Time**: a linked cancellation token enforces the 60-second file budget cooperatively in
  object traversal, parser stream reads/seeks, signature byte copying, incremental hashing and
  revocation transport. Checks also surround library cryptographic calls; a single synchronous
  library call cannot be forcibly interrupted. Expiry while locating signatures produces 422,
  since no complete signature inventory exists yet. After location, unfinished signatures report
  `unknown` / `validation-timeout`. Client cancellation before location propagates to the endpoint.
  Online attempts share the file budget and each has a 1–30 second timeout (configured below).
- **Traversal**: the object/edge budget is `clamp(fileBytes / 8, 10,000, 8,000,000)` operations,
  with depth 32 per file. This allows large ordinary page trees within the size cap while retaining
  a hard ceiling for compressed or adversarial graphs. Page, field and
  indirect-reference traversal uses visited sets and cached object identities. Limit violations
  produce 422. Physical signature dictionaries larger than 1 MiB are unreadable.
- **Concurrency**: `Signatures:MaxConcurrentValidations` defaults to 2 and is clamped to 1–16.
  A process-wide limiter covers both source buffering and validation, with no waiting queue.
  Saturated requests receive 503 and `Retry-After: 1`; slots are released on success, error and
  cancellation. Revocation I/O is awaited throughout the validation path, including timestamp
  responder checks. The gateway has no existing per-session/client rate-limit policy; this limit
  bounds concurrent work across all sessions. It is per process, not shared across replicas.
- `Cache-Control: no-store` always, including on error responses, because a verdict is a statement
  about "now".

## Reading the PDF

`PdfPig` (Apache-2.0) is used to walk the PDF object graph: catalog, `/AcroForm/Fields` (including
`/Kids` inheritance), `/FT /Sig` fields, their `/V` signature dictionaries, and `/Perms/DocMDP`.

Traversal starts at the catalog named by the **current** trailer (the last one in the file; PdfPig
follows `/Prev` and merges every cross-reference section), so fields added or rewritten by an
incremental update are reached through the newest `/AcroForm` and page objects. Every signature
field in `/AcroForm/Fields` (with `/Kids`) and in page `/Annots` is enumerated, recognised by
`/FT /Sig` (inherited through `/Parent`) or by a `/V` dictionary of `/Type /Sig`, and
de-duplicated by the resolved object identity of its `/V`.

Indirect references are resolved **exactly by (number, generation)**, as the PDF specification and
PdfPig do. This applies to the catalog named by the trailer's `/Root`, to every field, `/V`,
AcroForm and page object, to `/Perms/DocMDP` matching, to de-duplication, and to locating `/Contents`
physically. An object that exists at the referenced generation is never replaced by a higher
generation of the same number.

Some writers rewrite an object in an incremental update as `5 1 obj` (xref entry `00001 n`) while
the catalog still says `5 0 R`. That is outside the specification: an object rewritten by an
incremental update keeps its generation number, and a higher generation only appears after the
number has been freed and reused, at which point `5 0 R` no longer names it. In such a file the
generation-0 original is the object `5 0 R` names, so it is the one read; the `5 1 obj` body is
unreferenced content appended after any signature that covers the earlier revision, and such a
signature is reported as `modified-after-signing` / `bytes-appended-after-signed-range`. Resolving
references to the newest generation instead would let any appended `N 1 obj` shadow a signed field,
`/V` dictionary or AcroForm: a signature could vanish from the report or have its byte range,
`/Contents` and signer evidence swapped for the shadow's, while a strict reader still shows the
original.

Only when the exact (number, generation) entry is **missing** from the merged cross-reference data
does the reference fall back to the newest in-use generation of that object number. The fallback is
conservative and never silent: the response's document-level `diagnostics` array then contains
`reference-generation-fallback`, and the gateway logs a warning. It never makes a signature `intact`
on its own: each located signature is still checked against its own byte range, and rule 5 below
still decides whether an earlier approval survives later revisions. Fallback references go through
the same visited sets, depth limit and traversal budget. For the catalog the fallback is reached
only when PdfPig could open the file at all, which needs some `1 0 obj` body for `/Root 1 0 R`; an
unlisted body found by PdfPig's lenient scan is not an exact cross-reference entry.

A dangling reference inside the field/widget/annotation walk is skipped when PdfPig cannot find
the object and its resolved identity is absent from the cross-reference data. Remaining signatures
are still validated, and `diagnostics` contains `dangling-reference-skipped` once per document. A cyclic indirect-reference chain, a direct value of the wrong type and a reference chain longer than the depth limit are skipped the same way and reported as `reference-cycle-skipped`, `unexpected-object-type-skipped` and `reference-depth-exceeded`: no unresolvable field, `/V` or signature dictionary is ever skipped silently.
This tolerance applies to field and annotation arrays, their elements, field `/Kids`, `/Parent`
and `/V` references.

Before PdfPig opens the file at all, the gateway walks the page tree itself —
trailer `/Root` → `/Pages` → `/Kids` — with a small raw reader over the file bytes
(`PdfPageTreePrecheck`). The reason is a crash, not a shortcut: PdfPig builds the page tree eagerly
inside `PdfDocument.Open` and resolves bare indirect references through the unguarded self-recursion
`DirectObjectFinder.TryGet`, so one cyclic chain kills the process with a stack overflow that .NET
cannot catch (measured 2026-10-04 on PdfPig 0.1.16: exit `0xC00000FD`, about 9,600 repeating `TryGet`
frames via `PagesFactory.ProcessPagesNode`). The pre-check uses the same exact (number, generation)
resolution with newest-generation fallback as the locator, a visited set, and the same depth bound
(32). A proven cycle — including a page-tree node that references itself and a cyclic `/Type` chain,
which crashes the same way — a depth overrun, or a `/Kids` entry that is not an array of indirect
references fails closed with a named `PdfSignatureFormatException` (`page-tree-cyclic` /
`page-tree-too-deep`, HTTP 422). Page-tree dictionaries are additionally capped at 100,000 visited
nodes so a huge legitimate tree cannot pin the validator's CPU, and the walk shares the locator's
work-budget shape at four times its touch density (value tokenizing costs more than edge counting). A
longer already-visited cycle is skipped once, preserving the visited-once contract.

The pre-check only judges what it can read completely: cross-reference streams, hybrid files,
encrypted files, dangling references and unparseable objects are left to PdfPig, whose behavior for
those shapes is unchanged. Shapes outside the walked path that the eager open dereferences through
the same unguarded recursion — catalog `/Dests` and `/Names` name trees in particular — likewise still
reach the library. Process isolation for the whole validation step is the complete fix and is still
pending; see the Known limitation note in `SECURITY.md`.

Catalog and AcroForm roots, page-tree references and other parser failures
remain fatal (HTTP 422). A cyclic `/AcroForm` reference fails the file with the named reason `The PDF AcroForm reference is cyclic.` instead of reading as absent. The same depth, visited-set and traversal-budget limits still apply.

Why a library and not a hand-written reader: signature dictionaries live behind cross-reference
tables *and* cross-reference streams, object streams (`/ObjStm`), and incremental updates, and they
are read again after each append. Getting those right is a PDF parser, which is exactly what PdfPig
is, in pure managed code, Apache-2.0 (compatible with this MIT repository), and already on nuget.org.
iText is AGPL and is not an option here.

What the gateway does **not** do: it does not render, re-save, or copy document content; it does not
interpret `/Reference` document-level digest chains, OCSP-stapled `/DocTS` beyond reading the RFC 3161
token, or PAdES long-term validation data structures. Signatures that need those are reported with
`integrity: "unsupported"` and a reason, not as a failure.

Encrypted PDFs are not validated (422): the gateway has no document password and must not have one.

## Byte range and digest check

A signature dictionary carries `/ByteRange [offset1 length1 offset2 length2]` and `/Contents`
(the hex placeholder that holds the CMS blob). Validation:

1. Require exactly four non-negative integers, `offset1 == 0`, ordered non-overlapping ranges
   inside the file, and overflow-safe arithmetic. The sole excluded span must be exactly this
   xref-resolved signature object's direct `/Contents` hex string, including `<` and `>`.
   A bounded lexical pass establishes its physical position; duplicate dictionary keys, indirect
   values, compressed/inline signature dictionaries, non-integer ranges or any ambiguous position
   fail closed as `unreadable` / `byte-range-malformed`. In particular, a larger gap hiding an
   unsigned dictionary entry is never `intact`.
2. The covered bytes (range 1 followed by range 2, ...) are hashed with the message-digest algorithm
   from the CMS `SignerInfo`, and compared with the `messageDigest` signed attribute
   (`1.2.840.113549.1.9.4`, whose raw value is an `OCTET STRING` that has to be unwrapped before the
   comparison). Mismatch: `digest-mismatch`.
3. `SignedCms` is decoded with the covered bytes as the *external* content and checked with
   `CheckHash()` (step 2 again, framework side) and
   `CheckSignature(verifySignatureOnly: true)` (the RSA/ECDSA operation over the signed attributes;
   the private key is never touched). Failure: `signature-invalid`.

   The exact call shape matters and was verified against .NET 10: a PDF signature blob is CMS
   *detached* content, so the covered bytes must be handed to the constructor
   (`new SignedCms(new ContentInfo(coveredBytes), preferDetached: true)`) **before** `Decode`, and
   `SignedCms.ContentInfo` is read-only afterwards. Constructing an empty `SignedCms`, decoding the
   blob and then calling `CheckHash()`/`SignerInfo.CheckSignature(...)` reports
   *"The hash value is not correct"* for a perfectly intact signature, because the detached blob
   carries no content to hash.
4. `coversWholeFile` is true only after the strict gap check and when `offset2 + length2`
   equals the file length.
5. An approval signature with an uncovered tail stays `intact` only if a later, itself intact
   signature covers the complete earlier revision in its prefix and covers the current file.
   Otherwise it is `modified-after-signing` with `trust: unknown` and
   `bytes-appended-after-signed-range`. The later signature's mere presence is insufficient.
   Certification signatures with any appended bytes remain `modified-after-signing` / `invalid`.

`digest-mismatch` and `signature-invalid` always mean `trust: "invalid"`: a signature that does not
verify cannot be trusted at any time. `unsupported`/`unreadable` mean `trust: "unknown"`.

## Timestamps and the validation time

Two times are reported per signature, and they come from different places.

`validationTime` is the instant the chain is evaluated at:

- A signature timestamp token (CMS unsigned attribute `1.2.840.113549.1.9.16.2.14`,
  `id-aa-signatureTimeStampToken`) that decodes with `Rfc3161TimestampToken.TryDecode`, whose
  message imprint matches the signature value, whose own signature verifies over the signer
  (`VerifySignatureForSignerInfo`), and whose responder passes the same chain, certificate-time and revocation gates as the signer moves
  `validationTime` to the token's `genTime`. A broken, foreign, unanchored, revoked or revocation-unchecked token never does. An anchored
  responder without usable revocation evidence reports `timestamp-responder-not-trusted`.
- Otherwise `validationTime` = the gateway's current UTC time.

`signingTime` is the signer's claimed signing instant, in order of preference:

1. the CMS signed attribute `signingTime` (`1.2.840.113549.1.9.5`) — authenticated by the
   signature itself — `signingTimeSource: "signed-attribute"`;
2. the signature dictionary's `/M` entry — `signingTimeSource: "pdf-M"`;
3. a verified timestamp token's `genTime` (an upper bound on the signing instant, used only when
   the document claims no time itself) — `signingTimeSource: "timestamp"`;
4. otherwise `signingTime: null`, `signingTimeSource: "none"`.

A timestamp token is only trusted when it *verifies*; a broken or absent token never moves
`validationTime` into the past. That is what makes the expired-certificate rule honest:

| Certificate state at signing | Valid RFC 3161 token? | Chain evaluated at | `trust` |
| --- | --- | --- | --- |
| inside validity | yes | token `genTime` | `valid` (if the chain anchors and revocation is checked) |
| expired since | yes | token `genTime` | `valid` (proof of validity at signing time) |
| expired since | no | current time | `unknown`, reason `certificate-not-valid-at-validation-time` |
| not yet valid | no | current time | `unknown`, same reason |
| revoked (CRL says so, revocationDate <= validationTime) | any | as above | `invalid`, reason `revoked` |

Without an authenticated timestamp the gateway cannot know when the signature was made, so an
expired certificate is reported as `unknown` — not as `valid`, and not as `invalid` either, because
nothing about the signature itself is broken.

## Trust anchors, chain building, revocation

Chain building uses `System.Security.Cryptography.X509Chain` with `TrustMode = CustomRootTrust`:

- **Anchors** = Windows trusted roots (LocalMachine + CurrentUser `Root` stores) when
  `Signatures:UseWindowsTrustedRoots` is `true`, plus every certificate found in
  `Signatures:ExtraAnchorsDirectory` (`.cer`, `.crt`, `.der`, `.pem`, binary or PEM).
- **Intermediates** = the certificates inside the CMS blob (`SignedCms.Certificates`), plus the
  Windows intermediate stores as a fall-back. `DisableCertificateDownloads` is set, so no
  AIA fetch of CA certificates happens: intermediates come from the document or the machine.
- **Policy**: EKU `1.3.6.1.5.5.7.3.4` (document signing) is *not* required, because signing
  certificates in the field use many different EKU sets; the reason is reported when the chain
  engine says the usage does not match.

A successful `X509Chain.Build` result, an exact certificate match between the final chain element
and a configured anchor, and no status flags are all mandatory. A caught build exception with
empty status is not success. Every certificate must be valid at `validationTime`.
Unknown or combined unexpected status flags fail closed. The native chain always uses `NoCheck`
and disables certificate downloads: all network revocation is owned by the bounded transport.

A chain that does **not** end in one of the configured anchors is never `valid`:

| Chain outcome | `trust` | `trustReason` |
| --- | --- | --- |
| built, anchored, no status flags | `valid` (see revocation gate) | `null` or revocation note |
| `UntrustedRoot` / `PartialChain` (no configured anchor, or issuer not in the document) | `unknown` | `chain-not-anchored` |
| `NotTimeValid` | `unknown` | `certificate-not-valid-at-validation-time` |
| Verified CRL entry (with revocationDate <= validationTime) | `invalid` | `revoked` |
| `InvalidBasicConstraints`, `WeakSignatureAlgorithm` (SHA-1/MD5 hash or < 2048-bit RSA key) | `invalid` | `chain-policy-violation` / `weak-signature` |
| Missing, stale or not-yet-valid CRL / unresolved revocation | `unknown` | `revocation-unavailable` |
| anything unexpected | `unknown` | `validation-error` |

Revocation must cover every non-anchor chain element:

1. **Offline CRL files** (`Signatures:CrlDirectory`, optional). DER/PEM CRLs must have a
   verified issuer signature and `thisUpdate <= validationTime <= nextUpdate`. A missing
   `nextUpdate`, unsupported algorithm/critical extension, delta CRL or indirect CRL
   cannot establish coverage. Candidates are tried newest first, skipping verified CRLs whose
   windows do not cover the validation time so an archived CRL can cover a verified timestamp.
   Issuing distribution points (critical or non-critical) are accepted when they cover the
   certificate: full scope, matching user/CA type, and, when named, a URI matching a certificate
   distribution point. Partial reason coverage, attribute-certificate scope, relative/non-URI
   names and malformed IDPs remain unsupported and cannot produce `valid`. Non-critical scope
   restrictions are never ignored. Supported CRL signatures are RSA PKCS#1 and ECDSA with SHA-256/384/512.
   A listed serial with `revocationDate <= validationTime` means `invalid` / `revoked`.
   Delta CRLs are not supported. A base CRL carrying `freshestCRL` (OID `2.5.29.46`),
   even when non-critical, is also rejected: an unlisted certificate might be revoked in
   a delta CRL. Without other usable evidence, the result is `unknown` with
   `trustReason: "revocation-unavailable"`, never `valid`. This applies to local and downloaded CRLs.
2. **Online** (default): if local CRLs cannot answer, fetch a complete CRL from the certificate's
   distribution points through the gateway's bounded transport and apply the same verifier.
   OCSP-only certificates and unsupported distribution points produce `unknown` /
   `revocation-unavailable`; the native OS online engine is never a fallback.
3. **Offline**: use configured CRL files only. The OS revocation cache is not used. Missing
   evidence yields `unknown` / `revocation-unavailable`.
4. **NoCheck**: always `unknown` / `revocation-not-checked`, even if local CRLs are configured.
   This mode cannot authenticate a timestamp or produce `valid`.

Online transport accepts only HTTP/HTTPS, without credentials, proxies, cookies or redirects.
Every resolved address must be public: loopback, private, link-local, unique-local, multicast,
transition and reserved ranges are blocked, as are the gateway's local interface addresses and
own host name and the request's gateway host. DNS is resolved inside the connection callback, and the socket connects to the
validated IP directly, avoiding a second resolution and DNS rebinding. HTTPS still validates the
original host's certificate; its chain policy also disables AIA and native revocation downloads.
Mixed public/private DNS answers are rejected.

`Signatures:RevocationHostAllowList` optionally limits requests to exact DNS host names
(case-insensitive; no wildcards), in addition to the mandatory address checks. Empty means any
public host. Limits: eight fetch attempts per file (shared across signers and timestamp responders),
1 MiB per response including chunked bodies, no decompression, and
`Signatures:RevocationTimeoutSeconds` clamped to 1–30 seconds per attempt, including DNS and body
reads. Repeated URLs share the file-local result cache. Failures produce `unknown`, never `valid`.

## Failure isolation and logging

Each signature is validated in its own `try/catch`; all signatures share the file time and
fetch budgets. A malformed CMS blob produces a verdict for that signature instead of aborting the
response. Exhausting the shared budgets can also leave subsequent signatures unknown. The response
contains the complete located inventory and succeeds with `200` once location has finished.

Logs carry: session key (never a document name), file index, signature count, sub-filter name, the
verdict codes (`integrity`, `trust`, reason), byte counts, elapsed milliseconds, and exception type
names. **No document content, no signer names or other subject strings, no certificate serials, no
issuer DN** — those are in the response for the user, not in the log. Certificate subjects are the
kind of personal data a production log must not collect.

## Byte source

`/source` and `/signatures` need the same bytes with different shapes: `/source` streams, while
`/signatures` needs random access into the file for the byte ranges. Both call the same helpers:

- session lookup and index bounds check: `TryResolveSessionSource` (new, shared),
- direct file: `DirectSourceFileResolver.TryResolve` / `OpenRead` (existing),
- proxied fallback: `ReadGatewaySourceBytesAsync` + `FetchWebClientSourceBytesAsync` (existing,
  already the buffered reader for `/source-pack`, including retry, timeout and size handling),
- fallback URL construction: `WebClientFallbackUrlBuilder.BuildFallbackUrl` (existing).

`/signatures` reads the bytes into memory (bounded by `Signatures:MaxFileBytes`) through those
helpers; no proxy logic is duplicated.

## Configuration

```json
"ODVGateway": {
  "signatures": {
    "enabled": false,
    "useWindowsTrustedRoots": true,
    "extraAnchorsDirectory": "",
    "crlDirectory": "",
    "revocationMode": "Online",
    "revocationTimeoutSeconds": 15,
    "revocationHostAllowList": [],
    "maxFileBytes": 0,
    "maxConcurrentValidations": 2
  }
}
```

## Testing

Tests build a throwaway CA hierarchy in code (`CertificateRequest`, `X509SignatureGenerator`,
`CertificateRevocationListBuilder`), sign synthetic single-page PDFs written by the fixture helper,
and write DER CRL files into a temp directory. No network access, and almost every byte is generated
per test run, so nothing that looks like a customer document can leak into a public repository.
The two checked-in files under `tests/ODVGateway.Tests/Fixtures/Signatures/` come from
OpenDocViewer's `scripts/generate-signature-fixtures.mjs` (throwaway CA, fake names).
`odv-two-signatures-incremental.pdf` is a negative fixture for the out-of-specification generation
bump described under "Reading the PDF": its appended `3 1 obj` / `5 1 obj` must never shadow the
generation-0 originals. `odv-two-signatures-gen0.pdf` is the specification-conforming positive
fixture: both signatures are intact and document diagnostics are empty. `IncrementalUpdateSignatureTests`
also derives a conforming variant of the negative fixture in memory, re-signing the second
signature with the test CA for tampering tests. Origins and hashes are in the fixtures `README.md`.

Security regression coverage lives in `SignatureSecurityTests` and `SignatureValidationServiceTests`.
Network tests use an in-memory HTTP responder and generated CRLs; address classification and mixed
DNS results are tested separately, with no outbound network. `scripts/verify-signature-regressions.py`
temporarily breaks each F1–F7, N1–N4 and G1–G2 guard, expects its selected xUnit test to fail, restores the source in
a `finally` block, and runs the complete signature tests against the restored source. Run it only
in an isolated worktree without concurrent builds. Its logs stay under gitignored `TestResults/`.
