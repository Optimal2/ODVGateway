using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ODVGateway.Models;
using ODVGateway.Options;
using ODVGateway.Services;

// Default CSP is restrictive but allows the inline bootstrap script injected by
// OpenDocViewerIndexRenderer, the inline styles used by GatewayHtml.StatusPage,
// and the same-origin OpenDocViewer dist files. Deployments can override the
// entire policy via ODVGateway:contentSecurityPolicy in appsettings.json.
const string DefaultContentSecurityPolicy =
    "default-src 'self'; " +
    "script-src 'self' 'unsafe-inline'; " +
    "style-src 'self' 'unsafe-inline'; " +
    "img-src 'self' data: blob:; " +
    "connect-src 'self'; " +
    "font-src 'self'; " +
    "media-src 'self'; " +
    "object-src 'self'; " +
    "worker-src 'self' blob:; " +
    "frame-ancestors 'self'; " +
    "base-uri 'self'; " +
    "form-action 'self'";

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

var startupOptions = builder.Configuration
    .GetSection(ODVGatewayOptions.SectionName)
    .Get<ODVGatewayOptions>() ?? new ODVGatewayOptions();

ValidateTrustedSourceRootConfiguration(startupOptions, builder.Environment.ContentRootPath);

builder.Services.Configure<ODVGatewayOptions>(
    builder.Configuration.GetSection(ODVGatewayOptions.SectionName));
builder.Services.AddSingleton<ContentTypeMapper>();
builder.Services.AddSingleton<GatewaySessionStore>();
builder.Services.AddSingleton<DirectSourceFileResolver>();
builder.Services.AddSingleton<WebClientHandoffGuard>();
builder.Services.AddSingleton<OpenDocViewerDistResolver>();
builder.Services.AddSingleton<OpenDocViewerBundleFactory>();
builder.Services.AddSingleton<OpenDocViewerIndexRenderer>();
builder.Services.AddSingleton<WebClientSourceProxyLimiter>();
builder.Services.AddSingleton<WebClientFallbackUrlBuilder>();
builder.Services.AddHttpClient("ODVGateway.RemoteInline");

builder.Services.Configure<FormOptions>(options =>
{
    options.ValueLengthLimit = ClampFormValueLengthLimitBytes(startupOptions.MaxPrepBodyBytes);
    options.MultipartBodyLengthLimit = 64L * 1024L * 1024L;
});
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
    [
        "application/json"
    ]);
});

var app = builder.Build();

var distResolver = app.Services.GetRequiredService<OpenDocViewerDistResolver>();
var distPath = distResolver.ResolveDistPath();

LogProductionCompatibilityWarnings(app.Logger, startupOptions);

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync("An unexpected error occurred.");
    });
});
app.UseStatusCodePages();

app.UseResponseCompression();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Frame-Options"] = "SAMEORIGIN";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-Robots-Tag"] = "noindex";
    headers["Content-Security-Policy"] =
        startupOptions.ContentSecurityPolicy ?? DefaultContentSecurityPolicy;
    await next(context);
});

if (!string.IsNullOrWhiteSpace(distPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(distPath),
        OnPrepareResponse = context =>
        {
            if (context.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
            {
                context.Context.Response.Headers.CacheControl = "no-store";
                return;
            }

            context.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        }
    });
}

app.MapGet("/health", (
    OpenDocViewerDistResolver resolver,
    GatewaySessionStore sessions,
    WebClientHandoffGuard handoffGuard,
    IOptions<ODVGatewayOptions> options) =>
{
    var currentOptions = options.Value;
    var resolvedDistPath = resolver.ResolveDistPath();
    var exposeDistPath = currentOptions.ExposeOpenDocViewerDistPathInHealth;
    return Results.Json(new
    {
        status = string.IsNullOrWhiteSpace(resolvedDistPath) ? "degraded" : "ok",
        openDocViewerDistPath = exposeDistPath ? resolvedDistPath : null,
        openDocViewerDistAvailable = !string.IsNullOrWhiteSpace(resolvedDistPath),
        requireExplicitOpenDocViewerDistPath = currentOptions.RequireExplicitOpenDocViewerDistPath,
        activeSessions = sessions.Count,
        maxConcurrentSessions = sessions.MaxConcurrentSessions,
        sessionCapacityRejections = sessions.CapacityRejectedCount,
        useBundleUrlHandoff = currentOptions.UseBundleUrlHandoff,
        maxSourcePackFrameBytes = GetMaxSourcePackFrameBytes(currentOptions),
        maxSourceProxyBytes = GetMaxSourceProxyBytes(currentOptions),
        sourcePackStreamBufferBytes = GetSourcePackStreamBufferBytes(currentOptions),
        directSourceFiles = new
        {
            enabled = currentOptions.TrustClientFilePath,
            trustedSourceRootCount = currentOptions.TrustedSourceRoots
                .Count(root => !string.IsNullOrWhiteSpace(root))
        },
        webClientHandoff = new
        {
            allowedInitiatorCount = currentOptions.WebClientHandoff.AllowedInitiatorUrls
                .Count(url => !string.IsNullOrWhiteSpace(url)),
            currentOptions.WebClientHandoff.AllowMissingInitiatorHeaders,
            rejectedCount = handoffGuard.RejectedCount
        },
        webClientSourceFallback = new
        {
            currentOptions.WebClientSourceFallback.Enabled,
            currentOptions.WebClientSourceFallback.RequireSameHost,
            currentOptions.WebClientSourceFallback.UseFilePathUrlWhenDirectFileMissing,
            currentOptions.WebClientSourceFallback.UseWhenDirectFileMissing,
            currentOptions.WebClientSourceFallback.ProxyThroughGateway,
            currentOptions.WebClientSourceFallback.ProxyThroughGatewayAboveSourceCount,
            currentOptions.WebClientSourceFallback.ProxyMaxConcurrency
        },
        inlineSources = new
        {
            currentOptions.InlineSources.Enabled,
            currentOptions.InlineSources.MaxFileBytes,
            currentOptions.InlineSources.MaxTotalBytes,
            currentOptions.InlineSources.Extensions
        },
        remoteInlineSources = new
        {
            currentOptions.RemoteInlineSources.Enabled,
            currentOptions.RemoteInlineSources.RequireSameHost,
            currentOptions.RemoteInlineSources.MaxCount,
            currentOptions.RemoteInlineSources.MaxFileBytes,
            currentOptions.RemoteInlineSources.MaxTotalBytes,
            currentOptions.RemoteInlineSources.MaxConcurrency,
            currentOptions.RemoteInlineSources.RequestTimeoutMs,
            currentOptions.RemoteInlineSources.RetryCount,
            currentOptions.RemoteInlineSources.RetryBaseDelayMs,
            currentOptions.RemoteInlineSources.Extensions
        }
    });
});

