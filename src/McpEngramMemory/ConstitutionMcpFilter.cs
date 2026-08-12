using System.Security.Cryptography;
using System.Text.Json;
using McpEngramMemory.Core.Models;
using McpEngramMemory.Core.Models.Constitution;
using McpEngramMemory.Core.Services.Constitution;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpEngramMemory;

/// <summary>Applies the Core Constitution around every MCP tool call without coupling Core to MCP.</summary>
public static class ConstitutionMcpFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => async (request, cancellationToken) =>
        {
            var services = request.Server.Services
                ?? throw new InvalidOperationException("The MCP server has no service provider.");
            var kernel = services.GetRequiredService<ConstitutionKernel>();
            var principal = services.GetRequiredService<IPrincipalContext>();
            var operation = CreateEnvelope(request, principal);

            var precondition = await kernel.EvaluateAndAuditAsync(
                operation, ConstitutionPhase.Precondition, cancellationToken).ConfigureAwait(false);
            if (precondition.Outcome != ConstitutionOutcome.Allow)
                return Denied(precondition);

            try
            {
                var result = await next(request, cancellationToken).ConfigureAwait(false);
                // Postconditions are detection/audit, not authorization: the tool may already
                // have committed. A denial is therefore recorded durably but never disguised as
                // a failed tool call without a rollback transaction.
                await kernel.EvaluateAndAuditAsync(
                    operation, ConstitutionPhase.Postcondition, cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch
            {
                // The attempted operation and its precondition decision are already durable in audit.
                // Preserve the SDK's normal exception-to-tool-error handling for the underlying failure.
                throw;
            }
        };

    internal static OperationEnvelope CreateEnvelope(
        RequestContext<CallToolRequestParams> request,
        IPrincipalContext principal)
    {
        var name = request.Params.Name?.Trim() ?? string.Empty;
        var requestId = request.JsonRpcRequest.Id.ToString();
        if (requestId.Length == 0)
            requestId = Guid.NewGuid().ToString("N");
        return new OperationEnvelope(
            $"mcp:{requestId}:{name}",
            MapOperation(name),
            principal.TenantId,
            principal.AgentId,
            $"MCP tool call: {name}",
            Array.Empty<OperationArtifactReference>(),
            null,
            HashArguments(request.Params.Arguments),
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["adapter"] = "mcp",
                ["tool"] = name
            });
    }

    internal static CognitiveOperationKind MapOperation(string toolName)
        => toolName switch
        {
            "store_memory" or "store_batch" or "remember" or "reflect" => CognitiveOperationKind.WriteMemory,
            "delete_memory" or "purge_debates" => CognitiveOperationKind.DeleteMemory,
            "synthesize_memories" => CognitiveOperationKind.ProposeKnowledge,
            "promote_knowledge" => CognitiveOperationKind.PromoteKnowledge,
            "recall" or "search_memory" or "cross_search" or "deep_recall" or "spectral_recall" => CognitiveOperationKind.Retrieve,
            "get_context_block" => CognitiveOperationKind.CompileContext,
            "share_namespace" or "unshare_namespace" or "configure_decay" => CognitiveOperationKind.AdministerGovernance,
            _ => CognitiveOperationKind.ReadMemory
        };

    internal static string HashArguments(IDictionary<string, JsonElement>? arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in (arguments ?? new Dictionary<string, JsonElement>())
                         .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                WriteCanonical(writer, value);
            }
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(value => value.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var value in element.EnumerateArray())
                    WriteCanonical(writer, value);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static CallToolResult Denied(ConstitutionDecision decision)
        => new()
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = $"Constitution {decision.Outcome}: " + string.Join("; ",
                        decision.Findings.Select(value => $"{value.Code}: {value.Message}"))
                }
            ]
        };
}
