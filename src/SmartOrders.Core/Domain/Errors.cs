namespace SmartOrders.Core.Domain;

/// <summary>Base error for all Smart Orders domain errors.</summary>
public class SmartOrdersException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Raised when the order catalog cannot be queried.</summary>
public sealed class CatalogSearchException(string message, Exception? inner = null)
    : SmartOrdersException(message, inner);

/// <summary>Raised when an order intent cannot be mapped to a catalog entry.</summary>
public sealed class OrderMappingException(string message, Exception? inner = null)
    : SmartOrdersException(message, inner);

/// <summary>Raised when order submission to the TW Order Engine fails.</summary>
public sealed class OrderSubmissionException(string message, Exception? inner = null)
    : SmartOrdersException(message, inner);
