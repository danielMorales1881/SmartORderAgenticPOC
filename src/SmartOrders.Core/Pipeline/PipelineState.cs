using SmartOrders.Core.Domain;

namespace SmartOrders.Core.Pipeline;

/// <summary>
/// State bag that flows through each agent stage.
/// Equivalent to ADK's session state dict — each agent reads its input key
/// and writes its output key (order_intents, mapped_orders, validated_orders, submission_results).
/// </summary>
public class PipelineState
{
    public string ClinicalText { get; init; } = string.Empty;

    // Raw JSON strings — equivalent to ADK state dict entries written via output_key
    public string OrderIntentsJson { get; set; } = string.Empty;
    public string MappedOrdersJson { get; set; } = string.Empty;
    public string ValidatedOrdersJson { get; set; } = string.Empty;
    public string SubmissionResultsJson { get; set; } = string.Empty;

    // Full SubmissionAgent text response (includes HITL provider summary)
    public string SubmissionAgentResponse { get; set; } = string.Empty;

    /// <summary>
    /// True when the SubmissionAgent has presented orders and is waiting for the provider
    /// to confirm before submitting. The caller should follow up with
    /// <see cref="IPipelineOrchestrator.ConfirmAndSubmitAsync"/> passing
    /// <see cref="ValidatedOrdersJson"/> and the provider's response (e.g. "submit all").
    /// </summary>
    public bool AwaitingConfirmation { get; set; }

    // Parsed domain objects for convenience (populated from JSON)
    public IReadOnlyList<OrderIntent> OrderIntents { get; set; } = [];
}
