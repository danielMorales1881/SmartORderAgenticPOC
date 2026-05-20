using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using SmartOrders.Core.Repositories;

namespace SmartOrders.Infrastructure.Plugins;

/// <summary>
/// SAFETY CONTRACT: submit_order must ONLY be called after the provider has
/// explicitly confirmed the order. The agent instruction enforces this.
/// </summary>
public sealed class SubmissionPlugin(ITwOrderQueueRepository queue, ILogger<SubmissionPlugin> logger)
{
    [KernelFunction, Description("Submit a provider-confirmed order to the TouchWorks Order Engine. Call ONLY after explicit provider confirmation.")]
    public async Task<string> SubmitOrderAsync(
        [Description("Item ID from the TW catalog (ItemID / HDROrderCode).")] string orderDe,
        [Description("Human-readable order display name.")] string orderName,
        [Description("Active encounter identifier.")] int encounterId,
        [Description("Patient identifier.")] int patientId,
        [Description("RPL scope identifier (optional).")] double? rplDe = null,
        [Description("Clinical priority: 'Routine', 'Urgent', or 'STAT'.")] string priority = "Routine",
        [Description("ISO date string or 'today'.")] string toBeDoneDate = "today",
        CancellationToken cancellationToken = default)
    {
        var payload = new OrderQueuePayload(orderDe, orderName, encounterId, patientId, rplDe, priority, toBeDoneDate);
        var result = await queue.QueueOrderAsync(payload, cancellationToken);
        logger.LogInformation("order_submitted order_name={OrderName} order_id={OrderId}", orderName, result.OrderId);
        return JsonSerializer.Serialize(new { status = result.Status, order_id = result.OrderId, mock = result.Mock });
    }
}
