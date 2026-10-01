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
