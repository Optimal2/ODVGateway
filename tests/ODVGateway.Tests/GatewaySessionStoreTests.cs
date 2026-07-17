using Microsoft.Extensions.Logging.Abstractions;
using ODVGateway.Models;
using ODVGateway.Options;
using ODVGateway.Services;

namespace ODVGateway.Tests;

public sealed class GatewaySessionStoreTests
{
    private static GatewaySessionStore CreateStore(int maxConcurrentSessions = 10)
    {
        var options = new ODVGatewayOptions
        {
            MaxConcurrentSessions = maxConcurrentSessions
        };

        return new GatewaySessionStore(
            new StaticOptionsMonitor<ODVGatewayOptions>(options),
            NullLogger<GatewaySessionStore>.Instance);
    }

    private static WebClientPrepRequest CreatePrep(string documentId = "doc-1")
    {
        return new WebClientPrepRequest
        {
            UserId = "user-1",
            SessionId = "session-1",
            AspxAuth = "auth-ticket",
            PortableDocuments =
            [
                new WebClientPortableDocument
                {
                    DocumentId = documentId,
                    FileData = ["file-1|pdf|C:/docs/file-1.pdf"]
                }
            ]
        };
    }

    [Fact]
    public void Store_OnEmptyStore_StoresSessionAndUpdatesCount()
    {
        var store = CreateStore();

        var result = store.Store(CreatePrep());

        Assert.True(result.IsStored);
        Assert.NotNull(result.Session);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Store_AtCapacity_ReturnsCapacityExceededAndIncrementsCounter()
    {
        var store = CreateStore(maxConcurrentSessions: 1);

        var first = store.Store(CreatePrep("doc-1"));
        var second = store.Store(CreatePrep("doc-2"));

        Assert.True(first.IsStored);
        Assert.False(second.IsStored);
        Assert.Null(second.Session);
        Assert.Equal(1, store.CapacityRejectedCount);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void TryGet_RoundTripsStoredSessionBySessionKey()
    {
        var store = CreateStore();
        var stored = store.Store(CreatePrep());
        Assert.NotNull(stored.Session);

        var found = store.TryGet(stored.Session!.SessionKey, out var session);

        Assert.True(found);
        Assert.NotNull(session);
        Assert.Equal(stored.Session.SessionKey, session!.SessionKey);
        Assert.Equal(stored.Session.HandoffLookupKey, session.HandoffLookupKey);
    }

    [Fact]
    public void TryGetByHandoffLookupKey_RoundTripsStoredSession()
    {
        var prep = CreatePrep();
        var store = CreateStore();
        var stored = store.Store(prep);
        var lookupKey = SessionKeyFactory.CreateHandoffLookupKey(prep);

        var found = store.TryGetByHandoffLookupKey(lookupKey, out var session);

        Assert.True(found);
        Assert.NotNull(session);
        Assert.Equal(stored.Session!.SessionKey, session!.SessionKey);
    }

    [Fact]
    public void TryGet_UnknownKey_ReturnsFalse()
    {
        var store = CreateStore();
        store.Store(CreatePrep());

        Assert.False(store.TryGet("no-such-key", out var session));
        Assert.Null(session);
        Assert.False(store.TryGetByHandoffLookupKey("no-such-key", out var byLookup));
        Assert.Null(byLookup);
    }

    [Fact]
    public void Store_PopulatesSourceFilesFromPrep()
    {
        var store = CreateStore();

        var stored = store.Store(CreatePrep());

        Assert.NotNull(stored.Session);
        var sourceFile = Assert.Single(stored.Session!.SourceFiles);
        Assert.Equal("doc-1", sourceFile.DocumentId);
        Assert.Equal("pdf", sourceFile.Extension);
        Assert.Equal("file-1.pdf", sourceFile.DisplayName);
    }
}
