using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace SmartOrders.Infrastructure.Repositories;

/// <summary>
/// One-time catalog indexer — reads OIDExtract.db and builds the Qdrant vector index.
///
/// Direct port of scripts/index_catalog.py:
///   - Table: orders, filters IsInActiveFlag != 'Y'
///   - Text indexed: "Order Name | Display Name | Order Category" (same composite as Python)
///   - Embedding: all-MiniLM-L6-v2 via SentenceEmbedder (ONNX Runtime), 384-dim, cosine
///   - Collection: "tw_orders"
///   - Batch size: 256
///   - Supports resume: skips if already fully indexed
///
/// Usage: POST /api/catalog/index
/// </summary>
public sealed class CatalogIndexer(ILogger<CatalogIndexer> logger) : IDisposable
{
    private const string CollectionName = "tw_orders";
    private const int BatchSize = 256;

    private SentenceEmbedder? _embedder;

    /// <summary>
    /// Reads all active orders from OIDExtract.db, embeds them, upserts into Qdrant.
    /// Mirrors scripts/index_catalog.py main() exactly.
    /// </summary>
    public async Task IndexAsync(
        string dbPath, string qdrantHost, int qdrantPort,
        string onnxVocabPath, string onnxModelPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(dbPath))
            throw new FileNotFoundException($"OIDExtract.db not found at '{dbPath}'.");

        logger.LogInformation("Loading orders from {DbPath}...", dbPath);
        var orders = LoadOrders(dbPath);
        logger.LogInformation("{Count} active orders loaded", orders.Count);

        // Composite text — same as Python: "order_name | display_name | order_category"
        var texts = orders.Select(o =>
            $"{o.OrderName} | {o.DisplayName} | {o.OrderCategory}").ToList();

        using var client = new QdrantClient(qdrantHost, qdrantPort);

        // Resume support — mirrors Python logic
        var collections = await client.ListCollectionsAsync(ct);
        var exists = collections.Any(c => c == CollectionName);
        ulong alreadyIndexed = 0;

        if (exists)
        {
            var info = await client.GetCollectionInfoAsync(CollectionName, ct);
            alreadyIndexed = info.PointsCount;

            if (alreadyIndexed == (ulong)orders.Count)
            {
                logger.LogInformation("Already fully indexed ({Count} orders). Nothing to do.", orders.Count);
                return;
            }
            if (alreadyIndexed == 0)
            {
                await client.DeleteCollectionAsync(CollectionName, cancellationToken: ct);
                exists = false;
            }
            else
                logger.LogInformation("Resuming from point {Already} ({Total} total)", alreadyIndexed, orders.Count);
        }

        if (!exists)
        {
            await client.CreateCollectionAsync(
                CollectionName,
                new VectorParams { Size = SentenceEmbedder.EmbeddingDim, Distance = Distance.Cosine },
                cancellationToken: ct);
            logger.LogInformation("Collection '{Collection}' created (dim={Dim}, cosine)", CollectionName, SentenceEmbedder.EmbeddingDim);
        }

        // Lazy-load embedder once we know we actually need to index
        _embedder ??= new SentenceEmbedder(onnxVocabPath, onnxModelPath);
        logger.LogInformation("Embedding model loaded");

        var startFrom = (int)alreadyIndexed;
        var total = orders.Count;
        logger.LogInformation("Embedding {Count} orders in batches of {Batch}...", total - startFrom, BatchSize);

        for (var i = startFrom; i < total; i += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batchOrders = orders.GetRange(i, Math.Min(BatchSize, total - i));
            var batchTexts  = texts.GetRange(i, batchOrders.Count);

            var points = new List<PointStruct>(batchOrders.Count);
            for (var j = 0; j < batchOrders.Count; j++)
            {
                var o = batchOrders[j];
                var vec = _embedder.Embed(batchTexts[j]);
                points.Add(new PointStruct
                {
                    Id = (ulong)(i + j),
                    Vectors = vec,
                    Payload =
                    {
                        ["item_id"]       = o.ItemId,
                        ["display_name"]  = o.DisplayName,
                        ["order_name"]    = o.OrderName,
                        ["order_category"]= o.OrderCategory,
                    }
                });
            }

            await client.UpsertAsync(CollectionName, points, cancellationToken: ct);

            var batchNum = (i - startFrom) / BatchSize + 1;
            logger.LogInformation("Batch {Batch} done ({From}–{To}/{Total})", batchNum, i, Math.Min(i + BatchSize, total), total);
        }

        var finalCount = (await client.GetCollectionInfoAsync(CollectionName, ct)).PointsCount;
        logger.LogInformation("Indexing complete. {Count} orders in Qdrant.", finalCount);
    }

    private static List<OrderRow> LoadOrders(string dbPath)
    {
        var rows = new List<OrderRow>();
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Same WHERE clause as Python index_catalog.py
        cmd.CommandText = """
            SELECT TRIM("HDROrder Code")  AS item_id,
                   "Order Name"           AS order_name,
                   "Display Name"         AS display_name,
                   "Order Category"       AS order_category
            FROM   orders
            WHERE  ("IsInActiveFlag" IS NULL OR UPPER(TRIM("IsInActiveFlag")) != 'Y')
              AND  "HDROrder Code" IS NOT NULL
              AND  TRIM("HDROrder Code") != ''
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new OrderRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
        }
        return rows;
    }

    private sealed record OrderRow(string ItemId, string OrderName, string DisplayName, string OrderCategory);

    public void Dispose() => _embedder?.Dispose();
}
