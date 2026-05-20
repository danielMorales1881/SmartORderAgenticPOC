namespace SmartOrders.Core.Domain;

/// <summary>
/// Immutable value object: a clinical order intent extracted from text.
/// Produced by IntentAgent.
/// </summary>
public sealed record OrderIntent
{
    public string RawText { get; init; }
    public string OrderableHint { get; init; }
    public OrderPriority Priority { get; init; } = OrderPriority.Routine;
    public string? DiagnosisHint { get; init; }
    public double Confidence { get; init; } = 1.0;

    public OrderIntent(string rawText, string orderableHint, OrderPriority priority = OrderPriority.Routine,
        string? diagnosisHint = null, double confidence = 1.0)
    {
        if (confidence is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be in [0.0, 1.0].");
        if (string.IsNullOrWhiteSpace(orderableHint))
            throw new ArgumentException("orderableHint must not be blank.", nameof(orderableHint));

        RawText = rawText;
        OrderableHint = orderableHint;
        Priority = priority;
        DiagnosisHint = diagnosisHint;
        Confidence = confidence;
    }
}
