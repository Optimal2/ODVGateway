using ODVGateway.Models;
using ODVGateway.Services;

namespace ODVGateway.Tests;

public sealed class SessionKeyFactoryTests
{
    [Fact]
    public void CreateSessionKey_Returns64CharLowercaseHex()
    {
        var key = SessionKeyFactory.CreateSessionKey();

        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    [Fact]
    public void CreateSessionKey_ReturnsDifferentKeysAcrossCalls()
    {
        var first = SessionKeyFactory.CreateSessionKey();
        var second = SessionKeyFactory.CreateSessionKey();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CreateHandoffLookupKey_IsDeterministicForSameInput()
    {
        var prep = CreatePrep();

        var first = SessionKeyFactory.CreateHandoffLookupKey(prep);
        var second = SessionKeyFactory.CreateHandoffLookupKey(CreatePrep());

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void CreateHandoffLookupKey_DiffersForDifferentInput()
    {
        var baseline = SessionKeyFactory.CreateHandoffLookupKey(CreatePrep());

        var differentDocument = CreatePrep();
        differentDocument.PortableDocuments[0].DocumentId = "doc-other";
        var withDifferentDocument = SessionKeyFactory.CreateHandoffLookupKey(differentDocument);

        var differentUser = CreatePrep();
        differentUser.UserId = "user-other";
        var withDifferentUser = SessionKeyFactory.CreateHandoffLookupKey(differentUser);

        Assert.NotEqual(baseline, withDifferentDocument);
        Assert.NotEqual(baseline, withDifferentUser);
    }

    [Fact]
    public void CreateHandoffLookupKey_PrepAndEquivalentSessionDataProduceSameKey()
    {
        var prep = CreatePrep();
        var sessionData = new WebClientSessionData
        {
            UserId = prep.UserId,
            SessionId = prep.SessionId,
            AspxAuth = prep.AspxAuth,
            CaseIds = prep.PortableDocuments
                .Select(document => document.DocumentId!)
                .ToList()
        };

        var fromPrep = SessionKeyFactory.CreateHandoffLookupKey(prep);
        var fromSessionData = SessionKeyFactory.CreateHandoffLookupKey(sessionData);

        Assert.Equal(fromPrep, fromSessionData);
    }

    private static WebClientPrepRequest CreatePrep()
    {
        return new WebClientPrepRequest
        {
            UserId = "user-1",
            SessionId = "session-1",
            AspxAuth = "auth-ticket",
            PortableDocuments =
            [
                new WebClientPortableDocument { DocumentId = "doc-1" },
                new WebClientPortableDocument { DocumentId = "doc-2" }
            ]
        };
    }
}
