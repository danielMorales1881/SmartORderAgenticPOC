using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using SmartOrders.Core.Domain;
using SmartOrders.Core.Pipeline;
using SmartOrders.Infrastructure.Agents;
using SmartOrders.Infrastructure.NoteGateway;
using SmartOrders.Infrastructure.Services;

namespace SmartOrders.Infrastructure.Pipeline;

/// <summary>
/// Sequential pipeline orchestrator — equivalent to ADK SequentialAgent in pipeline.py.
/// Stage order: IntentAgent → MappingAgent → ValidationService (pure C#) → SubmissionAgent.
///
/// AIAgent.RunAsync handles the tool-call loop automatically via UseFunctionInvocation()
/// middleware on each agent's IChatClient — no manual round-trip loop needed.
/// </summary>
public sealed partial class SmartOrdersPipeline(
    AIAgent intentAgent,
    AIAgent mappingAgent,
    ValidationService validationService,
    AIAgent submissionPresenterAgent,
    AIAgent submissionConfirmerAgent,
    ILogger<SmartOrdersPipeline> logger) : IPipelineOrchestrator
{
    public async Task<PipelineState> RunAsync(string clinicalText, IProgress<PipelineProgressEvent>? progress = null, CancellationToken ct = default)
    {
        var state = new PipelineState { ClinicalText = clinicalText };
        var usageTracker = LlmUsageScope.Begin();

        // Stage 1: IntentAgent
        progress?.Report(new PipelineProgressEvent("stage_start", "IntentAgent", "Extracting order intents from clinical text..."));
        logger.LogInformation("pipeline_stage stage=IntentAgent");
        var intentResponse = await intentAgent.RunAsync(clinicalText, cancellationToken: ct);
        state.OrderIntentsJson = intentResponse.Text ?? "";
        state.OrderIntents = ParseIntents(state.OrderIntentsJson, clinicalText);
        logger.LogInformation("intents_extracted count={Count}", state.OrderIntents.Count);
        progress?.Report(new PipelineProgressEvent("stage_done", "IntentAgent",
            $"Found {state.OrderIntents.Count} order intent(s)",
            Data: state.OrderIntents.Count.ToString()));

        if (state.OrderIntents.Count == 0)
        {
            progress?.Report(new PipelineProgressEvent("error", "IntentAgent", "No order intents found in clinical text."));
            logger.LogInformation("pipeline_short_circuit reason=no_intents");
            ApplyMetrics(state, usageTracker, progress);
            return state;
        }

        // Stage 2: MappingAgent — tool-call loop handled automatically by UseFunctionInvocation()
        progress?.Report(new PipelineProgressEvent("stage_start", "MappingAgent", "Mapping intents to TouchWorks catalog..."));
        logger.LogInformation("pipeline_stage stage=MappingAgent");
        var mappingResponse = await mappingAgent.RunAsync(
            $"order_intents:\n{state.OrderIntentsJson}", cancellationToken: ct);
        state.MappedOrdersJson = NormalizeMappedOrders(ExtractJsonArray(mappingResponse.Text ?? ""));
        logger.LogInformation("mapping_complete raw_length={Len}", state.MappedOrdersJson.Length);
        progress?.Report(new PipelineProgressEvent("stage_done", "MappingAgent", "Catalog mapping complete."));

        // Stage 3: ValidationService — pure C#, no LLM
        progress?.Report(new PipelineProgressEvent("stage_start", "ValidationService", "Validating required order fields..."));
        logger.LogInformation("pipeline_stage stage=ValidationService");
        state.ValidatedOrdersJson = await validationService.ValidateBatchAsync(state.MappedOrdersJson);
        logger.LogInformation("validation_complete");
        progress?.Report(new PipelineProgressEvent("stage_done", "ValidationService", "Validation complete."));

        // Stage 4: SubmissionAgent — HITL first turn (presentation only, no submit yet).
        // submissionPresenterAgent has NO tools so the model cannot attempt catalog/encounter
        // lookups — it can only return text summarising the orders for the provider.
        progress?.Report(new PipelineProgressEvent("stage_start", "SubmissionAgent", "Preparing order summary for review..."));
        logger.LogInformation("pipeline_stage stage=SubmissionAgent initial_presentation");
        var presentationResponse = await submissionPresenterAgent.RunAsync(
            $"validated_orders:\n{state.ValidatedOrdersJson}", cancellationToken: ct);
        state.SubmissionAgentResponse = presentationResponse.Text ?? "";
        state.AwaitingConfirmation = true;

        progress?.Report(new PipelineProgressEvent("awaiting_confirmation", "SubmissionAgent",
            state.SubmissionAgentResponse, Data: state.ValidatedOrdersJson));

        ApplyMetrics(state, usageTracker, progress);
        return state;
    }

    /// <summary>
    /// Stage 4 continuation — provider has confirmed.
    /// Creates a fresh session for the SubmissionAgent with full context + confirmation.
    /// The agent calls submit_order for each approved order via UseFunctionInvocation() middleware.
    /// </summary>
    public async Task<PipelineState> ConfirmAndSubmitAsync(
        string validatedOrdersJson, string providerConfirmation, CancellationToken ct = default)
    {
        var state = new PipelineState { ValidatedOrdersJson = validatedOrdersJson };
        var usageTracker = LlmUsageScope.Begin();

        var confirmInput = $"""
            validated_orders:
            {validatedOrdersJson}

            clinical_context:
            encounterId: 1
            patientId: 1001

            Provider confirmation: {providerConfirmation}
            """;

        logger.LogInformation("pipeline_stage stage=SubmissionAgent confirmation provider_input={Input}",
            providerConfirmation);
        var submissionResponse = await submissionConfirmerAgent.RunAsync(confirmInput, cancellationToken: ct);
        state.SubmissionResultsJson = ExtractJsonArray(submissionResponse.Text ?? "");
        state.SubmissionAgentResponse = submissionResponse.Text ?? "";
        state.AwaitingConfirmation = false;

        ApplyMetrics(state, usageTracker, null);
        return state;
    }

    private void ApplyMetrics(
        PipelineState state,
        LlmUsageTracker tracker,
        IProgress<PipelineProgressEvent>? progress)
    {
        state.LlmCallCount      = tracker.TotalCalls;
        state.TotalInputTokens  = tracker.TotalInputTokens;
        state.TotalOutputTokens = tracker.TotalOutputTokens;
        state.EstimatedCostUsd  = tracker.EstimatedCostUsd;

        logger.LogInformation(
            "pipeline_metrics calls={Calls} input={In} output={Out} cost=${Cost:F4}",
            tracker.TotalCalls, tracker.TotalInputTokens, tracker.TotalOutputTokens, tracker.EstimatedCostUsd);

        progress?.Report(new PipelineProgressEvent(
            "pipeline_metrics",
            "Pipeline",
            $"{tracker.TotalCalls} LLM call(s) · {tracker.TotalInputTokens + tracker.TotalOutputTokens:N0} tokens · ~${tracker.EstimatedCostUsd:F4}",
            Data: JsonSerializer.Serialize(new
            {
                llmCallCount      = tracker.TotalCalls,
                totalInputTokens  = tracker.TotalInputTokens,
                totalOutputTokens = tracker.TotalOutputTokens,
                estimatedCostUsd  = tracker.EstimatedCostUsd
            })));
    }


    /// <summary>
    /// Parse IntentAgent structured output.
    /// Handles two formats the LLM may return:
    ///   - Wrapped object: {"orders": [{"orderable_hint": ...}]}
    ///   - Bare array:     [{"orderable_hint": ...}]
    /// </summary>
    private static IReadOnlyList<OrderIntent> ParseIntents(string? response, string rawText)
    {
        if (string.IsNullOrWhiteSpace(response)) return [];

        var trimmed = response.Trim();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

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
    /// Normalizes MappingAgent JSON output to canonical field names expected by ValidationService.
    /// The LLM sometimes returns alternative field names:
    ///   - "order_id"       → "item_id"
    ///   - "diagnoses"      → "icd10_code" (takes first element of the array)
    ///   - "order_defaults" → flattened "priority" and "to_be_done_date" at top level
    /// </summary>
    private static string NormalizeMappedOrders(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return json;

        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var orders = JsonSerializer.Deserialize<List<JsonElement>>(json, opts);
            if (orders is null) return json;

            var normalized = new List<Dictionary<string, object?>>();
            foreach (var order in orders)
            {
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                // Copy all properties first
                foreach (var prop in order.EnumerateObject())
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String  => (object?)prop.Value.GetString(),
                        JsonValueKind.Number  => prop.Value.TryGetDouble(out var d) ? d : (object?)null,
                        JsonValueKind.True    => true,
                        JsonValueKind.False   => false,
                        JsonValueKind.Null    => null,
                        _                    => prop.Value.GetRawText(), // arrays/objects as raw
                    };

                // Rename order_id → item_id (only if item_id not already present)
                if (!dict.ContainsKey("item_id") && dict.TryGetValue("order_id", out var oid))
                {
                    dict["item_id"] = oid;
                    dict.Remove("order_id");
                }

                // Normalize diagnoses array → icd10_code string
                if (!dict.ContainsKey("icd10_code") && dict.TryGetValue("diagnoses", out var diagnoses)
                    && diagnoses is string diagJson)
                {
                    try
                    {
                        var codes = JsonSerializer.Deserialize<List<string>>(diagJson);
                        if (codes?.Count > 0) dict["icd10_code"] = codes[0];
                    }
                    catch { /* leave as-is */ }
                    dict.Remove("diagnoses");
                }

                // Flatten order_defaults → top-level priority and to_be_done_date
                if (dict.TryGetValue("order_defaults", out var defaults) && defaults is string defaultsJson)
                {
                    try
                    {
                        var defaultsEl = JsonSerializer.Deserialize<JsonElement>(defaultsJson);
                        if (!dict.ContainsKey("priority") && defaultsEl.TryGetProperty("priority", out var pri))
                            dict["priority"] = pri.GetString();
                        if (!dict.ContainsKey("to_be_done_date") && defaultsEl.TryGetProperty("to_be_done_date", out var tbd))
                            dict["to_be_done_date"] = tbd.GetString();
                    }
                    catch { /* leave as-is */ }
                    dict.Remove("order_defaults");
                }

                normalized.Add(dict);
            }

            // Deduplicate by item_id — keep first occurrence.
            // Prevents duplicate orders when an intent is both explicitly listed
            // and implied via "recheck" / "repeat" (e.g. "Order BMP" + "recheck renal function").
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deduped = normalized.Where(o =>
            {
                var id = o.TryGetValue("item_id", out var v) ? v?.ToString() : null;
                if (string.IsNullOrWhiteSpace(id)) return true; // nulls always kept
                return seen.Add(id);
            }).ToList();

            return JsonSerializer.Serialize(deduped);
        }
        catch
        {
            return json; // Return original if normalization fails
        }
    }

    /// <summary>
    /// Extract first JSON array from agent response text.
    /// LLMs sometimes wrap JSON in prose or markdown fences — this handles that.
    /// </summary>
    private static string ExtractJsonArray(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "[]";

        var trimmed = text.Trim();
        if (trimmed.StartsWith('['))
        {
            try { JsonSerializer.Deserialize<JsonElement>(trimmed); return trimmed; }
            catch { }
        }

        var match = JsonArrayRegex().Match(text);
        return match.Success ? match.Value : "[]";
    }

    [GeneratedRegex(@"\[[\s\S]*\]")]
    private static partial Regex JsonArrayRegex();
}
