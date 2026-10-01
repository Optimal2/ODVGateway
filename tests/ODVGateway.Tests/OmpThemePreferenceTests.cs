using ODVGateway.Services;

namespace ODVGateway.Tests;

// The gateway's status/error pages follow the shared OMP theme contract: the
// OMP_THEME_PREFERENCE cookie carries a URI-encoded JSON object
// {"version":1,"mode":"system|light|dark","revision":"..."} and anything that
// does not match that exact shape falls back to "system".
public sealed class OmpThemePreferenceTests
{
    private static string Encode(string json) => Uri.EscapeDataString(json);

    [Theory]
    [InlineData("system")]
    [InlineData("light")]
    [InlineData("dark")]
    public void ParseMode_ValidPreference_ReturnsTheStoredMode(string mode)
    {
        var cookie = Encode($$"""{"version":1,"mode":"{{mode}}","revision":"mabc123"}""");

        Assert.Equal(mode, OmpThemePreference.ParseMode(cookie));
    }

    [Fact]
    public void ParseMode_UnencodedJson_StillParses()
    {
        // Defensive: the contract encodes the JSON, but a hand-set cookie should
        // not flip the page back to system.
        Assert.Equal("dark", OmpThemePreference.ParseMode("""{"version":1,"mode":"dark","revision":"r1"}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseMode_MissingCookie_FallsBackToSystem(string? cookie)
    {
        Assert.Equal("system", OmpThemePreference.ParseMode(cookie));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("%7Bbroken")]
    [InlineData("%")]
    public void ParseMode_MalformedValue_FallsBackToSystem(string cookie)
    {
        Assert.Equal("system", OmpThemePreference.ParseMode(cookie));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseMode_OversizedValue_FallsBackToSystem(bool encoded)
    {
        var json = "{\"version\":1,\"mode\":\"dark\",\"revision\":\"" + new string('a', 65_536) + "\"}";

        Assert.Equal("system", OmpThemePreference.ParseMode(encoded ? Encode(json) : json));
    }

    [Theory]
    [InlineData(4095, "dark")]
    [InlineData(4096, "dark")]
    [InlineData(4097, "system")]
    public void ParseMode_CookieLengthLimit_IsInclusive(int length, string expected)
    {
        var cookie = Encode("""{"version":1,"mode":"dark"}""").PadRight(length);

        Assert.Equal(expected, OmpThemePreference.ParseMode(cookie));
    }

    [Fact]
    public void ParseMode_DeeplyNestedValue_FallsBackToSystem()
    {
        var json = "{\"version\":1,\"mode\":\"dark\",\"revision\":"
            + new string('[', 128) + "0" + new string(']', 128) + "}";

        Assert.Equal("system", OmpThemePreference.ParseMode(Encode(json)));
    }

    [Theory]
    [InlineData("%7")]
    [InlineData("%GG")]
    [InlineData("%C0%AF")]
    [InlineData("{\"version\":1,\"mode\":\"dark\"")]
    [InlineData("{\"version\":1,\"mode\":\"\\uD800\"}")]
    [InlineData("{\"version\":1,\"mode\":\"\\uDC00\"}")]
    [InlineData("{\"version\":1,\"mode\":\"\\uZZZZ\"}")]
    public void ParseMode_InvalidEncodingOrJson_FallsBackToSystem(string cookie)
    {
        Assert.Equal("system", OmpThemePreference.ParseMode(cookie));
    }

    [Fact]
    public void ParseMode_UnknownVersion_IsIgnored()
    {
        var cookie = Encode("""{"version":2,"mode":"dark","revision":"r1"}""");

        Assert.Equal("system", OmpThemePreference.ParseMode(cookie));
    }

    [Fact]
    public void ParseMode_UnknownMode_IsIgnored()
    {
        var cookie = Encode("""{"version":1,"mode":"sepia","revision":"r1"}""");

        Assert.Equal("system", OmpThemePreference.ParseMode(cookie));
    }

    [Fact]
    public void ParseMode_ModeIsCaseSensitive()
    {
        var cookie = Encode("""{"version":1,"mode":"Dark","revision":"r1"}""");

        Assert.Equal("system", OmpThemePreference.ParseMode(cookie));
    }

    [Fact]
    public void ParseMode_MissingRevision_StillAppliesTheMode()
    {
        // Revision orders cookie vs localStorage; the server-rendered page only
        // needs the mode, so a missing revision must not discard a valid choice.
        var cookie = Encode("""{"version":1,"mode":"light"}""");

        Assert.Equal("light", OmpThemePreference.ParseMode(cookie));
    }
}
