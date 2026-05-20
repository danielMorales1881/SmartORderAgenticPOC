using Microsoft.AspNetCore.Mvc;
using SmartOrders.Api;
using SmartOrders.Core.Pipeline;

namespace SmartOrders.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController(IPipelineOrchestrator pipeline) : ControllerBase
{
    /// <summary>
    /// Step 1 — Process clinical text through stages 1-3 (Intent → Mapping → Validation)
    /// then ask the SubmissionAgent to present orders for provider review.
    /// Returns AwaitingConfirmation=true when the agent is ready for provider input.
    /// Follow up with POST /api/orders/submit to confirm and submit.
    /// </summary>
    [HttpPost("process")]
    [ProducesResponseType<PipelineState>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ProcessAsync([FromBody] ProcessOrdersRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ClinicalText))
            return BadRequest("ClinicalText is required.");

        var result = await pipeline.RunAsync(request.ClinicalText, null, ct);
        return Ok(result);
    }

    /// <summary>
    /// Step 2 — Provider confirmation. Send the ValidatedOrdersJson from step 1 and
    /// the provider's response (e.g. "submit all", "submit CBC only", "cancel").
    /// The SubmissionAgent will call submit_order for each confirmed order.
    /// </summary>
    [HttpPost("submit")]
    [ProducesResponseType<PipelineState>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SubmitAsync([FromBody] ConfirmOrdersRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ValidatedOrdersJson))
            return BadRequest("ValidatedOrdersJson is required. Copy it from the /process response.");
        if (string.IsNullOrWhiteSpace(request.ProviderConfirmation))
            return BadRequest("ProviderConfirmation is required (e.g. 'submit all').");

        var result = await pipeline.ConfirmAndSubmitAsync(
            request.ValidatedOrdersJson, request.ProviderConfirmation, ct);
        return Ok(result);
    }
}

