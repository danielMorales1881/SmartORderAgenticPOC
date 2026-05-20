namespace SmartOrders.Core.Repositories;

/// <summary>
/// Narrow read interface for the TW order catalog.
/// Phase 1: backed by OIDExtract.db (SQLite).
/// Phase 2: swap for Azure AI Search — no callers change.
/// </summary>
public interface IOrderCatalogRepository
{
    /// <summary>
    /// Search the catalog for orders matching <paramref name="text"/>.
    /// Returns items with item_id, display_name, and order_category.
    /// </summary>
    Task<IReadOnlyList<CatalogItem>> SearchAsync(string text, double? rplDe = null, int limit = 10,
        CancellationToken ct = default);
}

/// <summary>
/// Matches the Python dict: {"item_id": ..., "display_name": ..., "order_category": ..., "score": ...}
/// Score is null for SQLite LIKE results; populated by Qdrant semantic search.
/// </summary>
public sealed record CatalogItem(string ItemId, string DisplayName, string OrderCategory, double? Score = null, double? RplDe = null);
