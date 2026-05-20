using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SmartOrders.Infrastructure.Configuration;
using SmartOrders.Infrastructure.Repositories;

namespace SmartOrders.Api.Controllers;

/// <summary>
/// Catalog maintenance endpoints.
/// POST /api/catalog/index — equivalent to running scripts/index_catalog.py in the Python project.
/// </summary>
[ApiController]
[Route("api/catalog")]
public sealed class CatalogController(
    CatalogIndexer indexer,
    IOptions<SmartOrdersSettings> settings,
    ILogger<CatalogController> logger) : ControllerBase
{
    /// <summary>
    /// Builds (or resumes) the Qdrant semantic search index from OIDExtract.db.
    /// Run once after deployment, or whenever the TW catalog changes.
    /// Equivalent to: python scripts/index_catalog.py
    /// </summary>
    [HttpPost("index")]
    public async Task<IActionResult> IndexAsync(CancellationToken ct)
    {
        logger.LogInformation("catalog_index_start db={Db}", settings.Value.OrdersDbPath);
        await indexer.IndexAsync(
            settings.Value.OrdersDbPath,
            settings.Value.QdrantHost,
            settings.Value.QdrantPort,
            settings.Value.OnnxVocabPath,
            settings.Value.OnnxModelPath,
            ct);
        return Ok(new { status = "indexed" });
    }
}
