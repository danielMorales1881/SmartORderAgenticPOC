using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartOrders.Core.Repositories;

namespace SmartOrders.Infrastructure.Plugins;

/// <summary>
/// Bridges the Agent Framework tool call to IOrderCatalogRepository.
/// Returns {item_id, display_name, order_category} matching Python search_orders output.
/// </summary>
public sealed class SearchPlugin(IOrderCatalogRepository catalog, ILogger<SearchPlugin> logger)
{
    [Description("Search the TouchWorks order catalog for matching orderable items.")]
    public async Task<string> SearchOrdersAsync(
        [Description("Clinical search term e.g. 'CBC', 'chest X-ray'.")] string text,
        [Description("Optional RPL scope to filter results. Omit to search all RPLs.")] double? rplDe = null,
        [Description("Maximum number of results (default 5, max 20).")] int limit = 5,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("search_orders text={Text} rpl_de={RplDe} limit={Limit}", text, rplDe, limit);
        var results = await catalog.SearchAsync(text, rplDe, limit, cancellationToken);
        return JsonSerializer.Serialize(results.Select(r => new
        {
            item_id = r.ItemId,
            display_name = r.DisplayName,
            order_category = r.OrderCategory,
            score = r.Score,
        }));
    }
}
