using System.Text.Json;
using McpEngramMemory.Tools;

namespace McpEngramMemory.Tests;

/// <summary>
/// Unit coverage for the tolerant id-list coercion that keeps list-shaped tool
/// parameters from failing MCP model binding before the tool body runs.
/// </summary>
public class StringListNormalizerTests
{
    private static (bool Ok, string[]? Value, string? Error) Normalize(string? json)
    {
        JsonElement? input = json is null ? null : JsonDocument.Parse(json).RootElement;
        var ok = StringListNormalizer.TryNormalize(input, "relatedIds", out var value, out var error);
        return (ok, value, error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    [InlineData("\"  \"")]
    [InlineData("[null, null]")]
    public void AbsentOrEmptyInputYieldsNull(string? json)
    {
        var (ok, value, error) = Normalize(json);

        Assert.True(ok);
        Assert.Null(value);
        Assert.Null(error);
    }

    [Fact]
    public void JsonArrayOfStringsPassesThrough()
    {
        var (ok, value, _) = Normalize("""["a","b"]""");

        Assert.True(ok);
        Assert.Equal(new[] { "a", "b" }, value);
    }

    [Fact]
    public void SingleStringBecomesOneId()
    {
        var (ok, value, _) = Normalize("\"only-one\"");

        Assert.True(ok);
        Assert.Equal(new[] { "only-one" }, value);
    }

    [Fact]
    public void CommaSeparatedStringIsSplitAndTrimmed()
    {
        var (ok, value, _) = Normalize("\" a , b ,, c \"");

        Assert.True(ok);
        Assert.Equal(new[] { "a", "b", "c" }, value);
    }

    [Fact]
    public void BlanksInsideArrayAreDropped()
    {
        var (ok, value, _) = Normalize("""["a","","  ",null,"b"]""");

        Assert.True(ok);
        Assert.Equal(new[] { "a", "b" }, value);
    }

    [Theory]
    [InlineData("42", "42")]
    [InlineData("true", "true")]
    public void ScalarsBecomeTheirLiteralText(string json, string expected)
    {
        var (ok, value, _) = Normalize(json);

        Assert.True(ok);
        Assert.Equal(new[] { expected }, value);
    }

    [Fact]
    public void NumericIdsInsideArrayBecomeLiteralText()
    {
        var (ok, value, _) = Normalize("""["a",7]""");

        Assert.True(ok);
        Assert.Equal(new[] { "a", "7" }, value);
    }

    [Theory]
    [InlineData("""{"id":"a"}""", "object")]
    [InlineData("""[{"id":"a"}]""", "object")]
    [InlineData("""[["a"]]""", "array")]
    public void UnusableShapesFailWithAMessageNamingParameterAndShape(string json, string mentioned)
    {
        var (ok, value, error) = Normalize(json);

        Assert.False(ok);
        Assert.Null(value);
        Assert.NotNull(error);
        Assert.Contains("relatedIds", error);
        Assert.Contains("JSON array of ids", error);
        Assert.Contains("comma-separated string", error);
        Assert.Contains(mentioned, error);
    }
}
