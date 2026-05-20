namespace SmartOrders.Core.Repositories;

/// <summary>
/// Narrow write interface for the TW WIP order queue.
/// Phase 1: MockTwOrderQueueRepository.
/// Phase 1.5+: RealTwOrderQueueRepository once TW platform team confirms integration path.
/// </summary>
public interface ITwOrderQueueRepository
{
    /// <summary>
    /// Queue a single order in the TW Order Engine.
    /// Required fields: OrderDe, OrderName, EncounterId, PatientId.
    /// </summary>
    Task<OrderQueueResult> QueueOrderAsync(OrderQueuePayload payload, CancellationToken ct = default);
}

public sealed record OrderQueuePayload(
    string OrderDe,
    string OrderName,
    int EncounterId,
    int PatientId,
    double? RplDe = null,
    string Priority = "Routine",
    string ToBeDoneDate = "today");

public sealed record OrderQueueResult(string Status, string OrderId, bool Mock = false);
