namespace SmartOrders.Core.Domain;

/// <summary>
/// Order after catalog lookup. Produced by MappingAgent.
/// Mutable — additional fields are populated as the order moves through validation.
/// </summary>
public class MappedOrder
{
    public OrderIntent Intent { get; init; }
    public string ItemId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Icd10Code { get; set; }
    public double? RplDe { get; set; }
    public OrderPriority Priority { get; set; } = OrderPriority.Routine;
    public string ToBeDoneDate { get; set; } = "today";
    public OrderStatus Status { get; set; } = OrderStatus.Mapped;

    public MappedOrder(OrderIntent intent) => Intent = intent;
}
