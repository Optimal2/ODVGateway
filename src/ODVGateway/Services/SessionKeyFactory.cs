using System.Security.Cryptography;
using System.Text;
using ODVGateway.Models;

namespace ODVGateway.Services;

public static class SessionKeyFactory
{
    private const int SessionKeyBytes = 32;

    // Public gateway URLs use random session keys. The deterministic handoff
    // lookup key is internal only and bridges WebClient /prep to ?sessiondata.
    public static string CreateSessionKey()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(SessionKeyBytes))
            .ToLowerInvariant();
    }

    public static string CreateHandoffLookupKey(WebClientPrepRequest prep)
    {
        var documentIds = prep.PortableDocuments
            .Select(document => document.DocumentId)
            .ToArray();

        return CreateHandoffLookupKey(prep.UserId, prep.SessionId, prep.AspxAuth, documentIds);
    }

    public static string CreateHandoffLookupKey(WebClientSessionData sessionData)
    {
        return CreateHandoffLookupKey(
            sessionData.UserId,
            sessionData.SessionId,
            sessionData.AspxAuth,
            sessionData.CaseIds);
    }

    private static string CreateHandoffLookupKey(
        string? userId,
        string? sessionId,
        string? aspxAuth,
        IEnumerable<string?> documentIds)
    {
        var builder = new StringBuilder();
        AppendPart(builder, userId);
        AppendPart(builder, sessionId);
        AppendPart(builder, aspxAuth);

        foreach (var documentId in documentIds)
        {
            AppendPart(builder, documentId);
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AppendPart(StringBuilder builder, string? value)
    {
        var normalized = value ?? string.Empty;
        builder
            .Append(normalized.Length)
            .Append(':')
            .Append(normalized)
            .Append('|');
    }
}