app.MapPost("/prep", async (
    HttpRequest request,
    GatewaySessionStore sessions,
    IOptions<ODVGatewayOptions> options,
    WebClientHandoffGuard handoffGuard,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    request.HttpContext.Response.Headers.CacheControl = "no-store";
    var logger = loggerFactory.CreateLogger("ODVGateway.Prep");
    var handoffResult = handoffGuard.Validate(request);
    if (!handoffResult.IsAllowed)
    {
        return RejectInvalidHandoff(logger, "prep", handoffResult);
    }

    WebClientPrepRequest prep;
    try
    {
        prep = await WebClientPrepReader.ReadAsync(
            request,
            options.Value.MaxPrepBodyBytes,
            cancellationToken);
    }
    catch (InvalidDataException ex)
    {
        return RejectInvalidPrepPayload(logger, ex);
    }
    catch (JsonException ex)
    {
        return RejectInvalidPrepPayload(logger, ex);
    }

    var storeResult = sessions.Store(prep);
    if (!storeResult.IsStored)
    {
        logger.LogWarning(
            "Rejected /prep because the gateway session store is at capacity. ActiveSessions={ActiveSessions}, MaxConcurrentSessions={MaxConcurrentSessions}",
            sessions.Count,
            sessions.MaxConcurrentSessions);
        return Results.Json(
            new
            {
                error = "The gateway session store is at capacity. Retry after existing sessions expire or increase ODVGateway:maxConcurrentSessions.",
                maxConcurrentSessions = sessions.MaxConcurrentSessions
            },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    var session = storeResult.Session!;
    logger.LogInformation(
        "Prepared ODVGateway session with {DocumentCount} documents and {FileCount} files.",
        session.Prep.PortableDocuments.Count,
        session.SourceFiles.Count);

    return Results.Json(new
    {
        ok = true,
        sessionKey = session.SessionKey,
        documents = session.Prep.PortableDocuments.Count,
        files = session.SourceFiles.Count,
        expiresUtc = session.ExpiresUtc
    });
});

app.MapGet("/", RenderViewerAsync);
app.MapGet("/index.html", RenderViewerAsync);

app.MapGet("/bundle/{sessionKey}", async (
    HttpContext context,
    string sessionKey,
    GatewaySessionStore sessions,
    OpenDocViewerBundleFactory bundleFactory,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("ODVGateway.Bundle");
    if (!sessions.TryGet(sessionKey, out var session))
    {
        logger.LogWarning("No prepared gateway session found for bundle request.");
        return Results.NotFound(new { error = "Gateway session was not found or has expired." });
    }

    context.Response.Headers.CacheControl = "no-store";
    var bundle = await bundleFactory.CreateAsync(context.Request, session, sessionData: null, cancellationToken);
    ApplyBundleDiagnosticsHeaders(context, bundle);
    return Results.Json(bundle);
});

app.MapMethods("/source/{sessionKey}/{fileIndex:int}", ["GET", "HEAD"], async (
    HttpContext httpContext,
    string sessionKey,
    int fileIndex,
    GatewaySessionStore sessions,
    ContentTypeMapper contentTypes,
    IOptions<ODVGatewayOptions> options,
    DirectSourceFileResolver directSources,
    IHttpClientFactory httpClientFactory,
    WebClientSourceProxyLimiter sourceProxyLimiter,
    WebClientFallbackUrlBuilder fallbackUrlBuilder,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("ODVGateway.Source");
    if (!sessions.TryGet(sessionKey, out var session))
    {
        return Results.NotFound(new { error = "Gateway session was not found or has expired." });
    }

    if (fileIndex < 0 || fileIndex >= session.SourceFiles.Count)
    {
        return Results.NotFound(new { error = "Source file index is outside the prepared session." });
    }

    var source = session.SourceFiles[fileIndex];
    if (directSources.TryResolve(source, out var directSource))
    {
        var cacheControl = options.Value.SourceCacheControl;
        if (!string.IsNullOrWhiteSpace(cacheControl))
        {
            httpContext.Response.Headers.CacheControl = cacheControl;
        }

        return Results.File(
            directSources.OpenRead(directSource),
            contentTypes.GetContentType(source.Extension),
            source.DisplayName,
            enableRangeProcessing: true);
    }

    var proxyUrl = fallbackUrlBuilder.BuildFallbackUrl(
        httpContext.Request,
        session.SessionKey,
        source,
        options.Value.WebClientSourceFallback).Url;
    if (proxyUrl is not null && ShouldProxyWebClientFallback(options.Value, session.SourceFiles.Count))
    {
        using var sourceProxyLease = await sourceProxyLimiter.WaitAsync(cancellationToken);
        return await ProxyWebClientSourceAsync(
            httpContext,
            session,
            source,
            proxyUrl,
            contentTypes,
            options.Value,
            httpClientFactory,
            logger,
            cancellationToken);
    }

    if (!options.Value.TrustClientFilePath)
    {
        logger.LogWarning("Direct file path access is disabled for the requested gateway session.");
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    logger.LogWarning(
        "Prepared source file was not found. Index={FileIndex}, HasClientFilePath={HasClientFilePath}",
        fileIndex,
        !string.IsNullOrWhiteSpace(source.FilePath));
    return Results.NotFound(new
    {
        error = "Prepared source file was not found.",
        fileIndex,
        displayName = source.DisplayName
    });
});

app.MapGet("/source-pack/{sessionKey}", async (
    HttpContext httpContext,
    string sessionKey,
    GatewaySessionStore sessions,
    ContentTypeMapper contentTypes,
    IOptions<ODVGatewayOptions> options,
    DirectSourceFileResolver directSources,
    IHttpClientFactory httpClientFactory,
    WebClientFallbackUrlBuilder fallbackUrlBuilder,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("ODVGateway.SourcePack");
    if (!sessions.TryGet(sessionKey, out var session))
    {
        return Results.NotFound(new { error = "Gateway session was not found or has expired." });
    }

    var response = httpContext.Response;
    response.Headers.CacheControl = "no-store";
    response.Headers["X-ODVGateway-Source-Pack"] = "odvsp1";
    response.Headers["X-ODVGateway-Source-Count"] =
        session.SourceFiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    response.ContentType = "application/vnd.opendocviewer.source-pack";

    await WriteSourcePackAsync(
        httpContext.Request,
        response,
        session,
        contentTypes,
        options.Value,
        directSources,
        httpClientFactory,
        fallbackUrlBuilder,
        logger,
        cancellationToken);

    return Results.Empty;
});

app.Run();

static async Task<IResult> RenderViewerAsync(
    HttpContext context,
    GatewaySessionStore sessions,
    OpenDocViewerBundleFactory bundleFactory,
    OpenDocViewerIndexRenderer renderer,
    IOptions<ODVGatewayOptions> options,
    WebClientHandoffGuard handoffGuard,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken)
{
    var logger = loggerFactory.CreateLogger("ODVGateway.Viewer");
    if (HasBundleUrlQuery(context.Request))
    {
        var handoffResult = handoffGuard.Validate(context.Request);
        if (!handoffResult.IsAllowed)
        {
            return RejectInvalidHandoff(logger, "viewer-bundle-query", handoffResult);
        }

        return await renderer.RenderAsync(context, bundle: null, cancellationToken);
    }

    var sessionDataToken = context.Request.Query["sessiondata"].ToString();
    if (!string.IsNullOrWhiteSpace(sessionDataToken))
    {
        var handoffResult = handoffGuard.Validate(context.Request);
        if (!handoffResult.IsAllowed)
        {
            return RejectInvalidHandoff(logger, "viewer-sessiondata", handoffResult);
        }
    }

    if (string.IsNullOrWhiteSpace(sessionDataToken))
    {
        if (options.Value.AllowOpenDocViewerFallbackWithoutSession)
        {
            return await renderer.RenderAsync(context, bundle: null, cancellationToken);
        }

        return GatewayHtml.StatusPage(
            "ODVGateway",
            "The gateway is running, but no WebClient sessiondata query parameter was supplied.");
    }

    WebClientSessionData sessionData;
    try
    {
        sessionData = WebClientSessionDataDecoder.Decode(sessionDataToken);
    }
    catch (InvalidDataException ex)
    {
        return RejectInvalidSessionData(logger, ex);
    }
    catch (JsonException ex)
    {
        return RejectInvalidSessionData(logger, ex);
    }
    catch (FormatException ex)
    {
        return RejectInvalidSessionData(logger, ex);
    }
    catch (ArgumentException ex)
    {
        return RejectInvalidSessionData(logger, ex);
    }

    var handoffLookupKey = SessionKeyFactory.CreateHandoffLookupKey(sessionData);
    if (!sessions.TryGetByHandoffLookupKey(handoffLookupKey, out var session))
    {
        logger.LogWarning("No prepared gateway session found for decoded WebClient sessiondata.");
        return GatewayHtml.StatusPage(
            "Prepared ODVGateway session was not found",
            "The viewer was opened without a matching /prep call, or the in-memory gateway session has expired. Open the document from WebClient again.");
    }

    if (options.Value.UseBundleUrlHandoff)
    {
        return Results.Redirect(BuildViewerBundleUrl(context.Request, session));
    }

    var bundle = await bundleFactory.CreateAsync(context.Request, session, sessionData, cancellationToken);
    return await renderer.RenderAsync(context, bundle, cancellationToken);
}

static bool HasBundleUrlQuery(HttpRequest request)
{
    return request.Query.ContainsKey("bundleurl")
        || request.Query.ContainsKey("bundleUrl")
        || request.Query.ContainsKey("sessionurl")
        || request.Query.ContainsKey("sessionUrl");
}

static IResult RejectInvalidPrepPayload(ILogger logger, Exception ex)
{
    logger.LogWarning(ex, "Rejected invalid WebClient prep payload.");
    return Results.BadRequest(new { error = "Invalid WebClient prep payload." });
}

static IResult RejectInvalidSessionData(ILogger logger, Exception ex)
{
    logger.LogWarning(ex, "Could not decode WebClient sessiondata.");
    return Results.BadRequest("The WebClient sessiondata query parameter could not be decoded.");
}

static IResult RejectInvalidHandoff(ILogger logger, string operation, HandoffGuardResult result)
{
    logger.LogWarning(
        "Rejected ODVGateway handoff. Operation={Operation}, Reason={Reason}, Initiator={Initiator}",
        operation,
        result.Reason,
        result.Initiator);
    return Results.StatusCode(StatusCodes.Status403Forbidden);
}

static string BuildViewerBundleUrl(HttpRequest request, GatewaySession session)
{
    var basePath = request.PathBase.HasValue ? request.PathBase.Value : string.Empty;
    var appRoot = string.IsNullOrWhiteSpace(basePath) ? "/" : $"{basePath}/";
    var bundlePath = $"{basePath}/bundle/{Uri.EscapeDataString(session.SessionKey)}";
    var absoluteBundleUrl = $"{request.Scheme}://{request.Host}{bundlePath}";
    var query = new Dictionary<string, string?>
    {
        ["bundleUrl"] = absoluteBundleUrl,
        ["odvDocs"] = session.Prep.PortableDocuments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["odvFiles"] = session.SourceFiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    return $"{appRoot}?{string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? string.Empty)}"))}";
}

static void ApplyBundleDiagnosticsHeaders(HttpContext context, OpenDocViewerBundle bundle)
{
    var integration = bundle.Integration;
    context.Response.Headers["X-ODVGateway-Transport"] =
        Convert.ToString(integration.GetValueOrDefault("transport")) ?? string.Empty;
    context.Response.Headers["X-ODVGateway-Inline-Source-Count"] =
        Convert.ToString(integration.GetValueOrDefault("inlineSourceCount")) ?? "0";
    context.Response.Headers["X-ODVGateway-Inline-Source-Bytes"] =
        Convert.ToString(integration.GetValueOrDefault("inlineSourceBytes")) ?? "0";
    context.Response.Headers["X-ODVGateway-Remote-Inline-Source-Count"] =
        Convert.ToString(integration.GetValueOrDefault("remoteInlineSourceCount")) ?? "0";
    context.Response.Headers["X-ODVGateway-Remote-Inline-Source-Bytes"] =
        Convert.ToString(integration.GetValueOrDefault("remoteInlineSourceBytes")) ?? "0";
    context.Response.Headers["X-ODVGateway-Remote-Inline-Failure-Count"] =
        Convert.ToString(integration.GetValueOrDefault("remoteInlineFailureCount")) ?? "0";
    context.Response.Headers["X-ODVGateway-Remote-Inline-Max-Concurrency"] =
        Convert.ToString(integration.GetValueOrDefault("remoteInlineMaxConcurrency")) ?? "0";
    context.Response.Headers["X-ODVGateway-Remote-Inline-Max-Count"] =
        Convert.ToString(integration.GetValueOrDefault("remoteInlineMaxCount")) ?? "0";
    context.Response.Headers["X-ODVGateway-Remote-Inline-Max-Total-Bytes"] =
        Convert.ToString(integration.GetValueOrDefault("remoteInlineMaxTotalBytes")) ?? "0";
    context.Response.Headers["X-ODVGateway-Host-Gateway-Proxy-Source-Count"] =
        Convert.ToString(integration.GetValueOrDefault("hostGatewayProxySourceCount")) ?? "0";
    context.Response.Headers["X-ODVGateway-Host-Gateway-Proxy-Threshold"] =
        Convert.ToString(integration.GetValueOrDefault("hostGatewayProxyThreshold")) ?? "0";
    context.Response.Headers["X-ODVGateway-Host-Gateway-Proxy-Max-Concurrency"] =
        Convert.ToString(integration.GetValueOrDefault("hostGatewayProxyMaxConcurrency")) ?? "0";
    context.Response.Headers["X-ODVGateway-Bundle-Build-Ms"] =
        Convert.ToString(integration.GetValueOrDefault("gatewayBundleBuildMs")) ?? "0";
    context.Response.Headers["X-ODVGateway-Direct-Readable-Source-Count"] =
        Convert.ToString(integration.GetValueOrDefault("directReadableSourceCount")) ?? "0";
    context.Response.Headers["X-ODVGateway-Direct-Missing-Source-Count"] =
        Convert.ToString(integration.GetValueOrDefault("directMissingSourceCount")) ?? "0";
    context.Response.Headers["X-ODVGateway-Gateway-Source-Url-Count"] =
        Convert.ToString(integration.GetValueOrDefault("gatewaySourceUrlCount")) ?? "0";
    context.Response.Headers["X-ODVGateway-Host-Fallback-Source-Count"] =
        Convert.ToString(integration.GetValueOrDefault("hostFallbackSourceCount")) ?? "0";
    context.Response.Headers["X-ODVGateway-Host-FilePath-Url-Source-Count"] =
        Convert.ToString(integration.GetValueOrDefault("hostFilePathUrlSourceCount")) ?? "0";
    context.Response.Headers["X-ODVGateway-Host-Template-Url-Source-Count"] =
        Convert.ToString(integration.GetValueOrDefault("hostTemplateUrlSourceCount")) ?? "0";
    context.Response.Headers["X-ODVGateway-Page-Count-Hint-Count"] =
        Convert.ToString(integration.GetValueOrDefault("sourcePageCountHintCount")) ?? "0";
    context.Response.Headers["X-ODVGateway-Page-Count-Hint-Total"] =
        Convert.ToString(integration.GetValueOrDefault("sourcePageCountHintTotal")) ?? "0";
    context.Response.Headers["X-ODVGateway-Page-Count-Hint-Missing-Count"] =
        Convert.ToString(integration.GetValueOrDefault("sourcePageCountHintMissingCount")) ?? "0";
}

static async Task WriteSourcePackAsync(
    HttpRequest request,
    HttpResponse response,
    GatewaySession session,
    ContentTypeMapper contentTypes,
    ODVGatewayOptions options,
    DirectSourceFileResolver directSources,
    IHttpClientFactory httpClientFactory,
    WebClientFallbackUrlBuilder fallbackUrlBuilder,
    ILogger logger,
    CancellationToken cancellationToken)
{
    await response.Body.WriteAsync(Encoding.ASCII.GetBytes("ODVSP1\n"), cancellationToken);

    foreach (var source in session.SourceFiles.OrderBy(file => file.Index))
    {
        cancellationToken.ThrowIfCancellationRequested();

        SourcePackPayload payload;
        try
        {
            payload = await ReadGatewaySourceBytesAsync(
                request,
                session,
                source,
                contentTypes,
                options,
                directSources,
                httpClientFactory,
                fallbackUrlBuilder,
                logger,
                cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            payload = BuildFailedSourcePackPayload(logger, session, source, contentTypes, ex);
        }
        catch (IOException ex)
        {
            payload = BuildFailedSourcePackPayload(logger, session, source, contentTypes, ex);
        }
        catch (InvalidOperationException ex)
        {
            payload = BuildFailedSourcePackPayload(logger, session, source, contentTypes, ex);
        }
        catch (ArgumentException ex)
        {
            payload = BuildFailedSourcePackPayload(logger, session, source, contentTypes, ex);
        }
        catch (NotSupportedException ex)
        {
            payload = BuildFailedSourcePackPayload(logger, session, source, contentTypes, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            payload = BuildFailedSourcePackPayload(logger, session, source, contentTypes, ex);
        }

        await WriteSourcePackFrameAsync(response, source, payload, options, cancellationToken);
    }

    await response.Body.WriteAsync(new byte[4], cancellationToken);
    await response.Body.FlushAsync(cancellationToken);
}

static async Task<SourcePackPayload> ReadGatewaySourceBytesAsync(
    HttpRequest request,
    GatewaySession session,
    GatewaySourceFile source,
    ContentTypeMapper contentTypes,
    ODVGatewayOptions options,
    DirectSourceFileResolver directSources,
    IHttpClientFactory httpClientFactory,
    WebClientFallbackUrlBuilder fallbackUrlBuilder,
    ILogger logger,
    CancellationToken cancellationToken)
{
    if (directSources.TryResolve(source, out var directSource))
    {
        var maxFrameBytes = GetMaxSourcePackFrameBytes(options);
        if (directSource.Length > maxFrameBytes)
        {
            return new SourcePackPayload(
                Ok: false,
                Bytes: [],
                ContentType: contentTypes.GetContentType(source.Extension),
                Error: FormatSourcePackLimitExceededMessage(directSource.Length, maxFrameBytes));
        }

        return new SourcePackPayload(
            Ok: true,
            Bytes: [],
            ContentType: contentTypes.GetContentType(source.Extension),
            Error: null,
            ContentStream: directSources.OpenRead(directSource),
            ContentLength: directSource.Length);
    }

    var proxyUrl = fallbackUrlBuilder.BuildFallbackUrl(
        request,
        session.SessionKey,
        source,
        options.WebClientSourceFallback).Url;
    if (proxyUrl is null)
    {
        return new SourcePackPayload(
            Ok: false,
            Bytes: [],
            ContentType: contentTypes.GetContentType(source.Extension),
            Error: "No gateway source path or WebClient fallback URL was available.");
    }

    return await FetchWebClientSourceBytesAsync(
        session,
        source,
        proxyUrl,
        contentTypes,
        options,
        httpClientFactory,
        logger,
        cancellationToken);
}

static SourcePackPayload BuildFailedSourcePackPayload(
    ILogger logger,
    GatewaySession session,
    GatewaySourceFile source,
    ContentTypeMapper contentTypes,
    Exception ex)
{
    logger.LogWarning(
        ex,
        "Could not write source pack frame. Index={FileIndex}, ExceptionType={ExceptionType}, ExceptionMessage={ExceptionMessage}",
        source.Index,
        ex.GetType().Name,
        ex.Message);
    return new SourcePackPayload(
        Ok: false,
        Bytes: [],
        ContentType: contentTypes.GetContentType(source.Extension),
        Error: "The gateway could not read the source file.");
}

static async Task<SourcePackPayload> FetchWebClientSourceBytesAsync(
    GatewaySession session,
    GatewaySourceFile source,
    string sourceUrl,
    ContentTypeMapper contentTypes,
    ODVGatewayOptions options,
    IHttpClientFactory httpClientFactory,
    ILogger logger,
    CancellationToken cancellationToken)
{
    var remoteOptions = options.RemoteInlineSources;
    var maxFrameBytes = GetMaxSourcePackFrameBytes(options);
    var maxAttempts = Math.Max(1, remoteOptions.RetryCount + 1);
    var client = httpClientFactory.CreateClient("ODVGateway.RemoteInline");

    for (var attempt = 1; attempt <= maxAttempts; attempt += 1)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(250, remoteOptions.RequestTimeoutMs)));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
            request.Headers.TryAddWithoutValidation("Accept", "*/*");

            var cookieHeader = BuildWebClientCookieHeader(
                session.Prep.AspxAuth,
                remoteOptions.AspxAuthCookieNames,
                session.Prep.SessionId,
                remoteOptions.SessionCookieNames);
            if (!string.IsNullOrWhiteSpace(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                logger.LogWarning(
                    "WebClient source pack fetch failed. Index={FileIndex}, Attempt={Attempt}, Status={StatusCode}, Source={SourceEndpoint}",
                    source.Index,
                    attempt,
                    statusCode,
                    GatewayLogNames.ConfiguredWebClientSource);

                if (IsRetryableProxyStatusCode(statusCode) && attempt < maxAttempts)
                {
                    await DelayProxyRetryAsync(remoteOptions, attempt, cancellationToken);
                    continue;
                }

                return new SourcePackPayload(
                    Ok: false,
                    Bytes: [],
                    ContentType: contentTypes.GetContentType(source.Extension),
                    Error: $"WebClient returned HTTP {statusCode}.");
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 0 && contentLength.Value > maxFrameBytes)
            {
                return new SourcePackPayload(
                    Ok: false,
                    Bytes: [],
                    ContentType: contentTypes.GetContentType(source.Extension),
                    Error: FormatSourcePackLimitExceededMessage(contentLength.Value, maxFrameBytes));
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = await ReadSourcePackBytesWithLimitAsync(
                contentStream,
                maxFrameBytes,
                contentLength,
                GetSourcePackStreamBufferBytes(options),
                timeout.Token);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(contentType))
            {
                contentType = contentTypes.GetContentType(source.Extension);
            }

            return new SourcePackPayload(
                Ok: bytes.Length > 0,
                Bytes: bytes,
                ContentType: contentType,
                Error: bytes.Length > 0 ? null : "WebClient returned an empty response.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "WebClient source pack fetch timed out. Index={FileIndex}, Attempt={Attempt}, Source={SourceEndpoint}",
                source.Index,
                attempt,
                GatewayLogNames.ConfiguredWebClientSource);
        }
        catch (HttpRequestException ex)
        {
            LogWebClientSourcePackFetchFailed(logger, session, source, attempt, ex);
        }
        catch (IOException ex)
        {
            LogWebClientSourcePackFetchFailed(logger, session, source, attempt, ex);
        }
        catch (SourcePackPayloadTooLargeException ex)
        {
            logger.LogWarning(
                ex,
                "Source pack payload exceeded configured limit. Index={FileIndex}",
                source.Index);
            return new SourcePackPayload(
                Ok: false,
                Bytes: [],
                ContentType: contentTypes.GetContentType(source.Extension),
                Error: "Source file is too large for the source-pack transport.");
        }
        catch (InvalidOperationException ex)
        {
            LogWebClientSourcePackFetchFailed(logger, session, source, attempt, ex);
        }

        if (attempt < maxAttempts)
        {
            await DelayProxyRetryAsync(remoteOptions, attempt, cancellationToken);
        }
    }

    return new SourcePackPayload(
        Ok: false,
        Bytes: [],
        ContentType: contentTypes.GetContentType(source.Extension),
        Error: "WebClient source pack fetch exhausted retries.");
}

