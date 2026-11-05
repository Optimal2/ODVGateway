namespace ODVGateway.Tests;

/// <summary>
/// Source-level regressions for the owned GitHub standard findings. The behavioral suites
/// exercise the same code; these checks keep the exception and resource ownership explicit.
/// </summary>
public sealed class GitHubQualityWP5Tests
{
    [Fact]
    public void LegacyStartupExceptionBoundariesAreNotSilent()
    {
        var source = ReadRepositoryFile("tests", "ODVGateway.Tests", "LegacySignatureStartupTests.cs");

        Assert.DoesNotMatch(@"catch\s*\([^)]*\)\s*\{\s*\}", source);
    }

    [Fact]
    public void LegacyStartupResourcesAndAssemblyPathHaveExplicitOwnership()
    {
        var source = ReadRepositoryFile("tests", "ODVGateway.Tests", "LegacySignatureStartupTests.cs");

        Assert.Contains("using var prepContent = new StringContent(", source);
        Assert.Contains("using var listener = new TcpListener(", source);
        Assert.Contains("Path.Join(AppContext.BaseDirectory, \"ODVGateway.dll\")", source);
    }

    [Fact]
    public void SourceProxyResponseCreationShowsOwnershipEscape()
    {
        var source = ReadRepositoryFile("tests", "ODVGateway.Tests", "SourceProxyHttpTests.cs");

        Assert.Contains("return Task.FromResult(CreateResponse());", source);
        Assert.Contains("private HttpResponseMessage CreateResponse()", source);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "omp-components.json")))
            {
                var path = segments.Aggregate(
                    directory.FullName, (root, segment) => Path.Join(root, segment));
                return File.ReadAllText(path);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ODVGateway repository root.");
    }
}
