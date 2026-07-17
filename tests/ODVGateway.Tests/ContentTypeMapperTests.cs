using ODVGateway.Options;
using ODVGateway.Services;

namespace ODVGateway.Tests;

public sealed class ContentTypeMapperTests
{
    private static ContentTypeMapper CreateMapper(Dictionary<string, string>? overrides = null)
    {
        var options = new ODVGatewayOptions();
        if (overrides is not null)
        {
            options.ContentTypes = overrides;
        }

        return new ContentTypeMapper(Microsoft.Extensions.Options.Options.Create(options));
    }

    [Theory]
    [InlineData("pdf", "application/pdf")]
    [InlineData("tif", "image/tiff")]
    [InlineData("tiff", "image/tiff")]
    [InlineData("jpg", "image/jpeg")]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("png", "image/png")]
    [InlineData("bmp", "image/bmp")]
    [InlineData("gif", "image/gif")]
    [InlineData("webp", "image/webp")]
    public void GetContentType_ReturnsDefaultMapping(string extension, string expected)
    {
        var mapper = CreateMapper();

        Assert.Equal(expected, mapper.GetContentType(extension));
    }

    [Theory]
    [InlineData("PDF", "application/pdf")]
    [InlineData(".pdf", "application/pdf")]
    [InlineData("  .PDF ", "application/pdf")]
    [InlineData(".Tiff", "image/tiff")]
    public void GetContentType_NormalizesCaseLeadingDotAndWhitespace(string extension, string expected)
    {
        var mapper = CreateMapper();

        Assert.Equal(expected, mapper.GetContentType(extension));
    }

    [Fact]
    public void GetContentType_NormalizesWhitespacePaddedPdf()
    {
        var mapper = CreateMapper();

        Assert.Equal("application/pdf", mapper.GetContentType("  .PDF "));
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData(null)]
    public void GetContentType_UnknownOrMissingExtensionFallsBackToOctetStream(string? extension)
    {
        var mapper = CreateMapper();

        Assert.Equal("application/octet-stream", mapper.GetContentType(extension));
    }

    [Fact]
    public void GetContentType_OptionsOverrideWinsOverDefault()
    {
        var mapper = CreateMapper(new Dictionary<string, string>
        {
            ["pdf"] = "application/x-custom-pdf",
            [".MSG"] = "application/vnd.ms-outlook"
        });

        Assert.Equal("application/x-custom-pdf", mapper.GetContentType("pdf"));
        Assert.Equal("application/vnd.ms-outlook", mapper.GetContentType("msg"));
        // Non-overridden defaults still apply.
        Assert.Equal("image/png", mapper.GetContentType("png"));
    }
}