static void LogWebClientSourcePackFetchFailed(
    ILogger logger,
    GatewaySession session,
    GatewaySourceFile source,
    int attempt,
    Exception ex)
{
    logger.LogWarning(
        ex,
        "WebClient source pack fetch failed. Index={FileIndex}, Attempt={Attempt}, Source={SourceEndpoint}",
        source.Index,
        attempt,
        GatewayLogNames.ConfiguredWebClientSource);
}

static long GetMaxSourcePackFrameBytes(ODVGatewayOptions options)
{
    return Math.Clamp(options.MaxSourcePackFrameBytes, 1L, int.MaxValue);
}

static int ClampFormValueLengthLimitBytes(long maxPrepBodyBytes)
{
    // FormOptions.ValueLengthLimit is int-backed, while the gateway runtime option is long so
    // larger transport limits can still be represented by multipart/body-specific settings.
    return (int)Math.Clamp(maxPrepBodyBytes, 1L, int.MaxValue);
}

static long GetMaxSourceProxyBytes(ODVGatewayOptions options)
{
    return options.MaxSourceProxyBytes > 0
        ? Math.Clamp(options.MaxSourceProxyBytes, 1L, int.MaxValue)
        : GetMaxSourcePackFrameBytes(options);
}

static int GetSourcePackStreamBufferBytes(ODVGatewayOptions options)
{
    return Math.Clamp(options.SourcePackStreamBufferBytes, 4096, 1024 * 1024);
}

