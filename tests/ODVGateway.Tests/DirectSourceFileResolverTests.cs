using Microsoft.Extensions.Logging.Abstractions;
using ODVGateway.Options;
using ODVGateway.Services;

namespace ODVGateway.Tests;

public sealed class DirectSourceFileResolverTests
{
    [Fact]
    public void TryResolve_RelativeClientPath_IsRejectedEvenWhenItResolvesBelowTrustedRoot()
    {
        var root = Path.Join(Path.GetTempPath(), "odvgateway-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var filePath = Path.Join(root, "source.pdf");
        File.WriteAllBytes(filePath, [1, 2, 3]);

        try
        {
            var options = new ODVGatewayOptions
            {
                TrustClientFilePath = true,
                TrustedSourceRoots = [root]
            };
            var monitor = new StaticOptionsMonitor<ODVGatewayOptions>(options);
            var resolver = new DirectSourceFileResolver(monitor, NullLogger<DirectSourceFileResolver>.Instance);
            var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), filePath);

            var resolved = resolver.TryResolve(relativePath, out _);

            Assert.False(resolved, "A client-supplied relative path must not be resolved against the gateway working directory.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
