namespace SmartOrders.Api;

/// <summary>Request/response types shared between controllers and minimal API endpoints.</summary>
public sealed record ProcessOrdersRequest(string ClinicalText);

public sealed record ConfirmOrdersRequest(string ValidatedOrdersJson, string ProviderConfirmation);
