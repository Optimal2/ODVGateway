using Microsoft.AspNetCore.Http;
using ODVGateway.Models;
using ODVGateway.Options;
using ODVGateway.Services;

namespace ODVGateway.Tests;

public sealed class WebClientFallbackUrlBuilderTests
{
    private static WebClientSourceFallbackOptions CreateOptions(
        Action<WebClientSourceFallbackOptions>? configure = null)
    {
        var options = new WebClientSourceFallbackOptions();
        configure?.Invoke(options);
        return options;
    }

    private static HttpRequest CreateRequest(string pathBase = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("gateway.example");
        context.Request.PathBase = new PathString(pathBase);
        return context.Request;
    }

    private static GatewaySourceFile CreateFile(
        string filePath = "/direct/missing.tif",
        string? fileId = "file-1",
        string? extension = "tif",
        int index = 3)
    {
        return new GatewaySourceFile
        {
            Index = index,
            DocumentId = "doc-1",
            DocumentIndex = 0,
            FileNumber = 1,
            FileId = fileId,
            Extension = extension,
            FilePath = filePath,
            DisplayName = "display.tif"
        };
    }

    private static WebClientFallbackUrlResult Build(
        GatewaySourceFile file,
        WebClientSourceFallbackOptions options,
        HttpRequest? request = null,
        string sessionKey = "session-key")
    {
        return new WebClientFallbackUrlBuilder()
            .BuildFallbackUrl(request ?? CreateRequest(), sessionKey, file, options);
    }

    [Fact]
    public void Build_Disabled_ReturnsNone()
    {
        var result = Build(CreateFile(), CreateOptions(o => o.Enabled = false));

        Assert.Same(WebClientFallbackUrlResult.None, result);
    }

    [Fact]
    public void Build_UseWhenDirectFileMissingFalse_ReturnsNone()
    {
        var result = Build(CreateFile(), CreateOptions(o => o.UseWhenDirectFileMissing = false));

        Assert.Same(WebClientFallbackUrlResult.None, result);
    }

    [Fact]
    public void Build_FilePathIsAbsoluteHttpUrlOnRequestHost_ReturnsFilePathUrl()
    {
        var file = CreateFile(filePath: "https://gateway.example/streams/file-1");
        var result = Build(file, CreateOptions(o => o.UrlTemplate = "/ignored/{fileId}"));

        Assert.Equal(WebClientFallbackUrlKind.FilePath, result.Kind);
        Assert.Equal("https://gateway.example/streams/file-1", result.Url);
    }

    [Fact]
    public void Build_FilePathAbsoluteUrlOtherHost_RequireSameHost_FallsThroughToTemplate()
    {
        var file = CreateFile(filePath: "https://other.example/streams/file-1");
        var result = Build(file, CreateOptions());

        Assert.Equal(WebClientFallbackUrlKind.Template, result.Kind);
        Assert.Equal("https://gateway.example/WebClientODV/DocumentView/GetStream/?ticket=file-1", result.Url);
    }

    [Fact]
    public void Build_FilePathAbsoluteUrlOtherHost_InAllowedHosts_ReturnsFilePathUrl()
    {
        var file = CreateFile(filePath: "https://webclient.example:8443/streams/file-1");
        var options = CreateOptions(o => o.AllowedHosts = ["https://webclient.example:8443"]);

        var result = Build(file, options);

        Assert.Equal(WebClientFallbackUrlKind.FilePath, result.Kind);
        Assert.Equal("https://webclient.example:8443/streams/file-1", result.Url);
    }

    [Fact]
    public void Build_FilePathAbsoluteUrlOtherHost_BareHostPortAllowedHost_Matches()
    {
        // A bare "host:port" AllowedHosts entry (no scheme) must match. On
        // .NET, Uri.TryCreate("host:port", Absolute) succeeds with the host
        // text as scheme and an empty Host, so HostMatches must only treat
        // the entry as a URI when the scheme is http/https and otherwise use
        // the port-split parse.
        var file = CreateFile(filePath: "https://webclient.example:8443/streams/file-1");
        var options = CreateOptions(o => o.AllowedHosts = ["webclient.example:8443"]);

        var result = Build(file, options);

        Assert.Equal(WebClientFallbackUrlKind.FilePath, result.Kind);
        Assert.Equal("https://webclient.example:8443/streams/file-1", result.Url);
    }

    [Fact]
    public void Build_FilePathAbsoluteUrlOtherHost_BareIpv4HostPortAllowedHost_Matches()
    {
        var file = CreateFile(filePath: "http://10.20.30.40:8080/streams/file-1");
        var options = CreateOptions(o => o.AllowedHosts = ["10.20.30.40:8080"]);

        var result = Build(file, options);

        Assert.Equal(WebClientFallbackUrlKind.FilePath, result.Kind);
        Assert.Equal("http://10.20.30.40:8080/streams/file-1", result.Url);
    }

    [Fact]
    public void Build_FilePathAbsoluteUrlOtherHost_AllowedHostWithWrongPort_FallsThroughToTemplate()
    {
        var file = CreateFile(filePath: "https://webclient.example:8443/streams/file-1");
        var options = CreateOptions(o => o.AllowedHosts = ["webclient.example:9443"]);

        var result = Build(file, options);

        Assert.Equal(WebClientFallbackUrlKind.Template, result.Kind);
    }

