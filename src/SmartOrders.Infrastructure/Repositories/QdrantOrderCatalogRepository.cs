using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using SmartOrders.Core.Domain;
using SmartOrders.Core.Repositories;

namespace SmartOrders.Infrastructure.Repositories;

/// <summary>
/// Semantic vector search over the TW order catalog using Qdrant + all-MiniLM-L6-v2 embeddings.
///
/// Direct port of QdrantOrderCatalogRepository in order_catalog_qdrant.py:
///   - Collection: "tw_orders", 384-dim cosine
///   - Embedding: all-MiniLM-L6-v2 via SentenceEmbedder (ONNX Runtime)
///   - Index built once via POST /api/catalog/index  (≡ scripts/index_catalog.py)
///
/// Architecture difference vs Python:
///   Python uses QdrantClient(path=...) — embedded local storage.
///   .NET connects to a running Qdrant server via gRPC.
///   Start: docker run -p 6333:6333 -p 6334:6334 qdrant/qdrant
/// </summary>
public sealed class QdrantOrderCatalogRepository : IOrderCatalogRepository, IDisposable
{
    private const string CollectionName = "tw_orders";

    private readonly QdrantClient _client;
    private readonly SentenceEmbedder _embedder;
    private readonly ILogger<QdrantOrderCatalogRepository> _logger;

    public QdrantOrderCatalogRepository(
        string qdrantHost, int qdrantPort,
        string onnxVocabPath, string onnxModelPath,
        ILogger<QdrantOrderCatalogRepository> logger)
    {
        _client = new QdrantClient(qdrantHost, qdrantPort);
        _embedder = new SentenceEmbedder(onnxVocabPath, onnxModelPath);
        _logger = logger;
    }

    /// <summary>
    /// Semantic vector search — returns the <paramref name="limit"/> most similar orders.
    /// Mirrors QdrantOrderCatalogRepository.search() in order_catalog_qdrant.py.
    /// </summary>
    public async Task<IReadOnlyList<CatalogItem>> SearchAsync(
        string text, double? rplDe = null, int limit = 10, CancellationToken ct = default)
    {
        _logger.LogDebug("qdrant_search text={Text} limit={Limit}", text, limit);

        var vector = _embedder.Embed(text);
        var clampedLimit = (ulong)Math.Min(limit, 20);

        var response = await _client.SearchAsync(
            collectionName: CollectionName,
            vector: new ReadOnlyMemory<float>(vector),
            limit: clampedLimit,
            payloadSelector: new WithPayloadSelector { Enable = true },
            cancellationToken: ct);

        var results = response.Select(pt =>
        {
            var p = pt.Payload;
            return new CatalogItem(
                ItemId:        p.TryGetValue("item_id",       out var id)  ? id.StringValue  : string.Empty,
                DisplayName:   p.TryGetValue("display_name",  out var dn)  ? dn.StringValue  : string.Empty,
                OrderCategory: p.TryGetValue("order_category",out var cat) ? cat.StringValue : string.Empty,
                Score:         Math.Round(pt.Score, 4));
        }).ToList();

        _logger.LogDebug("qdrant_search completed text={Text} hits={Hits}", text, results.Count);
        return results;
    }

    public void Dispose()
    {
        _embedder.Dispose();
        _client.Dispose();
    }
}
