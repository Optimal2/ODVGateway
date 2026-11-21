using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ODVGateway.Models;
using ODVGateway.Options;
using ODVGateway.Services;

namespace ODVGateway.Tests;

// Covers OpenDocViewerBundleFactory.BuildViewerFile decisions that have no
// other test: the source-kind classification (B051) and the raw metadata
// passthrough (B052).
public sealed class OpenDocViewerBundleFactoryTests
{
    private static OpenDocViewerBundleFactory CreateFactory(ODVGatewayOptions? options = null)
    {
        options ??= new ODVGatewayOptions();
        var monitor = new StaticOptionsMonitor<ODVGatewayOptions>(options);
        return new OpenDocViewerBundleFactory(
            new ContentTypeMapper(Microsoft.Extensions.Options.Options.Create(options)),
            new DirectSourceFileResolver(monitor, NullLogger<DirectSourceFileResolver>.Instance),
            new WebClientFallbackUrlBuilder(),
            monitor);
    }

    private static HttpRequest CreateRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("gateway.example");
        return context.Request;
    }

    private static GatewaySession CreateSession(
        IReadOnlyList<GatewaySourceFile> sources,
        IReadOnlyList<WebClientPortableDocument>? documents = null)
    {
        return new GatewaySession
        {
            SessionKey = "session-key",
            HandoffLookupKey = "lookup-key",
            Prep = new WebClientPrepRequest
            {
                UserId = "user",
                SessionId = "session",
                PortableDocuments = [.. documents ?? [new WebClientPortableDocument { DocumentId = "document-1" }]]
            },
            CreatedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            SourceFiles = sources
        };
    }

    private static GatewaySourceFile CreateSource(int index, string extension)
    {
        return new GatewaySourceFile
        {
            Index = index,
            DocumentId = "document-1",
            DocumentIndex = 0,
            FileNumber = index + 1,
            FileId = $"file-{index}",
            Extension = extension,
            FilePath = $"/direct/missing-{index}.{extension}",
            DisplayName = $"missing-{index}.{extension}"
        };
    }

    [Theory]
    [InlineData("pdf", "pdf", null)]
    [InlineData("tif", "tiff", null)]
    [InlineData("tiff", "tiff", null)]
    [InlineData("jpg", "raster-single-page", 1)]
    [InlineData("jpeg", "raster-single-page", 1)]
    [InlineData("png", "raster-single-page", 1)]
    [InlineData("bmp", "raster-single-page", 1)]
    [InlineData("gif", "raster-single-page", 1)]
    [InlineData("webp", "raster-single-page", 1)]
    [InlineData("jxl", "raster-single-page", 1)]
    [InlineData("avif", "raster-single-page", 1)]
    [InlineData("xyz", "unknown", null)]
    public async Task BuildViewerFile_ClassifiesSourceKindAndPageCountHint(
        string extension, string expectedKind, int? expectedHint)
    {
        var factory = CreateFactory();
        var session = CreateSession([CreateSource(0, extension)]);

        var bundle = await factory.CreateAsync(
            CreateRequest(), session, sessionData: null, TestContext.Current.CancellationToken);

        var file = Assert.Single(bundle.Documents).Files.Single();
        Assert.Equal(expectedKind, file.SourceKind);
        Assert.Equal(expectedHint, file.PageCountHint);
    }

    [Fact]
    public async Task BuildMetadata_PreservesRawFieldStringsExactly()
    {
        var factory = CreateFactory();
        var session = CreateSession(
            [CreateSource(0, "pdf")],
            [new WebClientPortableDocument
            {
                DocumentId = "document-1",
                MetaData =
                [
                    new WebClientMetadataField { Id = "1", Value = "  padded  ", LookupValue = "   " },
                    new WebClientMetadataField { Id = "2", Value = "", LookupValue = null }
                ]
            }]);

        var bundle = await factory.CreateAsync(
            CreateRequest(), session, sessionData: null, TestContext.Current.CancellationToken);

        var metaById = Assert.Single(bundle.Documents).MetaById!;
        var first = metaById["1"];
        Assert.Equal("  padded  ", first.Value);
        Assert.Equal("  padded  ", first.RawValue);
        Assert.Null(first.LookupValue);
        Assert.Equal("   ", first.RawLookupValue);

        var second = metaById["2"];
        Assert.Null(second.Value);
        Assert.Equal(string.Empty, second.RawValue);
        Assert.Null(second.LookupValue);
        Assert.Null(second.RawLookupValue);
    }
}
