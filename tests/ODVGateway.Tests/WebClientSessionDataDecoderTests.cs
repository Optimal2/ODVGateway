using System.Text;
using ODVGateway.Services;

namespace ODVGateway.Tests;

public sealed class WebClientSessionDataDecoderTests
{
    private static string EncodeStandardBase64(string json)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static string EncodeBase64UrlWithoutPadding(string json)
    {
        return EncodeStandardBase64(json)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    [Fact]
    public void Decode_StandardBase64Json_ReturnsSessionData()
    {
        var token = EncodeStandardBase64(
            """{"userId":"u1","sessionId":"s1","aspxAuth":"auth","caseIds":["c1","c2"]}""");

        var data = WebClientSessionDataDecoder.Decode(token);

        Assert.Equal("u1", data.UserId);
        Assert.Equal("s1", data.SessionId);
        Assert.Equal("auth", data.AspxAuth);
        Assert.Equal(["c1", "c2"], data.CaseIds);
    }

    [Fact]
    public void Decode_Base64UrlWithoutPadding_ReturnsSameData()
    {
        const string json = """{"userId":"u1","sessionId":"s1"}""";
        var standard = EncodeStandardBase64(json);
        var base64Url = EncodeBase64UrlWithoutPadding(json);

        var data = WebClientSessionDataDecoder.Decode(base64Url);

        Assert.Equal("u1", data.UserId);
        Assert.Equal("s1", data.SessionId);
        // Guard the premise: this payload must actually exercise the
        // base64url character replacement and/or padding restoration.
        Assert.NotEqual(standard, base64Url);
    }

    [Fact]
    public void Decode_TokenWithSpacesAndWhitespace_NormalizesAndDecodes()
    {
        var standard = EncodeStandardBase64("""{"userId":"u1"}""");
        // Web clients may pass '+' through a query string, arriving as spaces.
        var mangled = "  " + standard.Replace('+', ' ') + "  ";

        var data = WebClientSessionDataDecoder.Decode(mangled);

        Assert.Equal("u1", data.UserId);
    }

    [Fact]
    public void Decode_NumericStringFields_CoercesToString()
    {
        var token = EncodeStandardBase64("""{"userId":12345,"sessionId":true}""");

        var data = WebClientSessionDataDecoder.Decode(token);

        Assert.Equal("12345", data.UserId);
        Assert.Equal(bool.TrueString, data.SessionId);
    }

    [Fact]
    public void Decode_MissingFields_ReturnsDefaults()
    {
        var token = EncodeStandardBase64("{}");

        var data = WebClientSessionDataDecoder.Decode(token);

        Assert.Null(data.UserId);
        Assert.Null(data.SessionId);
        Assert.Null(data.AspxAuth);
        Assert.Empty(data.CaseIds);
    }

    [Fact]
    public void Decode_InvalidBase64_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => WebClientSessionDataDecoder.Decode("!!!not-base64!!!"));
    }

    [Fact]
    public void Decode_EmptyJsonObject_ReturnsEmptyInstance()
    {
        // Decoding valid base64 of an empty object must not throw; null JSON
        // ("null") would throw InvalidDataException instead.
        var token = EncodeStandardBase64("null");

        Assert.Throws<InvalidDataException>(() => WebClientSessionDataDecoder.Decode(token));
    }
}
