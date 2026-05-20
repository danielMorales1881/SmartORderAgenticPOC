using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SmartOrders.Infrastructure.Plugins;

namespace SmartOrders.Infrastructure.Agents;

/// <summary>
/// Factory methods for the four LLM pipeline agents.
/// ValidationAgent is NOT here — it is a pure C# service (see ValidationService).
/// Equivalent to agents/intent_agent.py, mapping_agent.py, submission_agent.py.
///
/// Each method accepts the raw (unconfigured) base IChatClient and builds the correct
/// per-agent middleware pipeline (schema enforcement, UseFunctionInvocation) internally.
/// Callers only need to supply the base client, prompts, and plugin instances.
/// </summary>
public static class AgentFactory
{
    // -----------------------------------------------------------------------
    // Output schemas — Gemini-native type names (ARRAY, OBJECT, STRING, NUMBER).
    // Enforced at the HTTP layer so the model cannot deviate from the contract
    // regardless of what its system prompt says.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Response schema for IntentAgent.
    /// Equivalent to OrderIntentListSchema in schemas.py.
    /// </summary>
    private static readonly JsonElement s_orderIntentSchema = JsonSerializer.SerializeToElement(new
    {
        type = "ARRAY",
        items = new
        {
            type = "OBJECT",
            properties = new Dictionary<string, object>
            {
                ["orderable_hint"] = new { type = "STRING", description = "Clinical name of the orderable item" },
                ["priority"]       = new { type = "STRING", description = "STAT, Urgent, or Routine" },
                ["diagnosis_hint"] = new { type = "STRING", description = "Free-text diagnosis context, nullable", nullable = true },
                ["confidence"]     = new { type = "NUMBER", description = "0.0 to 1.0 confidence score" },
            },
            required = new[] { "orderable_hint", "priority", "confidence" }
        }
    });

    /// <summary>
    /// Response schema for MappingAgent — enforces exact field names on the synthesis turn
    /// (the turn after all tool calls complete). Without this, the model uses its own
    /// field-name conventions (e.g. order_id, diagnoses[]) even when the prompt says otherwise.
    /// </summary>
    private static readonly JsonElement s_mappedOrderSchema = JsonSerializer.SerializeToElement(new
    {
        type = "ARRAY",
        items = new
        {
            type = "OBJECT",
            properties = new Dictionary<string, object>
            {
                ["item_id"]         = new { type = "STRING", nullable = true },
                ["display_name"]    = new { type = "STRING" },
                ["order_category"]  = new { type = "STRING" },
                ["icd10_code"]      = new { type = "STRING", nullable = true },
                ["priority"]        = new { type = "STRING" },
                ["to_be_done_date"] = new { type = "STRING" },
                ["original_intent"] = new { type = "STRING" },
                ["reason"]          = new { type = "STRING", nullable = true },
                ["dose"]            = new { type = "STRING", nullable = true },
                ["route"]           = new { type = "STRING", nullable = true },
                ["frequency"]       = new { type = "STRING", nullable = true },
            },
            required = new[] { "display_name", "order_category", "priority", "to_be_done_date", "original_intent" }
        }
    });

    // -----------------------------------------------------------------------
    // Factory methods
    // -----------------------------------------------------------------------

    /// <summary>
    /// No tools — structured JSON output enforced via responseSchema.
    /// Equivalent to IntentAgent with output_schema=OrderIntentListSchema in Python.
    /// </summary>
    public static AIAgent CreateIntentAgent(IChatClient baseClient, string instruction)
    {
        var client = baseClient.AsBuilder()
            .ConfigureOptions(opts =>
            {
                opts.ResponseFormat = ChatResponseFormat.Json;
                opts.AdditionalProperties ??= [];
                opts.AdditionalProperties["response_schema"] = s_orderIntentSchema;
            })
            .Build();
        return client.AsAIAgent(name: "IntentAgent", instructions: instruction);
    }

    /// <summary>
    /// Tools: search_orders, map_diagnosis, apply_order_defaults.
    /// responseSchema enforces the exact field names on the final synthesis turn (after
    /// all tool calls complete). Sending tools alongside responseSchema causes Gemini to
    /// ignore the schema — GeminiChatCompletionService nullifies functionDeclarations on
    /// the synthesis turn to work around this.
    /// Equivalent to MappingAgent in Python.
    /// </summary>
    public static AIAgent CreateMappingAgent(
        IChatClient baseClient,
        string instruction,
        SearchPlugin searchPlugin,
        MappingPlugin mappingPlugin)
    {
        List<AITool> tools =
        [
            AIFunctionFactory.Create(
                (Func<string, double?, int, CancellationToken, Task<string>>)searchPlugin.SearchOrdersAsync),
            AIFunctionFactory.Create(
                (Func<string, Task<string>>)mappingPlugin.MapDiagnosisAsync),
            AIFunctionFactory.Create(
                (Func<string, string?, Task<string>>)mappingPlugin.ApplyOrderDefaultsAsync),
        ];
        var client = baseClient.AsBuilder()
            .ConfigureOptions(opts =>
            {
                opts.ResponseFormat = ChatResponseFormat.Json;
                opts.AdditionalProperties ??= [];
                opts.AdditionalProperties["response_schema"] = s_mappedOrderSchema;
            })
            .UseFunctionInvocation()
            .Build();
        return client.AsAIAgent(name: "MappingAgent", instructions: instruction, tools: tools);
    }

    /// <summary>
    /// Presentation-only agent — no tools. Used for the HITL first turn where the agent
    /// summarises validated orders and asks the provider for confirmation.
    /// Having zero tool declarations prevents the model from attempting to call
    /// catalog/encounter lookups it cannot actually reach on this turn.
    /// </summary>
    public static AIAgent CreateSubmissionPresenterAgent(IChatClient baseClient, string instruction) =>
        baseClient.AsBuilder()
            .Build()
            .AsAIAgent(name: "SubmissionAgent", instructions: instruction);

    /// <summary>
    /// Tools: submit_order only.
    /// BC-2: submit_order is NEVER exposed to any other agent in the pipeline.
    /// Equivalent to SubmissionAgent in Python.
    /// </summary>
    public static AIAgent CreateSubmissionAgent(
        IChatClient baseClient,
        string instruction,
        SubmissionPlugin submissionPlugin)
    {
        List<AITool> tools =
        [
            AIFunctionFactory.Create(
                (Func<string, string, int, int, double?, string, string, CancellationToken, Task<string>>)
                submissionPlugin.SubmitOrderAsync),
        ];
        var client = baseClient.AsBuilder()
            .UseFunctionInvocation()
            .Build();
        return client.AsAIAgent(name: "SubmissionAgent", instructions: instruction, tools: tools);
    }
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
