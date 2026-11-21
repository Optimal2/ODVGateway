using System.Text.Json;

namespace ODVGateway.Tests;

// The OMP artifact baseline (Packaging/appsettings.json) and the development
// sample (appsettings.json) must expose the same ODVGateway keys: a key that
// exists only in one file is invisible to whoever edits the other one (B053),
// and a shipped pin can silently defeat a "follow" code default (B054).
public sealed class ShippedConfigurationParityTests
{
    private static JsonDocument LoadSample(string fileName)
    {
        var path = Path.Join(AppContext.BaseDirectory, fileName);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static List<string> SortedKeyPaths(JsonElement element, string prefix = "")
    {
        var paths = new List<string>();
        if (element.ValueKind != JsonValueKind.Object)
        {
            return paths;
        }

        foreach (var property in element.EnumerateObject())
        {
            var path = string.IsNullOrEmpty(prefix) ? property.Name : prefix + "." + property.Name;
            paths.Add(path);
            paths.AddRange(SortedKeyPaths(property.Value, path));
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in property.Value.EnumerateArray())
                {
                    paths.AddRange(SortedKeyPaths(item, $"{path}[{index}]"));
                    index++;
                }
            }
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    [Fact]
    public void ShippedSamples_ShareTheSameGatewayKeySet()
    {
        using var dev = LoadSample("appsettings.json");
        using var packaging = LoadSample("packaging.appsettings.json");

        var devKeys = SortedKeyPaths(dev.RootElement.GetProperty("ODVGateway"));
        var packagingKeys = SortedKeyPaths(packaging.RootElement.GetProperty("ODVGateway"));

        Assert.Equal(devKeys, packagingKeys);
    }

    [Fact]
    public void PackagingBaseline_ContainsPrepHandoffWaitMilliseconds()
    {
        using var packaging = LoadSample("packaging.appsettings.json");
        var gateway = packaging.RootElement.GetProperty("ODVGateway");

        Assert.True(gateway.TryGetProperty("prepHandoffWaitMilliseconds", out var value));
        Assert.Equal(5000, value.GetInt32());
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("packaging.appsettings.json")]
    public void ShippedSamples_LeaveMaxSourceProxyBytesToFollowFrameBytes(string fileName)
    {
        // 0 means "follow maxSourcePackFrameBytes" (GetMaxSourceProxyBytes).
        // An explicit byte pin here would make that code default unreachable.
        using var sample = LoadSample(fileName);
        var gateway = sample.RootElement.GetProperty("ODVGateway");

        Assert.Equal(0, gateway.GetProperty("maxSourceProxyBytes").GetInt64());
        Assert.Equal(67108864, gateway.GetProperty("maxSourcePackFrameBytes").GetInt64());
    }
}