static async Task<byte[]> ReadSourcePackBytesWithLimitAsync(
    Stream source,
    long maxBytes,
    long? expectedBytes,
    int bufferSize,
    CancellationToken cancellationToken)
{
    if (expectedBytes is > 0 && expectedBytes.Value > maxBytes)
    {
        throw new SourcePackPayloadTooLargeException(
            FormatSourcePackLimitExceededMessage(expectedBytes.Value, maxBytes));
    }

    var initialCapacity = expectedBytes is > 0 and <= int.MaxValue
        ? (int)expectedBytes.Value
        : 0;
    using var output = initialCapacity > 0
        ? new MemoryStream(initialCapacity)
        : new MemoryStream();
    var buffer = new byte[bufferSize];
    var totalBytes = 0L;

    while (true)
    {
        var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        if (bytesRead <= 0)
        {
            return output.ToArray();
        }

        totalBytes += bytesRead;
        if (totalBytes > maxBytes)
        {
            throw new SourcePackPayloadTooLargeException(
                FormatSourcePackLimitExceededMessage(totalBytes, maxBytes));
        }

        output.Write(buffer.AsSpan(0, bytesRead));
    }
}

static string FormatSourcePackLimitExceededMessage(long actualBytes, long maxBytes)
{
    return string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"Source file is too large for the source-pack transport. Actual bytes: {actualBytes}. Maximum bytes: {maxBytes}.");
}

