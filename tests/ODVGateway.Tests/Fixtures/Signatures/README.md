# Synthetic signature fixtures

These files are synthetic test inputs. They contain no real documents, no real people and no
real certificates.

## `odv-two-signatures-incremental.pdf`

- Origin: `two-signatures.pdf` produced by `scripts/generate-signature-fixtures.mjs` in the public
  OpenDocViewer repository (`node scripts/generate-signature-fixtures.mjs --out <dir>`), copied
  byte for byte on 2026-10-04. SHA-256
  `7fa9496c44eb5d560532f50b3c2b23415834ca612f5a725b5cccdd63ce0543ca`.
- Content: one page with the text "ODV SIGNATURE FIXTURE SAMPLE TEXT", signed by
  `CN=ODV Fixture Signer` (field `Signature1`), then extended by an incremental update that adds
  field `ApprovalTwo` signed by `CN=ODV Fixture Approver`. Both certificates come from the
  generator's throwaway `CN=ODV Fixture Signing CA`, whose private keys are created per run and
  discarded. That CA is not a trust anchor in any test, so trust is never `valid` for this file.
- Why it is kept as a file: the incremental update rewrites the page (object 3) and the AcroForm
  (object 5) with generation number **1** (`3 1 obj`, `5 1 obj`, xref entries `00001 n`) while the
  catalog and page tree still reference `3 0 R` and `5 0 R`. The gateway's own in-code fixtures
  keep generation 0, so this structure is only covered by this file.
  See `IncrementalUpdateSignatureTests`.
