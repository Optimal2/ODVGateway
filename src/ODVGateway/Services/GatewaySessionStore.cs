using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ODVGateway.Models;
using ODVGateway.Options;

namespace ODVGateway.Services;

public sealed class GatewaySessionStore
{
    private readonly object _sessionMutationGate = new();

    private readonly ConcurrentDictionary<string, GatewaySession> _sessionsBySessionKey =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, string> _sessionKeysByHandoffLookupKey =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IOptionsMonitor<ODVGatewayOptions> _options;
    private long _capacityRejectedCount;

    public GatewaySessionStore(IOptionsMonitor<ODVGatewayOptions> options)
    {
        _options = options;
    }

    public int Count
    {
        get
        {
            PruneExpired();
            return _sessionsBySessionKey.Count;
        }
    }

    public int MaxConcurrentSessions => GetMaxConcurrentSessions(_options.CurrentValue);

    public long CapacityRejectedCount => Interlocked.Read(ref _capacityRejectedCount);

    public GatewaySessionStoreResult Store(WebClientPrepRequest prep)
    {
        lock (_sessionMutationGate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneExpiredCore(now);

            var currentOptions = _options.CurrentValue;
            var cap = GetMaxConcurrentSessions(currentOptions);
            if (_sessionsBySessionKey.Count >= cap)
            {
                Interlocked.Increment(ref _capacityRejectedCount);
                return GatewaySessionStoreResult.CapacityExceeded;
            }

            var ttl = TimeSpan.FromMinutes(Math.Max(1, currentOptions.SessionTtlMinutes));
            var sessionKey = SessionKeyFactory.CreateSessionKey();
            var handoffLookupKey = SessionKeyFactory.CreateHandoffLookupKey(prep);
            var sourceFiles = BuildSourceFiles(prep);

            var session = new GatewaySession
            {
                SessionKey = sessionKey,
                HandoffLookupKey = handoffLookupKey,
                Prep = prep,
                CreatedUtc = now,
                ExpiresUtc = now.Add(ttl),
                SourceFiles = sourceFiles
            };

            _sessionsBySessionKey[sessionKey] = session;
            _sessionKeysByHandoffLookupKey[handoffLookupKey] = sessionKey;
            return GatewaySessionStoreResult.Stored(session);
        }
    }

    public bool TryGet(string sessionKey, out GatewaySession session)
    {
        PruneExpired();

        if (_sessionsBySessionKey.TryGetValue(sessionKey, out var candidate) &&
            candidate.ExpiresUtc > DateTimeOffset.UtcNow)
        {
            session = candidate;
            return true;
        }

        session = null!;
        return false;
    }

    public bool TryGetByHandoffLookupKey(string handoffLookupKey, out GatewaySession session)
    {
        PruneExpired();

        if (_sessionKeysByHandoffLookupKey.TryGetValue(handoffLookupKey, out var sessionKey) &&
            TryGet(sessionKey, out session))
        {
            return true;
        }

        _sessionKeysByHandoffLookupKey.TryRemove(handoffLookupKey, out _);
        session = null!;
        return false;
    }

    private void PruneExpired()
    {
        lock (_sessionMutationGate)
        {
            PruneExpiredCore(DateTimeOffset.UtcNow);
        }
    }

    private void PruneExpiredCore(DateTimeOffset now)
    {
        foreach (var entry in _sessionsBySessionKey
            .Where(entry => entry.Value.ExpiresUtc <= now)
            .ToArray())
        {
            if (_sessionsBySessionKey.TryRemove(entry.Key, out var removedSession) &&
                _sessionKeysByHandoffLookupKey.TryGetValue(
                    removedSession.HandoffLookupKey,
                    out var mappedSessionKey) &&
                mappedSessionKey.Equals(removedSession.SessionKey, StringComparison.OrdinalIgnoreCase))
            {
                _sessionKeysByHandoffLookupKey.TryRemove(removedSession.HandoffLookupKey, out _);
            }
        }
    }

    private static int GetMaxConcurrentSessions(ODVGatewayOptions options)
    {
        return Math.Max(1, options.MaxConcurrentSessions);
    }

    private static IReadOnlyList<GatewaySourceFile> BuildSourceFiles(WebClientPrepRequest prep)
    {
        var files = new List<GatewaySourceFile>();

        for (var documentIndex = 0; documentIndex < prep.PortableDocuments.Count; documentIndex++)
        {
            var document = prep.PortableDocuments[documentIndex];
            var documentId = !string.IsNullOrWhiteSpace(document.DocumentId)
                ? document.DocumentId.Trim()
                : $"document-{documentIndex + 1}";

            for (var fileIndex = 0; fileIndex < document.FileData.Count; fileIndex++)
            {
                var parsed = FileTicket.Parse(document.FileData[fileIndex]);
                files.Add(new GatewaySourceFile
                {
                    Index = files.Count,
                    DocumentId = documentId,
                    DocumentIndex = documentIndex,
                    FileNumber = fileIndex + 1,
                    FileId = parsed.FileId,
                    Extension = parsed.Extension,
                    FilePath = parsed.FilePath,
                    DisplayName = parsed.GetDisplayName(fileIndex + 1)
                });
            }
        }

        return files;
    }
}

public readonly record struct GatewaySessionStoreResult(bool IsStored, GatewaySession? Session)
{
    public static GatewaySessionStoreResult Stored(GatewaySession session) => new(true, session);

    public static GatewaySessionStoreResult CapacityExceeded => new(false, null);
}

internal sealed record FileTicket(string? FileId, string? Extension, string FilePath)
{
    public static FileTicket Parse(string? value)
    {
        var raw = value ?? string.Empty;
        var parts = raw.Split('|', 3);

        if (parts.Length >= 3)
        {
            return new FileTicket(
                NormalizeText(parts[0]),
                NormalizeExtension(parts[1]) ?? NormalizeExtensionFromPath(parts[2]),
                NormalizeFilePath(parts[2]));
        }

        return new FileTicket(
            FileId: null,
            Extension: NormalizeExtensionFromPath(raw),
            FilePath: NormalizeFilePath(raw));
    }

    public string GetDisplayName(int ordinal)
    {
        var name = SafeGetFileName(FilePath);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var baseName = !string.IsNullOrWhiteSpace(FileId) ? FileId : $"source-{ordinal}";
        return !string.IsNullOrWhiteSpace(Extension) ? $"{baseName}.{Extension}" : baseName;
    }

    private static string NormalizeFilePath(string? value)
    {
        var raw = value?.Trim() ?? string.Empty;
        if (raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(raw, UriKind.Absolute, out var fileUri))
        {
            return fileUri.LocalPath;
        }

        return raw;
    }

    private static string? NormalizeExtensionFromPath(string? path)
    {
        try
        {
            return NormalizeExtension(Path.GetExtension(path ?? string.Empty));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? NormalizeExtension(string? value)
    {
        var normalized = value?.Trim().TrimStart('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizeText(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? SafeGetFileName(string value)
    {
        try
        {
            return Path.GetFileName(value);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
