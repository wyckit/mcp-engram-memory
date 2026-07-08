using System.Text.Json;
using System.Text.Json.Serialization;
using McpEngramMemory.Core.Models;

namespace McpEngramMemory.Tests;

/// <summary>
/// Model-level tests for the optional <see cref="CognitiveEntry.TenantId"/> field
/// (decision 3b, Phase 1). These run in every CI environment — no database required —
/// and prove the tenant field is fully backward-compatible and round-trips through JSON.
/// </summary>
public class CognitiveEntryTenantTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new FloatArrayBase64Converter() }
    };

    [Fact]
    public void DefaultTenant_IsEmptyString()
    {
        var entry = new CognitiveEntry("id1", new[] { 1f, 2f }, "ns", "text");
        Assert.Equal(string.Empty, entry.TenantId);
    }

    [Fact]
    public void NullOrWhitespaceTenant_NormalizesToEmpty()
    {
        var a = new CognitiveEntry("id1", new[] { 1f }, "ns", tenantId: null);
        var b = new CognitiveEntry("id2", new[] { 1f }, "ns", tenantId: "   ");
        Assert.Equal(string.Empty, a.TenantId);
        Assert.Equal(string.Empty, b.TenantId);
    }

    [Fact]
    public void ExplicitTenant_IsTrimmedAndPreserved()
    {
        var entry = new CognitiveEntry("id1", new[] { 1f }, "ns", tenantId: "  acme-corp  ");
        Assert.Equal("acme-corp", entry.TenantId);
    }

    [Fact]
    public void OverLengthTenant_Throws()
    {
        var tooLong = new string('x', CognitiveEntry.MaxTenantIdLength + 1);
        Assert.Throws<ArgumentException>(() =>
            new CognitiveEntry("id1", new[] { 1f }, "ns", tenantId: tooLong));
    }

    [Fact]
    public void Tenant_RoundTripsThroughJson()
    {
        var entry = new CognitiveEntry("id1", new[] { 1f, 2f, 3f }, "ns", "text", tenantId: "tenant-42");

        var json = JsonSerializer.Serialize(entry, JsonOptions);
        Assert.Contains("\"tenantId\":\"tenant-42\"", json);

        var restored = JsonSerializer.Deserialize<CognitiveEntry>(json, JsonOptions)!;
        Assert.Equal("tenant-42", restored.TenantId);
        Assert.Equal("id1", restored.Id);
        Assert.Equal("ns", restored.Ns);
    }

    [Fact]
    public void LegacyJson_WithoutTenantId_DeserializesToEmptyTenant()
    {
        // Pre-tenant serialized form: no tenantId property at all.
        const string legacyJson =
            "{\"id\":\"old1\",\"vector\":\"AACAPwAAAEA=\",\"ns\":\"legacy\",\"text\":\"old\"," +
            "\"category\":null,\"metadata\":{},\"lifecycleState\":\"stm\"," +
            "\"createdAt\":\"2020-01-01T00:00:00+00:00\",\"lastAccessedAt\":\"2020-01-01T00:00:00+00:00\"," +
            "\"accessCount\":1,\"activationEnergy\":0,\"isSummaryNode\":false,\"sourceClusterId\":null,\"keywords\":null}";

        var restored = JsonSerializer.Deserialize<CognitiveEntry>(legacyJson, JsonOptions)!;
        Assert.Equal(string.Empty, restored.TenantId);
        Assert.Equal("old1", restored.Id);
        Assert.Equal("legacy", restored.Ns);
    }
}
