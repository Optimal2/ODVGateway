using System.Diagnostics;
using Microsoft.Extensions.Options;
using ODVGateway.Models;
using ODVGateway.Options;

namespace ODVGateway.Services;

public sealed class OpenDocViewerBundleFactory
{
    private readonly ContentTypeMapper _contentTypes;
    private readonly DirectSourceFileResolver _directSources;
    private readonly WebClientFallbackUrlBuilder _fallbackUrlBuilder;
    private readonly IOptionsMonitor<ODVGatewayOptions> _options;

    public OpenDocViewerBundleFactory(
        ContentTypeMapper contentTypes,
        DirectSourceFileResolver directSources,
        WebClientFallbackUrlBuilder fallbackUrlBuilder,
        IOptionsMonitor<ODVGatewayOptions> options)
    {
        _contentTypes = contentTypes;
        _directSources = directSources;
        _fallbackUrlBuilder = fallbackUrlBuilder;
        _options = options;
    }

    public async Task<OpenDocViewerBundle> CreateAsync(
        HttpRequest request,
        GatewaySession session,
        WebClientSessionData? sessionData = null,
        CancellationToken cancellationToken = default)
    {
        var bundleBuildStarted = Stopwatch.GetTimestamp();
        var options = _options.CurrentValue;
        var sourceRoutes = new SourceRouteStats();
        var documents = new List<OpenDocViewerDocument>();
        var filesByDocument = session.SourceFiles
            .GroupBy(file => file.DocumentIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());

        for (var documentIndex = 0; documentIndex < session.Prep.PortableDocuments.Count; documentIndex++)
        {
            var sourceDocument = session.Prep.PortableDocuments[documentIndex];
            var documentId = !string.IsNullOrWhiteSpace(sourceDocument.DocumentId)
                ? sourceDocument.DocumentId.Trim()
                : $"document-{documentIndex + 1}";

            filesByDocument.TryGetValue(documentIndex, out var sourceFiles);
            sourceFiles ??= [];

            var metadata = BuildMetadata(sourceDocument);
            var aliases = BuildMetadataAliases(metadata.MetaById, options.MetadataAliases);
            var documentPageCountHint = ResolveDocumentPageCountHint(sourceDocument);
            var documentFileCount = sourceFiles.Length;

            documents.Add(new OpenDocViewerDocument
            {
                DocumentId = documentId,
                DocumentVersion = BuildDocumentVersion(sourceDocument, sourceFiles),
                Files = sourceFiles
                    .OrderBy(file => file.FileNumber)
                    .Select(file => BuildViewerFile(
                        request,
                        session,
                        session.SessionKey,
                        documentId,
                        file,
                        options,
                        sourceRoutes,
                        documentPageCountHint,
                        documentFileCount))
                    .ToArray(),
                Meta = metadata.Meta.Count > 0 ? metadata.Meta : null,
                MetaById = metadata.MetaById.Count > 0 ? metadata.MetaById : null,
                Metadata = aliases.Metadata.Count > 0 ? aliases.Metadata : null,
                MetadataDetails = aliases.MetadataDetails.Count > 0 ? aliases.MetadataDetails : null
            });
        }

        var bundleBuildMs = GetElapsedMilliseconds(bundleBuildStarted);

        return new OpenDocViewerBundle
        {
            Session = new OpenDocViewerSession
            {
                Id = sessionData?.SessionId ?? session.Prep.SessionId ?? session.SessionKey,
                UserId = sessionData?.UserId ?? session.Prep.UserId,
                IssuedAt = session.CreatedUtc.ToString("O")
            },
            Documents = documents,
            Integration = new Dictionary<string, object?>
            {
                ["source"] = "ODVGateway",
                ["mode"] = options.TrustClientFilePath ? "host-direct-file-path" : "host-ticket-fallback",
                ["transport"] = options.UseBundleUrlHandoff ? "bundle-url" : "inline-html",
                ["sourceTransport"] = "source-pack-stream",
                ["sourcePackFormat"] = "odvsp1",
                ["sourcePackUrl"] = BuildSourcePackUrl(request, session.SessionKey),
                ["sourcePackSourceCount"] = session.SourceFiles.Count,
                ["sessionKey"] = session.SessionKey,
                ["preparedAt"] = session.CreatedUtc.ToString("O"),
                ["expiresAt"] = session.ExpiresUtc.ToString("O"),
                ["inlineSourceCount"] = 0,
                ["inlineSourceBytes"] = 0,
                ["localInlineSourceCount"] = 0,
                ["localInlineSourceBytes"] = 0,
                ["remoteInlineSourceCount"] = 0,
                ["remoteInlineSourceBytes"] = 0,
                ["remoteInlineAttemptCount"] = 0,
                ["remoteInlineFailureCount"] = 0,
                ["remoteInlineMaxConcurrency"] = 0,
                ["remoteInlineMaxCount"] = 0,
                ["remoteInlineMaxTotalBytes"] = 0,
                ["hostGatewayProxySourceCount"] = sourceRoutes.WebClientGatewayProxySourceCount,
                ["hostGatewayProxyThreshold"] = options.WebClientSourceFallback.ProxyThroughGatewayAboveSourceCount,
                ["hostGatewayProxyMaxConcurrency"] = options.WebClientSourceFallback.ProxyMaxConcurrency,
                ["gatewayBundleBuildMs"] = bundleBuildMs,
                ["directReadableSourceCount"] = sourceRoutes.DirectReadableCount,
                ["directMissingSourceCount"] = sourceRoutes.DirectMissingCount,
                ["gatewaySourceUrlCount"] = sourceRoutes.GatewaySourceUrlCount,
                ["hostFallbackSourceCount"] = sourceRoutes.WebClientFallbackUrlCount,
                ["hostFilePathUrlSourceCount"] = sourceRoutes.WebClientFilePathUrlCount,
                ["hostTemplateUrlSourceCount"] = sourceRoutes.WebClientTemplateUrlCount,
                ["sourcePageCountHintCount"] = sourceRoutes.PageCountHintCount,
                ["sourcePageCountHintTotal"] = sourceRoutes.PageCountHintTotal,
                ["sourcePageCountHintMissingCount"] = sourceRoutes.PageCountHintMissingCount
            }
        };
    }

