using Microsoft.Extensions.Logging;
using SmartOrders.Core.Domain;
using SmartOrders.Core.Repositories;

namespace SmartOrders.Infrastructure.Repositories;

/// <summary>
/// In-memory stub for the TW Order Queue.
/// Swap for RealTwOrderQueueRepository once TW integration path is confirmed.
/// </summary>
public sealed class MockTwOrderQueueRepository(ILogger<MockTwOrderQueueRepository> logger)
    : ITwOrderQueueRepository
{
    public Task<OrderQueueResult> QueueOrderAsync(OrderQueuePayload payload, CancellationToken ct = default)
    {
        // Mirrors Python _assert_payload: orderDE, orderName, encounterID, patientID all required
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.OrderDe);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.OrderName);
        if (payload.EncounterId <= 0)
            throw new OrderSubmissionException($"Queue payload is missing required field: encounterID");
        if (payload.PatientId <= 0)
            throw new OrderSubmissionException($"Queue payload is missing required field: patientID");

        var orderId = Guid.NewGuid().ToString();
        logger.LogInformation("mock_queue_order order_id={OrderId} order_name={OrderName}", orderId, payload.OrderName);
        return Task.FromResult(new OrderQueueResult("queued", orderId, Mock: true));
    }
}
