using System.Text.Json.Serialization;

namespace SmartOrders.Core.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OrderPriority
{
    Routine,
    Urgent,
    Stat
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OrderStatus
{
    PendingMapping,
    Mapped,
    Incomplete,
    Complete,
    Submitted,
    Failed
}
