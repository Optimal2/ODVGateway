# PDF signature validation on the gateway (`GET /signatures/{sessionKey}/{fileIndex}`)

Status: implemented, disabled by default.
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
  "validatedAt": "2025-06-04T09:12:35+00:00"
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
| PDF cannot be opened/parsed at all, or is encrypted | `422` |
| file larger than `Signatures:MaxFileBytes` | `413` |
| PDF parses but has no signature fields | `200` with `signatures: []` |

Limits:

- **Size**: `Signatures:MaxFileBytes`. `0` (the default) follows the gateway's existing source
  transport limit (`maxSourcePackFrameBytes`, 64 MiB); a positive value caps the endpoint separately
  but never above that transport limit. The gateway buffers the file to validate it, so the limit is
  enforced before parsing, both for the direct file and for a proxied fallback body.
- **Time**: one fixed, non-configurable budget per file, `SignatureValidationLimits.FileBudgetSeconds`
  = 60 seconds, enforced with a linked `CancellationTokenSource`. A signature that is not finished
  inside the budget is reported as `trust: "unknown"` with `trustReason: "validation-timeout"`;
  the response itself still succeeds. Revocation latency itself is bounded by
  `Signatures:RevocationTimeoutSeconds`.
- **Concurrency**: nothing special. Requests that buffer a large PDF are bounded by the size limit,
  and `/signatures` is off unless an operator turns it on.
- `Cache-Control: no-store` always, including on error responses, because a verdict is a statement
  about "now".

## Reading the PDF

`PdfPig` (Apache-2.0) is used to walk the PDF object graph: catalog, `/AcroForm/Fields` (including
`/Kids` inheritance), `/FT /Sig` fields, their `/V` signature dictionaries, and `/Perms/DocMDP`.

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

A signature dictionary carries `/ByteRange [offset1 length1 offset2 length2 ...]` and `/Contents`
(the hex placeholder that holds the CMS blob). Validation:

1. The ranges must be sorted, inside the file, and must not overlap the `/Contents` value.
   Anything else: `integrity: "unsupported"`, reason `byte-range-malformed`.
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
4. `coversWholeFile` is true when the ranges start at 0 and end at the last byte of the file, so
   only the `/Contents` placeholder sits outside them.
5. Trailing bytes outside the ranges mean an incremental update after this signature. That is
   normal when another approval signature was appended, so for `kind: "approval"` it stays
   `intact` with `coversWholeFile: false`. For a certification signature it means the document was
   changed after certification (or the /M/DocMDP promise was broken):
   `integrity: "modified-after-signing"`, reason `bytes-appended-after-signed-range`.

`digest-mismatch` and `signature-invalid` always mean `trust: "invalid"`: a signature that does not
verify cannot be trusted at any time. `unsupported`/`unreadable` mean `trust: "unknown"`.

## Timestamps and the validation time

Two times are reported per signature, and they come from different places.

`validationTime` is the instant the chain is evaluated at:

- A signature timestamp token (CMS unsigned attribute `1.2.840.113549.1.9.16.2.14`,
  `id-aa-signatureTimeStampToken`) that decodes with `Rfc3161TimestampToken.TryDecode`, whose
  message imprint matches the signature value, whose own signature verifies over the signer
  (`VerifySignatureForSignerInfo`), and whose responder chains to a configured anchor moves
  `validationTime` to the token's `genTime`. A broken, foreign, or unanchored token never does.
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

A chain that does **not** end in one of the configured anchors is never `valid`:

| Chain outcome | `trust` | `trustReason` |
| --- | --- | --- |
| built, anchored, no status flags | `valid` (see revocation gate) | `null` or revocation note |
| `UntrustedRoot` / `PartialChain` (no configured anchor, or issuer not in the document) | `unknown` | `chain-not-anchored` |
| `NotTimeValid` | `unknown` | `certificate-not-valid-at-validation-time` |
| `Revoked` / `CrlNotForTimeOfUse` (with revocationDate <= validationTime) | `invalid` | `revoked` |
| `InvalidBasicConstraints`, `WeakSignatureAlgorithm` (SHA-1/MD5 hash or < 2048-bit RSA key) | `invalid` | `chain-policy-violation` / `weak-signature` |
| `RevocationStatusUnknown`, `OfflineRevocation`, `CrlNotYetValid` | `unknown` | `revocation-unavailable` |
| anything unexpected | `unknown` | `validation-error` |

Revocation is decided by exactly one source, in this order:

1. **Offline CRL files** (`Signatures:CrlDirectory`, optional). The gateway parses the DER/PEM CRLs
   itself (`System.Formats.Asn1`), verifies each CRL with its issuer's public key, requires
   `thisUpdate <= validationTime <= nextUpdate`, and looks up each chain element's serial. If every
   non-anchor element is covered by a usable CRL, revocation is considered **checked** and the OS
   revocation engine is skipped (`RevocationMode.NoCheck` for the chain build). A listed serial with
   `revocationDate <= validationTime` means `invalid` + `revoked`. This is the path for a gateway
   that has no outbound HTTP at all, and it is what the unit tests use.
2. **OS chain engine** with `Signatures:RevocationMode` = `Online` (default), `Offline`, or
   `NoCheck`, and `X509ChainPolicy.UrlRetrievalTimeout` = `Signatures:RevocationTimeoutSeconds`.

`Online` mode therefore **needs outbound HTTP from the server to the issuers' OCSP/CRL endpoints**
(the URLs come from the certificates, not from configuration). Where that is blocked, the gateway
reports `revocation-unavailable` and never `valid`. Operators who cannot reach those endpoints
should either publish CRL files into `Signatures:CrlDirectory` or set
`Signatures:RevocationMode: "Offline"`.

`Signatures:RevocationMode: "NoCheck"` deliberately skips revocation. Because "not checked" is an
operator decision rather than a failure, the verdict may still be `valid`, and the reason is then
`revocation-not-checked`. Every other configuration keeps the rule: `valid` requires that revocation
was actually checked.

## Failure isolation and logging

Each signature is validated in its own `try/catch` and its own timeout scope. One unreadable
signature dictionary, one malformed CMS blob, or one hanging OCSP endpoint can only ever change the
verdict of that signature; the response always contains the other signatures and always succeeds
with `200` once the file could be read.

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
    "maxFileBytes": 0
  }
}
```

## Testing

Tests build a throwaway CA hierarchy in code (`CertificateRequest`, `X509SignatureGenerator`,
`CertificateRevocationListBuilder`), sign synthetic single-page PDFs written by the fixture helper,
and write DER CRL files into a temp directory. No network access, no fixtures in the repository:
every byte is generated per test run, so nothing that looks like a customer document can leak into
a public repository.