static string FormatSourceProxyLimitExceededMessage(long actualBytes, long maxBytes)
{
    return string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"Source file is too large for the gateway source proxy transport. Actual bytes: {actualBytes}. Maximum bytes: {maxBytes}.");
}

static async Task WriteSourcePackFrameAsync(
    HttpResponse response,
    GatewaySourceFile source,
    SourcePackPayload payload,
    ODVGatewayOptions options,
    CancellationToken cancellationToken)
{
    var payloadBytes = payload.ContentStream is not null
        ? payload.ContentLength
        : payload.Bytes.LongLength;
    var header = new
    {
        ok = payload.Ok,
        fileIndex = source.Index,
        fileId = source.FileId,
        ext = source.Extension,
        displayName = source.DisplayName,
        contentType = payload.ContentType,
        payloadBytes,
        error = payload.Error
    };
    var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header);
    var headerLength = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(headerLength, checked((uint)headerBytes.Length));

    await response.Body.WriteAsync(headerLength, cancellationToken);
    await response.Body.WriteAsync(headerBytes, cancellationToken);
    if (payload.ContentStream is not null)
    {
        try
        {
            await CopySourcePackStreamAsync(
                payload.ContentStream,
                response.Body,
                payload.ContentLength,
                GetSourcePackStreamBufferBytes(options),
                cancellationToken);
        }
        finally
        {
            await payload.ContentStream.DisposeAsync();
        }
    }
    else if (payload.Bytes.Length > 0)
    {
        await response.Body.WriteAsync(payload.Bytes, cancellationToken);
    }

    await response.Body.FlushAsync(cancellationToken);
}

