using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using SmartOrders.Core.Domain;
using SmartOrders.Core.Pipeline;
using SmartOrders.Infrastructure.Agents;
using SmartOrders.Infrastructure.Services;

namespace SmartOrders.Infrastructure.Pipeline;

/// <summary>
/// Sequential pipeline orchestrator — equivalent to ADK SequentialAgent in pipeline.py.
/// Stage order: IntentAgent → MappingAgent → ValidationService (pure C#) → SubmissionAgent.
///
/// State passing: ADK uses output_key to write agent output to session state dict.
/// Here we pass the accumulated state explicitly as context in each agent's user message.
/// </summary>
public sealed partial class SmartOrdersPipeline(
    ChatCompletionAgent intentAgent,
    ChatCompletionAgent mappingAgent,
    ValidationService validationService,
    ChatCompletionAgent submissionAgent,
    ILogger<SmartOrdersPipeline> logger) : IPipelineOrchestrator
{
    public async Task<PipelineState> RunAsync(string clinicalText, IProgress<PipelineProgressEvent>? progress = null, CancellationToken ct = default)
    {
        var state = new PipelineState { ClinicalText = clinicalText };

        // Stage 1: IntentAgent
        progress?.Report(new PipelineProgressEvent("stage_start", "IntentAgent", "Extracting order intents from clinical text..."));
        logger.LogInformation("pipeline_stage stage=IntentAgent");
        var intentRaw = await RunAgentAsync(intentAgent, clinicalText, progress, ct);
        state.OrderIntentsJson = intentRaw;
        state.OrderIntents = ParseIntents(intentRaw, clinicalText);
        logger.LogInformation("intents_extracted count={Count}", state.OrderIntents.Count);
        progress?.Report(new PipelineProgressEvent("stage_done", "IntentAgent",
            $"Found {state.OrderIntents.Count} order intent(s)",
            Data: state.OrderIntents.Count.ToString()));

        if (state.OrderIntents.Count == 0)
        {
            progress?.Report(new PipelineProgressEvent("error", "IntentAgent", "No order intents found in clinical text."));
            logger.LogInformation("pipeline_short_circuit reason=no_intents");
            return state;
        }

        // Stage 2: MappingAgent
        progress?.Report(new PipelineProgressEvent("stage_start", "MappingAgent", "Mapping intents to TouchWorks catalog..."));
        logger.LogInformation("pipeline_stage stage=MappingAgent");
        var mappingInput = $"order_intents:\n{state.OrderIntentsJson}";
        var mappingRaw = await RunAgentAsync(mappingAgent, mappingInput, progress, ct);
        state.MappedOrdersJson = ExtractJsonArray(mappingRaw);
        logger.LogInformation("mapping_complete raw_length={Len}", state.MappedOrdersJson.Length);
        progress?.Report(new PipelineProgressEvent("stage_done", "MappingAgent", "Catalog mapping complete."));

        // Stage 3: ValidationService
        progress?.Report(new PipelineProgressEvent("stage_start", "ValidationService", "Validating required order fields..."));
        logger.LogInformation("pipeline_stage stage=ValidationService");
        state.ValidatedOrdersJson = await validationService.ValidateBatchAsync(state.MappedOrdersJson);
        logger.LogInformation("validation_complete");
        progress?.Report(new PipelineProgressEvent("stage_done", "ValidationService", "Validation complete."));

        // Stage 4: SubmissionAgent — HITL first turn (presentation only, no submit yet)
        progress?.Report(new PipelineProgressEvent("stage_start", "SubmissionAgent", "Preparing order summary for review..."));
        logger.LogInformation("pipeline_stage stage=SubmissionAgent initial_presentation");
        var submissionInput = $"validated_orders:\n{state.ValidatedOrdersJson}";
        var presentationRaw = await RunAgentAsync(submissionAgent, submissionInput, progress, ct);
        state.SubmissionAgentResponse = presentationRaw;
        state.AwaitingConfirmation = true;

        // Emit awaiting_confirmation with validatedOrdersJson so the UI can render order cards
        progress?.Report(new PipelineProgressEvent("awaiting_confirmation", "SubmissionAgent",
            presentationRaw, Data: state.ValidatedOrdersJson));

        return state;
    }

    /// <summary>
    /// Stage 4 continuation — provider has confirmed.
    /// Sends the validated orders + provider confirmation to the SubmissionAgent,
    /// which then calls submit_order for each approved order.
    /// </summary>
    public async Task<PipelineState> ConfirmAndSubmitAsync(
        string validatedOrdersJson, string providerConfirmation, CancellationToken ct = default)
    {
        var state = new PipelineState
        {
            ValidatedOrdersJson = validatedOrdersJson,
        };

        // Build a two-turn history for the SubmissionAgent:
        //   Turn 1 (user):  validated_orders + ask for confirmation
        //   Turn 2 (agent): (re-generated based on the history)
        //   Turn 3 (user):  provider's actual confirmation text
        // We combine turns 1+3 into one message since this is a stateless call.
        var confirmInput = $"""
            validated_orders:
            {validatedOrdersJson}

            Provider confirmation: {providerConfirmation}
            """;

        logger.LogInformation("pipeline_stage stage=SubmissionAgent confirmation provider_input={Input}",
            providerConfirmation);
        var submissionRaw = await RunAgentAsync(submissionAgent, confirmInput, null, ct);
        state.SubmissionResultsJson = ExtractJsonArray(submissionRaw);
        state.SubmissionAgentResponse = submissionRaw;
        state.AwaitingConfirmation = false;

        return state;
    }

    /// <summary>
    /// Runs a single pipeline stage: calls the LLM and handles any tool calls in a loop.
    /// SK 1.76 ChatCompletionAgent.InvokeAsync does NOT auto-execute tool calls for custom
    /// IChatCompletionService — it just yields FunctionCallContent to the caller.
    /// We implement the tool call loop here, equivalent to Python ADK's automatic tool handling.
    /// </summary>
    private async Task<string> RunAgentAsync(ChatCompletionAgent agent, string input, IProgress<PipelineProgressEvent>? progress, CancellationToken ct)
    {
        // Build history: system instruction + user message
        var history = new ChatHistory();
        if (!string.IsNullOrEmpty(agent.Instructions))
            history.AddSystemMessage(agent.Instructions);
        history.AddUserMessage(input);

        var chatService = agent.Kernel.GetRequiredService<IChatCompletionService>();
        // Extract PromptExecutionSettings from agent's default KernelArguments
        var settings = agent.Arguments?.ExecutionSettings?.Values.FirstOrDefault();
        const int maxRounds = 20; // safety limit — prevents infinite tool call loops

        for (var round = 0; round < maxRounds; round++)
        {
            var responses = await chatService.GetChatMessageContentsAsync(history, settings, agent.Kernel, ct);
            if (responses.Count == 0)
            {
                logger.LogWarning("agent_empty_response agent={Agent} round={Round}", agent.Name, round);
                break;
            }

            var response = responses[0];
            history.Add(response); // keep history for multi-turn tool calls

            // Check for function calls in this response
            var functionCalls = response.Items.OfType<FunctionCallContent>().ToList();
            if (functionCalls.Count == 0)
            {
                // No tool calls → final text response
                var text = response.Content ?? "";
                logger.LogInformation("agent_complete agent={Agent} rounds={R} result_length={Len}",
                    agent.Name, round + 1, text.Length);
                return text;
            }

            logger.LogInformation("agent_tool_calls agent={Agent} round={Round} count={Count}",
                agent.Name, round, functionCalls.Count);

            var resultItems = new ChatMessageContentItemCollection();
            var agentName = agent.Name ?? "Agent";
            foreach (var fc in functionCalls)
            {
                var toolLabel = $"{fc.PluginName ?? ""}.{fc.FunctionName}";
                var argsPreview = fc.Arguments is { Count: > 0 }
                    ? string.Join(", ", fc.Arguments.Select(a => $"{a.Key}={a.Value}"))
                    : "";
                progress?.Report(new PipelineProgressEvent("tool_call", agentName,
                    $"{toolLabel}({argsPreview})"));

                FunctionResultContent funcResult;
                try
                {
                    funcResult = await fc.InvokeAsync(agent.Kernel, ct);
                    var resultPreview = funcResult.Result?.ToString() ?? "";
                    if (resultPreview.Length > 120) resultPreview = resultPreview[..120] + "…";
                    progress?.Report(new PipelineProgressEvent("tool_result", agentName,
                        $"{toolLabel} → {resultPreview}"));
                    logger.LogInformation("agent_tool_result agent={Agent} fn={P}_{F} len={L}",
                        agentName, fc.PluginName, fc.FunctionName,
                        funcResult.Result?.ToString()?.Length ?? 0);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "agent_tool_error agent={Agent} fn={P}_{F}",
                        agentName, fc.PluginName, fc.FunctionName);
                    progress?.Report(new PipelineProgressEvent("tool_result", agentName,
                        $"{toolLabel} ⚠ {ex.Message}"));
                    funcResult = new FunctionResultContent(fc, $"{{\"error\": \"{ex.Message}\"}}");
                }

                resultItems.Add(funcResult);
            }

            // Add tool results back to history — BuildContents maps AuthorRole.Tool → Gemini "user" role
            // with functionResponse parts, which is the correct Gemini format
            history.Add(new ChatMessageContent(AuthorRole.Tool, resultItems));
        }

        logger.LogWarning("agent_max_rounds_exceeded agent={Agent}", agent.Name);
        return "";
    }

    /// <summary>
    /// Parse IntentAgent structured output.
    /// Handles two formats the LLM may return:
    ///   - Wrapped object: {"orders": [{"orderable_hint": ...}]}
    ///   - Bare array:     [{"orderable_hint": ...}]
    /// Properties use snake_case (matched via [JsonPropertyName] on OrderIntentResponse).
    /// Equivalent to ADK output_schema=OrderIntentListSchema.
    /// </summary>
    private static IReadOnlyList<OrderIntent> ParseIntents(string? response, string rawText)
    {
        if (string.IsNullOrWhiteSpace(response)) return [];

        var trimmed = response.Trim();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // Try wrapped object first: {"orders": [...]}
        if (trimmed.StartsWith('{'))
        {
            try
            {
                var wrapped = JsonSerializer.Deserialize<OrderIntentListResponse>(trimmed, opts);
                if (wrapped?.Orders?.Count > 0)
                    return MapToOrderIntents(wrapped.Orders, rawText);
            }
            catch { }
        }

        // Try bare array: [{"orderable_hint": ...}]
        if (trimmed.StartsWith('['))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<OrderIntentResponse>>(trimmed, opts);
                if (list?.Count > 0)
                    return MapToOrderIntents(list, rawText);
            }
            catch { }
        }

        return [];
    }

    private static IReadOnlyList<OrderIntent> MapToOrderIntents(
        IEnumerable<OrderIntentResponse> items, string rawText) =>
        items.Where(o => !string.IsNullOrWhiteSpace(o.OrderableHint))
             .Select(o => new OrderIntent(
                 rawText: rawText,
                 orderableHint: o.OrderableHint,
                 priority: Enum.TryParse<OrderPriority>(o.Priority, true, out var p) ? p : OrderPriority.Routine,
                 diagnosisHint: o.DiagnosisHint,
                 confidence: o.Confidence))
             .ToList();

    /// <summary>
    /// Extract first JSON array from agent response text.
    /// Mirrors the regex fallback used in PureValidationAgent and the pipeline state-delta pattern.
    /// LLMs sometimes wrap JSON in prose or markdown fences — this handles that.
    /// </summary>
    private static string ExtractJsonArray(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "[]";

        // Try direct parse first
        var trimmed = text.Trim();
        if (trimmed.StartsWith('['))
        {
            try { JsonSerializer.Deserialize<JsonElement>(trimmed); return trimmed; }
            catch { }
        }

        // Fallback: regex to extract first JSON array — same as Python re.search(r'\[[\s\S]*\]', ...)
        var match = JsonArrayRegex().Match(text);
        return match.Success ? match.Value : "[]";
    }

    [GeneratedRegex(@"\[[\s\S]*\]")]
    private static partial Regex JsonArrayRegex();
}