    [Fact]
    public void Build_FilePathIsUncShare_FallsThroughToTemplate()
    {
        var file = CreateFile(filePath: @"\\fileserver\share\doc.tif");
        var result = Build(file, CreateOptions());

        Assert.Equal(WebClientFallbackUrlKind.Template, result.Kind);
        Assert.Equal("https://gateway.example/WebClientODV/DocumentView/GetStream/?ticket=file-1", result.Url);
    }

    [Fact]
    public void Build_FilePathIsLocalDrivePath_FallsThroughToTemplate()
    {
        var file = CreateFile(filePath: @"E:\Shares\doc.tif");
        var result = Build(file, CreateOptions());

        Assert.Equal(WebClientFallbackUrlKind.Template, result.Kind);
    }

    [Fact]
    public void Build_FilePathRootRelative_ReturnsRequestHostUrl()
    {
        var file = CreateFile(filePath: "/webclient/stream/file-1");
        var result = Build(file, CreateOptions());

        Assert.Equal(WebClientFallbackUrlKind.FilePath, result.Kind);
        Assert.Equal("https://gateway.example/webclient/stream/file-1", result.Url);
    }

    [Fact]
    public void Build_FilePathRelative_ReturnsRequestHostUrlWithSlash()
    {
        var file = CreateFile(filePath: "webclient/stream/file-1");
        var result = Build(file, CreateOptions());

        Assert.Equal(WebClientFallbackUrlKind.FilePath, result.Kind);
        Assert.Equal("https://gateway.example/webclient/stream/file-1", result.Url);
    }

    [Fact]
    public void Build_FilePathSchemeRelative_IsParsedAsFileUriAndFallsThroughToTemplate()
    {
        // Documents .NET URI behavior: "//host/path" parses as an absolute
        // file:// URI, so the dedicated scheme-relative branch in
        // BuildBrowserUrlFromSourcePath is effectively unreachable for this
        // input and the builder falls through to the URL template instead.
        var file = CreateFile(filePath: "//gateway.example/streams/file-1");
        var result = Build(file, CreateOptions());

        Assert.Equal(WebClientFallbackUrlKind.Template, result.Kind);
        Assert.Equal("https://gateway.example/WebClientODV/DocumentView/GetStream/?ticket=file-1", result.Url);
    }

    [Fact]
    public void Build_FilePathDisabled_SkipsFilePathAndUsesTemplate()
    {
        var file = CreateFile(filePath: "https://gateway.example/streams/file-1");
        var options = CreateOptions(o => o.UseFilePathUrlWhenDirectFileMissing = false);

        var result = Build(file, options);

        Assert.Equal(WebClientFallbackUrlKind.Template, result.Kind);
    }

    [Fact]
    public void Build_TemplatePlaceholders_AreReplacedAndEscaped()
    {
        var file = CreateFile(fileId: "id/with space", extension: "tif", index: 7);
        var options = CreateOptions(o =>
        {
            o.UseFilePathUrlWhenDirectFileMissing = false;
            o.UrlTemplate = "/view/{fileId}/{sessionKey}/{fileIndex}/{extension}";
        });

        var result = Build(file, options, sessionKey: "key+1");

        Assert.Equal(WebClientFallbackUrlKind.Template, result.Kind);
        Assert.Equal(
            "https://gateway.example/view/id%2Fwith%20space/key%2B1/7/tif",
            result.Url);
    }

    [Fact]
    public void Build_TemplateWithoutLeadingSlash_UsesPathBase()
    {
        var options = CreateOptions(o =>
        {
            o.UseFilePathUrlWhenDirectFileMissing = false;
            o.UrlTemplate = "view/{fileId}";
        });

        var result = Build(CreateFile(), options, request: CreateRequest(pathBase: "/gateway"));

        Assert.Equal("https://gateway.example/gateway/view/file-1", result.Url);
    }

    [Fact]
    public void Build_AbsoluteTemplateOnOtherHost_RequireSameHost_ReturnsNone()
    {
        var options = CreateOptions(o =>
        {
            o.UseFilePathUrlWhenDirectFileMissing = false;
            o.UrlTemplate = "https://other.example/view/{fileId}";
        });

        var result = Build(CreateFile(), options);

        Assert.Same(WebClientFallbackUrlResult.None, result);
    }

    [Fact]
    public void Build_AbsoluteTemplateOnOtherHost_InAllowedHosts_ReturnsTemplateUrl()
    {
        var options = CreateOptions(o =>
        {
            o.UseFilePathUrlWhenDirectFileMissing = false;
            o.UrlTemplate = "https://webclient.example/view/{fileId}";
            o.AllowedHosts = ["https://webclient.example"];
        });

        var result = Build(CreateFile(), options);

        Assert.Equal(WebClientFallbackUrlKind.Template, result.Kind);
        Assert.Equal("https://webclient.example/view/file-1", result.Url);
    }

    [Fact]
    public void Build_MissingFileId_ReturnsNone()
    {
        var options = CreateOptions(o => o.UseFilePathUrlWhenDirectFileMissing = false);

        var result = Build(CreateFile(fileId: null), options);

        Assert.Same(WebClientFallbackUrlResult.None, result);
    }

    [Fact]
    public void Build_EmptyUrlTemplate_ReturnsNone()
    {
        var options = CreateOptions(o =>
        {
            o.UseFilePathUrlWhenDirectFileMissing = false;
            o.UrlTemplate = "  ";
        });

        var result = Build(CreateFile(), options);

        Assert.Same(WebClientFallbackUrlResult.None, result);
    }
}