static async Task CopySourcePackStreamAsync(
    Stream source,
    Stream destination,
    long contentLength,
    int bufferSize,
    CancellationToken cancellationToken)
{
    var buffer = new byte[bufferSize];
    var remainingBytes = Math.Max(0, contentLength);
    while (remainingBytes > 0)
    {
        var bytesToRead = (int)Math.Min(buffer.Length, remainingBytes);
        var bytesRead = await source.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);
        if (bytesRead <= 0)
        {
            return;
        }

        await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        remainingBytes -= bytesRead;
    }
}

static bool ShouldProxyWebClientFallback(ODVGatewayOptions options, int sourceCount)
{
    var fallback = options.WebClientSourceFallback;
    if (!fallback.Enabled || !fallback.ProxyThroughGateway) return false;

    var threshold = Math.Max(0, fallback.ProxyThroughGatewayAboveSourceCount);
    return threshold <= 0 || sourceCount >= threshold;
}

static async Task<IResult> ProxyWebClientSourceAsync(
    HttpContext httpContext,
    GatewaySession session,
    GatewaySourceFile source,
    string sourceUrl,
    ContentTypeMapper contentTypes,
    ODVGatewayOptions options,
    IHttpClientFactory httpClientFactory,
    ILogger logger,
    CancellationToken cancellationToken)
{
    var remoteOptions = options.RemoteInlineSources;
    var maxAttempts = Math.Max(1, remoteOptions.RetryCount + 1);
    var client = httpClientFactory.CreateClient("ODVGateway.RemoteInline");
    var maxProxyBytes = GetMaxSourceProxyBytes(options);

    for (var attempt = 1; attempt <= maxAttempts; attempt += 1)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(250, remoteOptions.RequestTimeoutMs)));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
            request.Headers.TryAddWithoutValidation("Accept", "*/*");

            var cookieHeader = BuildWebClientCookieHeader(
                session.Prep.AspxAuth,
                remoteOptions.AspxAuthCookieNames,
                session.Prep.SessionId,
                remoteOptions.SessionCookieNames);
            if (!string.IsNullOrWhiteSpace(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                logger.LogWarning(
                    "WebClient source proxy failed. Index={FileIndex}, Attempt={Attempt}, Status={StatusCode}, Source={SourceEndpoint}",
                    source.Index,
                    attempt,
                    statusCode,
                    GatewayLogNames.ConfiguredWebClientSource);

                if (IsRetryableProxyStatusCode(statusCode) && attempt < maxAttempts)
                {
                    await DelayProxyRetryAsync(remoteOptions, attempt, cancellationToken);
                    continue;
                }

                return Results.StatusCode(statusCode);
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 0 && contentLength.Value > maxProxyBytes)
            {
                logger.LogWarning(
                    "WebClient source proxy response exceeded configured limit. Index={FileIndex}, Attempt={Attempt}, ActualBytes={ActualBytes}, MaxBytes={MaxBytes}, Source={SourceEndpoint}",
                    source.Index,
                    attempt,
                    contentLength.Value,
                    maxProxyBytes,
                    GatewayLogNames.ConfiguredWebClientSource);
                return Results.Json(new
                {
                    error = FormatSourceProxyLimitExceededMessage(contentLength.Value, maxProxyBytes),
                    fileIndex = source.Index,
                    displayName = source.DisplayName
                }, statusCode: StatusCodes.Status502BadGateway);
            }

            var cacheControl = options.SourceCacheControl;
            if (!string.IsNullOrWhiteSpace(cacheControl))
            {
                httpContext.Response.Headers.CacheControl = cacheControl;
            }

            httpContext.Response.Headers["X-ODVGateway-Source-Proxy"] = "webclient";
            httpContext.Response.Headers["X-ODVGateway-Source-Index"] =
                source.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(contentType))
            {
                contentType = contentTypes.GetContentType(source.Extension);
            }

            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = contentType;
            if (contentLength is > 0)
            {
                httpContext.Response.ContentLength = contentLength.Value;
            }

            if (HttpMethods.IsHead(httpContext.Request.Method))
            {
                return Results.Empty;
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(timeout.Token);
            await CopyBoundedStreamAsync(
                contentStream,
                httpContext.Response.Body,
                maxProxyBytes,
                GetSourcePackStreamBufferBytes(options),
                timeout.Token);

            return Results.Empty;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "WebClient source proxy timed out. Index={FileIndex}, Attempt={Attempt}, Source={SourceEndpoint}",
                source.Index,
                attempt,
                GatewayLogNames.ConfiguredWebClientSource);
        }
        catch (HttpRequestException ex)
        {
            LogWebClientSourceProxyFailed(logger, session, source, attempt, ex);
        }
        catch (IOException ex)
        {
            LogWebClientSourceProxyFailed(logger, session, source, attempt, ex);
        }
        catch (SourcePackPayloadTooLargeException ex)
        {
            logger.LogWarning(
                ex,
                "WebClient source proxy response exceeded configured limit. Index={FileIndex}, Attempt={Attempt}, MaxBytes={MaxBytes}, Source={SourceEndpoint}",
                source.Index,
                attempt,
                maxProxyBytes,
                GatewayLogNames.ConfiguredWebClientSource);
            return Results.Json(new
            {
                error = "Source file is too large for the gateway source proxy transport.",
                fileIndex = source.Index,
                displayName = source.DisplayName
            }, statusCode: StatusCodes.Status502BadGateway);
        }
        catch (InvalidOperationException ex)
        {
            LogWebClientSourceProxyFailed(logger, session, source, attempt, ex);
        }

        if (attempt < maxAttempts)
        {
            await DelayProxyRetryAsync(remoteOptions, attempt, cancellationToken);
        }
    }

    return Results.NotFound(new
    {
        error = "WebClient source proxy could not resolve the source file.",
        fileIndex = source.Index,
        displayName = source.DisplayName
    });
}

