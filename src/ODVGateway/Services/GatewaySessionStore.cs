using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ODVGateway.Models;
using ODVGateway.Options;

namespace ODVGateway.Services;

public sealed class GatewaySessionStore
{
    private const int MinimumConcurrentSessions = 1;
    private const int MinimumSessionTtlMinutes = 5;

    // The dictionaries are individually concurrent, but the session store also maintains a
    // cross-dictionary lookup invariant and a capacity check. Keep those compound mutations under
    // one small gate so a session key and its handoff lookup key cannot drift apart.
    private readonly object _sessionMutationGate = new();

    private readonly ConcurrentDictionary<string, GatewaySession> _sessionsBySessionKey =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, string> _sessionKeysByHandoffLookupKey =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IOptionsMonitor<ODVGatewayOptions> _options;
    private readonly ILogger<GatewaySessionStore> _logger;
    private long _capacityRejectedCount;
    private int _minimumTtlWarningLogged;

    public GatewaySessionStore(
        IOptionsMonitor<ODVGatewayOptions> options,
        ILogger<GatewaySessionStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    public int Count
    {
        get
        {
            lock (_sessionMutationGate)
            {
                PruneExpiredCore(DateTimeOffset.UtcNow);
                return _sessionsBySessionKey.Count;
            }
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

            var ttl = TimeSpan.FromMinutes(GetEffectiveSessionTtlMinutes(currentOptions));
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

    public bool TryGet(string sessionKey, [NotNullWhen(true)] out GatewaySession? session)
    {
        lock (_sessionMutationGate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneExpiredCore(now);

            if (_sessionsBySessionKey.TryGetValue(sessionKey, out var candidate) &&
                candidate.ExpiresUtc > now)
            {
                session = candidate;
                return true;
            }

            session = null;
            return false;
        }
    }

    public bool TryGetByHandoffLookupKey(
        string handoffLookupKey,
        [NotNullWhen(true)] out GatewaySession? session)
    {
        lock (_sessionMutationGate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneExpiredCore(now);

            if (_sessionKeysByHandoffLookupKey.TryGetValue(handoffLookupKey, out var sessionKey))
            {
                if (_sessionsBySessionKey.TryGetValue(sessionKey, out var candidate) &&
                    candidate.ExpiresUtc > now)
                {
                    session = candidate;
                    return true;
                }

                RemoveHandoffLookupKeyIfCurrent(handoffLookupKey, sessionKey);
            }

            session = null;
            return false;
        }
    }

    private void PruneExpiredCore(DateTimeOffset now)
    {
        // Caller must hold _sessionMutationGate so session and handoff lookup indexes stay in sync.
        // The store is capped by MaxConcurrentSessions. Materializing the filtered snapshot keeps
        // exact key/value removals deterministic while pruning the two related indexes.
        foreach (var removedSession in _sessionsBySessionKey
            .Where(entry => entry.Value.ExpiresUtc <= now)
            .ToArray()
            .Select(TryRemoveSession)
            .OfType<GatewaySession>())
        {
            RemoveHandoffLookupKeyIfCurrent(removedSession);
        }
    }

    private GatewaySession? TryRemoveSession(KeyValuePair<string, GatewaySession> entry)
        => RemoveExact(_sessionsBySessionKey, entry)
            ? entry.Value
            : null;

    private void RemoveHandoffLookupKeyIfCurrent(GatewaySession removedSession)
    {
        RemoveHandoffLookupKeyIfCurrent(removedSession.HandoffLookupKey, removedSession.SessionKey);
    }

    private void RemoveHandoffLookupKeyIfCurrent(string handoffLookupKey, string sessionKey)
    {
        if (_sessionKeysByHandoffLookupKey.TryGetValue(
                handoffLookupKey,
                out var mappedSessionKey) &&
            mappedSessionKey.Equals(sessionKey, StringComparison.OrdinalIgnoreCase))
        {
            RemoveExact(
                _sessionKeysByHandoffLookupKey,
                new KeyValuePair<string, string>(handoffLookupKey, sessionKey));
        }
    }

    private static bool RemoveExact<TKey, TValue>(
        ConcurrentDictionary<TKey, TValue> dictionary,
        KeyValuePair<TKey, TValue> entry)
        where TKey : notnull
    {
        return ((ICollection<KeyValuePair<TKey, TValue>>)dictionary).Remove(entry);
    }

    private static int GetMaxConcurrentSessions(ODVGatewayOptions options)
    {
        return Math.Max(MinimumConcurrentSessions, options.MaxConcurrentSessions);
    }

    private int GetEffectiveSessionTtlMinutes(ODVGatewayOptions options)
    {
        if (options.SessionTtlMinutes >= MinimumSessionTtlMinutes)
        {
            Volatile.Write(ref _minimumTtlWarningLogged, 0);
            return options.SessionTtlMinutes;
        }

        if (Interlocked.Exchange(ref _minimumTtlWarningLogged, 1) == 0)
        {
            _logger.LogWarning(
                "ODVGateway SessionTtlMinutes is below the supported minimum. ConfiguredSessionTtlMinutes={ConfiguredSessionTtlMinutes}, EffectiveSessionTtlMinutes={EffectiveSessionTtlMinutes}",
                options.SessionTtlMinutes,
                MinimumSessionTtlMinutes);
        }

        return MinimumSessionTtlMinutes;
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
    private const char FileTicketDelimiter = '|';
    private const int FileTicketPartCount = 3;

    public static FileTicket Parse(string? value)
    {
        var raw = value ?? string.Empty;
        var parts = raw.Split(FileTicketDelimiter, FileTicketPartCount);

        if (parts.Length >= FileTicketPartCount)
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
        if (TryGetLocalFileUriPath(raw, out var localPath))
        {
            return localPath;
        }

        return raw;
    }

    private static bool TryGetLocalFileUriPath(
        string value,
        [NotNullWhen(true)] out string? localPath)
    {
        localPath = null;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var fileUri) ||
            !fileUri.IsFile)
        {
            return false;
        }

        try
        {
            var candidate = fileUri.LocalPath;
            if (!Path.IsPathFullyQualified(candidate))
            {
                return false;
            }

            // This parse step only converts local file URIs into path form for later routing.
            // Direct server-side reads still require TrustClientFilePath and TrustedSourceRoots.
            localPath = candidate;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
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
        catch (NotSupportedException)
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
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
