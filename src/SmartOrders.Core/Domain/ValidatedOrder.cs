namespace SmartOrders.Core.Domain;

/// <summary>
/// Order after field validation. Produced by ValidationAgent.
/// Carries completeness state so SubmissionAgent can decide which orders are ready.
/// </summary>
public class ValidatedOrder
{
    public MappedOrder Mapped { get; init; }
    public List<string> MissingFields { get; init; } = [];

    public bool IsComplete => MissingFields.Count == 0;
    public OrderStatus Status => IsComplete ? OrderStatus.Complete : OrderStatus.Incomplete;

    public ValidatedOrder(MappedOrder mapped) => Mapped = mapped;
}
