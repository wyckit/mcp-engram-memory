using System.Text.Json;
using McpEngramMemory;
using McpEngramMemory.Core.Models.Constitution;

namespace McpEngramMemory.Tests;

public sealed class ConstitutionMcpFilterTests
{
    [Theory]
    [InlineData("remember", CognitiveOperationKind.WriteMemory)]
    [InlineData("delete_memory", CognitiveOperationKind.DeleteMemory)]
    [InlineData("recall", CognitiveOperationKind.Retrieve)]
    [InlineData("get_context_block", CognitiveOperationKind.CompileContext)]
    [InlineData("share_namespace", CognitiveOperationKind.AdministerGovernance)]
    [InlineData("cognitive_stats", CognitiveOperationKind.ReadMemory)]
    public void ToolNamesMapToStableCognitiveOperations(string name, CognitiveOperationKind expected)
        => Assert.Equal(expected, ConstitutionMcpFilter.MapOperation(name));

    [Fact]
    public void ArgumentHashIsCanonicalAcrossObjectPropertyOrdering()
    {
        using var left = JsonDocument.Parse("{\"outer\":{\"b\":2,\"a\":1},\"z\":true}");
        using var right = JsonDocument.Parse("{\"z\":true,\"outer\":{\"a\":1,\"b\":2}}");
        var leftArgs = left.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        var rightArgs = right.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        Assert.Equal(ConstitutionMcpFilter.HashArguments(leftArgs),
            ConstitutionMcpFilter.HashArguments(rightArgs));
    }
}
