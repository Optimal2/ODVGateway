using System.Text.Encodings.Web;
using System.Text.Json;
using ODVGateway.Models;

namespace ODVGateway.Services;

public sealed class OpenDocViewerIndexRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Default,
        WriteIndented = false
    };

    private readonly OpenDocViewerDistResolver _distResolver;
    private readonly ILogger<OpenDocViewerIndexRenderer> _logger;

    public OpenDocViewerIndexRenderer(
        OpenDocViewerDistResolver distResolver,
        ILogger<OpenDocViewerIndexRenderer> logger)
    {
        _distResolver = distResolver;
        _logger = logger;
    }

    public async Task<IResult> RenderAsync(
        HttpContext context,
        OpenDocViewerBundle? bundle,
        CancellationToken cancellationToken)
    {
        var distPath = _distResolver.ResolveDistPath();
        if (string.IsNullOrWhiteSpace(distPath))
        {
            _logger.LogWarning("OpenDocViewer dist path is not configured or could not be found.");
            return GatewayHtml.StatusPage(
                "OpenDocViewer dist folder was not found",
                "Configure ODVGateway:OpenDocViewerDistPath so the gateway can serve the OpenDocViewer web app.");
        }

        var indexPath = Path.Join(distPath, "index.html");
        string html;
        try
        {
            html = await File.ReadAllTextAsync(indexPath, cancellationToken);
        }
        catch (FileNotFoundException ex)
        {
            return MissingIndexPage(indexPath, ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            return MissingIndexPage(indexPath, ex);
        }
        catch (IOException ex)
        {
            return MissingIndexPage(indexPath, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return MissingIndexPage(indexPath, ex);
        }

        if (bundle is not null)
        {
            html = InjectBundle(html, bundle);
        }

        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static string InjectBundle(string html, OpenDocViewerBundle bundle)
    {
        var payload = SerializeForInlineScript(new { bundle });
        var script = """
<script id="odvgateway-bootstrap">
(function () {
  var payload = __ODVGATEWAY_PAYLOAD__;
  var api = window.ODV = window.ODV || {};
  api.__pending = payload;
  api.start = api.start || function (nextPayload) {
    api.__pending = nextPayload || {};
  };
})();
</script>
""".Replace("__ODVGATEWAY_PAYLOAD__", payload, StringComparison.Ordinal);

        var moduleScriptIndex = html.IndexOf("<script type=\"module\"", StringComparison.OrdinalIgnoreCase);
        if (moduleScriptIndex >= 0)
        {
            return html.Insert(moduleScriptIndex, script + Environment.NewLine);
        }

        var headEndIndex = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headEndIndex >= 0)
        {
            return html.Insert(headEndIndex, script + Environment.NewLine);
        }

        return script + Environment.NewLine + html;
    }

    private IResult MissingIndexPage(string indexPath, Exception ex)
    {
        _logger.LogWarning(ex, "OpenDocViewer index file could not be read at {IndexPath}.", indexPath);
        return GatewayHtml.StatusPage(
            "OpenDocViewer index.html was not found",
            "The OpenDocViewer dist folder was found, but index.html could not be read. Reinstall or republish the OpenDocViewer dist package.");
    }

    private static string SerializeForInlineScript<T>(T value)
    {
        // JavaScriptEncoder.Default escapes '<' today; the explicit '</' replacement keeps this
        // safe even if the serializer options change later or bundle values contain '</script>'.
        return JsonSerializer.Serialize(value, JsonOptions)
            .Replace("</", "<\\/", StringComparison.Ordinal);
    }
}
