using System.Formats.Asn1;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using ODVGateway.Options;

namespace ODVGateway.Services.Signatures;

/// <summary>
/// One file's bounded CRL transport. OS revocation downloads are never enabled. DNS is checked at
/// connect time and the socket connects to that exact address, closing the DNS rebinding window.
/// OCSP-only and unsupported distribution points fail closed until a bounded OCSP verifier exists.
/// </summary>
internal sealed class RevocationHttpClient : IDisposable
{
    internal const int MaxFetches = 8;
    internal const int MaxResponseBytes = 1024 * 1024;
    private readonly SignatureValidationOptions options;
    private readonly string? gatewayHost;
    private readonly HttpClient client;
    private int fetches;
    private readonly Dictionary<Uri, byte[]?> cache = [];
    private readonly HashSet<IPAddress> localAddresses = NetworkInterface.GetAllNetworkInterfaces()
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).ToHashSet();

    internal RevocationHttpClient(SignatureValidationOptions options, HttpMessageHandler? testHandler = null,
        string? gatewayHost = null)
    {
        this.options = options;
        this.gatewayHost = gatewayHost;
        client = new HttpClient(testHandler ?? CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal SocketsHttpHandler CreateHandler() => new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            // HTTPS verification must not introduce a second, unfiltered AIA/CRL network path.
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateChainPolicy = new X509ChainPolicy
                {
                    DisableCertificateDownloads = true,
                    RevocationMode = X509RevocationMode.NoCheck
                }
            },
            ConnectCallback = ConnectAsync
        };

    internal bool IsAllowedUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
        !string.Equals(uri.IdnHost.TrimEnd('.'), Environment.MachineName, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(uri.IdnHost.TrimEnd('.'), Dns.GetHostName(), StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(uri.IdnHost.TrimEnd('.'), gatewayHost?.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) &&
        (options.RevocationHostAllowList.Length == 0 || options.RevocationHostAllowList.Any(host =>
            string.Equals(host.TrimEnd('.'), uri.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))) &&
        (!IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) || IsAllowedAddress(address));

    private bool IsAllowedAddress(IPAddress address) => IsPublicAddress(address) && !localAddresses.Any(local =>
        Normalize(local).Equals(Normalize(address)));

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    internal bool AreAllowedAddresses(IReadOnlyList<IPAddress> addresses) =>
        addresses.Count > 0 && addresses.All(IsAllowedAddress);

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (b.Length == 4)
        {
            // Includes non-routable, documentation, benchmarking, CGNAT and reserved space.
            return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
                !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
                !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
                !(b[0] == 192 && (b[1] == 168 || b[1] == 0 || (b[1] == 88 && b[2] == 99))) &&
                !(b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100))) &&
                !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        }
        // Only global unicast, excluding transition mechanisms and special-purpose assignments.
        return b.Length == 16 && address.ScopeId == 0 && (b[0] & 0xe0) == 0x20 &&
            !(b[0] == 0x20 && b[1] == 0x02) &&
            !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || (b[2] == 0x0d && b[3] == 0xb8))) &&
            !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0);
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
        if (!AreAllowedAddresses(addresses))
            throw new HttpRequestException("Revocation destination is not public.");
        var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    internal async Task<byte[]?> FetchAsync(Uri uri, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsAllowedUri(uri)) return null;
        if (cache.TryGetValue(uri, out var cached)) return cached;
        if (++fetches > MaxFetches) return null;
        cache[uri] = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.RevocationTimeoutSeconds, 1, 30)));
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            // No redirects are followed, even to another public endpoint.
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxResponseBytes)
                return null;
            using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await body.ReadAsync(buffer, timeout.Token);
                if (count == 0) break;
                if (output.Length + count > MaxResponseBytes) return null;
                output.Write(buffer, 0, count);
            }
            return cache[uri] = output.ToArray();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException) { return null; }
    }

    internal async Task<OfflineRevocationStore.CrlVerdict> CheckAsync(X509Certificate2 certificate, X509Certificate2 issuer,
        DateTimeOffset at, CancellationToken token)
    {
        foreach (var uri in DistributionPoints(certificate))
        {
            token.ThrowIfCancellationRequested();
            var data = await FetchAsync(uri, token);
            if (data is null) continue;
            var verdict = OfflineRevocationStore.CheckDer(data, certificate, issuer, at);
            if (verdict.Status is OfflineRevocationStore.CrlStatus.NotListed or OfflineRevocationStore.CrlStatus.Revoked)
                return verdict;
        }
        return new(OfflineRevocationStore.CrlStatus.NoCrl, null, default, null, null);
    }

    internal static IReadOnlyList<Uri> DistributionPoints(X509Certificate2 certificate)
    {
        var result = new List<Uri>();
        var extension = certificate.Extensions["2.5.29.31"];
        if (extension is null || extension.RawData.Length > 16384) return result;
        try
        {
            var points = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            while (points.HasData && result.Count < MaxFetches)
            {
                var point = points.ReadSequence();
                var distributionPoint = point.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
                // Reasons and CRL issuers require scoped/indirect CRL processing, which is unsupported.
                if (point.HasData) continue;
                var names = distributionPoint.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
                distributionPoint.ThrowIfNotEmpty();
                while (names.HasData && result.Count < MaxFetches)
                {
                    var tag = new Asn1Tag(TagClass.ContextSpecific, 6);
                    if (!names.PeekTag().HasSameClassAndValue(tag)) { names.ReadEncodedValue(); continue; }
                    var name = names.ReadCharacterString(UniversalTagNumber.IA5String, tag);
                    if (Uri.TryCreate(name, UriKind.Absolute, out var uri)) result.Add(uri);
                }
            }
        }
        catch (AsnContentException) { return []; }
        return result;
    }

    public void Dispose() => client.Dispose();
}
