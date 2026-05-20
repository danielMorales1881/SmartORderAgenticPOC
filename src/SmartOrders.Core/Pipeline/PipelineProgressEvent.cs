using System.Text.Json.Serialization;

namespace SmartOrders.Core.Pipeline;

/// <summary>
/// A single progress event emitted by the pipeline and streamed to the client via SSE.
/// Type values: stage_start | stage_done | tool_call | tool_result | awaiting_confirmation | error
/// </summary>
public sealed record PipelineProgressEvent(
    [property: JsonPropertyName("type")]    string Type,
    [property: JsonPropertyName("agent")]   string Agent,
    [property: JsonPropertyName("message")] string Message,
    /// <summary>
    /// Optional JSON payload:
    ///   awaiting_confirmation → validatedOrdersJson array string
    ///   stage_done (IntentAgent) → intent count
    /// </summary>
    [property: JsonPropertyName("data")]    string? Data = null
);
