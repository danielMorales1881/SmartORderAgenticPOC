using System.Text.Json.Serialization;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using SmartOrders.Infrastructure.Configuration;

namespace SmartOrders.Infrastructure.Agents;

/// <summary>
/// Factory methods for the three LLM pipeline agents.
/// ValidationAgent is NOT here — it is a pure C# agent (see ValidationService).
/// Equivalent to agents/intent_agent.py, mapping_agent.py, submission_agent.py.
/// </summary>
public static class AgentFactory
{
    /// <summary>
    /// No tools — uses structured output (ResponseFormat) to enforce JSON schema natively.
    /// Equivalent to IntentAgent with output_schema=OrderIntentListSchema in Python.
    /// </summary>
    public static ChatCompletionAgent CreateIntentAgent(Kernel kernel, string instruction) =>
        new()
        {
            Name = "IntentAgent",
            Description = "Extracts structured order intents from clinical text.",
            Instructions = instruction,
            Kernel = kernel,
            // ThinkingBudget=0 disables thinking on this thinking model — IntentAgent is simple extraction.
            // MaxOutputTokens=2048 matches the global default; 512 was too small for thinking models.
            Arguments = new KernelArguments(new GeminiExecutionSettings { JsonMode = true, MaxOutputTokens = 2048, ThinkingBudget = 0 }),
        };

    /// <summary>
    /// Tools: search_orders, map_diagnosis, apply_order_defaults.
    /// Equivalent to MappingAgent in Python (output format described in prompt, not output_schema).
    /// </summary>
    public static ChatCompletionAgent CreateMappingAgent(Kernel kernel, string instruction) =>
        new()
        {
            Name = "MappingAgent",
            Description = "Maps order intents to TouchWorks catalog entries.",
            Instructions = instruction,
            Kernel = kernel,
            // max_output_tokens=2048 matches Python MappingAgent generate_content_config
            Arguments = new KernelArguments(new GeminiExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
                MaxOutputTokens = 2048,
            }),
        };

    /// <summary>
    /// Tools: submit_order.
    /// Implements ADK HITL pattern: presents orders, waits for provider confirmation, then submits.
    /// Equivalent to SubmissionAgent in Python (output format described in prompt, not output_schema).
    /// </summary>
    public static ChatCompletionAgent CreateSubmissionAgent(Kernel kernel, string instruction) =>
        new()
        {
            Name = "SubmissionAgent",
            Description = "Presents validated orders to the provider for confirmation and submits approved orders to TW.",
            Instructions = instruction,
            Kernel = kernel,
            Arguments = new KernelArguments(new PromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
            }),
        };
}

/// <summary>
/// Structured output schema for IntentAgent.
/// Equivalent to OrderIntentListSchema in schemas.py.
/// </summary>
public sealed class OrderIntentListResponse
{
    [JsonPropertyName("orders")]
    public List<OrderIntentResponse> Orders { get; set; } = [];
}

public sealed class OrderIntentResponse
{
    [JsonPropertyName("orderable_hint")]
    public string OrderableHint { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public string Priority { get; set; } = "Routine";

    [JsonPropertyName("diagnosis_hint")]
    public string? DiagnosisHint { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } = 1.0;
}
