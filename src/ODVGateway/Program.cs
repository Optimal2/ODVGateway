using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using NLog.Web;
using ODVGateway.Models;
using ODVGateway.Options;
using ODVGateway.Services;
using ODVGateway.Services.Signatures;

// Default CSP is restrictive but allows the inline bootstrap script injected by
// OpenDocViewerIndexRenderer, the inline styles used by GatewayHtml.StatusPage,
// and the same-origin OpenDocViewer dist files. Deployments can override the
// entire policy via ODVGateway:contentSecurityPolicy in appsettings.json.
//
// blob: is required in frame-src and connect-src by the viewer's PDF print
// path. frame-src: printPdfBlob loads the generated PDF blob in a hidden
// iframe and calls contentWindow.print(); without blob: the frame is blocked,
// the probe throws cross-origin, and the print flow dies silently
// ("Förbereder utskrift 100 %", then nothing) — reproduced and verified
// against a simulated WebClient handoff 2026-09-15. frame-src must be
// explicit: when absent it falls back to default-src, which blocks blob:.
// connect-src: the viewer's external PDF worker fetches the page images'
// blob: URLs, and a dedicated worker obeys the CSP served on its own script
// response — which this middleware stamps on every response. Without blob:
// the worker path degrades silently to the main thread (a logged warning,
// slower prints). The generated PDF blob itself is never fetched.
// Isolated signature-validation worker entry mode: the same assembly validates one document
// from stdin and answers one JSON envelope on stdout, then exits. This runs before anything
// else so the worker never builds the web host, never opens a socket, and never touches logging.
if (SignatureWorkerMain.ShouldRun(args))
{
    return await SignatureWorkerMain.RunAsync(args);
}

const string DefaultContentSecurityPolicy =
    "default-src 'self'; " +
    "script-src 'self' 'unsafe-inline'; " +
    "style-src 'self' 'unsafe-inline'; " +
    "img-src 'self' data: blob:; " +
    "connect-src 'self' blob:; " +
    "font-src 'self'; " +
    "media-src 'self'; " +
    "object-src 'self'; " +
    "worker-src 'self' blob:; " +
    "frame-src 'self' blob:; " +
    "frame-ancestors 'self'; " +
    "base-uri 'self'; " +
    "form-action 'self'";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Host.UseNLog();

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
builder.Services.AddSingleton<SignatureValidationLimiter>();
builder.Services.AddSingleton(provider => new SignatureWorkerClient(
    provider.GetRequiredService<ILoggerFactory>().CreateLogger(GatewayLogNames.SignatureValidation),
    provider.GetRequiredService<IHostEnvironment>().ContentRootPath));
builder.Services.AddSingleton<WebClientFallbackUrlBuilder>();
builder.Services.AddHttpClient("ODVGateway.RemoteInline");

// Signature validation (GET /signatures/...). The anchor and CRL stores read their configured
// directories once per process and cache the result, so they are singletons by design: the
// validation service holds no per-session state.
builder.Services.AddSingleton(provider => new TrustAnchorStore(
    provider.GetRequiredService<IOptions<ODVGatewayOptions>>().Value.Signatures,
    provider.GetRequiredService<ILoggerFactory>().CreateLogger(GatewayLogNames.SignatureValidation),
    provider.GetRequiredService<IHostEnvironment>().ContentRootPath));
builder.Services.AddSingleton(provider => new OfflineRevocationStore(
    provider.GetRequiredService<IOptions<ODVGatewayOptions>>().Value.Signatures.CrlDirectory,
    provider.GetRequiredService<ILoggerFactory>().CreateLogger(GatewayLogNames.SignatureValidation),
    provider.GetRequiredService<IHostEnvironment>().ContentRootPath));
builder.Services.AddSingleton(provider => new PdfSignatureValidationService(
    provider.GetRequiredService<IOptions<ODVGatewayOptions>>().Value.Signatures,
    provider.GetRequiredService<TrustAnchorStore>(),
    provider.GetRequiredService<OfflineRevocationStore>(),
    provider.GetRequiredService<ILoggerFactory>().CreateLogger(GatewayLogNames.SignatureValidation)));

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

var correlationLoggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

