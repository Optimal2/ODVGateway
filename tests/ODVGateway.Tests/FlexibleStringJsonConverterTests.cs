using System.Text.Json;
using ODVGateway.Services;

namespace ODVGateway.Tests;

public sealed class FlexibleStringJsonConverterTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new FlexibleStringJsonConverter() }
    };

    private static string? Deserialize(string json)
    {
        return JsonSerializer.Deserialize<string>(json, SerializerOptions);
    }

    [Fact]
    public void Read_StringToken_ReturnsStringAsIs()
    {
        Assert.Equal("hello", Deserialize("\"hello\""));
    }

    [Fact]
    public void Read_IntegerToken_ReturnsInvariantString()
    {
        Assert.Equal("42", Deserialize("42"));
    }

    [Fact]
    public void Read_DoubleToken_ReturnsInvariantCultureString()
    {
        Assert.Equal("3.5", Deserialize("3.5"));
    }

    [Fact]
    public void Read_TrueToken_ReturnsTrueString()
    {
        Assert.Equal("True", Deserialize("true"));
    }

    [Fact]
    public void Read_FalseToken_ReturnsFalseString()
    {
        Assert.Equal("False", Deserialize("false"));
    }

    [Fact]
    public void Read_NullToken_ReturnsNull()
    {
        Assert.Null(Deserialize("null"));
    }
}
