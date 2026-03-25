using System.Text.Json.Serialization;

namespace McpEngramMemory.Core.Models;

/// <summary>
/// Standard structured error response for MCP tools.
/// Provides consistent JSON shape for all tool error returns.
/// </summary>
public sealed record ToolError(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("error")] string Error)
{
    /// <summary>Create a ToolError from an exception, hiding internal details.</summary>
    public static ToolError FromException(Exception ex)
        => new("error", ex is ArgumentException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");

    /// <summary>Create a ToolError with a custom message.</summary>
    public static ToolError Create(string message)
        => new("error", message);
}
