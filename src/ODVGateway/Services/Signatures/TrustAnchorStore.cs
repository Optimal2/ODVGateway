using System.Security.Cryptography.X509Certificates;

namespace ODVGateway.Services.Signatures;

/// <summary>
/// Collects the trust anchors the gateway is willing to accept: the Windows trusted-root stores
/// (when enabled) and every certificate in <c>Signatures:ExtraAnchorsDirectory</c>. Chain building
/// uses <see cref="X509ChainTrustMode.CustomRootTrust"/>, so nothing outside this set can ever make
/// a signature trusted.
/// </summary>
public sealed class TrustAnchorStore
{
    private readonly Options.SignatureValidationOptions options;
    private readonly ILogger logger;
    private readonly string contentRootPath;
    private X509Certificate2Collection? cache;

    public TrustAnchorStore(Options.SignatureValidationOptions options, ILogger logger, string contentRootPath)
    {
        this.options = options;
        this.logger = logger;
        this.contentRootPath = contentRootPath;
    }

    /// <summary>
    /// The anchor set, built once per gateway process. Restart the gateway after changing the
    /// configured anchor directory; the set is not watched for file changes.
    /// </summary>
    public X509Certificate2Collection Anchors => cache ??= Build();

    private X509Certificate2Collection Build()
    {
        var anchors = new X509Certificate2Collection();
        var windowsCount = 0;
        if (options.UseWindowsTrustedRoots)
        {
            windowsCount = AddWindowsRoots(anchors);
        }

        var directoryCount = 0;
        var configured = options.ExtraAnchorsDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var directory = Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(contentRootPath, configured));
            if (Directory.Exists(directory))
            {
                foreach (var path in Directory.EnumerateFiles(directory)
                             .Where(IsCertificateFile)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (TryAddCertificateFromFile(anchors, path))
                    {
                        directoryCount++;
                    }
                }
            }
            else
            {
                logger.LogWarning("Signature validation: configured anchor directory does not exist. Exists={Exists}", false);
            }
        }

        logger.LogInformation(
            "Signature validation: trust anchors loaded. WindowsRoots={WindowsCount} Directory={DirectoryCount} Total={TotalCount}",
            windowsCount, directoryCount, anchors.Count);
        return anchors;
    }

    private static bool IsCertificateFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".cer", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".crt", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".der", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".pem", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".base64", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryAddCertificateFromFile(X509Certificate2Collection collection, string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            collection.Add(X509CertificateLoader.LoadCertificate(bytes));
            return true;
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException
                                                 or IOException
                                                 or UnauthorizedAccessException
                                                 or FormatException)
        {
            // Only the file name is logged: it is operator-supplied configuration, not document content.
            logger.LogWarning(exception,
                "Signature validation: anchor file could not be read. File={FileName} Exception={ExceptionType}",
                Path.GetFileName(path), exception.GetType().Name);
            return false;
        }
    }

    private static int AddWindowsRoots(X509Certificate2Collection collection)
    {
        var added = 0;
        foreach (var storeName in new[] { StoreName.Root })
        {
            foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
            {
                using var store = new X509Store(storeName, location);
                try
                {
                    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // A service account may not have a CurrentUser root store; that is not an error.
                    continue;
                }

                foreach (var certificate in store.Certificates)
                {
                    if (!collection.Contains(certificate))
                    {
                        collection.Add(certificate);
                        added++;
                    }
                }
            }
        }

        return added;
    }
}