static async Task CopyBoundedStreamAsync(
    Stream source,
    Stream destination,
    long maxBytes,
    int bufferSize,
    CancellationToken cancellationToken)
{
    var buffer = new byte[bufferSize];
    var totalBytes = 0L;

    while (true)
    {
        var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        if (bytesRead <= 0)
        {
            return;
        }

        totalBytes += bytesRead;
        if (totalBytes > maxBytes)
        {
            throw new SourcePackPayloadTooLargeException(
                FormatSourceProxyLimitExceededMessage(totalBytes, maxBytes));
        }

        await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
    }
}

static void LogWebClientSourceProxyFailed(
    ILogger logger,
    GatewaySession session,
    GatewaySourceFile source,
    int attempt,
    Exception ex)
{
    logger.LogWarning(
        ex,
        "WebClient source proxy failed. Index={FileIndex}, Attempt={Attempt}, Source={SourceEndpoint}",
        source.Index,
        attempt,
        GatewayLogNames.ConfiguredWebClientSource);
}

static void ValidateTrustedSourceRootConfiguration(
    ODVGatewayOptions options,
    string? contentRootPath = null)
{
    if (!options.TrustClientFilePath)
    {
        return;
    }

    var invalidRoots = DirectSourceFileResolver.GetInvalidTrustedRoots(
        options.TrustedSourceRoots,
        contentRootPath);
    if (invalidRoots.Count == 0)
    {
        var trustedRoots = DirectSourceFileResolver.NormalizeTrustedRoots(
            options.TrustedSourceRoots,
            contentRootPath);
        if (trustedRoots.Count > 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "ODVGateway: TrustedSourceRoots must contain at least one absolute local or UNC path when TrustClientFilePath is enabled.");
    }

    throw new InvalidOperationException(
        "ODVGateway: TrustedSourceRoots must be absolute local or UNC paths when TrustClientFilePath is enabled. " +
        $"Invalid entry count: {invalidRoots.Count}.");
}

