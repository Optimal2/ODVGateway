using ODVGateway.Options;

namespace ODVGateway.Tests;

public sealed class InlineSourceOptionsTests
{
    [Theory]
    [InlineData("jxl")]
    [InlineData("avif")]
    public void DefaultInlineSources_AllowNewImageFormats(string extension)
    {
        Assert.Contains(extension, new InlineSourceOptions().Extensions);
    }

    [Theory]
    [InlineData("jxl")]
    [InlineData("avif")]
    public void DefaultRemoteInlineSources_AllowNewImageFormats(string extension)
    {
        Assert.Contains(extension, new RemoteInlineSourceOptions().Extensions);
    }
}
