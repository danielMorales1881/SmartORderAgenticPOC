using Microsoft.Extensions.Logging;
using SmartOrders.Core.Domain;
using SmartOrders.Core.Repositories;
using System.Net.Http.Json;

namespace SmartOrders.Infrastructure.Repositories;

/// <summary>
/// HTTP client for the real TW Order Queue endpoint.
/// Enable via SmartOrdersSettings.UseMockQueue = false once Q1 is resolved.
/// </summary>
public sealed class RealTwOrderQueueRepository(HttpClient httpClient, ILogger<RealTwOrderQueueRepository> logger)
    : ITwOrderQueueRepository
{
    public async Task<OrderQueueResult> QueueOrderAsync(OrderQueuePayload payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.OrderDe);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload.OrderName);
        if (payload.EncounterId <= 0)
            throw new OrderSubmissionException($"Queue payload is missing required field: encounterID");
        if (payload.PatientId <= 0)
            throw new OrderSubmissionException($"Queue payload is missing required field: patientID");

        var body = new
        {
            orderDE = payload.OrderDe,
            orderName = payload.OrderName,
            encounterID = payload.EncounterId,
            patientID = payload.PatientId,
            rplDE = payload.RplDe,
            priority = payload.Priority,
            toBeDoneDate = payload.ToBeDoneDate,
        };

        try
        {
            var response = await httpClient.PostAsJsonAsync("/api/orders/queue", body, ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<OrderQueueResult>(ct)
                ?? throw new OrderSubmissionException("TW queue returned an empty response.");

            logger.LogInformation("real_queue_order order_name={OrderName} status={Status}", payload.OrderName, result.Status);
            return result;
        }
        catch (HttpRequestException ex)
        {
            throw new OrderSubmissionException($"TW Order Engine unreachable: {ex.Message}", ex);
        }
    }
}