static Task DelayProxyRetryAsync(RemoteInlineSourceOptions options, int attempt, CancellationToken cancellationToken)
{
    var delayMs = Math.Max(0, options.RetryBaseDelayMs) * Math.Max(1, attempt);
    return delayMs > 0
        ? Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken)
        : Task.CompletedTask;
}

static bool IsRetryableProxyStatusCode(int statusCode)
{
    return statusCode == StatusCodes.Status408RequestTimeout ||
        statusCode == StatusCodes.Status429TooManyRequests ||
        statusCode >= 500;
}

static string? BuildWebClientCookieHeader(
    string? aspxAuth,
    IEnumerable<string>? aspxAuthCookieNames,
    string? sessionId,
    IEnumerable<string>? sessionCookieNames)
{
    var parts = BuildCookieParts(aspxAuthCookieNames, aspxAuth)
        .Concat(BuildCookieParts(sessionCookieNames, sessionId))
        .ToArray();

    return parts.Length > 0 ? string.Join("; ", parts) : null;
}

static IEnumerable<string> BuildCookieParts(IEnumerable<string>? cookieNames, string? rawValue)
{
    var value = rawValue?.Trim();
    if (string.IsNullOrWhiteSpace(value)) return [];

    return (cookieNames ?? [])
        .Select(name => name?.Trim())
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Where(IsValidCookieName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(name => $"{name}={Uri.EscapeDataString(value)}");
}

static bool IsValidCookieName(string? name)
{
    if (string.IsNullOrWhiteSpace(name)) return false;

    foreach (var character in name)
    {
        if (character <= 0x20 || character >= 0x7f)
        {
            return false;
        }

        if ("()<>@,;:\\\"/[]?={}".Contains(character, StringComparison.Ordinal))
        {
            return false;
        }
    }

    return true;
}

static void LogProductionCompatibilityWarnings(ILogger logger, ODVGatewayOptions options)
{
    if (options.WebClientHandoff.AllowMissingInitiatorHeaders)
    {
        logger.LogWarning(
            "ODVGateway is configured with webClientHandoff.allowMissingInitiatorHeaders enabled. " +
            "This is a development/compatibility setting and should be disabled in production.");
    }

    if (options.WebClientHandoff.AllowedInitiatorUrls.Length == 0 ||
        options.WebClientHandoff.AllowedInitiatorUrls.All(string.IsNullOrWhiteSpace))
    {
        logger.LogWarning(
            "ODVGateway is configured with an empty webClientHandoff.allowedInitiatorUrls list. " +
            "This is a development/compatibility setting and should be locked down in production.");
    }
}

internal static class GatewayLogNames
{
    public const string ConfiguredWebClientSource = "configured-webclient-source";
}

internal sealed class SourcePackPayloadTooLargeException : InvalidOperationException
{
    public SourcePackPayloadTooLargeException(string message)
        : base(message)
    {
    }
}

internal sealed record SourcePackPayload(
    bool Ok,
    byte[] Bytes,
    string ContentType,
    string? Error,
    Stream? ContentStream = null,
    long ContentLength = 0);
