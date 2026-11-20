using System.Collections.ObjectModel;
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
    private static readonly IReadOnlyDictionary<string, CognitiveOperationKind> ToolOperations =
        new ReadOnlyDictionary<string, CognitiveOperationKind>(
            new Dictionary<string, CognitiveOperationKind>(StringComparer.Ordinal)
            {
                // Core memory tools
                ["store_memory"] = CognitiveOperationKind.WriteMemory,
                ["store_batch"] = CognitiveOperationKind.WriteMemory,
                ["search_memory"] = CognitiveOperationKind.Retrieve,
                ["delete_memory"] = CognitiveOperationKind.DeleteMemory,

                // Composite tools
                ["remember"] = CognitiveOperationKind.WriteMemory,
                ["recall"] = CognitiveOperationKind.Retrieve,
                ["reflect"] = CognitiveOperationKind.WriteMemory,
                ["get_context_block"] = CognitiveOperationKind.CompileContext,

                // Admin tools
                ["get_memory"] = CognitiveOperationKind.ReadMemory,
                ["cognitive_stats"] = CognitiveOperationKind.ReadMemory,
                ["engram_status"] = CognitiveOperationKind.ReadMemory,
                ["purge_debates"] = CognitiveOperationKind.DeleteMemory,

                // Multi-agent tools
                ["cross_search"] = CognitiveOperationKind.Retrieve,
                ["share_namespace"] = CognitiveOperationKind.AdministerGovernance,
                ["unshare_namespace"] = CognitiveOperationKind.AdministerGovernance,
                ["list_shared"] = CognitiveOperationKind.ReadMemory,
                ["whoami"] = CognitiveOperationKind.ReadMemory,

                // Graph tools
                ["link_memories"] = CognitiveOperationKind.WriteMemory,
                ["unlink_memories"] = CognitiveOperationKind.DeleteMemory,
                ["get_neighbors"] = CognitiveOperationKind.ReadMemory,
                ["traverse_graph"] = CognitiveOperationKind.ReadMemory,

                // Lifecycle tools
                ["promote_memory"] = CognitiveOperationKind.WriteMemory,
                ["deep_recall"] = CognitiveOperationKind.Retrieve,
                ["memory_feedback"] = CognitiveOperationKind.WriteMemory,
                ["configure_decay"] = CognitiveOperationKind.AdministerGovernance,

                // Cluster tools
                ["create_cluster"] = CognitiveOperationKind.WriteMemory,
                ["update_cluster"] = CognitiveOperationKind.WriteMemory,
                ["store_cluster_summary"] = CognitiveOperationKind.WriteMemory,
                ["get_cluster"] = CognitiveOperationKind.ReadMemory,
                ["list_clusters"] = CognitiveOperationKind.ReadMemory,

                // Intelligence tools
                ["detect_duplicates"] = CognitiveOperationKind.ReadMemory,
                ["find_contradictions"] = CognitiveOperationKind.ReadMemory,
                ["uncollapse_cluster"] = CognitiveOperationKind.WriteMemory,
                ["list_collapse_history"] = CognitiveOperationKind.ReadMemory,
                ["merge_memories"] = CognitiveOperationKind.WriteMemory,

                // Accretion tools
                ["get_pending_collapses"] = CognitiveOperationKind.ReadMemory,
                ["collapse_cluster"] = CognitiveOperationKind.WriteMemory,
                ["dismiss_collapse"] = CognitiveOperationKind.DeleteMemory,

                // Memory diffusion & spectral retrieval tools
                ["compute_diffusion_basis"] = CognitiveOperationKind.Retrieve,
                ["diffusion_stats"] = CognitiveOperationKind.ReadMemory,
                ["invalidate_diffusion"] = CognitiveOperationKind.AdministerGovernance,
                ["spectral_recall"] = CognitiveOperationKind.Retrieve,

                // Governed learning & synthesis tools
                ["promote_knowledge"] = CognitiveOperationKind.PromoteKnowledge,
                ["synthesize_memories"] = CognitiveOperationKind.ProposeKnowledge,

                // Debate tools
                ["consult_expert_panel"] = CognitiveOperationKind.WriteMemory,
                ["map_debate_graph"] = CognitiveOperationKind.WriteMemory,
                ["resolve_debate"] = CognitiveOperationKind.WriteMemory,

                // Expert routing tools
                ["dispatch_task"] = CognitiveOperationKind.Retrieve,
                ["create_expert"] = CognitiveOperationKind.AdministerGovernance,
                ["link_to_parent"] = CognitiveOperationKind.AdministerGovernance,
                ["get_domain_tree"] = CognitiveOperationKind.ReadMemory,

                // Maintenance & visualization tools
                ["rebuild_embeddings"] = CognitiveOperationKind.AdministerGovernance,
                ["compression_stats"] = CognitiveOperationKind.ReadMemory,
                ["get_graph_snapshot"] = CognitiveOperationKind.ReadMemory,

                // Benchmark & metrics tools
                ["run_benchmark"] = CognitiveOperationKind.AdministerGovernance,
                ["run_agent_outcome_benchmark"] = CognitiveOperationKind.AdministerGovernance,
                ["run_live_agent_outcome_benchmark"] = CognitiveOperationKind.AdministerGovernance,
                ["compare_live_agent_outcome_artifacts"] = CognitiveOperationKind.ReadMemory,
                ["check_for_regression"] = CognitiveOperationKind.ReadMemory,
                ["get_metrics"] = CognitiveOperationKind.ReadMemory,
                ["reset_metrics"] = CognitiveOperationKind.AdministerGovernance,
                ["run_mrcr_benchmark"] = CognitiveOperationKind.AdministerGovernance,
                ["compare_mrcr_artifacts"] = CognitiveOperationKind.ReadMemory
            });

    internal static IReadOnlyDictionary<string, CognitiveOperationKind> ToolOperationMap => ToolOperations;

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => (request, cancellationToken) =>
        {
            var services = request.Server.Services
                ?? throw new InvalidOperationException("The MCP server has no service provider.");
            return InvokeAsync(
                services,
                request.Params.Name,
                request.Params.Arguments,
                GetRequestId(request),
                ct => next(request, ct),
                cancellationToken);
        };

    internal static async ValueTask<CallToolResult> InvokeAsync(
        IServiceProvider services,
        string? rawToolName,
        IDictionary<string, JsonElement>? arguments,
        string requestId,
        Func<CancellationToken, ValueTask<CallToolResult>> next,
        CancellationToken cancellationToken = default)
    {
        var kernel = services.GetRequiredService<ConstitutionKernel>();
        var principal = services.GetRequiredService<IPrincipalContext>();
        var name = rawToolName?.Trim() ?? string.Empty;
        var resolvedRequestId = string.IsNullOrEmpty(requestId) ? Guid.NewGuid().ToString("N") : requestId;

        if (!TryMapOperation(name, out var operationKind))
        {
            var operationId = $"mcp:{resolvedRequestId}:{name}";
            var provider = services.GetService<IConstitutionProvider>();
            var versionHashes = provider?.Current.VersionHashes ?? Array.Empty<string>();
            var denied = new ConstitutionDecision(
                operationId,
                ConstitutionPhase.Precondition,
                ConstitutionOutcome.Deny,
                [
                    new ConstitutionFinding(
                        "constitution.mcp-filter",
                        "unmapped-mcp-tool",
                        ConstitutionOutcome.Deny,
                        $"MCP tool '{name}' is not mapped to a governed cognitive operation.")
                ],
                versionHashes);

            var auditStore = services.GetService<IConstitutionAuditStore>();
            if (auditStore is not null)
            {
                await auditStore.AppendAsync(new ConstitutionAuditRecord(
                    0,
                    $"{operationId}:{ConstitutionPhase.Precondition}:{Guid.NewGuid():N}",
                    operationId,
                    principal.TenantId,
                    principal.AgentId,
                    ConstitutionPhase.Precondition,
                    ConstitutionOutcome.Deny,
                    versionHashes,
                    ["unmapped-mcp-tool"],
                    DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            }

            return Denied(denied);
        }

        var operation = CreateEnvelope(principal, name, resolvedRequestId, operationKind, arguments);

        var precondition = await kernel.EvaluateAndAuditAsync(
            operation, ConstitutionPhase.Precondition, cancellationToken).ConfigureAwait(false);
        if (precondition.Outcome != ConstitutionOutcome.Allow)
            return Denied(precondition);

        try
        {
            var result = await next(cancellationToken).ConfigureAwait(false);
            // Postconditions are detection/audit, not authorization: the tool may already
            // have committed. A denial is therefore recorded durably but never disguised as
            // a failed tool call without a rollback transaction.
            await kernel.EvaluateAndAuditAsync(
                operation, ConstitutionPhase.Postcondition, cancellationToken).ConfigureAwait(false);
            return NormalizeToolErrorResult(result);
        }
        catch
        {
            // The attempted operation and its precondition decision are already durable in audit.
            // Preserve the SDK's normal exception-to-tool-error handling for the underlying failure.
            throw;
        }
    }

    internal static OperationEnvelope CreateEnvelope(
        RequestContext<CallToolRequestParams> request,
        IPrincipalContext principal)
    {
        var name = request.Params.Name?.Trim() ?? string.Empty;
        return CreateEnvelope(principal, name, GetRequestId(request), MapOperation(name), request.Params.Arguments);
    }

    private static OperationEnvelope CreateEnvelope(
        IPrincipalContext principal,
        string name,
        string requestId,
        CognitiveOperationKind operationKind,
        IDictionary<string, JsonElement>? arguments)
        => new(
            $"mcp:{requestId}:{name}",
            operationKind,
            principal.TenantId,
            principal.AgentId,
            $"MCP tool call: {name}",
            Array.Empty<OperationArtifactReference>(),
            null,
            HashArguments(arguments),
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["adapter"] = "mcp",
                ["tool"] = name
            });

    private static string GetRequestId(RequestContext<CallToolRequestParams> request)
    {
        var requestId = request.JsonRpcRequest.Id.ToString();
        return requestId.Length == 0 ? Guid.NewGuid().ToString("N") : requestId;
    }

    internal static bool TryMapOperation(string? toolName, out CognitiveOperationKind operation)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            operation = default;
            return false;
        }

        return ToolOperations.TryGetValue(toolName.Trim(), out operation);
    }

    internal static CognitiveOperationKind MapOperation(string toolName)
    {
        if (TryMapOperation(toolName, out var operation))
            return operation;

        throw new ArgumentOutOfRangeException(
            nameof(toolName),
            toolName,
            $"MCP tool '{toolName}' is not mapped to a governed cognitive operation.");
    }

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

    /// <summary>
    /// Standardize MCP wire error signaling so tools returning either <see cref="ToolError"/> /
    /// <c>{"status":"error..."}</c> payloads or <c>"Error: ..."</c> strings always surface
    /// <see cref="CallToolResult.IsError"/> = <c>true</c> to MCP clients.
    /// </summary>
    internal static CallToolResult NormalizeToolErrorResult(CallToolResult result)
    {
        if (result.IsError == true || result.Content is null || result.Content.Count == 0)
            return result;

        if (result.Content[0] is not TextContentBlock { Text: { Length: > 0 } text })
            return result;

        if (IsErrorPayloadText(text))
            result.IsError = true;

        return result;
    }

    private static bool IsErrorPayloadText(string text)
    {
        var span = text.AsSpan().TrimStart();
        if (span.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            return true;

        if (span.Length == 0 || span[0] != '{')
            return false;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                (doc.RootElement.TryGetProperty("status", out var statusEl) ||
                 doc.RootElement.TryGetProperty("Status", out statusEl)) &&
                statusEl.ValueKind == JsonValueKind.String)
            {
                var status = statusEl.GetString();
                if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase) ||
                    (status is not null && status.StartsWith("error_", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON text starting with '{' is treated as normal content.
        }

        return false;
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
