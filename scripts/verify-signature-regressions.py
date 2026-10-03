"""Temporarily break F1-F7 and N1-N4 guards and prove the xUnit tests fail.

No network is used by these tests. Source bytes are restored even on failure. Never run alongside
another build/edit in the same worktree. Results are written only to gitignored TestResults.
"""
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parent.parent
SOURCE = Path('src/ODVGateway/Services/Signatures')
TESTS = 'tests/ODVGateway.Tests/ODVGateway.Tests.csproj'
MUTATIONS = [
    ('N1', 'OfflineRevocationStore.cs',
     'stale ??= new CrlVerdict(CrlStatus.StaleCrl, null, crl.ThisUpdate, crl.NextUpdate, null);',
     'return new CrlVerdict(CrlStatus.StaleCrl, null, crl.ThisUpdate, crl.NextUpdate, null);',
     'ExpiredCertificate_WithVerifiableTimestamp_IsValid'),
    ('N2', 'OfflineRevocationStore.cs',
     'if (!TryReadScope(value, out scope)) return false;', 'return false;', 'N2_FullScopeIdp_IsAcceptedOnline'),
    ('N2-scope', 'OfflineRevocationStore.cs',
     'if ((UsersOnly && ca) || (CasOnly && !ca)) return false;', '// Mutation: ignore certificate-type restrictions.',
     'N2_ScopeMustCoverCertificate'),
    ('N3-async', 'RevocationHttpClient.cs',
     'await FetchAsync(uri, token)', 'FetchAsync(uri, token).GetAwaiter().GetResult()',
     'N3_Validation_YieldsDuringNetworkWait_AndHonorsCancellation'),
    ('N3-limit', '../../Program.cs',
     'if (!signatureLease.IsAcquired)', 'if (false)', 'N3_Saturation_Returns503AndReleasesPermitAfterError'),
    ('N4', 'PdfSignatureLocator.cs',
     'Math.Clamp(fileBytes.Length / 8, 10000, 8_000_000)', '10000',
     'N4_LargePageTree_WithinSizeLimit_IsAccepted'),
    ('F1', 'PdfSignatureIntegrityVerifier.cs',
     'byteRange[2] != signature.ContentsEnd', 'false', 'F1_UnsignedNoteInsideGap'),
    ('F2', 'SignatureTrustEvaluator.cs',
     'if (!built || flags != X509ChainStatusFlags.NoError)', 'if (false)', 'F2_IncompleteOrUnexpectedChain'),
    ('F3', 'PdfSignatureLocator.cs',
     'if (!visited.Add(kid)) continue;', '// Mutation: enqueue duplicate page references.',
     'F3_RepeatedAndCyclicPageReferences'),
    ('F4', 'PdfSignatureValidationService.cs',
     'signatures = locator.Locate(fileBytes, token);', 'signatures = locator.Locate(fileBytes);',
     'F4_CancelledValidation_StopsBeforeParsing'),
    ('F5', 'RevocationHttpClient.cs',
     '|| IsAllowedAddress(address));', '|| true);', 'F5_Transport_BlocksSchemesPrivateHostsAndRedirects'),
    ('F6', 'SignatureTrustEvaluator.cs',
     'return (PdfSignatureTrust.Unknown, SignatureValidationReasons.RevocationNotChecked);',
     'return (PdfSignatureTrust.Valid, SignatureValidationReasons.RevocationNotChecked);',
     'RevocationSkipped_InNoCheckMode'),
    ('F7', 'SignatureTrustEvaluator.cs',
     'if (verdict.Trust != PdfSignatureTrust.Valid)', 'if (false)',
     'F7_RevokedTimestampResponder_CannotReviveExpiredSigner'),
]


def run_tests(selector, name):
    result = subprocess.run(['dotnet', 'test', TESTS, '--configuration', 'Release', '--filter',
                             f'FullyQualifiedName~{selector}', '--verbosity', 'normal'],
                            cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
    (ROOT / 'TestResults' / f'signature-{name}.log').write_bytes(result.stdout)
    return result.returncode, result.stdout.decode('utf-8', errors='replace')


def main():
    (ROOT / 'TestResults').mkdir(exist_ok=True)
    results = []
    for finding, file, before, after, test in MUTATIONS:
        path = ROOT / SOURCE / file
        original = path.read_bytes()
        text = original.decode('utf-8')
        if text.count(before) != 1:
            raise RuntimeError(f'{finding}: expected exactly one mutation target')
        try:
            path.write_bytes(text.replace(before, after).encode('utf-8'))
            code, output = run_tests(test, finding)
            # A build failure is not evidence that the regression test detects the defect.
            detected = code == 1 and '[FAIL]' in output and test in output
            results.append(dict(finding=finding, exitCode=code, detected=detected, test=test))
            print(f'{finding}: exit {code}; regression detected={detected}', flush=True)
            if not detected:
                raise RuntimeError(f'{finding}: mutation was not killed; inspect TestResults/signature-{finding}.log')
        finally:
            path.write_bytes(original)
    code, _ = run_tests('Signature', 'restored')
    print(f'Restored signature suite: exit {code}', flush=True)
    (ROOT / 'TestResults' / 'signature-mutations.json').write_text(
        json.dumps(dict(mutations=results, restoredExitCode=code), indent=2) + '\n', encoding='utf-8')
    return code


if __name__ == '__main__':
    sys.exit(main())
