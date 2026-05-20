namespace SmartOrders.Core.Pipeline;

public interface IPipelineOrchestrator
{
    /// <summary>
    /// Runs stages 1–3 then presents orders for confirmation.
    /// Returns AwaitingConfirmation=true; follow up with ConfirmAndSubmitAsync.
    /// Optionally supply <paramref name="progress"/> to receive live stage/tool events.
    /// </summary>
    Task<PipelineState> RunAsync(string clinicalText, IProgress<PipelineProgressEvent>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Stage 4 continuation — sends provider confirmation to the SubmissionAgent.
    /// </summary>
    Task<PipelineState> ConfirmAndSubmitAsync(string validatedOrdersJson, string providerConfirmation, CancellationToken ct = default);
}
