using System.Text;
using System.Text.Json.Nodes;

namespace Csmfd.Tests.Protocol;

/// <summary>Access to the shared JSON fixtures (also the reference examples for the iPad side).</summary>
internal static class Fixture
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Protocol", name));

    public static byte[] ReadBytes(string name) => Encoding.UTF8.GetBytes(Read(name));

    /// <summary>Semantic comparison: key order and whitespace do not matter, values and types do.</summary>
    public static void AssertJsonEqual(string expectedJson, byte[] actualUtf8)
    {
        var expected = JsonNode.Parse(expectedJson);
        var actual = JsonNode.Parse(actualUtf8);
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"Expected:{Environment.NewLine}{expectedJson}{Environment.NewLine}Actual:{Environment.NewLine}{Encoding.UTF8.GetString(actualUtf8)}");
    }
}