    private static double GetElapsedMilliseconds(long startedTimestamp)
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - startedTimestamp;
        return elapsedTicks * 1000d / Stopwatch.Frequency;
    }

    private OpenDocViewerFile BuildViewerFile(
        HttpRequest request,
        GatewaySession session,
        string sessionKey,
        string documentId,
        GatewaySourceFile file,
        ODVGatewayOptions options,
        SourceRouteStats sourceRoutes,
        int? documentPageCountHint,
        int documentFileCount)
    {
        var contentType = _contentTypes.GetContentType(file.Extension);
        var directReadable = _directSources.TryResolve(file, out var directSource);
        sourceRoutes.RecordDirectReadable(directReadable);

        // Source-pack is the single payload transport for gateway sessions. Do not also inline
        // raster bytes in the bootstrap bundle: WebClient tickets can behave as one-use links, and
        // pre-consuming them here makes the subsequent source-pack stream fail validation.
        var fallbackResult = !directReadable
            ? _fallbackUrlBuilder.BuildFallbackUrl(request, sessionKey, file, options.WebClientSourceFallback)
            : WebClientFallbackUrlResult.None;
        var fallbackUrl = fallbackResult.Url;
        if (fallbackResult.Kind == WebClientFallbackUrlKind.FilePath)
        {
            sourceRoutes.RecordWebClientFilePathUrl();
        }
        else if (fallbackResult.Kind == WebClientFallbackUrlKind.Template)
        {
            sourceRoutes.RecordWebClientTemplateUrl();
        }

        var shouldProxyWebClientFallback = fallbackUrl is not null &&
            ShouldProxyWebClientFallback(options, session.SourceFiles.Count);
        var sourceUrl = shouldProxyWebClientFallback
            ? BuildSourceUrl(request, sessionKey, file.Index)
            : fallbackUrl ?? BuildSourceUrl(request, sessionKey, file.Index);

        if (fallbackUrl is not null)
        {
            sourceRoutes.RecordWebClientFallbackUrl();
            if (shouldProxyWebClientFallback) sourceRoutes.RecordWebClientGatewayProxySource();
        }
        else
        {
            sourceRoutes.RecordGatewaySourceUrl();
        }

        var sourceKind = ResolveSourceKind(file.Extension);
        var pageHint = ResolvePageCountHint(file, documentPageCountHint, documentFileCount, sourceKind);
        if (pageHint.Count is > 0)
        {
            sourceRoutes.RecordPageCountHint(pageHint.Count.Value);
        }
        else
        {
            sourceRoutes.RecordMissingPageCountHint();
        }

        return new OpenDocViewerFile
        {
            Id = !string.IsNullOrWhiteSpace(file.FileId)
                ? file.FileId!
                : $"{documentId}-{file.FileNumber}",
            Url = sourceUrl,
            Ext = file.Extension,
            DisplayName = file.DisplayName,
            ContentType = contentType,
            FileNumber = file.FileNumber,
            SourceKind = sourceKind,
            PageCountHint = pageHint.Count,
            PageCountHintSource = pageHint.Source,
            SourceSizeBytes = directReadable ? directSource.Length : null,
            InlineBase64 = null,
            InlineMimeType = null,
            InlineSizeBytes = null
        };
    }

    private static bool ShouldProxyWebClientFallback(ODVGatewayOptions options, int sourceCount)
    {
        var fallback = options.WebClientSourceFallback;
        if (!fallback.Enabled || !fallback.ProxyThroughGateway) return false;

        var threshold = Math.Max(0, fallback.ProxyThroughGatewayAboveSourceCount);
        return threshold <= 0 || sourceCount >= threshold;
    }

    private static string ResolveSourceKind(string? extension)
    {
        return NormalizeExtension(extension) switch
        {
            "pdf" => "pdf",
            "tif" or "tiff" => "tiff",
            "jpg" or "jpeg" or "png" or "bmp" or "gif" or "webp" => "raster-single-page",
            _ => "unknown"
        };
    }

    private static PageCountHint ResolvePageCountHint(
        GatewaySourceFile file,
        int? documentPageCountHint,
        int documentFileCount,
        string sourceKind)
    {
        if (sourceKind == "raster-single-page")
        {
            return new PageCountHint(1, "single-page-raster-format");
        }

        if (documentPageCountHint is > 0)
        {
            if (documentFileCount <= 1)
            {
                return new PageCountHint(documentPageCountHint, "document-metadata-single-file");
            }

            if (documentPageCountHint == documentFileCount)
            {
                return new PageCountHint(1, "document-metadata-file-count-match");
            }
        }

        return new PageCountHint(null, null);
    }

    private static int? ResolveDocumentPageCountHint(WebClientPortableDocument document)
    {
        var pageCountField = document.MetaData.FirstOrDefault(field =>
            string.Equals(field.Id, "504", StringComparison.OrdinalIgnoreCase));
        return TryParsePositiveInt(pageCountField?.Value);
    }

    private static int? TryParsePositiveInt(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
            ? parsed
            : null;
    }

    private static string NormalizeExtension(string? extension)
    {
        return (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
    }

    private static string BuildSourceUrl(HttpRequest request, string sessionKey, int fileIndex)
    {
        var basePath = request.PathBase.HasValue ? request.PathBase.Value : string.Empty;
        var path = $"{basePath}/source/{Uri.EscapeDataString(sessionKey)}/{fileIndex}";
        return $"{request.Scheme}://{request.Host}{path}";
    }

    private static string BuildSourcePackUrl(HttpRequest request, string sessionKey)
    {
        var basePath = request.PathBase.HasValue ? request.PathBase.Value : string.Empty;
        var path = $"{basePath}/source-pack/{Uri.EscapeDataString(sessionKey)}";
        return $"{request.Scheme}://{request.Host}{path}";
    }

    private static string BuildDocumentVersion(
        WebClientPortableDocument document,
        IReadOnlyCollection<GatewaySourceFile> files)
    {
        var parts = new[]
        {
            document.DocumentId,
            document.Created,
            document.Modified,
            document.SourceId,
            files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(",", files.Select(file => file.FileId ?? file.DisplayName))
        };

        return string.Join("|", parts.Select(part => part ?? string.Empty));
    }

    private static MetadataProjection BuildMetadata(WebClientPortableDocument document)
    {
        var meta = new List<OpenDocViewerMetadataRecord>();
        var metaById = new Dictionary<string, OpenDocViewerMetadataRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in document.MetaData)
        {
            if (string.IsNullOrWhiteSpace(field.Id))
            {
                continue;
            }

            var id = field.Id.Trim();
            var record = new OpenDocViewerMetadataRecord
            {
                Id = id,
                Key = id,
                Value = EmptyToNull(field.Value),
                LookupValue = EmptyToNull(field.LookupValue),
                RawValue = EmptyToNull(field.Value),
                RawLookupValue = EmptyToNull(field.LookupValue)
            };

            meta.Add(record);
            if (!metaById.ContainsKey(id))
            {
                metaById[id] = record;
            }
        }

        return new MetadataProjection(meta, metaById);
    }

    private static MetadataAliasProjection BuildMetadataAliases(
        IReadOnlyDictionary<string, OpenDocViewerMetadataRecord> metaById,
        IReadOnlyDictionary<string, MetadataAliasOption> metadataAliases)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var details = new Dictionary<string, OpenDocViewerMetadataAliasDetail>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in metadataAliases)
        {
            var alias = entry.Key?.Trim();
            if (string.IsNullOrWhiteSpace(alias))
            {
                continue;
            }

            var option = entry.Value;
            foreach (var fieldId in option.GetFieldIds())
            {
                if (!metaById.TryGetValue(fieldId, out var record))
                {
                    continue;
                }

                var picked = PickMetadataValue(record, option.Prefer);
                if (picked is null)
                {
                    continue;
                }

                metadata[alias] = picked.Value.Value;
                details[alias] = new OpenDocViewerMetadataAliasDetail
                {
                    Alias = alias,
                    FieldId = fieldId,
                    SelectedValue = picked.Value.Value,
                    SelectedSource = picked.Value.Source,
                    Value = record.Value,
                    LookupValue = record.LookupValue,
                    RawValue = record.RawValue,
                    RawLookupValue = record.RawLookupValue,
                    Label = option.Label,
                    Type = option.Type,
                    Contexts = option.Contexts.Length > 0 ? option.Contexts : null
                };

                break;
            }
        }

        return new MetadataAliasProjection(metadata, details);
    }

    private static (string Value, string Source)? PickMetadataValue(
        OpenDocViewerMetadataRecord record,
        string? preference)
    {
        var normalizedPreference = (preference ?? string.Empty).Trim().ToLowerInvariant();
        var value = EmptyToNull(record.Value);
        var lookupValue = EmptyToNull(record.LookupValue);

        return normalizedPreference switch
        {
            "value" => value is not null ? (value, "value") : null,
            "lookupvalue" or "lookup" => lookupValue is not null ? (lookupValue, "lookupValue") : null,
            "lookupthenvalue" or "lookup-first" or "lookupfirst" =>
                lookupValue is not null ? (lookupValue, "lookupValue") :
                value is not null ? (value, "value") :
                null,
            _ =>
                value is not null ? (value, "value") :
                lookupValue is not null ? (lookupValue, "lookupValue") :
                null
        };
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private sealed record MetadataProjection(
        IReadOnlyList<OpenDocViewerMetadataRecord> Meta,
        IReadOnlyDictionary<string, OpenDocViewerMetadataRecord> MetaById);

    private sealed record MetadataAliasProjection(
        IReadOnlyDictionary<string, string> Metadata,
        IReadOnlyDictionary<string, OpenDocViewerMetadataAliasDetail> MetadataDetails);

    private sealed class SourceRouteStats
    {
        private readonly object _gate = new();

        public int DirectReadableCount { get; private set; }

        public int DirectMissingCount { get; private set; }

        public int GatewaySourceUrlCount { get; private set; }

        public int WebClientFallbackUrlCount { get; private set; }

        public int WebClientFilePathUrlCount { get; private set; }

        public int WebClientTemplateUrlCount { get; private set; }

        public int WebClientGatewayProxySourceCount { get; private set; }

        public int PageCountHintCount { get; private set; }

        public int PageCountHintMissingCount { get; private set; }

        public int PageCountHintTotal { get; private set; }

        public void RecordDirectReadable(bool directReadable)
        {
            lock (_gate)
            {
                if (directReadable) DirectReadableCount += 1;
                else DirectMissingCount += 1;
            }
        }

        public void RecordGatewaySourceUrl()
        {
            lock (_gate) GatewaySourceUrlCount += 1;
        }

        public void RecordWebClientFallbackUrl()
        {
            lock (_gate) WebClientFallbackUrlCount += 1;
        }

        public void RecordWebClientFilePathUrl()
        {
            lock (_gate) WebClientFilePathUrlCount += 1;
        }

        public void RecordWebClientTemplateUrl()
        {
            lock (_gate) WebClientTemplateUrlCount += 1;
        }

        public void RecordWebClientGatewayProxySource()
        {
            lock (_gate) WebClientGatewayProxySourceCount += 1;
        }

        public void RecordPageCountHint(int pageCount)
        {
            lock (_gate)
            {
                PageCountHintCount += 1;
                PageCountHintTotal += Math.Max(1, pageCount);
            }
        }

        public void RecordMissingPageCountHint()
        {
            lock (_gate) PageCountHintMissingCount += 1;
        }
    }

    private sealed record PageCountHint(int? Count, string? Source);

}
