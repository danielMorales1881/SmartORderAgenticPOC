using Microsoft.AspNetCore.Mvc;
using SmartOrders.Api;
using SmartOrders.Core.Pipeline;
using System.Text.Json;
using System.Threading.Channels;

namespace SmartOrders.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController(IPipelineOrchestrator pipeline) : ControllerBase
{
    private static readonly JsonSerializerOptions s_sseOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

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

    /// <summary>
    /// Streams live pipeline progress events via Server-Sent Events (text/event-stream).
    /// Each event is written as "event: {type}\ndata: {json}\n\n".
    /// The channel buffers up to 200 events; oldest are dropped if the consumer falls behind.
    /// </summary>
    [HttpPost("stream")]
    public async Task StreamAsync([FromBody] ProcessOrdersRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.ClinicalText))
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("ClinicalText is required.", ct);
            return;
        }

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");

        var channel = Channel.CreateBounded<PipelineProgressEvent>(
            new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest });

        // Run pipeline on a background task; SyncProgress writes to channel inline on the
        // pipeline thread to avoid the Progress<T> thread-pool race with TryComplete().
        _ = Task.Run(async () =>
        {
            IProgress<PipelineProgressEvent> progress = new SyncProgress<PipelineProgressEvent>(
                evt => channel.Writer.TryWrite(evt));
            try   { await pipeline.RunAsync(request.ClinicalText, progress, ct); }
            catch (Exception ex) { channel.Writer.TryWrite(new PipelineProgressEvent("error", "Pipeline", ex.Message)); }
            finally { channel.Writer.TryComplete(); }
        }, ct);

        await foreach (var evt in channel.Reader.ReadAllAsync(ct))
        {
            await Response.WriteAsync(
                $"event: {evt.Type}\ndata: {JsonSerializer.Serialize(evt, s_sseOptions)}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }
}