// Request correlation middleware. Runs first so every downstream log entry and
// response carries the same X-Correlation-ID. A valid client-supplied id
// (non-empty, at most 128 HTTP token characters) is reused, otherwise a new id
// is generated.
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-ID"].ToString();
    if (!IsValidCorrelationId(correlationId))
    {
        correlationId = Guid.NewGuid().ToString("N");
    }

    context.Response.Headers["X-Correlation-ID"] = correlationId;

    using (correlationLoggerFactory
        .CreateLogger("ODVGateway.Correlation")
        .BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = correlationId }))
    {
        await next(context);
    }
});

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
    // Minimal-host routing selects mapped endpoints before this middleware;
    // StaticFileMiddleware skips them, so /index.html still reaches RenderViewerAsync.
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
    var distAvailable = !string.IsNullOrWhiteSpace(resolvedDistPath);
    // Follow the ASP.NET Core health-check convention: a degraded service answers
    // 503 so status-code-based monitors see the truth, not just the payload.
    return Results.Json(new
    {
        status = distAvailable ? "ok" : "degraded",
        openDocViewerDistPath = exposeDistPath ? resolvedDistPath : null,
        openDocViewerDistAvailable = distAvailable,
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
    }, statusCode: distAvailable ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
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
    if (!TryResolveSessionSource(sessions, sessionKey, fileIndex, out var session, out var source, out var resolutionError))
    {
        return resolutionError;
    }

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

app.MapGet("/signatures/{sessionKey}/{fileIndex:int}", async (
    HttpContext httpContext,
    string sessionKey,
    int fileIndex,
    GatewaySessionStore sessions,
    ContentTypeMapper contentTypes,
    IOptions<ODVGatewayOptions> options,
    DirectSourceFileResolver directSources,
    IHttpClientFactory httpClientFactory,
    WebClientFallbackUrlBuilder fallbackUrlBuilder,
    PdfSignatureValidationService signatureValidator,
    SignatureWorkerClient signatureWorker,
    SignatureValidationLimiter signatureLimiter,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger(GatewayLogNames.SignatureValidation);
    var response = httpContext.Response;

    // Verdicts are per-file and can change with the next incremental save or revocation update, and
    // the request itself is an access to a document: nothing here may be cached downstream.
    response.Headers.CacheControl = "no-store";

    if (!options.Value.Signatures.Enabled)
    {
        return Results.NotFound(new { error = "Signature validation is not enabled on this gateway." });
    }

    if (!TryResolveSessionSource(sessions, sessionKey, fileIndex, out var session, out var source, out var resolutionError))
    {
        return resolutionError;
    }

    if (!IsPdfSource(source, contentTypes))
    {
        return Results.Json(new { error = "Signature validation is only available for PDF source files." }, statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    using var signatureLease = signatureLimiter.TryAcquire();
    if (!signatureLease.IsAcquired)
    {
        response.Headers.RetryAfter = "1";
        return Results.Json(new { error = "Signature validation is busy. Retry later." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var signatureSource = await ReadSignatureSourceBytesAsync(
        httpContext.Request,
        session,
        source,
        contentTypes,
        options.Value,
        directSources,
        httpClientFactory,
        fallbackUrlBuilder,
        logger,
        cancellationToken);
    if (signatureSource.Error is not null)
    {
        return signatureSource.Error;
    }

    try
    {
        // Isolation is the default: one worker process per request, bounded by the same limiter
        // slot held since before buffering. The in-process path exists only for trusted archives.
        var validation = options.Value.Signatures.IsolateProcess
            ? await signatureWorker.ValidateAsync(
                options.Value.Signatures, signatureSource.Bytes!, httpContext.Request.Host.Host, cancellationToken)
            : await signatureValidator.ValidateAsync(
                signatureSource.Bytes!, cancellationToken, httpContext.Request.Host.Host);
        return Results.Json(validation, SignatureValidationJson.Options, statusCode: StatusCodes.Status200OK);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // The client went away mid-validation: nothing to report, and nothing to log as a failure.
        throw;
    }
    catch (SignatureWorkerException exception)
    {
        // Named worker failure for this file only: no Retry-After, because retrying a file that
        // crashes its worker just crashes another worker. The gateway process is unaffected.
        logger.LogWarning(
            "Signature validation worker failed for one request. Index={FileIndex} Code={Code}",
            fileIndex,
            exception.Code);
        return Results.Json(new { error = exception.Message, code = exception.Code },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (PdfEncryptedException)
    {
        logger.LogWarning("Signature validation refused an encrypted PDF. Index={FileIndex}", fileIndex);
        return Results.UnprocessableEntity(new
        {
            error = "The PDF is encrypted, so its signatures cannot be validated by the gateway."
        });
    }
    catch (PdfSignatureFormatException)
    {
        logger.LogWarning("Signature validation could not parse the PDF. Index={FileIndex}", fileIndex);
        return Results.UnprocessableEntity(new
        {
            error = "The PDF could not be parsed far enough to locate its signature dictionaries."
        });
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        // Nothing about the document body or its signers reaches the log: only the failure type.
        logger.LogWarning(exception,
            "Signature validation failed unexpectedly. Index={FileIndex}, ExceptionType={ExceptionType}",
            fileIndex,
            exception.GetType().Name);
        return Results.Problem(
            detail: "Signature validation failed. Check the gateway log for the exception type.",
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/source-pack/{sessionKey}", async (    HttpContext httpContext,
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

return 0;

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
            "The gateway is running, but no WebClient sessiondata query parameter was supplied.",
            StatusCodes.Status400BadRequest,
            OmpThemePreference.ReadMode(context.Request));
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
            "The viewer was opened without a matching /prep call, or the in-memory gateway session has expired. Open the document from WebClient again.",
            StatusCodes.Status404NotFound,
            OmpThemePreference.ReadMode(context.Request));
    }

    if (options.Value.UseBundleUrlHandoff)
    {
        // The security middleware stamps Referrer-Policy: no-referrer on every response, and a
        // browser applies a redirect's Referrer-Policy to the request that follows it. The
        // follow-up GET ?bundleUrl=... then arrived without Referer, so the handoff guard
        // rejected the gateway's own redirect whenever allowedInitiatorUrls was configured
        // (measured 2026-09-10 against a real WebClient handoff). Keep the WebClient initiator
        // visible to the same-origin viewer request; a cross-origin destination still receives
        // only the origin, which is all a host-only allowlist entry needs.
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
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

            // Without Content-Length, validate the whole payload before committing a
            // successful response. Spill to a temporary file above 64 KiB to bound memory;
            // disposal removes it on success, size rejection, timeout, or cancellation.
            await using var bufferedContent = contentLength is null && !HttpMethods.IsHead(httpContext.Request.Method)
                ? new FileBufferingWriteStream(memoryThreshold: 64 * 1024)
                : null;
            if (bufferedContent is not null)
            {
                await using var upstream = await response.Content.ReadAsStreamAsync(timeout.Token);
                await CopyBoundedStreamAsync(upstream, bufferedContent, maxProxyBytes,
                    GetSourcePackStreamBufferBytes(options), timeout.Token);
                contentLength = bufferedContent.Length;
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

            if (bufferedContent is not null)
            {
                await bufferedContent.DrainBufferAsync(httpContext.Response.Body, timeout.Token);
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
        catch (Exception ex) when (httpContext.Response.HasStarted &&
            ex is OperationCanceledException or HttpRequestException or IOException or SourcePackPayloadTooLargeException or InvalidOperationException)
        {
            // A started transfer cannot be retried or replaced with a JSON error.
            LogWebClientSourceProxyFailed(logger, session, source, attempt, ex);
            httpContext.Abort();
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
            httpContext.Response.ContentLength = null;
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

static bool IsValidCorrelationId(string? value)
{
    if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
    {
        return false;
    }

    foreach (var character in value)
    {
        var isTokenCharacter = char.IsAsciiLetterOrDigit(character)
            || "!#$%&'*+-.^_`|~".Contains(character, StringComparison.Ordinal);
        if (!isTokenCharacter)
        {
            return false;
        }
    }

    return true;
}

/// <summary>
/// The same session lookup and index bounds check that <c>/source</c> uses, so
/// <c>/signatures</c> cannot drift from the file it is supposed to describe.
/// </summary>
static bool TryResolveSessionSource(
    GatewaySessionStore sessions,
    string sessionKey,
    int fileIndex,
    [NotNullWhen(true)] out GatewaySession? session,
    [NotNullWhen(true)] out GatewaySourceFile? source,
    [NotNullWhen(false)] out IResult? error)
{
    session = null;
    source = null;
    if (!sessions.TryGet(sessionKey, out var resolvedSession))
    {
        error = Results.NotFound(new { error = "Gateway session was not found or has expired." });
        return false;
    }

    if (fileIndex < 0 || fileIndex >= resolvedSession.SourceFiles.Count)
    {
        error = Results.NotFound(new { error = "Source file index is outside the prepared session." });
        return false;
    }

    session = resolvedSession;
    source = resolvedSession.SourceFiles[fileIndex];
    error = null!;
    return true;
}

static bool IsPdfSource(GatewaySourceFile source, ContentTypeMapper contentTypes) =>
    string.Equals(source.Extension, "pdf", StringComparison.OrdinalIgnoreCase)
    || string.Equals(contentTypes.GetContentType(source.Extension), "application/pdf", StringComparison.OrdinalIgnoreCase);

/// <summary>
/// Largest PDF the signature endpoint accepts: the configured cap when it is set, otherwise the
/// gateway's existing source transport limit. A configured value can never raise the transport
/// limit, only lower it, so the endpoint cannot ask for bytes the source reader would refuse.
/// </summary>
static long GetSignatureMaxFileBytes(ODVGatewayOptions options)
{
    var transportLimit = GetMaxSourcePackFrameBytes(options);
    var configured = options.Signatures.MaxFileBytes;
    return configured > 0 ? Math.Min(configured, transportLimit) : transportLimit;
}

/// <summary>
/// Reads the whole PDF into memory for validation, reusing the buffered gateway source reader: the
/// direct file resolver, the WebClient fallback URL and the proxy fetch with its retries are the
/// same code <c>/source-pack</c> uses, so a document is never read differently for one endpoint.
/// </summary>
static async Task<SignatureSourceResult> ReadSignatureSourceBytesAsync(
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
    var maxBytes = GetSignatureMaxFileBytes(options);
    try
    {
        var payload = await ReadGatewaySourceBytesAsync(
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
        if (!payload.Ok)
        {
            return new SignatureSourceResult(null, BuildSignatureSourceError(payload.Error));
        }

        byte[] bytes;
        if (payload.ContentStream is not null)
        {
            await using var stream = payload.ContentStream;
            bytes = await ReadSourcePackBytesWithLimitAsync(
                stream,
                maxBytes,
                payload.ContentLength > 0 ? payload.ContentLength : null,
                GetSourcePackStreamBufferBytes(options),
                cancellationToken);
        }
        else
        {
            bytes = payload.Bytes;
        }

        if (bytes.LongLength > maxBytes)
        {
            return new SignatureSourceResult(null, BuildSignatureTooLargeError(bytes.LongLength, maxBytes));
        }

        if (bytes.Length == 0)
        {
            return new SignatureSourceResult(null, BuildSignatureSourceError(
                "The gateway read an empty source file."));
        }

        return new SignatureSourceResult(bytes, null);
    }
    catch (SourcePackPayloadTooLargeException exception)
    {
        logger.LogWarning("Signature validation source exceeded the size limit. Index={FileIndex}", source.Index);
        return new SignatureSourceResult(null, Results.Json(
            new { error = exception.Message },
            statusCode: StatusCodes.Status413PayloadTooLarge));
    }
    catch (IOException exception)
    {
        logger.LogWarning(exception,
            "Signature validation source could not be read. Index={FileIndex} Exception={ExceptionType}",
            source.Index,
            exception.GetType().Name);
        return new SignatureSourceResult(null, BuildSignatureSourceError(
            "The gateway could not read the source file."));
    }
    catch (HttpRequestException exception)
    {
        logger.LogWarning(exception,
            "Signature validation source fetch failed. Index={FileIndex} Exception={ExceptionType}",
            source.Index,
            exception.GetType().Name);
        return new SignatureSourceResult(null, BuildSignatureSourceError(
            "The gateway could not read the source file."));
    }
    catch (UnauthorizedAccessException exception)
    {
        logger.LogWarning(exception,
            "Signature validation source access denied. Index={FileIndex}", source.Index);
        return new SignatureSourceResult(null, BuildSignatureSourceError(
            "The gateway could not read the source file."));
    }
}

static IResult BuildSignatureTooLargeError(long actualBytes, long maxBytes) =>
    Results.Json(
        new
        {
            error = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"The PDF is larger than the signature validation limit. Actual bytes: {actualBytes}. Maximum bytes: {maxBytes}.")
        },
        statusCode: StatusCodes.Status413PayloadTooLarge);

static IResult BuildSignatureSourceError(string? message) =>
    Results.Json(
        new { error = message ?? "The gateway could not read the source file." },
        statusCode: StatusCodes.Status404NotFound);

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

    /// <summary>Log category for signature validation. Never contains document content.
    /// </summary>
    public const string SignatureValidation = "ODVGateway.Signatures";
}

internal sealed class SourcePackPayloadTooLargeException : InvalidOperationException
{
    public SourcePackPayloadTooLargeException(string message)
        : base(message)
    {
    }
}

internal sealed record SignatureSourceResult(byte[]? Bytes, IResult? Error);

internal sealed record SourcePackPayload(
    bool Ok,
    byte[] Bytes,
    string ContentType,
    string? Error,
    Stream? ContentStream = null,
    long ContentLength = 0);
